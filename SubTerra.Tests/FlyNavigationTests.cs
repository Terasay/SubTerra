using System.Numerics;
using SubTerra.Navigation;
using SubTerra.Simulation;
using Xunit;

namespace SubTerra.Tests;

public sealed class FlyNavigationTests
{
    [Theory]
    [InlineData(-1.3f)]
    [InlineData(1.3f)]
    public void LevelFlightSeparatesHorizontalMovementFromLookAndVerticalKeys(float pitch)
    {
        var flight = new FlyNavigator("imported", new(2, 3, 1.65f), Vector3.UnitX) { KeepAltitude = true };
        flight.Look(.3f, pitch);
        for (var i = 0; i < 60; i++) flight.Move(1, .5f, 1f / 60, false);
        Assert.Equal(1.65f, flight.Position.Z); Assert.True(flight.DistanceTravelled > 5);
        flight.Move(0, 0, .1f, false, 1); Assert.InRange(flight.Position.Z, 2.249f, 2.251f);
        flight.Move(0, 0, .1f, false, -1); Assert.InRange(flight.Position.Z, 1.649f, 1.651f);
        flight.KeepAltitude = false;
        var height = flight.Position.Z; flight.Move(1, 0, .1f, false);
        Assert.Equal(Math.Sign(pitch), Math.Sign(flight.Position.Z - height));
    }
    [Fact]
    public void IndoorWalkKeepsEyeHeightAndCannotCrossObservedWallsOrMissingFloor()
    {
        Vector3[] p = [new(0, 0, 0), new(2, 0, 0), new(2, 2, 0), new(0, 2, 0),
            new(1.5f, 0, 0), new(1.5f, 2, 0), new(1.5f, 2, 3), new(1.5f, 0, 3)];
        var floor = new TriangleIndex(p, [0, 1, 2, 0, 2, 3]);
        var walls = new TriangleIndex(p, [4, 5, 6, 4, 6, 7]);
        var walk = new IndoorNavigator(new(.5f, 1, 1.65f), Vector3.UnitX, walls, floor);
        walk.Look(0, .9f);
        for (var i = 0; i < 100; i++) walk.Move(1, 0, .1f, true, 1);
        Assert.InRange(walk.Position.X, 1.1f, 1.33f); Assert.Equal(1.65f, walk.Position.Z, 4);
        var holeFloor = new TriangleIndex([new(0, 0, 0), new(1, 0, 0), new(1, 2, 0), new(0, 2, 0)], [0, 1, 2, 0, 2, 3]);
        var limited = new IndoorNavigator(new(.5f, 1, 1.65f), Vector3.UnitX, walls, holeFloor);
        for (var i = 0; i < 100; i++) limited.Move(1, 0, .1f, true);
        Assert.InRange(limited.Position.X, .8f, 1.001f);
        var before = limited.Position; limited.Move(float.NaN, 1, .1f, false);
        Assert.Equal(before, limited.Position);
    }
    [Theory]
    [InlineData(.001f)]
    [InlineData(1)]
    [InlineData(100)]
    public void SpeedDoesNotDependOnOrbitTargetDistance(float lookDistance)
    {
        var flight = new FlyNavigator("aurora", Vector3.Zero, Vector3.UnitX * lookDistance);
        for (var i = 0; i < 60; i++) flight.Move(1, 0, 1f / 60, false);
        Assert.InRange(flight.Position.X, 5.999f, 6.001f);
        Assert.Equal(0, flight.Position.Y);
        Assert.Equal(0, flight.Position.Z);
    }

    [Fact]
    public void FlightCanCrossWallsAndMoveVerticallyWithoutStalling()
    {
        var flight = new FlyNavigator("aurora", Vector3.Zero, Vector3.UnitX);
        for (var i = 0; i < 600; i++) flight.Move(0, 1, 1f / 60, false);
        Assert.InRange(flight.Position.Y, -60.001f, -59.999f);
        for (var i = 0; i < 60; i++) flight.Move(0, 0, 1f / 60, false, 1);
        Assert.InRange(flight.Position.Z, 5.999f, 6.001f);
        flight.Look(0, .5f);
        var height = flight.Position.Z;
        flight.Move(1, 0, .1f, false);
        Assert.True(flight.Position.Z > height);
    }

    [Fact]
    public void DiagonalsBoostSpeedLimitsAndInvalidInputAreBounded()
    {
        var straight = new FlyNavigator("aurora", Vector3.Zero, Vector3.UnitX);
        var diagonal = new FlyNavigator("aurora", Vector3.Zero, Vector3.UnitX);
        straight.Move(1, 0, .05f, false);
        diagonal.Move(1, 1, .05f, false, 1);
        Assert.InRange(Math.Abs(straight.DistanceTravelled - diagonal.DistanceTravelled), 0, .00001f);
        var before = diagonal.Position;
        diagonal.Move(float.NaN, 1, .1f, false);
        diagonal.Move(1, 0, float.PositiveInfinity, false);
        diagonal.Move(1, 0, .1f, false, float.NaN);
        Assert.Equal(before, diagonal.Position);
        diagonal.ChangeSpeed(1000);
        Assert.Equal(40, diagonal.Speed);
        diagonal.ChangeSpeed(-1000);
        Assert.Equal(.5f, diagonal.Speed);
        diagonal.Move(1, 0, 100, true);
        Assert.InRange(Vector3.Distance(before, diagonal.Position), .149f, .151f);
    }
}
