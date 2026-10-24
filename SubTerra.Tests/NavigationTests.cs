using System.Numerics;
using SubTerra.Models;
using SubTerra.Navigation;
using SubTerra.Simulation;
using Xunit;

namespace SubTerra.Tests;

public sealed class NavigationTests
{
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
