using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using SubTerra.Models;
using SubTerra.Services;
using SubTerra.Simulation;
using Xunit;

namespace SubTerra.Tests;

public sealed class SimulationTests
{
    private static Task<ScanDataset> SmallScan(float range = 20) => new SimulatedScanSource().GenerateAsync(
        ScenarioDefinition.All[0], new ScanSettings(range, 64, 8), null, CancellationToken.None);

    [Fact]
    public void RayReturnsNearestTriangleAndHonorsRange()
    {
        Vector3[] vertices = [new(5, -2, -2), new(5, 2, -2), new(5, 0, 2),
            new(2, -2, -2), new(2, 2, -2), new(2, 0, 2)];
        var index = new TriangleIndex(vertices, [0, 1, 2, 3, 4, 5]);
        Assert.True(index.Cast(Vector3.Zero, Vector3.UnitX, 10, out var hit));
        Assert.Equal(new Vector3(2, 0, 0), hit);
        Assert.False(index.Cast(Vector3.Zero, Vector3.UnitX, 1, out _));
        Assert.False(index.Cast(Vector3.Zero, Vector3.UnitY, 10, out _));
    }

    [Theory]
    [InlineData("aurora")]
    [InlineData("mine")]
    [InlineData("lava")]
    public void SceneHasValidTrianglesAndContinuousRoute(string id)
    {
        var scene = CaveFactory.Create(ScenarioDefinition.All.First(s => s.Id == id));
        Assert.True(scene.LengthMeters > 50);
        Assert.All(scene.Indices, i => Assert.InRange(i, 0, scene.Vertices.Length - 1));
        Assert.All(scene.Vertices, p => Assert.True(float.IsFinite(p.X + p.Y + p.Z)));
        Assert.Equal(scene.Vertices.Length, scene.TextureCoordinates.Length);
        Assert.All(scene.TextureCoordinates, uv => Assert.True(float.IsFinite(uv.X + uv.Y)));
        Assert.All(scene.Normals, n => Assert.InRange(n.Length(), .999f, 1.001f));
        for (var i = 1; i < scene.Route.Length; i++)
            Assert.InRange(Vector3.Distance(scene.Route[i - 1], scene.Route[i]), .01f, 1f);
    }

    [Fact]
    public async Task ScanIsReproducibleAndMeasurementsStayWithinRange()
    {
        var a = await SmallScan();
        var b = await SmallScan();
        Assert.Equal(8, a.Session.Frames.Length);
        Assert.True(a.Session.PointCount > 300);
        for (var i = 0; i < a.Session.Frames.Length; i++)
        {
            var frame = a.Session.Frames[i];
            Assert.Equal(frame.Points, b.Session.Frames[i].Points);
            Assert.All(frame.Points, point =>
            {
                Assert.InRange(Vector3.Distance(point.Position, frame.Pose.Position), .01f, 20.02f);
                Assert.InRange(point.Intensity, 0f, 1f);
            });
        }
    }

    [Fact]
    public async Task ShortRangeProducesFewerReturns()
    {
        var complete = await SmallScan();
        var shortRange = await SmallScan(1);
        Assert.True(shortRange.Session.PointCount < complete.Session.PointCount / 4);
    }

    [Fact]
    public async Task CancellationAndInvalidParametersAreRejected()
    {
        var source = new SimulatedScanSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GenerateAsync(
            ScenarioDefinition.All[0], new(), null, new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.GenerateAsync(
            ScenarioDefinition.All[0], new(float.NaN), null, CancellationToken.None));
    }

    [Fact]
    public async Task PartialSessionRestoresEveryCoordinateAndPoseExactly()
    {
        var data = await SmallScan();
        var path = Path.Combine(Path.GetTempPath(), $"subterra-test-{Guid.NewGuid():N}.subterra");
        try
        {
            var storage = new SessionStorage();
            await storage.SaveAsync(path, data.Session, 3);
            var loaded = await storage.LoadAsync(path);
            Assert.Equal(data.Session.Settings, loaded.Settings);
            Assert.Equal(3, loaded.Frames.Length);
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(data.Session.Frames[i].Points, loaded.Frames[i].Points);
                Assert.Equal(data.Session.Frames[i].Pose, loaded.Frames[i].Pose);
                Assert.Equal(data.Session.Frames[i].Timestamp.ToUnixTimeMilliseconds(), loaded.Frames[i].Timestamp.ToUnixTimeMilliseconds());
                Assert.Equal(data.Session.Frames[i].Progress, loaded.Frames[i].Progress);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BrokenArchiveAndUnsupportedVersionAreRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"subterra-test-{Guid.NewGuid():N}.subterra");
        try
        {
            await File.WriteAllTextAsync(path, "not a session");
            var storage = new SessionStorage();
            await Assert.ThrowsAsync<InvalidDataException>(() => storage.LoadAsync(path));
            File.Delete(path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                writer.Write("""{"SchemaVersion":99,"SourceType":"simulation","CoordinateUnit":"m"}""");
            await Assert.ThrowsAsync<InvalidDataException>(() => storage.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PlyUsesInvariantCoordinatesAndCorrectPointCount()
    {
        var path = Path.Combine(Path.GetTempPath(), $"subterra-test-{Guid.NewGuid():N}.ply");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            await new SessionStorage().ExportPlyAsync(path, [new(1.25f, -2.5f, 3.75f, .8f)]);
            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("element vertex 1", content);
            Assert.Contains("1.25 -2.5 3.75 0.8", content);
        }
        finally { CultureInfo.CurrentCulture = culture; File.Delete(path); }
    }
}
