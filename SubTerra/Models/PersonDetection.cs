using System.Numerics;

namespace SubTerra.Models;

public sealed record ScanViewPose(Vector3 Position, Vector3 Forward, Vector3 Up, float FieldOfView = 75);
public sealed record ImageBox(float Left, float Top, float Right, float Bottom, float Score)
{
    public float Area => Math.Max(0, Right - Left) * Math.Max(0, Bottom - Top);
}
public sealed record ScanImage(ScanViewPose Pose, int Size, byte[] Rgb, float[] Depth, Vector3[] Positions)
{
    public double Coverage => Depth.Count(float.IsFinite) / (double)Depth.Length;
}
public sealed record PersonFinding(int Id, int ViewIndex, ImageBox Box, Vector3? Minimum, Vector3? Maximum,
    StructureReviewState Review = StructureReviewState.Proposed)
{
    public int? SpatialGroup { get; init; }
    public float? DetectorScore { get; init; }
    public string Label => SpatialGroup is { } group ? $"3D №{group} · рамка {Id:00} · {Box.Score:P0}" : $"Рамка {Id:00} · без 3D · {Box.Score:P0}";
    public string Details => Review switch { StructureReviewState.Confirmed => "Подтверждено оператором", StructureReviewState.Rejected => "Ложная находка", _ => "Предположение модели · проверьте изображение" };
    public string LocationText => Minimum is null ? "Недостаточно точек для надёжной 3D-привязки" : "Приблизительная 3D-привязка по видимым точкам";
}
public sealed record PersonDetectionResult(ScanImage[] Views, PersonFinding[] Findings, string Model, int RejectedChairCandidates = 0)
{
    public int RejectedWallCandidates { get; init; }
}
