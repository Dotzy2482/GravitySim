# Visual Polish & UI/UX Overhaul Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the five-phase visual polish spec (`docs/superpowers/specs/2026-07-03-visual-polish-design.md`): ACES tonemap + soft-knee bloom, starfield + procedural planet/star shaders, soft-absorption merge animation, ImGui sidebar modernization.

**Architecture:** All physics stays untouched except one mass-conserving sparkle spawn in the merge branch. New GL-free `MergeAnimator` drives render-side merge ghosts off the existing `BodiesMerged` event. New `Starfield` renderer draws first into the HDR FBO. `UiTheme` centralizes ImGui style tokens; the sidebar is restructured into tabs.

**Tech Stack:** C# / .NET 8, OpenTK 4.9.4, OpenGL 4.3 core (shaders are `#version 330 core`), ImGui.NET.

**Non-negotiable project rules (CLAUDE.md):**
- Shaders are pure ASCII. No em-dashes, no smart quotes, no Turkish characters, not even in comments. `smooth`, `flat`, `sample`, `buffer` are reserved GLSL words.
- Build must stay 0 warnings / 0 errors: `dotnet build GravitySim.csproj -c Debug`
- Never claim the app works because the process is alive. Launch check after every shader/GL change:
  ```powershell
  $p = Start-Process bin\Debug\net8.0\GravitySim.exe -WorkingDirectory bin\Debug\net8.0 `
       -RedirectStandardError err.log -PassThru; Start-Sleep 6
  if ($p.HasExited) { Get-Content bin\Debug\net8.0\err.log } else { "OK - still running"; Stop-Process $p.Id }
  ```
- ASCII check after editing any `Shaders/*` file (expect NO output):
  ```powershell
  Get-ChildItem Shaders\* | ForEach-Object {
    $bytes = [IO.File]::ReadAllBytes($_.FullName)
    for ($i = 0; $i -lt $bytes.Length; $i++) { if ($bytes[$i] -gt 127) { "$($_.Name): non-ASCII at byte $i"; break } }
  }
  ```
- Multi-line commit messages: write to a file, `git commit -F file` (PowerShell mangles `-m` here-strings).
- `Shader.SetXxx` silently no-ops on typo'd uniform names. Double-check every uniform string against the GLSL.

## File structure

| File | Action | Responsibility |
|---|---|---|
| `Shaders/composite.frag` | modify | ACES tonemap + exposure |
| `Shaders/brightpass.frag` | modify | soft-knee threshold |
| `PostProcess.cs` | modify | `Exposure` field, retuned defaults, new uniform |
| `Shaders/starfield.vert/.frag` | create | background stars + nebula |
| `Starfield.cs` | create | fullscreen background pass (own shader + empty VAO) |
| `Shaders/body.vert/.frag` | modify | object-space pos varying; procedural surfaces, atmosphere, limb darkening |
| `Body.cs` | modify | add visual `Seed` field (GL-free, safe for headless suite) |
| `MergeAnimator.cs` | create | GL-free merge animation state (testable headlessly) |
| `PhysicsEngine.cs` | modify | gentle-merge sparkle (mass-conserving) |
| `UiTheme.cs` | create | ImGui style tokens |
| `ImGuiController.cs` | modify | TTF font loading |
| `SimulationWindow.cs` | modify | render order, merge ghost drawing, tabbed sidebar |
| `CLAUDE.md` | modify | render order, obsolete "no tonemap" note, tuning guide |

---

### Task 1: ACES tonemap + exposure

**Files:** Modify `Shaders/composite.frag`, `PostProcess.cs`, `SimulationWindow.cs:788-791`

- [ ] **Step 1: Replace `Shaders/composite.frag`** with:

```glsl
#version 330 core
in vec2 vUV;
out vec4 FragColor;

uniform sampler2D uScene;
uniform sampler2D uBloom;
uniform float uBloomStrength;
uniform float uExposure;

// Narkowicz ACES filmic approximation. Maps HDR into 0..1 with a soft
// shoulder, so hot debris reads as glowing orange instead of clipped white.
vec3 acesTonemap(vec3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main()
{
    vec3 hdr = texture(uScene, vUV).rgb + uBloomStrength * texture(uBloom, vUV).rgb;
    // NOTE: the pipeline has never gamma-encoded; all existing colors were
    // authored in that space. Keep it that way - exposure compensates.
    FragColor = vec4(acesTonemap(hdr * uExposure), 1.0);
}
```

- [ ] **Step 2: Add exposure to `PostProcess.cs`.** Below `public float BloomStrength = 0.9f;` add:

```csharp
    public float Exposure = 1.1f;
```

In `Composite()`, after `_composite.SetFloat("uBloomStrength", BloomStrength);` add:

```csharp
        _composite.SetFloat("uExposure", Exposure);
```

- [ ] **Step 3: Add the Exposure slider.** In `SimulationWindow.DrawDebrisSection`, right after the `ImGui.Checkbox("Bloom", ref _post.Enabled);` line:

```csharp
        ImGui.SliderFloat("Exposure", ref _post.Exposure, 0.25f, 4f, "%.2f");
```

- [ ] **Step 4: Build.** Run `dotnet build GravitySim.csproj -c Debug`. Expected: 0 warnings, 0 errors.
- [ ] **Step 5: ASCII check** (command in header). Expected: no output.
- [ ] **Step 6: Launch check** (recipe in header). Expected: "OK - still running".
- [ ] **Step 7: Commit:** `git add -A` then commit message `Add ACES tonemap and exposure control to the post chain`.

---

### Task 2: Soft-knee bright-pass + retuned bloom defaults

**Files:** Modify `Shaders/brightpass.frag`, `PostProcess.cs:30-31`

- [ ] **Step 1: Replace `Shaders/brightpass.frag`** with:

```glsl
#version 330 core
in vec2 vUV;
out vec4 FragColor;

uniform sampler2D uScene;
uniform float uThreshold;

void main()
{
    vec3 c = texture(uScene, vUV).rgb;
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));

    // Soft knee: quadratic ramp-in around the threshold instead of a hard
    // cut. Kills flicker and stops mid-brightness pixels from smearing.
    float knee = uThreshold * 0.5;
    float soft = clamp(luma - uThreshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 1e-4);
    float contrib = max(soft, luma - uThreshold) / max(luma, 1e-4);

    FragColor = vec4(c * contrib, 1.0);
}
```

- [ ] **Step 2: Retune defaults in `PostProcess.cs`:**

```csharp
    public float BloomThreshold = 1.15f;
    public float BloomStrength = 0.55f;
```

- [ ] **Step 3: Build + ASCII check + launch check** (same three commands). Visually: shatter two bodies (preset 1, crank Sim speed) — debris should glow tight and orange, not fog the screen.
- [ ] **Step 4: Commit:** `Soften bloom threshold knee; retune bloom defaults for tonemapped output`.

---

### Task 3: Starfield background

**Files:** Create `Shaders/starfield.vert`, `Shaders/starfield.frag`, `Starfield.cs`; modify `SimulationWindow.cs` (field, OnLoad, OnRenderFrame, OnUnload)

- [ ] **Step 1: Create `Shaders/starfield.vert`:**

```glsl
#version 330 core
// Fullscreen triangle from gl_VertexID; passes NDC through for ray reconstruction.
out vec2 vNdc;

void main()
{
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vNdc = p * 2.0 - 1.0;
    gl_Position = vec4(vNdc, 0.0, 1.0);
}
```

- [ ] **Step 2: Create `Shaders/starfield.frag`:**

```glsl
#version 330 core
in vec2 vNdc;
out vec4 FragColor;

uniform mat4 uInvViewProj;
uniform vec3 uCamPos;
uniform float uTime;

// All output stays well below the bloom threshold (~1.15) so the sky never blooms.

float hash13(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}

float vnoise(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);
    float a = mix(hash13(i + vec3(0, 0, 0)), hash13(i + vec3(1, 0, 0)), u.x);
    float b = mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), u.x);
    float c = mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), u.x);
    float d = mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), u.x);
    return mix(mix(a, b, u.y), mix(c, d, u.y), u.z);
}

float fbm(vec3 p)
{
    float s = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 4; i++)
    {
        s += amp * vnoise(p);
        p *= 2.03;
        amp *= 0.5;
    }
    return s;
}

// Map a direction onto a cube face so star cells do not swim with the camera.
vec3 cubeCell(vec3 dir, out vec2 fracUv)
{
    vec3 a = abs(dir);
    vec2 uv;
    float face;
    if (a.x >= a.y && a.x >= a.z) { uv = dir.yz / a.x; face = dir.x > 0.0 ? 0.0 : 1.0; }
    else if (a.y >= a.z)          { uv = dir.xz / a.y; face = dir.y > 0.0 ? 2.0 : 3.0; }
    else                          { uv = dir.xy / a.z; face = dir.z > 0.0 ? 4.0 : 5.0; }
    uv = uv * 0.5 + 0.5;
    const float cells = 200.0;
    fracUv = fract(uv * cells);
    return vec3(floor(uv * cells), face);
}

void main()
{
    vec4 far = uInvViewProj * vec4(vNdc, 1.0, 1.0);
    vec3 dir = normalize(far.xyz / far.w - uCamPos);

    // Stars: sparse hashed cells, three brightness tiers, gentle twinkle.
    vec2 f;
    vec3 cell = cubeCell(dir, f);
    float h = hash13(cell);
    float star = 0.0;
    if (h > 0.91)
    {
        vec2 center = vec2(fract(h * 37.0), fract(h * 91.0)) * 0.6 + 0.2;
        float d = length(f - center);
        float tier = fract(h * 13.0);
        float bright = 0.12 + 0.75 * tier * tier;
        float tw = 0.85 + 0.15 * sin(uTime * (0.8 + 3.0 * fract(h * 53.0)) + h * 40.0);
        star = smoothstep(0.10, 0.0, d) * bright * tw;
    }

    // Nebula: two faint dust layers, cool blue and dim violet.
    float n1 = fbm(dir * 2.6 + vec3(11.0, 3.0, 7.0));
    float n2 = fbm(dir * 1.4 + vec3(3.0, 17.0, 5.0));
    vec3 nebula = vec3(0.045, 0.065, 0.14) * n1 * n1 + vec3(0.075, 0.045, 0.11) * n2 * n2 * 0.7;

    vec3 col = vec3(star) * 0.55 + nebula * 0.5 + vec3(0.008, 0.009, 0.02);
    FragColor = vec4(col, 1.0);
}
```

- [ ] **Step 3: Create `Starfield.cs`:**

```csharp
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// Procedural deep-space background: hashed point stars + faint nebula noise.
/// Drawn first into the HDR scene FBO with depth writes off; everything else
/// overdraws it. Output intensity stays below the bloom threshold by design.
/// </summary>
public class Starfield : IDisposable
{
    private readonly Shader _shader = new("Shaders/starfield.vert", "Shaders/starfield.frag");
    private readonly int _emptyVao = GL.GenVertexArray();

    public void Draw(Matrix4 view, Matrix4 projection, Vector3 camPos, float time)
    {
        GL.Disable(EnableCap.DepthTest);
        GL.DepthMask(false);

        _shader.Use();
        _shader.SetMatrix4("uInvViewProj", Matrix4.Invert(view * projection));
        _shader.SetVector3("uCamPos", camPos);
        _shader.SetFloat("uTime", time);

        GL.BindVertexArray(_emptyVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.BindVertexArray(0);

        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _shader.Dispose();
        GL.DeleteVertexArray(_emptyVao);
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 4: Wire into `SimulationWindow`.**
  - Field (next to `_particleRenderer`): `private Starfield _starfield = null!;` and `private float _timeSeconds;`
  - `OnLoad` (after `_particleRenderer = new ParticleRenderer();`): `_starfield = new Starfield();`
  - `OnRenderFrame`, top of method after the minimized guard: `_timeSeconds += (float)args.Time;`
  - `OnRenderFrame`, immediately after `_post.BeginScene();` and before `DrawBodies`:
    `_starfield.Draw(view, projection, _camera.Position, _timeSeconds);`
  - `OnUnload`: `_starfield.Dispose();` next to the other disposals.

- [ ] **Step 5: Build + ASCII check + launch check.** Visually: dim stars + faint nebula behind everything; orbiting the camera must keep stars fixed in the sky (no swimming).
- [ ] **Step 6: Commit:** `Add procedural starfield background pass`.

---

### Task 4: Procedural planet & star surfaces

**Files:** Modify `Shaders/body.vert`, `Shaders/body.frag`, `Body.cs`, `SimulationWindow.cs` (`DrawBodies`)

- [ ] **Step 1: Add object-space varying to `Shaders/body.vert`** (full replacement):

```glsl
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

out vec3 vNormal;
out vec3 vWorldPos;
out vec3 vObjPos;

void main()
{
    vec4 world = uModel * vec4(aPosition, 1.0);
    vWorldPos = world.xyz;
    vObjPos = aPosition; // unit-sphere position: stable domain for surface noise
    // Model is translate * uniform scale, so transforming the normal by the
    // upper 3x3 and renormalizing is exact.
    vNormal = normalize(mat3(uModel) * aNormal);
    gl_Position = uProjection * uView * world;
}
```

- [ ] **Step 2: Replace `Shaders/body.frag`** with:

```glsl
#version 330 core
in vec3 vNormal;
in vec3 vWorldPos;
in vec3 vObjPos;

uniform vec3 uColor;
uniform vec3 uLightPos;   // world-space point light (the star)
uniform vec3 uViewPos;
uniform float uEmissive;  // 0 = lit planet, 1 = self-luminous star
uniform float uSeed;      // per-body variation
uniform float uTime;      // star surface churn

out vec4 FragColor;

float hash13(vec3 p)
{
    p = fract(p * 0.1031 + uSeed);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}

float vnoise(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);
    float a = mix(hash13(i + vec3(0, 0, 0)), hash13(i + vec3(1, 0, 0)), u.x);
    float b = mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), u.x);
    float c = mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), u.x);
    float d = mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), u.x);
    return mix(mix(a, b, u.y), mix(c, d, u.y), u.z);
}

float fbm(vec3 p)
{
    float s = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 4; i++)
    {
        s += amp * vnoise(p);
        p *= 2.03;
        amp *= 0.5;
    }
    return s;
}

void main()
{
    vec3 n = normalize(vNormal);
    vec3 lightDir = normalize(uLightPos - vWorldPos);
    vec3 viewDir = normalize(uViewPos - vWorldPos);
    float ndv = max(dot(n, viewDir), 0.0);

    // ---- planet branch -------------------------------------------------
    // Two-tone procedural terrain derived from the body color, so the user
    // color picker still drives the overall look.
    float h = fbm(vObjPos * 3.1);
    float landMix = smoothstep(0.38, 0.62, h);
    vec3 lowland = uColor * 0.5;
    vec3 highland = mix(uColor, vec3(1.0), 0.22);
    vec3 albedo = mix(lowland, highland, landMix);
    // Fine grain so close-ups do not look vinyl-smooth.
    albedo *= 0.92 + 0.16 * vnoise(vObjPos * 14.0);

    // Half-Lambert wrap: softer terminator than raw N.L.
    float ndl = dot(n, lightDir);
    float wrap = clamp((ndl + 0.28) / 1.28, 0.0, 1.0);
    float diffuse = wrap * wrap;

    vec3 halfVec = normalize(lightDir + viewDir);
    float spec = pow(max(dot(n, halfVec), 0.0), 48.0) * 0.12 * diffuse;

    vec3 lit = albedo * (0.05 + diffuse) + vec3(spec);

    // Atmosphere: fresnel rim tinted between sky blue and the body color,
    // stronger on the lit side.
    float fresnel = pow(1.0 - ndv, 2.6);
    vec3 atmColor = mix(vec3(0.36, 0.56, 0.95), uColor, 0.35);
    lit += atmColor * fresnel * 0.4 * clamp(diffuse + 0.3, 0.0, 1.0);

    // ---- star branch ---------------------------------------------------
    // Limb darkening (bright center, dimmer edge) + slow churning surface.
    float limb = 0.5 + 0.5 * pow(ndv, 0.55);
    float churn = fbm(vObjPos * 4.0 + vec3(0.0, uTime * 0.12, uTime * 0.05));
    vec3 sun = uColor * limb * (0.85 + 0.6 * churn) * 1.7; // HDR: exceeds bloom threshold
    sun += uColor * pow(1.0 - ndv, 2.0) * 0.7;             // soft rim into the bloom

    vec3 color = mix(lit, sun, uEmissive);
    FragColor = vec4(color, 1.0);
}
```

- [ ] **Step 3: Add `Seed` to `Body.cs`.** After `public float Emissive;` add:

```csharp
    /// <summary>Random per-body seed for procedural surface variation (visual only).</summary>
    public float Seed = Random.Shared.NextSingle() * 61.7f;
```

- [ ] **Step 4: Set the new uniforms in `SimulationWindow.DrawBodies`.**
  - After `_bodyShader.SetVector3("uLightPos", FindLightPosition());` add:
    ```csharp
        _bodyShader.SetFloat("uTime", _timeSeconds);
    ```
  - Inside the body loop, after `_bodyShader.SetVector3("uColor", body.Color);` add:
    ```csharp
            _bodyShader.SetFloat("uSeed", body.Seed);
    ```
  - In the selected-body outline block, after `_bodyShader.SetVector3("uColor", HighlightColor);` add `_bodyShader.SetFloat("uSeed", _selected.Seed);` (keeps the halo stable).

- [ ] **Step 5: Build + ASCII check + launch check.** Visually: planets show terrain patches + blue-ish rim; the star has a bright center, dimmer edge, slowly boiling surface. Two bodies with the same color but different seeds must look different.
- [ ] **Step 6: Commit:** `Procedural planet surfaces, atmosphere rim, star limb darkening`.

---

### Task 5: MergeAnimator core (GL-free, test-first)

**Files:** Create `MergeAnimator.cs`; test via throwaway console app in the scratchpad (pattern from CLAUDE.md).

- [ ] **Step 1: Write the failing headless test.** Create `<scratchpad>\merge-anim-test\` console app:

```powershell
dotnet new console -o merge-anim-test --force
Copy-Item <repo>\Body.cs merge-anim-test\
Copy-Item <repo>\MergeAnimator.cs merge-anim-test\
dotnet add merge-anim-test package OpenTK.Mathematics --version 4.9.4
```

Replace `merge-anim-test\Program.cs` with:

```csharp
using GravitySim;
using OpenTK.Mathematics;

int failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures++;
}

// Survivor at origin (already holds merged mass), removed body was at x=+2.
var survivor = new Body("big", Vector3.Zero, Vector3.Zero, 20f, new Vector3(0.5f, 0.5f, 0.8f));
var removed = new Body("small", new Vector3(2f, 0f, 0f), Vector3.Zero, 4f, new Vector3(0.9f, 0.3f, 0.2f));

var anim = new MergeAnimator();
anim.OnMerge(removed, survivor);

Check(anim.Active.Count == 1, "one animation active after merge");

// Ghost starts at the removed body's offset and sinks monotonically inward.
float prevDist = float.MaxValue;
bool monotonic = true;
for (int i = 0; i < 40; i++)
{
    anim.Update(1f / 60f);
    if (anim.Active.Count == 0) break;
    var a = anim.Active[0];
    float dist = (anim.GhostPosition(a) - survivor.Position).Length;
    if (dist > prevDist + 1e-4f) monotonic = false;
    prevDist = dist;
}
Check(monotonic, "ghost distance to survivor is non-increasing");

// Survivor render radius eases: mid-animation it sits between old and new radius.
var s2 = new Body("big2", Vector3.Zero, Vector3.Zero, 20f, Vector3.One);
var r2 = new Body("small2", new Vector3(2f, 0f, 0f), Vector3.Zero, 4f, Vector3.One);
float oldR = Body.RadiusFromMass(s2.Mass, s2.Density);
s2.Mass += r2.Mass; s2.UpdateRadius(); // simulate what PhysicsEngine.Merge does pre-event
anim.OnMerge(r2, s2);
anim.Update(MergeAnimator.Duration * 0.3f);
float mid = anim.RenderRadius(s2);
Check(mid > oldR - 1e-4f && mid < s2.Radius + 1e-4f, "render radius eases between old and new");

// After Duration everything is gone and radius snaps to the real value.
anim.Update(MergeAnimator.Duration + 0.1f);
Check(anim.Active.Count == 0, "animations expire after Duration");
Check(MathF.Abs(anim.RenderRadius(s2) - s2.Radius) < 1e-5f, "render radius equals body radius when idle");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
return failures;
```

- [ ] **Step 2: Run it to confirm it fails** (MergeAnimator.cs missing): `dotnet run --project merge-anim-test`. Expected: compile error, `MergeAnimator` not found.

- [ ] **Step 3: Create `MergeAnimator.cs`** in the repo root:

```csharp
using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// Render-side "soft absorption" merge animation. Physics merges instantly
/// (PhysicsEngine.Merge); this replays the removed body as a shrinking ghost
/// that sinks into the survivor, and eases the survivor's *rendered* radius
/// from its pre-merge to its post-merge value. GL-free: only OpenTK.Mathematics,
/// so it compiles into the headless verification suite.
/// </summary>
public class MergeAnimator
{
    public const float Duration = 0.55f; // seconds

    public struct Anim
    {
        public Body Survivor;         // live ref: ghost tracks the survivor's motion
        public Vector3 StartOffset;   // removed.Position - survivor.Position at merge time
        public float GhostRadius;
        public Vector3 GhostColor;
        public float SurvivorOldRadius;
        public float Age;
    }

    private readonly List<Anim> _anims = new();

    public IReadOnlyList<Anim> Active => _anims;

    /// <summary>Hook for PhysicsEngine.BodiesMerged. Survivor already carries the merged mass/radius.</summary>
    public void OnMerge(Body removed, Body survivor)
    {
        _anims.Add(new Anim
        {
            Survivor = survivor,
            StartOffset = removed.Position - survivor.Position,
            GhostRadius = removed.Radius,
            GhostColor = removed.Color,
            SurvivorOldRadius = Body.RadiusFromMass(
                MathF.Max(survivor.Mass - removed.Mass, 0.01f), survivor.Density),
            Age = 0f,
        });
    }

    public void Update(float dt)
    {
        for (int i = _anims.Count - 1; i >= 0; i--)
        {
            var a = _anims[i];
            a.Age += dt;
            if (a.Age >= Duration || !ReferenceEquals(a.Survivor, a.Survivor) /* keep struct copy */)
                _anims[i] = a;
            if (a.Age >= Duration) _anims.RemoveAt(i);
            else _anims[i] = a;
        }
    }

    /// <summary>0..1 eased progress (smoothstep).</summary>
    public static float Progress(in Anim a)
    {
        float t = Math.Clamp(a.Age / Duration, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Ghost center: sinks from the contact offset into the survivor's current position.</summary>
    public Vector3 GhostPosition(in Anim a)
        => a.Survivor.Position + a.StartOffset * (1f - Progress(a));

    /// <summary>Ghost scale factor (shrinks to nothing).</summary>
    public static float GhostScale(in Anim a) => MathF.Pow(1f - Progress(a), 0.7f);

    /// <summary>
    /// The radius a body should be *drawn* at: eased old->new while it is the
    /// survivor of an active merge, its physics radius otherwise.
    /// </summary>
    public float RenderRadius(Body b)
    {
        for (int i = _anims.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_anims[i].Survivor, b))
            {
                var a = _anims[i];
                return MathHelper.Lerp(a.SurvivorOldRadius, b.Radius, Progress(a));
            }
        }
        return b.Radius;
    }
}
```

  Note the `Update` loop above contains a redundant self-comparison line — remove it; final loop body is:

```csharp
        for (int i = _anims.Count - 1; i >= 0; i--)
        {
            var a = _anims[i];
            a.Age += dt;
            if (a.Age >= Duration) { _anims.RemoveAt(i); continue; }
            _anims[i] = a;
        }
