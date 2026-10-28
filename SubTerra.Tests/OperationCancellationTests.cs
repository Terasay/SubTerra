using System.IO;
using SubTerra.Models;
using SubTerra.Services;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class OperationCancellationTests
{
    [Fact]
    public async Task CancelledExportPreservesDestinationAndRemovesTemporaryFile()
    {
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        var path = Path.Combine(directory, "cloud.ply");
        try
        {
            await File.WriteAllTextAsync(path, "previous export");
            using var cancellation = new CancellationTokenSource();
            var points = Enumerable.Range(0, 20_000).Select(i => new ScanPoint(i, 0, 0, 1));
            var progress = new ImmediateProgress(value => { if (value > 5) cancellation.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SessionStorage().ExportPlyAsync(path, points, cancellation.Token, progress));
            Assert.Equal("previous export", await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public Task CancellingAnUncooperativeGeneratorKeepsTheOldSessionAndRejectsLateResults() => ScanControlTests.OnSta(async () =>
    {
        var source = new BlockingSource();
        using var vm = new MainViewModel(source, new SessionStorage());
        await vm.LoadDemoAsync();
        var frames = vm.CapturedFrameCount;
        var scenario = vm.SelectedScenario;
        var pending = vm.LoadDemoAsync(ScenarioDefinition.All[1]);
        Assert.True(vm.CanCancelOperation);
        Assert.True(vm.BusyIsIndeterminate);
        vm.CancelOperation();
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(vm.IsBusy || vm.HasNotification);
        Assert.False(vm.BusyIsIndeterminate);
        Assert.Equal(frames, vm.CapturedFrameCount);
        source.Pending.TrySetResult(await new WorkspaceFeatureTests.MemoryScanSource().GenerateAsync(ScenarioDefinition.All[1], new(), null, CancellationToken.None));
        await Task.Delay(20);
        Assert.Equal(scenario, vm.SelectedScenario);
    });

    [Fact]
    public async Task CancelledSavePreservesThePreviousArchive()
    {
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        var path = Path.Combine(directory, "scan.subterra");
        try
        {
            var data = await new WorkspaceFeatureTests.MemoryScanSource().GenerateAsync(ScenarioDefinition.All[0], new(), null, CancellationToken.None);
            var storage = new SessionStorage();
            await storage.SaveAsync(path, data.Session, 3);
            var original = await File.ReadAllBytesAsync(path);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.SaveAsync(path, data.Session, 6, cancellation.Token,
                new ImmediateProgress(value => { if (value > 10) cancellation.Cancel(); })));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ImmediateProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    [Fact]
    public Task ClosingDuringGenerationCancelsWithoutWaitingForever() => ScanControlTests.OnSta(async () =>
    {
        var source = new BlockingSource();
        using var vm = new MainViewModel(source, new SessionStorage());
        await vm.LoadDemoAsync();
        var operation = vm.LoadDemoAsync(ScenarioDefinition.All[1]);
        Assert.True(await vm.RequestCloseAsync().WaitAsync(TimeSpan.FromSeconds(4)));
        await operation;
        Assert.False(vm.IsBusy);
    });
    private sealed class BlockingSource : IScanSource
    {
        private bool _first = true;
        public TaskCompletionSource<ScanDataset> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ScanDataset> GenerateAsync(ScenarioDefinition scenario, ScanSettings settings, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            if (!_first) return Pending.Task;
            _first = false;
            return new WorkspaceFeatureTests.MemoryScanSource().GenerateAsync(scenario, settings, progress, cancellationToken);
        }
        public IAsyncEnumerable<ScanFrame> ReadFramesAsync(ScanRequest request, CancellationToken cancellationToken) => new SimulatedScanSource().ReadFramesAsync(request, cancellationToken);
    }
}
