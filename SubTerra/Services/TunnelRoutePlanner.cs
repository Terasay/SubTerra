using System.IO;
using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>Conservative 2.5D free-space graph bounded by observed lower and upper returns.
/// Unknown columns and obstacles are not traversable. Stacked passages require separate scans.</summary>
public static class TunnelRoutePlanner
{
    private const float CellSize = .4f;
    private readonly record struct Cell(int X, int Y);
    private static readonly (int X, int Y)[] Steps = [(-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (-1, 1), (1, -1), (1, 1)];

    public static Task<InspectionPoint[]> SuggestAsync(ScanProject project, InspectionPoint[]? waypoints = null,
        CancellationToken token = default) => Task.Run(() => Suggest(project, waypoints ?? [], token), token);

    private static InspectionPoint[] Suggest(ScanProject project, InspectionPoint[] waypoints, CancellationToken token)
    {
        if (waypoints.Length > 128 || waypoints.Any(p => !p.IsValid)) throw new ArgumentException("Некорректные опорные точки маршрута.");
        var samples = TunnelWorkspace.SamplePoints(project, 500_000, token);
        var columns = new Dictionary<Cell, List<float>>();
        foreach (var sample in samples)
        {
            token.ThrowIfCancellationRequested(); var p = sample.Position;
            var key = new Cell((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize));
            if (!columns.TryGetValue(key, out var zs)) columns.Add(key, zs = []);
            zs.Add(p.Z);
        }
        var centers = new Dictionary<Cell, Vector3>();
        foreach (var (key, zs) in columns)
        {
            token.ThrowIfCancellationRequested();
            if (zs.Count < 2) continue;
            zs.Sort(); var low = zs[(int)((zs.Count - 1) * .05)]; var high = zs[(int)Math.Ceiling((zs.Count - 1) * .95)];
            if (high - low < 1 || high - low > 15) continue;
            var mid = (low + high) * .5f;
            if (zs.Any(z => Math.Abs(z - mid) < .32f)) continue;
            centers[key] = new((key.X + .5f) * CellSize, (key.Y + .5f) * CellSize, mid);
        }
        if (centers.Count < 8) throw new InvalidDataException("Недостаточно наблюдений пола и свода для безопасного предложения оси. Добавьте точки вручную.");
        IEnumerable<Cell> Neighbors(Cell p)
        {
            foreach (var (dx, dy) in Steps)
            {
                var q = new Cell(p.X + dx, p.Y + dy);
                if (!centers.TryGetValue(q, out var position) || Math.Abs(position.Z - centers[p].Z) > .6f) continue;
                if (dx != 0 && dy != 0 && (!centers.ContainsKey(new(p.X + dx, p.Y)) || !centers.ContainsKey(new(p.X, p.Y + dy)))) continue;
                yield return q;
            }
        }
        // Distance from observed free-space boundary favours the middle of the passage.
        var clearance = new Dictionary<Cell, int>(); var wave = new Queue<Cell>();
        foreach (var p in centers.Keys)
            if (Neighbors(p).Count() < 8) { clearance[p] = 1; wave.Enqueue(p); }
        while (wave.TryDequeue(out var p))
        {
            token.ThrowIfCancellationRequested();
            foreach (var q in Neighbors(p)) if (clearance.TryAdd(q, clearance[p] + 1)) wave.Enqueue(q);
        }
        Cell Snap(InspectionPoint point)
        {
            var nearest = centers.MinBy(p => Vector3.DistanceSquared(p.Value, point.Position));
            if (Vector3.Distance(nearest.Value, point.Position) > 1.5f)
                throw new InvalidDataException("Опорная точка слишком далеко от наблюдаемого свободного прохода (более 1,5 м).");
            return nearest.Key;
        }
        (Cell End, Dictionary<Cell, Cell> Previous) Search(Cell start, Cell? target = null)
        {
            var queue = new PriorityQueue<Cell, double>(); queue.Enqueue(start, 0);
            var distance = new Dictionary<Cell, double> { [start] = 0 }; var previous = new Dictionary<Cell, Cell>();
            var furthest = start; var greatest = 0d;
            while (queue.TryDequeue(out var here, out var cost))
            {
                token.ThrowIfCancellationRequested();
                if (cost > distance[here]) continue;
                if (target == here) return (here, previous);
                if (cost > greatest) { greatest = cost; furthest = here; }
                foreach (var next in Neighbors(here))
                {
                    var candidate = cost + Vector3.Distance(centers[here], centers[next]) * (1 + 3d / clearance.GetValueOrDefault(next, 1));
                    if (distance.TryGetValue(next, out var old) && candidate >= old) continue;
                    distance[next] = candidate; previous[next] = here; queue.Enqueue(next, candidate);
                }
            }
            if (target is not null) throw new InvalidDataException("Опорные точки находятся в несвязанных проходах. Пропуски и стены не пересекаются автоматически.");
            return (furthest, previous);
        }
        var anchors = waypoints.Select(Snap).ToList();
        if (anchors.Count == 0)
        {
            // Pick the largest connected component; small isolated cavities must not win by coordinate order.
            var seen = new HashSet<Cell>(); List<Cell> largest = [];
            foreach (var seed in centers.Keys)
            {
                if (!seen.Add(seed)) continue;
                var component = new List<Cell>(); var pending = new Queue<Cell>(); pending.Enqueue(seed);
                while (pending.TryDequeue(out var current))
                {
                    token.ThrowIfCancellationRequested(); component.Add(current);
                    foreach (var next in Neighbors(current)) if (seen.Add(next)) pending.Enqueue(next);
                }
                if (component.Count > largest.Count) largest = component;
            }
            anchors.Add(Search(largest.MaxBy(p => clearance.GetValueOrDefault(p))).End);
        }
        if (anchors.Count == 1) anchors.Add(Search(anchors[0]).End);
        var route = new List<Cell> { anchors[0] };
        for (var i = 1; i < anchors.Count; i++)
        {
            var (end, previous) = Search(anchors[i - 1], anchors[i]); var leg = new List<Cell> { end };
            while (leg[^1] != anchors[i - 1]) leg.Add(previous[leg[^1]]);
            leg.Reverse(); route.AddRange(leg.Skip(1));
        }
        // Remove only collinear nodes: unconstrained smoothing could cut a corner through rock.
        var axis = new List<InspectionPoint>();
        for (var i = 0; i < route.Count; i++)
        {
            var p = centers[route[i]];
            if (i > 0 && i < route.Count - 1)
            {
                var a = Vector3.Normalize(p - centers[route[i - 1]]); var b = Vector3.Normalize(centers[route[i + 1]] - p);
                if (Vector3.DistanceSquared(a, b) < .0001f) continue;
            }
            axis.Add(InspectionPoint.From(p));
        }
        if (axis.Count is < 2 or > 128) throw new InvalidDataException("Предложение оси слишком короткое или сложное. Ограничьте участок опорными точками.");
        TunnelAnalyzer.Create(axis.ToArray());
        return axis.ToArray();
    }
}
