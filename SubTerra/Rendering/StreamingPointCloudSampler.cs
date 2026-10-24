using SubTerra.Models;

namespace SubTerra.Rendering;

/// <summary>Uniformly samples only received points. History queries do not depend on later frames.</summary>
public sealed class StreamingPointCloudSampler
{
    private readonly List<int> _withoutRoof = [];
    private int _pointCount;

    public void Append(IReadOnlyList<ScanPoint> points)
    {
        foreach (var point in points)
        {
            if (point.Z <= .8f) _withoutRoof.Add(_pointCount);
            _pointCount++;
        }
    }

    public int[] Select(int receivedCount, bool cutRoof, int pointLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pointLimit, 1);
        receivedCount = Math.Clamp(receivedCount, 0, _pointCount);
        var eligible = receivedCount;
        if (cutRoof)
        {
            var end = _withoutRoof.BinarySearch(receivedCount);
            eligible = end < 0 ? ~end : end;
        }
        var size = Math.Min(pointLimit, eligible);
        var indices = new int[size];
        for (var i = 0; i < size; i++)
        {
            var rank = size < 2 ? 0 : (int)((long)i * (eligible - 1) / (size - 1));
            indices[i] = cutRoof ? _withoutRoof[rank] : rank;
        }
        return indices;
    }
}
