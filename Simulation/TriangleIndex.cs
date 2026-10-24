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

    public TriangleIndex(Vector3[] vertices, int[] indices)
    {
        _vertices = vertices;
        _indices = indices;
        _triangles = Enumerable.Range(0, indices.Length / 3).ToArray();
        _root = Build(0, _triangles.Length);
    }

    private Node Build(int start, int count)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (var j = start; j < start + count; j++)
            for (var k = 0; k < 3; k++)
            {
                var p = _vertices[_indices[_triangles[j] * 3 + k]];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        if (count <= 10) return new(min, max, start, count, null, null);
        var extent = max - min;
        var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        Array.Sort(_triangles, start, count, Comparer<int>.Create((a, b) =>
            Centroid(a, axis).CompareTo(Centroid(b, axis))));
        var half = count / 2;
        return new(min, max, start, count, Build(start, half), Build(start + half, count - half));
    }

    private float Centroid(int triangle, int axis)
    {
        var p = _vertices[_indices[triangle * 3]] + _vertices[_indices[triangle * 3 + 1]] +
                _vertices[_indices[triangle * 3 + 2]];
        return axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
    }

    public bool Cast(Vector3 origin, Vector3 direction, float range, out Vector3 point)
    {
        var nearest = range;
        var hit = Visit(_root, origin, direction, ref nearest);
        point = origin + direction * nearest;
        return hit;
    }

    private bool Visit(Node node, Vector3 origin, Vector3 direction, ref float nearest)
    {
        if (!Box(node, origin, direction, nearest)) return false;
        if (node.Left is not null)
        {
            var a = Visit(node.Left, origin, direction, ref nearest);
            var b = Visit(node.Right!, origin, direction, ref nearest);
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
            hit = true;
        }
        return hit;
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
