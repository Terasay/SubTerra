using System.Numerics;
using HelixToolkit.SharpDX;
using SubTerra.Models;

namespace SubTerra.Rendering;

public static class RoomGeometry
{
    public static (LineGeometry3D Outline, LineGeometry3D Gaps) Create(RoomPlan room, PointClipPlane? clip)
    {
        var outline = new List<Vector3>(); var gaps = new List<Vector3>();
        void Add(List<Vector3> target, Vector3 a, Vector3 b)
        {
            if (clip is { } plane && !plane.TryClipSegment(ref a, ref b)) return;
            target.Add(a); target.Add(b);
        }
        for (var i = 0; i < room.FloorContour.Length; i++)
        {
            var next = (i + 1) % room.FloorContour.Length;
            Add(outline, room.FloorContour[i], room.FloorContour[next]);
            if (!room.HasHeight) continue;
            Add(outline, room.CeilingContour[i], room.CeilingContour[next]);
            Add(outline, room.FloorContour[i], room.CeilingContour[i]);
        }
        foreach (var gap in room.Gaps)
        {
            var a = room.FloorContour[gap.EdgeIndex]; var b = room.FloorContour[(gap.EdgeIndex + 1) % room.FloorContour.Length];
            Add(gaps, Vector3.Lerp(a, b, (float)gap.Start) + Vector3.UnitZ * .04f, Vector3.Lerp(a, b, (float)gap.End) + Vector3.UnitZ * .04f);
        }
        return (SceneGeometry.Lines(outline), SceneGeometry.Lines(gaps));
    }
}
