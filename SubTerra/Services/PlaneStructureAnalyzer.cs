using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>Conservative, deterministic plane proposals for one Z-up indoor space.</summary>
public sealed class PlaneStructureAnalyzer : IStructureAnalyzer
{
    public Task<StructureAnalysisResult> AnalyzeAsync(ScanProject project, StructureAnalysisOptions options,
        IProgress<double>? progress, CancellationToken cancellationToken) => Task.Run(() =>
            Analyze(project, options, progress, cancellationToken), cancellationToken);

    private static StructureAnalysisResult Analyze(ScanProject project, StructureAnalysisOptions options,
        IProgress<double>? progress, CancellationToken token)
    {
        options.Validate(); project.Validate(token); token.ThrowIfCancellationRequested();
        var visible = project.Objects.Where(o => o.IsVisible && o.PointCount > 0).ToArray();
        var total = visible.Sum(o => o.PointCount);
        var sampleCount = Math.Min(total, options.MaximumSamples);
        var samples = new List<StructureSample>(sampleCount);
        var ordinal = 0;
        foreach (var obj in visible)
        {
            var matrix = obj.Matrix;
            for (var i = 0; i < obj.Source.Points.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!obj.Includes(i)) continue;
                if (samples.Count < sampleCount && ordinal == (long)samples.Count * total / sampleCount)
                {
                    var p = Vector3.Transform(obj.Source.Points[i].Position, matrix);
                    if (!InspectionPoint.From(p).IsValid) throw new System.IO.InvalidDataException("Точки анализа выходят за допустимые координаты проекта.");
                    samples.Add(new(new(obj.Id, i), p));
                }
                ++ordinal;
            }
        }
        progress?.Report(5);
        var remaining = Enumerable.Range(0, samples.Count).ToList();
        var surfaces = new List<StructureSurface>();
        var random = new Random(42);
        for (var attempt = 0; attempt < options.MaximumPlanes * 2 && surfaces.Count < options.MaximumPlanes; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (remaining.Count < options.MinimumSupport) break;
            var bestCount = 0; var bestNormal = Vector3.Zero; double bestOffset = 0;
            for (var iteration = 0; iteration < options.Iterations; iteration++)
            {
                token.ThrowIfCancellationRequested();
                var a = samples[remaining[random.Next(remaining.Count)]].Position;
                var b = samples[remaining[random.Next(remaining.Count)]].Position;
                var c = samples[remaining[random.Next(remaining.Count)]].Position;
                var normal = Vector3.Cross(b - a, c - a);
                if (normal.LengthSquared() < 1e-10f) continue;
                normal = Vector3.Normalize(normal);
                var offset = -(normal.X * (double)a.X + normal.Y * (double)a.Y + normal.Z * (double)a.Z);
                var count = 0;
                foreach (var i in remaining)
                    if (Math.Abs(PlanePatch.Distance(normal, offset, samples[i].Position)) <= options.DistanceTolerance) ++count;
                if (count <= bestCount) continue;
                bestCount = count; bestNormal = normal; bestOffset = offset;
                if (count >= remaining.Count * .995) break;
            }
            if (bestCount < options.MinimumSupport) break;
            var inliers = remaining.Where(i => Math.Abs(PlanePatch.Distance(bestNormal, bestOffset, samples[i].Position)) <= options.DistanceTolerance).ToArray();
            var fit = PlanePatch.Refine(inliers.Select(i => samples[i].Position).ToArray(), token);
            if (fit is null) break;
            inliers = remaining.Where(i => Math.Abs(PlanePatch.Distance(fit.Normal, fit.Offset, samples[i].Position)) <= options.DistanceTolerance).ToArray();
            if (inliers.Length < options.MinimumSupport) break;
            fit = PlanePatch.Refine(inliers.Select(i => samples[i].Position).ToArray(), token) ?? fit;
            var assigned = inliers.ToHashSet(); remaining.RemoveAll(assigned.Contains);
            var points = inliers.Select(i => samples[i].Position).ToArray();
            var patch = PlanePatch.Boundary(fit, points);
            if (patch.Area >= options.MinimumArea)
            {
                var rms = Math.Sqrt(points.Average(p => Math.Pow(PlanePatch.Distance(fit.Normal, fit.Offset, p), 2)));
                surfaces.Add(new(surfaces.Count + 1, StructureSurfaceKind.Unknown, fit.Normal, fit.Offset,
                    fit.Center, patch.Boundary, patch.Area, rms, inliers.Select(i => samples[i]).ToArray()));
            }
            progress?.Report(5 + 85d * (attempt + 1) / (options.MaximumPlanes * 2));
        }
        token.ThrowIfCancellationRequested();
        if (samples.Count > 0) Classify(surfaces, samples, options);
        progress?.Report(100);
        return new(surfaces.ToArray(), samples.Count, total, options);
    }

    private static void Classify(List<StructureSurface> surfaces, List<StructureSample> samples, StructureAnalysisOptions options)
    {
        var xs = samples.Select(p => p.Position.X).Order().ToArray();
        var ys = samples.Select(p => p.Position.Y).Order().ToArray();
        var zs = samples.Select(p => p.Position.Z).Order().ToArray();
        float Low(float[] p) => p[(int)((p.Length - 1) * .01)];
        float High(float[] p) => p[(int)((p.Length - 1) * .99)];
        var bottom = Low(zs); var top = High(zs);
        if (top - bottom < 1.8f) return; // A flat tabletop is not evidence of a room.
        var footprint = Math.Max(.1, (High(xs) - Low(xs)) * (High(ys) - Low(ys)));
        var margin = Math.Max(.25, options.DistanceTolerance * 5);
        for (var i = 0; i < surfaces.Count; i++)
        {
            var surface = surfaces[i];
            var kind = StructureSurfaceKind.Unknown;
            if (Math.Abs(surface.Normal.Z) >= .978f && surface.AreaSquareMeters >= Math.Max(2, footprint * .2))
            {
                if (Math.Abs(surface.Center.Z - bottom) <= margin) kind = StructureSurfaceKind.Floor;
                else if (Math.Abs(surface.Center.Z - top) <= margin) kind = StructureSurfaceKind.Ceiling;
            }
            else if (Math.Abs(surface.Normal.Z) <= .208f && surface.AreaSquareMeters >= 1 &&
                     surface.Samples.Min(p => p.Position.Z) <= bottom + margin &&
                     surface.Samples.Max(p => p.Position.Z) >= top - margin)
                kind = StructureSurfaceKind.Wall;
            surfaces[i] = surface with { Kind = kind };
        }
    }
}

