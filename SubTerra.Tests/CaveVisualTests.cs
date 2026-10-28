using System.Numerics;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Simulation;
using Xunit;

namespace SubTerra.Tests;

public sealed class CaveVisualTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurfaceVisibilityMatchesSceneAcrossSeekingAndRoofChanges(bool initialCutRoof)
    {
        var scene = CaveFactory.Create(ScenarioDefinition.All[0]);
        var geometry = new SceneGeometry(scene);
        foreach (var cutRoof in new[] { initialCutRoof, !initialCutRoof, initialCutRoof })
        foreach (var coverage in new ScanCoverage[][] { [new(0, 1)], [new(.1f, .3f), new(.6f, .8f)], [] })
        {
            var expected = new List<int>();
            for (var i = 0; i < scene.Indices.Length; i += 3)
            {
                var vertices = scene.Indices.AsSpan(i, 3);
                var min = Math.Min(scene.VertexProgress[vertices[0]], Math.Min(scene.VertexProgress[vertices[1]], scene.VertexProgress[vertices[2]]));
                var max = Math.Max(scene.VertexProgress[vertices[0]], Math.Max(scene.VertexProgress[vertices[1]], scene.VertexProgress[vertices[2]]));
                if (!coverage.Any(range => min >= range.Start && max <= range.End)) continue;
                if (cutRoof && (MathF.Sin(scene.VertexAngles[vertices[0]]) > .30f ||
                                MathF.Sin(scene.VertexAngles[vertices[1]]) > .30f ||
                                MathF.Sin(scene.VertexAngles[vertices[2]]) > .30f)) continue;
                expected.AddRange(vertices);
            }
            Assert.Equal(expected, geometry.Surface(coverage, cutRoof).Indices);
        }
    }

    [Theory]
    [InlineData("aurora")]
    [InlineData("mine")]
    [InlineData("lava")]
    public void AllMaterialLayersHaveRealGeometryAndTheScannerRouteRemainsClear(string id)
    {
        var scene = CaveFactory.Create(ScenarioDefinition.All.First(s => s.Id == id));
        Assert.Equal(SceneVersions.Current, scene.SceneVersion);
        Assert.Equal(scene.Indices.Length / 3, scene.SurfaceKinds.Length);
        foreach (var kind in Enum.GetValues<CaveSurfaceKind>()) Assert.Contains(kind, scene.SurfaceKinds);
        Assert.InRange(scene.Vertices.Length, 30_000, 65_000);
        Assert.InRange(scene.Indices.Length / 3, 70_000, 130_000);
        for (var i = 0; i < scene.Indices.Length; i += 3)
        {
            var a = scene.Vertices[scene.Indices[i]];
            var b = scene.Vertices[scene.Indices[i + 1]];
            var c = scene.Vertices[scene.Indices[i + 2]];
            var normal = Vector3.Cross(b - a, c - a);
            Assert.True(normal.LengthSquared() > 1e-12f, $"Degenerate triangle {i / 3}");
            if (scene.SurfaceKinds[i / 3] == CaveSurfaceKind.Sediment)
                Assert.True(Vector3.Normalize(normal).Z > .6f,
                    $"Sediment floor triangle {i / 3}, normal {Vector3.Normalize(normal)}, vertices {a} / {b} / {c}");
        }
        var collision = new TriangleIndex(scene.Vertices, scene.Indices);
        for (var t = .02f; t < .99f; t += .02f)
        {
            var origin = CaveFactory.Center(id, t);
            foreach (var direction in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
                Assert.False(collision.Cast(origin, direction, .6f, out _));
            Assert.True(collision.Cast(origin, -Vector3.UnitZ, 8, out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaterialLayersPartitionTheVisibleMeshWithoutShowingUnscannedGaps(bool cutRoof)
    {
        var scene = CaveFactory.Create(ScenarioDefinition.All[0]);
        var geometry = new SceneGeometry(scene);
        ScanCoverage[] coverage = [new(.05f, .32f), new(.56f, .87f)];
        var all = geometry.Surface(coverage, cutRoof, true);
        var layers = geometry.SurfaceLayers(coverage, cutRoof);
        Assert.NotNull(all.Indices);
        var actual = new HashSet<(int, int, int)>();
        foreach (var mesh in new[] { layers.Rock, layers.Floor, layers.Damp })
        {
            Assert.NotNull(mesh.Indices);
            Assert.NotNull(mesh.Tangents);
            Assert.NotNull(mesh.BiTangents);
            Assert.NotEmpty(mesh.Indices);
            for (var i = 0; i < mesh.Indices.Count; i += 3)
                Assert.True(actual.Add((mesh.Indices[i], mesh.Indices[i + 1], mesh.Indices[i + 2])), "Duplicate triangle across materials");
            Assert.All(mesh.Tangents, n => Assert.True(float.IsFinite(n.X + n.Y + n.Z)));
            Assert.All(mesh.BiTangents, n => Assert.True(float.IsFinite(n.X + n.Y + n.Z)));
        }
        Assert.Equal(all.Indices.Count / 3, actual.Count);
        for (var i = 0; i < all.Indices.Count; i += 3)
            Assert.Contains((all.Indices[i], all.Indices[i + 1], all.Indices[i + 2]), actual);
        Assert.NotNull(layers.Floor.Indices);
        Assert.NotNull(layers.Floor.TextureCoordinates);
        foreach (var i in layers.Floor.Indices)
            Assert.InRange(Vector2.Distance(new Vector2(scene.Vertices[i].X, scene.Vertices[i].Y) / 2.4f, layers.Floor.TextureCoordinates[i]), 0, .00001f);
    }

    [Theory]
    [InlineData(CaveSurfaceKind.Sediment)]
    [InlineData(CaveSurfaceKind.DampRock)]
    public void NewMaterialsAreSeamlessAndDifferentFromDryStone(CaveSurfaceKind kind)
    {
        var contrast = 0f;
        for (var i = 0; i < 50; i++)
        {
            var t = i / 50f;
            var left = LimestoneTexture.Sample(0, t, kind);
            var right = LimestoneTexture.Sample(1, t, kind);
            var top = LimestoneTexture.Sample(t, 0, kind);
            var bottom = LimestoneTexture.Sample(t, 1, kind);
            Assert.InRange(Math.Abs(left.Height - right.Height), 0, .0001f);
            Assert.InRange(Vector3.Distance(left.Color, right.Color), 0, .0001f);
            Assert.InRange(Math.Abs(top.Height - bottom.Height), 0, .0001f);
            Assert.InRange(Vector3.Distance(top.Color, bottom.Color), 0, .0001f);
            contrast += Vector3.Distance(left.Color, LimestoneTexture.Sample(0, t).Color);
        }
        Assert.True(contrast / 50 > .05f);
        var map = LimestoneTexture.Create(64, kind);
        Assert.Equal(kind == CaveSurfaceKind.DampRock, map.Specular is not null);
        Assert.All(map.Normals.Where((_, i) => i % 4 == 3), alpha => Assert.Equal(255, alpha));
    }
}
