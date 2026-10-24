using SubTerra.Models;

namespace SubTerra.Rendering;

/// <summary>
/// Builds a stable, bounded preview of a prerecorded session.
/// The same source indices are used as the timeline advances; original measurements are never modified.
/// </summary>
public sealed class PointCloudSampler
{
    private readonly int _pointCount;
    private readonly int[] _withoutRoof;
    private readonly Dictionary<(bool CutRoof, int Limit), int[]> _levels = [];

    public PointCloudSampler(IReadOnlyList<ScanPoint> points)
    {
        _pointCount = points.Count;
        var visible = new List<int>();
        for (var i = 0; i < points.Count; i++)
            if (points[i].Z <= .8f) visible.Add(i);
        _withoutRoof = visible.ToArray();
    }

    public ReadOnlyMemory<int> Select(int receivedCount, bool cutRoof, int pointLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pointLimit, 1);
        receivedCount = Math.Clamp(receivedCount, 0, _pointCount);
        var eligible = cutRoof ? _withoutRoof.Length : _pointCount;
        var size = Math.Min(pointLimit, eligible);
        var key = (cutRoof, size);
        if (!_levels.TryGetValue(key, out var indices))
        {
            indices = new int[size];
            for (var i = 0; i < size; i++)
            {
                var rank = size < 2 ? 0 : (int)((long)i * (eligible - 1) / (size - 1));
                indices[i] = cutRoof ? _withoutRoof[rank] : rank;
            }
            _levels.Add(key, indices);
        }
        // Lower bound: samples belonging to future frames must never be displayed.
        var end = Array.BinarySearch(indices, receivedCount);
        if (end < 0) end = ~end;
        return indices.AsMemory(0, end);
    }
}
