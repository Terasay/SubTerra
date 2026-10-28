using System.Numerics;

namespace SubTerra.Models;

/// <summary>A convex envelope inferred from reviewed planes, not proof of continuous walls.</summary>
public sealed record RoomGap(int WallSurfaceId, int EdgeIndex, double Start, double End);
public sealed record RoomPlan(string Name, int Version, string SourceSignature, int FloorSurfaceId, int? CeilingSurfaceId,
    int[] WallSurfaceIds, Vector3[] FloorContour, Vector3[] CeilingContour, RoomGap[] Gaps, int[] IncompleteWallIds)
{
    public bool HasHeight => CeilingContour.Length == FloorContour.Length;
    public double AreaSquareMeters
    {
        get
        {
            double area = 0; var origin = FloorContour[0];
            for (var i = 0; i < FloorContour.Length; i++)
            {
                var a = FloorContour[i] - origin; var b = FloorContour[(i + 1) % FloorContour.Length] - origin;
                area += a.X * (double)b.Y - a.Y * (double)b.X;
            }
            return Math.Abs(area) / 2;
        }
    }
    public double PerimeterMeters => Enumerable.Range(0, FloorContour.Length).Sum(i =>
    {
        var a = FloorContour[i]; var b = FloorContour[(i + 1) % FloorContour.Length];
        return Math.Sqrt(Math.Pow(a.X - (double)b.X, 2) + Math.Pow(a.Y - (double)b.Y, 2));
    });
    public double? MinimumHeight => HasHeight ? FloorContour.Zip(CeilingContour, (a, b) => (double)b.Z - a.Z).Min() : null;
    public double? MaximumHeight => HasHeight ? FloorContour.Zip(CeilingContour, (a, b) => (double)b.Z - a.Z).Max() : null;
}
public sealed record RoomBuildResult(RoomPlan? Room, string Message);
