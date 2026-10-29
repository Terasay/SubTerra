using System.Numerics;
using SubTerra.Models;
using SubTerra.Services;
using Xunit;

namespace SubTerra.Tests;

public sealed class RoomPlannerTests
{
    internal static async Task<ScanProject> TwoRooms()
    {
        var left = await StructureAnalysisTests.Classroom(); var right = await StructureAnalysisTests.Classroom();
        right = right.Replace(right.Active! with { Transform = new(6) });
        var analyzer = new PlaneStructureAnalyzer();
        var a = await analyzer.AnalyzeAsync(left, new(), null, CancellationToken.None);
        var b = await analyzer.AnalyzeAsync(right, new(), null, CancellationToken.None);
        var project = left with { Objects = [left.Active!, right.Active!] };
        var surfaces = a.Surfaces.Concat(b.Surfaces).Select((s, i) => s with { Id = i + 1 }).ToArray();
        var doc = Review(StructureProvenance.Create(project, new(surfaces, a.SampleCount + b.SampleCount, a.SourcePointCount + b.SourcePointCount, new(MaximumPlanes:24))));
        int[] Ids(Guid id) => doc.Result.Surfaces.Where(s => s.Samples[0].Source.ObjectId == id && s.Kind != StructureSurfaceKind.Unknown).Select(s => s.Id).ToArray();
        var first = RoomPlanner.Build(doc, "Аудитория А", surfaceIds:Ids(left.Active!.Id)).Room;
        var second = RoomPlanner.Build(doc, "Аудитория Б", surfaceIds:Ids(right.Active!.Id)).Room;
        Assert.NotNull(first); Assert.NotNull(second);
        var id = Guid.NewGuid();
        doc = (doc with { Room = first, ActiveRoomId = id, Rooms = [new(id, "Этаж 1", first), new(Guid.NewGuid(), "Этаж 1", second)] }).NormalizeVersion();
        return project with { Structure = doc };
    }

    [Fact]
    public async Task RoomsUseTheirOwnPlanesAndShareOnlyGeometricallyVerifiedBoundaries()
    {
        var project = await TwoRooms(); var doc = project.Structure!;
        StructureProvenance.Validate(doc, project);
        Assert.All(doc.Rooms!, r => Assert.InRange(r.Plan.AreaSquareMeters, 29.8, 30.2));
        var link = Assert.Single(RoomTopology.Connections(doc)); Assert.False(link.HasConfirmedOpening);
        var room = doc.Room!;
        var sharedEdge = Enumerable.Range(0, room.FloorContour.Length).Single(i => Math.Abs(room.FloorContour[i].X - 6) < .02 && Math.Abs(room.FloorContour[(i + 1) % room.FloorContour.Length].X - 6) < .02);
        var opened = RoomPlanEditor.AddOpening(doc, sharedEdge, .5, 1.5);
        var edited = (doc with { Room = opened }).NormalizeVersion();
        Assert.True(Assert.Single(RoomTopology.Connections(edited)).HasConfirmedOpening);
        var changedOtherReview = doc.Result.Surfaces.First(s => !room.SurfaceIds!.Contains(s.Id) && s.Kind == StructureSurfaceKind.Wall);
        var changed = doc with { Result = doc.Result with { Surfaces = doc.Result.Surfaces.Select(s => s.Id == changedOtherReview.Id ? s with { Review = StructureReviewState.Rejected } : s).ToArray() } };
        Assert.True(RoomPlanner.IsCurrent(changed));
        Assert.False(RoomPlanner.IsCurrent(changed with { Room = changed.Rooms![1].Plan }));
    }

