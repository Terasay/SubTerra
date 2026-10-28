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
