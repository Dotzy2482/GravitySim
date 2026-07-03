using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// CPU debris-particle pool.
///
/// Pure data/simulation — owns no GL state; <see cref="ParticleRenderer"/> reads the
/// arrays each frame to upload them. Particles feel gravity from the massive bodies as a
/// one-way external field (they never pull back on the bodies), are absorbed on contact,
/// and cool over time.
///
/// v1: ballistic motion + heat. v2 (<see cref="EnableSph"/>): soft-particle SPH —
/// neighbour repulsion + cohesion + XSPH viscosity over a <see cref="SpatialHash"/>, so
/// debris behaves like a sticky, splashy liquid that forms blobs. v3: heat diffuses
/// between neighbours, and settled, cooled clusters re-coalesce back into solid bodies
/// (<see cref="ExtractCoalesced"/>) so long-running cost stays bounded.
///
/// Storage is structure-of-arrays packed into [0..Count); dead particles are removed by
/// swapping the last live particle into the gap, keeping the buffer dense.
/// </summary>
public class ParticleSystem
{
    public Vector3[] Pos = Array.Empty<Vector3>();
    public Vector3[] Vel = Array.Empty<Vector3>();
    public Vector3[] Color = Array.Empty<Vector3>();
    public float[] Temp = Array.Empty<float>();   // 0 cold, ~1 orange, >1.2 white-hot (HDR)
    public float[] Mass = Array.Empty<float>();
    public float[] Age = Array.Empty<float>();
    public float[] Life = Array.Empty<float>();
    public float[] Crowd = Array.Empty<float>();  // 0..1 local neighbour density (for the blob look)
    public int Count { get; private set; }

    public int MaxParticles { get; private set; }

    // --- general tunables (surfaced in the UI) ---
    public float ParticlesPerUnitMass = 500f;    // resolution of a disruption
    public int MaxPerDisruption = 6000;          // hard cap on particles spawned per impact
    public float BaseLife = 16f;                 // seconds before a particle ages out
    public float CoolRate = 0.55f;               // exponential cooling per second
    public float AmbientTemp = 0f;
    public float SpawnScale = 1f;                // adaptive-budget multiplier [0.1..1]
    public float ParticleSoftening = 0.5f;       // softens body gravity near centres
    public bool AccreteOnContact = true;

    // --- v2 fluid cohesion (weakly-compressible SPH) ---
    public bool EnableSph = true;
    public float SphRadius = 0.9f;     // neighbour interaction radius h
    public float RestDist = 0.42f;     // rest spacing r0 (sets rest density; h > r0)
    public float Repulsion = 12f;      // pressure stiffness when compressed (resists collapse)
    public float Cohesion = 8f;        // pressure stiffness when sparse (pulls the blob together)
    public float Viscosity = 0.5f;     // XSPH velocity smoothing [0..1]
    public float MaxSpeed = 90f;       // hard velocity clamp for stability

    // --- v3 heat diffusion ---
    public float HeatDiffuse = 2.0f;   // how fast temperature equalises between neighbours

    // --- v3 re-coalescence ---
    public bool EnableCoalesce = true;
    public int CoalesceMinCluster = 450;     // a cluster smaller than this is left as debris
    public float CoalesceMaxTemp = 0.16f;    // must have cooled below this
    public float CoalesceMaxDispersion = 0.7f; // and settled (low velocity spread) to reform
    public float CoalesceInterval = 0.8f;    // seconds between (cheap) coalescence scans

    private readonly Random _rng = new();
    private readonly SpatialHash _hash = new();

    // Per-substep scratch (sized with the pool).
    private Vector3[] _acc = Array.Empty<Vector3>();
    private Vector3[] _vxsph = Array.Empty<Vector3>();
    private float[] _tdiff = Array.Empty<float>();
    private float[] _density = Array.Empty<float>();
    private float _coalesceTimer;

    public ParticleSystem(int maxParticles = 50_000) => Allocate(maxParticles);

