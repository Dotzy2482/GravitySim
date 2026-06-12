using OpenTK.Mathematics;

namespace GravitySim;

/// <summary>
/// Orbit camera: yaw/pitch around a target point at a given distance.
/// Left-mouse drag rotates, scroll wheel zooms.
/// </summary>
public class OrbitCamera
{
    public Vector3 Target = Vector3.Zero;
    public float Distance = 35f;
    public float Yaw = MathHelper.DegreesToRadians(45f);
    public float Pitch = MathHelper.DegreesToRadians(28f);

    public float MinDistance = 2f;
    public float MaxDistance = 200f;
    public float RotateSensitivity = 0.006f;
    public float ZoomSensitivity = 0.10f;

    private const float PitchLimit = MathHelper.PiOver2 - 0.05f;

    public Vector3 Position
    {
        get
        {
            float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
            float cy = MathF.Cos(Yaw),   sy = MathF.Sin(Yaw);
            return Target + Distance * new Vector3(cp * cy, sp, cp * sy);
        }
    }

    public void Rotate(float deltaX, float deltaY)
    {
        Yaw += deltaX * RotateSensitivity;
        Pitch = MathHelper.Clamp(Pitch + deltaY * RotateSensitivity, -PitchLimit, PitchLimit);
    }

    public void Zoom(float scrollDelta)
    {
        // Exponential zoom feels uniform at all distances.
        Distance = MathHelper.Clamp(Distance * MathF.Exp(-scrollDelta * ZoomSensitivity),
                                    MinDistance, MaxDistance);
    }

    /// <summary>
    /// Pan the orbit target camera-relative: WASD on the horizontal plane,
    /// up/down along world Y (E/Q). Speed scales with zoom distance so
    /// movement feels uniform at any range.
    /// </summary>
    public void Pan(float forward, float right, float up, float deltaTime)
    {
        Vector3 fwd = Target - Position;
        fwd.Y = 0f;
        fwd = fwd.LengthSquared > 1e-6f ? fwd.Normalized() : Vector3.UnitX;
        Vector3 rightDir = Vector3.Cross(fwd, Vector3.UnitY);

        float speed = Distance * 0.8f * deltaTime;
        Target += (fwd * forward + rightDir * right + Vector3.UnitY * up) * speed;
    }

    /// <summary>Vertical field of view; also used for screen-space pick-radius math.</summary>
    public const float FovDegrees = 50f;

    public Matrix4 GetViewMatrix() => Matrix4.LookAt(Position, Target, Vector3.UnitY);

    public Matrix4 GetProjectionMatrix(float aspectRatio)
        => Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(FovDegrees),
                                                aspectRatio, 0.1f, 500f);
}
