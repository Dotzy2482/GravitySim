# Collision → Fluid Debris & Hot-Spot Plan

**Status:** Design approved. No code written yet. Decisions locked 2026-06-13 (see §10).
**Target project:** GravitySim (OpenTK 4.9.4, .NET 8, OpenGL 3.3 Core today).
**Goal:** When two bodies collide hard enough, they tear into a fluid-like debris
cloud with realistic splash, and the impact region heats up and glows
(white-hot → orange → red, emissive). Real-time (≥60 FPS), GPU-friendly, CPU-light.

---

## 0. Phase-0 findings (current architecture)

| Area | Reality today |
|---|---|
| **GL context** | OpenGL **3.3 Core**, `Profile = Core`, MSAA 4x, VSync on (`SimulationWindow` ctor). OpenTK **4.9.4**. GLSL `#version 330 core`. |
| **Collision** | `PhysicsEngine.HandleCollisions()` — O(n²), trigger `dist < 0.85·(rA+rB)`, merges into heavier body (mass+momentum conserved), `Bodies.Remove(removed)`, fires `BodiesMerged?.Invoke(...)`, `goto restart`. |
| **Physics step** | `PhysicsEngine.Update(frameTime)` — fixed `Dt = 1/240` accumulator (capped 0.1s). Each substep: `Step(Dt)` then `if (EnableCollisions) HandleCollisions()`. Gravity all-pairs O(n²) + Plummer softening (`G=1`, `ε=0.05`). VelocityVerlet default. |
| **Bodies** | `public class Body` in `List<Body> Bodies` owned by `PhysicsEngine`. Fields: Position, Velocity, Acceleration, Mass, Radius, Color, Emissive, Density, Anchored, IsHeld, ShowTrail, Trail. `UpdateRadius()`, `RadiusFromMass()`, `KineticEnergy`. |
| **Main loop** | `OnUpdateFrame`: HandleInput → `_physics.Update(dt)` → selection check → camera follow → RecordTrails. `OnRenderFrame`: ImGui.Update → Clear → DrawBodies → DrawTrailsAndArrows → DrawGrid → DrawUI → ImGui.Render → SwapBuffers. |
| **Rendering** | Shared `SphereMesh`, **one `DrawElements` per body** (not instanced). `GridMesh` = `GL_LINES`, Y updated per-frame via `BufferSubData(PotentialAt)`. `LineRenderer` = streaming VBO (`StreamDraw`) for trails/arrows. `Shader` class: `Use / SetMatrix4 / SetVector3 / SetVector4 / SetFloat`. |
| **Post / blend** | **No FBO, no bloom, no post-processing.** Alpha blend only on trails/grid. Depth test on, depth write toggled. No point sprites, no geometry/compute shaders. |
| **GL 4.3 bump** | **Viable.** No code assumes 3.3-only features; any modern GPU supports 4.3 + SSBOs + compute. ImGui's 3.3-core backend runs unchanged under a 4.3 core context, and existing GLSL 330 shaders still compile. |

---

## 1. Chosen approach

**Approach 3 — Hybrid (shatter → fluid → re-coalesce), delivered in three independently-shippable stages where v1 *is* the Approach-1 burst.**

The end-state architecture is the hybrid because it is the only one of the three
that satisfies **both** hard goals at once:

- *"Looks like a real liquid that splashes and forms filaments"* → needs SPH/PBF
  cohesion (Approach 2), not ballistic sparks.
- *"Stays real-time and never hammers the CPU/GPU over a long session"* → needs a
  way to **retire** particles, which only the hybrid's re-coalescence provides.

Crucially, the hybrid is reached by *layering*, so we still "pick one" architecture
but ship value at every step:

- **v1** delivers the Approach-1 visual (ballistic burst + heat + glow) **and**
  stands up all the durable infrastructure (pool, SSBO layout, spawn pipeline,
  external-gravity field, heat ramp, HDR+bloom). Ships first, looks good.
- **v2** adds the Approach-2 fluid solver (PBF density/pressure/viscosity + surface
  tension) on top of the same pool and grid.
