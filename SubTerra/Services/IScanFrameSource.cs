using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>A pull stream: the consumer requests the next frame, so no unbounded queue is needed.</summary>
/// <remarks>Frames use metres, Z up, intensity in [0,1] and points in scene coordinates.
/// Each yield transfers ownership of the point array to the consumer. Timestamps must increase
/// at millisecond precision (the saved session format) and frame progress must increase through (0,1].</remarks>
public interface IScanFrameSource
{
    IAsyncEnumerable<ScanFrame> ReadFramesAsync(ScanRequest request, CancellationToken cancellationToken);
}

public sealed record ScanRequest(string ScenarioId, ScanSettings Settings, int FirstFrame = 0,
    DateTimeOffset? FirstTimestamp = null, IReadOnlyList<ScannerAdjustment>? Adjustments = null,
    int SceneVersion = SceneVersions.Current);