```

- [ ] **Step 4: Copy the file into the test app again and run:** `Copy-Item ..\GravitySim\MergeAnimator.cs merge-anim-test\ -Force; dotnet run --project merge-anim-test`. Expected: `ALL PASS`, exit code 0.
- [ ] **Step 5: Build the main project** (`dotnet build GravitySim.csproj -c Debug`): 0 warnings 0 errors.
- [ ] **Step 6: Commit:** `Add GL-free MergeAnimator with headless test coverage`.

---

### Task 6: Merge ghost rendering

**Files:** Modify `SimulationWindow.cs` (field, OnLoad hookup, DrawBodies)

- [ ] **Step 1: Own an animator.** Field: `private readonly MergeAnimator _mergeAnim = new();`
  In `OnLoad`, extend the existing `BodiesMerged` subscription:

```csharp
        _physics.BodiesMerged += (removed, survivor) =>
        {
            if (_selected == removed) _selected = survivor;
            _mergeAnim.OnMerge(removed, survivor);
        };
```

- [ ] **Step 2: Advance it.** In `OnUpdateFrame` after `_physics.Update(dt);`:

```csharp
        _mergeAnim.Update(_physics.Paused ? 0f : dt);
```

- [ ] **Step 3: Draw eased radius + ghosts.** In `DrawBodies`, change the model matrix line of the body loop to:

```csharp
            Matrix4 model = Matrix4.CreateScale(_mergeAnim.RenderRadius(body))
                          * Matrix4.CreateTranslation(body.Position);
