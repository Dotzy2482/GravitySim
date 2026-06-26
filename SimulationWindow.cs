using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace GravitySim;

/// <summary>
/// Main window: owns the physics engine and all GPU resources.
/// OnUpdateFrame advances physics + input; OnRenderFrame draws the scene and UI.
/// </summary>
public class SimulationWindow : GameWindow
{
    private readonly PhysicsEngine _physics = new();
    private readonly OrbitCamera _camera = new();

    private Shader _bodyShader = null!;
    private Shader _gridShader = null!;
    private Shader _trailShader = null!;
    private SphereMesh _sphere = null!;
    private GridMesh _grid = null!;
    private LineRenderer _lines = null!;
    private ParticleRenderer _particleRenderer = null!;
    private PostProcess _post = null!;
    private ImGuiController _imgui = null!;

    // Adaptive debris budget: throttle spawns to hold the frame-time target.
    private float _frameMsEma = 16.6f;
    private float _targetFps = 60f;
    private int _pendingParticleCap;

    // --- UI / interaction state ---
    private const float SidebarWidth = 340f;
    private Body? _selected;
    private bool _dragging;
    private bool _cameraRotating;
    private Vector3 _dragVelocity;
    private bool _sidebarOpen = true;
    private bool _showGrid = true;
    private bool _showTrails = true;
    private bool _showVelocityArrows = false;
    private bool _dragToLaunch = false;
    private bool _followSelected = false;
    private int _currentPreset = 2;
    private int _clusterSize = 10;
    private int _spawnCounter = 0;
    private float _pendingGridSize;
    private int _pendingGridRes;
    private float[] _lineScratch = new float[4096];

    private const int MaxTrailPoints = 700;
    private static readonly Vector3 HighlightColor = new(1.0f, 0.85f, 0.25f);

    public SimulationWindow()
        : base(GameWindowSettings.Default,
               new NativeWindowSettings
               {
                   ClientSize = new Vector2i(1440, 900),
                   Title = "GravitySim",
                   API = ContextAPI.OpenGL,
                   // 4.3 core: enables SSBOs/compute for the GPU debris path (v2+).
                   // The scene now renders through an HDR FBO, so MSAA on the default
                   // framebuffer is gone — anti-aliasing is handled by FXAA in PostProcess.
                   APIVersion = new Version(4, 3),
                   Profile = ContextProfile.Core,
               })
    {
        VSync = VSyncMode.On;
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        GL.ClearColor(0.012f, 0.012f, 0.03f, 1f); // deep-space near-black
        GL.Enable(EnableCap.DepthTest);

        _bodyShader = new Shader("Shaders/body.vert", "Shaders/body.frag");
        _gridShader = new Shader("Shaders/grid.vert", "Shaders/grid.frag");
        _trailShader = new Shader("Shaders/trail.vert", "Shaders/trail.frag");
        _sphere = new SphereMesh(latSegments: 32, lonSegments: 64);
        _grid = new GridMesh(size: 175f, resolution: 220);
        _lines = new LineRenderer();
        _particleRenderer = new ParticleRenderer();
        _post = new PostProcess(ClientSize.X, ClientSize.Y);
        _imgui = new ImGuiController(ClientSize.X, ClientSize.Y);

        _pendingParticleCap = _physics.Particles.MaxParticles;

        _pendingGridSize = _grid.Size;
        _pendingGridRes = _grid.Resolution;

        // If a merge eats the selected body, follow the survivor.
        _physics.BodiesMerged += (removed, survivor) =>
        {
            if (_selected == removed) _selected = survivor;
        };

        LoadPreset(_currentPreset);
    }

    private void LoadPreset(int preset)
    {
        _currentPreset = preset;
        ScenePresets.Load(preset, _physics);
        _selected = null;
        _dragging = false;
        UpdateTitle();
    }

    // ------------------------------------------------------------ update ----

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        float dt = (float)args.Time;

        HandleInput(dt);
        _physics.Update(dt);

        // Selection can vanish via collisions even with the merge handler (defensive).
        if (_selected != null && !_physics.Bodies.Contains(_selected))
            _selected = null;

        if (_followSelected && _selected != null)
            _camera.Target = Vector3.Lerp(_camera.Target, _selected.Position,
                                          1f - MathF.Exp(-8f * dt));

