using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using SubTerra.Models;
using SubTerra.Services;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class SceneCompatibilityTests
{
    [Theory]
    [InlineData(SceneVersions.Legacy)]
    [InlineData(SceneVersions.Current)]
    public Task OpenResumeAndSavePreserveTheOriginalScene(int version) => ScanControlTests.OnSta(async () =>
    {
        var source = new SimulatedScanSource();
        var storage = new SessionStorage();
        var settings = new ScanSettings(20, 360, 6, FrameIntervalMilliseconds: 25);
        var frames = new List<ScanFrame>();
        await foreach (var frame in source.ReadFramesAsync(new("aurora", settings, SceneVersion: version), CancellationToken.None))
            frames.Add(frame);
        var session = new ScanSession { ScenarioId = "aurora", SceneVersion = version, Settings = settings, Frames = frames.ToArray() };
        var path = Path.Combine(Path.GetTempPath(), $"subterra-compat-{Guid.NewGuid():N}.subterra");
        try
        {
            await storage.SaveAsync(path, session, 2);
            var partial = await storage.LoadAsync(path);
            Assert.Equal(version, partial.SceneVersion);
            var map = new MapAccumulator(partial.ScenarioId, partial.Settings, partial.SceneVersion);
            foreach (var frame in partial.Frames) map.Append(frame);
            Assert.Equal(version, map.Snapshot().SceneVersion);
            using var vm = new MainViewModel(source, storage, access: new TestAccess(), interaction: new TestInteraction());
            await vm.OpenAsync(path);
            Assert.False(vm.HasNotification, vm.Notification);
            Assert.True(vm.CanStart);
            Assert.Equal(2, vm.CapturedFrameCount);
            await vm.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (vm.IsRunning) await Task.Delay(20, timeout.Token);
            Assert.False(vm.HasNotification, vm.Notification);
            Assert.Equal(6, vm.CapturedFrameCount);
            Assert.True(await vm.SaveAsync(path));
            var complete = await storage.LoadAsync(path);
            Assert.Equal(version, complete.SceneVersion);
            for (var i = 0; i < frames.Count; i++) Assert.Equal(frames[i].Points, complete.Frames[i].Points);
            if (version == SceneVersions.Legacy)
            {
                // A completely new pass upgrades the geometry; resuming never does.
                vm.Range = 19;
                await vm.StartAsync();
                while (vm.IsRunning) await Task.Delay(20, timeout.Token);
                Assert.True(await vm.SaveAsync(path));
                Assert.Equal(SceneVersions.Current, (await storage.LoadAsync(path)).SceneVersion);
            }
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public async Task UnknownSceneVersionIsRejectedBeforeReadingFrames()
    {
        var source = new SimulatedScanSource();
        var data = await source.GenerateAsync(ScenarioDefinition.All[0], new(20, 32, 3), null, CancellationToken.None);
        var path = Path.Combine(Path.GetTempPath(), $"subterra-version-{Guid.NewGuid():N}.subterra");
        try
        {
            var storage = new SessionStorage();
            await storage.SaveAsync(path, data.Session, 2);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("manifest.json")!;
                JsonNode json;
                using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(reader.ReadToEnd())!;
                json["SceneVersion"] = 999;
                entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
                writer.Write(json.ToJsonString());
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => storage.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }
}
