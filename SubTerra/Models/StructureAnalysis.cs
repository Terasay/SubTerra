using System.Numerics;

namespace SubTerra.Models;

public enum StructureSurfaceKind { Unknown, Floor, Wall, Ceiling }
public enum StructureReviewState { Proposed, Confirmed, Rejected }
public readonly record struct SourcePointReference(Guid ObjectId, int PointIndex);
public readonly record struct StructureSample(SourcePointReference Source, Vector3 Position);

public sealed record StructureSurface(int Id, StructureSurfaceKind Kind, Vector3 Normal, double Offset,
    Vector3 Center, Vector3[] Boundary, double AreaSquareMeters, double RmsMeters, StructureSample[] Samples,
    StructureReviewState Review = StructureReviewState.Proposed, StructureSurfaceKind? SuggestedKind = null)
{
    public string KindText => Kind switch
    {
        StructureSurfaceKind.Floor => "Пол", StructureSurfaceKind.Ceiling => "Потолок",
        StructureSurfaceKind.Wall => "Стена", _ => "Не определено"
    };
    public string Title => $"{Id:00} · {KindText}";
    public string ReviewText => Review switch
    {
        StructureReviewState.Confirmed => "Подтверждено оператором",
        StructureReviewState.Rejected => "Отклонено · скрыто", _ => "Кандидат · требует проверки"
    };
    public string Details => $"≈ {AreaSquareMeters:F1} м² · разброс {RmsMeters * 100:F1} см · {Samples.Length:N0} точек выборки";
}

public sealed record StructureAnalysisOptions(double DistanceTolerance = .025, int MaximumSamples = 30_000,
    int Iterations = 192, int MaximumPlanes = 12, int MinimumSupport = 64, double MinimumArea = .5)
{
    public void Validate()
    {
        if (!double.IsFinite(DistanceTolerance) || DistanceTolerance is < .001 or > .1 ||
            MaximumSamples is < 128 or > 50_000 || Iterations is < 32 or > 512 || MaximumPlanes is < 1 or > 24 ||
            MinimumSupport is < 16 or > 1000 || MinimumSupport > MaximumSamples ||
            !double.IsFinite(MinimumArea) || MinimumArea is < .05 or > 100)
            throw new ArgumentOutOfRangeException(nameof(StructureAnalysisOptions), "Параметры анализа вне допустимого диапазона.");
    }
}

public sealed record StructureAnalysisResult(StructureSurface[] Surfaces, int SampleCount, int SourcePointCount,
    StructureAnalysisOptions Options)
{
    public const string Algorithm = "planes-ransac-v1";
}

/// <summary>Geometry provenance; names, tint and active selection do not affect plane analysis.</summary>
public sealed record StructureInputStamp(Guid ObjectId, int PointCount, int IncludedCount, string PointsHash,
    string MaskHash, ScanTransform Transform, bool IsVisible);

public sealed record StructureAnalysisDocument(int Version, string Algorithm, DateTimeOffset CreatedAtUtc,
    StructureInputStamp[] Inputs, StructureAnalysisResult Result, bool IsStale = false);