    /// <summary>(Re)allocate the pool to a new hard cap. Clears all live particles.</summary>
    public void Allocate(int maxParticles)
    {
        MaxParticles = Math.Max(1024, maxParticles);
        Pos = new Vector3[MaxParticles];
        Vel = new Vector3[MaxParticles];
        Color = new Vector3[MaxParticles];
        Temp = new float[MaxParticles];
        Mass = new float[MaxParticles];
        Age = new float[MaxParticles];
        Life = new float[MaxParticles];
        Crowd = new float[MaxParticles];
        _acc = new Vector3[MaxParticles];
        _vxsph = new Vector3[MaxParticles];
        _tdiff = new float[MaxParticles];
        _density = new float[MaxParticles];
        Count = 0;
    }

    public void Clear() => Count = 0;

    public int Remaining => MaxParticles - Count;

    /// <summary>Suggested particle budget for disrupting a body of the given mass.</summary>
    public int SuggestCount(float mass)
    {
        int n = (int)MathF.Round(ParticlesPerUnitMass * MathF.Sqrt(MathF.Max(mass, 0f)));
        return Math.Clamp(n, 200, Math.Min(MaxParticles, MaxPerDisruption));
    }

    /// <summary>
    /// Convert a chunk of a body into hot ejecta. Particles fly outward from the impact
    /// point (fastest/hottest nearest contact). With SPH on they fill a blob sized so the
    /// spawn spacing matches the rest spacing (no first-frame explosion). The batch's net
    /// <em>added</em> momentum is zeroed so the source's bulk momentum is preserved.
    /// </summary>
    public void SpawnDebris(Vector3 sourceCenter, float sourceRadius, Vector3 bulkVel,
                            Vector3 color, float totalMass, Vector3 contactPoint,
                            float impactSpeed, float heat, int requested)
    {
        requested = (int)MathF.Round(requested * SpawnScale);
        requested = Math.Clamp(requested, 0, Remaining);
        if (requested <= 0 || totalMass <= 0f) return;

        // Fill a sphere whose radius gives ~RestDist spacing for SPH stability.
        float fillRadius = sourceRadius;
        if (EnableSph)
        {
            float blob = RestDist * MathF.Cbrt(3f * requested / (4f * MathF.PI));
            fillRadius = MathF.Max(sourceRadius, blob);
        }

        int start = Count;
        float perMass = totalMass / requested;
        Vector3 sumEjecta = Vector3.Zero;

        for (int k = 0; k < requested; k++)
        {
            Vector3 dirIn = RandomUnit();
            float rr = fillRadius * MathF.Cbrt(_rng.NextSingle());
            Vector3 p = sourceCenter + dirIn * rr;

            Vector3 fromContact = p - contactPoint;
            float dist = fromContact.Length;
            float proximity = 1f / (1f + dist * dist * 0.5f);        // 1 at contact -> 0 far
            Vector3 outDir = dist > 1e-4f ? fromContact / dist : RandomUnit();

            float speed = impactSpeed * (0.15f + 0.85f * proximity) * (0.5f + _rng.NextSingle());
            Vector3 ejecta = outDir * speed + RandomUnit() * (impactSpeed * 0.08f);

            int idx = Count++;
            Pos[idx] = p;
            Vel[idx] = bulkVel + ejecta;
            Color[idx] = color;
            Temp[idx] = heat * (0.25f + 0.75f * proximity) * (0.7f + 0.6f * _rng.NextSingle());
            Mass[idx] = perMass;
            Age[idx] = 0f;
            Life[idx] = BaseLife * (0.7f + 0.6f * _rng.NextSingle());
            Crowd[idx] = 0f;
            sumEjecta += ejecta;
        }

        // Remove the mean ejecta so the batch injects no net momentum.
        Vector3 meanEjecta = sumEjecta / requested;
        for (int i = start; i < Count; i++) Vel[i] -= meanEjecta;
    }

