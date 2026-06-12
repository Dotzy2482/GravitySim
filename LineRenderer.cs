using OpenTK.Graphics.OpenGL4;

namespace GravitySim;

/// <summary>
/// A single streaming VBO for line-based effects (orbit trails, velocity arrows).
/// Vertex layout: vec3 position + float fade (0..1, multiplies alpha in the shader).
/// </summary>
public class LineRenderer : IDisposable
{
    private readonly int _vao, _vbo;
    private int _capacityBytes;

    public LineRenderer()
    {
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        _capacityBytes = 4096 * 4 * sizeof(float);
        GL.BufferData(BufferTarget.ArrayBuffer, _capacityBytes, IntPtr.Zero, BufferUsageHint.StreamDraw);

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, 4 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        GL.BindVertexArray(0);
    }

    /// <summary>Upload <paramref name="vertexCount"/> vertices from data and draw them.</summary>
    public void Draw(float[] data, int vertexCount, PrimitiveType primitive)
    {
        if (vertexCount < 2) return;

        int bytes = vertexCount * 4 * sizeof(float);
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        if (bytes > _capacityBytes)
        {
            _capacityBytes = bytes * 2;
            GL.BufferData(BufferTarget.ArrayBuffer, _capacityBytes, IntPtr.Zero, BufferUsageHint.StreamDraw);
        }
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, bytes, data);
        GL.DrawArrays(primitive, 0, vertexCount);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        GL.DeleteBuffer(_vbo);
        GL.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
