using System.Numerics;
using System.Runtime.CompilerServices;
using SubTerra.Models;
using SubTerra.Simulation;

namespace SubTerra.Navigation;

public enum TunnelMotionBlock { None, MissingGround, SteepGround, InsufficientSupport, Obstacle, Step }

/// <summary>Immutable collision data for observed triangles. Completed triangles never provide support.</summary>
public sealed class TunnelWalkSurface
{
    private static readonly ConditionalWeakTable<InspectionPoint[], ConditionalWeakTable<int[], TunnelWalkSurface>> Cache = new();
    internal const float MinimumGroundNormalZ = .7071067f;
    internal readonly TriangleIndex Obstacles, Ground;
    internal readonly float InitialSearchDepth;
    public bool HasGround { get; }
    private TunnelWalkSurface(TunnelReconstruction mesh, CancellationToken token)
    {
        var positions = new Vector3[mesh.Vertices.Length]; var minimum = float.MaxValue; var maximum = float.MinValue;
        for (var i = 0; i < positions.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            positions[i] = mesh.Vertices[i].Position; minimum = Math.Min(minimum, positions[i].Z); maximum = Math.Max(maximum, positions[i].Z);
        }
        var ground = new List<int>();
        for (var i = 0; i < mesh.ObservedTriangles.Length; i += 3)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var a = mesh.ObservedTriangles[i]; var b = mesh.ObservedTriangles[i + 1]; var c = mesh.ObservedTriangles[i + 2];
            var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            if (n.LengthSquared() < 1e-12f || Math.Abs(n.Z) / n.Length() < MinimumGroundNormalZ) continue;
            ground.Add(a); ground.Add(b); ground.Add(c);
        }
        HasGround = ground.Count > 0;
        InitialSearchDepth = Math.Clamp(maximum - minimum + 1, 2, 100);
        token.ThrowIfCancellationRequested();
        Obstacles = new(positions, mesh.ObservedTriangles, token);
        token.ThrowIfCancellationRequested(); Ground = new(positions, ground.ToArray(), token); token.ThrowIfCancellationRequested();
    }
    public static TunnelWalkSurface Prepare(TunnelReconstruction mesh, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Cache.GetOrCreateValue(mesh.Vertices).GetValue(mesh.ObservedTriangles, _ => new(mesh, token));
    }
    public static TunnelWalkSurface? Prepared(TunnelReconstruction mesh) =>
        Cache.TryGetValue(mesh.Vertices, out var variants) && variants.TryGetValue(mesh.ObservedTriangles, out var surface) ? surface : null;

    public bool TryCreate(Vector3 seed, Vector3 direction, out TunnelNavigator? navigator)
    {
        navigator = null;
        if (!HasGround || !InspectionPoint.From(seed).IsValid || !InspectionPoint.From(direction).IsValid) return false;
        var heading = new Vector3(direction.X, direction.Y, 0);
        if (heading.LengthSquared() < 1e-8f) heading = Vector3.UnitX; else heading = Vector3.Normalize(heading);
        // End sections can lie at an open boundary: search nearby inside the same passage.
        ReadOnlySpan<float> offsets = [0, .4f, -.4f, .8f, -.8f, 1.2f, -1.2f, 2, -2];
        foreach (var offset in offsets)
        {
            var candidate = new TunnelNavigator(this, seed + heading * offset, direction);
            if (!candidate.TryPlace(seed + heading * offset)) continue;
            navigator = candidate; return true;
        }
        return false;
    }
}

