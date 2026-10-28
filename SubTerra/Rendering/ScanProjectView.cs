using SubTerra.Models;
using SubTerra.Services;

namespace SubTerra.Rendering;

public sealed record ScanProjectView(EvaluatedScanProject Data, ImportedGeometry? Geometry, ImportedGeometry? CloudOnly)
{
    private static readonly SemaphoreSlim PreparationGate = new(1, 1);
    public static async Task<ScanProjectView> PrepareAsync(ScanProject project, CancellationToken token = default)
    {
        var work = Task.Run(async () =>
        {
            await PreparationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var data = ScanProjectEvaluator.Evaluate(project, token);
                var geometry = data.VisibleScan is null ? null : new ImportedGeometry(data.VisibleScan);
                geometry?.PrepareMeshPreviews(token);
                ImportedGeometry? cloudOnly = null;
                if (data.VisibleScan is { } scan && data.CloudOnlyIndices.Length > 0 && scan.HasMesh)
                    cloudOnly = new(new ImportedScan { Metadata = scan.Metadata,
                        Points = data.CloudOnlyIndices.Select(i => scan.Points[i]).ToArray(),
                        Colors = scan.Colors.Length == 0 ? [] : data.CloudOnlyIndices.Select(i => scan.Colors[i]).ToArray() });
                return new ScanProjectView(data, geometry, cloudOnly);
            }
            finally { PreparationGate.Release(); }
        }, token);
        return await work.WaitAsync(token).ConfigureAwait(false);
    }
}
