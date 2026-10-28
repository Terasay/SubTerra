using System.Numerics;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using SubTerra.Models;
using SubTerra.Simulation;

namespace SubTerra.Rendering;

public sealed class SceneGeometry
{
    private readonly CaveScene _scene;
    private readonly List<ScanPoint> _points = [];
    private readonly List<Color4> _pointColors = [];
    private readonly PointCloudSampler? _sampler;
    private readonly StreamingPointCloudSampler? _streamSampler;
    private PointGeometry3D? _cachedCloud;
    private (int Count, bool CutRoof, int Limit) _cloudKey;
    private readonly Color4Collection _surfaceColors;
    private readonly Color4Collection _rockColors;
    private readonly Vector3Collection _positions;
    private readonly Vector3Collection _normals;
    private readonly Vector2Collection _textureCoordinates;
    private readonly Vector3Collection _tangents;
    private readonly Vector3Collection _bitangents;
    private readonly Vector2Collection _floorCoordinates;
    private readonly Vector3Collection _floorTangents;
    private readonly Vector3Collection _floorBitangents;
    private readonly TriangleVisibility[] _triangleVisibility;
    private readonly record struct TriangleVisibility(float Minimum, float Maximum, bool IsRoof);
    private readonly float _minimum;
    private readonly float _span;
    private static readonly Vector3[] Palette =
    [
        new(.10f, .30f, .52f), new(.06f, .73f, .79f), new(.13f, .84f, .54f),
        new(.56f, .91f, .27f), new(.92f, .87f, .22f), new(.97f, .55f, .18f)
    ];

    public SceneGeometry(CaveScene scene, ScanSession? session = null)
    {
        _scene = scene;
        _minimum = scene.MinimumHeight;
        _span = Math.Max(1, scene.MaximumHeight - _minimum);
        if (session is not null)
        {
            _points.AddRange(session.Frames.SelectMany(f => f.Points));
            _pointColors.AddRange(_points.Select(p => HeightColor(p.Z, p.Intensity)));
            _sampler = new(_points);
        }
        else _streamSampler = new();
        _surfaceColors = new(scene.Vertices.Select(p => HeightColor(p.Z, 1)));
        _rockColors = new(scene.Vertices.Select(p =>
        {
            var shade = .72f + .18f * Simulation.RockNoise.Fractal(p * .35f);
            return new Color4(shade, shade * .95f, shade * .86f, 1);
        }));
        _positions = new(scene.Vertices);
        _normals = new(scene.Normals);
        // A 2.4 m tile preserves close-up detail without changing scan geometry.
        _textureCoordinates = new(scene.TextureCoordinates.Select(uv => uv * (3.2f / 2.4f)));
        HelixToolkit.Geometry.MeshBuilder.ComputeTangents(scene.Vertices, scene.Normals,
            _textureCoordinates, scene.Indices, out var tangents, out var bitangents);
        _tangents = new(tangents);
        _bitangents = new(bitangents);
        _floorCoordinates = new(scene.Vertices.Select(p => new Vector2(p.X, p.Y) / 2.4f));
        _floorTangents = new(scene.Normals.Select(n =>
        {
            var axis = Math.Abs(n.X) < .9f ? Vector3.UnitX : Vector3.UnitY;
            return Vector3.Normalize(axis - n * Vector3.Dot(n, axis));
        }));
        _floorBitangents = new(scene.Normals.Select((n, i) => Vector3.Cross(n, _floorTangents[i])));
        // Scene topology is immutable. Do the per-vertex work once, instead of
        // recomputing bounds and three sines per triangle on every scan update.
        _triangleVisibility = new TriangleVisibility[scene.Indices.Length / 3];
        var roofVertices = scene.VertexAngles.Select(angle => MathF.Sin(angle) > .30f).ToArray();
        for (var triangle = 0; triangle < _triangleVisibility.Length; triangle++)
        {
            var a = scene.Indices[triangle * 3];
            var b = scene.Indices[triangle * 3 + 1];
            var c = scene.Indices[triangle * 3 + 2];
            _triangleVisibility[triangle] = new(
                Math.Min(scene.VertexProgress[a], Math.Min(scene.VertexProgress[b], scene.VertexProgress[c])),
                Math.Max(scene.VertexProgress[a], Math.Max(scene.VertexProgress[b], scene.VertexProgress[c])),
                roofVertices[a] || roofVertices[b] || roofVertices[c]);
        }
    }

    public void AppendFrame(ScanFrame frame)
    {
        if (_sampler is not null) throw new InvalidOperationException("Нельзя дополнять заранее рассчитанный просмотр.");
        _points.AddRange(frame.Points);
        foreach (var point in frame.Points) _pointColors.Add(HeightColor(point.Z, point.Intensity));
        _streamSampler!.Append(frame.Points);
        _cachedCloud = null;
    }

    private Color4 HeightColor(float height, float intensity)
    {
        var value = Math.Clamp((height - _minimum) / _span, 0, 1) * (Palette.Length - 1);
        var i = Math.Min(Palette.Length - 2, (int)value);
        var color = Vector3.Lerp(Palette[i], Palette[i + 1], value - i) * (.77f + .23f * intensity);
        return new(color.X, color.Y, color.Z, 1);
    }