    internal static StructureAnalysisDocument Review(StructureAnalysisDocument document) => document with
    { Result = document.Result with { Surfaces = document.Result.Surfaces.Select(s => s.Kind == StructureSurfaceKind.Unknown ? s : s with { Review = StructureReviewState.Confirmed }).ToArray() } };
    internal static async Task<ScanProject> ReviewedRoom()
    {
        var project = await StructurePersistenceTests.AnalyzedRoom(); return project with { Structure = Review(project.Structure!) };
    }
    internal static async Task<ScanProject> PlannedRoom()
    {
        var project = await ReviewedRoom(); var result = RoomPlanner.Build(project.Structure!, "Аудитория 101");
        Assert.NotNull(result.Room); return project with { Structure = project.Structure! with { Version = 2, Room = result.Room } };
    }

    [Fact]
    public async Task ClassroomProducesAThirtySquareMeterPlanWithMeasuredHeight()
    {
        var project = await ReviewedRoom(); var result = RoomPlanner.Build(project.Structure!, "Аудитория 101");
        var room = Assert.IsType<RoomPlan>(result.Room);
        Assert.Equal(4, room.FloorContour.Length); Assert.Equal(4, room.CeilingContour.Length); Assert.Equal(4, room.WallSurfaceIds.Distinct().Count());
        Assert.InRange(room.AreaSquareMeters, 29.8, 30.2); Assert.InRange(room.PerimeterMeters, 21.9, 22.1);
        Assert.InRange(room.MinimumHeight!.Value, 2.98, 3.02); Assert.InRange(room.MaximumHeight!.Value, 2.98, 3.02);
        Assert.Empty(room.Gaps); Assert.Empty(room.IncompleteWallIds);
        Assert.Equal(3747, project.PointCount); Assert.Null(project.Active!.Mask);
        RoomPlanValidation.Validate(project.Structure! with { Version = 2, Room = room }, CancellationToken.None);
    }

    [Fact]
    public async Task MissingCeilingKeepsTheFloorPlanAndLeavesHeightUnknown()
    {
        var project = await ReviewedRoom(); var doc = project.Structure!;
        doc = doc with { Result = doc.Result with { Surfaces = doc.Result.Surfaces.Select(s => s.Kind == StructureSurfaceKind.Ceiling ? s with { Review = StructureReviewState.Proposed } : s).ToArray() } };
        var room = Assert.IsType<RoomPlan>(RoomPlanner.Build(doc, "Без потолка").Room);
        Assert.InRange(room.AreaSquareMeters, 29.8, 30.2); Assert.False(room.HasHeight); Assert.Null(room.MinimumHeight); Assert.Empty(room.CeilingContour);
    }

    [Fact]
    public async Task MissingWallDoesNotUseAFakeBoundingBox()
    {
        var doc = (await ReviewedRoom()).Structure!; var wall = doc.Result.Surfaces.First(s => s.Kind == StructureSurfaceKind.Wall);
        doc = doc with { Result = doc.Result with { Surfaces = doc.Result.Surfaces.Select(s => s.Id == wall.Id ? s with { Review = StructureReviewState.Rejected } : s).ToArray() } };
        var result = RoomPlanner.Build(doc, "Незавершённая"); Assert.Null(result.Room); Assert.NotEmpty(result.Message);
    }

    [Fact]
    public async Task OnlyOperatorConfirmedSurfacesCanBeUsed()
    {
        var doc = (await StructurePersistenceTests.AnalyzedRoom()).Structure!;
        Assert.Null(RoomPlanner.Build(doc, "Кандидаты").Room);
        doc = Review(doc) with { IsStale = true };
        Assert.Null(RoomPlanner.Build(doc, "Устаревшие").Room);
    }

    [Fact]
    public async Task ATableMisclassifiedAsFloorCannotSupportAWholeRoom()
    {
        var doc = (await ReviewedRoom()).Structure!;
        doc = doc with { Result = doc.Result with { Surfaces = doc.Result.Surfaces.Select(s => s.Kind == StructureSurfaceKind.Floor ? s with { Review = StructureReviewState.Rejected } :
            s.Kind == StructureSurfaceKind.Unknown ? s with { Kind = StructureSurfaceKind.Floor, Review = StructureReviewState.Confirmed } : s).ToArray() } };
        Assert.Null(RoomPlanner.Build(doc, "Стол").Room);
    }

