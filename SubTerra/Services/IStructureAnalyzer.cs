using SubTerra.Models;

namespace SubTerra.Services;

public interface IStructureAnalyzer
{
    Task<StructureAnalysisResult> AnalyzeAsync(ScanProject project, StructureAnalysisOptions options,
        IProgress<double>? progress, CancellationToken cancellationToken);
}
