using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// HDR off-screen rendering with threshold/blur bloom and FXAA.
///
/// Usage per frame:
///   <c>BeginScene()</c> → draw the 3-D scene (bodies, grid, trails, particles) →
///   <c>Composite()</c>. Composite runs the post chain and leaves the default
///   framebuffer bound (depth test off) so ImGui can draw on top in LDR.
///
/// When <see cref="Enabled"/> is false the scene is drawn straight to the screen and
/// Composite() is a no-op (cheap fallback / debugging).
/// </summary>
public class PostProcess : IDisposable
{
    private int _w, _h, _bw, _bh;

    private int _sceneFbo, _sceneColor, _sceneDepth;
    private int _ldrFbo, _ldrColor;
    private readonly int _bloomFbo = GL.GenFramebuffer();
    private readonly int[] _bloomTex = new int[2];

    private readonly Shader _bright, _blur, _composite, _fxaa;
    private readonly int _emptyVao = GL.GenVertexArray();

    public bool Enabled = true;
    public float BloomThreshold = 1.15f;
    public float BloomStrength = 0.55f;
    public float Exposure = 1.1f;
    public int BlurPasses = 6;   // total horizontal + vertical half-passes

    public PostProcess(int width, int height)
    {
        _bright    = new Shader("Shaders/fullscreen.vert", "Shaders/brightpass.frag");
        _blur      = new Shader("Shaders/fullscreen.vert", "Shaders/blur.frag");
        _composite = new Shader("Shaders/fullscreen.vert", "Shaders/composite.frag");
        _fxaa      = new Shader("Shaders/fullscreen.vert", "Shaders/fxaa.frag");
        Resize(width, height);
    }

    public void Resize(int width, int height)
    {
        _w = Math.Max(1, width);
        _h = Math.Max(1, height);
        _bw = Math.Max(1, _w / 2);
        _bh = Math.Max(1, _h / 2);

        DeleteTargets();

        // HDR scene target + depth.
        _sceneColor = NewColorTex(_w, _h, PixelInternalFormat.Rgba16f);
        _sceneDepth = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _sceneDepth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, _w, _h);
        _sceneFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                                TextureTarget.Texture2D, _sceneColor, 0);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                                   RenderbufferTarget.Renderbuffer, _sceneDepth);
        CheckFbo("scene");

        // Bloom ping-pong targets (half res, no depth).
        _bloomTex[0] = NewColorTex(_bw, _bh, PixelInternalFormat.Rgba16f);
        _bloomTex[1] = NewColorTex(_bw, _bh, PixelInternalFormat.Rgba16f);

        // LDR target that FXAA reads from.
        _ldrColor = NewColorTex(_w, _h, PixelInternalFormat.Rgba8);
        _ldrFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _ldrFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                                TextureTarget.Texture2D, _ldrColor, 0);
        CheckFbo("ldr");

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>Bind the HDR target (or the screen if disabled) and clear it.</summary>
    public void BeginScene()
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, Enabled ? _sceneFbo : 0);
        GL.Viewport(0, 0, _w, _h);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    /// <summary>Run bright-pass → blur → composite → FXAA; leaves framebuffer 0 bound.</summary>
    public void Composite()
    {
        if (!Enabled) return;

        GL.Disable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.Disable(EnableCap.Blend);
        GL.BindVertexArray(_emptyVao);

        // 1) Bright pass: scene → bloomTex[0] (half res).
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _bloomFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                                TextureTarget.Texture2D, _bloomTex[0], 0);
        GL.Viewport(0, 0, _bw, _bh);
        _bright.Use();
        _bright.SetFloat("uThreshold", BloomThreshold);
        BindTex(0, _sceneColor, _bright, "uScene");
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // 2) Separable Gaussian blur, ping-ponging between the two bloom textures.
        _blur.Use();
        int passes = Math.Max(2, BlurPasses);
        int lastDst = 0;
        bool horizontal = true;
        for (int i = 0; i < passes; i++)
        {
            int src = (i == 0) ? 0 : lastDst;
            int dst = 1 - src;
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                                    TextureTarget.Texture2D, _bloomTex[dst], 0);
            _blur.SetVector2("uTexel", horizontal ? new Vector2(1f / _bw, 0f)
                                                  : new Vector2(0f, 1f / _bh));
            BindTex(0, _bloomTex[src], _blur, "uImage");
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            lastDst = dst;
            horizontal = !horizontal;
        }

        // 3) Composite scene + bloom → LDR full-res target.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _ldrFbo);
        GL.Viewport(0, 0, _w, _h);
        _composite.Use();
        _composite.SetFloat("uBloomStrength", BloomStrength);
        _composite.SetFloat("uExposure", Exposure);
        BindTex(0, _sceneColor, _composite, "uScene");
        BindTex(1, _bloomTex[lastDst], _composite, "uBloom");
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // 4) FXAA → screen.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, _w, _h);
        _fxaa.Use();
        _fxaa.SetVector2("uTexel", new Vector2(1f / _w, 1f / _h));
        BindTex(0, _ldrColor, _fxaa, "uImage");
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

        GL.BindVertexArray(0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Disable(EnableCap.DepthTest); // ImGui draws unconditionally on top
    }

    private static int NewColorTex(int w, int h, PixelInternalFormat fmt)
    {
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, fmt, w, h, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return tex;
    }

    private static void BindTex(int unit, int tex, Shader shader, string name)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, tex);
        shader.SetInt(name, unit);
    }

    private static void CheckFbo(string name)
    {
        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new Exception($"Framebuffer '{name}' incomplete: {status}");
    }

    private void DeleteTargets()
    {
        if (_sceneColor != 0) GL.DeleteTexture(_sceneColor);
        if (_sceneDepth != 0) GL.DeleteRenderbuffer(_sceneDepth);
        if (_sceneFbo != 0) GL.DeleteFramebuffer(_sceneFbo);
        if (_ldrColor != 0) GL.DeleteTexture(_ldrColor);
        if (_ldrFbo != 0) GL.DeleteFramebuffer(_ldrFbo);
        if (_bloomTex[0] != 0) GL.DeleteTexture(_bloomTex[0]);
        if (_bloomTex[1] != 0) GL.DeleteTexture(_bloomTex[1]);
    }

    public void Dispose()
    {
        DeleteTargets();
        GL.DeleteFramebuffer(_bloomFbo);
        GL.DeleteVertexArray(_emptyVao);
        _bright.Dispose();
        _blur.Dispose();
        _composite.Dispose();
        _fxaa.Dispose();
        GC.SuppressFinalize(this);
    }
}