    public PointGeometry3D Cloud(int count, bool cutRoof, int pointLimit = 60_000)
    {
        count = Math.Clamp(count, 0, _points.Count);
        var key = (count, cutRoof, pointLimit);
        if (_cachedCloud is not null && _cloudKey == key) return _cachedCloud;
        ReadOnlySpan<int> indices = _sampler is null ? _streamSampler!.Select(count, cutRoof, pointLimit) :
            _sampler.Select(count, cutRoof, pointLimit).Span;
        var positions = new Vector3Collection(indices.Length);
        var colors = new Color4Collection(indices.Length);
        foreach (var i in indices)
        {
            positions.Add(_points[i].Position);
            colors.Add(_pointColors[i]);
        }
        _cloudKey = key;
        return _cachedCloud = new() { Positions = positions, Colors = colors };
    }

    public MeshGeometry3D Surface(float progress, bool cutRoof, bool heightColors = false, float startProgress = 0)
        => Surface([new(startProgress, progress)], cutRoof, heightColors);

    public MeshGeometry3D Surface(IReadOnlyList<ScanCoverage> coverage, bool cutRoof, bool heightColors = false)
    {
        var indices = new IntCollection();
        foreach (var triangle in VisibleTriangles(coverage, cutRoof))
        {
            var i = triangle * 3;
            indices.Add(_scene.Indices[i]); indices.Add(_scene.Indices[i + 1]); indices.Add(_scene.Indices[i + 2]);
        }
        return CreateSurface(indices, heightColors);
    }

    public (MeshGeometry3D Rock, MeshGeometry3D Floor, MeshGeometry3D Damp) SurfaceLayers(
        IReadOnlyList<ScanCoverage> coverage, bool cutRoof)
    {
        var rock = new IntCollection(); var floor = new IntCollection(); var damp = new IntCollection();
        foreach (var triangle in VisibleTriangles(coverage, cutRoof))
        {
            var kind = _scene.SurfaceKinds.Length == 0 ? CaveSurfaceKind.Rock : _scene.SurfaceKinds[triangle];
            var target = kind == CaveSurfaceKind.Sediment ? floor : kind == CaveSurfaceKind.DampRock ? damp : rock;
            var i = triangle * 3;
            target.Add(_scene.Indices[i]); target.Add(_scene.Indices[i + 1]); target.Add(_scene.Indices[i + 2]);
        }
        return (CreateSurface(rock, false), CreateSurface(floor, false, true), CreateSurface(damp, false));
    }

    private IEnumerable<int> VisibleTriangles(IReadOnlyList<ScanCoverage> coverage, bool cutRoof)
    {
        for (var triangle = 0; triangle < _triangleVisibility.Length; triangle++)
        {
            var visibility = _triangleVisibility[triangle];
            if (cutRoof && visibility.IsRoof)
                continue;
            if (!Contains(coverage, visibility.Minimum, visibility.Maximum))
                continue;
            yield return triangle;
        }
    }

    private MeshGeometry3D CreateSurface(IEnumerable<int> indices, bool heightColors, bool floor = false)
        => new()
        {
            Positions = _positions,
            Normals = _normals,
            TextureCoordinates = floor ? _floorCoordinates : _textureCoordinates,
            Tangents = floor ? _floorTangents : _tangents,
            BiTangents = floor ? _floorBitangents : _bitangents,
            Colors = heightColors ? _surfaceColors : _rockColors,
            Indices = indices as IntCollection ?? new IntCollection(indices)
        };

    public LineGeometry3D Route(float progress, float startProgress = 0)
        => Route([new(startProgress, progress)]);

    public LineGeometry3D Route(IReadOnlyList<ScanCoverage> coverage)
    {
        var points = new List<Vector3>();
        for (var i = 1; i < _scene.Route.Length; i++)
        {
            if (!Contains(coverage, (i - 1) / (float)(_scene.Route.Length - 1), i / (float)(_scene.Route.Length - 1))) continue;
            points.Add(_scene.Route[i - 1]);
            points.Add(_scene.Route[i]);
        }
        return Lines(points);
    }

    public static LineGeometry3D Grid(float height)
    {
        var points = new List<Vector3>();
        for (var n = -40; n <= 40; n += 5)
        {
            points.AddRange([new(n, -40, height), new(n, 40, height),
                new(-40, n, height), new(40, n, height)]);
        }
        return Lines(points);
    }

    public static LineGeometry3D Lines(IEnumerable<Vector3> points)
    {
        var positions = new Vector3Collection(points);
        return new() { Positions = positions, Indices = new IntCollection(Enumerable.Range(0, positions.Count)) };
    }

    public static PointGeometry3D Points(IEnumerable<Vector3> points) =>
        new() { Positions = new Vector3Collection(points) };

    private static bool Contains(IReadOnlyList<ScanCoverage> coverage, float minimum, float maximum)
    {
        for (var i = 0; i < coverage.Count; i++)
            if (minimum >= coverage[i].Start && maximum <= coverage[i].End) return true;
        return false;
    }
}
