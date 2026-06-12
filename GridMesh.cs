using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// The "rubber sheet": a flat grid of vertices on the XZ plane whose Y values are
/// displaced downward each frame by the summed gravitational potential of all
/// bodies. Rendered as a semi-transparent wireframe of line segments.
/// Extent and resolution can be rebuilt at runtime (see <see cref="Rebuild"/>).
/// </summary>
public class GridMesh : IDisposable
{
    /// <summary>Upper bound on vertices per side; 300² ≈ 90k potential samples per frame.</summary>
    public const int MaxResolution = 300;
    public const int MinResolution = 20;

    private readonly int _vao, _vbo, _ebo;
    private int _indexCount;
    private float[] _vertices = Array.Empty<float>();

    public float Size { get; private set; }
    public int Resolution { get; private set; }

    /// <summary>Height of the resting (undisturbed) sheet.</summary>
    public float BaseHeight = -2.5f;
    /// <summary>Scales potential into vertical displacement. Bigger = deeper wells.</summary>
    public float WellStrength = 0.012f;
    /// <summary>Soft cap so a close pass doesn't punch an infinitely deep funnel.</summary>
    public float MaxDip = 7.0f;

    public GridMesh(float size = 175f, int resolution = 220)
    {
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        _vbo = GL.GenBuffer();
        _ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);

        Rebuild(size, resolution);
    }

    /// <summary>Reallocate vertex/index buffers for a new extent or density.</summary>
    public void Rebuild(float size, int resolution)
    {
        Size = MathF.Max(size, 1f);
        Resolution = Math.Clamp(resolution, MinResolution, MaxResolution);

        _vertices = new float[Resolution * Resolution * 3];

        // Static index buffer: horizontal + vertical line segments between neighbors.
        var indices = new List<uint>(Resolution * Resolution * 4);
        for (int z = 0; z < Resolution; z++)
        {
            for (int x = 0; x < Resolution; x++)
            {
                uint i = (uint)(z * Resolution + x);
                if (x < Resolution - 1) { indices.Add(i); indices.Add(i + 1); }
                if (z < Resolution - 1) { indices.Add(i); indices.Add(i + (uint)Resolution); }
            }
        }
        _indexCount = indices.Count;

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertices.Length * sizeof(float),
                      IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Count * sizeof(uint),
                      indices.ToArray(), BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);
    }

    /// <summary>Recompute every vertex's Y from the current body positions and upload.</summary>
    public void Update(PhysicsEngine physics)
    {
        float step = Size / (Resolution - 1);
        float half = Size * 0.5f;

        int v = 0;
        for (int zi = 0; zi < Resolution; zi++)
        {
            float z = -half + zi * step;
            for (int xi = 0; xi < Resolution; xi++)
            {
                float x = -half + xi * step;

                // Sample potential on the sheet's resting plane; Φ is negative,
                // so the dip = -Φ · strength pulls vertices downward. tanh gives
                // a soft cap: wells round off at MaxDip instead of clipping flat.
                float phi = physics.PotentialAt(new Vector3(x, BaseHeight, z));
                float dip = MaxDip * MathF.Tanh(-phi * WellStrength / MaxDip);

                _vertices[v++] = x;
                _vertices[v++] = BaseHeight - dip;
                _vertices[v++] = z;
            }
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero,
                         _vertices.Length * sizeof(float), _vertices);
    }

    public void Draw()
    {
        GL.BindVertexArray(_vao);
        GL.DrawElements(PrimitiveType.Lines, _indexCount, DrawElementsType.UnsignedInt, 0);
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
