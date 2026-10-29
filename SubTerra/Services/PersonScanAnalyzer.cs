using System.Numerics;
using SubTerra.Models;
using SubTerra.Rendering;

namespace SubTerra.Services;

public interface IPersonScanAnalyzer
{
    Task<PersonDetectionResult> AnalyzeAsync(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces,
        float threshold, PointClipPlane? clip, IProgress<double>? progress, CancellationToken token);
}

public sealed class PersonScanAnalyzer(Func<IPersonImageDetector>? detectorFactory = null) : IPersonScanAnalyzer
{
    public static ScanViewPose[] RoomViews(StructureAnalysisDocument document)
    {
        if (document.IsStale) throw new ArgumentException("Анализ помещения устарел.");
        var floor = document.Result.Surfaces.Where(s => s.Kind == StructureSurfaceKind.Floor && s.Review != StructureReviewState.Rejected).MaxBy(s => s.AreaSquareMeters)
            ?? throw new ArgumentException("Сначала найдите пол помещения.");
        var eye = floor.Center + Vector3.UnitZ * 1.55f;
        if (RoomPlanner.IsCurrent(document) && document.Room?.InteriorPoint is { } seed) eye = new(seed.X, seed.Y, floor.Center.Z + 1.55f);
        return Enumerable.Range(0, 8).Select(i => new ScanViewPose(eye, new(MathF.Cos(i * MathF.PI / 4), MathF.Sin(i * MathF.PI / 4), -.03f), Vector3.UnitZ, 80)).ToArray();
    }
    public Task<PersonDetectionResult> AnalyzeAsync(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces,
        float threshold, PointClipPlane? clip, IProgress<double>? progress, CancellationToken token) => Task.Run(() =>
    {
        project.Validate(token);
        if (poses.Length is < 1 or > 16) throw new ArgumentException("Для анализа нужны 1–16 ракурсов.");
        using var detector = detectorFactory?.Invoke() ?? new OnnxPersonDetector();
        var views = new List<ScanImage>(); var findings = new List<PersonFinding>();
        foreach (var pose in poses)
        {
            token.ThrowIfCancellationRequested();
            var image = ScanImageRenderer.Render(project, pose, clip: clip, token: token);
            var boxes = detector.Detect(image, threshold, token);
            foreach (var box in boxes)
            {
                var (min, max) = Locate(image, box, surfaces, token);
                findings.Add(new(findings.Count + 1, views.Count, box, min, max));
            }
            views.Add(image); progress?.Report(100d * views.Count / poses.Length);
        }
        token.ThrowIfCancellationRequested();
        return new PersonDetectionResult(views.ToArray(), findings.ToArray(), OnnxPersonDetector.ModelName);
    }, token);

    private static (Vector3? Min, Vector3? Max) Locate(ScanImage image, ImageBox box, StructureSurface[] surfaces, CancellationToken token)
    {
        var samples = new Dictionary<(int, int, int), (Vector3 P, float Depth)>();
        var x0 = (int)Math.Clamp(box.Left + (box.Right - box.Left) * .15f, 0, image.Size - 1);
        var x1 = (int)Math.Clamp(box.Right - (box.Right - box.Left) * .15f, 0, image.Size - 1);
        var y0 = (int)Math.Clamp(box.Top + (box.Bottom - box.Top) * .1f, 0, image.Size - 1);
        var y1 = (int)Math.Clamp(box.Bottom - (box.Bottom - box.Top) * .05f, 0, image.Size - 1);
        for (var y = y0; y <= y1; y += 2) for (var x = x0; x <= x1; x += 2)
        {
            if ((x - x0 & 31) == 0) token.ThrowIfCancellationRequested();
            var slot = y * image.Size + x; var depth = image.Depth[slot]; if (!float.IsFinite(depth)) continue;
            var p = image.Positions[slot];
            if (surfaces.Any(s => s.Kind != StructureSurfaceKind.Unknown && s.Review != StructureReviewState.Rejected && Math.Abs(PlanePatch.Distance(s.Normal, s.Offset, p)) < .06)) continue;
            samples[((int)Math.Floor(p.X / .025), (int)Math.Floor(p.Y / .025), (int)Math.Floor(p.Z / .025))] = (p, depth);
        }
        if (samples.Count < 16) return (null, null);
        var sorted = samples.Values.OrderBy(p => p.Depth).ToArray(); var depthCenter = sorted[sorted.Length / 3].Depth;
        var support = sorted.Where(p => Math.Abs(p.Depth - depthCenter) <= .65f).Select(p => p.P).ToArray();
        if (support.Length < 16) return (null, null);
        var min = support.Aggregate(new Vector3(float.MaxValue), Vector3.Min); var max = support.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        if ((max - min).Length() > 3.5f || max.Z - min.Z > 2.8f) return (null, null);
        return (min - new Vector3(.04f), max + new Vector3(.04f));
    }
}
