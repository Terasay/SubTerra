using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Services;

public sealed record EvaluatedScanProject(ImportedScan? VisibleScan, int[] CloudOnlyIndices,
    (Vector3 Minimum, Vector3 Maximum) Bounds);

public static class ScanProjectEvaluator
{
    public static EvaluatedScanProject Evaluate(ScanProject project, CancellationToken token = default)
    {
        project.Validate(token);
        var objects = project.Objects.Where(o => o.IsVisible && o.PointCount > 0).ToArray();
        var count = objects.Sum(o => o.PointCount);
        if (count == 0) return new(null, [], (Vector3.Zero, Vector3.Zero));
        var points = new ScanPoint[count];
        var hasColors = objects.Any(o => o.Tint.HasValue || o.Source.Colors.Length > 0);
        var colors = hasColors ? new uint[count] : [];
        var triangles = new List<int>(); var cloudOnly = new List<int>();
        var next = 0;
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var o in objects)
        {
            var matrix = o.Matrix;
            var remap = o.Mask is null ? null : new int[o.Source.Points.Length];
            if (remap is not null) Array.Fill(remap, -1);
            var offset = next;
            for (var i = 0; i < o.Source.Points.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!o.Includes(i)) continue;
                var position = Vector3.Transform(o.Source.Points[i].Position, matrix);
                if (!InspectionPoint.From(position).IsValid) throw new System.IO.InvalidDataException("Объект выходит за допустимые координаты ±100 км.");
                points[next] = new(position.X, position.Y, position.Z, o.Source.Points[i].Intensity);
                if (hasColors) colors[next] = o.Tint ?? (o.Source.Colors.Length > 0 ? o.Source.Colors[i] : 0xd0dbe2u);
                if (remap is not null) remap[i] = next;
                if (!o.Source.HasMesh) cloudOnly.Add(next);
                min = Vector3.Min(min, position); max = Vector3.Max(max, position);
                ++next;
            }
            for (var i = 0; i < o.Source.Triangles.Length; i += 3)
            {
                if ((i % 4095) == 0) token.ThrowIfCancellationRequested();
                var a = o.Source.Triangles[i]; var b = o.Source.Triangles[i + 1]; var c = o.Source.Triangles[i + 2];
                if (remap is null) { triangles.Add(a + offset); triangles.Add(b + offset); triangles.Add(c + offset); }
                else if (remap[a] >= 0 && remap[b] >= 0 && remap[c] >= 0)
                { triangles.Add(remap[a]); triangles.Add(remap[b]); triangles.Add(remap[c]); }
            }
        }
        var scan = new ImportedScan { Metadata = new("SubTerra-project.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0),
            Points = points, Colors = colors, Triangles = triangles.ToArray() };
        return new(scan, cloudOnly.ToArray(), (min, max));
    }
    public static (Vector3 Minimum, Vector3 Maximum) ObjectBounds(ScanObject o)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        var matrix = o.Matrix;
        for (var i = 0; i < o.Source.Points.Length; i++)
        {
            if (!o.Includes(i)) continue;
            var p = Vector3.Transform(o.Source.Points[i].Position, matrix);
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
        return o.PointCount == 0 ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }
}
