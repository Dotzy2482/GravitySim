using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// Preset systems, switchable at runtime with the number keys.
/// All orbital speeds are derived from v = √(GM/r) so orbits start near-circular.
/// </summary>
public static class ScenePresets
{
    public const int Count = 3;

    public static string Name(int index) => index switch
    {
        1 => "Two-body (star + planet)",
        2 => "Star system (1 star, 5 planets, 1 moon)",
        3 => "Binary stars + circumbinary planets",
        _ => "Unknown",
    };

    public static void Load(int index, PhysicsEngine physics)
    {
        physics.Bodies.Clear();
        switch (index)
        {
            case 1: LoadTwoBody(physics); break;
            case 3: LoadBinary(physics); break;
            default: LoadStarSystem(physics); break;
        }
        // Prime accelerations so velocity Verlet's first step is correct.
        physics.ComputeAccelerations();
    }

    private static void LoadTwoBody(PhysicsEngine p)
    {
        float starMass = 1000f;
        var star = new Body("Star", Vector3.Zero, Vector3.Zero, starMass,
                            new Vector3(1.0f, 0.85f, 0.4f), emissive: 1f, density: 30f);

        float r = 12f;
        float v = p.CircularOrbitSpeed(starMass, r);
        var planet = new Body("Planet", new Vector3(r, 0, 0), new Vector3(0, 0, v), 8f,
                              new Vector3(0.4f, 0.6f, 1.0f));

        // Give the star the opposite momentum so the system barycenter stays put.
        star.Velocity = -planet.Velocity * (planet.Mass / star.Mass);

        p.Bodies.Add(star);
        p.Bodies.Add(planet);
    }

    private static void LoadStarSystem(PhysicsEngine p)
    {
        float starMass = 1200f;
        var star = new Body("Sun", Vector3.Zero, Vector3.Zero, starMass,
                            new Vector3(1.0f, 0.8f, 0.35f), emissive: 1f, density: 30f);
        p.Bodies.Add(star);

        // radius, mass, color, slight orbital-plane tilt for visual depth
        (float r, float m, Vector3 c, float tiltDeg)[] planets =
        {
            ( 6.5f,  2.0f, new Vector3(0.75f, 0.65f, 0.55f),  2f),
            ( 9.5f,  6.0f, new Vector3(0.90f, 0.55f, 0.25f), -3f),
            (13.5f,  8.0f, new Vector3(0.35f, 0.55f, 1.00f),  1f),
            (18.0f,  4.0f, new Vector3(0.85f, 0.30f, 0.25f), -2f),
            (24.0f, 16.0f, new Vector3(0.80f, 0.70f, 0.50f),  4f),
        };

        var momentum = Vector3.Zero;
        for (int i = 0; i < planets.Length; i++)
        {
            var (r, m, c, tiltDeg) = planets[i];
            // Spread starting angles so planets don't begin in a line.
            float angle = i * MathHelper.TwoPi / planets.Length * 1.7f;
            var pos = new Vector3(r * MathF.Cos(angle), 0, r * MathF.Sin(angle));
            float speed = p.CircularOrbitSpeed(starMass, r);
            var vel = new Vector3(-MathF.Sin(angle), 0, MathF.Cos(angle)) * speed;

            // Tilt the orbital plane slightly around X.
            var tilt = Matrix3.CreateRotationX(MathHelper.DegreesToRadians(tiltDeg));
            pos = tilt * pos;
            vel = tilt * vel;

            var planet = new Body($"Planet {i + 1}", pos, vel, m, c);
            p.Bodies.Add(planet);
            momentum += vel * m;
        }

        // A moon around the big outer planet: local circular orbit added to the
        // planet's own velocity.
        var host = p.Bodies[^1];
        float moonR = 1.4f;
        float moonSpeed = p.CircularOrbitSpeed(host.Mass, moonR);
        var moonPos = host.Position + new Vector3(moonR, 0.3f, 0);
        var moonVel = host.Velocity + new Vector3(0, 0, moonSpeed);
        var moon = new Body("Moon", moonPos, moonVel, 0.4f, new Vector3(0.7f, 0.7f, 0.75f));
        p.Bodies.Add(moon);
        momentum += moonVel * moon.Mass;

        // Zero total momentum so the whole system doesn't drift off-screen.
        star.Velocity = -momentum / star.Mass;
    }

    private static void LoadBinary(PhysicsEngine p)
    {
        // Equal-mass binary: each star orbits the barycenter at separation/2.
        // v = √(G·M_other·sep)/sep ... for equal masses: v = √(G·m/(4·a)) with a = sep/2
        float m = 600f;
        float sep = 7f;
        float v = MathF.Sqrt(p.G * m / (2f * sep));

        p.Bodies.Add(new Body("Star A", new Vector3(-sep / 2, 0, 0), new Vector3(0, 0, -v),
                              m, new Vector3(1.0f, 0.75f, 0.3f), emissive: 1f, density: 30f));
        p.Bodies.Add(new Body("Star B", new Vector3(sep / 2, 0, 0), new Vector3(0, 0, v),
                              m, new Vector3(0.55f, 0.7f, 1.0f), emissive: 1f, density: 30f));

        // Circumbinary planets orbit the combined mass, far enough out to be stable.
        float total = 2f * m;
        (float r, float mass, Vector3 c)[] planets =
        {
            (16f,  5.0f, new Vector3(0.4f, 0.8f, 0.6f)),
            (22f,  9.0f, new Vector3(0.9f, 0.5f, 0.7f)),
            (29f, 12.0f, new Vector3(0.6f, 0.55f, 0.95f)),
        };

        for (int i = 0; i < planets.Length; i++)
        {
            var (r, mass, c) = planets[i];
            float angle = i * 2.1f;
            var pos = new Vector3(r * MathF.Cos(angle), 0, r * MathF.Sin(angle));
            float speed = p.CircularOrbitSpeed(total, r);
            var vel = new Vector3(-MathF.Sin(angle), 0, MathF.Cos(angle)) * speed;
            p.Bodies.Add(new Body($"Planet {i + 1}", pos, vel, mass, c));
        }
    }
}
