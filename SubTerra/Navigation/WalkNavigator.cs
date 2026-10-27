using System.Numerics;
using SubTerra.Models;
using SubTerra.Simulation;

namespace SubTerra.Navigation;

/// <summary>Floor following and wall sliding, independent of WPF and mouse input.</summary>
public sealed class WalkNavigator
{
    public const float EyeHeight = 1.65f;
    public const float Radius = .28f;
    private readonly CaveScene _scene;
    private readonly TriangleIndex _collision;
    private readonly float _availableProgress;
    private readonly float _startProgress;
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public float DistanceTravelled { get; private set; }
    public Vector3 Forward => new(MathF.Cos(Yaw) * MathF.Cos(Pitch), MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch));

    public WalkNavigator(CaveScene scene, float availableProgress = 1, float startProgress = 0)
    {
        _scene = scene;
        _availableProgress = Math.Clamp(availableProgress, .02f, 1);
        _startProgress = Math.Clamp(startProgress, 0, Math.Max(0, _availableProgress - .02f));
        var indices = new List<int>();
        for (var i = 0; i < scene.Indices.Length; i += 3)
        {
            var a = scene.Indices[i]; var b = scene.Indices[i+1]; var c = scene.Indices[i+2];
            if (Math.Max(scene.VertexProgress[a], Math.Max(scene.VertexProgress[b], scene.VertexProgress[c])) <= _availableProgress &&
                Math.Min(scene.VertexProgress[a], Math.Min(scene.VertexProgress[b], scene.VertexProgress[c])) >= _startProgress)
                indices.AddRange([a,b,c]);
        }
        _collision = new TriangleIndex(scene.Vertices, indices.ToArray());
        PlaceAt(_startProgress + .045f);
    }

    public void PlaceAt(float progress)
    {
        var t = Math.Clamp(progress, _startProgress + .008f, Math.Max(_startProgress + .008f, _availableProgress - .01f));
        var p = CaveFactory.Center(_scene.Scenario.Id, t);
        if (!Ground(p, out var grounded)) throw new InvalidOperationException("Не удалось найти пол для прогулки.");
        Position = grounded;
        var basis = CaveFactory.Basis(_scene.Scenario.Id, t);
        Yaw = MathF.Atan2(basis.Forward.Y, basis.Forward.X);
        Pitch = 0;
    }

    public void Look(float yawDelta, float pitchDelta)
    {
        if (!float.IsFinite(yawDelta) || !float.IsFinite(pitchDelta)) return;
        Yaw = MathF.IEEERemainder(Yaw + yawDelta, MathF.Tau);
        Pitch = Math.Clamp(Pitch + pitchDelta, -1.28f, 1.28f);
    }

    public void Move(float forward, float right, float deltaSeconds, bool fast)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0) return;
        var direction = new Vector2(MathF.Cos(Yaw), MathF.Sin(Yaw));
        var movement = direction * forward + new Vector2(direction.Y, -direction.X) * right;
        if (movement.LengthSquared() > 1) movement = Vector2.Normalize(movement);
        var requested = movement * (fast ? 4.8f : 2.5f) * Math.Min(deltaSeconds, .05f);
        var old = Position;
        // Separate axes allow the camera to slide along a wall instead of stopping abruptly.
        TryMove(new Vector3(requested.X, 0, 0));
        TryMove(new Vector3(0, requested.Y, 0));
        DistanceTravelled += Vector3.Distance(old, Position);
    }

    private void TryMove(Vector3 step)
    {
        var length = step.Length();
        if (length < 1e-6f) return;
        var direction = step / length;
        foreach (var z in new[] { -1.12f, -.45f, .08f })
        {
            if (_collision.Cast(Position + Vector3.UnitZ * z, direction, length + Radius, out _)) return;
        }
        var candidate = Position + step;
        if (!Ground(candidate, out var grounded) || MathF.Abs(grounded.Z - Position.Z) > .38f) return;
        if (_collision.Cast(grounded, Vector3.UnitZ, .22f, out _)) return;
        Position = grounded;
    }

    private bool Ground(Vector3 candidate, out Vector3 grounded)
    {
        grounded = default;
        if (!_collision.Cast(candidate + Vector3.UnitZ * .12f, -Vector3.UnitZ, 12, out var floor)) return false;
        grounded = new(candidate.X, candidate.Y, floor.Z + EyeHeight);
        return true;
    }
}
