# GravitySim Visual Polish & UI/UX Overhaul — Design Spec

Date: 2026-07-03 · Branch: `feature/visual-polish` · Status: awaiting owner review

## Goal

Physics is done and verified; this effort is purely cosmetic ("makyaj"). Make the
simulation look cinematic-realistic (Interstellar/NASA mood) and the control panel feel
like a polished product, without touching physics behaviour or the headless-verified
invariants (CLAUDE.md rule 7).

Owner decisions locked in:
1. **Art direction:** cinematic realistic (not neon, not minimal).
2. **Bloom fix:** add tonemapping + tighter, soft-knee bloom (not just slider defaults).
3. **UI:** keep the left sidebar, modernize it (custom theme + tabs + basic/advanced split).
4. **Merges:** "soft absorption" — the smaller body visually sinks into the survivor.

## Current state (audit)

- `Shaders/body.frag`: flat single colour, Lambert + 0.08 ambient; stars = colour + rim.
  No textures, no noise, no atmosphere, no starfield background (clear colour is near-black).
- `PostProcess.cs` + `composite.frag`: additive bloom **with no tonemap** — hot cores clip
  to white and the half-res 6-pass blur smears everything bright. This is the "everything
  is blurry" complaint.
- Merges (`PhysicsEngine.HandleCollisions` → `Merge`): instantaneous. One body vanishes,
  the survivor's radius snaps larger. A `BodiesMerged(removed, survivor)` event already
  exists — perfect hook for a visual-only animation.
- UI (`SimulationWindow.DrawSidebar`): stock ImGui dark theme, 5 always-open
  CollapsingHeaders, ~30 raw sliders, default 13 px bitmap font, no grouping/tooltips.

## Design

### Phase 1 — Post-processing foundation (bloom de-blur)

The rest of the visual work is judged through the post chain, so this lands first.

- **ACES filmic tonemap + exposure** in `composite.frag`: `color = ACES(scene + bloom * strength)`,
  new `uExposure` uniform (default 1.0). Kills the white clipping; hot debris reads as
  glowing-orange instead of blown-out white.
- **Soft-knee bright-pass** in `brightpass.frag` (knee = threshold/2): smooth onset instead
  of the hard `max(luma - t, 0)` cut; less flicker, less indiscriminate glow.
- **Retune defaults** after tonemap: `BloomThreshold 1.0 → ~1.15`, `BloomStrength 0.9 → ~0.55`,
  keep 6 half-passes (half-res is fine once the strength drops).
- **UI additions:** Exposure slider next to the bloom controls.
- Note: CLAUDE.md's "no tonemap, clipping reads as molten" comment becomes obsolete —
  update the comment and CLAUDE.md when this lands. The particle blackbody ramp
  (`particle.frag`) and `Brightness` default likely need a compensating retune (verify on
  screen, per the "visual tuning needs a human" rule).

### Phase 2 — Planet & star visuals

All shader work; **ASCII-only files** (CLAUDE.md rule 1), no reserved-word names (rule 2).

- **Procedural planet surfaces** in `body.frag`: 3-4 octave value noise (hash-based, no
  textures) over object-space position + per-body `uSeed`; two-tone palette derived from
  the existing `Body.Color` (darken/saturate for lowlands, lighten for highlands) so user
  colour picks still work. Optional banded-gas look for large-radius bodies (blend factor
  from radius uniform).
- **Atmosphere rim:** fresnel-weighted additive tint (sky-blue for rocky, colour-matched
  for gas) — cheap, no extra pass, sells "planet" instantly.
- **Nicer lighting:** half-Lambert wrap (softer terminator) + small specular lobe.
- **Stars:** limb darkening (brightness falls toward the silhouette edge) + slow noise
  churn on the surface; the star's HDR output stays > bloom threshold so the corona comes
  from bloom, now controlled by the Phase 1 tonemap.
- **Starfield background:** procedural fullscreen pass (or big inverted cube) drawn first
  with depth-write off: hashed point stars (2-3 brightness tiers, subtle twinkle) + very
  faint nebula noise tint. Must stay **below** bloom threshold so the background never blooms.

### Phase 3 — Soft-absorption merges

Renderer-side only; the physics merge stays instantaneous and conservation-exact.

- New `MergeAnimator` (list of active merge animations, owned by `SimulationWindow`,
  fed by the existing `BodiesMerged` event).
