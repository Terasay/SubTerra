using System.Numerics;
using SubTerra.Navigation;
using Xunit;

namespace SubTerra.Tests;

public sealed class FlyNavigationTests
{
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
