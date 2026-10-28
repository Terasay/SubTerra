using System.IO;
using SubTerra.Models;
using SubTerra.Services;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class InterfaceStateTests
{
    [Fact]
    public Task FullFrameProgressDoesNotClaimCoverageOfAnUnvisitedGap() => ScanControlTests.OnSta(async () =>
    {
        var settings = new ScanSettings(20, 32, 20);
        ScannerAdjustment[] adjustments = [new(4, .75f)];
        var source = new SimulatedScanSource();
        var frames = new List<ScanFrame>();
        await foreach (var frame in source.ReadFramesAsync(new("aurora", settings, Adjustments: adjustments), CancellationToken.None))
            frames.Add(frame);
        var session = new ScanSession { ScenarioId = "aurora", Settings = settings, Frames = frames.ToArray(), Adjustments = adjustments };
        var path = TemporaryPath();
        try
        {
            var storage = new SessionStorage();
            await storage.SaveAsync(path, session, frames.Count);
            using var vm = new MainViewModel(source, storage);
            await vm.OpenAsync(path);
            Assert.False(vm.HasNotification, vm.Notification);
            Assert.Equal(100, vm.Progress);
            Assert.InRange(vm.CoveragePercent, 44.9, 45.1);
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public Task MarkersCanBeVisitedRepeatedlyAndFlightKeepsThePointCloud() => ScanControlTests.OnSta(async () =>
    {
        using var vm = new MainViewModel(new SmallSource(), new SessionStorage());
        await vm.LoadDemoAsync();
        Assert.Equal("Демо", vm.SessionStateText);
        vm.PlaybackFrame = 3;
        Assert.Equal("История", vm.SessionStateText);
        Assert.Equal(100, vm.CaptureProgress);
        Assert.Equal(50, vm.Progress);
        vm.LatestCommand.Execute(null);
        vm.PointsCommand.Execute(null);
        var points = vm.Cloud;
        var position = vm.Camera.Position;
        vm.ExploreCommand.Execute(null);
        Assert.True(vm.IsFlying && vm.IsNavigating && vm.IsPoints);
        Assert.False(vm.IsWalking || vm.IsOverview);
        Assert.Equal(position, vm.Camera.Position);
        Assert.Same(points, vm.Cloud);
        var requests = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.WalkTargetProgress)) requests++; };
        var marker = vm.VisibleMarkers.First();
        vm.NavigateMarkerCommand.Execute(marker);
        vm.NavigateMarkerCommand.Execute(marker);
        Assert.Equal(2, requests);
        vm.Home();
        Assert.False(vm.IsNavigating);
        Assert.True(vm.IsPoints && vm.IsOverview);
        Assert.True(vm.Camera.LookDirection.Length > 10);
    });

    [Fact]
    public Task SaveUpdatesTheCurrentFileWhileSaveAsChoosesAnotherPath() => ScanControlTests.OnSta(async () =>
    {
        var first = TemporaryPath(); var second = TemporaryPath();
        var interaction = new TestInteraction { SavePath = first };
        using var vm = new MainViewModel(new SmallSource(), new SessionStorage(), access: new TestAccess(), interaction: interaction);
        try
        {
            await vm.LoadDemoAsync();
            Assert.True(await vm.SaveCurrentAsync());
            Assert.Equal(1, interaction.SavePathPrompts);
            interaction.SavePath = second;
            Assert.True(await vm.SaveCurrentAsync());
            Assert.Equal(1, interaction.SavePathPrompts);
            Assert.False(File.Exists(second));
            Assert.True(await vm.SaveCurrentAsync(saveAs: true));
            Assert.Equal(2, interaction.SavePathPrompts);
            Assert.True(File.Exists(first) && File.Exists(second));
            Assert.Equal(Path.GetFileName(second), vm.SessionTitle);
            Assert.Equal("Сохранено", vm.SaveStateText);
        }
        finally { File.Delete(first); File.Delete(second); }
    });

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(100)]
    public Task ImportedRangeRemainsExactAndThePartialScanCanResume(float range) => ScanControlTests.OnSta(async () =>
    {
        var storage = new SessionStorage();
        var source = new SimulatedScanSource();
        var data = await source.GenerateAsync(ScenarioDefinition.All[0], new(range, 32, 6), null, CancellationToken.None);
        var path = TemporaryPath();
        try
        {
            await storage.SaveAsync(path, data.Session, 3);
            using var vm = new MainViewModel(source, storage, access: new TestAccess());
            await vm.OpenAsync(path);
            Assert.Equal(range, vm.Range);
            Assert.False(vm.HasPendingSettings);
            Assert.Equal("Продолжить сканирование", vm.StartText);
        }
        finally { File.Delete(path); }
    });

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"subterra-ui-{Guid.NewGuid():N}.subterra");
    private sealed class SmallSource : IScanSource
    {
        public IAsyncEnumerable<ScanFrame> ReadFramesAsync(ScanRequest request, CancellationToken cancellationToken) =>
            new SimulatedScanSource().ReadFramesAsync(request, cancellationToken);
        public Task<ScanDataset> GenerateAsync(ScenarioDefinition scenario, ScanSettings settings,
            IProgress<double>? progress, CancellationToken cancellationToken) => new SimulatedScanSource().GenerateAsync(
                scenario, settings with { RaysPerFrame = 32, FrameCount = 6 }, progress, cancellationToken);
    }
}
