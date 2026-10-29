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
        var triangles = new List<int>();
        var referenced = new bool[count];
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
                min = Vector3.Min(min, position); max = Vector3.Max(max, position);
                ++next;
            }
            for (var i = 0; i < o.Source.Triangles.Length; i += 3)
            {
                if ((i % 4095) == 0) token.ThrowIfCancellationRequested();
                var a = o.Source.Triangles[i]; var b = o.Source.Triangles[i + 1]; var c = o.Source.Triangles[i + 2];
                if (remap is null) { a += offset; b += offset; c += offset; }
                else { a = remap[a]; b = remap[b]; c = remap[c]; }
                if (a < 0 || b < 0 || c < 0) continue;
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
                referenced[a] = referenced[b] = referenced[c] = true;
            }
        }
        var cloudOnly = new List<int>();
        for (var i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (!referenced[i]) cloudOnly.Add(i);
        }
        var scan = new ImportedScan { Metadata = new("SubTerra-project.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0)
            { IsSynthetic = project.Objects.Any(o => o.IsVisible && o.PointCount > 0 && o.Source.Metadata.IsSynthetic) },
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

public sealed record ScanAlignmentResult(Matrix4x4 Correction, double RmsMeters, int MatchedPoints, int SourcePoints);

/// <summary>Rigid registration only: manual correspondences followed by optional bounded point-to-point ICP.</summary>
public static class ScanAlignment
{
    public static Matrix4x4 Fit(IReadOnlyList<Vector3> source, IReadOnlyList<Vector3> target, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (source.Count < 3 || source.Count != target.Count || source.Concat(target).Any(p => !InspectionPoint.From(p).IsValid))
            throw new System.IO.InvalidDataException("Нужны минимум три пары корректных точек.");
        bool SpansPlane(IReadOnlyList<Vector3> points)
        {
            var direction = points.Skip(1).MaxBy(p => Vector3.DistanceSquared(p, points[0])) - points[0];
            return points.Skip(1).Any(p => Vector3.Cross(direction, p - points[0]).LengthSquared() > 1e-16f);
        }
        if (!SpansPlane(source) || !SpansPlane(target)) throw new System.IO.InvalidDataException("Опорные точки не должны лежать на одной прямой.");
        var a = new double[3]; var b = new double[3];
        foreach (var p in source) { a[0] += p.X; a[1] += p.Y; a[2] += p.Z; }
        foreach (var p in target) { b[0] += p.X; b[1] += p.Y; b[2] += p.Z; }
        for (var i = 0; i < 3; i++) { a[i] /= source.Count; b[i] /= source.Count; }
        var s = new double[3, 3];
        for (var i = 0; i < source.Count; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            double[] x = [source[i].X - a[0], source[i].Y - a[1], source[i].Z - a[2]];
            double[] y = [target[i].X - b[0], target[i].Y - b[1], target[i].Z - b[2]];
            for (var j = 0; j < 3; j++) for (var k = 0; k < 3; k++) s[j, k] += x[j] * y[k];
        }
        var n = new double[4, 4]; var v = new double[4, 4]; for (var i = 0; i < 4; i++) v[i, i] = 1;
        n[0, 0] = s[0, 0] + s[1, 1] + s[2, 2];
        n[1, 1] = s[0, 0] - s[1, 1] - s[2, 2]; n[2, 2] = -s[0, 0] + s[1, 1] - s[2, 2]; n[3, 3] = -s[0, 0] - s[1, 1] + s[2, 2];
        n[0, 1] = n[1, 0] = s[1, 2] - s[2, 1]; n[0, 2] = n[2, 0] = s[2, 0] - s[0, 2]; n[0, 3] = n[3, 0] = s[0, 1] - s[1, 0];
        n[1, 2] = n[2, 1] = s[0, 1] + s[1, 0]; n[1, 3] = n[3, 1] = s[0, 2] + s[2, 0]; n[2, 3] = n[3, 2] = s[1, 2] + s[2, 1];
        for (var iteration = 0; iteration < 80; iteration++)
        {
            token.ThrowIfCancellationRequested();
            var p = 0; var q = 1;
            for (var j = 0; j < 4; j++) for (var k = j + 1; k < 4; k++) if (Math.Abs(n[j, k]) > Math.Abs(n[p, q])) { p = j; q = k; }
            if (Math.Abs(n[p, q]) < 1e-14) break;
            var angle = .5 * Math.Atan2(2 * n[p, q], n[q, q] - n[p, p]); var c = Math.Cos(angle); var sn = Math.Sin(angle);
            var app = n[p, p]; var aqq = n[q, q]; var apq = n[p, q];
            n[p, p] = c * c * app - 2 * sn * c * apq + sn * sn * aqq;
            n[q, q] = sn * sn * app + 2 * sn * c * apq + c * c * aqq; n[p, q] = n[q, p] = 0;
            for (var k = 0; k < 4; k++)
            {
                if (k != p && k != q) { var kp = n[k, p]; var kq = n[k, q]; n[k, p] = n[p, k] = c * kp - sn * kq; n[k, q] = n[q, k] = sn * kp + c * kq; }
                var vp = v[k, p]; var vq = v[k, q]; v[k, p] = c * vp - sn * vq; v[k, q] = sn * vp + c * vq;
            }
        }
        var best = Enumerable.Range(0, 4).MaxBy(i => n[i, i]);
        var rotation = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new((float)v[1, best], (float)v[2, best], (float)v[3, best], (float)v[0, best])));
        var shift = new Vector3((float)b[0], (float)b[1], (float)b[2]) - Vector3.Transform(new((float)a[0], (float)a[1], (float)a[2]), rotation);
        return rotation * Matrix4x4.CreateTranslation(shift);
    }
    public static Vector3[] Sample(ScanObject obj, int limit = 5000, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var count = Math.Min(limit, obj.PointCount); var points = new Vector3[count]; var rank = 0; var next = 0;
        for (var i = 0; i < obj.Source.Points.Length && next < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (!obj.Includes(i)) continue;
            if (rank++ != (long)next * obj.PointCount / count) continue;
            points[next++] = Vector3.Transform(obj.Source.Points[i].Position, obj.Matrix);
        }
        return points;
    }
    public static ScanAlignmentResult Refine(Vector3[] source, Vector3[] target, Matrix4x4 initial, double maximumDistance,
        bool refine, CancellationToken token = default)
    {
        if (!double.IsFinite(maximumDistance) || maximumDistance is < .000001 or > 100 || source.Length < 3 || target.Length < 3)
            throw new System.IO.InvalidDataException("Для совмещения нужны точки обоих сканов и допустимая дистанция от 0,000001 до 100 м.");
        (long, long, long) Key(Vector3 p) => ((long)Math.Floor(p.X / maximumDistance), (long)Math.Floor(p.Y / maximumDistance), (long)Math.Floor(p.Z / maximumDistance));
        var cells = new Dictionary<(long, long, long), List<Vector3>>();
        foreach (var p in target) { var key = Key(p); if (!cells.TryGetValue(key, out var list)) cells[key] = list = []; list.Add(p); }
        var correction = initial; var previous = double.PositiveInfinity;
        for (var iteration = 0; iteration < (refine ? 12 : 1); iteration++)
        {
            var pairs = Matches(correction);
            if (pairs.Count < 3) throw new System.IO.InvalidDataException("Недостаточно перекрытия. Проверьте опорные пары и допустимую дистанцию.");
            if (!refine) break;
            var kept = pairs.OrderBy(p => p.Distance).Take(Math.Max(3, (int)(pairs.Count * .8))).ToArray();
            var rms = Math.Sqrt(kept.Average(p => p.Distance));
            if (previous - rms < 1e-7) break;
            previous = rms;
            correction *= Fit(kept.Select(p => p.Source).ToArray(), kept.Select(p => p.Target).ToArray(), token);
        }
        var final = Matches(correction);
        if (final.Count < 3) throw new System.IO.InvalidDataException("После уточнения недостаточно перекрытия.");
        return new(correction, Math.Sqrt(final.Average(p => p.Distance)), final.Count, source.Length);
        List<(Vector3 Source, Vector3 Target, double Distance)> Matches(Matrix4x4 matrix)
        {
            var pairs = new List<(Vector3, Vector3, double)>();
            for (var i = 0; i < source.Length; i++)
            {
                if ((i & 63) == 0) token.ThrowIfCancellationRequested();
                var p = Vector3.Transform(source[i], matrix); var key = Key(p); var distance = maximumDistance * maximumDistance; var nearest = Vector3.Zero; var found = false;
                for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
                    if (cells.TryGetValue((key.Item1 + x, key.Item2 + y, key.Item3 + z), out var candidates))
                        foreach (var q in candidates) { var d = Vector3.DistanceSquared(p, q); if (d > distance) continue; distance = d; nearest = q; found = true; }
                if (found) pairs.Add((p, nearest, distance));
            }
            return pairs;
        }
    }
    public static ScanTransform Transform(Matrix4x4 matrix)
    {
        var cosY = Math.Sqrt(matrix.M11 * (double)matrix.M11 + matrix.M12 * (double)matrix.M12);
        var y = Math.Atan2(-matrix.M13, cosY);
        var x = cosY > 1e-6 ? Math.Atan2(matrix.M23, matrix.M33) : Math.Atan2(-matrix.M32, matrix.M22);
        var z = cosY > 1e-6 ? Math.Atan2(matrix.M12, matrix.M11) : 0;
        var result = new ScanTransform(matrix.M41, matrix.M42, matrix.M43, (float)(x * 180 / Math.PI), (float)(y * 180 / Math.PI), (float)(z * 180 / Math.PI));
        result.Validate(); return result;
    }
}
