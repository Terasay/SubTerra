using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using SubTerra.Models;
using SubTerra.Services;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class SessionProtectionTests
{
    [Fact]
    public Task GuestCanViewButCannotMutateEvenThroughDirectCalls() => ScanControlTests.OnSta(async () =>
    {
        var source = new ShortSource();
        using var vm = new MainViewModel(source, new SessionStorage(), access: new TestAccess(false));
        await vm.LoadDemoAsync();
        var count = vm.CapturedFrameCount;
        var range = vm.Range;
        var density = vm.DensityIndex;
        vm.Range = 30; vm.DensityIndex = 2; vm.StartPosition = 50; vm.Speed = 3;
        vm.FrameIntervalMilliseconds = 50;
        Assert.Equal(range, vm.Range);
        Assert.Equal(density, vm.DensityIndex);
        Assert.Equal(0, vm.StartPosition);
        Assert.Equal(1, vm.Speed);
        Assert.Equal(100, vm.FrameIntervalMilliseconds);
        Assert.False(vm.CanStart); Assert.False(vm.CanSave); Assert.False(vm.CanRepositionScanner);
        Assert.False(vm.ClearCommand.CanExecute(null));
        await vm.ClearCommand.ExecuteAsync(); await vm.ClearAsync(); await vm.StartAsync();
        await vm.ApplyScannerAdjustmentAsync();
        var path = TemporaryPath();
        try
        {
            Assert.False(await vm.SaveAsync(path));
            await vm.ExportAsync(path);
            Assert.False(File.Exists(path));
            Assert.Equal(count, vm.CapturedFrameCount);
            Assert.Equal(0, source.Streams);
            vm.PointsCommand.Execute(null); Assert.True(vm.IsPoints);
            vm.PlaybackFrame = 2; Assert.Equal(2, vm.PlaybackFrame);
            vm.SurfaceCommand.Execute(null); Assert.True(vm.IsCave);
            Assert.True(vm.CanWalk);
            Assert.False(vm.HasUnsavedChanges);
            var imported = await new SimulatedScanSource().GenerateAsync(ScenarioDefinition.All[1], new(17, 32, 6), null, CancellationToken.None);
            await new SessionStorage().SaveAsync(path, imported.Session, 3);
            await vm.OpenAsync(path);
            Assert.False(vm.HasNotification, vm.Notification);
            Assert.Equal(3, vm.CapturedFrameCount);
            Assert.Equal("mine", vm.SelectedScenario.Id);
            Assert.Equal(17, vm.Range);
            Assert.False(vm.CanSave);
        }
        finally { File.Delete(path); }
    });

    [Theory]
    [InlineData("close")]
    [InlineData("logout")]
    [InlineData("clear")]
    [InlineData("new")]
    [InlineData("scene")]
    [InlineData("open")]
    public Task CancelPreservesDirtyMapForEveryReplacement(string action) => ScanControlTests.OnSta(async () =>
    {
        var interaction = new TestInteraction { Decision = UnsavedDecision.Cancel };
        using var vm = new MainViewModel(new ShortSource(), new SessionStorage(), access: new TestAccess(), interaction: interaction);
        await Record(vm);
        var recording = vm.RecordingText;
        var scenario = vm.SelectedScenario;
        var frames = vm.CapturedFrameCount;
        switch (action)
        {
            case "close": Assert.False(await vm.RequestCloseAsync()); break;
            case "logout": Assert.False(await vm.LogoutAsync()); break;
            case "clear": await vm.ClearAsync(); break;
            case "new": vm.Range = 25; await vm.StartAsync(); break;
            case "scene": await vm.LoadDemoAsync(ScenarioDefinition.All[1]); break;
            case "open": await vm.OpenAsync("does-not-exist.subterra"); break;
        }
        Assert.Equal(1, interaction.Prompts);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(frames, vm.CapturedFrameCount);
        Assert.Equal(recording, vm.RecordingText);
        Assert.Equal(scenario, vm.SelectedScenario);
        Assert.True(vm.IsAdministrator);
        Assert.False(vm.HasNotification, vm.Notification);
    });

    [Fact]
    public Task SaveBeforeCloseIncludesHiddenFramesAndCancelPathKeepsData() => ScanControlTests.OnSta(async () =>
    {
        var interaction = new TestInteraction { Decision = UnsavedDecision.Save };
        using var vm = new MainViewModel(new ShortSource(), new SessionStorage(), access: new TestAccess(), interaction: interaction);
        await Record(vm);
        vm.PlaybackFrame = 1;
        Assert.False(await vm.RequestCloseAsync());
        Assert.True(vm.HasUnsavedChanges);
        var path = TemporaryPath();
        try
        {
            interaction.SavePath = path;
            Assert.True(await vm.RequestCloseAsync());
            Assert.False(vm.HasUnsavedChanges);
            Assert.Equal(vm.CapturedFrameCount, (await new SessionStorage().LoadAsync(path)).Frames.Length);
            await vm.StartAsync();
            await Until(() => !vm.IsRunning);
            Assert.True(vm.HasUnsavedChanges);
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public Task FailedSaveOrOpenKeepsDirtyMapAndDiscardCanClearIt() => ScanControlTests.OnSta(async () =>
    {
        var interaction = new TestInteraction { Decision = UnsavedDecision.Save,
            SavePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no-directory.subterra") };
        using var vm = new MainViewModel(new ShortSource(), new SessionStorage(), access: new TestAccess(), interaction: interaction);
        await Record(vm);
        Assert.False(await vm.RequestCloseAsync());
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.HasNotification);
        var count = vm.CapturedFrameCount;
        interaction.Decision = UnsavedDecision.Discard;
        await vm.OpenAsync("does-not-exist.subterra");
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(count, vm.CapturedFrameCount);
        await vm.ClearAsync();
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(0, vm.CapturedFrameCount);
    });

    [Fact]
    public Task RevokingAccessStopsAnActiveStream() => ScanControlTests.OnSta(async () =>
    {
        var access = new TestAccess();
        using var vm = new MainViewModel(new ShortSource(), new SessionStorage(), access: access);
        vm.FrameIntervalMilliseconds = 1000;
        await vm.StartAsync();
        Assert.True(vm.IsRunning);
        Assert.Equal(1, vm.CapturedFrameCount);
        access.Logout();
        await Task.Delay(80);
        Assert.False(vm.IsRunning);
        Assert.False(vm.CanStart);
        Assert.False(vm.CanSave);
        Assert.Equal(1, vm.CapturedFrameCount);
        Assert.True(vm.HasUnsavedChanges);
        Assert.False(await vm.RequestCloseAsync());
    });

    private static async Task Record(MainViewModel vm)
    {
        vm.FrameIntervalMilliseconds = 25; vm.Speed = 3;
        await vm.StartAsync(); await Until(() => !vm.IsRunning);
        Assert.Equal(2, vm.CapturedFrameCount);
        Assert.True(vm.HasUnsavedChanges);
        vm.DismissNotificationCommand.Execute(null);
    }
    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!condition()) { Assert.True(DateTime.UtcNow < until); await Task.Delay(10); }
    }
    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"subterra-guard-{Guid.NewGuid():N}.subterra");
    private sealed class ShortSource : IScanSource
    {
        public int Streams { get; private set; }
        public Task<ScanDataset> GenerateAsync(ScenarioDefinition scenario, ScanSettings settings,
            IProgress<double>? progress, CancellationToken cancellationToken) =>
            new SimulatedScanSource().GenerateAsync(scenario, settings with { FrameCount = 6, RaysPerFrame = 32 }, progress, cancellationToken);
        public async IAsyncEnumerable<ScanFrame> ReadFramesAsync(ScanRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Streams++;
            for (var i = request.FirstFrame; i < request.FirstFrame + 2; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new((request.FirstTimestamp ?? DateTimeOffset.UnixEpoch).AddMilliseconds(i * request.Settings.FrameIntervalMilliseconds),
                    new(new Vector3(i, 0, 0), Quaternion.Identity), (i + 1f) / request.Settings.FrameCount, [new(i, 0, 0, .8f)]);
                await Task.Yield();
            }
            // A short source leaves a resumable map; tests exercise persistence after a source interruption.
            throw new IOException("Тестовое завершение потока");
        }
    }
}
