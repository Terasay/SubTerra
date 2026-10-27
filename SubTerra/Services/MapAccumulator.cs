using System.IO;
using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>Owns committed frames independently of playback and display quality. Used on one consumer thread.</summary>
public sealed class MapAccumulator
{
    private readonly List<ScanFrame> _frames = [];
    public string ScenarioId { get; }
    public ScanSettings Settings { get; }
    public int SceneVersion { get; }
    public int FrameCount => _frames.Count;
    public int PointCount { get; private set; }

    public MapAccumulator(string scenarioId, ScanSettings settings, int sceneVersion = SceneVersions.Current)
    {
        settings.Validate();
        if (!SceneVersions.IsSupported(sceneVersion)) throw new ArgumentOutOfRangeException(nameof(sceneVersion));
        SceneVersion = sceneVersion;
        ScenarioId = scenarioId;
        Settings = settings;
    }

    public void Append(ScanFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (FrameCount >= Settings.FrameCount || frame.Points is null || frame.Points.Length > Settings.RaysPerFrame ||
            !float.IsFinite(frame.Progress) || frame.Progress < Settings.StartProgress || frame.Progress > 1 ||
            !Finite(frame.Pose.Position) || !float.IsFinite(frame.Pose.Orientation.LengthSquared()) ||
            frame.Pose.Orientation.LengthSquared() is < .9f or > 1.1f ||
            (_frames.Count > 0 && (frame.Progress <= _frames[^1].Progress || frame.Timestamp <= _frames[^1].Timestamp)))
            throw new InvalidDataException("Источник передал некорректный или повторный кадр.");
        foreach (var point in frame.Points)
            if (!Finite(point.Position) || !float.IsFinite(point.Intensity) || point.Intensity is < 0 or > 1)
                throw new InvalidDataException("Источник передал некорректную точку.");
        // The source transfers ownership of the frame and its points to this map.
        _frames.Add(frame);
        PointCount += frame.Points.Length;
    }

    public ScanSession Snapshot(ScannerAdjustment[]? adjustments = null) => new()
        { ScenarioId = ScenarioId, SceneVersion = SceneVersion, Settings = Settings, Frames = _frames.ToArray(), Adjustments = adjustments ?? [] };
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
