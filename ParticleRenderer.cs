using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// Additive point-sprite renderer for debris particles. Reads a <see cref="ParticleSystem"/>
/// each frame, packs it into a streaming VBO, and draws GL_POINTS with program point size.
/// Vertex layout: position(3) + temperature(1) + alpha(1) + color(3) = 8 floats.
/// </summary>
public class ParticleRenderer : IDisposable
{
    private const int FloatsPerParticle = 9;

    private readonly int _vao, _vbo;
    private readonly Shader _shader;
    private float[] _scratch = Array.Empty<float>();
    private int _capacityBytes;

    /// <summary>World-space sprite radius (UI-tunable).</summary>
    public float ParticleRadius = 0.5f;
    /// <summary>Emissive multiplier feeding the bloom (UI-tunable).</summary>
    public float Brightness = 1.4f;
    /// <summary>Metaball smoothing: enlarge/soften crowded particles into a connected blob.</summary>
    public bool Smooth = true;

    public ParticleRenderer()
    {
        _shader = new Shader("Shaders/particle.vert", "Shaders/particle.frag");

        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);
        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

        int stride = FloatsPerParticle * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, 4 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, 5 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
        GL.EnableVertexAttribArray(4);

        GL.BindVertexArray(0);
    }

    public void Draw(ParticleSystem ps, Matrix4 view, Matrix4 projection, int viewportHeight)
    {
        int n = ps.Count;
        if (n == 0) return;

        int floats = n * FloatsPerParticle;
        if (_scratch.Length < floats)
            _scratch = new float[Math.Max(floats, _scratch.Length * 2)];

        for (int i = 0; i < n; i++)
        {
            float life = ps.Life[i] <= 0f ? 1f : ps.Life[i];
            float t = ps.Age[i] / life;
            float alpha = MathHelper.Clamp(1f - (t - 0.6f) / 0.4f, 0f, 1f); // fade over last 40%

            int o = i * FloatsPerParticle;
            _scratch[o + 0] = ps.Pos[i].X;
            _scratch[o + 1] = ps.Pos[i].Y;
            _scratch[o + 2] = ps.Pos[i].Z;
            _scratch[o + 3] = ps.Temp[i];
            _scratch[o + 4] = alpha;
            _scratch[o + 5] = ps.Color[i].X;
            _scratch[o + 6] = ps.Color[i].Y;
            _scratch[o + 7] = ps.Color[i].Z;
            _scratch[o + 8] = ps.Crowd[i];
        }

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        int bytes = floats * sizeof(float);
        if (bytes > _capacityBytes)
        {
            _capacityBytes = Math.Max(bytes, _capacityBytes * 2);
            GL.BufferData(BufferTarget.ArrayBuffer, _capacityBytes, IntPtr.Zero, BufferUsageHint.StreamDraw);
        }
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, bytes, _scratch);

        _shader.Use();
        _shader.SetMatrix4("uView", view);
        _shader.SetMatrix4("uProjection", projection);
        _shader.SetFloat("uViewportHeight", viewportHeight);
        _shader.SetFloat("uParticleRadius", ParticleRadius);
        _shader.SetFloat("uBrightness", Brightness);
        _shader.SetFloat("uSmooth", Smooth ? 1f : 0f);

        GL.Enable(EnableCap.ProgramPointSize);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); // additive glow
        GL.DepthMask(false); // test against the scene, but don't occlude each other

        GL.DrawArrays(PrimitiveType.Points, 0, n);

        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        GL.DeleteBuffer(_vbo);
        GL.DeleteVertexArray(_vao);
        _shader.Dispose();
        GC.SuppressFinalize(this);
    }
}