/// <summary>Substepped capsule motion, observed ground support, headroom, wall sliding and bounded steps.</summary>
public sealed class TunnelNavigator : ICameraNavigator
{
    public const float EyeHeight = 1.65f, Radius = .25f, BodyHeight = 1.85f;
    public const float MaximumStepUp = .22f, MaximumDrop = .25f;
    private readonly TunnelWalkSurface _surface;
    private readonly FlyNavigator _view;
    public Vector3 Position { get; private set; }
    public Vector3 Forward => _view.Forward;
    public float DistanceTravelled { get; private set; }
    public float Speed { get; set; } = 2.5f;
    public float FastMultiplier { get; set; } = 3;
    public TunnelMotionBlock BlockedBy { get; private set; }
    public Vector3 BlockedAt { get; private set; }
    public string MotionHint => BlockedBy switch
    {
        TunnelMotionBlock.MissingGround or TunnelMotionBlock.InsufficientSupport => "нет наблюдаемой опоры",
        TunnelMotionBlock.SteepGround => "слишком крутая поверхность", TunnelMotionBlock.Obstacle => "препятствие или низкий свод",
        TunnelMotionBlock.Step => "слишком высокий перепад", _ => ""
    };
    internal TunnelNavigator(TunnelWalkSurface surface, Vector3 start, Vector3 direction)
    { _surface = surface; _view = new("imported", start, direction); Position = start; }
    public void PlaceAt(float progress) { }
    public void Look(float yawDelta, float pitchDelta) => _view.Look(yawDelta, pitchDelta);
    internal bool TryPlace(Vector3 seed)
    {
        if (!_surface.Ground.Cast(seed + Vector3.UnitZ * .1f, -Vector3.UnitZ, _surface.InitialSearchDepth, out var floor, out _)) return false;
        if (!Stand(floor, out var foot)) return false;
        Position = foot + Vector3.UnitZ * EyeHeight; return true;
    }
    private bool Stand(Vector3 foot, out Vector3 supported)
    {
        supported = default;
        BlockedAt = foot;
        if (!_surface.Ground.Cast(foot + Vector3.UnitZ * (MaximumStepUp + .05f), -Vector3.UnitZ,
            MaximumStepUp + MaximumDrop + .05f, out var floor, out var normal)) { BlockedBy = TunnelMotionBlock.MissingGround; return false; }
        var vertical = Math.Abs(normal.Z);
        if (vertical < TunnelWalkSurface.MinimumGroundNormalZ) { BlockedBy = TunnelMotionBlock.SteepGround; return false; }
        if (Math.Abs(floor.Z - foot.Z) > Math.Max(MaximumStepUp, MaximumDrop) + .001f) { BlockedBy = TunnelMotionBlock.Step; return false; }
        var bottom = Radius / vertical + .025f;
        // Support the footprint on both sides; a centre ray alone would walk across narrow gaps.
        ReadOnlySpan<Vector2> probes = [new(1, 0), new(-1, 0), new(0, 1), new(0, -1), new(.707107f, .707107f),
            new(-.707107f, .707107f), new(.707107f, -.707107f), new(-.707107f, -.707107f)];
        foreach (var probe in probes)
        {
            var origin = floor + new Vector3(probe.X, probe.Y, 0) * (Radius * .85f) + Vector3.UnitZ * .35f;
            if (!_surface.Ground.Cast(origin, -Vector3.UnitZ, .7f, out var side, out var sideNormal))
            { BlockedBy = TunnelMotionBlock.InsufficientSupport; BlockedAt = origin; return false; }
            if (Math.Abs(sideNormal.Z) < TunnelWalkSurface.MinimumGroundNormalZ || Math.Abs(side.Z - floor.Z) > .3f)
            { BlockedBy = TunnelMotionBlock.Step; BlockedAt = origin; return false; }
            bottom = Math.Max(bottom, side.Z - floor.Z + Radius + .025f);
        }
        if (bottom >= BodyHeight - Radius || _surface.Obstacles.IntersectsCapsule(floor + Vector3.UnitZ * bottom,
            floor + Vector3.UnitZ * (BodyHeight - Radius), Radius)) { BlockedBy = TunnelMotionBlock.Obstacle; return false; }
        supported = floor; return true;
    }
    private bool TryStep(Vector3 horizontal)
    {
        if (horizontal.LengthSquared() < 1e-12f) return false;
        var foot = Position - Vector3.UnitZ * EyeHeight;
        if (!Stand(foot + horizontal, out var target)) return false;
        if (target.Z - foot.Z > MaximumStepUp || foot.Z - target.Z > MaximumDrop) { BlockedBy = TunnelMotionBlock.Step; return false; }
        var next = target + Vector3.UnitZ * EyeHeight;
        DistanceTravelled += Vector3.Distance(Position, next); Position = next; BlockedBy = TunnelMotionBlock.None; return true;
    }
    public void Move(float forward, float right, float deltaSeconds, bool fast, float up = 0)
    {
        if (!float.IsFinite(forward) || !float.IsFinite(right) || !float.IsFinite(up) || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0 ||
            !float.IsFinite(Speed) || !float.IsFinite(FastMultiplier)) return;
        var heading = new Vector3(MathF.Cos(_view.Yaw), MathF.Sin(_view.Yaw), 0);
        var motion = heading * forward + new Vector3(heading.Y, -heading.X, 0) * right;
        if (motion.LengthSquared() < 1e-12f) return;
        if (motion.LengthSquared() > 1) motion = Vector3.Normalize(motion);
        motion *= Math.Clamp(Speed, .5f, 8) * (fast ? Math.Clamp(FastMultiplier, 1, 5) : 1) * Math.Min(deltaSeconds, .05f);
        var pieces = Math.Max(1, (int)Math.Ceiling(motion.Length() / .05f)); var step = motion / pieces;
        for (var i = 0; i < pieces; i++)
        {
            if (TryStep(step)) continue;
            var moved = TryStep(new(step.X, 0, 0)); moved |= TryStep(new(0, step.Y, 0));
            if (!moved) break;
        }
    }
}