```

  After the body loop (before the outline block), add:

```csharp
        // Soft-absorption merge ghosts: the removed body shrinks and sinks
        // into the survivor, squashed along the sink direction, with a hot
        // contact flare that fades out over the animation.
        foreach (var anim in _mergeAnim.Active)
        {
            float progress = MergeAnimator.Progress(anim);
            float scale = MergeAnimator.GhostScale(anim) * anim.GhostRadius;
            if (scale < 1e-3f) continue;

            Vector3 ghostPos = _mergeAnim.GhostPosition(anim);
            Vector3 axis = anim.StartOffset.LengthSquared > 1e-8f
                ? anim.StartOffset.Normalized() : Vector3.UnitY;

            // Squash along the sink axis: S' = R * diag(squash,1,1) * R^T.
            float squash = 1f - 0.35f * MathF.Sin(progress * MathF.PI);
            Matrix4 rot = Matrix4.CreateFromAxisAngle(
                Vector3.Cross(Vector3.UnitX, axis).LengthSquared > 1e-8f
                    ? Vector3.Cross(Vector3.UnitX, axis).Normalized() : Vector3.UnitY,
                MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.UnitX, axis), -1f, 1f)));
            Matrix4 model = Matrix4.CreateScale(squash * scale, scale, scale)
                          * rot
                          * Matrix4.CreateTranslation(ghostPos);

            _bodyShader.SetMatrix4("uModel", model);
            _bodyShader.SetVector3("uColor",
                Vector3.Lerp(anim.GhostColor, anim.Survivor.Color, progress));
            _bodyShader.SetFloat("uSeed", 1f);
            _bodyShader.SetFloat("uEmissive", 0.15f + 0.35f * progress); // heats up as it sinks
            _sphere.Draw();

            // Contact flare: tiny emissive sphere at the seam, fading out.
            float flare = (1f - progress) * 0.35f * anim.GhostRadius;
            if (flare > 1e-3f)
            {
                Vector3 seam = anim.Survivor.Position
                             + axis * _mergeAnim.RenderRadius(anim.Survivor) * 0.9f;
                Matrix4 flareModel = Matrix4.CreateScale(flare)
                                   * Matrix4.CreateTranslation(seam);
                _bodyShader.SetMatrix4("uModel", flareModel);
                _bodyShader.SetVector3("uColor", new Vector3(1.0f, 0.62f, 0.25f));
                _bodyShader.SetFloat("uEmissive", 1f);
                _sphere.Draw();
            }
        }
