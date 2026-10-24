using System.IO;
using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Simulation;

public static class ScannerPlan
{
    public static void Validate(IReadOnlyList<ScannerAdjustment> adjustments, ScanSettings settings, int? captured = null)
    {
        if (adjustments.Count > settings.FrameCount) throw new InvalidDataException("Слишком много перестановок датчика.");
        var previous = -1;
        foreach (var item in adjustments)
        {
            if (item is null || item.FirstFrame <= previous || item.FirstFrame >= settings.FrameCount ||
                (captured is not null && item.FirstFrame > captured) ||
                !float.IsFinite(item.RoutePosition) || item.RoutePosition is < .01f or > .98f ||
                !float.IsFinite(item.LateralOffset) || MathF.Abs(item.LateralOffset) > 1 ||
                !float.IsFinite(item.HeightOffset) || MathF.Abs(item.HeightOffset) > .5f ||
                !float.IsFinite(item.YawDegrees) || MathF.Abs(item.YawDegrees) > 180)
                throw new InvalidDataException("Некорректные параметры перестановки датчика.");
            previous = item.FirstFrame;
        }
    }

    public static ScannerAdjustment? Active(IReadOnlyList<ScannerAdjustment> adjustments, int frame) =>
        adjustments.LastOrDefault(a => a.FirstFrame <= frame);

    public static float PositionAt(ScanSettings settings, IReadOnlyList<ScannerAdjustment> adjustments, int frame)
    {
        var active = Active(adjustments, frame);
        if (active is null)
            return settings.StartProgress + (1 - settings.StartProgress) * (.004f + .992f * frame / (settings.FrameCount - 1));
        var remaining = settings.FrameCount - active.FirstFrame - 1;
        return active.RoutePosition + (1 - active.RoutePosition) * .992f *
            (remaining == 0 ? 0 : (frame - active.FirstFrame) / (float)remaining);
    }

    public static SensorPose Pose(string scenario, float position, ScannerAdjustment? adjustment)
    {
        var original = CaveFactory.Pose(scenario, position);
        if (adjustment is null) return original;
        var (forward, right, up) = CaveFactory.Basis(scenario, position);
        var rotation = Quaternion.CreateFromAxisAngle(up, adjustment.YawDegrees * MathF.PI / 180);
        forward = Vector3.Transform(forward, rotation);
        right = Vector3.Transform(right, rotation);
        var basis = new Matrix4x4(right.X, right.Y, right.Z, 0, forward.X, forward.Y, forward.Z, 0,
            up.X, up.Y, up.Z, 0, 0, 0, 0, 1);
        var (_, originalRight, originalUp) = CaveFactory.Basis(scenario, position);
        return new(original.Position + originalRight * adjustment.LateralOffset + originalUp * adjustment.HeightOffset,
            Quaternion.CreateFromRotationMatrix(basis));
    }

    public static bool IsClear(CaveScene scene, ScannerAdjustment adjustment)
    {
        var index = new TriangleIndex(scene.Vertices, scene.Indices);
        var origin = CaveFactory.Center(scene.Scenario.Id, adjustment.RoutePosition);
        var pose = Pose(scene.Scenario.Id, adjustment.RoutePosition, adjustment);
        var shift = pose.Position - origin;
        if (shift.Length() > .001f && index.Cast(origin, Vector3.Normalize(shift), shift.Length() + .18f, out _)) return false;
        foreach (var direction in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            if (index.Cast(pose.Position, direction, .18f, out _)) return false;
        return true;
    }

    public static ScanCoverage[] Coverage(ScanSettings settings, IReadOnlyList<ScannerAdjustment> adjustments, int frames)
    {
        if (frames <= 0) return [];
        var ranges = new List<ScanCoverage>();
        var boundaries = new[] { 0 }.Concat(adjustments.Select(a => a.FirstFrame)).Where(f => f < frames).Distinct().Order().ToArray();
        for (var i = 0; i < boundaries.Length; i++)
        {
            var begin = boundaries[i];
            var last = (i + 1 < boundaries.Length ? boundaries[i + 1] : frames) - 1;
            var active = Active(adjustments, begin);
            var start = active?.RoutePosition ?? settings.StartProgress;
            var end = active is null
                ? settings.StartProgress + (1 - settings.StartProgress) * (last + 1f) / settings.FrameCount
                : Math.Min(1, PositionAt(settings, adjustments, last) + .008f);
            ranges.Add(new(start, end));
        }
        var merged = new List<ScanCoverage>();
        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = new(merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else merged.Add(range);
        }
        return merged.ToArray();
    }
}