- **v3** closes the hybrid loop (heat diffusion + re-coalescence + optional
  screen-space metaballs).

### Why the others are rejected as the *final* design

- **Approach 1 alone (ballistic burst):** No cohesion → debris reads as sparks/dust,
  not a torn-apart liquid. Fails the "fluid-like / splash / filaments" requirement.
  *Kept as v1* precisely because it's cheap and de-risks everything else.
- **Approach 2 alone (pure SPH forever):** Cost is **unbounded over time**. Every
  collision permanently adds N particles to the per-frame solver; nothing ever
  leaves. After a few collisions you sit pinned at the pool cap and the fluid
  solver runs at full cost forever — exactly the "hammers the CPU" failure mode.
  Without re-coalescence there is no graceful decay back to cheap point masses.

The hybrid's re-coalescence is therefore not a nicety — it is the mechanism that
keeps the long-run cost bounded, which is why it's the chosen architecture.

### Solver choice inside the fluid stage: **Position-Based Fluids (PBF)** over classic SPH

- PBF is **unconditionally stable at large/variable timesteps** (it solves a density
  constraint via Jacobi iterations rather than stiff pressure forces). Our `dt` is
  user-tunable and `TimeScale` ranges 0.05–50×, so stiff-pressure SPH would explode.
- PBF maps cleanly to a fixed iteration budget → trivial to make adaptive (drop
  iterations under load).
- XSPH viscosity + a surface-tension/cohesion term give the splash + filament look.

---

## 2. Data structures

### 2.1 Particle record (GPU `std430` SSBO; mirrored 1:1 as a C# struct)

Packed into `vec4`s so CPU upload and GPU `std430` agree byte-for-byte (no padding
surprises). 64 bytes/particle.

```glsl
struct Particle {
    vec4 pos;      // xyz = position,           w = mass (per-particle chunk)
    vec4 predPos;  // xyz = PBF predicted pos,   w = temperature (Kelvin-ish)
    vec4 vel;      // xyz = velocity,            w = density (SPH ρ)
    vec4 aux;      // x = lambda/pressure, y = age(s), z = flags (bitfield), w = pad
};
```

```csharp
[StructLayout(LayoutKind.Sequential)]
struct Particle {           // 64 bytes — identical layout to the GLSL struct
    public Vector4 Pos;     // w = mass
    public Vector4 PredPos; // w = temperature
    public Vector4 Vel;     // w = density
    public Vector4 Aux;     // x = lambda, y = age, z = flags(bits), w = pad
}
```

`flags` bits: `ACTIVE`, `BALLISTIC_LOD` (skip density/pressure), `SETTLED`
(candidate for coalescence), `HOT_SEED` (got initial impact heat).

**Why `mass` per particle:** partial disruption + re-coalescence must conserve mass
and momentum, so each particle carries its mass chunk; momentum = Σ mᵢvᵢ.

### 2.2 Uniform spatial-hash grid (rebuilt each step → O(n) neighbor search)

- **Bounded, cloud-following grid.** Each step compute the active-particle AABB,
  pad it, and lay a uniform grid with **cell size = 2h** (h = SPH smoothing radius).
  A bounded grid over just the debris AABB avoids hash collisions entirely and keeps
  cells dense; clamp grid dims to a max (e.g. 256³ logical, but realistically tiny
  since debris is localized).
- **Build (GPU compute, two viable variants):**
  1. *Counting sort* (preferred, exact): clear cell counts → atomic count per cell →
     prefix-sum to cell offsets → scatter particle indices into a sorted array.
     Gives per-cell `[start,end)` with no fixed capacity. O(n).
  2. *Fixed-capacity buckets* (simpler first GPU cut): `cellBucket[cell][K]` with an
     atomic append counter; overflow beyond K is dropped. K≈48. Slight artifacts at
     extreme density; acceptable for VFX. **Recommend variant 2 for the first GPU
     pass, upgrade to variant 1 if banding appears.**
- Neighbor query = the 27 (or 8 with 2h cells) adjacent cells only.

