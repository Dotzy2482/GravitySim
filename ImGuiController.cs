using System.Runtime.CompilerServices;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace GravitySim;

/// <summary>
/// Dear ImGui backend for OpenTK 4.x / OpenGL 3.3 core.
/// Owns the font atlas texture, a streaming VBO/EBO for draw lists, and the
/// translation of OpenTK input (mouse, keys, text, scroll) into ImGui events.
/// Usage per frame: Update(window, dt) → build UI → Render().
/// </summary>
public class ImGuiController : IDisposable
{
    private bool _frameBegun;
    private int _vao, _vbo, _ebo;
    private int _vboSize = 10_000, _eboSize = 2_000;
    private int _fontTexture;
    private int _program;
    private int _locProjection, _locTexture;
    private int _windowWidth, _windowHeight;
    private Vector2 _scroll;
    private readonly List<char> _typedChars = new();

    private static readonly Keys[] AllKeys =
        Enum.GetValues<Keys>().Distinct().Where(k => k != Keys.Unknown).ToArray();

    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in vec2 aUV;
        layout(location = 2) in vec4 aColor;
        uniform mat4 uProjection;
        out vec2 vUV;
        out vec4 vColor;
        void main()
        {
            vUV = aUV;
            vColor = aColor;
            gl_Position = uProjection * vec4(aPosition, 0.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec2 vUV;
        in vec4 vColor;
        uniform sampler2D uTexture;
        out vec4 FragColor;
        void main() { FragColor = vColor * texture(uTexture, vUV); }
        """;

    public ImGuiController(int width, int height)
    {
        _windowWidth = width;
        _windowHeight = height;

        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.Fonts.AddFontDefault();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;

        CreateDeviceResources();

        SetPerFrameData(1f / 60f);
        ImGui.NewFrame();
        _frameBegun = true;
    }

    private void CreateDeviceResources()
    {
        _program = GL.CreateProgram();
        int vert = CompileShader(ShaderType.VertexShader, VertexSource);
        int frag = CompileShader(ShaderType.FragmentShader, FragmentSource);
        GL.AttachShader(_program, vert);
        GL.AttachShader(_program, frag);
        GL.LinkProgram(_program);
        GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0) throw new Exception($"ImGui shader link error: {GL.GetProgramInfoLog(_program)}");
        GL.DeleteShader(vert);
        GL.DeleteShader(frag);
        _locProjection = GL.GetUniformLocation(_program, "uProjection");
        _locTexture = GL.GetUniformLocation(_program, "uTexture");

        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _vboSize, IntPtr.Zero, BufferUsageHint.StreamDraw);

        _ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, _eboSize, IntPtr.Zero, BufferUsageHint.StreamDraw);

        int stride = Unsafe.SizeOf<ImDrawVert>(); // pos(8) + uv(8) + col(4) = 20
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);
        GL.EnableVertexAttribArray(2);

        GL.BindVertexArray(0);

        RecreateFontTexture();
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) throw new Exception($"ImGui {type} compile error: {GL.GetShaderInfoLog(shader)}");
        return shader;
    }

    private void RecreateFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);

        _fontTexture = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

        io.Fonts.SetTexID(_fontTexture);
        io.Fonts.ClearTexData();
    }

    public void WindowResized(int width, int height)
    {
        _windowWidth = width;
        _windowHeight = height;
    }

    /// <summary>Queue a character typed via the window's TextInput event.</summary>
    public void PressChar(char c) => _typedChars.Add(c);

    /// <summary>Queue scroll from the window's MouseWheel event.</summary>
    public void MouseScroll(Vector2 offset) => _scroll += offset;

    /// <summary>Feed input and begin a new ImGui frame. Call once per rendered frame.</summary>
    public void Update(GameWindow window, float deltaSeconds)
    {
        if (_frameBegun) ImGui.Render(); // discard a frame that was never drawn

        SetPerFrameData(deltaSeconds);
        UpdateInput(window);

        _frameBegun = true;
        ImGui.NewFrame();
    }

    private void SetPerFrameData(float deltaSeconds)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new System.Numerics.Vector2(_windowWidth, _windowHeight);
        io.DisplayFramebufferScale = System.Numerics.Vector2.One;
        io.DeltaTime = deltaSeconds > 0 ? deltaSeconds : 1f / 60f;
    }

    private void UpdateInput(GameWindow window)
    {
        var io = ImGui.GetIO();
        var mouse = window.MouseState;
        var kb = window.KeyboardState;

        io.AddMousePosEvent(mouse.X, mouse.Y);
        io.AddMouseButtonEvent(0, mouse[MouseButton.Left]);
        io.AddMouseButtonEvent(1, mouse[MouseButton.Right]);
        io.AddMouseButtonEvent(2, mouse[MouseButton.Middle]);
        io.AddMouseWheelEvent(_scroll.X, _scroll.Y);
        _scroll = Vector2.Zero;

        io.AddKeyEvent(ImGuiKey.ModCtrl, kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl));
        io.AddKeyEvent(ImGuiKey.ModShift, kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift));
        io.AddKeyEvent(ImGuiKey.ModAlt, kb.IsKeyDown(Keys.LeftAlt) || kb.IsKeyDown(Keys.RightAlt));
        io.AddKeyEvent(ImGuiKey.ModSuper, kb.IsKeyDown(Keys.LeftSuper) || kb.IsKeyDown(Keys.RightSuper));

        foreach (var key in AllKeys)
        {
            var imKey = TranslateKey(key);
            if (imKey != ImGuiKey.None)
                io.AddKeyEvent(imKey, kb.IsKeyDown(key));
        }

        foreach (char c in _typedChars) io.AddInputCharacter(c);
        _typedChars.Clear();
    }

    /// <summary>End the frame and draw the UI. Call after all ImGui.* widget calls.</summary>
    public void Render()
    {
        if (!_frameBegun) return;
        _frameBegun = false;
        ImGui.Render();
        RenderImDrawData(ImGui.GetDrawData());
    }

    private void RenderImDrawData(ImDrawDataPtr drawData)
    {
        if (drawData.CmdListsCount == 0) return;

        GL.Enable(EnableCap.Blend);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha,
                             BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ScissorTest);

        GL.UseProgram(_program);
        var projection = Matrix4.CreateOrthographicOffCenter(
            0f, drawData.DisplaySize.X, drawData.DisplaySize.Y, 0f, -1f, 1f);
        GL.UniformMatrix4(_locProjection, false, ref projection);
        GL.Uniform1(_locTexture, 0);
        GL.BindVertexArray(_vao);
        GL.ActiveTexture(TextureUnit.Texture0);

        drawData.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];

            int vtxBytes = cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            if (vtxBytes > _vboSize)
            {
                _vboSize = (int)(vtxBytes * 1.5f);
                GL.BufferData(BufferTarget.ArrayBuffer, _vboSize, IntPtr.Zero, BufferUsageHint.StreamDraw);
            }
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vtxBytes, cmdList.VtxBuffer.Data);

            int idxBytes = cmdList.IdxBuffer.Size * sizeof(ushort);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            if (idxBytes > _eboSize)
            {
                _eboSize = (int)(idxBytes * 1.5f);
                GL.BufferData(BufferTarget.ElementArrayBuffer, _eboSize, IntPtr.Zero, BufferUsageHint.StreamDraw);
            }
            GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, idxBytes, cmdList.IdxBuffer.Data);

            for (int i = 0; i < cmdList.CmdBuffer.Size; i++)
            {
                var cmd = cmdList.CmdBuffer[i];
                if (cmd.UserCallback != IntPtr.Zero) continue;

                GL.BindTexture(TextureTarget.Texture2D, (int)cmd.TextureId);

                // ImGui clip rects are top-left origin; GL scissor is bottom-left.
                var clip = cmd.ClipRect;
                GL.Scissor((int)clip.X, _windowHeight - (int)clip.W,
                           (int)(clip.Z - clip.X), (int)(clip.W - clip.Y));

                GL.DrawElementsBaseVertex(PrimitiveType.Triangles, (int)cmd.ElemCount,
                                          DrawElementsType.UnsignedShort,
                                          (IntPtr)(cmd.IdxOffset * sizeof(ushort)),
                                          (int)cmd.VtxOffset);
            }
        }

        // Restore the state the 3D scene expects.
        GL.Disable(EnableCap.ScissorTest);
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    private static ImGuiKey TranslateKey(Keys key)
    {
        if (key >= Keys.D0 && key <= Keys.D9) return ImGuiKey._0 + (key - Keys.D0);
        if (key >= Keys.A && key <= Keys.Z) return ImGuiKey.A + (key - Keys.A);
        if (key >= Keys.KeyPad0 && key <= Keys.KeyPad9) return ImGuiKey.Keypad0 + (key - Keys.KeyPad0);
        if (key >= Keys.F1 && key <= Keys.F12) return ImGuiKey.F1 + (key - Keys.F1);

        return key switch
        {
            Keys.Tab => ImGuiKey.Tab,
            Keys.Left => ImGuiKey.LeftArrow,
            Keys.Right => ImGuiKey.RightArrow,
            Keys.Up => ImGuiKey.UpArrow,
            Keys.Down => ImGuiKey.DownArrow,
            Keys.PageUp => ImGuiKey.PageUp,
            Keys.PageDown => ImGuiKey.PageDown,
            Keys.Home => ImGuiKey.Home,
            Keys.End => ImGuiKey.End,
            Keys.Insert => ImGuiKey.Insert,
            Keys.Delete => ImGuiKey.Delete,
            Keys.Backspace => ImGuiKey.Backspace,
            Keys.Space => ImGuiKey.Space,
            Keys.Enter => ImGuiKey.Enter,
            Keys.Escape => ImGuiKey.Escape,
            Keys.Apostrophe => ImGuiKey.Apostrophe,
            Keys.Comma => ImGuiKey.Comma,
            Keys.Minus => ImGuiKey.Minus,
            Keys.Period => ImGuiKey.Period,
            Keys.Slash => ImGuiKey.Slash,
            Keys.Semicolon => ImGuiKey.Semicolon,
            Keys.Equal => ImGuiKey.Equal,
            Keys.LeftBracket => ImGuiKey.LeftBracket,
            Keys.Backslash => ImGuiKey.Backslash,
            Keys.RightBracket => ImGuiKey.RightBracket,
            Keys.GraveAccent => ImGuiKey.GraveAccent,
            Keys.CapsLock => ImGuiKey.CapsLock,
            Keys.ScrollLock => ImGuiKey.ScrollLock,
            Keys.NumLock => ImGuiKey.NumLock,
            Keys.PrintScreen => ImGuiKey.PrintScreen,
            Keys.Pause => ImGuiKey.Pause,
            Keys.KeyPadDecimal => ImGuiKey.KeypadDecimal,
            Keys.KeyPadDivide => ImGuiKey.KeypadDivide,
            Keys.KeyPadMultiply => ImGuiKey.KeypadMultiply,
            Keys.KeyPadSubtract => ImGuiKey.KeypadSubtract,
            Keys.KeyPadAdd => ImGuiKey.KeypadAdd,
            Keys.KeyPadEnter => ImGuiKey.KeypadEnter,
            Keys.KeyPadEqual => ImGuiKey.KeypadEqual,
            Keys.LeftShift => ImGuiKey.LeftShift,
            Keys.LeftControl => ImGuiKey.LeftCtrl,
            Keys.LeftAlt => ImGuiKey.LeftAlt,
            Keys.LeftSuper => ImGuiKey.LeftSuper,
            Keys.RightShift => ImGuiKey.RightShift,
            Keys.RightControl => ImGuiKey.RightCtrl,
            Keys.RightAlt => ImGuiKey.RightAlt,
            Keys.RightSuper => ImGuiKey.RightSuper,
            Keys.Menu => ImGuiKey.Menu,
            _ => ImGuiKey.None,
        };
    }

    public void Dispose()
    {
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ebo);
        GL.DeleteVertexArray(_vao);
        GL.DeleteTexture(_fontTexture);
        GL.DeleteProgram(_program);
        ImGui.DestroyContext();
        GC.SuppressFinalize(this);
    }
}
