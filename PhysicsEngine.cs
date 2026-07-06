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
    /// <summary>Detect overlapping bodies and resolve them (merge / fracture).</summary>
    public bool EnableCollisions = false;

    // --- collision → fluid debris (v1) ---
    /// <summary>When true, hard impacts shatter bodies into hot debris instead of always merging.</summary>
    public bool EnableFracture = true;
    /// <summary>Below this specific impact energy Q, bodies simply merge (current behaviour).</summary>
    public float QMerge = 1.5f;
    /// <summary>At or above this Q, both bodies fully disrupt into a debris cloud.</summary>
    public float QDisrupt = 8f;
    /// <summary>Max fraction of mass thrown off as ejecta during a partial disruption.</summary>
    public float FractureMassLoss = 0.6f;

    /// <summary>Debris-particle pool. Bodies that shatter spawn into this.</summary>
    public ParticleSystem Particles { get; } = new();

    public List<Body> Bodies { get; } = new();

    /// <summary>Raised after a collision merge: (removed body, surviving body).</summary>
    public event Action<Body, Body>? BodiesMerged;

    private float _accumulator;

    /// <summary>Advance the simulation by a frame's worth of real time using fixed substeps.</summary>
    public void Update(float frameTime)
    {
        if (Paused) return;

        // Cap to avoid spiral-of-death after a long stall (window drag, etc.)
        _accumulator += MathF.Min(frameTime, 0.1f) * TimeScale;

        float consumed = 0f;
        while (_accumulator >= Dt)
        {
            if (Bodies.Count > 0)
            {
                Step(Dt);
                if (EnableCollisions) HandleCollisions();
            }
            _accumulator -= Dt;
            consumed += Dt;
        }

        // Debris advances once per frame over the consumed sim-time (decoupled from the
        // physics substep count so its cost stays bounded). Runs even when no bodies
        // remain, so a cloud from a total disruption keeps drifting and cooling.
        if (consumed > 0f)
        {
            Particles.Step(consumed, Bodies, G, Softening);

            // Settled, cooled debris clusters reform into solid bodies (mass + momentum
            // conserved), bounding long-running particle counts.
            var reborn = Particles.ExtractCoalesced();
            if (reborn != null)
            {
                Bodies.AddRange(reborn);
                ComputeAccelerations();
            }
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
    /// Resolve overlapping pairs by specific impact energy Q: gentle touches merge
    /// (conserving mass and momentum), medium impacts merge but throw off a hot crater
    /// spray, and hard impacts fully disrupt both bodies into a debris cloud. Held
    /// bodies are left alone. With <see cref="EnableFracture"/> off, everything merges.
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

                float total = a.Mass + b.Mass;

                // Specific impact energy: Q = ½·μ·v_rel² / (m_a + m_b), the kinetic
                // energy of the collision per unit of total mass. Low → merge,
                // medium → crater spray, high → both bodies shatter.
                float mu = a.Mass * b.Mass / total;
                Vector3 vrel = a.Velocity - b.Velocity;
                float impactSpeed = vrel.Length;
                float Q = 0.5f * mu * impactSpeed * impactSpeed / total;

                // Contact point on the line of centres, weighted by radii.
                float rsum = a.Radius + b.Radius;
                float tA = rsum > 1e-5f ? a.Radius / rsum : 0.5f;
                Vector3 contact = a.Position + (b.Position - a.Position) * tA;

                if (!EnableFracture || Q < QMerge)
                {
                    Vector3 gentleVel = (a.Velocity * a.Mass + b.Velocity * b.Mass) / total;
                    Vector3 gentleColor = (a.Color * a.Mass + b.Color * b.Mass) / total;
                    Body gentleSurvivor = Merge(a, b, total);

                    // A pinch of glowing dust so even soft merges read as physical.
                    // Mass is moved from the survivor into the particles, so the
                    // conservation invariants stay exact.
                    if (EnableFracture && Q > 0.25f * QMerge && !gentleSurvivor.Anchored)
                    {
                        float sparkMass = MathF.Min(total * 0.002f, 0.5f);
                        if (gentleSurvivor.Mass - sparkMass > 0.01f)
                        {
                            gentleSurvivor.Mass -= sparkMass;
                            gentleSurvivor.UpdateRadius();
                            Particles.SpawnDebris(contact, gentleSurvivor.Radius, gentleVel,
                                                  gentleColor, sparkMass, contact, impactSpeed,
                                                  0.45f,
                                                  Math.Min(Particles.SuggestCount(sparkMass), 250));
                        }
                    }
                }
                else if (Q < QDisrupt)
                {
                    // Partial disruption: merge, then blow a hot crater spray off the survivor.
                    Vector3 mergedVel   = (a.Velocity * a.Mass + b.Velocity * b.Mass) / total;
                    Vector3 mergedColor = (a.Color * a.Mass + b.Color * b.Mass) / total;
                    Body survivor = Merge(a, b, total);

                    float frac = FractureMassLoss *
                                 Math.Clamp((Q - QMerge) / MathF.Max(QDisrupt - QMerge, 1e-3f), 0f, 1f);
                    float ejectMass = survivor.Mass * frac;
                    if (ejectMass > 0f && !survivor.Anchored)
                    {
                        survivor.Mass = MathF.Max(survivor.Mass - ejectMass, 0.01f);
                        survivor.UpdateRadius();
                        Particles.SpawnDebris(contact, survivor.Radius, mergedVel, mergedColor,
                                              ejectMass, contact, impactSpeed, HeatFromQ(Q),
                                              Particles.SuggestCount(ejectMass));
                    }
                }
                else
                {
                    // Full disruption: both bodies become debris.
                    float heat = HeatFromQ(Q);
                    Particles.SpawnDebris(a.Position, a.Radius, a.Velocity, a.Color, a.Mass,
                                          contact, impactSpeed, heat, Particles.SuggestCount(a.Mass));
                    Particles.SpawnDebris(b.Position, b.Radius, b.Velocity, b.Color, b.Mass,
                                          contact, impactSpeed, heat, Particles.SuggestCount(b.Mass));
                    Bodies.Remove(a);
                    Bodies.Remove(b);
                    // No survivor to follow; the window nulls a vanished selection defensively.
                }

                goto restart; // indices shifted; collisions are rare, so rescan
            }
        }
    }

    /// <summary>Merge b into the more massive body, conserving mass and momentum. Returns the survivor.</summary>
    private Body Merge(Body a, Body b, float total)
    {
        Body survivor = a.Mass >= b.Mass ? a : b;
        Body removed  = survivor == a ? b : a;

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
        return survivor;
    }

    /// <summary>Map specific impact energy to a peak debris temperature (HDR ~0.3..1.6).</summary>
    private float HeatFromQ(float Q)
    {
        float t = Q / MathF.Max(QDisrupt, 1e-3f);
        return Math.Clamp(0.5f + 0.9f * t, 0.3f, 1.6f);
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
