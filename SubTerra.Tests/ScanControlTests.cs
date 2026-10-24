using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using SubTerra.Models;
using SubTerra.Navigation;
using SubTerra.Services;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class ScanControlTests
{
    [Fact]
    public async Task SelectedStartChangesMeasurementsAndFrameTiming()
    {
        var source = new SimulatedScanSource();
        var settings = new ScanSettings(20, 64, 8, StartProgress: .55f, FrameIntervalMilliseconds: 200);
        var shifted = await source.GenerateAsync(ScenarioDefinition.All[0], settings, null, CancellationToken.None);
        var entrance = await source.GenerateAsync(ScenarioDefinition.All[0], settings with { StartProgress = 0 }, null, CancellationToken.None);
        Assert.True(Vector3.Distance(entrance.Session.Frames[0].Pose.Position, shifted.Session.Frames[0].Pose.Position) > 20);
        Assert.NotEqual(entrance.Session.Frames[0].Points, shifted.Session.Frames[0].Points);
        for (var i = 0; i < shifted.Session.Frames.Length; i++)
        {
            var frame = shifted.Session.Frames[i];
            Assert.InRange(frame.Progress, .55f, 1);
            Assert.All(frame.Points, point => Assert.InRange(Vector3.Distance(point.Position, frame.Pose.Position), .01f, 20.02f));
            if (i > 0) Assert.Equal(200, (frame.Timestamp - shifted.Session.Frames[i - 1].Timestamp).TotalMilliseconds);
        }
    }

    [Theory]
    [InlineData(-.1f, 100)]
    [InlineData(.9f, 100)]
    [InlineData(float.NaN, 100)]
    [InlineData(0, 0)]
    [InlineData(0, 2000)]
    public async Task InvalidStartOrCadenceIsRejected(float start, int interval)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new SimulatedScanSource().GenerateAsync(
            ScenarioDefinition.All[0], new(20, 32, 4, StartProgress: start, FrameIntervalMilliseconds: interval),
            null, CancellationToken.None));
    }

    [Fact]
    public void WalkCannotLeaveTheSelectedSection()
    {
        var scene = CaveFactory.Create(ScenarioDefinition.All[0]);
        var walker = new WalkNavigator(scene, .75f, .5f);
        Assert.True(walker.Position.X >= CaveFactory.Center("aurora", .5f).X);
        walker.PlaceAt(0);
        Assert.True(walker.Position.X > CaveFactory.Center("aurora", .5f).X);
        for (var i = 0; i < 100; i++) walker.Move(-1, 0, .05f, true);
        Assert.True(walker.Position.X >= CaveFactory.Center("aurora", .5f).X - .3f);
    }

    [Fact]
    public Task SeekingAndPendingSettingsNeverDiscardRecordedFrames() => OnSta(async () =>
    {
        var source = new ShortSource();
        using var vm = new MainViewModel(source, new SessionStorage(), access: new TestAccess(), interaction: new TestInteraction());
        vm.StartPosition = 40;
        vm.FrameIntervalMilliseconds = 200;
        await vm.LoadDemoAsync();
        Assert.Equal(6, vm.CapturedFrameCount);
        var fullPoints = vm.PointCountText;
        vm.PlaybackFrame = 2;
        Assert.Equal(2, vm.PlaybackFrame);
        Assert.True(vm.IsReviewing);
        Assert.NotEqual(fullPoints, vm.PointCountText);
        Assert.Equal("00:00.4", vm.DurationText);
        vm.PlaybackFrame = 0;
        Assert.Equal("0", vm.PointCountText);
        Assert.True(vm.CanSave);
        vm.StartPosition = 10;
        Assert.True(vm.HasPendingSettings);
        var path = Path.Combine(Path.GetTempPath(), $"subterra-timeline-{Guid.NewGuid():N}.subterra");
        try
        {
            await vm.SaveAsync(path);
            var loaded = await new SessionStorage().LoadAsync(path);
            Assert.Equal(6, loaded.Frames.Length);
            Assert.Equal(.4f, loaded.Settings.StartProgress);
            Assert.Equal(200, loaded.Settings.FrameIntervalMilliseconds);
            for (var i = 0; i < 6; i++) Assert.Equal(source.Data!.Session.Frames[i].Points, loaded.Frames[i].Points);
        }
        finally { File.Delete(path); }
        vm.LatestCommand.Execute(null);
        Assert.Equal(6, vm.PlaybackFrame);
        Assert.False(vm.IsReviewing);
        await vm.ClearCommand.ExecuteAsync();
        Assert.Equal(0, vm.CapturedFrameCount);
        Assert.Equal(0, vm.PlaybackFrame);
    });

    [Fact]
    public async Task EarlierSessionsUseDefaultStartAndCadence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"subterra-compat-{Guid.NewGuid():N}.subterra");
        try
        {
            var data = await new SimulatedScanSource().GenerateAsync(ScenarioDefinition.All[0],
                new(20, 32, 4), null, CancellationToken.None);
            var storage = new SessionStorage();
            await storage.SaveAsync(path, data.Session, 4);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("manifest.json")!;
                JsonObject manifest;
                using (var reader = new StreamReader(entry.Open())) manifest = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
                var settings = manifest["Settings"]!.AsObject();
                settings.Remove("StartProgress"); settings.Remove("FrameIntervalMilliseconds");
                entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
                writer.Write(manifest.ToJsonString());
            }
            var restored = await storage.LoadAsync(path);
            Assert.Equal(0, restored.Settings.StartProgress);
            Assert.Equal(100, restored.Settings.FrameIntervalMilliseconds);
            Assert.Equal(data.Session.Frames[2].Points, restored.Frames[2].Points);
        }
        finally { File.Delete(path); }
    }

    private sealed class ShortSource : IScanSource
    {
        public IAsyncEnumerable<ScanFrame> ReadFramesAsync(ScanRequest request, CancellationToken cancellationToken) =>
            new SimulatedScanSource().ReadFramesAsync(request, cancellationToken);
        public ScanDataset? Data { get; private set; }
        public async Task<ScanDataset> GenerateAsync(ScenarioDefinition scenario, ScanSettings settings,
            IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Data = await new SimulatedScanSource().GenerateAsync(scenario,
                settings with { RaysPerFrame = 32, FrameCount = 6 }, progress, cancellationToken);
            return Data;
        }
    }

    [Fact]
    public Task QualityChangesOnlyThePreview() => OnSta(async () =>
    {
        using var vm = new MainViewModel(new PreviewSource(), new SessionStorage(), access: new TestAccess());
        vm.DensityIndex = 2;
        vm.CutRoof = false;
        await vm.LoadDemoAsync();
        var frames = vm.CapturedFrameCount;
        var recording = vm.RecordingText;
        var points = vm.PointCountText;
        vm.PointsCommand.Execute(null);
        Assert.InRange(vm.RenderedPointCount, 1, 60_000);
        vm.PointQuality = PointCloudQuality.All[0];
        Assert.Equal(25_000, vm.RenderedPointCount);
        vm.PointQuality = PointCloudQuality.All[2];
        Assert.True(vm.RenderedPointCount > 60_000);
        Assert.Equal(frames, vm.CapturedFrameCount);
        Assert.Equal(recording, vm.RecordingText);
        Assert.Equal(points, vm.PointCountText);
        vm.SurfaceCommand.Execute(null);
        Assert.Equal(0, vm.RenderedPointCount);
    });

    private sealed class PreviewSource : IScanSource
    {
        public IAsyncEnumerable<ScanFrame> ReadFramesAsync(ScanRequest request, CancellationToken cancellationToken) =>
            new SimulatedScanSource().ReadFramesAsync(request, cancellationToken);
        public Task<ScanDataset> GenerateAsync(ScenarioDefinition scenario, ScanSettings settings,
            IProgress<double>? progress, CancellationToken cancellationToken) => new SimulatedScanSource().GenerateAsync(
                scenario, settings with { FrameCount = 60 }, progress, cancellationToken);
    }

    internal static Task OnSta(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
