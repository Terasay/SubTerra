using System.IO;
using System.Numerics;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class InspectionTests
{
    [Fact]
    public Task MeasurementAndUserMarkersUseSelectedCoordinatesAndRespectGuestAccess() => ScanControlTests.OnSta(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"subterra-annotations-{Guid.NewGuid():N}.subterra");
        var storage = new SessionStorage();
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), storage, access: new TestAccess());
        try
        {
            await vm.LoadDemoAsync();
            vm.BeginInspection(true); vm.SelectInspectionPoint(Vector3.Zero); vm.SelectInspectionPoint(new(3, 4, 12));
            Assert.Contains("13", vm.MeasurementText);
            Assert.Equal(2, vm.MeasurementLine!.Positions!.Count);
            vm.AnnotationName = "Зал"; vm.AddAnnotationCommand.Execute(null);
            Assert.True(vm.HasUnsavedChanges);
            Assert.Equal(new Vector3(3, 4, 12), Assert.Single(vm.UserAnnotations).Point.Position);
            await vm.SaveAsync(path);
            var loaded = await storage.LoadAsync(path);
            Assert.Equal("Зал", Assert.Single(loaded.Annotations).Name);
            using var guest = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), storage, access: new TestAccess(false));
            await guest.OpenAsync(path); guest.BeginInspection(false); guest.SelectInspectionPoint(new(1, 1, 1));
            Assert.False(guest.CanAddAnnotation);
            guest.AddAnnotationCommand.Execute(null);
            Assert.Single(guest.UserAnnotations);
            Assert.False(guest.HasUnsavedChanges);
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public void ArbitraryClipFiltersBeforeSamplingAndLeavesSourcePointsUntouched()
    {
        var points = Enumerable.Range(0, 10_000).Select(i => new ScanPoint(i % 100 - 50, i / 100 - 50, 0, 1)).ToArray();
        var session = new ScanSession { ScenarioId = "aurora", Settings = new(), Frames = [new(DateTimeOffset.UnixEpoch, new(Vector3.Zero, Quaternion.Identity), 1, points)] };
        var geometry = new SceneGeometry(CaveFactory.Create(ScenarioDefinition.All[0]), session);
        var original = points.ToArray();
        var full = geometry.Cloud(points.Length, false, 500);
        var plane = PointClipPlane.FromAngles(35, 20, -10);
        var clipped = geometry.Cloud(points.Length, false, 500, plane);
        Assert.Equal(500, clipped.Positions!.Count);
        Assert.All(clipped.Positions, point => Assert.True(plane.Contains(point)));
        Assert.Equal(original, points);
        Assert.Same(full, geometry.Cloud(points.Length, false, 500));
        Assert.Empty(geometry.Cloud(0, false, 500, plane).Positions!);
    }

    [Fact]
    public async Task MaximumAnnotationAndAdjustmentCountsStillProduceAReadableSession()
    {
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        var path = Path.Combine(directory, "maximum.subterra");
        try
        {
            var data = await new WorkspaceFeatureTests.MemoryScanSource(360).GenerateAsync(ScenarioDefinition.All[0], new(), null, CancellationToken.None);
            var session = new ScanSession
            {
                ScenarioId = data.Session.ScenarioId, Settings = data.Session.Settings, Frames = data.Session.Frames,
                Adjustments = Enumerable.Range(0, 360).Select(index => new ScannerAdjustment(index, .98f, -1, -.5f, -180)).ToArray(),
                Annotations = Enumerable.Range(0, 256).Select(_ => new UserAnnotation(new string('Ж', 120), new(-100_000, -100_000, -100_000))).ToArray()
            };
            var storage = new SessionStorage();
            await storage.SaveAsync(path, session, session.Frames.Length);
            var restored = await storage.LoadAsync(path);
            Assert.Equal(256, restored.Annotations.Length);
            Assert.Equal(360, restored.Adjustments.Length);
        }
        finally { Directory.Delete(directory, true); }
    }
}
