using System.Numerics;

namespace SubTerra.Models;

public enum StructureSurfaceKind { Unknown, Floor, Wall, Ceiling }
public enum StructureReviewState { Proposed, Confirmed, Rejected }
public readonly record struct SourcePointReference(Guid ObjectId, int PointIndex);
public readonly record struct StructureSample(SourcePointReference Source, Vector3 Position);

public sealed record StructureSurface(int Id, StructureSurfaceKind Kind, Vector3 Normal, double Offset,
    Vector3 Center, Vector3[] Boundary, double AreaSquareMeters, double RmsMeters, StructureSample[] Samples,
    StructureReviewState Review = StructureReviewState.Proposed, StructureSurfaceKind? SuggestedKind = null, double? NeuralScore = null)
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
    public string Details => $"≈ {AreaSquareMeters:F1} м² · разброс {RmsMeters * 100:F1} см · {Samples.Length:N0} точек выборки" +
        (NeuralScore is { } score ? $" · MLP {score:P0} (оценка модели)" : "");
}

public sealed record StructureAnalysisOptions(double DistanceTolerance = .025, int MaximumSamples = 30_000,
    int Iterations = 192, int MaximumPlanes = 12, int MinimumSupport = 64, double MinimumArea = .5, bool UseNeuralClasses = false)
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
    public const string NeuralAlgorithm = "planes-ransac-v1+room-mlp-synthetic-v1";
}

/// <summary>Geometry provenance; names, tint and active selection do not affect plane analysis.</summary>
public sealed record StructureInputStamp(Guid ObjectId, int PointCount, int IncludedCount, string PointsHash,
    string MaskHash, ScanTransform Transform, bool IsVisible);

public sealed record StructureAnalysisDocument(int Version, string Algorithm, DateTimeOffset CreatedAtUtc,
    StructureInputStamp[] Inputs, StructureAnalysisResult Result, bool IsStale = false, RoomPlan? Room = null,
    StructureObject[]? Objects = null, ObjectGroupingOptions? ObjectOptions = null, string? ObjectSourceSignature = null,
    SavedRoom[]? Rooms = null, Guid ActiveRoomId = default)
{
    public StructureAnalysisDocument NormalizeVersion() => this with
    {
        Version = Rooms is not null ? 5 : Objects is not null ? 4 : Room is null ? 1 : Room.IsManuallyEdited ? 3 : 2,
        Rooms = Rooms?.Select(r => r.Id == ActiveRoomId && Room is not null ? r with { Plan = Room } : r).ToArray()
    };
}

public sealed record ObjectGroupingOptions(double CellSize = .15, int MinimumPoints = 20, bool ExcludeConfirmedPlanes = true)
{
    public void Validate()
    {
        if (!double.IsFinite(CellSize) || CellSize is < .000001 or > 100 || MinimumPoints is < 3 or > 30_000)
            throw new ArgumentOutOfRangeException(nameof(ObjectGroupingOptions), "Ячейка: 0,000001–100 м, минимум точек: 3–30000.");
    }
}

public sealed record StructureObject(int Id, string Label, uint Color, StructureSample[] Samples,
    Vector3 Minimum, Vector3 Maximum, StructureReviewState Review = StructureReviewState.Proposed)
{
    public string ColorHex => $"#{Color:X6}";
    public string Details => $"{Samples.Length:N0} точек выборки · {(Maximum.X - Minimum.X):F2} × {(Maximum.Y - Minimum.Y):F2} × {(Maximum.Z - Minimum.Z):F2} м";
    public string StatusText => Review switch { StructureReviewState.Confirmed => "Проверено оператором", StructureReviewState.Rejected => "Отклонено", _ => "Геометрическая группа" };
}
