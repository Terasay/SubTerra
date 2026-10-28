using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using SubTerra.Models;
using SubTerra.Services;
using Xunit;

namespace SubTerra.Tests;

public sealed class RoomPersistenceTests
{
    [Fact]
    public async Task VersionSevenRoundTripKeepsRoomAndOriginalMeasurements()
    {
        using var files = new Files(); var project = await RoomPlannerTests.PlannedRoom(); var storage = new SessionStorage();
        var path = Path.Combine(files.Directory, "room.subterra"); await storage.SaveAsync(path, project.ToSession(), 1);
        using (var zip = ZipFile.OpenRead(path))
        {
            using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
            Assert.Equal(7, JsonNode.Parse(reader.ReadToEnd())!["SchemaVersion"]!.GetValue<int>());
        }
        var restored = (await storage.LoadAsync(path)).Project!; var room = restored.Structure!.Room!;
        Assert.True(RoomPlanner.IsCurrent(restored.Structure)); Assert.Equal(project.Structure!.Room!.Name, room.Name);
        Assert.Equal(project.Structure.Room.FloorContour, room.FloorContour); Assert.Equal(project.Structure.Room.CeilingContour, room.CeilingContour);
        Assert.Equal(project.Structure.Room.WallSurfaceIds, room.WallSurfaceIds); Assert.Equal(project.Structure.Room.Gaps, room.Gaps);
        Assert.Equal(project.Active!.Source.Points, restored.Active!.Source.Points);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task StaleRoomSurvivesGeometryAndReviewChanges(bool geometry)
    {
        using var files = new Files(); var project = await RoomPlannerTests.PlannedRoom();
        if (geometry) project = project.Replace(project.Active! with { Transform = new(1) });
        else
        {
            var doc = project.Structure!; var wall = doc.Result.Surfaces.First(s => s.Kind == StructureSurfaceKind.Wall);
            project = project with { Structure = doc with { Result = doc.Result with { Surfaces = doc.Result.Surfaces.Select(s => s.Id == wall.Id ? s with { Review = StructureReviewState.Rejected } : s).ToArray() } } };
        }
        var path = Path.Combine(files.Directory, "stale.subterra"); var storage = new SessionStorage();
        await storage.SaveAsync(path, project.ToSession(), 1); var restored = (await storage.LoadAsync(path)).Project!;
        Assert.NotNull(restored.Structure!.Room); Assert.False(RoomPlanner.IsCurrent(restored.Structure));
    }

    [Theory]
    [InlineData("version")] [InlineData("floor-id")] [InlineData("coordinate")] [InlineData("footprint")]
    [InlineData("ceiling-count")] [InlineData("signature")] [InlineData("gap")] [InlineData("schema")]
    public async Task CorruptedRoomIsRejected(string damage)
    {
        using var files = new Files(); var path = Path.Combine(files.Directory, "bad.subterra"); var storage = new SessionStorage();
        await storage.SaveAsync(path, (await RoomPlannerTests.PlannedRoom()).ToSession(), 1);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry(damage == "schema" ? "manifest.json" : "structure/analysis.json")!; JsonNode json;
            using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(reader.ReadToEnd())!;
            if (damage == "schema") json["SchemaVersion"] = 6;
            else
            {
                var room = json["Room"]!;
                switch (damage)
                {
                    case "version": room["Version"] = 99; break;
                    case "floor-id": room["FloorSurfaceId"] = 999; break;
                    case "coordinate": room["FloorContour"]![0]!["X"] = 100001; break;
                    case "footprint": room["FloorContour"]![0]!["X"] = room["FloorContour"]![0]!["X"]!.GetValue<float>() + .2f; break;
                    case "ceiling-count": room["CeilingContour"]!.AsArray().RemoveAt(0); break;
                    case "signature": room["SourceSignature"] = "bad"; break;
                    case "gap": room["Gaps"] = JsonNode.Parse("[{\"WallSurfaceId\":1,\"EdgeIndex\":999,\"Start\":0,\"End\":1}]"); break;
                }
            }
            var name = entry.FullName; entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(json.ToJsonString());
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.LoadAsync(path));
    }
    private sealed class Files : IDisposable
    {
        public string Directory { get; } = WorkspaceFeatureTests.TemporaryDirectory();
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