/// <summary>Bounded geometric instance proposals. Semantic classes require a separate trained model.</summary>
public static class PointObjectAnalyzer
{
    public const int MaximumSamples = 30_000, MaximumObjects = 64;
    private readonly record struct Cell(long X, long Y, long Z);
    public static StructureObject[] Analyze(ScanProject project, StructureAnalysisDocument document, ObjectGroupingOptions options,
        CancellationToken token = default)
    {
        options.Validate(); project.Validate(token); token.ThrowIfCancellationRequested();
        if (document.IsStale) throw new System.IO.InvalidDataException("Сначала обновите анализ поверхностей.");
        var planes = options.ExcludeConfirmedPlanes ? document.Result.Surfaces.Where(s => s.Review == StructureReviewState.Confirmed && s.Kind != StructureSurfaceKind.Unknown).ToArray() : [];
        var count = project.Objects.Where(o => o.IsVisible).Sum(o => o.PointCount);
        var target = Math.Min(count, MaximumSamples); var rank = 0; var sampled = 0;
        var samples = new List<StructureSample>(target);
        foreach (var obj in project.Objects.Where(o => o.IsVisible))
            for (var i = 0; i < obj.Source.Points.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!obj.Includes(i)) continue;
                if (sampled < target && rank == (long)sampled * count / target)
                {
                    ++sampled; var p = Vector3.Transform(obj.Source.Points[i].Position, obj.Matrix);
                    if (!InspectionPoint.From(p).IsValid) throw new System.IO.InvalidDataException("Некорректные координаты предметов.");
                    if (!planes.Any(s => Math.Abs(PlanePatch.Distance(s.Normal, s.Offset, p)) <= document.Result.Options.DistanceTolerance))
                        samples.Add(new(new(obj.Id, i), p));
                }
                ++rank;
            }
        if (samples.Count == 0) return [];
        var origin = samples.Aggregate(new Vector3(float.MaxValue), (v, s) => Vector3.Min(v, s.Position));
        Cell Key(Vector3 p) => new((long)Math.Floor(((double)p.X - origin.X) / options.CellSize),
            (long)Math.Floor(((double)p.Y - origin.Y) / options.CellSize), (long)Math.Floor(((double)p.Z - origin.Z) / options.CellSize));
        var cells = new Dictionary<Cell, List<int>>();
        for (var i = 0; i < samples.Count; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var key = Key(samples[i].Position);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            list.Add(i);
        }
        var visited = new HashSet<Cell>(); var components = new List<StructureSample[]>(); var queue = new Queue<Cell>();
        foreach (var start in cells.Keys)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(start)) continue;
            queue.Enqueue(start); var group = new List<StructureSample>();
            while (queue.TryDequeue(out var current))
            {
                token.ThrowIfCancellationRequested();
                group.AddRange(cells[current].Select(i => samples[i]));
                for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
                {
                    var next = new Cell(current.X + x, current.Y + y, current.Z + z);
                    if (cells.ContainsKey(next) && visited.Add(next)) queue.Enqueue(next);
                }
            }
            if (group.Count >= options.MinimumPoints) components.Add(group.ToArray());
        }
        return components.OrderByDescending(s => s.Length).Take(MaximumObjects).Select((s, i) => Create(i + 1, $"Предмет {i + 1}", s)).ToArray();
    }
    public static StructureObject Create(int id, string label, StructureSample[] samples, StructureReviewState review = StructureReviewState.Proposed)
    {
        uint[] colors = [0x63dfae, 0xf6ae60, 0x6ab6ff, 0xe58bd6, 0xc5d76d, 0xad98f4, 0xff8580, 0x64d4de];
        var color = id <= colors.Length ? colors[(id - 1) % colors.Length] : (uint)(0x408080 + id * 104729) & 0xffffff;
        return new(id, label, color, samples, samples.Aggregate(new Vector3(float.MaxValue), (v, s) => Vector3.Min(v, s.Position)),
            samples.Aggregate(new Vector3(float.MinValue), (v, s) => Vector3.Max(v, s.Position)), review);
    }
}
