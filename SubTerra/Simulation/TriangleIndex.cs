using System.Numerics;

namespace SubTerra.Simulation;

/// <summary>A small BVH keeps ray queries independent of the UI and the rendering library.</summary>
public sealed class TriangleIndex
{
    private readonly Vector3[] _vertices;
    private readonly int[] _indices;
    private readonly int[] _triangles;
    private readonly Node _root;
    private sealed record Node(Vector3 Min, Vector3 Max, int Start, int Count, Node? Left, Node? Right);

    public TriangleIndex(Vector3[] vertices, int[] indices, CancellationToken token = default)
    {
        _vertices = vertices;
        _indices = indices;
        _triangles = Enumerable.Range(0, indices.Length / 3).ToArray();
        _root = Build(0, _triangles.Length, token);
    }

    private Node Build(int start, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (var j = start; j < start + count; j++)
        {
            if ((j & 4095) == 0) token.ThrowIfCancellationRequested();
            for (var k = 0; k < 3; k++)
            {
                var p = _vertices[_indices[_triangles[j] * 3 + k]];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        if (count <= 10) return new(min, max, start, count, null, null);
        var extent = max - min;
        var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        Array.Sort(_triangles, start, count, Comparer<int>.Create((a, b) =>
            Centroid(a, axis).CompareTo(Centroid(b, axis))));
        var half = count / 2;
        return new(min, max, start, count, Build(start, half, token), Build(start + half, count - half, token));
    }

    private float Centroid(int triangle, int axis)
    {
        var p = _vertices[_indices[triangle * 3]] + _vertices[_indices[triangle * 3 + 1]] +
                _vertices[_indices[triangle * 3 + 2]];
        return axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
    }

    public bool Cast(Vector3 origin, Vector3 direction, float range, out Vector3 point)
        => Cast(origin, direction, range, out point, out _);

    public bool Cast(Vector3 origin, Vector3 direction, float range, out Vector3 point, out Vector3 normal)
    {
        normal = Vector3.Zero;
        var nearest = range;
        var hit = Visit(_root, origin, direction, ref nearest, ref normal);
        point = origin + direction * nearest;
        return hit;
    }

    private bool Visit(Node node, Vector3 origin, Vector3 direction, ref float nearest, ref Vector3 normal)
    {
        if (!Box(node, origin, direction, nearest)) return false;
        if (node.Left is not null)
        {
            var a = Visit(node.Left, origin, direction, ref nearest, ref normal);
            var b = Visit(node.Right!, origin, direction, ref nearest, ref normal);
            return a || b;
        }
        var hit = false;
        for (var j = node.Start; j < node.Start + node.Count; j++)
        {
            var i = _triangles[j] * 3;
            var a = _vertices[_indices[i]];
            var e1 = _vertices[_indices[i + 1]] - a;
            var e2 = _vertices[_indices[i + 2]] - a;
            var p = Vector3.Cross(direction, e2);
            var determinant = Vector3.Dot(e1, p);
            if (MathF.Abs(determinant) < 1e-7f) continue;
            var inverse = 1 / determinant;
            var s = origin - a;
            var u = inverse * Vector3.Dot(s, p);
            if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(s, e1);
            var v = inverse * Vector3.Dot(direction, q);
            if (v < 0 || u + v > 1) continue;
            var t = inverse * Vector3.Dot(e2, q);
            if (t <= .025f || t >= nearest) continue;
            nearest = t;
            normal = Vector3.Normalize(Vector3.Cross(e1, e2));
            hit = true;
        }
        return hit;
    }

    /// <summary>Exact distance from a segment to triangles, accelerated by the same BVH as ray casts.</summary>
    public bool IntersectsCapsule(Vector3 lower, Vector3 upper, float radius)
    {
        if (!float.IsFinite(lower.X + lower.Y + lower.Z + upper.X + upper.Y + upper.Z) || !float.IsFinite(radius) || radius <= 0)
            throw new ArgumentException("Некорректный объём камеры.");
        return CapsuleVisit(_root, lower, upper, radius, radius * radius);
    }
    private bool CapsuleVisit(Node node, Vector3 lower, Vector3 upper, float radius, float squared)
    {
        var min = Vector3.Min(lower, upper) - new Vector3(radius); var max = Vector3.Max(lower, upper) + new Vector3(radius);
        if (max.X < node.Min.X || min.X > node.Max.X || max.Y < node.Min.Y || min.Y > node.Max.Y || max.Z < node.Min.Z || min.Z > node.Max.Z) return false;
        if (node.Left is not null) return CapsuleVisit(node.Left, lower, upper, radius, squared) || CapsuleVisit(node.Right!, lower, upper, radius, squared);
        for (var j = node.Start; j < node.Start + node.Count; j++)
        {
            var i = _triangles[j] * 3; var a = _vertices[_indices[i]]; var b = _vertices[_indices[i + 1]]; var c = _vertices[_indices[i + 2]];
            if (SegmentTriangleDistanceSquared(lower, upper, a, b, c) < squared) return true;
        }
        return false;
    }
    private static float SegmentTriangleDistanceSquared(Vector3 p, Vector3 q, Vector3 a, Vector3 b, Vector3 c)
    {
        var direction = q - p; var e1 = b - a; var e2 = c - a;
        var cross = Vector3.Cross(direction, e2); var determinant = Vector3.Dot(e1, cross);
        if (Math.Abs(determinant) > 1e-10f)
        {
            var inverse = 1 / determinant; var delta = p - a; var u = Vector3.Dot(delta, cross) * inverse;
            var other = Vector3.Cross(delta, e1); var v = Vector3.Dot(direction, other) * inverse; var t = Vector3.Dot(e2, other) * inverse;
            if (u >= 0 && v >= 0 && u + v <= 1 && t >= 0 && t <= 1) return 0;
        }
        return Math.Min(Math.Min(PointTriangleDistanceSquared(p, a, b, c), PointTriangleDistanceSquared(q, a, b, c)),
            Math.Min(SegmentDistanceSquared(p, q, a, b), Math.Min(SegmentDistanceSquared(p, q, b, c), SegmentDistanceSquared(p, q, c, a))));
    }
    private static float PointTriangleDistanceSquared(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a; var d1 = Vector3.Dot(ab, ap); var d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return ap.LengthSquared();
        var bp = p - b; var d3 = Vector3.Dot(ab, bp); var d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return bp.LengthSquared();
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return Vector3.DistanceSquared(p, a + ab * (d1 / (d1 - d3)));
        var cp = p - c; var d5 = Vector3.Dot(ab, cp); var d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return cp.LengthSquared();
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return Vector3.DistanceSquared(p, a + ac * (d2 / (d2 - d6)));
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return Vector3.DistanceSquared(p, b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6)));
        var denominator = va + vb + vc;
        if (Math.Abs(denominator) < 1e-20f) return Math.Min(Vector3.DistanceSquared(p, a), Math.Min(Vector3.DistanceSquared(p, b), Vector3.DistanceSquared(p, c)));
        return Vector3.DistanceSquared(p, a + ab * (vb / denominator) + ac * (vc / denominator));
    }
    private static float SegmentDistanceSquared(Vector3 p, Vector3 q, Vector3 a, Vector3 b)
    {
        var d = q - p; var e = b - a; var r = p - a;
        var dd = Vector3.Dot(d, d); var ee = Vector3.Dot(e, e); var er = Vector3.Dot(e, r);
        float s, t;
        if (dd < 1e-12f && ee < 1e-12f) return r.LengthSquared();
        if (dd < 1e-12f) { s = 0; t = Math.Clamp(er / ee, 0, 1); }
        else
        {
            var dr = Vector3.Dot(d, r);
            if (ee < 1e-12f) { t = 0; s = Math.Clamp(-dr / dd, 0, 1); }
            else
            {
                var de = Vector3.Dot(d, e); var denominator = dd * ee - de * de;
                s = denominator > 1e-12f ? Math.Clamp((de * er - dr * ee) / denominator, 0, 1) : 0;
                t = (de * s + er) / ee;
                if (t < 0) { t = 0; s = Math.Clamp(-dr / dd, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((de - dr) / dd, 0, 1); }
            }
        }
        return Vector3.DistanceSquared(p + d * s, a + e * t);
    }

    private static bool Box(Node box, Vector3 origin, Vector3 direction, float range)
    {
        var near = 0f;
        var far = range;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var lo = axis == 0 ? box.Min.X : axis == 1 ? box.Min.Y : box.Min.Z;
            var hi = axis == 0 ? box.Max.X : axis == 1 ? box.Max.Y : box.Max.Z;
            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < lo || o > hi) return false;
                continue;
            }
            var a = (lo - o) / d;
            var b = (hi - o) / d;
            near = Math.Max(near, Math.Min(a, b));
            far = Math.Min(far, Math.Max(a, b));
            if (far < near) return false;
        }
        return true;
    }
}
