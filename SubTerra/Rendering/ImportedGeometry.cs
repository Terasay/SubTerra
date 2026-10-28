using System.Numerics;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using SubTerra.Models;

namespace SubTerra.Rendering;

/// <summary>Bounded display buffers, independent of the complete imported measurements.</summary>
public sealed class ImportedGeometry(ImportedScan scan)
{
    private readonly (Vector3 Minimum, Vector3 Maximum) _bounds = scan.Bounds;
    public (Vector3 Minimum, Vector3 Maximum) Bounds => _bounds;
    private readonly Dictionary<(int Limit, PointClipPlane? Clip, bool Height), PointGeometry3D> _clouds = [];
    private readonly Dictionary<bool, MeshGeometry3D> _meshes = [];
    private static readonly Vector3[] Palette = [new(.10f, .30f, .52f), new(.06f, .73f, .79f), new(.13f, .84f, .54f),
        new(.56f, .91f, .27f), new(.92f, .87f, .22f), new(.97f, .55f, .18f)];
    public PointGeometry3D Cloud(int limit, PointClipPlane? clip, bool height = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var key = (limit, clip, height);
        if (_clouds.TryGetValue(key, out var cached)) return cached;
        // Count and sample in two passes; clipping never creates an array of millions of indices.
        var eligible = clip is null ? scan.Points.Length : scan.Points.Count(p => clip.Value.Contains(p.Position));
        var size = Math.Min(limit, eligible);
        var positions = new Vector3Collection(size); var colors = new Color4Collection(size);
        if (clip is null)
        {
            for (var i = 0; i < size; i++)
            {
                var index = size < 2 ? 0 : (int)((long)i * (eligible - 1) / (size - 1));
                positions.Add(scan.Points[index].Position); colors.Add(Color(index, height));
            }
        }
        else
        {
            var rank = 0; var next = 0;
            for (var i = 0; i < scan.Points.Length && next < size; i++)
            {
                if (!clip.Value.Contains(scan.Points[i].Position)) continue;
                var target = size < 2 ? 0 : (int)((long)next * (eligible - 1) / (size - 1));
                if (rank++ != target) continue;
                positions.Add(scan.Points[i].Position); colors.Add(Color(i, height)); ++next;
            }
        }
        if (_clouds.Count >= 2) _clouds.Clear();
        return _clouds[key] = new() { Positions = positions, Colors = colors };
    }
    public MeshGeometry3D Mesh(bool height)
    {
        if (_meshes.TryGetValue(height, out var cached)) return cached;
        var count = Math.Min(250_000, scan.Triangles.Length / 3);
        var positions = new Vector3Collection(count * 3); var normals = new Vector3Collection(count * 3);
        var colors = new Color4Collection(count * 3); var indices = new IntCollection(count * 3);
        for (var i = 0; i < count; i++)
        {
            var triangle = count < 2 ? 0 : (int)((long)i * (scan.Triangles.Length / 3 - 1) / (count - 1));
            var a = scan.Points[scan.Triangles[triangle * 3]].Position;
            var b = scan.Points[scan.Triangles[triangle * 3 + 1]].Position;
            var c = scan.Points[scan.Triangles[triangle * 3 + 2]].Position;
            var cross = Vector3.Cross(b - a, c - a);
            var normal = cross.LengthSquared() > 1e-16f ? Vector3.Normalize(cross) : Vector3.UnitZ;
            for (var j = 0; j < 3; j++)
            {
                var index = scan.Triangles[triangle * 3 + j];
                positions.Add(scan.Points[index].Position); normals.Add(normal); colors.Add(Color(index, height)); indices.Add(indices.Count);
            }
        }
        return _meshes[height] = new() { Positions = positions, Normals = normals, Colors = colors, Indices = indices };
    }
    private Color4 Color(int index, bool height)
    {
        if (!height && scan.Colors.Length > 0)
        {
            var color = scan.Colors[index];
            return new((color >> 16 & 255) / 255f, (color >> 8 & 255) / 255f, (color & 255) / 255f, 1);
        }
        var t = Math.Clamp((scan.Points[index].Z - _bounds.Minimum.Z) / Math.Max(.0001f, _bounds.Maximum.Z - _bounds.Minimum.Z), 0, 1);
        var position = t * (Palette.Length - 1);
        var i = Math.Min(Palette.Length - 2, (int)position);
        var rgb = Vector3.Lerp(Palette[i], Palette[i + 1], position - i);
        return new(rgb.X, rgb.Y, rgb.Z, 1);
    }
    public LineGeometry3D Grid()
    {
        var size = Math.Max(.01f, (_bounds.Maximum - _bounds.Minimum).Length());
        var center = (_bounds.Minimum + _bounds.Maximum) * .5f;
        var z = _bounds.Minimum.Z - size * .01f;
        var points = new List<Vector3>();
        for (var i = -5; i <= 5; i++)
        {
            var offset = i * size / 10;
            points.Add(new(center.X + offset, center.Y - size / 2, z)); points.Add(new(center.X + offset, center.Y + size / 2, z));
            points.Add(new(center.X - size / 2, center.Y + offset, z)); points.Add(new(center.X + size / 2, center.Y + offset, z));
        }
        return SceneGeometry.Lines(points);
    }
}
