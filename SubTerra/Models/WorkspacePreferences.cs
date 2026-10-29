namespace SubTerra.Models;

public sealed record WorkspacePreferences
{
    public double MouseSensitivity { get; init; } = 1;
    public bool InvertMouseY { get; init; }
    public double WalkSpeed { get; init; } = 2.5;
    public double FlightSpeed { get; init; } = 6;
    public double FastMultiplier { get; init; } = 3;
    public bool LevelFlight { get; init; } = true;
    public int DisplayPointLimit { get; init; } = 1_000_000;
    public bool AdaptiveQuality { get; init; } = true;
    public bool AutoSaveEnabled { get; init; } = true;
    public string[] RecentFiles { get; init; } = [];

    public WorkspacePreferences Normalize() => this with
    {
        MouseSensitivity = Bounded(MouseSensitivity, .2, 3, 1),
        WalkSpeed = Bounded(WalkSpeed, .5, 8, 2.5),
        FlightSpeed = Bounded(FlightSpeed, .5, 40, 6),
        FastMultiplier = Bounded(FastMultiplier, 1, 5, 3),
        DisplayPointLimit = PointCloudQuality.All.Any(q => q.PointLimit == DisplayPointLimit) ? DisplayPointLimit : 1_000_000,
        RecentFiles = (RecentFiles ?? []).Where(path => !string.IsNullOrWhiteSpace(path) && path.Length <= 1024)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray()
    };
    private static double Bounded(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed record RecentSession(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
}
