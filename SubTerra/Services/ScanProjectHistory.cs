using SubTerra.Models;

namespace SubTerra.Services;

public sealed record ScanProjectChange(ScanProject Before, ScanProject After, string Description);
public sealed class ScanProjectHistory
{
    private readonly List<ScanProjectChange> _undo = [], _redo = [];
    public ScanProjectChange? UndoChange => _undo.LastOrDefault();
    public ScanProjectChange? RedoChange => _redo.LastOrDefault();
    public void Clear() { _undo.Clear(); _redo.Clear(); }
    public void Commit(ScanProject before, ScanProject after, string description)
    {
        _redo.Clear(); _undo.Add(new(before, after, description));
        while (_undo.Count > 1 && (_undo.Count > 20 || RetainedBytes(after) > 128L * 1024 * 1024)) _undo.RemoveAt(0);
    }
    public void CompleteUndo() { var change = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); _redo.Add(change); }
    public void CompleteRedo() { var change = _redo[^1]; _redo.RemoveAt(_redo.Count - 1); _undo.Add(change); }
    private long RetainedBytes(ScanProject current)
    {
        var sources = new HashSet<ImportedScan>(current.Objects.Select(o => o.Source), ReferenceEqualityComparer.Instance);
        var masks = new HashSet<ScanPointMask>(current.Objects.Where(o => o.Mask is not null).Select(o => o.Mask!), ReferenceEqualityComparer.Instance);
        long bytes = 0;
        var arrays = new HashSet<Array>(ReferenceEqualityComparer.Instance);
        void IncludeAnalysis(StructureAnalysisDocument? document, bool count)
        {
            if (document is null) return;
            foreach (var surface in document.Result.Surfaces)
            {
                if (arrays.Add(surface.Samples) && count) bytes += surface.Samples.LongLength * 32;
                if (arrays.Add(surface.Boundary) && count) bytes += surface.Boundary.LongLength * 12;
            }
        }
        IncludeAnalysis(current.Structure, false);
        foreach (var project in _undo.SelectMany(c => new[] { c.Before, c.After })) IncludeAnalysis(project.Structure, true);
        foreach (var o in _undo.SelectMany(c => c.Before.Objects.Concat(c.After.Objects)))
        {
            if (sources.Add(o.Source)) bytes += o.Source.Points.LongLength * 16 + o.Source.Colors.LongLength * 4 + o.Source.Triangles.LongLength * 4;
            if (o.Mask is not null && masks.Add(o.Mask)) bytes += o.Mask.Length;
        }
        return bytes;
    }
}
