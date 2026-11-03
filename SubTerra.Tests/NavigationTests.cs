using System.Numerics;
using SubTerra.Models;
using SubTerra.Navigation;
using SubTerra.Simulation;
using SubTerra.Services;
using Xunit;

namespace SubTerra.Tests;

public sealed class NavigationTests
{
    private static TunnelReconstruction Corridor(float slope = .1f, float ceiling = 3, bool gap = false, bool lowBeam = false)
    {
        var vertices = new List<InspectionPoint>(); var observed = new List<int>(); var completed = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, bool inferred = false)
        {
            var start = vertices.Count; vertices.AddRange(new[] { a, b, c, d }.Select(InspectionPoint.From));
            (inferred ? completed : observed).AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
        void Floor(float from, float to, bool inferred = false) => Quad(new(from, -1.5f, from * slope), new(to, -1.5f, to * slope), new(to, 1.5f, to * slope), new(from, 1.5f, from * slope), inferred);
        if (gap) { Floor(0, 4); Floor(5, 12); Floor(4, 5, true); } else Floor(0, 12);
        Quad(new(0, -1.5f, ceiling), new(12, -1.5f, 12 * slope + ceiling), new(12, 1.5f, 12 * slope + ceiling), new(0, 1.5f, ceiling));
        foreach (var y in new[] { -1.5f, 1.5f }) Quad(new(0, y, 0), new(12, y, 12 * slope), new(12, y, 12 * slope + ceiling), new(0, y, ceiling));
        if (lowBeam) Quad(new(3, -1.5f, 3 * slope + 1.75f), new(4, -1.5f, 4 * slope + 1.75f), new(4, 1.5f, 4 * slope + 1.75f), new(3, 1.5f, 3 * slope + 1.75f));
        return new(new(), vertices.ToArray(), observed.ToArray(), completed.ToArray(), [], "");
    }

    [Theory]
    [InlineData(0)] [InlineData(.12f)] [InlineData(.7f)] [InlineData(1f)]
    public void TunnelWalkingFollowsObservedSlopeAtEyeHeight(float slope)
    {
        var surface = TunnelWalkSurface.Prepare(Corridor(slope));
        Assert.True(surface.TryCreate(new(1, 0, slope + 1.5f), Vector3.UnitX, out var walker));
        for (var i = 0; i < 100; i++) walker!.Move(1, 0, .02f, false);
        Assert.InRange(walker!.Position.X, 5.8f, 6.2f);
        Assert.InRange(walker.Position.Z - slope * walker.Position.X, TunnelNavigator.EyeHeight - .001f, TunnelNavigator.EyeHeight + .001f);
    }

    [Fact]
    public void TunnelCameraVolumeStopsAtWallsAndLowHeadroom()
    {
        Assert.False(TunnelWalkSurface.Prepare(Corridor(ceiling: 1.7f)).TryCreate(new(1, 0, 1), Vector3.UnitX, out _));
        var surface = TunnelWalkSurface.Prepare(Corridor(lowBeam: true));
        Assert.True(surface.TryCreate(new(1, 0, 1.5f), Vector3.UnitX, out var walker));
        for (var i = 0; i < 100; i++) walker!.Move(1, 0, .05f, true);
        Assert.InRange(walker!.Position.X, 2, 3);
        for (var i = 0; i < 100; i++) walker.Move(0, 1, .05f, true);
        Assert.InRange(walker.Position.Y, -1.26f, -1.1f);
        var stopped = walker.Position; walker.Move(0, 1, .05f, true); Assert.Equal(stopped, walker.Position);
    }

