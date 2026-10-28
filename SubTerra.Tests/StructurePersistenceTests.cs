using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using SubTerra.Models;
using SubTerra.Services;
using Xunit;

namespace SubTerra.Tests;

public sealed class StructurePersistenceTests
{
    internal static async Task<ScanProject> AnalyzedRoom()
    {
        var project = await StructureAnalysisTests.Classroom();
        var result = await new PlaneStructureAnalyzer().AnalyzeAsync(project, new(), null, CancellationToken.None);
        return project with { Structure = StructureProvenance.Create(project, result) };
    }

    [Fact]
    public async Task VersionSixRoundTripKeepsProvenanceReviewsAndExactGeometry()
    {
        using var files = new Files(); var project = await AnalyzedRoom(); var document = project.Structure!;
        var surfaces = document.Result.Surfaces.Select((s, i) => s with
        {
            Kind = i == 0 ? StructureSurfaceKind.Unknown : s.Kind,
            Review = i == 0 ? StructureReviewState.Confirmed : i == 1 ? StructureReviewState.Rejected : s.Review
        }).ToArray();
        project = project with { Structure = document with { Result = document.Result with { Surfaces = surfaces } } };
        var path = Path.Combine(files.Directory, "reviewed.subterra"); var storage = new SessionStorage();
        await storage.SaveAsync(path, project.ToSession(), 1);
        using (var zip = ZipFile.OpenRead(path))
        {
            using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
            Assert.Equal(6, JsonNode.Parse(reader.ReadToEnd())!["SchemaVersion"]!.GetValue<int>());
            Assert.NotNull(zip.GetEntry("structure/analysis.json"));
        }
        var loaded = (await storage.LoadAsync(path)).Project!; var restored = loaded.Structure!;
        Assert.False(restored.IsStale); Assert.Equal(document.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(document.Algorithm, restored.Algorithm); Assert.Equal(document.Inputs, restored.Inputs);
        Assert.Equal(document.Result.Options, restored.Result.Options);
        for (var i = 0; i < surfaces.Length; i++)
        {
            var expected = surfaces[i]; var actual = restored.Result.Surfaces[i];
            Assert.Equal(expected.Kind, actual.Kind); Assert.Equal(expected.SuggestedKind, actual.SuggestedKind);
            Assert.Equal(expected.Review, actual.Review); Assert.Equal(expected.Normal, actual.Normal);
            Assert.Equal(expected.Offset, actual.Offset); Assert.Equal(expected.Center, actual.Center);
            Assert.Equal(expected.Boundary, actual.Boundary); Assert.Equal(expected.Samples, actual.Samples);
            Assert.Equal(expected.AreaSquareMeters, actual.AreaSquareMeters); Assert.Equal(expected.RmsMeters, actual.RmsMeters);
        }
        Assert.Equal(project.Active!.Source.Points, loaded.Active!.Source.Points);
    }

    [Theory]
    [InlineData("transform")] [InlineData("mask")] [InlineData("visibility")] [InlineData("points")] [InlineData("remove")]
    public async Task ChangedGeometryIsPersistedAsStaleWithoutDiscardingReviews(string change)
    {
        using var files = new Files(); var project = await AnalyzedRoom(); var original = project;
        var o = project.Active!;
        project = change switch
        {
            "transform" => project.Replace(o with { Transform = new(1) }),
            "mask" => project.Replace(o with { Mask = new(o.Source.Points.Select((_, i) => i % 2 == 0).ToArray()) }),
            "visibility" => project.Replace(o with { IsVisible = false }),
            "points" => project.Replace(o with { Source = new ImportedScan { Metadata = o.Source.Metadata,
                Points = o.Source.Points.Select((p, i) => i == 0 ? p with { X = p.X + .04f } : p).ToArray(), Colors = o.Source.Colors } }),
            _ => project with { Objects = [], ActiveObjectId = Guid.Empty }
        };
        Assert.True(StructureProvenance.Refresh(project.Structure!, project).IsStale);
        var path = Path.Combine(files.Directory, "stale.subterra"); var storage = new SessionStorage();
        await storage.SaveAsync(path, project.ToSession(), 0); var loaded = (await storage.LoadAsync(path)).Project!;
        Assert.True(loaded.Structure!.IsStale);
        Assert.Equal(original.Structure!.Result.Surfaces.SelectMany(s => s.Samples).ToArray(), loaded.Structure.Result.Surfaces.SelectMany(s => s.Samples).ToArray());
        Assert.False(StructureProvenance.Refresh(loaded.Structure, original).IsStale);
    }

    [Fact]
    public async Task NamesColorsAndActiveSelectionDoNotInvalidateGeometry()
    {
        var project = await AnalyzedRoom(); var o = project.Active!;
        var changed = project.Replace(o with { Name = "Аудитория", Tint = 0xaabbcc });
        Assert.False(StructureProvenance.Refresh(project.Structure!, changed).IsStale);
    }

    [Fact]
    public async Task LegacyProjectWithoutAnalysisStillUsesAndLoadsVersionFive()
    {
        using var files = new Files(); var path = Path.Combine(files.Directory, "legacy.subterra"); var storage = new SessionStorage();
        await storage.SaveAsync(path, (await StructureAnalysisTests.Classroom()).ToSession(), 1);
        using (var zip = ZipFile.OpenRead(path))
        {
            using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
            Assert.Equal(5, JsonNode.Parse(reader.ReadToEnd())!["SchemaVersion"]!.GetValue<int>());
        }
        Assert.Null((await storage.LoadAsync(path)).Project!.Structure);
    }

    [Fact]
    public async Task StaleFlagInTheFileCannotConcealAMismatchingTransform()
    {
        using var files = new Files(); var project = await AnalyzedRoom(); var storage = new SessionStorage();
        var path = Path.Combine(files.Directory, "changed.subterra"); await storage.SaveAsync(path, project.ToSession(), 1);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            Edit(zip, "manifest.json", json => json["Project"]!["Objects"]![0]!["Transform"] = JsonNode.Parse("{\"X\":2}"));
        Assert.True((await storage.LoadAsync(path)).Project!.Structure!.IsStale);
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate-entry")] [InlineData("oversized")]
    [InlineData("object-id")] [InlineData("negative-index")] [InlineData("large-index")] [InlineData("duplicate-reference")]
    [InlineData("position")] [InlineData("review")] [InlineData("normal")] [InlineData("inputs")] [InlineData("algorithm")]
    [InlineData("sample-count")] [InlineData("options")]
    public async Task CorruptAnalysisIsRejected(string damage)
    {
        using var files = new Files(); var path = Path.Combine(files.Directory, "corrupt.subterra"); var storage = new SessionStorage();
        await storage.SaveAsync(path, (await AnalyzedRoom()).ToSession(), 1);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            const string entry = "structure/analysis.json";
            if (damage == "missing") zip.GetEntry(entry)!.Delete();
            else if (damage == "duplicate-entry") zip.CreateEntry(entry);
            else if (damage == "oversized")
            {
                zip.GetEntry(entry)!.Delete(); using var stream = zip.CreateEntry(entry).Open(); var block = new byte[1024 * 1024];
                for (var i = 0; i < 17; i++) stream.Write(block);
            }
            else Edit(zip, entry, json =>
            {
                var surface = json["Result"]!["Surfaces"]![0]!; var sample = surface["Samples"]![0]!;
                switch (damage)
                {
                    case "object-id": sample["Source"]!["ObjectId"] = Guid.NewGuid(); break;
                    case "negative-index": sample["Source"]!["PointIndex"] = -1; break;
                    case "large-index": sample["Source"]!["PointIndex"] = 5_000_000; break;
                    case "duplicate-reference": surface["Samples"]![1] = sample.DeepClone(); break;
                    case "position": sample["Position"]!["X"] = sample["Position"]!["X"]!.GetValue<float>() + .5f; break;
                    case "review": surface["Review"] = 99; break;
                    case "normal": surface["Normal"] = JsonNode.Parse("{\"X\":0,\"Y\":0,\"Z\":0}"); break;
                    case "inputs": json["Inputs"] = null; break;
                    case "algorithm": json["Algorithm"] = "unknown"; break;
                    case "sample-count": json["Result"]!["SampleCount"] = 50001; break;
                    case "options": json["Result"]!["Options"] = null; break;
                }
            });
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.LoadAsync(path));
    }

    [Fact]
    public async Task CancellingSaveWithAnalysisPreservesTheExistingFileAndCleansTemporaryData()
    {
        using var files = new Files(); var path = Path.Combine(files.Directory, "room.subterra");
        await File.WriteAllTextAsync(path, "existing file"); using var cts = new CancellationTokenSource();
        var project = await AnalyzedRoom();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SessionStorage().SaveAsync(path, project.ToSession(), 1,
            cts.Token, new ImmediateProgress(_ => cts.Cancel())));
        Assert.Equal("existing file", await File.ReadAllTextAsync(path)); Assert.Single(Directory.GetFiles(files.Directory));
    }
    private static void Edit(ZipArchive zip, string name, Action<JsonNode> change)
    {
        var entry = zip.GetEntry(name)!; JsonNode json;
        using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(reader.ReadToEnd())!;
        change(json); entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(json.ToJsonString());
    }
    private sealed class ImmediateProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    private sealed class Files : IDisposable
    {
        public string Directory { get; } = WorkspaceFeatureTests.TemporaryDirectory();
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
