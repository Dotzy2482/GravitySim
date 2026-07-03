# GravitySim — Project Rules & Knowledge Base

3D N-body gravity simulation. C# / .NET 8, OpenTK 4.9.4, **OpenGL 4.3 Core**, ImGui.NET
sidebar. Collision-fluid feature: hard impacts shatter bodies into a hot, cohesive,
glowing SPH debris cloud that cools, spreads heat, and re-coalesces into solid bodies.

## Build / Run / Verify

```powershell
dotnet build GravitySim.csproj -c Debug        # must stay 0 warnings / 0 errors
```

**Never claim "the app works" because the process is alive.** OpenTK creates the window
*before* `OnLoad` runs, so a shader/FBO crash still flashes a window. Always launch with
stderr captured and check `HasExited`:

```powershell
$p = Start-Process bin\Debug\net8.0\GravitySim.exe -WorkingDirectory bin\Debug\net8.0 `
     -RedirectStandardError err.log -PassThru; Start-Sleep 6
if ($p.HasExited) { Get-Content err.log }   # crash -> stack trace is here
```

**Headless physics verification** (the screen can't be auto-screenshotted — the
computer-use allowlist doesn't resolve custom exes): compile the GL-free sources
(`Body.cs`, `PhysicsEngine.cs`, `ParticleSystem.cs`, `SpatialHash.cs`) into a throwaway
console app and assert invariants. The suite used during development checked:
fracture spawns hot debris; momentum conserved through disruption; SPH contains the
cloud vs ballistic (no NaN/blow-up); heat diffusion lowers temperature variance;
settled debris re-coalesces conserving mass. Re-create it for any physics change.

## Architecture map

| File | Role |
|---|---|
| `PhysicsEngine.cs` | Fixed-dt accumulator (`Dt=1/240`), O(n²) gravity + Plummer softening, energy-branched `HandleCollisions` (Q = ½μv²/M: merge < `QMerge` < crater spray < `QDisrupt` < full shatter), owns `Particles`, steps them once per frame over consumed sim-time, re-adds coalesced bodies |
| `ParticleSystem.cs` | Pooled SoA debris (hard cap, swap-remove). Weakly-compressible SPH + XSPH viscosity + heat diffusion + re-coalescence (`ExtractCoalesced`). One-way gravity from bodies (O(P·B)) |
| `SpatialHash.cs` | O(n) hashed uniform grid (counting sort), dedup 27-cell queries. Distance-check every candidate — distinct cells can share a bucket |
| `ParticleRenderer.cs` + `Shaders/particle.*` | Additive point sprites, blackbody heat ramp, `Crowd` swells dense sprites into connected blobs |
| `PostProcess.cs` | HDR RGBA16F FBO → bright-pass → ping-pong blur (half-res) → composite → FXAA. Scene MSAA is intentionally OFF (FBO path); FXAA replaces it |
| `SimulationWindow.cs` | Main loop, input, ImGui sidebar, adaptive spawn budget (`SpawnScale` throttles on frame-time EMA) |
| Render order | `_post.BeginScene()` → bodies → trails → grid → particles (additive, depth-write off) → `_post.Composite()` → ImGui (LDR, on top) |

## Hard rules (each one was learned the painful way)

1. **Shaders must be pure ASCII.** A U+2014 em-dash in a *comment* made the NVIDIA GLSL
   compiler fail with `unexpected $end at token "<EOF>"` and the app crashed on launch.
   No smart quotes, no em-dashes, no Turkish characters in any `Shaders/*` file.
2. **`smooth` is a reserved GLSL keyword** (interpolation qualifier). So are `flat`,
   `sample`, `buffer`. Don't use them as variable names.
3. **SPH force model: use density-pressure, not pairwise springs.** The first attempt
   (per-pair repulsion inside r0 / attraction outside) collapsed the blob into a dense
   boiling ball that never settled. The stable model: per-particle number density vs a
   lattice rest density (`RestDensity()`), pressure = stiffness·(ρ−ρ0), symmetric
   (ρi+ρj) force along the pair axis, different stiffness for compression (`Repulsion`)
   vs sparsity (`Cohesion`).
4. **Parallel SPH passes may only write their own index.** Three `Parallel.For` passes
   (density → force → integrate) with an implicit barrier between; reads of neighbor
   state come from the previous pass's output. Serial below 2048 particles.
5. **`stackalloc` never inside a loop body** (CA2014 — stack grows until return).
   Hoist it; inside a `Parallel.For` lambda is fine (fresh stack per invocation).
6. **Cap the fluid's per-frame sim-time** (`dt = min(dt, 0.08)` in `ParticleSystem.Step`).
   High `TimeScale` (up to 50×) would otherwise multiply SPH substeps → the impact
   freeze. Debris lagging at extreme speed is invisible; a hitch is not.
7. **Conservation invariants:** disruption must conserve mass exactly and momentum to
   ~10% (ejecta mean-velocity subtraction). Re-coalescence sums mass+momentum into the
   new body. Any physics edit must keep the headless checks passing.
8. **Perf budget** (Release, i7-class): 6k particles ≈ 2 ms, 12k ≈ 4.7 ms, 24k ≈ 11.6 ms
   per frame. Defaults: `ParticlesPerUnitMass=500`, `MaxPerDisruption=6000`. Don't raise
   defaults past what keeps a two-body shatter under ~5 ms.
9. **`Shader` uniform setters silently no-op on unknown names** (location −1). A typo'd
   uniform name fails invisibly — double-check names against the GLSL source.
10. **Adding a vertex attribute to particles**: update `FloatsPerParticle`, the
    `VertexAttribPointer` layout, the scratch-pack loop in `ParticleRenderer.Draw`, AND
    `particle.vert` locations together.

## Tooling quirks on this machine (Windows 11, PowerShell 5.1)

- **No `gh` CLI.** Create/edit PRs via the GitHub REST API. Get the token in **Git Bash**
  (PowerShell mangles the stdin): `printf 'protocol=https\nhost=github.com\n\n' | git
  credential fill | sed -n 's/^password=//p'`, then POST with Python `urllib`.
- **Multi-line commit messages:** PowerShell here-strings passed to `git commit -m`
  get split — write the message to a file and use `git commit -F file`.
- `git push` stderr surfaces as a scary `NativeCommandError` in PowerShell — check for
  the `old..new branch -> branch` line; that means success.
- LF→CRLF warnings on commit are noise; ignore.
- The claude-mem plugin intercepts `Read` on previously-observed files (returns 1 line).
  Workaround: read via `Grep` pattern `^` with `output_mode: content`; a 1-line `Read`
  still registers the file for `Edit`.

## Tuning guide (ImGui "Collision & debris")

- Impacts only merge → lower **Shatter above Q** (or raise impact speed).
- Debris flies apart / no blobs → raise **Cohesion**, then **Viscosity**.
- Debris clumps too hard / looks solid → raise **Repulsion** or lower **Cohesion**.
- Frame hitches on impact → lower **Detail** or **Max per impact**; the adaptive
  **Spawn scale** readout shows if the auto-throttle is already engaged.
- Glow too weak/strong → **Brightness**, **Bloom strength**, **Bloom threshold**.
- Blobs look like separate dots → enable **Smooth blobs** (crowd-based sprite swell).

## Roadmap / future work

- **GPU compute port** of the SPH: SSBOs + `particle_*.comp` dispatches, per the staged
  design in `docs/collision-fluid-plan.md`. **Deprioritized (2026-07-03, owner decision):**
  the parallel CPU SPH holds 60 FPS at the default caps, so don't build this unless the
  particle budget needs to grow well past ~24k. The GL 4.3 context stays in place for it.
- **Screen-space metaball surface** (depth/thickness pass + smoothing + normal
  reconstruction) for a true connected-liquid look; current approach approximates it
  with crowd-swelled additive sprites.
- Visual tuning needs a human: physics is verified headlessly, but final slider values
  should be judged on screen.
