# GravitySim

A 3D Newtonian N-body gravity simulation in C# / OpenTK, in the style of the
classic "Simulating Gravity in C++" project: celestial bodies rendered as lit
spheres, a deforming spacetime "rubber sheet" grid underneath whose wells follow
the gravitational potential in real time, and a Dear ImGui sidebar for live
editing of every body and simulation parameter.

![tech](https://img.shields.io/badge/.NET-8.0-blueviolet) ![gl](https://img.shields.io/badge/OpenGL-3.3_core-blue) ![ui](https://img.shields.io/badge/UI-ImGui.NET-green)

## Build & run

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (8.0 or newer)
on Windows.

```
dotnet run -c Release
```

Dependencies (OpenTK 4.x, ImGui.NET) are pulled via NuGet on first restore; the
GLSL shaders in `Shaders/` are copied next to the executable automatically.

## Controls

### Mouse (in the 3D viewport)
| Input | Action |
|---|---|
| **Right-click a body** | Select it (gold outline + sidebar editing) |
| **Left-drag the selected body** | Move it on a screen-parallel plane (physics holds it while dragged) |
| **Shift + left-drag** | Constrain the drag to the horizontal XZ plane |
| **Middle-drag** | Orbit the camera |
| **Scroll wheel** | Zoom |

Clicks and typing inside the sidebar never affect the camera or selection
(input is gated behind ImGui's capture flags).

### Keyboard
| Key | Action |
|---|---|
| **W / A / S / D** (hold) | Pan the camera (camera-relative, horizontal plane) |
| **Q / E** (hold) | Move the camera down / up |
| **Tab** | Collapse / open the side panel |
| **Space** | Pause / resume |
| **R** | Reset the current scene |
| **G** | Toggle the spacetime grid |
| **F** | Follow the selected body with the camera |
| **Delete** | Delete the selected body |
| **↑ → +** / **↓ ← −** (hold) | Speed up / slow down time |
| **1 / 2 / 3** | Switch scene preset |
| **Esc** | Quit |

## The sidebar

- **Selected body** — name, log-scale mass slider (radius re-derives from
  density), density, position/velocity (with speed readout), color picker,
  Star/Emissive toggle, **Anchored** (exerts gravity but never moves), per-body
  trail toggle, kinetic energy, and distance/orbital-speed readouts relative to
  the most massive other body (with the circular-orbit speed for reference),
  plus a Delete button.
- **Global** — *Add Body* (spawns at the camera target, auto-selected so you can
  drag it immediately), *Add random cluster (N)* (orbiting the dominant mass),
  sliders for **G**, **dt**, **sim speed**, **softening**, toggles for grid /
  trails / velocity arrows / **collisions (merge)** / **drag-to-launch** /
  follow camera, preset buttons, and Reset.
- **Spacetime grid** — extent, resolution (capped at 300² vertices), well
  strength, and max dip.
- **Stats** — body count, kinetic + potential + total energy, total momentum
  magnitude, FPS.

## Scene presets

1. **Two-body** — one star, one planet in a circular orbit.
2. **Star system** *(default)* — a central star, five planets on slightly tilted
   orbits, and a moon around the outermost planet.
3. **Binary stars** — two equal-mass stars orbiting their barycenter, with three
   circumbinary planets.

All initial speeds are derived from `v = √(GM/r)`; momentum is balanced so
systems don't drift.

## Physics

Newton's law with Plummer softening, all pairs (O(n²)):

```
F = G · m₁ · m₂ / (r² + ε²)
```

- **Velocity Verlet** integration by default (symplectic, stable orbits);
  semi-implicit Euler also implemented (`PhysicsEngine.Integrator`).
- Fixed physics timestep with an accumulator, decoupled from the framerate.
- Dragged and anchored bodies are skipped by the integrator but still exert
  gravity on everything else.
- Optional collisions: overlapping bodies merge, conserving mass and momentum
  and blending color/density by mass.

## Parameters to tweak

Everything important is live in the sidebar. Code-side defaults:

| Parameter | Where | Default |
|---|---|---|
| `G` / `Dt` / `Softening` / `TimeScale` | `PhysicsEngine.cs` | `1.0` / `1/240` / `0.05` / `1.0` |
| `Integrator` | `PhysicsEngine.cs` | `VelocityVerlet` |
| `DefaultDensity` | `Body.cs` | `12` (radius = `∛(3m/4πρ)`) |
| Grid extent / resolution | `SimulationWindow.OnLoad` | `175` / `220` (cap 300) |
| `WellStrength` / `MaxDip` | `GridMesh.cs` | `0.012` / `7.0` (tanh soft cap) |
| Trail length | `SimulationWindow.cs` `MaxTrailPoints` | `700` points |
| Sphere detail | `SphereMesh` ctor | `32 × 64` segments |

## Code structure

| File | Responsibility |
|---|---|
| `Body.cs` | Body data: position, velocity, mass, density→radius, color, emissive, anchored/held flags, trail buffer |
| `PhysicsEngine.cs` | N-body forces, softening, Verlet/Euler integrators, collisions, energy/momentum, potential sampling |
| `ScenePresets.cs` | The three switchable starting systems |
| `SphereMesh.cs` | Procedural UV-sphere, drawn per body via model matrices |
| `GridMesh.cs` | The rubber sheet: XZ grid displaced per frame by potential; rebuildable extent/resolution |
| `LineRenderer.cs` | Streaming VBO for orbit trails and velocity arrows |
| `Shader.cs` | GLSL program loading + uniform helpers |
| `OrbitCamera.cs` | Yaw/pitch/distance orbit camera |
| `ImGuiController.cs` | Dear ImGui backend for OpenTK / GL 3.3 core (fonts, draw lists, input) |
| `SimulationWindow.cs` | `GameWindow`: input (picking, dragging), update loop, render loop, sidebar UI |
| `Shaders/body.*` | Diffuse-lit planets, rim-glow emissive stars |
| `Shaders/grid.*` | Semi-transparent wireframe, tinted by well depth |
| `Shaders/trail.*` | Fading polylines (trails, velocity arrows) |

Physics and rendering stay decoupled: `PhysicsEngine` and `Body` reference only
`OpenTK.Mathematics` — no OpenGL or UI types.
