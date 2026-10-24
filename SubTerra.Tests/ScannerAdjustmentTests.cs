using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json.Nodes;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class ScannerAdjustmentTests
{
    [Fact]
    public Task RepositionSaveReloadAndResumePreservePrefixAndExactMeasurements() => ScanControlTests.OnSta(async () =>
    {
        var source = new SimulatedScanSource();
        var storage = new SessionStorage();
        var data = await source.GenerateAsync(ScenarioDefinition.All[0], new(20, 64, 12, 731, 0, 25), null, CancellationToken.None);
        var path = TemporaryPath();
        try
        {
            await storage.SaveAsync(path, data.Session, 3);
            using (var vm = new MainViewModel(source, storage, access: new TestAccess()))
            {
                await vm.OpenAsync(path);
                Assert.True(vm.CanRepositionScanner);
                Assert.False(vm.HasUnsavedChanges);
                vm.ScannerPosition = 73; vm.ScannerLateral = .25; vm.ScannerHeight = .1; vm.ScannerYaw = 50;
                Assert.False(vm.HasUnsavedChanges); // Preview alone is not a session edit.
                await vm.ApplyScannerAdjustmentAsync();
                Assert.False(vm.HasNotification, vm.Notification);
                Assert.True(vm.HasUnsavedChanges);
                Assert.Equal(3, vm.CapturedFrameCount);
                Assert.True(await vm.SaveAsync(path));
            }
            var partial = await storage.LoadAsync(path);
            var adjustment = Assert.Single(partial.Adjustments);
            Assert.Equal(3, adjustment.FirstFrame);
            Assert.Equal(.73f, adjustment.RoutePosition);
            Assert.Equal(50, adjustment.YawDegrees);
            for (var i = 0; i < 3; i++) Assert.Equal(data.Session.Frames[i].Points, partial.Frames[i].Points);
            using (var restored = new MainViewModel(source, storage, access: new TestAccess()))
            {
                await restored.OpenAsync(path);
                Assert.InRange(restored.ScannerPosition, 72.99, 73.01);
                Assert.Equal(50, restored.ScannerYaw);
                Assert.Equal("Позиция следующего кадра", restored.ScannerCaption);
                restored.Speed = 3;
                await restored.StartAsync();
                var until = DateTime.UtcNow.AddSeconds(8);
                while (restored.IsRunning) { Assert.True(DateTime.UtcNow < until); await Task.Delay(10); }
                Assert.False(restored.HasNotification, restored.Notification);
                Assert.Equal(12, restored.CapturedFrameCount);
                Assert.True(await restored.SaveAsync(path));
                restored.InsideCommand.Execute(null);
                Assert.True(restored.IsWalking);
                Assert.All(restored.VisibleMarkers, marker => Assert.True(marker.Progress >= .73f));
                Assert.True(float.IsFinite(restored.CreateNavigator().Position.Z));
            }
            var full = await storage.LoadAsync(path);
            var expected = new List<ScanFrame>();
            await foreach (var frame in source.ReadFramesAsync(new("aurora", data.Session.Settings, 0,
                               partial.Frames[0].Timestamp, partial.Adjustments), CancellationToken.None)) expected.Add(frame);
            for (var i = 0; i < full.Frames.Length; i++)
            {
                Assert.Equal(expected[i].Points, full.Frames[i].Points);
                Assert.Equal(expected[i].Pose, full.Frames[i].Pose);
                Assert.Equal(expected[i].Timestamp, full.Frames[i].Timestamp);
            }
            Assert.NotEqual(data.Session.Frames[3].Pose, full.Frames[3].Pose);
            Assert.InRange(Vector3.Distance(full.Frames[3].Pose.Position,
                ScannerPlan.Pose("aurora", .73f, adjustment).Position), 0, .0001f);
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public void JumpDoesNotRevealUnvisitedGapAndBacktrackingMergesCoverage()
    {
        var settings = new ScanSettings(FrameCount: 20);
        ScannerAdjustment[] plan = [new(4, .75f), new(8, .1f)];
        var pending = ScannerPlan.Coverage(settings, plan, 4);
        Assert.Single(pending);
        Assert.InRange(pending[0].End, .199f, .201f);
        var jumped = ScannerPlan.Coverage(settings, plan, 8);
        Assert.Equal(2, jumped.Length);
        Assert.True(jumped[0].End < .3f);
        Assert.Equal(.75f, jumped[1].Start);
        var scene = CaveFactory.Create(ScenarioDefinition.All[0]);
        var geometry = new SceneGeometry(scene);
        var mesh = geometry.Surface(jumped, false);
        Assert.NotNull(mesh.Indices);
        Assert.NotEmpty(mesh.Indices);
        for (var i = 0; i < mesh.Indices.Count; i += 3)
        {
            var min = Enumerable.Range(i, 3).Min(j => scene.VertexProgress[mesh.Indices[j]]);
            var max = Enumerable.Range(i, 3).Max(j => scene.VertexProgress[mesh.Indices[j]]);
            Assert.Contains(jumped, range => min >= range.Start && max <= range.End);
        }
        var finished = ScannerPlan.Coverage(settings, plan, 20);
        Assert.Single(finished);
        Assert.Equal(0, finished[0].Start);
        Assert.InRange(finished[0].End, .99f, 1f);
    }

    [Fact]
    public void ScannerClearanceRejectsWallCrossing()
    {
        var scene = CaveFactory.Create(ScenarioDefinition.All[0]);
        Assert.True(ScannerPlan.IsClear(scene, new(4, .73f)));
        // Deliberately beyond the UI limits: the collision check must reject crossing the cave shell.
        Assert.False(ScannerPlan.IsClear(scene, new(4, .73f, 100)));
    }

    [Theory]
    [InlineData(-1, .5f, 0)]
    [InlineData(12, .5f, 0)]
    [InlineData(3, float.NaN, 0)]
    [InlineData(3, .5f, 181)]
    public void InvalidAdjustmentIsRejected(int first, float route, float yaw)
    {
        Assert.Throws<InvalidDataException>(() => ScannerPlan.Validate([new(first, route, YawDegrees: yaw)], new(FrameCount: 12)));
    }

    [Fact]
    public async Task ArchiveRejectsPlanThatRewritesUncapturedFutureAndSupportsZeroHits()
    {
        var path = TemporaryPath();
        try
        {
            var settings = new ScanSettings(1, 32, 12);
            var session = new ScanSession { ScenarioId = "aurora", Settings = settings,
                Frames = [new(DateTimeOffset.UnixEpoch, new(Vector3.Zero, Quaternion.Identity), 1f / 12, [])],
                Adjustments = [new(1, .7f)] };
            var storage = new SessionStorage();
            await storage.SaveAsync(path, session, 1);
            Assert.Equal(0, (await storage.LoadAsync(path)).PointCount);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("manifest.json")!;
                JsonNode metadata;
                using (var reader = new StreamReader(entry.Open())) metadata = JsonNode.Parse(reader.ReadToEnd())!;
                metadata["Adjustments"]![0]!["FirstFrame"] = 2;
                entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
                writer.Write(metadata.ToJsonString());
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => storage.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"subterra-position-{Guid.NewGuid():N}.subterra");
}
