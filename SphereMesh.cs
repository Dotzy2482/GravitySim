using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// Procedural unit UV-sphere (radius 1), generated once and drawn for every body
/// with a per-body model matrix (translate + uniform scale by radius).
/// Vertex layout: position (vec3) + normal (vec3); for a unit sphere they coincide.
/// </summary>
public class SphereMesh : IDisposable
{
    private readonly int _vao, _vbo, _ebo;
    private readonly int _indexCount;

    public SphereMesh(int latSegments = 24, int lonSegments = 48)
    {
        var vertices = new List<float>();
        var indices = new List<uint>();

        for (int lat = 0; lat <= latSegments; lat++)
        {
            float theta = MathF.PI * lat / latSegments;            // 0..π (pole to pole)
            float sinT = MathF.Sin(theta), cosT = MathF.Cos(theta);

            for (int lon = 0; lon <= lonSegments; lon++)
            {
                float phi = 2f * MathF.PI * lon / lonSegments;     // 0..2π around
                var p = new Vector3(sinT * MathF.Cos(phi), cosT, sinT * MathF.Sin(phi));
                vertices.Add(p.X); vertices.Add(p.Y); vertices.Add(p.Z); // position
                vertices.Add(p.X); vertices.Add(p.Y); vertices.Add(p.Z); // normal
            }
        }

        int stride = lonSegments + 1;
        for (int lat = 0; lat < latSegments; lat++)
        {
            for (int lon = 0; lon < lonSegments; lon++)
            {
                uint a = (uint)(lat * stride + lon);
                uint b = (uint)((lat + 1) * stride + lon);
                indices.Add(a); indices.Add(b); indices.Add(a + 1);
                indices.Add(a + 1); indices.Add(b); indices.Add(b + 1);
            }
        }
        _indexCount = indices.Count;

        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Count * sizeof(float),
                      vertices.ToArray(), BufferUsageHint.StaticDraw);

        _ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Count * sizeof(uint),
                      indices.ToArray(), BufferUsageHint.StaticDraw);

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        GL.BindVertexArray(0);
    }

    public void Draw()
    {
        GL.BindVertexArray(_vao);
        GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ebo);
        GL.DeleteVertexArray(_vao);
        GC.SuppressFinalize(this);
    }
}
