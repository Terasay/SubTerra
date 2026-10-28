using System.Diagnostics;
using System.IO;
using System.Numerics;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class WorkspaceFeatureTests
{
    [Fact]
    public Task ExitKeepsTheInspectedViewAndHomeStillResetsIt() => ScanControlTests.OnSta(async () =>
    {
        using var vm = new MainViewModel(new MemoryScanSource(), new SessionStorage());
        await vm.LoadDemoAsync(); vm.PointsCommand.Execute(null); vm.FlyCommand.Execute(null);
        var navigator = vm.CreateFlyNavigator();
        navigator.Move(1, .4f, .1f, false, .2f); navigator.Look(.3f, -.2f);
        vm.UpdateWalkPosition(navigator);
        var position = vm.Camera.Position;
        var look = vm.Camera.LookDirection; look.Normalize();
        var fov = vm.Camera.FieldOfView;
        vm.ReturnToOverview();
        Assert.False(vm.IsNavigating);
        Assert.Equal(position, vm.Camera.Position);
        var actual = vm.Camera.LookDirection; actual.Normalize();
        Assert.InRange((actual - look).Length, 0, .000001);
        Assert.Equal(fov, vm.Camera.FieldOfView);
        Assert.InRange(vm.Camera.LookDirection.Length, 2, 15);
        vm.Home();
        Assert.NotEqual(position, vm.Camera.Position);
        Assert.Equal(47, vm.Camera.FieldOfView);
    });

    [Fact]
    public Task PreferencesAndRecentFilesPersistBetweenInstances() => ScanControlTests.OnSta(async () =>
    {
        var directory = TemporaryDirectory();
        try
        {
            var store = new WorkspaceStateStore(directory);
            var path = Path.Combine(directory, "saved.subterra");
            using (var vm = new MainViewModel(new MemoryScanSource(), new SessionStorage(), access: new TestAccess(), workspaceStore: store))
            {
                await vm.LoadDemoAsync();
                vm.MouseSensitivity = 1.8; vm.InvertMouseY = true; vm.WalkSpeed = 4; vm.FlightSpeed = 12;
                vm.FastMultiplier = 2; vm.AdaptiveQuality = false;
                Assert.True(await vm.SaveAsync(path));
                await vm.FlushPreferencesAsync();
            }
            using var restored = new MainViewModel(new MemoryScanSource(), new SessionStorage(), workspaceStore: new WorkspaceStateStore(directory));
            Assert.Equal(1.8, restored.MouseSensitivity); Assert.True(restored.InvertMouseY);
            Assert.Equal(4, restored.WalkSpeed); Assert.Equal(12, restored.FlightSpeed);
            Assert.Equal(2, restored.FastMultiplier); Assert.False(restored.AdaptiveQuality);
            Assert.Equal(path, Assert.Single(restored.RecentSessions).Path);
            File.WriteAllText(store.PreferencesPath, "{broken");
            Assert.Equal(1, store.LoadPreferences().MouseSensitivity);
        }
        finally { Directory.Delete(directory, true); }
    });

    [Fact]
    public Task RecoveryRestoresAnnotationsAndNeverOverwritesTheUserFileUntilSave() => ScanControlTests.OnSta(async () =>
    {
        var directory = TemporaryDirectory();
        var path = Path.Combine(directory, "original.subterra");
        var storage = new SessionStorage();
        try
        {
            var store = new WorkspaceStateStore(directory);
            using (var vm = new MainViewModel(new MemoryScanSource(), storage, access: new TestAccess(), workspaceStore: store))
            {
                await vm.LoadDemoAsync(); await vm.SaveAsync(path);
                vm.BeginInspection(false); vm.SelectInspectionPoint(new(1, 2, 3)); vm.AnnotationName = "Ниша";
                vm.AddAnnotationCommand.Execute(null);
                await vm.AutoSaveAsync();
                Assert.True(store.HasRecovery);
                Assert.Empty((await storage.LoadAsync(path)).Annotations);
            }
            using (var restored = new MainViewModel(new MemoryScanSource(), storage, access: new TestAccess(), workspaceStore: new WorkspaceStateStore(directory)))
            {
                Assert.True(restored.HasRecovery);
                await restored.LoadDemoAsync();
                Assert.True(store.HasRecovery);
                Assert.False(restored.CanStart);
                restored.BeginInspection(false); restored.SelectInspectionPoint(Vector3.Zero);
                Assert.False(restored.CanAddAnnotation);
                await restored.RestoreRecoveryAsync();
                Assert.False(restored.HasNotification, restored.Notification);
                Assert.True(restored.HasUnsavedChanges);
                Assert.False(restored.HasRecovery);
                Assert.Equal("Ниша", Assert.Single(restored.UserAnnotations).Name);
                Assert.Equal(Path.GetFileName(path), restored.SessionTitle);
                Assert.Empty((await storage.LoadAsync(path)).Annotations);
                Assert.True(await restored.SaveCurrentAsync());
                Assert.Single((await storage.LoadAsync(path)).Annotations);
                Assert.False(store.HasRecovery);
            }
        }
        finally { Directory.Delete(directory, true); }
    });

    [Fact]
    public Task MovingPreviewIsBoundedAndRestoresTheCachedFullCloud() => ScanControlTests.OnSta(async () =>
    {
        using var vm = new MainViewModel(new MemoryScanSource(40, 1000), new SessionStorage());
        await vm.LoadDemoAsync(); vm.PointsCommand.Execute(null);
        var full = vm.Cloud;
        Assert.Equal(40_000, vm.RenderedPointCount);
        vm.NotifyCameraInteraction();
        Assert.True(vm.IsCameraMoving);
        Assert.Equal(15_000, vm.RenderedPointCount);
        await Task.Delay(300);
        vm.SettleCameraInteraction();
        Assert.False(vm.IsCameraMoving);
        Assert.Equal(40_000, vm.RenderedPointCount);
        Assert.Same(full, vm.Cloud);
    });

    [Fact]
    public void FrameTimingMeasuresRenderedCadenceAndRecognizesIdle()
    {
        var timing = new FrameTiming();
        var start = Stopwatch.Frequency;
        for (var i = 0; i < 90; i++) timing.Frame(start + i * Stopwatch.Frequency / 60);
        var snapshot = timing.Snapshot(start + 89 * Stopwatch.Frequency / 60);
        Assert.NotNull(snapshot);
        Assert.InRange(snapshot.Value.Fps, 59.9, 60.1);
        Assert.InRange(snapshot.Value.FrameMilliseconds, 16.6, 16.8);
        Assert.Null(timing.Snapshot(start + 3 * Stopwatch.Frequency));
    }

    [Fact]
    public async Task RecoveryMetadataMustMatchItsArchiveBeforeReusingAnOriginalPath()
    {
        var directory = TemporaryDirectory();
        try
        {
            var store = new WorkspaceStateStore(directory);
            var data = await new MemoryScanSource().GenerateAsync(ScenarioDefinition.All[0], new(), null, CancellationToken.None);
            await store.SaveRecoveryAsync(new SessionStorage(), data.Session, Path.Combine(directory, "original.subterra"));
            Assert.NotNull(store.RecoveryOriginalPath());
            // A crash between writing the archive and its metadata must force Save As.
            await File.AppendAllTextAsync(store.RecoveryPath, "different generation");
            Assert.Null(store.RecoveryOriginalPath());
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static string TemporaryDirectory() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"subterra-workspace-{Guid.NewGuid():N}")).FullName;
    internal sealed class MemoryScanSource(int count = 6, int rays = 32) : IScanSource
    {
        public Task<ScanDataset> GenerateAsync(ScenarioDefinition scenario, ScanSettings settings, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            var frames = Enumerable.Range(0, count).Select(frame => new ScanFrame(DateTimeOffset.UnixEpoch.AddMilliseconds(frame * 100),
                new(CaveFactory.Center(scenario.Id, (frame + 1f) / count), Quaternion.Identity), (frame + 1f) / count,
                Enumerable.Range(0, rays).Select(i => new ScanPoint(frame + i / (float)rays, i % 7, 0, 1)).ToArray())).ToArray();
            return Task.FromResult(new ScanDataset(CaveFactory.Create(scenario), new ScanSession
                { ScenarioId = scenario.Id, Settings = new(20, rays, count), Frames = frames }));
        }
        public IAsyncEnumerable<ScanFrame> ReadFramesAsync(ScanRequest request, CancellationToken cancellationToken) => new SimulatedScanSource().ReadFramesAsync(request, cancellationToken);
    }
}
