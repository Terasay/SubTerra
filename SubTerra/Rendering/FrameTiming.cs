using System.Diagnostics;

namespace SubTerra.Rendering;

/// <summary>Rendered-frame cadence, not an estimate of GPU execution time.</summary>
public sealed class FrameTiming
{
    private readonly Queue<double> _intervals = new();
    private readonly object _gate = new();
    private long _last;
    public void Frame(long timestamp)
    {
        lock (_gate)
        {
            if (_last > 0)
            {
                var milliseconds = (timestamp - _last) * 1000d / Stopwatch.Frequency;
                if (milliseconds is > 0 and < 500) { _intervals.Enqueue(milliseconds); if (_intervals.Count > 120) _intervals.Dequeue(); }
                else _intervals.Clear();
            }
            _last = timestamp;
        }
    }
    public (double Fps, double FrameMilliseconds)? Snapshot(long timestamp)
    {
        lock (_gate)
        {
            if (_last == 0 || _intervals.Count == 0 || (timestamp - _last) / (double)Stopwatch.Frequency > 1) return null;
            var mean = _intervals.Average();
            return (1000 / mean, mean);
        }
    }
}
