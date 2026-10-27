using System.Numerics;
using SubTerra.Models;
using SubTerra.Navigation;
using SubTerra.Simulation;
using Xunit;

namespace SubTerra.Tests;

public sealed class MouseLookTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void StationaryMouseDoesNotTiltCameraWhileRunning(double dpi)
    {
        var input = new MouseLookInput();
        // An odd viewport dimension and fractional origin reproduce the old drift.
        input.SetCenter((-1920 + 901 / 2.0) * dpi, (73.25 + 537 / 2.0) * dpi, dpi, dpi);
        var walker = new WalkNavigator(CaveFactory.Create(ScenarioDefinition.All[0]));
        walker.Look(.12f, .18f);
        var yaw = walker.Yaw;
        var pitch = walker.Pitch;
        for (var frame = 0; frame < 2000; frame++)
        {
            var delta = input.Read(input.CenterX, input.CenterY);
            Assert.Equal(Vector2.Zero, delta);
            walker.Look(-delta.X * .0025f, -delta.Y * .0025f);
            walker.Move(1, 0, 1f / 60, true);
        }
        Assert.Equal(yaw, walker.Yaw);
        Assert.Equal(pitch, walker.Pitch);
    }

    [Fact]
    public void RealMotionIsScaledAndRecenteringAddsNoMotion()
    {
        var input = new MouseLookInput();
        Assert.True(input.SetCenter(-701.5, 410.25, 1.5, 1.5));
        Assert.Equal(new Vector2(8, -4), input.Read(input.CenterX + 12, input.CenterY - 6));
        Assert.Equal(Vector2.Zero, input.Read(input.CenterX, input.CenterY));
        Assert.False(input.SetCenter(-701.5, 410.25, 1.5, 1.5));
        Assert.True(input.SetCenter(801.5, 310.5, 1, 1));
        Assert.Equal(Vector2.Zero, input.Read(input.CenterX, input.CenterY));
        input.Reset();
        Assert.Equal(Vector2.Zero, input.Read(0, 0));
        Assert.True(input.SetCenter(801.5, 310.5, 1, 1));
    }
}
