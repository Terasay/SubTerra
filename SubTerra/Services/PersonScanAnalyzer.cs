using System.Numerics;
using SubTerra.Models;
using SubTerra.Rendering;

namespace SubTerra.Services;

public interface IPersonScanAnalyzer
{
    Task<PersonDetectionResult> AnalyzeAsync(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces,
        float threshold, PointClipPlane? clip, IProgress<double>? progress, CancellationToken token);
    Task<PersonDetectionResult> AnalyzeForReviewAsync(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces,
        float threshold, PointClipPlane? clip, IProgress<double>? progress, CancellationToken token) =>
        AnalyzeAsync(project, poses, surfaces, threshold, clip, progress, token);
}

public sealed class PersonScanAnalyzer(Func<IPersonImageDetector>? detectorFactory = null) : IPersonScanAnalyzer
{
    public static ScanViewPose[] RoomViews(StructureAnalysisDocument document)
    {
        if (document.IsStale) throw new ArgumentException("Анализ помещения устарел.");
        return CreateRoomViews(document.Result.Surfaces, RoomPlanner.IsCurrent(document) ? document.Room?.InteriorPoint : null);
    }
    public static ScanViewPose[] RoomViews(StructureSurface[] surfaces) => CreateRoomViews(surfaces, null);

    public static ScanViewPose[] RoomDetailViews(ScanViewPose[] overview)
    {
        if (overview.Length is < 8 or >= 16) return [];
        var count = Math.Min(8, 16 - overview.Length);
        var eye = overview[0].Position;
        return Enumerable.Range(0, count).Select(i =>
        {
            var angle = (i + .5f) * (2 * MathF.PI / count);
            return new ScanViewPose(eye, new(MathF.Cos(angle), MathF.Sin(angle), -.12f), Vector3.UnitZ, 55);
        }).ToArray();
    }

    public static PersonDetectionResult CombineRoomViews(PersonDetectionResult overview, PersonDetectionResult details)
    {
        var findings = overview.Findings.Concat(details.Findings.Select(f => f with { ViewIndex = f.ViewIndex + overview.Views.Length }))
            .Select((f, index) => f with { Id = index + 1, SpatialGroup = null }).ToArray();
        return overview with
        {
            Views = [.. overview.Views, .. details.Views],
            Findings = GroupAcrossViews(findings),
            RejectedChairCandidates = overview.RejectedChairCandidates + details.RejectedChairCandidates,
            RejectedWallCandidates = overview.RejectedWallCandidates + details.RejectedWallCandidates,
            RejectedFurnitureGroups = overview.RejectedFurnitureGroups + details.RejectedFurnitureGroups,
            RejectedDecorGroups = overview.RejectedDecorGroups + details.RejectedDecorGroups
        };
    }

    public static PersonDetectionResult ConsolidateDetailedFindings(PersonDetectionResult result)
    {
        // The caller has already applied the final score threshold. A lower-scoring body must survive
        // that threshold before it can replace a higher-scoring head in the same view.
        var byView = result.Findings.ToLookup(f => f.ViewIndex);
        var remaining = result.Findings.Where(f => !byView[f.ViewIndex].Any(other => IsContainedUpperFragment(f, other))).ToArray();
        return result with { Findings = GroupAcrossViews(remaining, reconcilePartialViews: true) };
    }

    private static ScanViewPose[] CreateRoomViews(StructureSurface[] surfaces, Vector3? interior)
    {
        var floor = surfaces.Where(s => s.Kind == StructureSurfaceKind.Floor && s.Review != StructureReviewState.Rejected).MaxBy(s => s.AreaSquareMeters)
            ?? throw new ArgumentException("Сначала найдите пол помещения.");
        var eye = floor.Center + Vector3.UnitZ * 1.55f;
        if (interior is { } seed) eye = new(seed.X, seed.Y, floor.Center.Z + 1.55f);
        var overview = Enumerable.Range(0, 8).Select(i => new ScanViewPose(eye,
            new(MathF.Cos(i * MathF.PI / 4), MathF.Sin(i * MathF.PI / 4), -.03f), Vector3.UnitZ, 80));
        return overview.Concat(ViewsAlongFloor(floor, eye)).ToArray();
    }

