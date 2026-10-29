using System.Numerics;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using SubTerra.Models;
using SubTerra.Services;

namespace SubTerra.Rendering;

/// <summary>Bounded display buffers, independent of the complete imported measurements.</summary>
public sealed class ImportedGeometry(ImportedScan scan)
{
    private readonly (Vector3 Minimum, Vector3 Maximum) _bounds = scan.Bounds;
    public (Vector3 Minimum, Vector3 Maximum) Bounds => _bounds;
    private readonly Dictionary<(int Limit, PointClipPlane? Clip, bool Height), PointGeometry3D> _clouds = [];
    private readonly Dictionary<(bool Height, bool Coarse), MeshGeometry3D> _meshes = [];
    private ImportedScan? _meshPreview;
    private ImportedScan? _coarsePreview;
    public int MeshTriangleCount(bool coarse) => (coarse ? _coarsePreview ?? scan : _meshPreview ?? scan).Triangles.Length / 3;
    public void PrepareMeshPreviews(CancellationToken token = default)
    {
        if (!scan.HasMesh) return;
        _meshPreview = MeshPreviewBuilder.Build(scan, 1_000_000, token);
        _coarsePreview = MeshPreviewBuilder.Build(_meshPreview, 250_000, token);
        foreach (var coarse in new[] { false, true }) foreach (var height in new[] { false, true })
        { token.ThrowIfCancellationRequested(); _ = Mesh(height, coarse); }
    }
    private static readonly Vector3[] Palette = [new(.10f, .30f, .52f), new(.06f, .73f, .79f), new(.13f, .84f, .54f),
        new(.56f, .91f, .27f), new(.92f, .87f, .22f), new(.97f, .55f, .18f)];
    public PointGeometry3D Cloud(int limit, PointClipPlane? clip, bool height = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        limit = Math.Min(limit, scan.Points.Length);
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
    public MeshGeometry3D Mesh(bool height, bool coarse = false)
    {
        if (_meshes.TryGetValue((height, coarse), out var cached)) return cached;
        if (coarse && ReferenceEquals(_coarsePreview ?? scan, _meshPreview ?? scan))
            return _meshes[(height, true)] = Mesh(height);
        var data = coarse ? _coarsePreview ?? scan : _meshPreview ?? scan;
        var count = data.Triangles.Length / 3;
        if (_meshes.TryGetValue((!height, coarse), out var shared))
        {
            var vertexColors = new Color4Collection(count * 3);
            foreach (var index in data.Triangles) vertexColors.Add(Color(data, index, height));
            return _meshes[(height, coarse)] = new()
                { Positions = shared.Positions, Normals = shared.Normals, Indices = shared.Indices, Colors = vertexColors };
        }
        var positions = new Vector3Collection(count * 3); var normals = new Vector3Collection(count * 3);
        var colors = new Color4Collection(count * 3); var indices = new IntCollection(count * 3);
        for (var i = 0; i < count; i++)
        {
            var triangle = i;
            var a = data.Points[data.Triangles[triangle * 3]].Position;
            var b = data.Points[data.Triangles[triangle * 3 + 1]].Position;
            var c = data.Points[data.Triangles[triangle * 3 + 2]].Position;
            var cross = Vector3.Cross(b - a, c - a);
            var normal = cross.LengthSquared() > 1e-16f ? Vector3.Normalize(cross) : Vector3.UnitZ;
            for (var j = 0; j < 3; j++)
            {
                var index = data.Triangles[triangle * 3 + j];
                positions.Add(data.Points[index].Position); normals.Add(normal); colors.Add(Color(data, index, height)); indices.Add(indices.Count);
            }
        }
        return _meshes[(height, coarse)] = new() { Positions = positions, Normals = normals, Colors = colors, Indices = indices };
    }
    private Color4 Color(int index, bool height) => Color(scan, index, height);
    private Color4 Color(ImportedScan data, int index, bool height)
    {
        if (!height && data.Colors.Length > 0)
        {
            var color = data.Colors[index];
            return new((color >> 16 & 255) / 255f, (color >> 8 & 255) / 255f, (color & 255) / 255f, 1);
        }
        var t = Math.Clamp((data.Points[index].Z - _bounds.Minimum.Z) / Math.Max(.0001f, _bounds.Maximum.Z - _bounds.Minimum.Z), 0, 1);
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

public enum IndoorSurfaceStyle { SourceColors, Materials, Classes }
public sealed record IndoorSurfacePreview(ImportedScan Scan, uint[] MaterialColors, uint[] ClassColors, int[] FloorTriangles);

/// <summary>Observed planar surface preview. Empty grid cells are never filled with a convex hull.</summary>
public static class IndoorSurfaceBuilder
{
    private sealed class Cell
    {
        public int Count; public long Red, Green, Blue;
    }
    private sealed record Patch(StructureSurface Surface, Vector3 U, Vector3 V, Dictionary<(int X, int Y), Cell> Cells);
    public static IndoorSurfacePreview Build(ScanProject project, StructureAnalysisDocument document, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); project.Validate(token);
        if (StructureProvenance.Refresh(document, project, token).IsStale)
            throw new System.IO.InvalidDataException("Поверхности устарели. Повторите анализ.");
        var surfaces = document.Result.Surfaces.Where(s => s.Kind != StructureSurfaceKind.Unknown && s.Review != StructureReviewState.Rejected).ToArray();
        if (surfaces.Length == 0) throw new System.IO.InvalidDataException("Нет пола, стен или потолка для предварительных поверхностей.");
        var area = surfaces.Sum(s => s.AreaSquareMeters);
        var visibleCount = project.Objects.Where(o => o.IsVisible).Sum(o => o.PointCount);
        var cellSize = Math.Max(.06, Math.Max(Math.Sqrt(area / 80_000), Math.Min(.25, 1.7 * Math.Sqrt(area / Math.Max(1, visibleCount)))));
        var patches = surfaces.Select(s =>
        {
            var u = Vector3.Normalize(Vector3.Cross(s.Normal, Math.Abs(s.Normal.Z) < .9f ? Vector3.UnitZ : Vector3.UnitX));
            return new Patch(s, u, Vector3.Cross(s.Normal, u), []);
        }).ToArray();
        var tolerance = document.Result.Options.DistanceTolerance * 2;
        var cellCount = 0;
        foreach (var obj in project.Objects.Where(o => o.IsVisible))
        {
            var matrix = obj.Matrix;
            for (var i = 0; i < obj.Source.Points.Length; i++)
            {
                if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
                if (!obj.Includes(i)) continue;
                var p = Vector3.Transform(obj.Source.Points[i].Position, matrix);
                Patch? nearest = null; var distance = tolerance;
                foreach (var patch in patches)
                {
                    var d = Math.Abs(PlanePatch.Distance(patch.Surface.Normal, patch.Surface.Offset, p));
                    if (d < distance) { nearest = patch; distance = d; }
                }
                if (nearest is null) continue;
                var relative = p - nearest.Surface.Center;
                var key = ((int)Math.Floor(Vector3.Dot(relative, nearest.U) / cellSize), (int)Math.Floor(Vector3.Dot(relative, nearest.V) / cellSize));
                if (!nearest.Cells.TryGetValue(key, out var cell))
                {
                    if (++cellCount > 250_000) throw new System.IO.InvalidDataException("Слишком много участков для предпросмотра. Выберите один этаж или помещение.");
                    nearest.Cells[key] = cell = new();
                }
                var rgb = obj.Source.Colors.Length == 0 ? 0xB5BCC3u : obj.Source.Colors[i];
                cell.Count++; cell.Red += rgb >> 16 & 255; cell.Green += rgb >> 8 & 255; cell.Blue += rgb & 255;
            }
        }
        var points = new List<ScanPoint>(); var colors = new List<uint>(); var materials = new List<uint>(); var classes = new List<uint>();
        var triangles = new List<int>(); var floorTriangles = new List<int>();
        foreach (var patch in patches)
        {
            token.ThrowIfCancellationRequested();
            var indices = new Dictionary<(int, int), int>();
            foreach (var (key, cell) in patch.Cells.OrderBy(c => c.Key.X).ThenBy(c => c.Key.Y))
            {
                var p = patch.Surface.Center + patch.U * (float)((key.X + .5) * cellSize) + patch.V * (float)((key.Y + .5) * cellSize);
                indices[key] = points.Count; points.Add(new(p.X, p.Y, p.Z, 1));
                colors.Add((uint)(cell.Red / cell.Count << 16 | cell.Green / cell.Count << 8 | cell.Blue / cell.Count));
                materials.Add(Material(patch.Surface.Kind, p));
                classes.Add(patch.Surface.Kind switch { StructureSurfaceKind.Floor => 0x61E08Cu, StructureSurfaceKind.Wall => 0xF5B84Du, _ => 0x66A6FAu });
            }
            foreach (var key in indices.Keys)
            {
                var a = indices[key];
                var right = indices.TryGetValue((key.Item1 + 1, key.Item2), out var b);
                var above = indices.TryGetValue((key.Item1, key.Item2 + 1), out var c);
                var diagonal = indices.TryGetValue((key.Item1 + 1, key.Item2 + 1), out var d);
                if (right && above) Add(a, b, c);
                if (right && above && diagonal) Add(b, d, c);
            }
            void Add(int a, int b, int c)
            {
                if (triangles.Count / 3 >= 300_000) throw new System.IO.InvalidDataException("Превышен лимит предварительной сетки. Выберите меньшее помещение.");
                triangles.AddRange([a, b, c]);
                if (patch.Surface.Kind == StructureSurfaceKind.Floor) floorTriangles.AddRange([a, b, c]);
            }
        }
        token.ThrowIfCancellationRequested();
        if (triangles.Count == 0) throw new System.IO.InvalidDataException("Недостаточно соседних точек для поверхностей. Попробуйте более плотный скан.");
        return new(new ImportedScan
        {
            Metadata = new("indoor-surface-preview.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0) { IsSynthetic = true },
            Points = points.ToArray(), Colors = colors.ToArray(), Triangles = triangles.ToArray()
        }, materials.ToArray(), classes.ToArray(), floorTriangles.ToArray());
    }
    private static uint Material(StructureSurfaceKind kind, Vector3 p)
    {
        if (kind == StructureSurfaceKind.Ceiling) return 0xEBE9E0;
        if (kind == StructureSurfaceKind.Wall) return 0xD9D5C9;
        var x = p.X / .6; var y = p.Y / .6;
        if (Math.Min(x - Math.Floor(x), 1 - (x - Math.Floor(x))) < .035 ||
            Math.Min(y - Math.Floor(y), 1 - (y - Math.Floor(y))) < .035) return 0x9C9D97;
        return (((long)Math.Floor(x) + (long)Math.Floor(y)) & 1) == 0 ? 0xC4C4B8u : 0xB8B9AEu;
    }
}
