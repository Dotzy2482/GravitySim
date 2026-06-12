using OpenTK.Mathematics;

namespace GravitySim;

public enum Integrator
{
    SemiImplicitEuler,
    VelocityVerlet,
}

/// <summary>
/// Newtonian N-body solver. O(n²) all-pairs forces with Plummer softening:
///   F = G * m1 * m2 / (r² + ε²)
/// </summary>
public class PhysicsEngine
{
    public float G = 1.0f;
    /// <summary>Softening length ε — avoids force singularities at close range.</summary>
    public float Softening = 0.05f;
    /// <summary>Fixed physics step. Each update consumes frame time in steps of this size.</summary>
    public float Dt = 1f / 240f;
    /// <summary>Simulation speed multiplier, adjustable at runtime.</summary>
    public float TimeScale = 1.0f;
    public bool Paused = false;
    public Integrator Integrator = Integrator.VelocityVerlet;
    /// <summary>Merge overlapping bodies, conserving mass and momentum.</summary>
    public bool EnableCollisions = false;

    public List<Body> Bodies { get; } = new();

    /// <summary>Raised after a collision merge: (removed body, surviving body).</summary>
    public event Action<Body, Body>? BodiesMerged;

    private float _accumulator;

    /// <summary>Advance the simulation by a frame's worth of real time using fixed substeps.</summary>
    public void Update(float frameTime)
    {
        if (Paused || Bodies.Count == 0) return;

        // Cap to avoid spiral-of-death after a long stall (window drag, etc.)
        _accumulator += MathF.Min(frameTime, 0.1f) * TimeScale;

        while (_accumulator >= Dt)
        {
            Step(Dt);
            if (EnableCollisions) HandleCollisions();
            _accumulator -= Dt;
        }
    }

    public void Step(float dt)
    {
        switch (Integrator)
        {
            case Integrator.SemiImplicitEuler: StepSemiImplicitEuler(dt); break;
            case Integrator.VelocityVerlet:    StepVelocityVerlet(dt);    break;
        }
    }

    // v += a·dt first, then x += v·dt — symplectic, conserves orbital energy far
    // better than explicit Euler.
    private void StepSemiImplicitEuler(float dt)
    {
        ComputeAccelerations();
        foreach (var b in Bodies)
        {
            if (b.Anchored || b.IsHeld) continue;
            b.Velocity += b.Acceleration * dt;
            b.Position += b.Velocity * dt;
        }
    }

    // x += v·dt + ½a·dt², then v += ½(a_old + a_new)·dt.
    private void StepVelocityVerlet(float dt)
    {
        foreach (var b in Bodies)
        {
            if (b.Anchored || b.IsHeld) continue;
            b.Position += b.Velocity * dt + 0.5f * b.Acceleration * dt * dt;
        }

        // Save old accelerations, compute new ones at the updated positions.
        Span<Vector3> oldAcc = Bodies.Count <= 64 ? stackalloc Vector3[Bodies.Count] : new Vector3[Bodies.Count];
        for (int i = 0; i < Bodies.Count; i++) oldAcc[i] = Bodies[i].Acceleration;

        ComputeAccelerations();

        for (int i = 0; i < Bodies.Count; i++)
        {
            var b = Bodies[i];
            if (b.Anchored || b.IsHeld) continue;
            b.Velocity += 0.5f * (oldAcc[i] + b.Acceleration) * dt;
        }
    }