    [Fact]
    public async Task RotationAndTranslationDoNotChangePlanDimensions()
    {
        var project = await StructureAnalysisTests.Classroom(); var obj = project.Active!;
        project = project.Replace(obj with { Transform = new(120, -37, 4, RotateZ: 37) });
        var result = await new PlaneStructureAnalyzer().AnalyzeAsync(project, new(), null, CancellationToken.None);
        var doc = Review(StructureProvenance.Create(project, result));
        var room = Assert.IsType<RoomPlan>(RoomPlanner.Build(doc, "Поворот").Room);
        Assert.InRange(room.AreaSquareMeters, 29.8, 30.2); Assert.InRange(room.PerimeterMeters, 21.9, 22.1);
        Assert.All(room.FloorContour, p => Assert.InRange(p.Z, 3.98, 4.02));
    }

    [Fact]
    public async Task DoorLikeGapInLowerWallIsReportedWithoutAssumingAContinuousWall()
    {
        var project = await StructureAnalysisTests.Classroom(); var obj = project.Active!;
        project = project.Replace(obj with { Mask = new(obj.Source.Points.Select(p => !(Math.Abs(p.Y) < .01 && p.X > 2 && p.X < 3.2 && p.Z > .01 && p.Z < 2.2)).ToArray()) });
        var result = await new PlaneStructureAnalyzer().AnalyzeAsync(project, new(), null, CancellationToken.None);
        var doc = Review(StructureProvenance.Create(project, result));
        var room = Assert.IsType<RoomPlan>(RoomPlanner.Build(doc, "С проёмом").Room);
        Assert.InRange(room.AreaSquareMeters, 29.8, 30.2); Assert.NotEmpty(room.Gaps);
        Assert.Contains(room.Gaps, gap =>
        {
            var a = room.FloorContour[gap.EdgeIndex]; var b = room.FloorContour[(gap.EdgeIndex + 1) % room.FloorContour.Length];
            return Math.Abs(a.Y) < .02 && Math.Abs(b.Y) < .02 && Vector3.Distance(a, b) * (gap.End - gap.Start) >= .6;
        });
    }

    [Fact]
    public async Task ReviewChangesInvalidateTheRoomAndUndoingThemRestoresItsSignature()
    {
        var doc = (await PlannedRoom()).Structure!; Assert.True(RoomPlanner.IsCurrent(doc));
        var wall = doc.Result.Surfaces.First(s => s.Kind == StructureSurfaceKind.Wall);
        var changed = doc with { Result = doc.Result with { Surfaces = doc.Result.Surfaces.Select(s => s.Id == wall.Id ? s with { Review = StructureReviewState.Rejected } : s).ToArray() } };
        Assert.False(RoomPlanner.IsCurrent(changed)); Assert.True(RoomPlanner.IsCurrent(doc));
    }