```

  (OpenTK `Matrix4` is row-vector convention — `scale * rot * translation` order matches the existing `CreateScale * CreateTranslation` usage.)

- [ ] **Step 4: Build + launch check.** Visually: preset 1, enable Collisions, let the two bodies fall together gently (Q below merge threshold): the smaller one should visibly sink into the big one over ~half a second with an orange flare at the seam — no pop.
- [ ] **Step 5: Commit:** `Render soft-absorption merge ghosts with contact flare and eased survivor radius`.

---

### Task 7: Gentle-merge sparkle (mass-conserving) + conservation check

**Files:** Modify `PhysicsEngine.cs:166-169`; headless check in scratchpad.

- [ ] **Step 1: Write the failing conservation check.** Create `<scratchpad>\merge-sparkle-test\` console app (same pattern: `dotnet new console`, add `OpenTK.Mathematics` 4.9.4, copy `Body.cs`, `PhysicsEngine.cs`, `ParticleSystem.cs`, `SpatialHash.cs`). `Program.cs`:

```csharp
using GravitySim;
using OpenTK.Mathematics;

var eng = new PhysicsEngine { EnableCollisions = true, EnableFracture = true };
var a = new Body("a", new Vector3(-1.2f, 0, 0), new Vector3(0.8f, 0, 0), 10f, Vector3.One);
var b = new Body("b", new Vector3(1.2f, 0, 0), new Vector3(-0.8f, 0, 0), 6f, Vector3.One);
eng.Bodies.Add(a);
eng.Bodies.Add(b);

