using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HelixToolkit.Wpf.SharpDX;
using SubTerra.Models;
using SubTerra.Navigation;
using SubTerra.Services;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class WalkInputTests
{
    [Fact]
    public Task HelixKeysCannotDisplaceCameraWhileRunningOrAfterReenteringWalk() => ScanControlTests.OnSta(async () =>
    {
        using var vm = new MainViewModel(new SmallSource(), new SessionStorage());
        await vm.LoadDemoAsync();
        using var viewport = new Viewport3DX { Camera = vm.Camera, IsInertiaEnabled = false };
        var window = new Window { Content = viewport };
        using var input = new WalkController(window, viewport, vm);
        for (var visit = 0; visit < 2; visit++)
        {
            input.Start();
            var walker = vm.CreateNavigator();
            for (var frame = 0; frame < 120; frame++)
            {
                walker.Move(1, 0, 1f / 60, true);
                vm.UpdateWalkPosition(walker);
                var expectedPosition = vm.Camera.Position;
                var expectedLook = vm.Camera.LookDirection;
                // Exercise Helix's actual key handler and deferred movement, even
                // if an event bypasses the window's preview handler.
                foreach (var key in new[] { Key.W, Key.Q, Key.Z, Key.PageUp, Key.PageDown })
                {
                    viewport.RaiseEvent(KeyDown(key));
                    viewport.Update(TimeSpan.FromSeconds(1 + frame / 60d));
                    Assert.Equal(expectedPosition, vm.Camera.Position);
                    Assert.Equal(expectedLook, vm.Camera.LookDirection);
                }
            }
            input.Stop();
            Assert.True(viewport.IsRotationEnabled && viewport.IsPanEnabled && viewport.IsZoomEnabled);
            Assert.True(viewport.IsChangeFieldOfViewEnabled);
        }
    });

    [Fact]
    public Task ReleasedWalkInputDoesNotConsumeSidebarKeys() => ScanControlTests.OnSta(async () =>
    {
        using var vm = new MainViewModel(new SmallSource(), new SessionStorage());
        await vm.LoadDemoAsync();
        using var viewport = new Viewport3DX { Camera = vm.Camera };
        var window = new Window { Content = new StackPanel { Children = { viewport, new TextBox() } } };
        using var input = new WalkController(window, viewport, vm);
        input.Start();
        foreach (var key in new[] { Key.W, Key.A, Key.S, Key.D, Key.Left, Key.Right, Key.Space, Key.Back, Key.Tab })
            Assert.False(input.HandleKeyDown(KeyDown(key)));
        Assert.False(vm.MouseCaptured);
    });

    private static KeyEventArgs KeyDown(Key key) => new(Keyboard.PrimaryDevice, new InputSource(), Environment.TickCount, key)
    { RoutedEvent = Keyboard.KeyDownEvent };

    private sealed class InputSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private sealed class SmallSource : IScanSource
    {
        public IAsyncEnumerable<ScanFrame> ReadFramesAsync(ScanRequest request, CancellationToken cancellationToken) =>
            new SimulatedScanSource().ReadFramesAsync(request, cancellationToken);

        public Task<ScanDataset> GenerateAsync(ScenarioDefinition scenario, ScanSettings settings,
            IProgress<double>? progress, CancellationToken cancellationToken) => new SimulatedScanSource().GenerateAsync(
                scenario, settings with { RaysPerFrame = 32, FrameCount = 2 }, progress, cancellationToken);
    }
}
