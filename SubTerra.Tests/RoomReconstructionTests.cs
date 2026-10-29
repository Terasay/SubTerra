using System.IO;
using System.Numerics;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.Simulation;
using Xunit;

namespace SubTerra.Tests;

public sealed class RoomReconstructionTests
{
    [Fact]
    public async Task FloorOcclusionIsFilledAndLeveledWithoutChangingFurnitureOrSourceColors()
    {
        var floor = (from x in Enumerable.Range(0, 101) from y in Enumerable.Range(0, 101)
            let p = new Vector3(x * .04f, y * .04f, x * .00048f + y * .0002f)
            where !(p.X > 1.5 && p.X < 2.3 && p.Y > 1.4 && p.Y < 2.2) select p).ToArray();
        Vector3[] objects = [new(1.8f, 1.8f, .8f), new(1.81f, 1.8f, .8f), new(1.8f, 1.81f, .8f), new(10, 10, 4)];
        var (project, doc) = Fixture(floor, objects, StructureSurfaceKind.Floor, Vector3.Normalize(new(-.012f, -.005f, 1)),
            [new(0, 0, 0), new(4, 0, .048f), new(4, 4, .068f), new(0, 4, .02f)], 16);
        var original = project.Active!.Source; var points = original.Points.ToArray(); var colors = original.Colors.ToArray();
        var observed = RoomReconstructionBuilder.Build(project, doc, new(FloorHoleSize: 0, RemoveIsolatedPoints: false));
        Assert.False(Index(observed).Cast(new(1.9f, 1.8f, 1), -Vector3.UnitZ, 2, out _));
        var preview = RoomReconstructionBuilder.Build(project, doc, new());
        Assert.True(preview.FilledCells > 100); Assert.True(preview.LeveledSurfaces > 0);
        Assert.True(Index(preview).Cast(new(1.9f, 1.8f, 1), -Vector3.UnitZ, 2, out _));
        Assert.Single(preview.Scan.Points.Select(p => p.Z).Distinct());
        Assert.Equal(3, preview.RemainingPoints!.Points.Length); Assert.Equal(1, preview.SuppressedPoints);
        Assert.Equal(objects.Take(3), preview.RemainingPoints.Points.Select(p => p.Position));
        Assert.Equal(colors.Skip(floor.Length).Take(3), preview.RemainingPoints.Colors);
        Assert.Contains(0xF4B860u, preview.CompletionColors); Assert.Equal(points, original.Points); Assert.Equal(colors, original.Colors);
        var exported = RoomReconstructionBuilder.ExportModel(preview);
        var path = Path.Combine(Path.GetTempPath(), $"subterra-reconstruction-{Guid.NewGuid():N}.ply");
        try
        {
            await new SessionStorage().ExportImportedPlyAsync(path, exported);
            var restored = await new ScanImporter().ImportAsync(path, new(ScanUnit.Meters, LocalOrigin: false));
            Assert.True(restored.Metadata.IsSynthetic); Assert.Equal(exported.Points, restored.Points);
            Assert.Equal(exported.Colors, restored.Colors); Assert.Equal(exported.Triangles, restored.Triangles);
            Assert.Equal(points, original.Points); Assert.Equal(colors, original.Colors);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LargeWindowAndBoundaryDoorRemainOpenWhileSmallEnclosedWallHoleCanBeFilled()
    {
        var wall = (from x in Enumerable.Range(0, 201) from z in Enumerable.Range(0, 151)
            let p = new Vector3(x * .02f, 0, z * .02f)
            where !(p.X > 1 && p.X < 2 && p.Z > 1 && p.Z < 2) &&
                  !(p.X > .25 && p.X < .85 && p.Z < 2) &&
                  !(p.X > 3 && p.X < 3.12 && p.Z > 1 && p.Z < 1.12) select p).ToArray();
        var (project, doc) = Fixture(wall, [], StructureSurfaceKind.Wall, Vector3.UnitY,
            [new(0, 0, 0), new(4, 0, 0), new(4, 0, 3), new(0, 0, 3)], 12);
        var preview = RoomReconstructionBuilder.Build(project, doc, new()); var index = Index(preview);
        Assert.False(index.Cast(new(1.5f, -1, 1.5f), Vector3.UnitY, 2, out _));
        Assert.False(index.Cast(new(.5f, -1, 1), Vector3.UnitY, 2, out _));
        Assert.True(index.Cast(new(3.06f, -1, 1.06f), Vector3.UnitY, 2, out _));
        Assert.True(index.Cast(new(2.5f, -1, 1.5f), Vector3.UnitY, 2, out _)); Assert.True(preview.FilledCells > 0);
    }

    [Fact]
    public async Task CropRequiresCurrentRoomAndLeavesOnlyInteriorPointsInDerivedView()
    {
        var project = await RoomPlannerTests.PlannedRoom(); var doc = project.Structure!;
        Assert.Throws<InvalidDataException>(() => RoomReconstructionBuilder.Build(project, doc with { Room = null }, new(LimitToRoom: true)));
        var preview = RoomReconstructionBuilder.Build(project, doc, new(LimitToRoom: true));
        Assert.All(preview.Scan.Points, p => Assert.True(RoomTopology.Contains(doc.Room!, p.Position)));
        if (preview.RemainingPoints is { } objects) Assert.All(objects.Points, p => Assert.True(RoomTopology.Contains(doc.Room!, p.Position)));
        Assert.ThrowsAny<OperationCanceledException>(() => RoomReconstructionBuilder.Build(project, doc, new(), new(true)));
        var changed = project.Replace(project.Active! with { Transform = new(1) });
        Assert.Throws<InvalidDataException>(() => RoomReconstructionBuilder.Build(changed, doc, new()));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomReconstructionBuilder.Build(project, doc, new(FloorHoleSize: double.NaN)));
    }

    private static TriangleIndex Index(IndoorSurfacePreview preview) => new(preview.Scan.Points.Select(p => p.Position).ToArray(), preview.Scan.Triangles);

    [Fact]
    public void ClosedWallsSupportLargeBoundaryOcclusionsAndConditionalWindowsWithoutSealingTheDoor()
    {
        var points = new List<ScanPoint>(); var colors = new List<uint>(); var planes = new List<StructureSurface>();
        var id = Guid.NewGuid();
        void Plane(StructureSurfaceKind kind, Vector3 normal, Vector3[] boundary, double area, IEnumerable<Vector3> sample)
        {
            var samples = new List<StructureSample>();
            foreach (var p in sample) { samples.Add(new(new(id, points.Count), p)); points.Add(new(p.X, p.Y, p.Z, 1)); colors.Add(0xA9B7C4); }
            var center = boundary.Aggregate(Vector3.Zero, (a, b) => a + b) / boundary.Length;
            planes.Add(new(planes.Count + 1, kind, normal, -Vector3.Dot(normal, center), center, boundary, area, 0, samples.ToArray()));
        }
        var xs = Enumerable.Range(0, 61).Select(i => i * .1f).ToArray(); var ys = Enumerable.Range(0, 51).Select(i => i * .1f).ToArray(); var zs = Enumerable.Range(0, 31).Select(i => i * .1f).ToArray();
        Plane(StructureSurfaceKind.Floor, Vector3.UnitZ, [new(0,0,0),new(6,0,0),new(6,5,0),new(0,5,0)],30,
            from x in xs from y in ys where !(x > 2 && x < 4 && y < 1.5) select new Vector3(x,y,0));
        Plane(StructureSurfaceKind.Ceiling, Vector3.UnitZ, [new(0,0,3),new(6,0,3),new(6,5,3),new(0,5,3)],30,from x in xs from y in ys select new Vector3(x,y,3));
        // Overlapping RANSAC floor fragments must not double the footprint area and reject the shell.
        Plane(StructureSurfaceKind.Floor, Vector3.UnitZ, [new(0,0,.08f),new(6,0,.08f),new(6,5,.08f),new(0,5,.08f)],30,
            from x in xs from y in ys where !(x > 2 && x < 4 && y < 1.5) select new Vector3(x,y,.08f));
        foreach (var x in new[] { 0, 6 }) Plane(StructureSurfaceKind.Wall,Vector3.UnitX,[new(x,0,0),new(x,5,0),new(x,5,3),new(x,0,3)],15,from y in ys from z in zs select new Vector3(x,y,z));
        Plane(StructureSurfaceKind.Wall,Vector3.UnitY,[new(0,0,0),new(6,0,0),new(6,0,3),new(0,0,3)],18,from x in xs from z in zs where !(x > .5 && x < 1.3 && z < 2) select new Vector3(x,0,z));
        Plane(StructureSurfaceKind.Wall,Vector3.UnitY,[new(0,5,0),new(6,5,0),new(6,5,3),new(0,5,3)],18,from x in xs from z in zs where !(x > 2 && x < 4 && z > 1 && z < 2.5) select new Vector3(x,5,z));
        var scan = new ImportedScan { Metadata = new("room.ply",ScanUnit.Meters,ScanUpAxis.Z,0,0,0),Points=points.ToArray(),Colors=colors.ToArray() };
        var project = new ScanProject([new(id,"room",scan)],id,null);
        var doc = StructureProvenance.Create(project,new(planes.ToArray(),points.Count,points.Count,new()));
        var observed = RoomReconstructionBuilder.Build(project,doc,new(CompleteRoomShell:false,RepairWindows:false));
        Assert.False(Index(observed).Cast(new(3,.6f,1),-Vector3.UnitZ,2,out _));
        var restored = RoomReconstructionBuilder.Build(project,doc,new());
        Assert.True(restored.HasRoomShell); Assert.True(Index(restored).Cast(new(3,.6f,1),-Vector3.UnitZ,2,out _));
        Assert.True(restored.ReconstructedWindows > 0); Assert.True(Index(restored).Cast(new(3,4,1.7f),Vector3.UnitY,2,out _));
        Assert.False(Index(restored).Cast(new(.9f,1,1),-Vector3.UnitY,2,out _));
        var noWindow = RoomReconstructionBuilder.Build(project,doc,new(RepairWindows:false));
        Assert.Equal(0,noWindow.ReconstructedWindows); Assert.False(Index(noWindow).Cast(new(3,4,1.7f),Vector3.UnitY,2,out _));
        Assert.Equal(points.ToArray(),scan.Points); Assert.Equal(colors.ToArray(),scan.Colors);
    }

    [Fact]
    public async Task ConfirmedOpeningIsProtectedEvenWhenWallPointsExistThere()
    {
        var project = await RoomPlannerTests.PlannedRoom(); var doc = project.Structure!;
        var opened = RoomPlanEditor.AddOpening(doc, 0, .5, 1.5);
        doc = doc with { Room = opened };
        var preview = RoomReconstructionBuilder.Build(project, doc, new());
        var a = opened.FloorContour[0]; var b = opened.FloorContour[1];
        var along = Vector3.Normalize(b - a); var middle = a + along + Vector3.UnitZ;
        var wall = doc.Result.Surfaces.Single(s => s.Id == opened.WallSurfaceIds[0]);
        Assert.False(Index(preview).Cast(middle - wall.Normal * .5f, wall.Normal, 1, out _));
    }

    [Fact]
    public void CleanupCanBeDisabledAndObjectDisplayCanBeHiddenWithoutChangingSource()
    {
        var floor = (from x in Enumerable.Range(0, 41) from y in Enumerable.Range(0, 41) select new Vector3(x * .05f, y * .05f, 0)).ToArray();
        var (project, doc) = Fixture(floor, [new(10, 10, 2)], StructureSurfaceKind.Floor, Vector3.UnitZ,
            [new(0, 0, 0), new(2, 0, 0), new(2, 2, 0), new(0, 2, 0)], 4);
        var preview = RoomReconstructionBuilder.Build(project, doc, new(RemoveIsolatedPoints: false));
        Assert.Single(preview.RemainingPoints!.Points); Assert.Equal(0, preview.SuppressedPoints);
        var hidden = RoomReconstructionBuilder.Build(project, doc, new(KeepObjects: false)); Assert.Null(hidden.RemainingPoints);
        Assert.Equal(floor.Length + 1, project.Active!.Source.Points.Length);
    }

    [Theory]
    [InlineData(.08f, 1)]
    [InlineData(.3f, 2)]
    public void SmallFloorDisagreementIsMergedWhileDistinctLevelsArePreserved(float height, int levels)
    {
        var left = (from x in Enumerable.Range(0, 41) from y in Enumerable.Range(0, 41) select new Vector3(x * .05f, y * .05f, 0)).ToArray();
        var right = left.Select(p => p + new Vector3(2.1f, 0, height)).ToArray();
        var (project, first) = Fixture(left, right, StructureSurfaceKind.Floor, Vector3.UnitZ,
            [new(0, 0, 0), new(2, 0, 0), new(2, 2, 0), new(0, 2, 0)], 4);
        var source = first.Result.Surfaces[0];
        var second = source with { Id = 2, Center = source.Center + new Vector3(2.1f, 0, height), Offset = -height,
            Boundary = source.Boundary.Select(p => p + new Vector3(2.1f, 0, height)).ToArray(),
            Samples = right.Select((p, i) => new StructureSample(new(project.Active!.Id, left.Length + i), p)).ToArray() };
        var doc = StructureProvenance.Create(project, first.Result with { Surfaces = [source, second] });
        var preview = RoomReconstructionBuilder.Build(project, doc, new());
        Assert.Equal(levels, preview.Scan.Points.Select(p => p.Z).Distinct().Count());
        Assert.Equal(0, project.Active!.Source.Points[0].Z); Assert.Equal(height, project.Active.Source.Points[^1].Z);
    }
    private static (ScanProject, StructureAnalysisDocument) Fixture(Vector3[] measured, Vector3[] objects, StructureSurfaceKind kind,
        Vector3 normal, Vector3[] boundary, double area)
    {
        var all = measured.Concat(objects).Select(p => new ScanPoint(p.X, p.Y, p.Z, 1)).ToArray();
        var scan = new ImportedScan { Metadata = new("fixture.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0), Points = all,
            Colors = Enumerable.Range(0, all.Length).Select(i => i < measured.Length ? 0xBAC8D4u : 0x914F2Bu + (uint)(i - measured.Length)).ToArray() };
        var project = ScanProject.FromSession(scan.ToSession());
        var center = boundary.Aggregate(Vector3.Zero, (a, b) => a + b) / boundary.Length;
        var samples = measured.Select((p, i) => new StructureSample(new(project.Active!.Id, i), p)).ToArray();
        var surface = new StructureSurface(1, kind, normal, -Vector3.Dot(center, normal), center, boundary, area, .001, samples);
        var result = new StructureAnalysisResult([surface], samples.Length, all.Length, new());
        return (project, StructureProvenance.Create(project, result));
    }
}
