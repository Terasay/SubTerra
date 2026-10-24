using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class StreamingTests
{
    [Fact]
    public async Task StreamAndResumedStreamMatchExistingBatchMeasurements()
    {
        var source = new SimulatedScanSource();
        var settings = new ScanSettings(17, 64, 12, 713, .35f, 50);
        var data = await source.GenerateAsync(ScenarioDefinition.All[0], settings, null, CancellationToken.None);
        foreach (var first in new[] { 0, 5, 12 })
        {
            var frames = new List<ScanFrame>();
            await foreach (var frame in source.ReadFramesAsync(new("aurora", settings, first,
                data.Session.Frames[0].Timestamp), CancellationToken.None)) frames.Add(frame);
            Assert.Equal(12 - first, frames.Count);
            for (var i = 0; i < frames.Count; i++)
            {
                var expected = data.Session.Frames[first + i];
                Assert.Equal(expected.Points, frames[i].Points);
                Assert.Equal(expected.Pose, frames[i].Pose);
                Assert.Equal(expected.Timestamp, frames[i].Timestamp);
                Assert.Equal(expected.Progress, frames[i].Progress);
            }
        }
    }

    [Fact]
    public async Task CancelledStreamDoesNotYieldAnotherFrame()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new SimulatedScanSource().ReadFramesAsync(new("aurora", new(20, 32, 8)),
            cancellation.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public async Task InvalidStreamStartIsRejected(int first)
    {
        await using var stream = new SimulatedScanSource().ReadFramesAsync(new("aurora", new(20, 32, 8), first),
            CancellationToken.None).GetAsyncEnumerator();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await stream.MoveNextAsync());
    }

    [Fact]
    public void MapCommitsAtomicallyAndRejectsDuplicateOrInvalidFrames()
    {
        var settings = new ScanSettings(20, 32, 3);
        var map = new MapAccumulator("aurora", settings);
        var request = new ScanRequest("aurora", settings);
        var first = Frame(request, 0);
        map.Append(first);
        var snapshot = map.Snapshot();
        Assert.Throws<InvalidDataException>(() => map.Append(first));
        Assert.Throws<InvalidDataException>(() => map.Append(Frame(request, 1) with { Points = [new(float.NaN, 0, 0, 1)] }));
        Assert.Throws<InvalidDataException>(() => map.Append(Frame(request, 1) with { Pose = new(Vector3.Zero, default) }));
        Assert.Equal(1, map.FrameCount);
        Assert.Equal(1, map.PointCount);
        map.Append(Frame(request, 1));
        map.Append(Frame(request, 2));
        Assert.Single(snapshot.Frames);
        Assert.Equal(3, map.Snapshot().Frames.Length);
        Assert.Throws<InvalidDataException>(() => map.Append(Frame(request, 2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrowingPreviewNeverUsesFuturePointsAndHistoryStaysReproducible(bool cut)
    {
        var points = Enumerable.Range(0, 20003).Select(i => new ScanPoint(i, 0, i % 2 == 0 ? 0 : 2, 1)).ToArray();
        var sampler = new StreamingPointCloudSampler();
        sampler.Append(points.Take(1001).ToArray());
        var history = sampler.Select(1001, cut, 200);
        sampler.Append(points.Skip(1001).ToArray());
        Assert.Equal(history, sampler.Select(1001, cut, 200));
        var current = sampler.Select(points.Length, cut, 200);
        Assert.Equal(200, current.Length);
        Assert.Equal(0, current[0]);
        Assert.Equal(20002, current[^1]);
        Assert.Equal(200, current.Distinct().Count());
        Assert.All(history, i => Assert.InRange(i, 0, 1000));
        if (cut) Assert.All(current, i => Assert.True(points[i].Z <= .8f));
        Assert.Empty(sampler.Select(0, cut, 200));
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.Select(100, cut, 0));
    }

    [Fact]
    public void AppendedGeometryRetainsHistoryAndMatchingColors()
    {
        var scene = CaveFactory.Create(ScenarioDefinition.All[0]);
        var settings = new ScanSettings(20, 32, 3);
        var request = new ScanRequest("aurora", settings);
        var geometry = new SceneGeometry(scene);
        geometry.AppendFrame(Frame(request, 0));
        var history = geometry.Cloud(1, false);
        geometry.AppendFrame(Frame(request, 1));
        var current = geometry.Cloud(2, false);
        Assert.NotNull(history.Positions);
        Assert.NotNull(history.Colors);
        Assert.NotNull(current.Positions);
        Assert.NotNull(current.Colors);
        Assert.Single(history.Positions);
        Assert.Equal(2, current.Positions.Count);
        Assert.Equal(current.Positions.Count, current.Colors.Count);
        Assert.Equal(history.Positions[0], current.Positions[0]);
        Assert.Equal(history.Colors[0], current.Colors[0]);
        Assert.Same(current, geometry.Cloud(2, false));
    }

    [Fact]
    public Task LoadedPartialSessionContinuesWithoutReplacingMeasurementsOrSettings() => ScanControlTests.OnSta(async () =>
    {
        var source = new SimulatedScanSource();
        var storage = new SessionStorage();
        var settings = new ScanSettings(17, 64, 12, 713, .35f, 50);
        var data = await source.GenerateAsync(ScenarioDefinition.All[0], settings, null, CancellationToken.None);
        var path = Path.Combine(Path.GetTempPath(), $"subterra-stream-{Guid.NewGuid():N}.subterra");
        try
        {
            await storage.SaveAsync(path, data.Session, 4);
            using var vm = new MainViewModel(source, storage, access: new TestAccess(), interaction: new TestInteraction());
            await vm.OpenAsync(path);
            Assert.Equal(4, vm.CapturedFrameCount);
            Assert.False(vm.HasPendingSettings);
            Assert.Equal("Продолжить сканирование", vm.StartText);
            vm.PlaybackFrame = 1;
            vm.Speed = 3;
            await vm.StartAsync();
            Assert.Equal(4, vm.PlaybackFrame);
            await Until(() => !vm.IsRunning);
            Assert.False(vm.HasNotification, vm.Notification);
            Assert.Equal(12, vm.CapturedFrameCount);
            await vm.SaveAsync(path);
            var restored = await storage.LoadAsync(path);
            Assert.Equal(settings, restored.Settings);
            for (var i = 0; i < 12; i++)
            {
                Assert.Equal(data.Session.Frames[i].Points, restored.Frames[i].Points);
                Assert.Equal(data.Session.Frames[i].Pose, restored.Frames[i].Pose);
                Assert.Equal(data.Session.Frames[i].Timestamp.ToUnixTimeMilliseconds(), restored.Frames[i].Timestamp.ToUnixTimeMilliseconds());
            }
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public Task PauseAndClearRejectLateFramesEvenWhenSourceIgnoresCancellation() => ScanControlTests.OnSta(async () =>
    {
        var source = new GatedSource();
        using var vm = new MainViewModel(source, new SessionStorage(), access: new TestAccess(), interaction: new TestInteraction());
        vm.Speed = 3;
        vm.FrameIntervalMilliseconds = 25;
        await vm.StartAsync();
        var first = await source.Next();
        Assert.Equal(0, vm.CapturedFrameCount);
        first.Completion.SetResult(Frame(first.Request, first.Index));
        var late = await source.Next();
        Assert.Equal(1, vm.CapturedFrameCount);
        vm.Pause();
        var path = Path.Combine(Path.GetTempPath(), $"subterra-paused-{Guid.NewGuid():N}.subterra");
        try
        {
            await vm.SaveAsync(path);
            Assert.Single((await new SessionStorage().LoadAsync(path)).Frames);
        }
        finally { File.Delete(path); }
        late.Completion.SetResult(Frame(late.Request, late.Index));
        await Task.Delay(100);
        Assert.Equal(1, vm.CapturedFrameCount);
        Assert.False(vm.IsRunning);
        vm.PlaybackFrame = 0;
        await vm.StartAsync();
        Assert.Equal(1, vm.PlaybackFrame);
        var resumed = await source.Next();
        Assert.Equal(1, resumed.Index);
        resumed.Completion.SetResult(Frame(resumed.Request, resumed.Index));
        late = await source.Next();
        vm.StopCommand.Execute(null);
        Assert.Equal(2, vm.CapturedFrameCount);
        await vm.ClearCommand.ExecuteAsync();
        late.Completion.SetResult(Frame(late.Request, late.Index));
        await Task.Delay(100);
        Assert.Equal(0, vm.CapturedFrameCount);
        Assert.Equal("0", vm.PointCountText);
        Assert.False(vm.CanSave);
        Assert.False(vm.HasNotification, vm.Notification);
    });

    [Fact]
    public Task UnresponsiveSourceDoesNotLeaveControlsBusyForever() => ScanControlTests.OnSta(async () =>
    {
        var source = new GatedSource();
        using var vm = new MainViewModel(source, new SessionStorage(), access: new TestAccess(), interaction: new TestInteraction());
        await vm.StartAsync();
        var pending = await source.Next();
        vm.Pause();
        try
        {
            await vm.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsRunning);
            Assert.True(vm.HasNotification);
            Assert.Equal(0, vm.CapturedFrameCount);
        }
        finally { pending.Completion.TrySetResult(Frame(pending.Request, pending.Index)); }
    });

    [Fact]
    public Task SourceFailureKeepsCommittedMapAvailableForSaving() => ScanControlTests.OnSta(async () =>
    {
        using var vm = new MainViewModel(new FailingSource(), new SessionStorage(), access: new TestAccess());
        await vm.StartAsync();
        await Until(() => !vm.IsRunning);
        Assert.Equal(1, vm.CapturedFrameCount);
        Assert.True(vm.HasNotification);
        Assert.Contains("Тестовый обрыв", vm.Notification);
        Assert.True(vm.CanSave);
        var path = Path.Combine(Path.GetTempPath(), $"subterra-failure-{Guid.NewGuid():N}.subterra");
        try
        {
            await vm.SaveAsync(path);
            Assert.Single((await new SessionStorage().LoadAsync(path)).Frames);
        }
        finally { File.Delete(path); }
    });

    private static ScanFrame Frame(ScanRequest request, int index) => new(
        (request.FirstTimestamp ?? DateTimeOffset.UnixEpoch).AddMilliseconds(index * request.Settings.FrameIntervalMilliseconds),
        new(new Vector3(index, 0, 0), Quaternion.Identity),
        request.Settings.StartProgress + (1 - request.Settings.StartProgress) * (index + 1f) / request.Settings.FrameCount,
        [new(index, 0, 0, 1)]);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Ожидание завершения потока превысило 8 секунд.");
            await Task.Delay(10);
        }
    }

    private sealed record Pending(ScanRequest Request, int Index, TaskCompletionSource<ScanFrame> Completion);
    private sealed class GatedSource : IScanSource
    {
        private readonly Channel<Pending> _pending = Channel.CreateUnbounded<Pending>();
        public Task<Pending> Next() => _pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public Task<ScanDataset> GenerateAsync(ScenarioDefinition scenario, ScanSettings settings,
            IProgress<double>? progress, CancellationToken cancellationToken) => throw new Exception("Новый проход не должен рассчитываться заранее.");
        public async IAsyncEnumerable<ScanFrame> ReadFramesAsync(ScanRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = request.FirstFrame; i < request.Settings.FrameCount; i++)
            {
                var completion = new TaskCompletionSource<ScanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Writer.TryWrite(new(request, i, completion));
                // Deliberately non-cooperative: verifies the consumer's stale-frame barrier.
                yield return await completion.Task;
            }
        }
    }

    private sealed class FailingSource : IScanSource
    {
        public Task<ScanDataset> GenerateAsync(ScenarioDefinition scenario, ScanSettings settings,
            IProgress<double>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<ScanFrame> ReadFramesAsync(ScanRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Frame(request, 0);
            await Task.Yield();
            throw new IOException("Тестовый обрыв источника");
        }
    }
}