    /// <summary>
    /// Advance every particle by <paramref name="dt"/> seconds of simulation time.
    /// Internally substepped (and capped) so a large frame dt stays stable without scaling
    /// cost with the physics substep count.
    /// </summary>
    public void Step(float dt, List<Body> bodies, float g, float softening)
    {
        if (Count == 0 || dt <= 0f) { _coalesceTimer += dt; return; }

        const float maxStep = 0.02f;
        // Cap the fluid's advance per frame. At high TimeScale a frame can carry seconds
        // of sim-time; integrating all of it needs either many SPH substeps (cost spiral,
        // the "freeze" on impact) or a huge h (instability). Debris lagging the bodies at
        // extreme speeds is invisible; a hitch is not.
        dt = MathF.Min(dt, 4 * maxStep);
        int sub = Math.Clamp((int)MathF.Ceiling(dt / maxStep), 1, 4);
        float h = dt / sub;
        float eps2 = ParticleSoftening * ParticleSoftening + softening * softening;

        for (int s = 0; s < sub; s++)
        {
            if (EnableSph) FluidSubstep(h, bodies, g, eps2);
            else Integrate(h, bodies, g, eps2);
        }

        // Cooling + ageing once per frame (radiative loss; diffusion already ran per substep).
        float cool = MathF.Exp(-CoolRate * dt);
        for (int i = 0; i < Count; i++)
        {
            Temp[i] = AmbientTemp + (Temp[i] - AmbientTemp) * cool;
            Age[i] += dt;
        }

        // Reap aged-out particles.
        int j = 0;
        while (j < Count)
        {
            if (Age[j] >= Life[j]) SwapRemove(j);
            else j++;
        }

        _coalesceTimer += dt;
    }

    // ---- v2 fluid (soft-particle SPH) ----

    private void FluidSubstep(float h, List<Body> bodies, float g, float eps2)
    {
        _hash.Build(Pos, Count, SphRadius);
        float h2 = SphRadius * SphRadius;
        float invH = 1f / SphRadius;
        float invH2 = 1f / h2;
        float rho0 = RestDensity();

        // Pass 1: SPH number density (parallel; each particle writes only its own slot).
        ParallelFor(Count, i =>
        {
            Span<int> buckets = stackalloc int[27];
            Vector3 xi = Pos[i];
            float rho = 1f; // self contribution Wn(0) = 1
            int nn = 0;
            int nb = _hash.CollectBuckets(xi, buckets);
            for (int bi = 0; bi < nb; bi++)
            {
                int b = buckets[bi];
                int s0 = _hash.CellStart[b], s1 = _hash.CellStart[b + 1];
                for (int e = s0; e < s1; e++)
                {
                    int jdx = _hash.Entries[e];
                    if (jdx == i) continue;
                    float r2 = (xi - Pos[jdx]).LengthSquared;
                    if (r2 >= h2) continue;
                    float q = 1f - r2 * invH2;
                    rho += q * q * q;
                    nn++;
                }
            }
            _density[i] = rho;
            Crowd[i] = MathF.Min(1f, nn / 30f);
        });

        // Pass 2: pressure (repulsive above rest density, cohesive below) + XSPH viscosity
        // + heat diffusion, all from start-of-substep state (parallel).
        ParallelFor(Count, i =>
        {
            Span<int> buckets = stackalloc int[27];
            Vector3 xi = Pos[i], vi = Vel[i];
            float ti = Temp[i];
            float pi = _density[i] - rho0;
            Vector3 aP = Vector3.Zero, vRel = Vector3.Zero;
            float tDiff = 0f, wSum = 0f;

            int nb = _hash.CollectBuckets(xi, buckets);
            for (int bi = 0; bi < nb; bi++)
            {
                int b = buckets[bi];
                int s0 = _hash.CellStart[b], s1 = _hash.CellStart[b + 1];
                for (int e = s0; e < s1; e++)
                {
                    int jdx = _hash.Entries[e];
                    if (jdx == i) continue;
                    Vector3 d = xi - Pos[jdx];
                    float r2 = d.LengthSquared;
                    if (r2 >= h2 || r2 < 1e-12f) continue;

                    float r = MathF.Sqrt(r2);
                    Vector3 dir = d / r;
                    float w = 1f - r * invH;
                    float grad = w * w;            // kernel gradient magnitude (h-r)^2, normalized

                    // Symmetric pressure: dir points from j to i, so positive pressure
                    // (compressed) pushes i away (repulsion); negative (sparse) pulls in.
                    float psum = pi + (_density[jdx] - rho0);
                    float k = psum > 0f ? Repulsion : Cohesion;
                    aP += dir * (psum * grad * k);

                    vRel += (Vel[jdx] - vi) * w;
                    tDiff += (Temp[jdx] - ti) * w;
                    wSum += w;
                }
            }

            _acc[i] = aP;
            _vxsph[i] = wSum > 1e-5f ? vRel / wSum : Vector3.Zero;
            _tdiff[i] = wSum > 1e-5f ? tDiff / wSum : 0f;
        });

        // Pass 3: add gravity, integrate, viscosity, heat diffusion (parallel).
        const float maxA = 600f;
        ParallelFor(Count, i =>
        {
            Vector3 xi = Pos[i];

            Vector3 aG = Vector3.Zero;
            for (int b = 0; b < bodies.Count; b++)
            {
                Body body = bodies[b];
                Vector3 d = body.Position - xi;
                float dist2 = d.LengthSquared + eps2;
                float inv = 1f / MathF.Sqrt(dist2);
                aG += d * (g * body.Mass * inv / dist2);
            }

            Vector3 a = aG + _acc[i];
            float al = a.Length;
            if (al > maxA) a *= maxA / al;

            Vector3 v = Vel[i] + a * h;
            v += Viscosity * _vxsph[i];
            float sp = v.Length;
            if (sp > MaxSpeed) v *= MaxSpeed / sp;

            Temp[i] += HeatDiffuse * _tdiff[i] * h;
            Vel[i] = v;
            Pos[i] = xi + v * h;
        });

        if (AccreteOnContact)
        {
            int i = 0;
            while (i < Count) { if (Absorbed(Pos[i], bodies)) SwapRemove(i); else i++; }
        }
    }

