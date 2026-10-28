using System.Numerics;
using SubTerra.Models;
using SubTerra.Services;
using Xunit;

namespace SubTerra.Tests;

public sealed class RoomPlannerTests
{
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
}
