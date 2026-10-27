using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>A pull stream: the consumer requests the next frame, so no unbounded queue is needed.</summary>
public interface IScanFrameSource
{
    IAsyncEnumerable<ScanFrame> ReadFramesAsync(ScanRequest request, CancellationToken cancellationToken);
}

public sealed record ScanRequest(string ScenarioId, ScanSettings Settings, int FirstFrame = 0,
    DateTimeOffset? FirstTimestamp = null, IReadOnlyList<ScannerAdjustment>? Adjustments = null,
    int SceneVersion = SceneVersions.Current);