    /// <summary>Run <paramref name="body"/> over [0,count): parallel above a threshold, else serial.</summary>
    private static void ParallelFor(int count, Action<int> body)
    {
        if (count < 2048) { for (int i = 0; i < count; i++) body(i); }
        else System.Threading.Tasks.Parallel.For(0, count, body);
    }

    /// <summary>Rest density: SPH number density of a regular lattice at RestDist spacing.</summary>
    private float RestDensity()
    {
        float h = SphRadius, r0 = MathF.Max(RestDist, 1e-3f);
        float invH2 = 1f / (h * h);
        int span = (int)MathF.Ceiling(h / r0);
        float rho = 1f; // self
        for (int z = -span; z <= span; z++)
        for (int y = -span; y <= span; y++)
        for (int x = -span; x <= span; x++)
        {
            if (x == 0 && y == 0 && z == 0) continue;
            float r2 = r0 * r0 * (x * x + y * y + z * z);
            if (r2 >= h * h) continue;
            float q = 1f - r2 * invH2;
            rho += q * q * q;
        }
        return rho;
    }

    // ---- v1 ballistic (SPH off) ----

    private void Integrate(float h, List<Body> bodies, float g, float eps2)
    {
        int i = 0;
        while (i < Count)
        {
            Vector3 p = Pos[i];

            Vector3 a = Vector3.Zero;
            for (int b = 0; b < bodies.Count; b++)
            {
                Body body = bodies[b];
                Vector3 d = body.Position - p;
                float dist2 = d.LengthSquared + eps2;
                float inv = 1f / MathF.Sqrt(dist2);
                a += d * (g * body.Mass * inv / dist2);
            }

            Vector3 v = Vel[i] + a * h;
            p += v * h;

            if (AccreteOnContact && Absorbed(p, bodies))
            {
                SwapRemove(i);
                continue;
            }

            Vel[i] = v;
            Pos[i] = p;
            i++;
        }
    }

    // ---- v3 re-coalescence ----