    /// <summary>
    /// Merge any overlapping pair into the more massive body, conserving mass and
    /// momentum and blending color/density by mass. Held bodies are left alone.
    /// </summary>
    private void HandleCollisions()
    {
    restart:
        for (int i = 0; i < Bodies.Count; i++)
        {
            for (int j = i + 1; j < Bodies.Count; j++)
            {
                Body a = Bodies[i], b = Bodies[j];
                if (a.IsHeld || b.IsHeld) continue;

                float touchDist = 0.85f * (a.Radius + b.Radius);
                if ((a.Position - b.Position).LengthSquared >= touchDist * touchDist) continue;

                Body survivor = a.Mass >= b.Mass ? a : b;
                Body removed  = survivor == a ? b : a;

                float total = a.Mass + b.Mass;
                if (!survivor.Anchored)
                {
                    survivor.Velocity = (a.Velocity * a.Mass + b.Velocity * b.Mass) / total;
                    survivor.Position = (a.Position * a.Mass + b.Position * b.Mass) / total;
                }
                survivor.Color    = (a.Color * a.Mass + b.Color * b.Mass) / total;
                survivor.Density  = (a.Density * a.Mass + b.Density * b.Mass) / total;
                survivor.Emissive = MathF.Max(a.Emissive, b.Emissive);
                survivor.Mass = total;
                survivor.UpdateRadius();

                Bodies.Remove(removed);
                BodiesMerged?.Invoke(removed, survivor);
                goto restart; // indices shifted; merges are rare, so rescan
            }
        }
    }

    /// <summary>All-pairs gravitational acceleration with softening.</summary>
    public void ComputeAccelerations()
    {
        foreach (var b in Bodies) b.Acceleration = Vector3.Zero;

        float eps2 = Softening * Softening;
        for (int i = 0; i < Bodies.Count; i++)
        {
            for (int j = i + 1; j < Bodies.Count; j++)
            {
                Vector3 delta = Bodies[j].Position - Bodies[i].Position;
                float dist2 = delta.LengthSquared + eps2;
                float invDist = 1f / MathF.Sqrt(dist2);
                // a_i = G·m_j / (r²+ε²) toward j; direction = delta/|delta| ≈ delta·invDist
                float f = G * invDist / dist2;
                Bodies[i].Acceleration += delta * (f * Bodies[j].Mass);
                Bodies[j].Acceleration -= delta * (f * Bodies[i].Mass);
            }
        }
    }

    /// <summary>
    /// Gravitational potential Φ at a point (negative; deeper near massive bodies).
    /// Used by the spacetime grid to shape the wells.
    /// </summary>
    public float PotentialAt(Vector3 point)
    {
        float phi = 0f;
        float eps2 = Softening * Softening;
        foreach (var b in Bodies)
        {
            float dist = MathF.Sqrt((b.Position - point).LengthSquared + eps2);
            phi -= G * b.Mass / dist;
        }
        return phi;
    }

    /// <summary>Speed for a circular orbit of radius r around central mass M: v = √(GM/r).</summary>
    public float CircularOrbitSpeed(float centralMass, float radius)
        => MathF.Sqrt(G * centralMass / radius);

    public float TotalKineticEnergy()
    {
        float ke = 0f;
        foreach (var b in Bodies) ke += b.KineticEnergy;
        return ke;
    }

    /// <summary>Pairwise potential energy: Σ −G·m_i·m_j / √(r² + ε²).</summary>
    public float TotalPotentialEnergy()
    {
        float pe = 0f;
        float eps2 = Softening * Softening;
        for (int i = 0; i < Bodies.Count; i++)
            for (int j = i + 1; j < Bodies.Count; j++)
            {
                float dist = MathF.Sqrt((Bodies[i].Position - Bodies[j].Position).LengthSquared + eps2);
                pe -= G * Bodies[i].Mass * Bodies[j].Mass / dist;
            }
        return pe;
    }

    public Vector3 TotalMomentum()
    {
        var p = Vector3.Zero;
        foreach (var b in Bodies) p += b.Velocity * b.Mass;
        return p;
    }

    /// <summary>The most massive body other than <paramref name="exclude"/> (or null).</summary>
    public Body? MostMassive(Body? exclude = null)
    {
        Body? best = null;
        foreach (var b in Bodies)
            if (b != exclude && (best == null || b.Mass > best.Mass))
                best = b;
        return best;
    }
}
