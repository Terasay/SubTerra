using System.Numerics;
using SubTerra.Simulation;

namespace SubTerra.Navigation;

/// <summary>Free flight through a point cloud, with no floor, wall or orbit-target constraint.</summary>
public sealed class FlyNavigator : ICameraNavigator
{
    private readonly string _scenario;
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public float Speed { get; private set; } = 6;
    public float DistanceTravelled { get; private set; }
    public Vector3 Forward => new(MathF.Cos(Yaw) * MathF.Cos(Pitch), MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch));

    public FlyNavigator(string scenario, Vector3 position, Vector3 direction, float speed = 6)
    {
        _scenario = scenario;
        Position = position;
        var unit = direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Vector3.UnitX;
        Yaw = MathF.Atan2(unit.Y, unit.X);
        Pitch = Math.Clamp(MathF.Asin(Math.Clamp(unit.Z, -1, 1)), -1.5f, 1.5f);
        if (float.IsFinite(speed)) Speed = Math.Clamp(speed, .5f, 40);
    }

    public void PlaceAt(float progress)
    {
        if (!float.IsFinite(progress)) return;
        var t = Math.Clamp(progress, 0, 1);
        Position = CaveFactory.Center(_scenario, t);
        var forward = CaveFactory.Basis(_scenario, t).Forward;
        Yaw = MathF.Atan2(forward.Y, forward.X);
        Pitch = 0;
    }

    public void Look(float yawDelta, float pitchDelta)
    {
        if (!float.IsFinite(yawDelta) || !float.IsFinite(pitchDelta)) return;
        Yaw = MathF.IEEERemainder(Yaw + yawDelta, MathF.Tau);
        Pitch = Math.Clamp(Pitch + pitchDelta, -1.5f, 1.5f);
    }

    public void ChangeSpeed(float wheelSteps)
    {
        if (float.IsFinite(wheelSteps)) Speed = Math.Clamp(Speed * MathF.Pow(1.25f, wheelSteps), .5f, 40);
    }

    public void Move(float forward, float right, float deltaSeconds, bool fast, float up = 0)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0 || !float.IsFinite(forward) ||
            !float.IsFinite(right) || !float.IsFinite(up)) return;
        var sideways = new Vector3(MathF.Sin(Yaw), -MathF.Cos(Yaw), 0);
        var direction = Forward * forward + sideways * right + Vector3.UnitZ * up;
        if (direction.LengthSquared() > 1) direction = Vector3.Normalize(direction);
        var step = direction * Speed * (fast ? 3 : 1) * Math.Min(deltaSeconds, .1f);
        Position += step;
        DistanceTravelled += step.Length();
    }
}
