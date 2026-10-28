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
        if (room.Version != 1 || string.IsNullOrWhiteSpace(room.Name) || room.Name.Length > 80 ||
            room.SourceSignature?.Length != 64 || room.SourceSignature.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')) ||
            room.FloorContour is null || room.FloorContour.Length is < 3 or > 24 || room.CeilingContour is null ||
            room.WallSurfaceIds is null || room.WallSurfaceIds.Length != room.FloorContour.Length ||
            room.WallSurfaceIds.Distinct().Count() != room.WallSurfaceIds.Length ||
            room.Gaps is null || room.Gaps.Length > 256 || room.IncompleteWallIds is null || room.IncompleteWallIds.Length > 24) Invalid();
        if (room.CeilingSurfaceId is null ? room.CeilingContour.Length != 0 : room.CeilingContour.Length != room.FloorContour.Length) Invalid();
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
            if (cross <= 1e-7 || Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y)) < .01) Invalid();
            crossSign += cross;
        }
        if (!double.IsFinite(crossSign) || room.MinimumHeight is { } min && min < .5 || room.MaximumHeight is { } max && max > 100) Invalid();
        foreach (var gap in room.Gaps)
            if (gap is null || gap.EdgeIndex < 0 || gap.EdgeIndex >= room.WallSurfaceIds.Length || gap.WallSurfaceId != room.WallSurfaceIds[gap.EdgeIndex] ||
                !double.IsFinite(gap.Start) || !double.IsFinite(gap.End) || gap.Start < 0 || gap.End > 1 || gap.Start >= gap.End) Invalid();
        // A stale result stays inspectable, but cannot be used or rendered as a current room.
        if (!RoomPlanner.IsCurrent(document)) return;
        var expected = RoomPlanner.Build(document, room.Name, token).Room;
        if (expected is null || expected.FloorSurfaceId != room.FloorSurfaceId || expected.CeilingSurfaceId != room.CeilingSurfaceId ||
            !expected.WallSurfaceIds.SequenceEqual(room.WallSurfaceIds) || !Close(expected.FloorContour, room.FloorContour) ||
            !Close(expected.CeilingContour, room.CeilingContour) || !expected.Gaps.SequenceEqual(room.Gaps) ||
            !expected.IncompleteWallIds.SequenceEqual(room.IncompleteWallIds)) Invalid();
    }
    private static bool Close(Vector3[] a, Vector3[] b) => a.Length == b.Length && a.Zip(b).All(pair => Vector3.DistanceSquared(pair.First, pair.Second) < .0004f);
}
