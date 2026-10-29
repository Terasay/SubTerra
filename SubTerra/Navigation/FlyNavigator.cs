using System.Numerics;
using SubTerra.Simulation;

namespace SubTerra.Navigation;

/// <summary>Free flight through a point cloud, with no floor, wall or orbit-target constraint.</summary>
public sealed class FlyNavigator : ICameraNavigator
{
    private readonly string _scenario;
    private readonly float _minimumSpeed;
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public float Speed { get; private set; } = 6;
    public float FastMultiplier { get; set; } = 3;
    public bool KeepAltitude { get; set; }
    public float DistanceTravelled { get; private set; }
    public Vector3 Forward => new(MathF.Cos(Yaw) * MathF.Cos(Pitch), MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch));

    public FlyNavigator(string scenario, Vector3 position, Vector3 direction, float speed = 6, float minimumSpeed = .5f)
    {
        _scenario = scenario;
        _minimumSpeed = float.IsFinite(minimumSpeed) ? Math.Clamp(minimumSpeed, .0005f, .5f) : .5f;
        Position = position;
        var unit = direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Vector3.UnitX;
        Yaw = MathF.Atan2(unit.Y, unit.X);
        Pitch = Math.Clamp(MathF.Asin(Math.Clamp(unit.Z, -1, 1)), -1.5f, 1.5f);
        if (float.IsFinite(speed)) Speed = Math.Clamp(speed, _minimumSpeed, 40);
    }

    public void PlaceAt(float progress)
    {
        if (!float.IsFinite(progress) || _scenario == "imported") return;
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
        if (float.IsFinite(wheelSteps)) Speed = Math.Clamp(Speed * MathF.Pow(1.25f, wheelSteps), _minimumSpeed, 40);
    }
    public void SetSpeed(float speed) { if (float.IsFinite(speed)) Speed = Math.Clamp(speed, _minimumSpeed, 40); }

    public void Move(float forward, float right, float deltaSeconds, bool fast, float up = 0)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0 || !float.IsFinite(forward) ||
            !float.IsFinite(right) || !float.IsFinite(up)) return;
        var sideways = new Vector3(MathF.Sin(Yaw), -MathF.Cos(Yaw), 0);
        var heading = KeepAltitude ? new Vector3(MathF.Cos(Yaw), MathF.Sin(Yaw), 0) : Forward;
        var direction = heading * forward + sideways * right + Vector3.UnitZ * up;
        if (direction.LengthSquared() > 1) direction = Vector3.Normalize(direction);
        var step = direction * Speed * (fast ? FastMultiplier : 1) * Math.Min(deltaSeconds, .1f);
        Position += step;
        DistanceTravelled += step.Length();
    }
}

/// <summary>Eye-height motion restricted to observed floor triangles and preview wall collisions.</summary>
public sealed class IndoorNavigator(Vector3 start, Vector3 direction, TriangleIndex obstacles, TriangleIndex floor, float speed = 2.5f) : ICameraNavigator
{
    private readonly FlyNavigator _view = new("imported", start, direction);
    public Vector3 Position { get; private set; } = start;
    public Vector3 Forward => _view.Forward;
    public float DistanceTravelled { get; private set; }
    public float Speed { get; set; } = speed;
    public float FastMultiplier { get; set; } = 3;
    public void PlaceAt(float progress) { }
    public void Look(float yawDelta, float pitchDelta) => _view.Look(yawDelta, pitchDelta);
    public void Move(float forward, float right, float deltaSeconds, bool fast, float up = 0)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0 || !float.IsFinite(forward) || !float.IsFinite(right) || !float.IsFinite(Speed)) return;
        var heading = new Vector3(MathF.Cos(_view.Yaw), MathF.Sin(_view.Yaw), 0);
        var sideways = new Vector3(heading.Y, -heading.X, 0);
        var motion = heading * forward + sideways * right;
        if (motion.LengthSquared() < 1e-10f) return;
        if (motion.LengthSquared() > 1) motion = Vector3.Normalize(motion);
        motion *= Math.Clamp(Speed, .5f, 8) * (fast && float.IsFinite(FastMultiplier) ? Math.Clamp(FastMultiplier, 1, 5) : 1) * Math.Min(deltaSeconds, .1f);
        var pieces = Math.Max(1, (int)Math.Ceiling(motion.Length() / .08f));
        for (var i = 0; i < pieces; i++)
        {
            var target = Position + motion / pieces;
            if (!floor.Cast(target + Vector3.UnitZ * .3f, -Vector3.UnitZ, 2.2f, out var ground)) break;
            target.Z = ground.Z + 1.65f;
            if (Math.Abs(target.Z - Position.Z) > .25f) break;
            var step = target - Position; var length = step.Length();
            if (length < 1e-8f) break;
            var blocked = false;
            foreach (var lower in new[] { 0f, -.7f, -1.45f })
                if (obstacles.Cast(Position + Vector3.UnitZ * lower, step / length, length + .18f, out _)) { blocked = true; break; }
            if (blocked) break;
            Position = target; DistanceTravelled += length;
        }
    }
}
