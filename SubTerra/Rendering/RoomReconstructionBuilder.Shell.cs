using System.IO;
using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Rendering;

public static partial class RoomReconstructionBuilder
{
    private sealed record RoomShell(Vector3[] Contour, float Floor, float Ceiling)
    {
        private Vector2[] Boundary { get; } = Contour.Select(v => new Vector2(v.X, v.Y)).ToArray();
        public bool Contains(Vector3 p, double margin) => p.Z >= Floor - margin && p.Z <= Ceiling + margin &&
            Inside(Boundary, new(p.X, p.Y), margin);
    }

    private static RoomShell? CreateShell(Patch[] patches, RoomPlan? room)
    {
        if (room is { HasHeight: true }) return new(room.FloorContour, room.FloorContour.Min(p => p.Z), room.CeilingContour.Max(p => p.Z));
        var floor = patches.Where(p => p.Kind == StructureSurfaceKind.Floor).MaxBy(p => p.Sources.Sum(s => s.AreaSquareMeters));
        var ceiling = patches.Where(p => p.Kind == StructureSurfaceKind.Ceiling).MaxBy(p => p.Sources.Sum(s => s.AreaSquareMeters));
        if (floor is null || ceiling is null || Math.Abs(floor.Normal.Z) < .996f || Math.Abs(ceiling.Normal.Z) < .996f ||
            ceiling.Center.Z - floor.Center.Z is < 1.8f or > 15) return null;
        var walls = new List<(Vector2 N, double D)>(); var center = new Vector2(floor.Center.X, floor.Center.Y);
        foreach (var patch in patches.Where(p => p.Kind == StructureSurfaceKind.Wall && Math.Abs(p.Normal.Z) < .087f))
        {
            var n = Vector2.Normalize(new(patch.Normal.X, patch.Normal.Y)); var d = Vector2.Dot(n, new(patch.Center.X, patch.Center.Y));
            var distance = Vector2.Dot(n, center) - d;
            if (Math.Abs(distance) < .3) return null;
            if (distance > 0) { n = -n; d = -d; }
            if (!walls.Any(w => Vector2.Dot(w.N, n) > .996 && Math.Abs(w.D - d) < .16)) walls.Add((n, d));
        }
        if (walls.Count < 3) return null;
        var candidates = new List<Vector2>();
        for (var i = 0; i < walls.Count; i++) for (var j = i + 1; j < walls.Count; j++)
        {
            var a = walls[i]; var b = walls[j]; var determinant = a.N.X * (double)b.N.Y - a.N.Y * (double)b.N.X;
            if (Math.Abs(determinant) < 1e-5) continue;
            var p = new Vector2((float)((a.D * b.N.Y - a.N.Y * b.D) / determinant), (float)((a.N.X * b.D - a.D * b.N.X) / determinant));
            if (walls.All(w => Vector2.Dot(w.N, p) <= w.D + .025) && Vector2.Distance(p, center) < 50 && !candidates.Any(v => Vector2.DistanceSquared(v, p) < .0025)) candidates.Add(p);
        }
        if (candidates.Count < 3) return null;
        var polygon = candidates.OrderBy(p => Math.Atan2(p.Y - center.Y, p.X - center.X)).ToArray();
        for (var i = 0; i < polygon.Length; i++)
        {
            var mid = (polygon[i] + polygon[(i + 1) % polygon.Length]) * .5f;
            if (!walls.Any(w => Math.Abs(Vector2.Dot(w.N, mid) - w.D) < .05)) return null;
        }
        double area = 0;
        for (var i = 0; i < polygon.Length; i++) area += polygon[i].X * (double)polygon[(i + 1) % polygon.Length].Y - polygon[i].Y * (double)polygon[(i + 1) % polygon.Length].X;
        area = Math.Abs(area) / 2;
        // RANSAC fragments can overlap almost entirely; summing their areas rejects an otherwise valid room.
        var observedArea = Services.PlanePatch.Boundary(new(floor.Normal, -Vector3.Dot(floor.Normal, floor.Center), floor.Center),
            floor.Sources.SelectMany(s => s.Boundary).ToArray()).Area;
        if (area < 2 || area > Math.Min(1500, observedArea * 1.8) || area < observedArea * .7) return null;
        var samples = floor.Sources.SelectMany(s => s.Samples).ToArray();
        if (samples.Count(s => Inside(polygon, new(s.Position.X, s.Position.Y), .1)) < samples.Length * .9) return null;
        return new(polygon.Select(p => new Vector3(p.X, p.Y, floor.Center.Z)).ToArray(), floor.Center.Z, ceiling.Center.Z);
    }

    private static void CompleteSupport(Patch patch, double step, Func<Vector3, bool> excluded, ref int total, ref int filled, CancellationToken token)
    {
        if (patch.Cells.Count == 0) return;
        var boundary = patch.Boundaries.SelectMany(p => p).ToArray();
        var x0 = (int)Math.Floor(boundary.Min(p => p.X) / step); var x1 = (int)Math.Ceiling(boundary.Max(p => p.X) / step);
        var y0 = (int)Math.Floor(boundary.Min(p => p.Y) / step); var y1 = (int)Math.Ceiling(boundary.Max(p => p.Y) / step);
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > MaximumGridArea) throw new InvalidDataException("Для достройки контура выберите меньшее помещение.");
        var pending = new HashSet<(int X, int Y)>();
        for (var y = y0; y <= y1; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = x0; x <= x1; x++)
            {
                var p = patch.World(x, y, step);
                if (!patch.Cells.ContainsKey((x, y)) && patch.Contains(patch.Local(p)) && !excluded(p)) pending.Add((x, y));
            }
        }
        if (total + pending.Count > MaximumCells) throw new InvalidDataException("Превышен размер реконструкции контура комнаты.");
        var queue = new Queue<(int X, int Y)>();
        foreach (var p in pending) if (Neighbors.Any(d => patch.Cells.ContainsKey((p.X + d.X, p.Y + d.Y)))) queue.Enqueue(p);
        while (queue.TryDequeue(out var p))
        {
            if ((filled & 1023) == 0) token.ThrowIfCancellationRequested();
            if (!pending.Remove(p)) continue;
            var cell = new Cell { Inferred = true };
            foreach (var d in Neighbors) if (patch.Cells.TryGetValue((p.X + d.X, p.Y + d.Y), out var neighbor)) cell.Add(neighbor.Color);
            if (cell.Count == 0) continue;
            patch.Cells.Add(p, cell); total++; filled++;
            foreach (var d in Neighbors) if (pending.Contains((p.X + d.X, p.Y + d.Y))) queue.Enqueue((p.X + d.X, p.Y + d.Y));
        }
    }

    private static bool IsWindow(Patch patch, double step, int x0, int x1, int y0, int y1, int cells, RoomShell shell)
    {
        var width = (x1 - x0 + 1) * step; var height = (y1 - y0 + 1) * step;
        var a = patch.World(x0, y0, step); var b = patch.World(x1, y1, step);
        return Math.Abs(patch.U.Z) < .01f && width is >= .45 and <= 4.5 && height is >= .4 and <= 2.5 &&
            Math.Min(a.Z, b.Z) > shell.Floor + .45 && Math.Max(a.Z, b.Z) < shell.Ceiling - .12 &&
            cells >= (x1 - x0 + 1) * (y1 - y0 + 1) * .55;
    }
}
