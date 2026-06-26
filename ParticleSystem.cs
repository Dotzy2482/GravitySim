using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// CPU debris-particle pool (v1: ballistic motion + heat, no cohesion yet).
///
/// Pure data/simulation — owns no GL state; <see cref="ParticleRenderer"/> reads the
/// arrays each frame to upload them. Particles feel gravity from the massive bodies
/// as a one-way external field (they never pull back on the bodies), are absorbed on
/// contact, and cool over time. SPH cohesion + heat diffusion arrive in v2/v3.
///
/// Storage is structure-of-arrays packed into [0..Count); dead particles are removed
/// by swapping the last live particle into the gap, keeping the buffer dense for a
/// contiguous render upload.
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
    public int Count { get; private set; }

    public int MaxParticles { get; private set; }

    // --- tunables (surfaced in the UI) ---
    /// <summary>Roughly how many particles a disruption of unit-sqrt-mass produces.</summary>
    public float ParticlesPerUnitMass = 1400f;
    public float BaseLife = 16f;            // seconds before a particle ages out
    public float CoolRate = 0.55f;          // exponential cooling per second
    public float AmbientTemp = 0f;
    public float SpawnScale = 1f;           // adaptive-budget multiplier [0.1..1]
    public float ParticleSoftening = 0.5f;  // softens body gravity near centres
    public bool AccreteOnContact = true;

    private readonly Random _rng = new();

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
        Count = 0;
    }

    public void Clear() => Count = 0;

    public int Remaining => MaxParticles - Count;

    /// <summary>Suggested particle budget for disrupting a body of the given mass.</summary>
    public int SuggestCount(float mass)
    {
        int n = (int)MathF.Round(ParticlesPerUnitMass * MathF.Sqrt(MathF.Max(mass, 0f)));
        return Math.Clamp(n, 200, MaxParticles);
    }

    /// <summary>
    /// Convert a chunk of a body into hot ejecta. Particles fill the source sphere and
    /// fly outward from the impact point (fastest/hottest nearest contact). The batch's
    /// net <em>added</em> momentum is zeroed so the source's bulk momentum is preserved.
    /// </summary>
    public void SpawnDebris(Vector3 sourceCenter, float sourceRadius, Vector3 bulkVel,
                            Vector3 color, float totalMass, Vector3 contactPoint,
                            float impactSpeed, float heat, int requested)
    {
        requested = (int)MathF.Round(requested * SpawnScale);
        requested = Math.Clamp(requested, 0, Remaining);
        if (requested <= 0 || totalMass <= 0f) return;

        int start = Count;
        float perMass = totalMass / requested;
        Vector3 sumEjecta = Vector3.Zero;

        for (int k = 0; k < requested; k++)
        {
            Vector3 dirIn = RandomUnit();
            float rr = sourceRadius * MathF.Cbrt(_rng.NextSingle());
            Vector3 p = sourceCenter + dirIn * rr;

            Vector3 fromContact = p - contactPoint;
            float dist = fromContact.Length;
            float proximity = 1f / (1f + dist * dist * 0.5f);        // 1 at contact → 0 far
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
            sumEjecta += ejecta;
        }

        // Remove the mean ejecta so the batch injects no net momentum.
        Vector3 meanEjecta = sumEjecta / requested;
        for (int i = start; i < Count; i++) Vel[i] -= meanEjecta;
    }

    /// <summary>
    /// Advance every particle by <paramref name="dt"/> seconds of simulation time:
    /// gravity from the massive bodies, accretion on contact, cooling, ageing.
    /// Integration is internally substepped (and capped) so a large frame dt stays stable
    /// without scaling cost with the physics substep count.
    /// </summary>
    public void Step(float dt, List<Body> bodies, float g, float softening)
    {
        if (Count == 0 || dt <= 0f) return;

        const float maxStep = 0.02f;
        int sub = Math.Clamp((int)MathF.Ceiling(dt / maxStep), 1, 4);
        float h = dt / sub;
        float eps2 = ParticleSoftening * ParticleSoftening + softening * softening;

        for (int s = 0; s < sub; s++)
            Integrate(h, bodies, g, eps2);

        // Heat + ageing once per frame at full dt (cheap, no stability concern).
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
    }

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

            // Semi-implicit Euler.
            Vector3 v = Vel[i] + a * h;
            p += v * h;

            if (AccreteOnContact && Absorbed(p, bodies))
            {
                SwapRemove(i);   // a different particle now occupies index i
                continue;        // don't advance i — process the swapped-in particle
            }

            Vel[i] = v;
            Pos[i] = p;
            i++;
        }
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
            Temp[i] = Temp[last]; Mass[i] = Mass[last]; Age[i] = Age[last]; Life[i] = Life[last];
        }
    }

    private Vector3 RandomUnit()
    {
        // Uniform direction on the unit sphere.
        float z = 2f * _rng.NextSingle() - 1f;
        float angle = 2f * MathF.PI * _rng.NextSingle();
        float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), z);
    }
}