Buffers: `gridCellStart[]`, `gridCellEnd[]` (or `bucket[]`+`bucketCount[]`),
`sortedIndex[]`. A few MB total.

### 2.3 Pool sizing (pre-allocated, hard cap)

- One `Particle[]` / one persistent SSBO, **allocated once** at max cap. Never grows.
- A free-list / active-count compaction so dead particles are reused.
- **Default caps (tunable, see decisions):**
  - GPU backend: `MaxParticles = 150_000` (≈ 9.6 MB for the main SSBO; ~16 MB with
    grid/sort buffers). Headroom to 250k if the GPU is strong.
  - CPU fallback: `MaxParticles = 8_000` (PBF) / `30_000` (ballistic-only). PBF on CPU
    is heavy, so the fallback caps low and leans on LOD.
- **Spawn budget per disruption:** scales with available headroom and the adaptive
  frame budget. Default target ~20k–40k for a full disruption of two mid bodies;
  each particle's mass = `bodyMass / particlesForThatBody`.

---

## 3. Physics: outcome by specific impact energy

At the existing collision trigger in `HandleCollisions()`, compute:

- relative velocity `v_rel = |a.Velocity − b.Velocity|`
- reduced mass `μ = mA·mB / (mA+mB)`
- impact KE `E = ½·μ·v_rel²`
- **specific impact energy** `Q = E / (mA + mB)` (energy per unit total mass)

Branch on two tunable thresholds (sim units, exposed in UI — these are artistic
knobs, not physical constants, since `G=1`):

| Regime | Condition | Outcome |
|---|---|---|
| **Low** | `Q < Q_merge` | **Merge** — existing behaviour, untouched. |
| **Medium** | `Q_merge ≤ Q < Q_disrupt` | **Partial disruption** — eject a crater spray. Fraction of mass `f(Q)` becomes particles at the contact patch; the rest stays as a (smaller) merged survivor. Mass + momentum split between survivor and ejecta. |
| **High** | `Q ≥ Q_disrupt` | **Full disruption** — both bodies become particles. |

**Spawn geometry (conserving mass + momentum):**
- Seed particles inside each disrupted body's sphere volume (blue-noise / jittered
  fill) so the cloud starts non-interpenetrating.
- Each particle's base velocity = its body's bulk velocity, **plus** an ejecta field:
  radial-outward from the contact point, strongest at the contact patch, scaled so
  total Σmᵢvᵢ ≈ pre-impact momentum (residual corrected by a uniform velocity offset).
- Particles near the contact patch are tagged `HOT_SEED` and get the impact heat.

**One-way gravity coupling (perf):** particles feel gravity from the few massive
bodies as an **external field** (O(P·B), B = body count), and may accrete onto
bodies, but **do not** pull on bodies. This keeps the N-body core at its current cost.
(Trade-off acknowledged in Risks.)

---

## 4. Heat model

Per-particle temperature `T` (stored in `predPos.w`).

- **Seeding:** on disruption, `T_i ∝ local share of impact KE`. Contact-patch
  particles start white-hot; bulk interior starts warm/cool.
- **Diffusion (v3):** heat-equation smoothing over the *same* neighbor grid used by
  SPH: `T_i += k_diff · Σ_j (T_j − T_i)·W(r_ij) · dt`. Reuses neighbor lists for free.
- **Cooling:** default **exponential** `dT = −k_cool·(T − T_amb)·dt` (stable, cheap).
  Optional Stefan-Boltzmann flavour `dT = −σ·(T⁴ − T_amb⁴)·dt` for a faster initial
  white→orange falloff (guarded for stability).
- **Color (blackbody-ish ramp):** map `T → RGB` via a precomputed 1-D ramp texture
  (or analytic approximation) running white → yellow → orange → deep red → dark.
  Emissive **HDR intensity** also scales with `T` so hot spots blow out and bloom.

---

## 5. Rendering plan + GL-version decision

### 5.1 GL version: **bump to OpenGL 4.3 Core**

- Change `APIVersion = new Version(4, 3)` in `SimulationWindow`'s `NativeWindowSettings`.
- Enables **compute shaders + SSBOs** (the GPU SPH/PBF path) and direct
  vertex-pulling from the SSBO for rendering (`gl_VertexID`, no instance VBO copy).