    [Fact]
    public void MissingObservedGroundIsNotReplacedByCompletedTrianglesOrLongFrames()
    {
        var mesh = Corridor(gap: true); var surface = TunnelWalkSurface.Prepare(mesh);
        Assert.True(surface.TryCreate(new(1, 0, 1.5f), Vector3.UnitX, out var walker));
        walker!.Speed = 8; walker.FastMultiplier = 5;
        for (var i = 0; i < 20; i++) walker.Move(1, 0, 10, true);
        Assert.InRange(walker.Position.X, 3.5f, 3.8f);
        Assert.NotEmpty(mesh.CompletedTriangles);
        var before = walker.Position;
        walker.Move(float.NaN, 0, .02f, true); walker.Move(1, float.PositiveInfinity, .02f, true); walker.Move(1, 0, float.NaN, true);
        Assert.Equal(before, walker.Position);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => TunnelWalkSurface.Prepare(mesh, cancellation.Token));
    }

    [Fact]
    public void CapsuleQueriesCatchWallsEdgesAndCeilingBetweenRayHeights()
    {
        var index = new TriangleIndex([new(0, -2, 0), new(0, 2, 0), new(0, 0, 3)], [0, 1, 2]);
        Assert.True(index.IntersectsCapsule(new(.2f, 0, .3f), new(.2f, 0, 1.6f), .25f));
        Assert.False(index.IntersectsCapsule(new(.4f, 0, .3f), new(.4f, 0, 1.6f), .25f));
        Assert.True(index.IntersectsCapsule(new(-1, 0, 1), new(1, 0, 1), .01f));
        Assert.True(index.Cast(new(1, 0, 1), -Vector3.UnitX, 2, out var point, out var normal));
        Assert.InRange(point.X, -.0001f, .0001f); Assert.InRange(Math.Abs(normal.X), .999f, 1.001f);
    }

    [Fact]
    public void TunnelMovementDoesNotAllocateAndUnsupportedSteepSurfaceCannotStartWalk()
    {
        Assert.False(TunnelWalkSurface.Prepare(Corridor(slope: 1.2f)).HasGround);
        var mesh = Corridor(); var surface = TunnelWalkSurface.Prepare(mesh);
        Assert.Same(surface, TunnelWalkSurface.Prepared(mesh));
        Assert.True(surface.TryCreate(new(1, 0, 1.5f), Vector3.UnitX, out var walker));
        for (var i = 0; i < 20; i++) walker!.Move(1, 0, .02f, false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 300; i++) walker!.Move(1, 0, .02f, true);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
    [Fact]
    public void InvalidInputDoesNotMoveCameraAndRunningDoesNotAllocatePerStep()
    {
        var walker = new WalkNavigator(CaveFactory.Create(ScenarioDefinition.All[0]));
        var initial = walker.Position;
        walker.Move(float.NaN, 1, .02f, true);
        walker.Move(1, float.PositiveInfinity, .02f, true);
        walker.Move(1, 0, float.NaN, true);
        Assert.Equal(initial, walker.Position);
        // Warm up the collision path before measuring steady-state allocations.
        for (var i = 0; i < 20; i++) walker.Move(1, 0, 1f / 60, true);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 300; i++) walker.Move(1, 0, 1f / 60, true);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData("aurora")]
    [InlineData("mine")]
    [InlineData("lava")]
    public void CameraFollowsFloorAlongEntireRoute(string id)
    {
        var scene = CaveFactory.Create(ScenarioDefinition.All.First(s => s.Id == id));
        var index = new TriangleIndex(scene.Vertices, scene.Indices);
        var walker = new WalkNavigator(scene);
        for (var t = .02f; t < .98f; t += .02f)
        {
            walker.PlaceAt(t);
            var initial = walker.Position;
            walker.Move(1, 0, .02f, false);
            Assert.True(Vector3.Distance(initial, walker.Position) > .015f);
            Assert.True(index.Cast(walker.Position, -Vector3.UnitZ, 5, out var floor));
            Assert.InRange(walker.Position.Z - floor.Z, WalkNavigator.EyeHeight - .002f, WalkNavigator.EyeHeight + .002f);
        }
    }

    [Fact]
    public void WallsAndUnscannedEndsStopTheCamera()
    {
        var scene = CaveFactory.Create(ScenarioDefinition.All[0]);
        var walker = new WalkNavigator(scene, .18f);
        walker.PlaceAt(.12f);
        // Walk sideways into the wall, then continue pressing against it.
        for (var i = 0; i < 120; i++) walker.Move(0, 1, .05f, true);
        var stopped = walker.Position;
        for (var i = 0; i < 20; i++) walker.Move(0, 1, .05f, true);
        Assert.InRange(Vector3.Distance(stopped, walker.Position), 0, .01f);
        Assert.InRange(Vector3.Distance(CaveFactory.Center("aurora", .12f), stopped), 1, 5);

        walker.PlaceAt(.17f);
        for (var i = 0; i < 150; i++) walker.Move(1, 0, .05f, true);
        Assert.True(walker.Position.X < CaveFactory.Center("aurora", .19f).X);
    }

    [Fact]
    public void DiagonalSpeedPitchAndLongFramesAreBounded()
    {
        var scene = CaveFactory.Create(ScenarioDefinition.All[0]);
        var straight = new WalkNavigator(scene);
        var diagonal = new WalkNavigator(scene);
        straight.Move(1, 0, .04f, false);
        diagonal.Move(1, 1, .04f, false);
        Assert.InRange(Math.Abs(straight.DistanceTravelled - diagonal.DistanceTravelled), 0, .005f);
        var before = diagonal.Position;
        diagonal.Move(1, 0, 10, true);
        Assert.InRange(Vector3.Distance(before, diagonal.Position), 0, .25f);
        diagonal.Look(1000, 1000);
        Assert.InRange(diagonal.Pitch, -1.28f, 1.28f);
        Assert.InRange(diagonal.Forward.Length(), .999f, 1.001f);
    }
}