    /// <summary>
    /// Find settled, cooled clusters of particles and reform each into a single solid body
    /// (mass + momentum conserved), returning the new bodies and freeing their particles.
    /// Runs at most every <see cref="CoalesceInterval"/> seconds. Returns null if nothing
    /// reformed this tick. Keeps long-running particle counts bounded.
    /// </summary>
    public List<Body>? ExtractCoalesced()
    {
        if (!EnableCoalesce || Count < CoalesceMinCluster || _coalesceTimer < CoalesceInterval)
            return null;
        _coalesceTimer = 0f;

        // Connect anything still within cohesive interaction range, so a relaxed blob
        // counts as one cluster even if its spacing has grown past the rest distance.
        float link = MathF.Max(SphRadius, RestDist * 1.8f);
        _hash.Build(Pos, Count, link);
        float link2 = link * link;

        var visited = new bool[Count];
        var remove = new bool[Count];
        var stack = new Stack<int>();
        var cluster = new List<int>();
        List<Body>? result = null;
        Span<int> buckets = stackalloc int[27]; // reused per flood-fill pop

        for (int seed = 0; seed < Count; seed++)
        {
            if (visited[seed]) continue;

            // Flood-fill one connected component.
            cluster.Clear();
            stack.Push(seed);
            visited[seed] = true;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                cluster.Add(i);

                int nb = _hash.CollectBuckets(Pos[i], buckets);
                for (int bi = 0; bi < nb; bi++)
                {
                    int b = buckets[bi];
                    int s0 = _hash.CellStart[b], s1 = _hash.CellStart[b + 1];
                    for (int e = s0; e < s1; e++)
                    {
                        int jdx = _hash.Entries[e];
                        if (visited[jdx]) continue;
                        if ((Pos[i] - Pos[jdx]).LengthSquared <= link2)
                        {
                            visited[jdx] = true;
                            stack.Push(jdx);
                        }
                    }
                }
            }

            if (cluster.Count < CoalesceMinCluster) continue;

            // Mass-weighted aggregates.
            float m = 0f, tempSum = 0f;
            Vector3 com = Vector3.Zero, vmean = Vector3.Zero, col = Vector3.Zero;
            foreach (int i in cluster)
            {
                m += Mass[i];
                com += Pos[i] * Mass[i];
                vmean += Vel[i] * Mass[i];
                col += Color[i] * Mass[i];
                tempSum += Temp[i];
            }
            if (m <= 1e-5f) continue;
            com /= m; vmean /= m; col /= m;
            float meanTemp = tempSum / cluster.Count;

            // Velocity dispersion (RMS of |v - vmean|, mass-weighted).
            float disp = 0f;
            foreach (int i in cluster) disp += Mass[i] * (Vel[i] - vmean).LengthSquared;
            disp = MathF.Sqrt(disp / m);

            if (meanTemp > CoalesceMaxTemp || disp > CoalesceMaxDispersion) continue;

            // Reform: one body carrying the cluster's mass + momentum.
            result ??= new List<Body>();
            result.Add(new Body("Reformed", com, vmean, m, col));
            foreach (int i in cluster) remove[i] = true;
        }

        if (result == null) return null;

        // Compact out the consumed particles (stable order).
        int w = 0;
        for (int r = 0; r < Count; r++)
        {
            if (remove[r]) continue;
            if (w != r)
            {
                Pos[w] = Pos[r]; Vel[w] = Vel[r]; Color[w] = Color[r];
                Temp[w] = Temp[r]; Mass[w] = Mass[r]; Age[w] = Age[r];
                Life[w] = Life[r]; Crowd[w] = Crowd[r];
            }
            w++;
        }
        Count = w;
        return result;
    }

    private static bool Absorbed(Vector3 p, List<Body> bodies)
    {
        for (int b = 0; b < bodies.Count; b++)
        {
            float r = bodies[b].Radius;
            if ((bodies[b].Position - p).LengthSquared < r * r) return true;
        }
        return false;
    }

    private void SwapRemove(int i)
    {
        int last = --Count;
        if (i != last)
        {
            Pos[i] = Pos[last]; Vel[i] = Vel[last]; Color[i] = Color[last];
            Temp[i] = Temp[last]; Mass[i] = Mass[last]; Age[i] = Age[last];
            Life[i] = Life[last]; Crowd[i] = Crowd[last];
        }
    }

    private Vector3 RandomUnit()
    {
        float z = 2f * _rng.NextSingle() - 1f;
        float angle = 2f * MathF.PI * _rng.NextSingle();
        float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), z);
    }
}