- Existing GLSL 330 shaders and the ImGui 3.3-core backend run unchanged under 4.3 core.
- **Capability probe + fallback:** after context creation read
  `GL.GetInteger(GetPName.MajorVersion/MinorVersion)`. If <4.3 (or compute
  unavailable), select the **CPU backend** + a simple instanced/point-sprite VBO
  renderer (no compute). Backend chosen behind an `IParticleBackend` interface.

### 5.2 Particle rendering

- **Additive point-sprites / billboards.** Draw `GL_POINTS` with
  `GL.Enable(EnableCap.ProgramPointSize)`; vertex shader reads the SSBO by
  `gl_VertexID`, sizes the sprite by `radius / distance`, and passes `T`→color.
  Fragment shader makes a soft round falloff via `gl_PointCoord`.
- **Additive blend** for the hot core: `GL.BlendFunc(One, One)` (or `SrcAlpha, One`),
  depth-test on, depth-write off, drawn after opaque bodies.

### 5.3 HDR + bloom (needed for "glow")

- New `BloomPostProcess`: render the 3-D scene into an **HDR FBO (RGBA16F)** instead
  of the default framebuffer, then bright-pass → ping-pong Gaussian blur (½/¼ res) →
  composite + tonemap to the default framebuffer. **ImGui draws last, in LDR.**
- **MSAA interaction (important):** the default framebuffer's 4x MSAA can't be used
  once we render into an FBO. Options: (a) multisample HDR FBO + resolve (costlier),
  or (b) drop MSAA for the scene and rely on bloom/FXAA. The wireframe grid currently
  leans on MSAA for crispness, so this is a visible trade-off (see decisions).

### 5.4 New shaders/files

- `Shaders/particle.vert`, `particle.frag` (point-sprite, SSBO vertex-pull, heat ramp).
- `Shaders/fullscreen.vert`, `brightpass.frag`, `blur.frag`, `composite.frag` (bloom).
- Compute (v1→v3, GLSL 430): `particle_integrate.comp`, `particle_grid.comp`,
  `particle_density.comp` (PBF density/lambda), `particle_solve.comp` (constraint),
  `particle_viscosity.comp`, `particle_heat.comp`. Passes separated by
  `GL.MemoryBarrier` between dispatches.

---

## 6. Exact integration points (named files)

| File | Change |
|---|---|
| **`SimulationWindow.cs`** (ctor) | `APIVersion = new Version(4, 3)`. |
| **`SimulationWindow.cs`** (`OnLoad`) | Probe GL caps; construct `ParticleSystem` (picks GPU/CPU backend), `ParticleRenderer`, `BloomPostProcess` (HDR FBO sized to client). Subscribe to a new `PhysicsEngine.BodyDisrupted` event (parallel to `BodiesMerged`). |
| **`SimulationWindow.cs`** (`OnRenderFrame`) | Bind HDR FBO before DrawBodies; after DrawTrails/Grid, draw particles (additive) into the HDR FBO; run bloom composite to default FB; then DrawUI + ImGui in LDR. Push the latest frame-time sample to the adaptive budgeter. |
| **`SimulationWindow.cs`** (`OnResize`) | Resize HDR + bloom FBOs. |
| **`SimulationWindow.cs`** (`DrawUI` / ImGui sidebar) | New "Fracture & Debris" section: enable fracture, `Q_merge`/`Q_disrupt`, particle cap, backend (Auto/GPU/CPU), heat cool/diffuse rates, bloom intensity, adaptive target FPS, live particle count + backend readout. |
| **`PhysicsEngine.cs`** (`HandleCollisions`) | In the overlap branch, compute `Q`. Route: low→existing merge; medium/high→`_particles.SpawnFromCollision(a, b, impact)` and remove the disrupted body/bodies; raise `BodyDisrupted`. New fields: `EnableFracture`, `Q_merge`, `Q_disrupt`. |
| **`PhysicsEngine.cs`** (`Update`) | Inside/after the fixed-`Dt` accumulator loop, advance particles: `_particles.Step(Dt, Bodies)` (passing the massive bodies for the external gravity field). Particle substep rate is adaptive (every substep vs every frame). |
| **`PhysicsEngine.cs`** | Hold a `ParticleSystem _particles` reference (or inject it) so collisions can spawn and the step can advance it. |
| **`Body.cs`** | *Optional:* add `float Strength` (disruption resistance) to bias `Q_disrupt` per body; otherwise no change — spawn reads Mass/Density/Radius/Color/Velocity/Position. |
| **NEW `ParticleSystem.cs`** | Owns pool, SSBO/CPU arrays, spatial hash, spawn/step/coalesce, adaptive budget, `IParticleBackend` (`GpuParticleBackend` compute / `CpuParticleBackend`). |
| **NEW `ParticleRenderer.cs`** | Point-sprite draw from SSBO (GPU) or VBO (CPU) + heat ramp. |
| **NEW `BloomPostProcess.cs`** | HDR FBO, bright-pass, ping-pong blur, composite. |
| **NEW `Shaders/*`** | Files listed in §5.4. |
| **`GravitySim.csproj`** | No new dependencies (OpenTK 4.9.4 already exposes `DispatchCompute`, `MemoryBarrier`, `BindBufferBase`, SSBO targets). |
| **`Program.cs`** | No change (GL version lives in `SimulationWindow`). |

