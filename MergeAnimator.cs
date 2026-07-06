using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// Render-side "soft absorption" merge animation. Physics merges instantly
/// (PhysicsEngine.Merge); this replays the removed body as a shrinking ghost
/// that sinks into the survivor, and eases the survivor's *rendered* radius
/// from its pre-merge to its post-merge value. GL-free: only OpenTK.Mathematics,
/// so it compiles into the headless verification suite.
/// </summary>
public class MergeAnimator
{
    public const float Duration = 0.55f; // seconds

    public struct Anim
    {
        public Body Survivor;         // live ref: ghost tracks the survivor's motion
        public Vector3 StartOffset;   // removed.Position - survivor.Position at merge time
        public float GhostRadius;
        public Vector3 GhostColor;
        public float SurvivorOldRadius;
        public float Age;
    }

    private readonly List<Anim> _anims = new();

    public IReadOnlyList<Anim> Active => _anims;

    /// <summary>Hook for PhysicsEngine.BodiesMerged. Survivor already carries the merged mass/radius.</summary>
    public void OnMerge(Body removed, Body survivor)
    {
        _anims.Add(new Anim
        {
            Survivor = survivor,
            StartOffset = removed.Position - survivor.Position,
            GhostRadius = removed.Radius,
            GhostColor = removed.Color,
            SurvivorOldRadius = Body.RadiusFromMass(
                MathF.Max(survivor.Mass - removed.Mass, 0.01f), survivor.Density),
            Age = 0f,
        });
    }

    public void Update(float dt)
    {
        for (int i = _anims.Count - 1; i >= 0; i--)
        {
            var a = _anims[i];
            a.Age += dt;
            if (a.Age >= Duration) { _anims.RemoveAt(i); continue; }
            _anims[i] = a;
        }
    }

    /// <summary>0..1 eased progress (smoothstep).</summary>
    public static float Progress(in Anim a)
    {
        float t = Math.Clamp(a.Age / Duration, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Ghost center: sinks from the contact offset into the survivor's current position.</summary>
    public Vector3 GhostPosition(in Anim a)
        => a.Survivor.Position + a.StartOffset * (1f - Progress(a));

    /// <summary>Ghost scale factor (shrinks to nothing).</summary>
    public static float GhostScale(in Anim a) => MathF.Pow(1f - Progress(a), 0.7f);

    /// <summary>
    /// The radius a body should be *drawn* at: eased old->new while it is the
    /// survivor of an active merge, its physics radius otherwise.
    /// </summary>
    public float RenderRadius(Body b)
    {
        for (int i = _anims.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_anims[i].Survivor, b))
            {
                var a = _anims[i];
                return MathHelper.Lerp(a.SurvivorOldRadius, b.Radius, Progress(a));
            }
        }
        return b.Radius;
    }
}
