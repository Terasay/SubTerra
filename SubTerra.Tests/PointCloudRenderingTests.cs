using System.IO;
using System.Numerics;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.Simulation;
using Xunit;

namespace SubTerra.Tests;

public sealed class PointCloudRenderingTests
{
    [Theory]
    [InlineData(25_000)]
    [InlineData(60_000)]
    [InlineData(150_000)]
    public void PreviewRespectsBudgetAndCoversTheWholeSession(int limit)
    {
        var points = Enumerable.Range(0, 100_003).Select(i => new ScanPoint(i, i % 11, i % 2 == 0 ? 0 : 2, 1)).ToArray();
        var sampler = new PointCloudSampler(points);
        foreach (var cut in new[] { false, true })
        {
            var indices = sampler.Select(points.Length, cut, limit).ToArray();
            Assert.Equal(Math.Min(limit, cut ? 50_002 : points.Length), indices.Length);
            Assert.Equal(0, indices[0]);
            Assert.Equal(100_002, indices[^1]);
            Assert.True(indices.SequenceEqual(indices.Order()));
            Assert.Equal(indices.Length, indices.Distinct().Count());
            if (cut) Assert.All(indices, i => Assert.True(points[i].Z <= .8f));
        }
    }

    [Fact]
    public void TimelineNeverShowsFuturePointsOrReplacesExistingSamples()
    {
        var points = Enumerable.Range(0, 10_000).Select(i => new ScanPoint(i, 0, 0, 1)).ToArray();
        var sampler = new PointCloudSampler(points);
        var first = sampler.Select(1700, false, 500).ToArray();
        var later = sampler.Select(6500, false, 500).ToArray();
        Assert.All(first, i => Assert.InRange(i, 0, 1699));
        Assert.All(later, i => Assert.InRange(i, 0, 6499));
        Assert.Equal(first, later.Where(i => i < 1700).ToArray());
        Assert.Equal(first, sampler.Select(1700, false, 500).ToArray());
        Assert.Empty(sampler.Select(0, false, 500).ToArray());
        Assert.Single(sampler.Select(10_000, false, 1).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.Select(10, false, 0));
        Assert.Empty(new PointCloudSampler([]).Select(100, true, 10).ToArray());
    }

    [Fact]
    public async Task LimitedGeometryKeepsColorsAndLeavesSavedMeasurementsIntact()
    {
        var points = Enumerable.Range(0, 20_000).Select(i => new ScanPoint(i, i % 7, i % 9 - 4, .7f + i % 3 * .1f)).ToArray();
        var frames = Enumerable.Range(0, 20).Select(f => new ScanFrame(
            DateTimeOffset.UnixEpoch.AddMilliseconds(f * 100), new(Vector3.Zero, Quaternion.Identity),
            (f + 1f) / 20, points.Skip(f * 1000).Take(1000).ToArray())).ToArray();
        var session = new ScanSession { ScenarioId = "aurora", Settings = new(20, 1000, 20), Frames = frames };
        var geometry = new SceneGeometry(CaveFactory.Create(ScenarioDefinition.All[0]), session);
        var full = geometry.Cloud(points.Length, false, int.MaxValue);
        var limited = geometry.Cloud(points.Length, false, 200);
        Assert.NotNull(full.Positions);
        Assert.NotNull(full.Colors);
        Assert.NotNull(limited.Positions);
        Assert.NotNull(limited.Colors);
        Assert.Equal(200, limited.Positions.Count);
        Assert.Equal(limited.Positions.Count, limited.Colors.Count);
        var indices = new PointCloudSampler(points).Select(points.Length, false, 200).ToArray();
        for (var i = 0; i < indices.Length; i++)
        {
            Assert.Equal(full.Positions[indices[i]], limited.Positions[i]);
            Assert.Equal(full.Colors[indices[i]], limited.Colors[i]);
        }
        Assert.Same(limited, geometry.Cloud(points.Length, false, 200));
        var path = Path.Combine(Path.GetTempPath(), $"subterra-preview-{Guid.NewGuid():N}.subterra");
        try
        {
            var storage = new SessionStorage();
            await storage.SaveAsync(path, session, frames.Length);
            var loaded = await storage.LoadAsync(path);
            Assert.Equal(points, loaded.Frames.SelectMany(f => f.Points).ToArray());
        }
        finally { File.Delete(path); }
    }
}