float MassBodies() { float m = 0; foreach (var x in eng.Bodies) m += x.Mass; return m; }
float MassParticles() { float m = 0; for (int i = 0; i < eng.Particles.Count; i++) m += eng.Particles.Mass[i]; return m; }

float m0 = MassBodies();
Vector3 p0 = eng.TotalMomentum();

for (int i = 0; i < 600; i++) eng.Update(1f / 60f); // ~10 sim-seconds

float m1 = MassBodies() + MassParticles();
Vector3 p1 = eng.TotalMomentum();
for (int i = 0; i < eng.Particles.Count; i++) p1 += eng.Particles.Vel[i] * eng.Particles.Mass[i];

bool merged = eng.Bodies.Count == 1;
bool massOk = MathF.Abs(m1 - m0) < 1e-3f;
bool momOk = (p1 - p0).Length < 0.1f * MathF.Max(p0.Length, 1f) + 0.05f;

Console.WriteLine($"merged={merged} mass {m0}->{m1} ok={massOk} |dp|={(p1 - p0).Length} ok={momOk}");
Console.WriteLine(merged && massOk && momOk ? "ALL PASS" : "FAIL");
return merged && massOk && momOk ? 0 : 1;
```

  Run it BEFORE the change — expected `ALL PASS` (baseline: current code conserves). If baseline fails, STOP and investigate; do not proceed.

- [ ] **Step 2: Add the sparkle.** In `PhysicsEngine.HandleCollisions`, replace the plain-merge branch:

```csharp
                if (!EnableFracture || Q < QMerge)
                {
                    Merge(a, b, total);
                }
```

with:

```csharp
                if (!EnableFracture || Q < QMerge)
                {
                    Vector3 gentleVel = (a.Velocity * a.Mass + b.Velocity * b.Mass) / total;
                    Vector3 gentleColor = (a.Color * a.Mass + b.Color * b.Mass) / total;
                    Body gentleSurvivor = Merge(a, b, total);

                    // A pinch of glowing dust so even soft merges read as physical.
                    // Mass is moved from the survivor into the particles, so the
                    // conservation invariants stay exact.
                    if (EnableFracture && Q > 0.25f * QMerge && !gentleSurvivor.Anchored)
                    {
                        float sparkMass = MathF.Min(total * 0.002f, 0.5f);
                        if (gentleSurvivor.Mass - sparkMass > 0.01f)
                        {
                            gentleSurvivor.Mass -= sparkMass;
                            gentleSurvivor.UpdateRadius();
                            Particles.SpawnDebris(contact, gentleSurvivor.Radius, gentleVel,
                                                  gentleColor, sparkMass, contact, impactSpeed,
                                                  0.45f,
                                                  Math.Min(Particles.SuggestCount(sparkMass), 250));
                        }
                    }
                }