- For ~0.5 s after a merge: draw a **ghost** of the removed body (its last mesh/colour)
  easing along the contact axis into the survivor, scaling down and slightly squashing
  along the contact normal; meanwhile the survivor's rendered radius eases from the old
  value to the new one (render-side lerp — `Body.Radius` itself is physics state and is
  not touched).
- Contact-point hot glow: brief emissive flare (small additive sprite reusing the particle
  renderer's ramp) at the contact point, faded over the animation.
- Cheap crater-spray sparkle for gentle merges (a handful of particles via the existing
  pool, respecting spawn caps) so even Q < QMerge impacts feel physical.

### Phase 4 — Sidebar modernization

- **Design tokens** (single static theme class, semantic names — no scattered hex):
  background `#0D1220`, surface `#161D2E`, hover `#1E2740`, accent `#4CC3FF` (ice-cyan;
  fits the cinematic-space mood better than the stock blue), text `#E6EDF7`, muted text
  `#8A94A8`, danger `#EF4444`. Rounded corners (4-6 px), 8 px padding rhythm.
- **Font upgrade:** bundle a TTF (Inter or similar permissive-licence font) and load it at
  ~15-16 px via `ImGuiController` font atlas (risk item — controller currently uses the
  default font; needs a font-loading path).
- **Tabs** (`BeginTabBar`) replacing the header pile: **Sim** (G, dt, speed, integrator,
  toggles, presets) · **Görsel/Visuals** (bloom/exposure, particles, grid, trails)
  · **Çarpışma/Collision** (Q thresholds, SPH, coalescence) · **Stats** (energy, FPS plot).
  "Selected body" stays pinned above the tabs — it is contextual, not a category.
- **Basic/advanced split:** rarely-touched sliders (softening, SPH radii, rest spacing,
  heat diffusion, blur passes…) fold into an "Advanced" `TreeNode` per tab, closed by default.
- **Polish details:** units in labels, `(?)` hover tooltips on physics-jargon sliders
  (Q, softening, XSPH…), `PlotLines` sparkline for FPS and total energy in Stats,
  accent-coloured section separators, consistent slider widths.

### Phase 5 — Debris & final tuning (human-in-the-loop)

- Re-balance `Brightness`, blackbody ramp, `ParticleRadius`, bloom sliders against the new
  tonemap; verify "Smooth blobs" still reads as connected liquid.
- Full on-screen pass with the owner: launch, shatter two bodies, merge two bodies, judge
  slider defaults by eye (CLAUDE.md: visual tuning needs a human).

## Explicitly out of scope (YAGNI)

- GPU compute SPH (deprioritized 2026-07-03, owner decision — unchanged).
- Screen-space metaball surface (stays on the roadmap, not this branch).
- Texture assets from disk (everything stays procedural), shadows/shadow maps,
  physically-correct atmospheric scattering, docking/multi-window ImGui.

## Error handling & risks

| Risk | Mitigation |
|---|---|
| Shader crash on launch (rules 1-2) | ASCII-only check before commit; launch via the stderr-capture recipe in CLAUDE.md after every shader change |
| Tonemap changes the familiar look too much | Exposure + strength sliders expose the old feel; keep `PostProcess.Enabled` toggle as escape hatch |
| Font loading breaks ImGuiController atlas | Do font work in its own commit; fall back to default font if the TTF path fails at runtime |
| Merge ghost desyncs from physics (body re-shattered mid-animation) | Animator holds copies (position/radius/colour), never live `Body` refs; animations are fire-and-forget and clamp to survivor's current position each frame |
| Perf regression from noise shaders | Noise runs per-fragment on small spheres — negligible; still check the 2/4.7/11.6 ms particle budget stays intact (rule 8) |

## Testing

- `dotnet build -c Debug` → 0 warnings, 0 errors after every phase (CLAUDE.md).
- Launch check with stderr capture (`HasExited` recipe) after every shader/GL change.
- Headless physics suite re-run after Phase 3 (merge path touched only via event
  subscription — conservation checks must stay green).
- Visual acceptance: owner judges Phases 1, 2, 3 on screen before defaults are frozen.

## Implementation order & sizing

| Phase | Size | Depends on |
|---|---|---|
| 1. Tonemap + bloom | S | — |
| 2. Planet/star/starfield shaders | L | 1 |
| 3. Soft-absorption merge | M | 1 (glow judged through tonemap) |
| 4. Sidebar theme + tabs + font | M | — (parallel-safe) |
| 5. Retune + human pass | S | 1-4 |
