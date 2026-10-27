using System.Numerics;
using System.Runtime.CompilerServices;
using SubTerra.Models;
using SubTerra.Services;

namespace SubTerra.Simulation;

public interface IScanSource : IScanFrameSource
{
    Task<ScanDataset> GenerateAsync(ScenarioDefinition scenario, ScanSettings settings,
        IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Produces reproducible measurements from actual ray/triangle intersections.</summary>
public sealed class SimulatedScanSource : IScanSource
{
    public Task<ScanDataset> GenerateAsync(ScenarioDefinition scenario, ScanSettings settings,
        IProgress<double>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        settings.Validate();
        var scene = CaveFactory.Create(scenario);
        var index = new TriangleIndex(scene.Vertices, scene.Indices);
        var frames = new ScanFrame[settings.FrameCount];
        var generator = new FrameGenerator(scenario.Id, settings, index, DateTimeOffset.UtcNow);
        for (var f = 0; f < frames.Length; f++)
        {
            frames[f] = generator.Next(f, cancellationToken);
            if (f % 6 == 0 || f == frames.Length - 1) progress?.Report((f + 1d) / frames.Length * 100);
        }
        return new ScanDataset(scene, new ScanSession { ScenarioId = scenario.Id, Settings = settings, Frames = frames });
    }, cancellationToken);

    public async IAsyncEnumerable<ScanFrame> ReadFramesAsync(ScanRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request.Settings.Validate();
        var adjustments = request.Adjustments ?? [];
        ScannerPlan.Validate(adjustments, request.Settings);
        ArgumentOutOfRangeException.ThrowIfNegative(request.FirstFrame);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.FirstFrame, request.Settings.FrameCount);
        var scenario = ScenarioDefinition.All.First(s => s.Id == request.ScenarioId);
        var generator = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scene = CaveFactory.Create(scenario, request.SceneVersion);
            var index = new TriangleIndex(scene.Vertices, scene.Indices);
            var value = new FrameGenerator(scenario.Id, request.Settings, index,
                request.FirstTimestamp ?? DateTimeOffset.UtcNow, adjustments);
            // Replaying RNG state against the recorded scene version preserves earlier measurements.
            // Previously committed frames are neither emitted nor changed.
            for (var f = 0; f < request.FirstFrame; f++) value.Next(f, cancellationToken);
            return value;
        }, cancellationToken).ConfigureAwait(false);
        for (var f = request.FirstFrame; f < request.Settings.FrameCount; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameIndex = f;
            yield return await Task.Run(() => generator.Next(frameIndex, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class FrameGenerator(string scenarioId, ScanSettings settings, TriangleIndex index, DateTimeOffset start,
        IReadOnlyList<ScannerAdjustment>? adjustments = null)
    {
        private readonly Random _random = new(settings.Seed);
        public ScanFrame Next(int f, CancellationToken cancellationToken)
        {
            var t = ScannerPlan.PositionAt(settings, adjustments ?? [], f);
            var adjustment = ScannerPlan.Active(adjustments ?? [], f);
            var pose = ScannerPlan.Pose(scenarioId, t, adjustment);
            var (forward, right, up) = CaveFactory.Basis(scenarioId, t);
            if (adjustment is not null)
            {
                forward = Vector3.Transform(Vector3.UnitY, pose.Orientation);
                right = Vector3.Transform(Vector3.UnitX, pose.Orientation);
                up = Vector3.Transform(Vector3.UnitZ, pose.Orientation);
            }
            var points = new List<ScanPoint>(settings.RaysPerFrame);
            for (var r = 0; r < settings.RaysPerFrame; r++)
            {
                if (r % 32 == 0) cancellationToken.ThrowIfCancellationRequested();
                var angle = 2 * MathF.PI * (r + (float)_random.NextDouble()) / settings.RaysPerFrame;
                var axial = ((float)_random.NextDouble() - .5f) * 1.8f;
                var direction = Vector3.Normalize(right * MathF.Cos(angle) + up * MathF.Sin(angle) + forward * axial);
                if (!index.Cast(pose.Position, direction, settings.RangeMeters, out var hit)) continue;
                hit += direction * (((float)_random.NextDouble() - .5f) * .018f);
                points.Add(new(hit.X, hit.Y, hit.Z, .65f + .35f * (float)_random.NextDouble()));
            }
            var routeProgress = settings.StartProgress + (1 - settings.StartProgress) * (f + 1f) / settings.FrameCount;
            return new(start.AddMilliseconds(f * settings.FrameIntervalMilliseconds), pose, routeProgress, points.ToArray());
        }
    }
}
