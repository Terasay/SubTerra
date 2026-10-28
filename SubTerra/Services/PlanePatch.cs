using System.Numerics;

namespace SubTerra.Services;

/// <summary>Least-squares plane and convex support boundary. No rendering dependencies.</summary>
internal static class PlanePatch
{
    internal sealed record Fit(Vector3 Normal, double Offset, Vector3 Center);
    public static double Distance(Vector3 normal, double offset, Vector3 point) =>
        normal.X * (double)point.X + normal.Y * (double)point.Y + normal.Z * (double)point.Z + offset;

    public static Fit? Refine(Vector3[] points, CancellationToken token)
    {
        double x = 0, y = 0, z = 0;
        foreach (var point in points) { x += point.X; y += point.Y; z += point.Z; }
        x /= points.Length; y /= points.Length; z /= points.Length;
        var a = new double[3, 3];
        for (var i = 0; i < points.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var dx = points[i].X - x; var dy = points[i].Y - y; var dz = points[i].Z - z;
            a[0, 0] += dx * dx; a[0, 1] += dx * dy; a[0, 2] += dx * dz;
            a[1, 1] += dy * dy; a[1, 2] += dy * dz; a[2, 2] += dz * dz;
        }
        a[1, 0] = a[0, 1]; a[2, 0] = a[0, 2]; a[2, 1] = a[1, 2];
        var v = new double[3, 3]; v[0, 0] = v[1, 1] = v[2, 2] = 1;
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var p = 0; var q = 1;
            if (Math.Abs(a[0, 2]) > Math.Abs(a[p, q])) { p = 0; q = 2; }
            if (Math.Abs(a[1, 2]) > Math.Abs(a[p, q])) { p = 1; q = 2; }
            if (Math.Abs(a[p, q]) < 1e-12) break;
            var angle = .5 * Math.Atan2(2 * a[p, q], a[q, q] - a[p, p]);
            var c = Math.Cos(angle); var s = Math.Sin(angle);
            var app = a[p, p]; var aqq = a[q, q]; var apq = a[p, q];
            a[p, p] = c * c * app - 2 * s * c * apq + s * s * aqq;
            a[q, q] = s * s * app + 2 * s * c * apq + c * c * aqq;
            a[p, q] = a[q, p] = 0;
            for (var k = 0; k < 3; k++)
            {
                if (k != p && k != q)
                {
                    var akp = a[k, p]; var akq = a[k, q];
                    a[k, p] = a[p, k] = c * akp - s * akq;
                    a[k, q] = a[q, k] = s * akp + c * akq;
                }
                var vkp = v[k, p]; var vkq = v[k, q];
                v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
            }
        }
        var order = Enumerable.Range(0, 3).OrderBy(i => a[i, i]).ToArray();
        if (a[order[1], order[1]] < 1e-9) return null;
        var m = order[0];
        var normal = Vector3.Normalize(new((float)v[0, m], (float)v[1, m], (float)v[2, m]));
        if (normal.Z < -.00001f || (Math.Abs(normal.Z) <= .00001f && (normal.X < -.00001f || (Math.Abs(normal.X) <= .00001f && normal.Y < 0))))
            normal = -normal;
        return new(normal, -(normal.X * x + normal.Y * y + normal.Z * z), new((float)x, (float)y, (float)z));
    }

    public static (Vector3[] Boundary, double Area) Boundary(Fit plane, Vector3[] points)
    {
        var axis = Math.Abs(plane.Normal.Z) < .9f ? Vector3.UnitZ : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(plane.Normal, axis));
        var v = Vector3.Cross(plane.Normal, u);
        var sorted = points.Select(p =>
        {
            var d = p - plane.Center;
            return new Vector2(Vector3.Dot(d, u), Vector3.Dot(d, v));
        }).Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (sorted.Length < 3) return ([], 0);
        var hull = new List<Vector2>();
        foreach (var p in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        var lower = hull.Count + 1;
        for (var i = sorted.Length - 2; i >= 0; i--)
        {
            var p = sorted[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        double area = 0;
        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i]; var b = hull[(i + 1) % hull.Count];
            area += a.X * (double)b.Y - a.Y * (double)b.X;
        }
        return (hull.Select(p => plane.Center + u * p.X + v * p.Y).ToArray(), Math.Abs(area) / 2);
    }
    private static double Cross(Vector2 a, Vector2 b, Vector2 c) =>
        (b.X - (double)a.X) * (c.Y - (double)a.Y) - (b.Y - (double)a.Y) * (c.X - (double)a.X);
}