---

## 7. Performance / LOD / adaptive-budget strategy

**Budget:** maintain an EMA of GPU+CPU frame time against a target (default 16.6 ms /
60 FPS, configurable). When over budget, pull these levers **in order**; when under
budget with headroom, release them in reverse:

1. **Particle update rate (LOD):** step the fluid solver every frame → every 2nd →
   every 3rd; ballistic-only particles can always update every frame (cheap).
2. **Spawn throttle:** scale new-disruption particle counts by current headroom; never
   exceed the hard cap.
3. **PBF iteration count:** 3 → 2 → 1 constraint iterations.
4. **Neighbor LOD:** demote distant / low-energy / cool particles to `BALLISTIC_LOD`
   (skip density/pressure/viscosity — pure gravity + heat). They still render.
5. **Render LOD:** lower bloom downsample resolution; clamp sprite size.

**Structural cost controls (always on):**
- Only colliding bodies become particles; the rest of the sim stays cheap point masses.
- Particle gravity = external field from the few bodies (O(P·B)), never all-pairs.
- Neighbor search via the uniform grid (O(n)), never O(n²) among particles.
- **Re-coalescence (v3)** is the long-run budget guarantee: settled, cool, low-dispersion
  clusters are converted back into solid `Body` objects (mass + momentum summed) and
  their particles returned to the pool — so a long session trends *back* toward cheap.
- GPU path keeps data resident in SSBOs; the only CPU↔GPU traffic is the spawn upload
  and a **tiny** readback (per-cluster summary for coalescence), never the full pool.

---

## 8. Risks & unknowns

- **Bloom/FBO is the biggest new rendering subsystem**, and it collides with the
  current 4x MSAA default framebuffer (need multisample-FBO+resolve, or drop MSAA →
  grid looks less crisp). Decision needed.
- **GPU grid build:** fixed-capacity buckets can drop neighbors at extreme density
  (banding); counting-sort variant avoids it but is more code. Start simple, upgrade
  if needed.
- **PBF tuning:** iteration count, h, rest density, surface-tension strength all
  interact; needs empirical tuning to avoid "boiling" or sticky blobs.
- **Re-coalescence heuristics are genuinely hard:** deciding *when* a cloud is
  "settled" and whether it reforms as one body or several is a clustering problem;
  risk of visible popping when particles snap into a sphere. This is the riskiest v3
  piece.
- **Spawn packing:** seeding N non-interpenetrating particles inside a sphere with a
  momentum-conserving ejecta field needs care, or the cloud "explodes" on frame 1.