```

- [ ] **Step 3: Re-run the conservation check** (copy the modified `PhysicsEngine.cs` over, `dotnet run`). Expected: `ALL PASS` — total mass identical, momentum drift within tolerance, bodies merged.
- [ ] **Step 4: Build main project + launch check.**
- [ ] **Step 5: Commit:** `Spawn mass-conserving sparkle dust on energetic-but-gentle merges`.

---

### Task 8: UiTheme + font upgrade

**Files:** Create `UiTheme.cs`; modify `ImGuiController.cs:63`, `SimulationWindow.OnLoad`

- [ ] **Step 1: Create `UiTheme.cs`:**

```csharp
using ImGuiNET;
using SN = System.Numerics;

namespace GravitySim;

/// <summary>
/// Semantic color tokens + global ImGui style for the deep-space look.
/// Single source of truth: no raw hex anywhere else in the UI code.
/// </summary>
public static class UiTheme
{
    public static readonly SN.Vector4 Background  = Rgb(0x0D1220);
    public static readonly SN.Vector4 Surface     = Rgb(0x161D2E);
    public static readonly SN.Vector4 SurfaceHover= Rgb(0x1E2740);
    public static readonly SN.Vector4 SurfaceActive = Rgb(0x263252);
    public static readonly SN.Vector4 Accent      = Rgb(0x4CC3FF);
    public static readonly SN.Vector4 AccentDim   = Rgb(0x2A6E96);
    public static readonly SN.Vector4 Text        = Rgb(0xE6EDF7);
    public static readonly SN.Vector4 TextMuted   = Rgb(0x8A94A8);
    public static readonly SN.Vector4 Border      = Rgb(0x2A3350);
    public static readonly SN.Vector4 Danger      = Rgb(0xEF4444);

    public static void Apply()
    {
        var style = ImGui.GetStyle();

        style.WindowRounding = 0f;      // sidebar sits flush against the edge
        style.FrameRounding = 4f;
        style.GrabRounding = 4f;
        style.TabRounding = 4f;
        style.PopupRounding = 4f;
        style.ScrollbarRounding = 6f;
        style.WindowPadding = new SN.Vector2(10f, 10f);
        style.FramePadding = new SN.Vector2(8f, 4f);
        style.ItemSpacing = new SN.Vector2(8f, 6f);
        style.ScrollbarSize = 12f;
        style.WindowBorderSize = 0f;

        var c = style.Colors;
        c[(int)ImGuiCol.WindowBg]          = Background;
        c[(int)ImGuiCol.ChildBg]           = Background;
        c[(int)ImGuiCol.PopupBg]           = Surface;
        c[(int)ImGuiCol.Border]            = Border;
        c[(int)ImGuiCol.FrameBg]           = Surface;
        c[(int)ImGuiCol.FrameBgHovered]    = SurfaceHover;
        c[(int)ImGuiCol.FrameBgActive]     = SurfaceActive;
        c[(int)ImGuiCol.TitleBg]           = Background;
        c[(int)ImGuiCol.TitleBgActive]     = Background;
        c[(int)ImGuiCol.Header]            = Surface;
        c[(int)ImGuiCol.HeaderHovered]     = SurfaceHover;
        c[(int)ImGuiCol.HeaderActive]      = SurfaceActive;
        c[(int)ImGuiCol.Button]            = Surface;
        c[(int)ImGuiCol.ButtonHovered]     = SurfaceHover;
        c[(int)ImGuiCol.ButtonActive]      = AccentDim;
        c[(int)ImGuiCol.SliderGrab]        = Accent;
        c[(int)ImGuiCol.SliderGrabActive]  = Accent;
        c[(int)ImGuiCol.CheckMark]         = Accent;
        c[(int)ImGuiCol.Tab]               = Surface;
        c[(int)ImGuiCol.TabHovered]        = SurfaceHover;
        c[(int)ImGuiCol.TabSelected]       = SurfaceActive;
        c[(int)ImGuiCol.Separator]         = Border;
        c[(int)ImGuiCol.SeparatorHovered]  = AccentDim;
        c[(int)ImGuiCol.Text]              = Text;
        c[(int)ImGuiCol.TextDisabled]      = TextMuted;
        c[(int)ImGuiCol.PlotLines]         = Accent;
        c[(int)ImGuiCol.PlotHistogram]     = Accent;
        c[(int)ImGuiCol.ScrollbarBg]       = Background;
        c[(int)ImGuiCol.ScrollbarGrab]     = Surface;
        c[(int)ImGuiCol.ScrollbarGrabHovered] = SurfaceHover;
        c[(int)ImGuiCol.ScrollbarGrabActive]  = SurfaceActive;
        c[(int)ImGuiCol.ResizeGrip]        = Surface;
    }

    private static SN.Vector4 Rgb(uint hex, float a = 1f)
        => new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, a);
}
```

  **NOTE:** if `ImGuiCol.TabSelected` does not exist in the installed ImGui.NET version, the enum name is `ImGuiCol.TabActive` — check compile error and use whichever exists.

- [ ] **Step 2: Font.** In `ImGuiController` constructor, replace `io.Fonts.AddFontDefault();` with:

```csharp
        // Larger, crisper UI font. Segoe UI ships with every Windows 10/11
        // install; fall back to the bitmap default anywhere it is missing.
        string fontPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
        if (File.Exists(fontPath))
            io.Fonts.AddFontFromFileTTF(fontPath, 17f);
        else
            io.Fonts.AddFontDefault();
```

- [ ] **Step 3: Apply the theme.** In `SimulationWindow.OnLoad`, right after `_imgui = new ImGuiController(...)`: `UiTheme.Apply();`
- [ ] **Step 4: Build + launch check.** Sidebar should be navy with cyan accents and a readable 17px font. If the font atlas fails (blank text), the fallback path is wrong — check err.log.
- [ ] **Step 5: Commit:** `Add deep-space UI theme tokens and Segoe UI font atlas`.

---

### Task 9: Tabbed sidebar restructure

**Files:** Modify `SimulationWindow.cs` (`DrawUI` and section methods)

- [ ] **Step 1: Add helpers + FPS ring buffer.** New fields:

```csharp
    private readonly float[] _fpsHistory = new float[180];
    private int _fpsCursor;
