using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// Uniform spatial hash for O(n) neighbour queries among particles. Cells are hashed
/// into a fixed table (Teschner et al.), so memory is O(n) regardless of how spread out
/// the cloud is. Rebuilt each solver substep via a counting sort; all buffers are reused.
///
/// Query a point's neighbourhood by iterating the 27 surrounding cells. Because distinct
/// cells can hash to the same bucket, callers should distance-check every candidate, and
/// <see cref="CollectBuckets"/> de-duplicates buckets so no candidate is visited twice.
/// </summary>
public class SpatialHash
{
    public int[] CellStart = new int[1]; // bucket -> first index into Entries (length TableSize+1)
    public int[] Entries = Array.Empty<int>();   // particle indices, grouped by bucket
    public float CellSize { get; private set; } = 1f;
    public int TableSize { get; private set; }

    private int[] _keys = Array.Empty<int>();    // per-particle bucket
    private int[] _cursor = new int[1];

    private const int PX = 92837111, PY = 689287499, PZ = 283923481;

    public void Build(Vector3[] pos, int count, float cellSize)
    {
        CellSize = MathF.Max(cellSize, 1e-4f);
        TableSize = NextPrime(Math.Max(16, count * 2));

        if (CellStart.Length < TableSize + 1) CellStart = new int[TableSize + 1];
        else Array.Clear(CellStart, 0, TableSize + 1);
        if (_cursor.Length < TableSize + 1) _cursor = new int[TableSize + 1];
        if (_keys.Length < count) _keys = new int[Math.Max(count, _keys.Length * 2)];
        if (Entries.Length < count) Entries = new int[Math.Max(count, Entries.Length * 2)];

        float inv = 1f / CellSize;

        // Count per bucket.
        for (int i = 0; i < count; i++)
        {
            int k = Bucket(
                (int)MathF.Floor(pos[i].X * inv),
                (int)MathF.Floor(pos[i].Y * inv),
                (int)MathF.Floor(pos[i].Z * inv));
            _keys[i] = k;
            CellStart[k]++;
        }

        // Prefix sum -> bucket start offsets.
        int sum = 0;
        for (int b = 0; b <= TableSize; b++) { int c = CellStart[b]; CellStart[b] = sum; sum += c; }

        // Scatter indices into Entries grouped by bucket.
        Array.Copy(CellStart, _cursor, TableSize + 1);
        for (int i = 0; i < count; i++) Entries[_cursor[_keys[i]]++] = i;
    }

    public int Bucket(int ix, int iy, int iz)
    {
        int h = (ix * PX) ^ (iy * PY) ^ (iz * PZ);
        int m = h % TableSize;
        return m < 0 ? m + TableSize : m;
    }

    /// <summary>
    /// Fill <paramref name="buckets"/> with the de-duplicated buckets of the 27 cells
    /// around <paramref name="p"/>; returns how many were written (≤ 27).
    /// </summary>
    public int CollectBuckets(Vector3 p, Span<int> buckets)
    {
        float inv = 1f / CellSize;
        int cx = (int)MathF.Floor(p.X * inv);
        int cy = (int)MathF.Floor(p.Y * inv);
        int cz = (int)MathF.Floor(p.Z * inv);

        int n = 0;
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int b = Bucket(cx + dx, cy + dy, cz + dz);
            bool seen = false;
            for (int q = 0; q < n; q++) if (buckets[q] == b) { seen = true; break; }
            if (!seen) buckets[n++] = b;
        }
        return n;
    }

    private static int NextPrime(int n)
    {
        if (n < 2) return 2;
        if ((n & 1) == 0) n++;
        while (!IsPrime(n)) n += 2;
        return n;
    }

    private static bool IsPrime(int n)
    {
        if (n < 2) return false;
        if (n % 2 == 0) return n == 2;
        for (int i = 3; (long)i * i <= n; i += 2)
            if (n % i == 0) return false;
        return true;
    }
}