    private static IEnumerable<ScanViewPose> ViewsAlongFloor(StructureSurface floor, Vector3 overview)
    {
        if (floor.Boundary.Length < 3 || floor.Samples.Length < 64) yield break;
        // The principal XY direction of the measured floor makes the layout rotation-independent.
        double xx = 0, yy = 0, xy = 0;
        foreach (var sample in floor.Samples)
        {
            var delta = sample.Position - floor.Center;
            xx += delta.X * (double)delta.X;
            yy += delta.Y * (double)delta.Y;
            xy += delta.X * (double)delta.Y;
        }
        var angle = (float)(.5 * Math.Atan2(2 * xy, xx - yy));
        var along = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);
        var across = new Vector3(-along.Y, along.X, 0);
        var bounds = floor.Boundary.Select(p => p - floor.Center).ToArray();
        var start = bounds.Min(p => Vector3.Dot(p, along));
        var length = bounds.Max(p => Vector3.Dot(p, along)) - start;
        var width = bounds.Max(p => Vector3.Dot(p, across)) - bounds.Min(p => Vector3.Dot(p, across));
        if (length < 8 || width < .6f || length / width < 2.8f) yield break;

        var floorPoints = floor.Samples.Select(s =>
        {
            var p = s.Position - floor.Center;
            return (Along: Vector3.Dot(p, along), Across: Vector3.Dot(p, across), Z: s.Position.Z);
        }).ToArray();
        foreach (var fraction in new[] { .12f, .33f, .67f, .88f })
        {
            var station = start + fraction * length;
            // Sample the local cross-section. A global bounding rectangle can put a camera beyond a wall or over a gap.
            var support = floorPoints.Where(p => Math.Abs(p.Along - station) <= .65f).ToArray();
            if (support.Length < 12) continue;
            var lateral = support.Select(p => p.Across).Order().ToArray();
            var middle = (lateral[lateral.Length / 5] + lateral[lateral.Length - 1 - lateral.Length / 5]) / 2;
            var point = floor.Center + along * station + across * middle;
            point.Z = support.Select(p => p.Z).Order().ElementAt(support.Length / 2) + 1.55f;
            if (Vector3.DistanceSquared(point, overview) < 1) continue;
            yield return new(point, along - Vector3.UnitZ * .12f, Vector3.UnitZ, 84);
            yield return new(point, -along - Vector3.UnitZ * .12f, Vector3.UnitZ, 84);
        }
    }

    public Task<PersonDetectionResult> AnalyzeAsync(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces,
        float threshold, PointClipPlane? clip, IProgress<double>? progress, CancellationToken token) =>
        Analyze(project, poses, surfaces, threshold, clip, progress, verifyObjects: true, token);

    public Task<PersonDetectionResult> AnalyzeForReviewAsync(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces,
        float threshold, PointClipPlane? clip, IProgress<double>? progress, CancellationToken token) =>
        Analyze(project, poses, surfaces, threshold, clip, progress, verifyObjects: false, token);

    private Task<PersonDetectionResult> Analyze(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces,
        float threshold, PointClipPlane? clip, IProgress<double>? progress, bool verifyObjects, CancellationToken token) => Task.Run(() =>
    {
        project.Validate(token);
        if (poses.Length is < 1 or > 16) throw new ArgumentException("Для анализа нужны 1–16 ракурсов.");
        using var detector = detectorFactory?.Invoke() ?? new OnnxPersonDetector();
        var recheck = verifyObjects && detector.SupportsCloseupVerification;
        var views = new List<ScanImage>(); var findings = new List<PersonFinding>(); var rejectedChairs = 0;
        var rejectedWallImages = 0;
        foreach (var pose in poses)
        {
            token.ThrowIfCancellationRequested();
            var image = ScanImageRenderer.Render(project, pose, clip: clip, token: token);
            var boxes = detector.Detect(image, threshold, token);
            rejectedChairs += detector.RejectedChairCandidates;
            var frame = new List<PersonFinding>();
            foreach (var box in boxes)
            {
                var (min, max) = Locate(image, box, surfaces, token);
                if (min is null && LiesOnWall(image, box, surfaces))
                {
                    rejectedWallImages++;
                    continue;
                }
                frame.Add(new(0, views.Count, box, min, max));
            }
            foreach (var finding in frame)
                if (!frame.Any(other => IsNestedFragment(finding, other)))
                    findings.Add(finding with { Id = findings.Count + 1 });
            views.Add(image);
            progress?.Report((recheck ? 75d : 100d) * views.Count / poses.Length);
        }
        var grouped = GroupAcrossViews(findings);
        var excluded = new Dictionary<int, PersonCloseupEvidence>();
        if (recheck)
        {
            var suspects = grouped.Where(f => f.SpatialGroup.HasValue)
                .GroupBy(f => f.SpatialGroup!.Value)
                .Select(g => (Best: g.MaxBy(f => f.Box.Score)!, Views: g.Count()))
                .Where(g => g.Best.Box.Score < .75f)
                // Spend the bounded render budget on persistent groups and compact silhouettes first.
                // Aspect ratio affects scheduling only, so a seated person remains eligible.
                .OrderByDescending(g => g.Views)
                .ThenBy(g => (g.Best.Box.Bottom - g.Best.Box.Top) / Math.Max(1, g.Best.Box.Right - g.Best.Box.Left))
                .ThenByDescending(g => g.Best.Box.Score)
                .Take(12).Select(g => g.Best).ToArray();
            for (var i = 0; i < suspects.Length; i++)
            {
                var candidate = suspects[i];
                var objectEvidence = VerifyObject(project, detector, candidate, views[candidate.ViewIndex].Pose, clip, token);
                if (objectEvidence != PersonCloseupEvidence.None)
                    excluded.Add(candidate.SpatialGroup!.Value, objectEvidence);
                progress?.Report(75 + 25d * (i + 1) / suspects.Length);
            }
            if (excluded.Count > 0)
                grouped = grouped.Where(f => !f.SpatialGroup.HasValue || !excluded.ContainsKey(f.SpatialGroup.Value)).ToArray();
        }
        progress?.Report(100);
        token.ThrowIfCancellationRequested();
        return new PersonDetectionResult(views.ToArray(), grouped, OnnxPersonDetector.ModelName, rejectedChairs)
        {
            RejectedWallCandidates = rejectedWallImages,
            RejectedFurnitureGroups = excluded.Values.Count(e => e == PersonCloseupEvidence.Furniture),
            RejectedDecorGroups = excluded.Values.Count(e => e == PersonCloseupEvidence.PlantOrVase)
        };
    }, token);

    private static PersonCloseupEvidence VerifyObject(ScanProject project, IPersonImageDetector detector, PersonFinding candidate,
        ScanViewPose original, PointClipPlane? clip, CancellationToken token)
    {
        var min = candidate.Minimum!.Value;
        var max = candidate.Maximum!.Value;
        var center = (min + max) / 2;
        var extent = max - min;
        var approach = original.Position - center;
        approach.Z = 0;
        if (approach.LengthSquared() < .25f) return PersonCloseupEvidence.None;
        approach = Vector3.Normalize(approach);
        var lateral = new Vector3(-approach.Y, approach.X, 0);
        var distance = Math.Clamp(Math.Max(extent.Z, Math.Max(extent.X, extent.Y)) * 1.65f, 1.8f, 3.1f);
        int furniture = 0, weakFurniture = 0, decor = 0;
        var strongChair = false;
        foreach (var shift in new[] { 0f, -.45f, .45f, -.9f, .9f })
        {
            token.ThrowIfCancellationRequested();
            var offset = approach * distance + lateral * shift + Vector3.UnitZ * .55f;
            var pose = new ScanViewPose(center + offset, -offset, Vector3.UnitZ, 50);
            if (!InspectionPoint.From(pose.Position).IsValid || !TryProjectBounds(pose, min, max, out var target)) continue;
            var image = ScanImageRenderer.Render(project, pose, clip: clip, token: token);
            if (!HasCloseupSupport(image, target, min, max)) continue;
            switch (detector.CheckCloseup(image, target, token))
            {
                case PersonCloseupEvidence.Person: return PersonCloseupEvidence.None;
                case PersonCloseupEvidence.StrongChair: strongChair = true; break;
                case PersonCloseupEvidence.Furniture: furniture++; break;
                case PersonCloseupEvidence.WeakFurniture: weakFurniture++; break;
                case PersonCloseupEvidence.PlantOrVase: decor++; break;
            }
        }
        if (strongChair || furniture > 0 && furniture + weakFurniture >= 2) return PersonCloseupEvidence.Furniture;
        return decor >= 2 ? PersonCloseupEvidence.PlantOrVase : PersonCloseupEvidence.None;
    }

    private static bool TryProjectBounds(ScanViewPose pose, Vector3 min, Vector3 max, out ImageBox target)
    {
        const int center = 208;
        target = null!;
        var forward = Vector3.Normalize(pose.Forward);
        var right = Vector3.Normalize(Vector3.Cross(forward, pose.Up));
        var up = Vector3.Cross(right, forward);
        var focal = center / MathF.Tan(pose.FieldOfView * MathF.PI / 360);
        var low = new Vector2(float.MaxValue);
        var high = new Vector2(float.MinValue);
        for (var i = 0; i < 8; i++)
        {
            var p = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            p -= pose.Position;
            var depth = Vector3.Dot(p, forward);
            if (depth < .12f) return false;
            var pixel = new Vector2(center + focal * Vector3.Dot(p, right) / depth, center - focal * Vector3.Dot(p, up) / depth);
            low = Vector2.Min(low, pixel);
            high = Vector2.Max(high, pixel);
        }
        target = new(Math.Clamp(low.X, 0, 415), Math.Clamp(low.Y, 0, 415), Math.Clamp(high.X, 0, 415), Math.Clamp(high.Y, 0, 415), 0);
        return target.Area >= 64;
    }

    private static bool HasCloseupSupport(ScanImage image, ImageBox box, Vector3 min, Vector3 max)
    {
        var bounds = new ScanBox(min - new Vector3(.08f), max + new Vector3(.08f));
        var count = 0;
        for (var y = 0; y < 10; y++)
        for (var x = 0; x < 10; x++)
        {
            var px = (int)(box.Left + (x + .5f) * (box.Right - box.Left) / 10);
            var py = (int)(box.Top + (y + .5f) * (box.Bottom - box.Top) / 10);
            var slot = py * image.Size + px;
            if (float.IsFinite(image.Depth[slot]) && bounds.Contains(image.Positions[slot]) && ++count >= 8) return true;
        }
        return false;
    }

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

    public static PersonFinding[] GroupAcrossViews(IReadOnlyList<PersonFinding> findings) =>
        GroupAcrossViews(findings, reconcilePartialViews: false);

    private static PersonFinding[] GroupAcrossViews(IReadOnlyList<PersonFinding> findings, bool reconcilePartialViews)
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
                var overlap = groups[j].Min(other => SpatialMatch(finding, other, reconcilePartialViews));
                if (overlap > bestOverlap) { bestOverlap = overlap; best = j; }
            }
            if (best < 0) { best = groups.Count; groups.Add([]); }
            groups[best].Add(finding);
            result[i] = finding with { SpatialGroup = best + 1 };
        }
        return result;
    }

    public static bool IsNestedFragment(PersonFinding small, PersonFinding large)
    {
        if (small.ViewIndex != large.ViewIndex || large.Box.Score < small.Box.Score ||
            small.Box.Area <= 0 || large.Box.Area < small.Box.Area * 1.8f ||
            small.Minimum is null || small.Maximum is null || large.Minimum is null || large.Maximum is null) return false;
        if (ImageOverlap(small.Box, large.Box) < .9f * small.Box.Area) return false;
        var (asize, bsize, common, distance) = SpatialOverlap(small, large);
        return distance <= .2f && asize.Z < .7f * bsize.Z &&
            common.X * common.Y >= .82f * asize.X * asize.Y && common.Z >= .85f * asize.Z;
    }

    private static bool IsContainedUpperFragment(PersonFinding small, PersonFinding large)
    {
        if (small.ViewIndex != large.ViewIndex || small.Box.Area <= 0 || large.Box.Area < 3 * small.Box.Area ||
            small.Minimum is null || small.Maximum is null || large.Minimum is null || large.Maximum is null ||
            large.Review == StructureReviewState.Rejected ||
            small.Review == StructureReviewState.Confirmed && large.Review != StructureReviewState.Confirmed ||
            ImageOverlap(small.Box, large.Box) < .98f * small.Box.Area) return false;

        var (smallSize, largeSize, common, _) = SpatialOverlap(small, large);
        var footprint = smallSize.X * smallSize.Y;
        return footprint > 0 && smallSize.Z > 0 && smallSize.Z <= .45f * largeSize.Z &&
            common.X * common.Y >= .97f * footprint && common.Z >= .9f * smallSize.Z &&
            small.Minimum.Value.Z >= large.Minimum.Value.Z + .5f * largeSize.Z;
    }

    private static float ImageOverlap(ImageBox a, ImageBox b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));

    private static (Vector3 First, Vector3 Second, Vector3 Common, float CenterDistance) SpatialOverlap(PersonFinding a, PersonFinding b)
    {
        var amin = a.Minimum!.Value; var amax = a.Maximum!.Value;
        var bmin = b.Minimum!.Value; var bmax = b.Maximum!.Value;
        var common = Vector3.Max(Vector3.Zero, Vector3.Min(amax, bmax) - Vector3.Max(amin, bmin));
        var delta = (amin - bmin + amax - bmax) / 2;
        return (amax - amin, bmax - bmin, common, new Vector2(delta.X, delta.Y).Length());
    }

    private static float SpatialMatch(PersonFinding a, PersonFinding b, bool reconcilePartialViews)
    {
        static float Volume(Vector3 size) => size.X * size.Y * size.Z;
        var (asize, bsize, common, distance) = SpatialOverlap(a, b);
        var volume = Volume(common);
        var iou = volume / Math.Max(1e-9f, Volume(asize) + Volume(bsize) - volume);
        // A side view can contain only the upper body. The overlapping footprint and close XY
        // centers still match that partial observation to the full-height detection.
        var firstArea = asize.X * asize.Y;
        var secondArea = bsize.X * bsize.Y;
        var narrowArea = Math.Min(firstArea, secondArea);
        var height = Math.Min(asize.Z, bsize.Z);
        var footprint = common.X * common.Y / Math.Max(1e-9f, narrowArea);
        if (distance <= .3f && footprint >= .45f && common.Z >= .65f * height)
            return Math.Max(iou, .2f + .25f * footprint);

        // Wide views may include depth behind a seated person. Reconcile a narrow observation only
        // when most of its footprint and almost its entire height lie inside the broad observation.
        var conflictingReviews = a.Review != StructureReviewState.Proposed && b.Review != StructureReviewState.Proposed && a.Review != b.Review;
        if (reconcilePartialViews && !conflictingReviews && distance <= .45f && height >= .45f &&
            narrowArea <= .55f * Math.Max(firstArea, secondArea) && footprint >= .75f && common.Z >= .9f * height)
            return Math.Max(iou, .2f + .25f * footprint);
        return iou;
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
