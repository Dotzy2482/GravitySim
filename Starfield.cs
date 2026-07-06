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