- **Conservation caveats:** one-way gravity (particles don't pull bodies) and
  coalescence approximations mean momentum/energy aren't strictly conserved — fine for
  VFX, not for a physics-accurate sim. Decision needed.
- **Determinism:** GPU atomics give non-deterministic neighbor ordering → runs aren't
  bit-reproducible. Acceptable for VFX.
- **OpenTK 4.9.4 compute surface:** `DispatchCompute`/`MemoryBarrier`/`BindBufferBase`
  exist, but exact enum names need verifying at implementation time.
- **Spacetime grid:** does debris deform the well? Recommend **no** (keep `PotentialAt`
  driven by massive bodies only) to avoid coupling the per-frame grid update to 150k
  particles.

---

## 9. Staged roadmap (each stage independently shippable)

### v1 — Ballistic burst + heat color + glow  *(Approach-1 visual; all durable infra)*
- Pool + `ParticleSystem` + `SpawnFromCollision` (full disruption first; partial next).
- Ballistic integration under the external gravity field; accrete-on-contact with bodies.
- Heat: seed from impact KE, exponential cooling, blackbody color ramp (no diffusion yet).
- Rendering: additive point-sprites from the pool + **HDR FBO + bloom**.
- GL bumped to 4.3; capability probe + CPU fallback. (Ballistic is cheap enough that v1
  can even run CPU; recommend wiring the GPU integrate pass now to lay SSBO groundwork.)
- Adaptive: hard cap + frame-time-driven spawn throttle.
- UI: enable fracture, thresholds, cap, bloom intensity.
- **Visible result:** bodies shatter into a glowing, cooling ejecta spray that falls
  back under gravity. Ships and looks good.

### v2 — SPH/PBF cohesion  *(Approach 2)*
- GPU spatial-hash grid build; PBF density + lambda + constraint solve; XSPH viscosity;
  surface-tension/cohesion term.
- Adaptive solver iterations + update-rate LOD; `BALLISTIC_LOD` demotion.
- **Visible result:** debris behaves like a cohesive liquid — splashes, filaments,
  blobs that wobble and stretch.

### v3 — Heat diffusion + re-coalescence + (optional) metaballs  *(closes the hybrid)*
- Heat diffusion across neighbors (advected + diffused field; hot spots bleed outward).
- **Re-coalescence:** detect settled/cool/low-dispersion clusters → sum mass+momentum
  into new solid `Body`(ies) → free particles. Keeps long-run cost bounded.
- Optional **screen-space metaballs:** render particle thickness/depth to an FBO,
  smooth, reconstruct normals → a connected liquid surface (shaded/refractive).
- **Visible result:** a connected liquid with bleeding hot spots that, once it calms,
  reforms into solid bodies — the full shatter→fluid→reform cycle.

---

## 10. Decisions

### Locked (2026-06-13)

1. **GL bump + backend:** ✅ **GL 4.3 Core; GPU compute (SSBO PBF) primary, capped CPU
   fallback.**
2. **Particle cap & FPS target:** ✅ **150k GPU / 8k CPU, 60 FPS (16.6 ms) budget.**
3. **Bloom & MSAA trade-off:** ✅ **Drop the scene's 4x MSAA; rely on bloom + FXAA.**
   (Implication: remove `NumberOfSamples = 4` / `GL.Enable(Multisample)` from the
   scene path; add an optional FXAA pass in the bloom composite. Grid crispness now
   leans on FXAA.)
4. **Fracture default + thresholds:** ✅ **Fracture toggle defaults ON; `Q_merge` /
   `Q_disrupt` exposed as artistic sliders.** Low-energy hits still merge as today.

### Still open (sensible defaults assumed unless you say otherwise)

5. **Conservation vs. cost:** Assuming **one-way gravity** (particles don't pull on the
   massive bodies) and non-strict momentum/energy conservation for VFX. Override if you
   want two-way coupling.
6. **Mass fate:** Assuming mass is **conserved and returned** via v3 re-coalescence;
   pre-v3, cold particles fade out (temporary mass loss until that stage lands). Say so
   if you'd rather never reclaim mass.
7. **v1 scope:** Assuming **ballistic burst + heat + bloom** is the acceptable first
   ship, with true fluid cohesion in v2.
