using System.Numerics;

namespace SubTerra.Models;

public enum StructureSurfaceKind { Unknown, Floor, Wall, Ceiling }
public readonly record struct SourcePointReference(Guid ObjectId, int PointIndex);
public readonly record struct StructureSample(SourcePointReference Source, Vector3 Position);

public sealed record StructureSurface(int Id, StructureSurfaceKind Kind, Vector3 Normal, double Offset,
    Vector3 Center, Vector3[] Boundary, double AreaSquareMeters, double RmsMeters, StructureSample[] Samples)
{
    public string KindText => Kind switch
    {
        StructureSurfaceKind.Floor => "Кандидат пола", StructureSurfaceKind.Ceiling => "Кандидат потолка",
        StructureSurfaceKind.Wall => "Кандидат стены", _ => "Не определено"
    };
    public string Title => $"{Id:00} · {KindText}";
    public string Details => $"≈ {AreaSquareMeters:F1} м² · RMS {RmsMeters * 100:F1} см · {Samples.Length:N0} точек выборки";
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