        RecordTrails();
    }

    private void HandleInput(float dt)
    {
        var io = ImGui.GetIO();
        var kb = KeyboardState;
        var mouse = MouseState;

        if (!io.WantCaptureKeyboard)
        {
            if (kb.IsKeyPressed(Keys.Escape)) Close();
            if (kb.IsKeyPressed(Keys.Space)) { _physics.Paused = !_physics.Paused; UpdateTitle(); }
            if (kb.IsKeyPressed(Keys.G)) { _showGrid = !_showGrid; UpdateTitle(); }
            if (kb.IsKeyPressed(Keys.R)) LoadPreset(_currentPreset);
            if (kb.IsKeyPressed(Keys.F)) _followSelected = !_followSelected;
            if (kb.IsKeyPressed(Keys.Tab)) _sidebarOpen = !_sidebarOpen;
            if (kb.IsKeyPressed(Keys.Delete)) DeleteSelected();

            if (kb.IsKeyPressed(Keys.D1)) LoadPreset(1);
            if (kb.IsKeyPressed(Keys.D2)) LoadPreset(2);
            if (kb.IsKeyPressed(Keys.D3)) LoadPreset(3);

            // WASD: pan the camera target (camera-relative, horizontal plane).
            // Q/E: move it down/up along world Y.
            float panForward = 0f, panRight = 0f, panUp = 0f;
            if (kb.IsKeyDown(Keys.W)) panForward += 1f;
            if (kb.IsKeyDown(Keys.S)) panForward -= 1f;
            if (kb.IsKeyDown(Keys.D)) panRight += 1f;
            if (kb.IsKeyDown(Keys.A)) panRight -= 1f;
            if (kb.IsKeyDown(Keys.E)) panUp += 1f;
            if (kb.IsKeyDown(Keys.Q)) panUp -= 1f;
            if (panForward != 0f || panRight != 0f || panUp != 0f)
            {
                _followSelected = false; // manual panning overrides follow mode
                _camera.Pan(panForward, panRight, panUp, dt);
            }

            // Speed up / slow down: arrows or +/- (held — smooth ramp).
            const float ramp = 1.02f;
            if (kb.IsKeyDown(Keys.Up) || kb.IsKeyDown(Keys.Right) ||
                kb.IsKeyDown(Keys.Equal) || kb.IsKeyDown(Keys.KeyPadAdd))
            {
                _physics.TimeScale = MathHelper.Clamp(_physics.TimeScale * ramp, 0.05f, 50f);
                UpdateTitle();
            }
            if (kb.IsKeyDown(Keys.Down) || kb.IsKeyDown(Keys.Left) ||
                kb.IsKeyDown(Keys.Minus) || kb.IsKeyDown(Keys.KeyPadSubtract))
            {
                _physics.TimeScale = MathHelper.Clamp(_physics.TimeScale / ramp, 0.05f, 50f);
                UpdateTitle();
            }
        }

        // Mouse: interactions may only START outside the UI; once a drag or camera
        // rotation is in progress it continues even if the cursor crosses the sidebar.
        // Right button: select. Left button: drag the selected body. Middle: orbit camera.
        if (mouse.IsButtonPressed(MouseButton.Right) && !io.WantCaptureMouse)
        {
            var (origin, dir) = MouseRay();
            Body? hit = PickBody(origin, dir);
            if (hit != null) _selected = hit;
        }

        if (mouse.IsButtonPressed(MouseButton.Left) && !io.WantCaptureMouse && _selected != null)
        {
            // Dragging starts only when the press lands on the selected body.
            var (origin, dir) = MouseRay();
            if (PickBody(origin, dir) == _selected)
                BeginDrag();
        }

        if (mouse.IsButtonPressed(MouseButton.Middle) && !io.WantCaptureMouse)
            _cameraRotating = true;

        if (_dragging && mouse.IsButtonDown(MouseButton.Left))
            UpdateDrag(dt);

        if (_cameraRotating && mouse.IsButtonDown(MouseButton.Middle))
            _camera.Rotate(mouse.Delta.X, mouse.Delta.Y);

        if (mouse.IsButtonReleased(MouseButton.Left))
            EndDrag();

        if (mouse.IsButtonReleased(MouseButton.Middle))
            _cameraRotating = false;
    }

    // ------------------------------------------------------------ picking ----

    /// <summary>World-space ray from the camera through the cursor.</summary>
    private (Vector3 origin, Vector3 dir) MouseRay()
    {
        // NDC: x right, y up — flip the cursor's top-left-origin Y.
        float x = 2f * MousePosition.X / Size.X - 1f;
        float y = 1f - 2f * MousePosition.Y / Size.Y;

        Matrix4 invVP = Matrix4.Invert(_camera.GetViewMatrix() *
                                       _camera.GetProjectionMatrix(Size.X / (float)Size.Y));

        Vector4 near = new Vector4(x, y, -1f, 1f) * invVP;
        Vector4 far  = new Vector4(x, y,  1f, 1f) * invVP;
        near /= near.W;
        far /= far.W;

        Vector3 origin = near.Xyz;
        return (origin, (far.Xyz - origin).Normalized());
    }

    /// <summary>
    /// Nearest ray-sphere hit. Every body is guaranteed a minimum *on-screen*
    /// pick target (~12 px radius): the pick sphere grows with distance from the
    /// camera, so tiny or far-away planets stay comfortably clickable.
    /// </summary>
    private Body? PickBody(Vector3 origin, Vector3 dir)
    {
        // World-space size of one vertical pixel at distance 1 from the camera.
        float pixelToWorld = 2f * MathF.Tan(MathHelper.DegreesToRadians(OrbitCamera.FovDegrees) * 0.5f)
                           / Size.Y;
        const float minPickPixels = 12f;

        Body? best = null;
        float bestT = float.MaxValue;

        foreach (var b in _physics.Bodies)
        {
            float distAlongRay = MathF.Max(Vector3.Dot(b.Position - origin, dir), 0.1f);
            float minPick = minPickPixels * pixelToWorld * distAlongRay;
            float r = MathF.Max(b.Radius * 1.25f, minPick);

            Vector3 oc = origin - b.Position;
            float half = Vector3.Dot(oc, dir);
            float disc = half * half - (oc.LengthSquared - r * r);
            if (disc < 0f) continue;

            float sqrt = MathF.Sqrt(disc);
            float t = -half - sqrt;
            if (t < 0f) t = -half + sqrt; // camera inside the sphere
            if (t > 0f && t < bestT) { bestT = t; best = b; }
        }
        return best;
    }

    // ------------------------------------------------------------ dragging ----

    private void BeginDrag()
    {
        if (_selected == null) return;
        _dragging = true;
        _dragVelocity = Vector3.Zero;
        _selected.IsHeld = true;
        _selected.Velocity = Vector3.Zero;
    }

    private void UpdateDrag(float dt)
    {
        if (_selected == null) return;

        // Default: plane through the body, parallel to the screen.
        // Shift: constrain to the horizontal XZ plane at the body's height.
        bool shift = KeyboardState.IsKeyDown(Keys.LeftShift) || KeyboardState.IsKeyDown(Keys.RightShift);
        Vector3 normal = shift ? Vector3.UnitY
                               : (_camera.Target - _camera.Position).Normalized();

        var (origin, dir) = MouseRay();
        float denom = Vector3.Dot(dir, normal);
        if (MathF.Abs(denom) < 1e-4f) return;

        float t = Vector3.Dot(_selected.Position - origin, normal) / denom;
        if (t <= 0f) return;

        Vector3 newPos = origin + dir * t;
        if (dt > 1e-5f)
            _dragVelocity = Vector3.Lerp(_dragVelocity, (newPos - _selected.Position) / dt, 0.35f);

        _selected.Position = newPos;
        _selected.Velocity = Vector3.Zero; // held: no residual motion
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (_selected != null)
        {
            _selected.IsHeld = false;
            if (_dragToLaunch) _selected.Velocity = _dragVelocity * 0.5f;
        }
    }

    // ------------------------------------------------------------ bodies ----

    private void DeleteSelected()
    {
        if (_selected == null) return;
        _physics.Bodies.Remove(_selected);
        _selected = null;
        _dragging = false;
    }

    private void AddBody()
    {
        var rnd = Random.Shared;
        var color = new Vector3(0.35f + 0.6f * rnd.NextSingle(),
                                0.35f + 0.6f * rnd.NextSingle(),
                                0.35f + 0.6f * rnd.NextSingle());
        var body = new Body($"Body {++_spawnCounter}", _camera.Target, Vector3.Zero, 8f, color);
        _physics.Bodies.Add(body);
        _physics.ComputeAccelerations();
        _selected = body; // ready to drag/edit immediately
    }

    private void AddCluster(int count)
    {
        var rnd = Random.Shared;
        Body? primary = _physics.MostMassive();

        for (int i = 0; i < count; i++)
        {
            float angle = rnd.NextSingle() * MathHelper.TwoPi;
            float radius = 7f + 14f * rnd.NextSingle();
            var pos = _camera.Target + new Vector3(radius * MathF.Cos(angle),
                                                   (rnd.NextSingle() - 0.5f) * 4f,
                                                   radius * MathF.Sin(angle));
            var vel = Vector3.Zero;
            if (primary != null)
            {
                // Near-circular orbit around the dominant body, with some jitter.
                Vector3 rel = pos - primary.Position;
                float dist = MathF.Max(rel.Length, 1f);
                Vector3 tangent = Vector3.Cross(Vector3.UnitY, rel).Normalized();
                vel = primary.Velocity + tangent * _physics.CircularOrbitSpeed(primary.Mass, dist)
                                       * (0.85f + 0.3f * rnd.NextSingle());
            }

            var color = new Vector3(0.35f + 0.6f * rnd.NextSingle(),
                                    0.35f + 0.6f * rnd.NextSingle(),
                                    0.35f + 0.6f * rnd.NextSingle());
            _physics.Bodies.Add(new Body($"Body {++_spawnCounter}", pos, vel,
                                         0.3f + 5f * rnd.NextSingle(), color));
        }
        _physics.ComputeAccelerations();
    }

    private void RecordTrails()
    {
        bool advancing = !_physics.Paused || _dragging;
        if (!_showTrails || !advancing) return;

        foreach (var b in _physics.Bodies)
        {
            if (!b.ShowTrail) continue;
            if (b.Trail.Count == 0 || (b.Position - b.Trail[^1]).LengthSquared > 0.0025f)
            {
                b.Trail.Add(b.Position);
                if (b.Trail.Count > MaxTrailPoints)
                    b.Trail.RemoveRange(0, b.Trail.Count - MaxTrailPoints);
            }
        }
    }

    // ------------------------------------------------------------ render ----

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        if (ClientSize.X == 0 || ClientSize.Y == 0) return; // minimized

        _imgui.Update(this, (float)args.Time);
        UpdateAdaptiveBudget((float)args.Time);

        Matrix4 view = _camera.GetViewMatrix();
        Matrix4 projection = _camera.GetProjectionMatrix(Size.X / (float)Size.Y);

        // Scene → HDR target; debris drawn last (additive) so it glows over everything.
        _post.BeginScene();
        DrawBodies(view, projection);
        DrawTrailsAndArrows(view, projection);
        if (_showGrid) DrawGrid(view, projection);
        _particleRenderer.Draw(_physics.Particles, view, projection, ClientSize.Y);
        _post.Composite(); // bright-pass + blur + composite + FXAA → screen

        DrawUI();
        _imgui.Render();

        SwapBuffers();
    }

    /// <summary>
    /// Hold the frame-time target by scaling how many particles future disruptions
    /// spawn. With VSync on, frame time only exceeds the target once the GPU/CPU can't
    /// keep up, at which point we throttle; with headroom the scale recovers toward 1.
    /// </summary>
    private void UpdateAdaptiveBudget(float dt)
    {
        _frameMsEma = MathHelper.Lerp(_frameMsEma, dt * 1000f, 0.1f);
        float targetMs = 1000f / MathF.Max(_targetFps, 1f);
        var ps = _physics.Particles;

        if (_frameMsEma > targetMs * 1.15f)
            ps.SpawnScale = MathF.Max(0.1f, ps.SpawnScale * 0.96f);
        else if (_frameMsEma < targetMs * 0.85f)
            ps.SpawnScale = MathF.Min(1f, ps.SpawnScale * 1.04f);
    }

    private void DrawBodies(Matrix4 view, Matrix4 projection)
    {
        _bodyShader.Use();
        _bodyShader.SetMatrix4("uView", view);
        _bodyShader.SetMatrix4("uProjection", projection);
        _bodyShader.SetVector3("uViewPos", _camera.Position);
        _bodyShader.SetVector3("uLightPos", FindLightPosition());

        foreach (var body in _physics.Bodies)
        {
            Matrix4 model = Matrix4.CreateScale(body.Radius)
                          * Matrix4.CreateTranslation(body.Position);
            _bodyShader.SetMatrix4("uModel", model);
            _bodyShader.SetVector3("uColor", body.Color);
            // Selected bodies get a small emissive boost on top of the outline.
            float emissive = body == _selected
                ? MathF.Min(1f, body.Emissive + 0.25f)
                : body.Emissive;
            _bodyShader.SetFloat("uEmissive", emissive);
            _sphere.Draw();
        }

        // Outline: redraw the selected body slightly enlarged with front faces
        // culled — only the rim of back faces survives the depth test, which
        // reads as a halo around the silhouette.
        if (_selected != null)
        {
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(TriangleFace.Front);

            Matrix4 model = Matrix4.CreateScale(_selected.Radius * 1.18f + 0.05f)
                          * Matrix4.CreateTranslation(_selected.Position);
            _bodyShader.SetMatrix4("uModel", model);
            _bodyShader.SetVector3("uColor", HighlightColor);
            _bodyShader.SetFloat("uEmissive", 1f);
            _sphere.Draw();

            GL.CullFace(TriangleFace.Back);
            GL.Disable(EnableCap.CullFace);
        }
    }

    /// <summary>Light the scene from the brightest star; fall back to above the scene.</summary>
    private Vector3 FindLightPosition()
    {
        Body? star = null;
        foreach (var b in _physics.Bodies)
            if (b.Emissive > 0.5f && (star == null || b.Mass > star.Mass))
                star = b;
        return star?.Position ?? new Vector3(0, 30, 0);
    }

    private void DrawTrailsAndArrows(Matrix4 view, Matrix4 projection)
    {
        if (!_showTrails && !_showVelocityArrows) return;

        _trailShader.Use();
        _trailShader.SetMatrix4("uView", view);
        _trailShader.SetMatrix4("uProjection", projection);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);

        foreach (var b in _physics.Bodies)
        {
            if (_showTrails && b.ShowTrail && b.Trail.Count >= 2)
            {
                int n = b.Trail.Count;
                EnsureScratch(n * 4);
                for (int i = 0; i < n; i++)
                {
                    _lineScratch[i * 4 + 0] = b.Trail[i].X;
                    _lineScratch[i * 4 + 1] = b.Trail[i].Y;
                    _lineScratch[i * 4 + 2] = b.Trail[i].Z;
                    _lineScratch[i * 4 + 3] = i / (float)(n - 1); // fade toward the tail
                }
                _trailShader.SetVector3("uColor", b.Color);
                _trailShader.SetFloat("uAlpha", 0.6f);
                _lines.Draw(_lineScratch, n, PrimitiveType.LineStrip);
            }

            if (_showVelocityArrows && b.Velocity.LengthSquared > 1e-4f)
                DrawVelocityArrow(b);
        }

        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    private void DrawVelocityArrow(Body b)
    {
        Vector3 dir = b.Velocity.Normalized();
        float len = MathF.Max(b.Velocity.Length * 0.45f, b.Radius * 1.5f);
        Vector3 tip = b.Position + dir * (b.Radius + len);

        // Arrowhead barbs in a stable plane around the shaft.
        Vector3 up = MathF.Abs(dir.Y) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
        Vector3 side = Vector3.Cross(dir, up).Normalized();
        float barb = MathF.Min(0.25f * len, 0.8f);
        Vector3 b1 = tip - dir * barb + side * barb * 0.6f;
        Vector3 b2 = tip - dir * barb - side * barb * 0.6f;

        EnsureScratch(6 * 4);
        WriteLineVert(0, b.Position + dir * b.Radius, 1f);
        WriteLineVert(1, tip, 1f);
        WriteLineVert(2, tip, 1f);
        WriteLineVert(3, b1, 1f);
        WriteLineVert(4, tip, 1f);
        WriteLineVert(5, b2, 1f);

        _trailShader.SetVector3("uColor", Vector3.Lerp(b.Color, Vector3.One, 0.5f));
        _trailShader.SetFloat("uAlpha", 0.9f);
        _lines.Draw(_lineScratch, 6, PrimitiveType.Lines);
    }

    private void WriteLineVert(int index, Vector3 pos, float fade)
    {
        _lineScratch[index * 4 + 0] = pos.X;
        _lineScratch[index * 4 + 1] = pos.Y;
        _lineScratch[index * 4 + 2] = pos.Z;
        _lineScratch[index * 4 + 3] = fade;
    }

    private void EnsureScratch(int floats)
    {
        if (_lineScratch.Length < floats)
            _lineScratch = new float[Math.Max(floats, _lineScratch.Length * 2)];
    }

    private void DrawGrid(Matrix4 view, Matrix4 projection)
    {
        _grid.Update(_physics);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false); // transparent pass: test depth but don't write it

        _gridShader.Use();
        _gridShader.SetMatrix4("uView", view);
        _gridShader.SetMatrix4("uProjection", projection);
        _gridShader.SetFloat("uBaseHeight", _grid.BaseHeight);
        _gridShader.SetFloat("uMaxDip", _grid.MaxDip);
        _gridShader.SetVector4("uBaseColor", new Vector4(0.25f, 0.45f, 0.85f, 0.35f));
        _gridShader.SetVector3("uWellColor", new Vector3(0.7f, 0.45f, 1.0f));
        _grid.Draw();

        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    // ------------------------------------------------------------ UI ----

    private void DrawUI()
    {
        var viewport = ImGui.GetMainViewport();

        if (!_sidebarOpen)
        {
            // Collapsed: just a small floating button to bring the panel back.
            ImGui.SetNextWindowPos(viewport.WorkPos);
            ImGui.Begin("##sidebar_collapsed",
                        ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
                        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar |
                        ImGuiWindowFlags.AlwaysAutoResize);
            if (ImGui.Button(">> Panel (Tab)")) _sidebarOpen = true;
            ImGui.End();
            return;
        }

        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(SidebarWidth, viewport.WorkSize.Y));
        ImGui.Begin("Simulation",
                    ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);

        if (ImGui.Button("<< Hide panel (Tab)", new System.Numerics.Vector2(-1, 0)))
            _sidebarOpen = false;
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("Selected body", ImGuiTreeNodeFlags.DefaultOpen))
            DrawSelectedSection();
        if (ImGui.CollapsingHeader("Global", ImGuiTreeNodeFlags.DefaultOpen))
            DrawGlobalSection();
        if (ImGui.CollapsingHeader("Spacetime grid", ImGuiTreeNodeFlags.DefaultOpen))
            DrawGridSection();
        if (ImGui.CollapsingHeader("Collision & debris", ImGuiTreeNodeFlags.DefaultOpen))
            DrawDebrisSection();
        if (ImGui.CollapsingHeader("Stats", ImGuiTreeNodeFlags.DefaultOpen))
            DrawStatsSection();

        ImGui.End();
    }

    private void DrawSelectedSection()
    {
        if (_selected == null)
        {
            ImGui.TextWrapped("Right-click a body in the viewport to select it. " +
                              "Left-drag the selected body to move it (Shift = horizontal plane). " +
                              "Middle-drag orbits the camera.");
            return;
        }
        Body b = _selected;

        string name = b.Name;
        if (ImGui.InputText("Name", ref name, 64)) b.Name = name;

        float mass = b.Mass;
        if (ImGui.SliderFloat("Mass", ref mass, 0.01f, 5000f, "%.2f", ImGuiSliderFlags.Logarithmic))
        {
            b.Mass = mass;
            b.UpdateRadius();
        }

        float density = b.Density;
        if (ImGui.SliderFloat("Density", ref density, 0.5f, 100f, "%.1f", ImGuiSliderFlags.Logarithmic))
        {
            b.Density = density;
            b.UpdateRadius();
        }
        ImGui.Text($"Radius (derived): {b.Radius:0.000}");

        var pos = ToSN(b.Position);
        if (ImGui.DragFloat3("Position", ref pos, 0.1f)) b.Position = ToTK(pos);

        var vel = ToSN(b.Velocity);
        if (ImGui.DragFloat3("Velocity", ref vel, 0.05f)) b.Velocity = ToTK(vel);
        ImGui.Text($"Speed: {b.Velocity.Length:0.000}");

        var color = ToSN(b.Color);
        if (ImGui.ColorEdit3("Color", ref color)) b.Color = ToTK(color);

        bool star = b.Emissive > 0.5f;
        if (ImGui.Checkbox("Star / Emissive", ref star)) b.Emissive = star ? 1f : 0f;
        ImGui.SameLine();
        bool anchored = b.Anchored;
        if (ImGui.Checkbox("Anchored", ref anchored))
        {
            b.Anchored = anchored;
            if (anchored) b.Velocity = Vector3.Zero;
        }

        bool trail = b.ShowTrail;
        if (ImGui.Checkbox("Show trail", ref trail))
        {
            b.ShowTrail = trail;
            if (!trail) b.Trail.Clear();
        }

        ImGui.Separator();
        ImGui.Text($"Kinetic energy: {b.KineticEnergy:0.00}");

        Body? primary = _physics.MostMassive(exclude: b);
        if (primary != null)
        {
            float dist = (b.Position - primary.Position).Length;
            float relSpeed = (b.Velocity - primary.Velocity).Length;
            float circular = _physics.CircularOrbitSpeed(primary.Mass, MathF.Max(dist, 0.001f));
            ImGui.Text($"Distance to {primary.Name}: {dist:0.00}");
            ImGui.Text($"Orbital speed: {relSpeed:0.00}  (circular: {circular:0.00})");
        }

        ImGui.Spacing();
        if (ImGui.Button("Delete body (Del)", new System.Numerics.Vector2(-1, 0)))
            DeleteSelected();
    }

    private void DrawGlobalSection()
    {
        if (ImGui.Button("Add Body")) AddBody();
        ImGui.SameLine();
        if (ImGui.Button($"Add random cluster ({_clusterSize})")) AddCluster(_clusterSize);
        ImGui.SliderInt("Cluster size", ref _clusterSize, 2, 40);

        ImGui.Separator();

        ImGui.SliderFloat("G", ref _physics.G, 0.01f, 20f, "%.3f", ImGuiSliderFlags.Logarithmic);
        ImGui.SliderFloat("Time step dt", ref _physics.Dt, 0.0005f, 0.02f, "%.4f", ImGuiSliderFlags.Logarithmic);
        if (ImGui.SliderFloat("Sim speed", ref _physics.TimeScale, 0.05f, 50f, "%.2fx", ImGuiSliderFlags.Logarithmic))
            UpdateTitle();
        ImGui.SliderFloat("Softening", ref _physics.Softening, 0.001f, 1f, "%.3f", ImGuiSliderFlags.Logarithmic);

        ImGui.Separator();

        bool paused = _physics.Paused;
        if (ImGui.Checkbox("Paused (Space)", ref paused)) { _physics.Paused = paused; UpdateTitle(); }
        ImGui.Checkbox("Grid (G)", ref _showGrid);
        ImGui.SameLine();
        if (ImGui.Checkbox("Trails", ref _showTrails) && !_showTrails)
            foreach (var body in _physics.Bodies) body.Trail.Clear();
        ImGui.SameLine();
        ImGui.Checkbox("Velocity arrows", ref _showVelocityArrows);

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
    }

    private void DrawGridSection()
    {
        // Extent/resolution rebuild GPU buffers — apply when the slider is released.
        ImGui.SliderFloat("Extent", ref _pendingGridSize, 40f, 400f, "%.0f");
        if (ImGui.IsItemDeactivatedAfterEdit())
            _grid.Rebuild(_pendingGridSize, _pendingGridRes);

        ImGui.SliderInt("Resolution", ref _pendingGridRes, GridMesh.MinResolution, GridMesh.MaxResolution);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _grid.Rebuild(_pendingGridSize, _pendingGridRes);

        ImGui.SliderFloat("Well strength", ref _grid.WellStrength, 0.001f, 0.06f, "%.4f",
                          ImGuiSliderFlags.Logarithmic);
        ImGui.SliderFloat("Max dip", ref _grid.MaxDip, 1f, 15f, "%.1f");
    }

    private void DrawDebrisSection()
    {
        var ps = _physics.Particles;

        if (!_physics.EnableCollisions)
            ImGui.TextWrapped("Tip: enable \"Collisions\" under Global for impacts to resolve.");

        ImGui.Checkbox("Fracture on impact", ref _physics.EnableFracture);
        ImGui.SameLine();
        if (ImGui.Button("Clear debris")) ps.Clear();

        ImGui.SliderFloat("Merge below Q", ref _physics.QMerge, 0.1f, 200f, "%.2f",
                          ImGuiSliderFlags.Logarithmic);
        ImGui.SliderFloat("Shatter above Q", ref _physics.QDisrupt, 0.1f, 500f, "%.2f",
                          ImGuiSliderFlags.Logarithmic);
        if (_physics.QDisrupt < _physics.QMerge) _physics.QDisrupt = _physics.QMerge;

        ImGui.SliderFloat("Detail (particles/mass)", ref ps.ParticlesPerUnitMass, 100f, 6000f, "%.0f");
        ImGui.SliderFloat("Cooling rate", ref ps.CoolRate, 0.05f, 3f, "%.2f");
        ImGui.SliderFloat("Particle life (s)", ref ps.BaseLife, 2f, 40f, "%.1f");

        ImGui.Separator();

        ImGui.SliderFloat("Particle size", ref _particleRenderer.ParticleRadius, 0.03f, 1.0f, "%.2f");
        ImGui.SliderFloat("Brightness", ref _particleRenderer.Brightness, 0.2f, 4f, "%.2f");
        ImGui.Checkbox("Bloom", ref _post.Enabled);
        ImGui.SliderFloat("Bloom strength", ref _post.BloomStrength, 0f, 3f, "%.2f");
        ImGui.SliderFloat("Bloom threshold", ref _post.BloomThreshold, 0.1f, 3f, "%.2f");

        ImGui.Separator();

        ImGui.SliderFloat("Target FPS", ref _targetFps, 30f, 240f, "%.0f");
        ImGui.SliderInt("Particle cap", ref _pendingParticleCap, 5000, 150000);
        if (ImGui.IsItemDeactivatedAfterEdit()) _physics.Particles.Allocate(_pendingParticleCap);

        ImGui.Text($"Particles: {ps.Count} / {ps.MaxParticles}");
        ImGui.Text($"Spawn scale: {ps.SpawnScale:0.00}x  (adaptive)");
    }

    private void DrawStatsSection()
    {
        float ke = _physics.TotalKineticEnergy();
        float pe = _physics.TotalPotentialEnergy();
        ImGui.Text($"Bodies: {_physics.Bodies.Count}");
        ImGui.Text($"Kinetic energy:   {ke:0.0}");
        ImGui.Text($"Potential energy: {pe:0.0}");
        ImGui.Text($"Total energy:     {ke + pe:0.0}");
        ImGui.Text($"|Momentum|: {_physics.TotalMomentum().Length:0.000}");
        ImGui.Text($"FPS: {ImGui.GetIO().Framerate:0.}  ({1000f / MathF.Max(ImGui.GetIO().Framerate, 0.001f):0.00} ms)");
    }

    private static System.Numerics.Vector3 ToSN(Vector3 v) => new(v.X, v.Y, v.Z);
    private static Vector3 ToTK(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    // ------------------------------------------------------------ events ----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _imgui.MouseScroll(e.Offset);
        if (!ImGui.GetIO().WantCaptureMouse)
            _camera.Zoom(e.OffsetY);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        _imgui.PressChar((char)e.Unicode);
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, e.Width, e.Height);
        _post?.Resize(e.Width, e.Height);
        _imgui.WindowResized(e.Width, e.Height);
    }

    private void UpdateTitle()
    {
        string state = _physics.Paused ? "PAUSED" : $"{_physics.TimeScale:0.0}x";
        Title = $"GravitySim — [{_currentPreset}] {ScenePresets.Name(_currentPreset)} | {state} " +
                "| RMB: select · LMB: drag · MMB: orbit · WASD: move · Q/E: down/up · F follow · Del delete · Space pause · G grid · 1/2/3 presets";
    }

    protected override void OnUnload()
    {
        _bodyShader.Dispose();
        _gridShader.Dispose();
        _trailShader.Dispose();
        _sphere.Dispose();
        _grid.Dispose();
        _lines.Dispose();
        _particleRenderer.Dispose();
        _post.Dispose();
        _imgui.Dispose();
        base.OnUnload();
    }
}