```

New private static method:

```csharp
    private static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(280f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
```

- [ ] **Step 2: Replace the header pile in `DrawUI`** (keep the collapsed-button branch as is). Replace the five `CollapsingHeader` lines with:

```csharp
        if (ImGui.CollapsingHeader("Selected body", ImGuiTreeNodeFlags.DefaultOpen))
            DrawSelectedSection();

        ImGui.Spacing();
        if (ImGui.BeginTabBar("##sidebar_tabs"))
        {
            if (ImGui.BeginTabItem("Sim"))      { DrawSimTab();      ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Visuals"))  { DrawVisualsTab();  ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Collision")){ DrawCollisionTab();ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Stats"))    { DrawStatsTab();    ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
```

- [ ] **Step 3: Regroup the sections.** Rename/reshape the existing methods (content moves, no control is lost):

**`DrawSimTab()`** = old `DrawGlobalSection` minus visual toggles, plus advanced fold:

```csharp
    private void DrawSimTab()
    {
        if (ImGui.Button("Add Body")) AddBody();
        ImGui.SameLine();
        if (ImGui.Button($"Add random cluster ({_clusterSize})")) AddCluster(_clusterSize);
        ImGui.SliderInt("Cluster size", ref _clusterSize, 2, 40);

        ImGui.Separator();

        ImGui.SliderFloat("Gravity G", ref _physics.G, 0.01f, 20f, "%.3f", ImGuiSliderFlags.Logarithmic);
        if (ImGui.SliderFloat("Sim speed", ref _physics.TimeScale, 0.05f, 50f, "%.2fx", ImGuiSliderFlags.Logarithmic))
            UpdateTitle();

        bool paused = _physics.Paused;
        if (ImGui.Checkbox("Paused (Space)", ref paused)) { _physics.Paused = paused; UpdateTitle(); }
        ImGui.Checkbox("Collisions (merge)", ref _physics.EnableCollisions);
        ImGui.SameLine();
        ImGui.Checkbox("Drag-to-launch", ref _dragToLaunch);
        ImGui.Checkbox("Follow selected (F)", ref _followSelected);

        ImGui.Separator();
        ImGui.Text("Presets:");
        if (ImGui.Button("1: Two-body")) LoadPreset(1);
        ImGui.SameLine();
        if (ImGui.Button("2: Star system")) LoadPreset(2);
        ImGui.SameLine();
        if (ImGui.Button("3: Binary")) LoadPreset(3);
        if (ImGui.Button("Reset scene (R)", new System.Numerics.Vector2(-1, 0)))
            LoadPreset(_currentPreset);

        if (ImGui.TreeNode("Advanced##sim"))
        {
            ImGui.SliderFloat("Time step dt", ref _physics.Dt, 0.0005f, 0.02f, "%.4f", ImGuiSliderFlags.Logarithmic);
            HelpMarker("Fixed physics step. Smaller = more accurate, more CPU.");
            ImGui.SliderFloat("Softening", ref _physics.Softening, 0.001f, 1f, "%.3f", ImGuiSliderFlags.Logarithmic);
            HelpMarker("Plummer softening length: caps gravity at tiny distances so close passes stay stable.");
            ImGui.TreePop();
        }
    }
```

**`DrawVisualsTab()`** = display toggles + bloom + particle look + grid:

```csharp
    private void DrawVisualsTab()
    {
        ImGui.Checkbox("Grid (G)", ref _showGrid);
        ImGui.SameLine();
        if (ImGui.Checkbox("Trails", ref _showTrails) && !_showTrails)
            foreach (var body in _physics.Bodies) body.Trail.Clear();
        ImGui.SameLine();
        ImGui.Checkbox("Velocity arrows", ref _showVelocityArrows);

        ImGui.SeparatorText("Glow");
        ImGui.Checkbox("Bloom", ref _post.Enabled);
        ImGui.SliderFloat("Exposure", ref _post.Exposure, 0.25f, 4f, "%.2f");
        ImGui.SliderFloat("Bloom strength", ref _post.BloomStrength, 0f, 3f, "%.2f");
        ImGui.SliderFloat("Bloom threshold", ref _post.BloomThreshold, 0.1f, 3f, "%.2f");
        HelpMarker("Only pixels brighter than this bloom. Higher = tighter glow.");

        ImGui.SeparatorText("Debris look");
        ImGui.SliderFloat("Particle size", ref _particleRenderer.ParticleRadius, 0.03f, 1.0f, "%.2f");
        ImGui.SliderFloat("Brightness", ref _particleRenderer.Brightness, 0.2f, 4f, "%.2f");
        ImGui.Checkbox("Smooth blobs", ref _particleRenderer.Smooth);
        HelpMarker("Swells crowded sprites into connected liquid-looking blobs.");

        ImGui.SeparatorText("Spacetime grid");
        ImGui.SliderFloat("Well strength", ref _grid.WellStrength, 0.001f, 0.06f, "%.4f", ImGuiSliderFlags.Logarithmic);
        ImGui.SliderFloat("Max dip", ref _grid.MaxDip, 1f, 15f, "%.1f");
        if (ImGui.TreeNode("Advanced##grid"))
        {
            ImGui.SliderFloat("Extent", ref _pendingGridSize, 40f, 400f, "%.0f");
            if (ImGui.IsItemDeactivatedAfterEdit())
                _grid.Rebuild(_pendingGridSize, _pendingGridRes);
            ImGui.SliderInt("Resolution", ref _pendingGridRes, GridMesh.MinResolution, GridMesh.MaxResolution);
            if (ImGui.IsItemDeactivatedAfterEdit())
                _grid.Rebuild(_pendingGridSize, _pendingGridRes);
            ImGui.TreePop();
        }
    }
```

**`DrawCollisionTab()`** = old `DrawDebrisSection` minus the visual sliders moved above, with SPH folded:

```csharp
    private void DrawCollisionTab()
    {
        var ps = _physics.Particles;

        if (!_physics.EnableCollisions)
            ImGui.TextWrapped("Tip: enable \"Collisions\" in the Sim tab for impacts to resolve.");

        ImGui.Checkbox("Fracture on impact", ref _physics.EnableFracture);
        ImGui.SameLine();
        if (ImGui.Button("Clear debris")) ps.Clear();

        ImGui.SliderFloat("Merge below Q", ref _physics.QMerge, 0.1f, 200f, "%.2f", ImGuiSliderFlags.Logarithmic);
        HelpMarker("Specific impact energy Q = kinetic energy per unit total mass. Below this: clean merge.");
        ImGui.SliderFloat("Shatter above Q", ref _physics.QDisrupt, 0.1f, 500f, "%.2f", ImGuiSliderFlags.Logarithmic);
        if (_physics.QDisrupt < _physics.QMerge) _physics.QDisrupt = _physics.QMerge;

        ImGui.SliderFloat("Detail (particles/mass)", ref ps.ParticlesPerUnitMass, 100f, 6000f, "%.0f");
        ImGui.SliderInt("Max per impact", ref ps.MaxPerDisruption, 500, 30000);
        ImGui.SliderFloat("Cooling rate", ref ps.CoolRate, 0.05f, 3f, "%.2f");
        ImGui.SliderFloat("Particle life (s)", ref ps.BaseLife, 2f, 40f, "%.1f");

        ImGui.SeparatorText("Fluid & heat");
        ImGui.Checkbox("Fluid cohesion (SPH)", ref ps.EnableSph);
        ImGui.Checkbox("Re-coalesce settled debris", ref ps.EnableCoalesce);
        HelpMarker("Cooled, settled debris clusters reform into solid bodies (mass and momentum conserved).");

        if (ImGui.TreeNode("Advanced##sph"))
        {
            ImGui.SliderFloat("Cohesion", ref ps.Cohesion, 0f, 40f, "%.1f");
            ImGui.SliderFloat("Repulsion", ref ps.Repulsion, 5f, 150f, "%.0f");
            ImGui.SliderFloat("Viscosity", ref ps.Viscosity, 0f, 1f, "%.2f");
            ImGui.SliderFloat("Neighbour radius", ref ps.SphRadius, 0.3f, 2.0f, "%.2f");
            ImGui.SliderFloat("Rest spacing", ref ps.RestDist, 0.1f, 1.5f, "%.2f");
            if (ps.RestDist >= ps.SphRadius) ps.RestDist = ps.SphRadius * 0.9f;
            ImGui.SliderFloat("Heat diffusion", ref ps.HeatDiffuse, 0f, 8f, "%.2f");
            ImGui.SliderInt("Min cluster size", ref ps.CoalesceMinCluster, 50, 5000);
            ImGui.TreePop();
        }
    }
```

**`DrawStatsTab()`** = old stats + perf caps + FPS sparkline:

```csharp
    private void DrawStatsTab()
    {
        var ps = _physics.Particles;
        var io = ImGui.GetIO();

        _fpsHistory[_fpsCursor] = io.Framerate;
        _fpsCursor = (_fpsCursor + 1) % _fpsHistory.Length;
        ImGui.PlotLines("##fps", ref _fpsHistory[0], _fpsHistory.Length, _fpsCursor,
                        $"FPS {io.Framerate:0.}", 0f, 165f,
                        new System.Numerics.Vector2(-1f, 46f));

        float ke = _physics.TotalKineticEnergy();
        float pe = _physics.TotalPotentialEnergy();
        ImGui.Text($"Bodies: {_physics.Bodies.Count}   Particles: {ps.Count} / {ps.MaxParticles}");
        ImGui.Text($"Kinetic energy:   {ke:0.0}");
        ImGui.Text($"Potential energy: {pe:0.0}");
        ImGui.Text($"Total energy:     {ke + pe:0.0}");
        ImGui.Text($"|Momentum|: {_physics.TotalMomentum().Length:0.000}");
        ImGui.Text($"Spawn scale: {ps.SpawnScale:0.00}x (adaptive)");

        ImGui.SeparatorText("Performance");
        ImGui.SliderFloat("Target FPS", ref _targetFps, 30f, 240f, "%.0f");
        ImGui.SliderInt("Particle cap", ref _pendingParticleCap, 5000, 150000);
        if (ImGui.IsItemDeactivatedAfterEdit()) _physics.Particles.Allocate(_pendingParticleCap);
    }
```

  Delete the now-empty `DrawGlobalSection`, `DrawGridSection`, `DrawDebrisSection`, `DrawStatsSection`. `DrawSelectedSection` stays unchanged.
  **NOTE:** `ImGui.SeparatorText` needs ImGui.NET >= 1.89.4; if it does not compile, use `ImGui.Separator(); ImGui.TextDisabled("label");` instead.

- [ ] **Step 4: Build + launch check.** Every control listed above must still exist somewhere (count sliders if unsure — nothing gets dropped).
- [ ] **Step 5: Commit:** `Restructure sidebar into tabs with advanced folds, tooltips, FPS sparkline`.

---

### Task 10: Docs, CLAUDE.md, final human tuning

**Files:** Modify `CLAUDE.md`; on-screen session with the owner.

- [ ] **Step 1: Update `CLAUDE.md`:**
  - Architecture map, `PostProcess.cs` row: replace "composite" description with "composite (ACES tonemap + exposure)".
  - Render order line: `_post.BeginScene()` → **starfield** → bodies (+ merge ghosts) → trails → grid → particles → `_post.Composite()` → ImGui.
  - Add rows: `Starfield.cs + Shaders/starfield.*` (procedural background), `MergeAnimator.cs` (GL-free merge animation, headless-testable), `UiTheme.cs` (ImGui tokens).
  - Tuning guide: add "Image too dark/bright -> Exposure" and note the old "clips to white reads as molten" comment is gone (tonemapped now).
- [ ] **Step 2: Build + full launch check one more time.**
- [ ] **Step 3: Commit:** `Update CLAUDE.md for the visual polish pipeline`.
- [ ] **Step 4: Human tuning session (owner at the screen).** Launch the app; owner judges: exposure default, bloom strength/threshold, particle Brightness vs the new tonemap, star churn speed, nebula faintness, merge animation duration/squash, theme colors, font size. Apply slider-default changes they request, then commit `Retune visual defaults after on-screen review`.
- [ ] **Step 5:** Invoke superpowers:finishing-a-development-branch (merge/PR decision — note: no `gh` CLI; use the GitHub REST API via Git Bash token flow from CLAUDE.md).

## Self-review notes

- Spec coverage: Phase 1 = Tasks 1-2, Phase 2 = Tasks 3-4, Phase 3 = Tasks 5-7, Phase 4 = Tasks 8-9, Phase 5 = Task 10. Starfield, atmosphere, limb darkening, ghost+flare, sparkle, tokens, font, tabs, tooltips, sparkline: all present.
- Uniform names cross-checked: `uExposure` (set in Task 1 Step 2, read in Step 1), `uSeed`/`uTime` (set Task 4 Step 4, read Step 2), `uInvViewProj`/`uCamPos`/`uTime` (Task 3). Remember rule 9: a typo fails silently.
- Type consistency: `MergeAnimator.Duration`, `.Active`, `.OnMerge`, `.Update`, `.GhostPosition`, `.RenderRadius`, `Progress`, `GhostScale` used identically in Tasks 5 and 6. `Body.Seed` (Task 4) used in Task 6 ghost draw (`uSeed`, constant 1f — deliberate).
- Known API risks flagged inline: `ImGuiCol.TabSelected` vs `TabActive`, `ImGui.SeparatorText` availability. Both have written fallbacks.