    [Fact]
    public async Task CancellationDoesNotChangeReviewedSurfaces()
    {
        var doc = (await ReviewedRoom()).Structure!; using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => RoomPlanner.Build(doc, "Комната", cts.Token)); Assert.Null(doc.Room);
    }

    [Fact]
    public async Task VertexCorrectionsPreserveAutomaticGeometryAndCanAddAndRemoveVertices()
    {
        var doc = (await PlannedRoom()).Structure!; var room = doc.Room!;
        var center = room.FloorContour.Aggregate(Vector3.Zero, (sum, p) => sum + p) / room.FloorContour.Length;
        var point = Vector3.Lerp(room.FloorContour[0], center, .05f);
        var moved = RoomPlanEditor.MoveVertex(doc, 0, point.X, point.Y);
        Assert.Same(room, moved.AutomaticPlan); Assert.Equal(room.FloorContour[0], moved.AutomaticPlan!.FloorContour[0]);
        Assert.NotEqual(room.AreaSquareMeters, moved.AreaSquareMeters);
        var edited = doc with { Version = 3, Room = moved };
        RoomPlanValidation.Validate(edited, CancellationToken.None);
        var inserted = RoomPlanEditor.InsertVertex(edited, 0);
        Assert.Equal(5, inserted.FloorContour.Length); Assert.Equal(moved.AreaSquareMeters, inserted.AreaSquareMeters, 4);
        var removed = RoomPlanEditor.RemoveVertex(edited with { Room = inserted }, 1);
        Assert.Equal(moved.FloorContour, removed.FloorContour); Assert.Equal(moved.CeilingContour, removed.CeilingContour);
        Assert.Same(room, removed.AutomaticPlan);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryRoomEdgeCanBeSplitWithoutChangingItsShape(int edge)
    {
        var project = await PlannedRoom(); var doc = project.Structure!; var original = doc.Room!;
        var error = Record.Exception(() => RoomPlanEditor.InsertVertex(doc, edge));
        Assert.True(error is null, $"Edge {edge}: {error?.Message}; contour {string.Join("; ", original.FloorContour.Select(p => $"{p.X:R},{p.Y:R}"))}");
        var inserted = RoomPlanEditor.InsertVertex(doc, edge);
        Assert.Equal(5, inserted.FloorContour.Length); Assert.Equal(original.AreaSquareMeters, inserted.AreaSquareMeters, 4);
        RoomPlanValidation.Validate(doc with { Version = 3, Room = inserted }, CancellationToken.None);
        var restored = RoomPlanEditor.RemoveVertex(doc with { Version = 3, Room = inserted }, edge + 1);
        Assert.Equal(original.FloorContour, restored.FloorContour);
    }

    [Fact]
    public async Task SplittingAnEdgePreservesOpeningLengthsAndRejectsDestructiveMerges()
    {
        var doc = (await PlannedRoom()).Structure!; var room = doc.Room!;
        var a = room.FloorContour[0]; var b = room.FloorContour[1];
        var length = Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));
        var opened = RoomPlanEditor.AddOpening(doc, 0, length * .25, length * .75);
        var edited = doc with { Version = 3, Room = opened };
        Assert.True(Assert.Single(opened.Gaps).IsConfirmedOpening);
        Assert.Throws<System.IO.InvalidDataException>(() => RoomPlanEditor.AddOpening(edited, 0, length * .3, length * .6));
        var split = RoomPlanEditor.InsertVertex(edited, 0); Assert.Equal(2, split.Gaps.Length);
        Assert.Equal(.5, split.Gaps.Sum(g => (g.End - g.Start) * .5), 6);
        Assert.Throws<System.IO.InvalidDataException>(() => RoomPlanEditor.RemoveVertex(edited with { Room = split }, 1));
        var cleared = split;
        foreach (var gap in split.Gaps) cleared = RoomPlanEditor.RemoveGap(edited with { Room = cleared }, gap);
        var merged = RoomPlanEditor.RemoveVertex(edited with { Room = cleared }, 1);
        Assert.Equal(room.FloorContour, merged.FloorContour);
    }

    [Fact]
    public async Task InvalidOrCancelledEditsDoNotModifyTheRoom()
    {
        var doc = (await PlannedRoom()).Structure!; var room = doc.Room!;
        Assert.Throws<System.IO.InvalidDataException>(() => RoomPlanEditor.MoveVertex(doc, 0, double.NaN, 0));
        Assert.Throws<System.IO.InvalidDataException>(() => RoomPlanEditor.MoveVertex(doc, 0, room.FloorContour[2].X, room.FloorContour[2].Y));
        Assert.Throws<System.IO.InvalidDataException>(() => RoomPlanEditor.AddOpening(doc, 0, -1, 1));
        Assert.Throws<System.IO.InvalidDataException>(() => RoomPlanEditor.InsertVertex(doc with { IsStale = true }, 0));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => RoomPlanEditor.InsertVertex(doc, 0, cts.Token));
        Assert.False(room.IsManuallyEdited); Assert.Equal(4, room.FloorContour.Length);
    }
}
