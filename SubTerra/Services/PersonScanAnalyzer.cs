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
        return CreateRoomViews(document.Result.Surfaces, RoomPlanner.IsCurrent(document) ? document.Room?.InteriorPoint : null);
    }
    public static ScanViewPose[] RoomViews(StructureSurface[] surfaces) => CreateRoomViews(surfaces, null);
    private static ScanViewPose[] CreateRoomViews(StructureSurface[] surfaces, Vector3? interior)
    {
        var floor = surfaces.Where(s => s.Kind == StructureSurfaceKind.Floor && s.Review != StructureReviewState.Rejected).MaxBy(s => s.AreaSquareMeters)
            ?? throw new ArgumentException("Сначала найдите пол помещения.");
        var eye = floor.Center + Vector3.UnitZ * 1.55f;
        if (interior is { } seed) eye = new(seed.X, seed.Y, floor.Center.Z + 1.55f);
        return Enumerable.Range(0, 8).Select(i => new ScanViewPose(eye, new(MathF.Cos(i * MathF.PI / 4), MathF.Sin(i * MathF.PI / 4), -.03f), Vector3.UnitZ, 80)).ToArray();
    }
    public Task<PersonDetectionResult> AnalyzeAsync(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces,
        float threshold, PointClipPlane? clip, IProgress<double>? progress, CancellationToken token) => Task.Run(() =>
    {
        project.Validate(token);
        if (poses.Length is < 1 or > 16) throw new ArgumentException("Для анализа нужны 1–16 ракурсов.");
        using var detector = detectorFactory?.Invoke() ?? new OnnxPersonDetector();
        var views = new List<ScanImage>(); var findings = new List<PersonFinding>(); var rejectedChairs = 0;
        var rejectedWallImages = 0;
        foreach (var pose in poses)
        {
            token.ThrowIfCancellationRequested();
            var image = ScanImageRenderer.Render(project, pose, clip: clip, token: token);
            var boxes = detector.Detect(image, threshold, token);
            rejectedChairs += detector.RejectedChairCandidates;
            foreach (var box in boxes)
            {
                var (min, max) = Locate(image, box, surfaces, token);
                if (min is null && LiesOnWall(image, box, surfaces))
                {
                    rejectedWallImages++;
                    continue;
                }
                findings.Add(new(findings.Count + 1, views.Count, box, min, max));
            }
            views.Add(image); progress?.Report(100d * views.Count / poses.Length);
        }
        token.ThrowIfCancellationRequested();
        return new PersonDetectionResult(views.ToArray(), GroupAcrossViews(findings), OnnxPersonDetector.ModelName, rejectedChairs)
        { RejectedWallCandidates = rejectedWallImages };
    }, token);

    public static bool LiesOnWall(ScanImage image, ImageBox box, StructureSurface[] surfaces)
    {
        // Sample the inside of the candidate. A hole or a single background pixel is not wall evidence.
        var points = new List<Vector3>(64);
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            var px = (int)Math.Clamp(box.Left + (.15f + .1f * x) * (box.Right - box.Left), 0, image.Size - 1);
            var py = (int)Math.Clamp(box.Top + (.15f + .1f * y) * (box.Bottom - box.Top), 0, image.Size - 1);
            var slot = py * image.Size + px;
            if (float.IsFinite(image.Depth[slot])) points.Add(image.Positions[slot]);
        }
        if (points.Count < 58) return false;
        foreach (var wall in surfaces)
        {
            if (wall.Kind != StructureSurfaceKind.Wall || wall.Review == StructureReviewState.Rejected || wall.Boundary.Length < 3) continue;
            var onPatch = points.Count(p => Math.Abs(PlanePatch.Distance(wall.Normal, wall.Offset, p)) <= .045 &&
                InsideWallBoundary(wall, p));
            if (onPatch >= points.Count * .95) return true;
        }
        return false;
    }

    private static bool InsideWallBoundary(StructureSurface wall, Vector3 point)
    {
        // The measured patch is a convex hull. Accept either boundary winding.
        var positive = false;
        var negative = false;
        var last = wall.Boundary[^1];
        foreach (var next in wall.Boundary)
        {
            var side = Vector3.Dot(Vector3.Cross(next - last, point - last), wall.Normal);
            positive |= side > .0001f;
            negative |= side < -.0001f;
            if (positive && negative) return false;
            last = next;
        }
        return true;
    }

    public static PersonFinding[] GroupAcrossViews(IReadOnlyList<PersonFinding> findings)
    {
        var groups = new List<List<PersonFinding>>();
        var result = findings.ToArray();
        for (var i = 0; i < result.Length; i++)
        {
            var finding = result[i];
            if (finding.Minimum is null || finding.Maximum is null) continue;
            var best = -1;
            var bestOverlap = .30f;
            for (var j = 0; j < groups.Count; j++)
            {
                if (groups[j].Any(other => other.ViewIndex == finding.ViewIndex)) continue;
                // Require overlap with every member. A chain of weak matches can join two neighbours.
                var overlap = groups[j].Min(other => VolumeIoU(finding, other));
                if (overlap > bestOverlap) { bestOverlap = overlap; best = j; }
            }
            if (best < 0) { best = groups.Count; groups.Add([]); }
            groups[best].Add(finding);
            result[i] = finding with { SpatialGroup = best + 1 };
        }
        return result;
    }

    private static float VolumeIoU(PersonFinding a, PersonFinding b)
    {
        static float Volume(Vector3 size) => size.X * size.Y * size.Z;
        var amin = a.Minimum!.Value; var amax = a.Maximum!.Value;
        var bmin = b.Minimum!.Value; var bmax = b.Maximum!.Value;
        var common = Vector3.Max(Vector3.Zero, Vector3.Min(amax, bmax) - Vector3.Max(amin, bmin));
        var overlap = Volume(common);
        return overlap / Math.Max(1e-9f, Volume(amax - amin) + Volume(bmax - bmin) - overlap);
    }

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
