using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Services;

public static class RoomPlanValidation
{
    public static void Validate(StructureAnalysisDocument document, CancellationToken token)
    {
        if (document.Room is not { } room) return;
        [DoesNotReturn] static void Invalid() => throw new InvalidDataException("Некорректный план комнаты.");
        if (room.Version is not (1 or 2) || (room.Version == 2) != room.IsManuallyEdited ||
            room.AutomaticPlan?.AutomaticPlan is not null || string.IsNullOrWhiteSpace(room.Name) || room.Name.Length > 80 ||
            room.SourceSignature?.Length != 64 || room.SourceSignature.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')) ||
            room.FloorContour is null || room.FloorContour.Length is < 3 or > 24 || room.CeilingContour is null ||
            room.WallSurfaceIds is null || room.WallSurfaceIds.Length != room.FloorContour.Length ||
            (!room.IsManuallyEdited && room.WallSurfaceIds.Distinct().Count() != room.WallSurfaceIds.Length) ||
            room.Gaps is null || room.Gaps.Length > 256 || room.IncompleteWallIds is null || room.IncompleteWallIds.Length > 24) Invalid();
        if (room.CeilingSurfaceId is null ? room.CeilingContour.Length != 0 : room.CeilingContour.Length != room.FloorContour.Length) Invalid();
        if (room.SurfaceIds is { } scoped && (scoped.Length > 24 || scoped.Distinct().Count() != scoped.Length || scoped.Any(id => document.Result.Surfaces.All(s => s.Id != id)))) Invalid();
        if (room.InteriorPoint is { } seed && !InspectionPoint.From(seed).IsValid) Invalid();
        var ids = document.Result.Surfaces.Select(s => s.Id).ToHashSet();
        if (!ids.Contains(room.FloorSurfaceId) || room.CeilingSurfaceId is { } ceiling && !ids.Contains(ceiling) ||
            room.WallSurfaceIds.Any(id => !ids.Contains(id) || id == room.FloorSurfaceId || id == room.CeilingSurfaceId) ||
            room.FloorSurfaceId == room.CeilingSurfaceId || room.IncompleteWallIds.Any(id => !room.WallSurfaceIds.Contains(id)) ||
            room.IncompleteWallIds.Distinct().Count() != room.IncompleteWallIds.Length) Invalid();
        if (room.FloorContour.Concat(room.CeilingContour).Any(p => !InspectionPoint.From(p).IsValid) ||
            room.AreaSquareMeters is < .5 or > 1_000_000 || room.PerimeterMeters is < 1 or > 1_000_000) Invalid();
        double crossSign = 0;
        for (var i = 0; i < room.FloorContour.Length; i++)
        {
            var a = room.FloorContour[i]; var b = room.FloorContour[(i + 1) % room.FloorContour.Length]; var c = room.FloorContour[(i + 2) % room.FloorContour.Length];
            var cross = (b.X - (double)a.X) * (c.Y - (double)b.Y) - (b.Y - (double)a.Y) * (c.X - (double)b.X);
            var edgeLength = Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));
            // A float midpoint can fall fractions of a micrometre outside its original edge.
            // Operator contours allow that distance, while automatic corners remain strictly convex.
            var turnTolerance = room.IsManuallyEdited ? Math.Max(1e-7, edgeLength * 1e-6) : 1e-7;
            if (cross < -turnTolerance || (!room.IsManuallyEdited && cross <= 1e-7) || edgeLength < .01) Invalid();
            // Global convexity also rejects self-intersecting star contours with positive local turns.
            if (room.FloorContour.Any(p => (b.X - (double)a.X) * (p.Y - (double)a.Y) - (b.Y - (double)a.Y) * (p.X - (double)a.X) < -turnTolerance)) Invalid();
            if (room.HasHeight && Vector2.DistanceSquared(new(a.X, a.Y), new(room.CeilingContour[i].X, room.CeilingContour[i].Y)) > 1e-8f) Invalid();
            crossSign += cross;
        }
        if (!double.IsFinite(crossSign) || room.MinimumHeight is { } min && min < .5 || room.MaximumHeight is { } max && max > 100) Invalid();
        foreach (var gap in room.Gaps)
            if (gap is null || gap.EdgeIndex < 0 || gap.EdgeIndex >= room.WallSurfaceIds.Length || gap.WallSurfaceId != room.WallSurfaceIds[gap.EdgeIndex] ||
                !double.IsFinite(gap.Start) || !double.IsFinite(gap.End) || gap.Start < 0 || gap.End > 1 || gap.Start >= gap.End) Invalid();
        if (room.AutomaticPlan is { } automatic)
        {
            if (automatic.Version != 1 || room.SourceSignature != automatic.SourceSignature || room.FloorSurfaceId != automatic.FloorSurfaceId ||
                room.CeilingSurfaceId != automatic.CeilingSurfaceId || !(room.SurfaceIds ?? []).SequenceEqual(automatic.SurfaceIds ?? []) || room.InteriorPoint != automatic.InteriorPoint ||
                room.WallSurfaceIds.Any(id => !automatic.WallSurfaceIds.Contains(id))) Invalid();
            Validate(document with { Version = 2, Room = automatic }, token);
            if (!document.IsStale)
            {
                var floor = document.Result.Surfaces.Single(s => s.Id == room.FloorSurfaceId);
                if (room.FloorContour.Any(p => Math.Abs(PlanePatch.Distance(floor.Normal, floor.Offset, p)) > .05)) Invalid();
                if (room.CeilingSurfaceId is { } ceilingId)
                {
                    var ceilingPlane = document.Result.Surfaces.Single(s => s.Id == ceilingId);
                    if (room.CeilingContour.Any(p => Math.Abs(PlanePatch.Distance(ceilingPlane.Normal, ceilingPlane.Offset, p)) > .05)) Invalid();
                }
            }
            return;
        }
        // A stale result stays inspectable, but cannot be used or rendered as a current room.
        if (!RoomPlanner.IsCurrent(document)) return;
        var expected = RoomPlanner.Build(document, room.Name, token, room.SurfaceIds, room.InteriorPoint).Room;
        if (expected is null || expected.FloorSurfaceId != room.FloorSurfaceId || expected.CeilingSurfaceId != room.CeilingSurfaceId ||
            !expected.WallSurfaceIds.SequenceEqual(room.WallSurfaceIds) || !Close(expected.FloorContour, room.FloorContour) ||
            !Close(expected.CeilingContour, room.CeilingContour) || !expected.Gaps.SequenceEqual(room.Gaps) ||
            !expected.IncompleteWallIds.SequenceEqual(room.IncompleteWallIds)) Invalid();
    }
    private static bool Close(Vector3[] a, Vector3[] b) => a.Length == b.Length && a.Zip(b).All(pair => Vector3.DistanceSquared(pair.First, pair.Second) < .0004f);
    public static bool Same(RoomPlan a, RoomPlan b) => a.Name == b.Name && a.Version == b.Version && a.SourceSignature == b.SourceSignature &&
        a.FloorSurfaceId == b.FloorSurfaceId && a.CeilingSurfaceId == b.CeilingSurfaceId && a.FloorContour.SequenceEqual(b.FloorContour) &&
        a.CeilingContour.SequenceEqual(b.CeilingContour) && a.WallSurfaceIds.SequenceEqual(b.WallSurfaceIds) && a.Gaps.SequenceEqual(b.Gaps) &&
        a.IncompleteWallIds.SequenceEqual(b.IncompleteWallIds) && (a.SurfaceIds ?? []).SequenceEqual(b.SurfaceIds ?? []) && a.InteriorPoint == b.InteriorPoint &&
        (a.AutomaticPlan is null ? b.AutomaticPlan is null : b.AutomaticPlan is not null && Same(a.AutomaticPlan, b.AutomaticPlan));
}
