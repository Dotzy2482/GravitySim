using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// A single celestial body. Pure data — knows nothing about rendering.
/// </summary>
public class Body
{
    public string Name;
    public Vector3 Position;
    public Vector3 Velocity;
    public Vector3 Acceleration;   // cached by the integrator (used by velocity Verlet)
    public float Mass;
    public float Radius;
    public Vector3 Color;
    /// <summary>0 = lit like a planet, 1 = fully self-luminous (a star).</summary>
    public float Emissive;
    /// <summary>Exerts gravity but is never moved by the integrator.</summary>
    public bool Anchored;
    /// <summary>True while the user drags this body; integration is skipped.</summary>
    public bool IsHeld;
    /// <summary>Per-body opt-out from the global trail rendering.</summary>
    public bool ShowTrail = true;
    /// <summary>Recent positions (oldest first); maintained by the window, drawn as a fading line.</summary>
    public readonly List<Vector3> Trail = new();

    /// <summary>Visual density: radius is derived as r = ∛(3m / 4πρ).</summary>
    public float Density;
    public const float DefaultDensity = 12.0f;

    public Body(string name, Vector3 position, Vector3 velocity, float mass,
                Vector3 color, float emissive = 0f, float? density = null)
    {
        Name = name;
        Position = position;
        Velocity = velocity;
        Mass = mass;
        Color = color;
        Emissive = emissive;
        Density = density ?? DefaultDensity;
        UpdateRadius();
    }

    /// <summary>Re-derive the radius after Mass or Density changes.</summary>
    public void UpdateRadius() => Radius = RadiusFromMass(Mass, Density);

    public static float RadiusFromMass(float mass, float density)
        => MathF.Cbrt(3f * mass / (4f * MathF.PI * density));

    public float KineticEnergy => 0.5f * Mass * Velocity.LengthSquared;
}
