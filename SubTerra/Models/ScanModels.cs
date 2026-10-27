using System.Numerics;

namespace SubTerra.Models;

public readonly record struct ScanPoint(float X, float Y, float Z, float Intensity)
{
    public Vector3 Position => new(X, Y, Z);
}

public readonly record struct SensorPose(Vector3 Position, Quaternion Orientation);

public sealed record ScannerAdjustment(int FirstFrame, float RoutePosition, float LateralOffset = 0,
    float HeightOffset = 0, float YawDegrees = 0);
public readonly record struct ScanCoverage(float Start, float End);

public sealed record ScanSettings(float RangeMeters = 20, int RaysPerFrame = 720, int FrameCount = 180,
    int Seed = 42, float StartProgress = 0, int FrameIntervalMilliseconds = 100)
{
    public void Validate()
    {
        if (!float.IsFinite(RangeMeters) || RangeMeters is < 1 or > 100 || RaysPerFrame is < 32 or > 2048 ||
            FrameCount is < 2 or > 360 || !float.IsFinite(StartProgress) || StartProgress is < 0 or > .85f ||
            FrameIntervalMilliseconds is < 25 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(ScanSettings), "Параметры сканирования вне допустимого диапазона.");
    }
}

public sealed record ScanFrame(DateTimeOffset Timestamp, SensorPose Pose, float Progress, ScanPoint[] Points);

public sealed record SceneMarker(string Title, string Description, string Kind, float Progress, Vector3 Position);

public sealed record ScenarioDefinition(string Id, string Name, string Subtitle, string Description)
{
    public override string ToString() => Name;
    public static IReadOnlyList<ScenarioDefinition> All { get; } =
    [
        new("aurora", "Пещера Аврора", "ПЕЩЕРА · ВИРТУАЛЬНАЯ СЦЕНА",
            "Извилистый проход, скальные выступы и просторный центральный зал."),
        new("mine", "Шахтная выработка", "ШАХТА · ВИРТУАЛЬНАЯ СЦЕНА",
            "Протяжённая выработка с арочным профилем и перепадом высоты."),
        new("lava", "Лавовый тоннель", "ТОННЕЛЬ · ВИРТУАЛЬНАЯ СЦЕНА",
            "Плавные изгибы, неоднородные стены и узкий боковой участок.")
    ];
}

public sealed class ScanSession
{
    public int SceneVersion { get; init; } = SceneVersions.Current;
    public required string ScenarioId { get; init; }
    public required ScanSettings Settings { get; init; }
    public required ScanFrame[] Frames { get; init; }
    public ScannerAdjustment[] Adjustments { get; init; } = [];
    public int PointCount => Frames.Sum(frame => frame.Points.Length);
}

public sealed record ScanDataset(CaveScene Scene, ScanSession Session);

public static class SceneVersions
{
    public const int Legacy = 2;
    public const int Current = 3;
    public static bool IsSupported(int version) => version is Legacy or Current;
}

public enum CaveSurfaceKind { Rock, Sediment, DampRock }

public sealed class CaveScene
{
    public int SceneVersion { get; init; } = SceneVersions.Current;
    // One classification per triangle. Legacy scenes fall back to the rock material.
    public CaveSurfaceKind[] SurfaceKinds { get; init; } = [];
    public required ScenarioDefinition Scenario { get; init; }
    public required Vector3[] Vertices { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] TextureCoordinates { get; init; }
    public required int[] Indices { get; init; }
    public required float[] VertexProgress { get; init; }
    public required float[] VertexAngles { get; init; }
    public required Vector3[] Route { get; init; }
    public required SceneMarker[] Markers { get; init; }
    public required float LengthMeters { get; init; }
    public float MinimumHeight => Vertices.Min(p => p.Z);
    public float MaximumHeight => Vertices.Max(p => p.Z);
}
