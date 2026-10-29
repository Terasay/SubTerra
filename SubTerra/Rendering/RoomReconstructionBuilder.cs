using System.IO;
using System.Numerics;
using SubTerra.Models;
using SubTerra.Services;

namespace SubTerra.Rendering;

public sealed record RoomReconstructionOptions(bool AlignSurfaces = true, double FloorHoleSize = 1.6,
    double WallHoleSize = .25, bool KeepObjects = true, bool RemoveIsolatedPoints = true, bool LimitToRoom = false,
    bool CompleteRoomShell = true, bool RepairWindows = true, bool ClipOutsideShell = true)
{
    public void Validate()
    {
        if (!double.IsFinite(FloorHoleSize) || FloorHoleSize is < 0 or > 3 ||
            !double.IsFinite(WallHoleSize) || WallHoleSize is < 0 or > .5)
            throw new ArgumentOutOfRangeException(nameof(FloorHoleSize), "Пропуски пола: 0–3 м; стен: 0–0,5 м.");
    }
}

/// <summary>Bounded room hypotheses from measured planes. Original points, labels and colors are immutable.</summary>
public static partial class RoomReconstructionBuilder
{
    private const int MaximumCells = 450_000, MaximumGridArea = 450_000, MaximumTriangles = 850_000;
    private static readonly (int X, int Y)[] Neighbors = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    public static ImportedScan ExportModel(IndoorSurfacePreview preview, CancellationToken token = default)
    {
        if (preview.ReconstructionOptions is null) throw new InvalidDataException("Сначала постройте реконструкцию.");
        var mesh = preview.Scan; var remaining = preview.RemainingPoints;
        var count = mesh.Points.Length + (remaining?.Points.Length ?? 0);
        if (count > ImportedScan.MaximumPoints) throw new InvalidDataException("Реконструкция превышает лимит экспорта в 5 млн точек.");
        var points = new ScanPoint[count]; var colors = new uint[count];
        for (var i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var source = i < mesh.Points.Length ? mesh : remaining!;
            var index = i < mesh.Points.Length ? i : i - mesh.Points.Length;
            points[i] = source.Points[index]; colors[i] = source.Colors[index];
        }
        var result = new ImportedScan { Metadata = mesh.Metadata with { IsSynthetic = true }, Points = points,
            Colors = colors, Triangles = (int[])mesh.Triangles.Clone() };
        result.Validate(token); return result;
    }
    private sealed class Cell
    {
        public int Count;
        public long Red, Green, Blue;
        public bool Inferred;
        public uint Color => Count == 0 ? 0xB5BCC3u : (uint)(Red / Count << 16 | Green / Count << 8 | Blue / Count);
        public void Add(uint rgb) { Count++; Red += rgb >> 16 & 255; Green += rgb >> 8 & 255; Blue += rgb & 255; }
    }
    private sealed class Patch
    {
        public required StructureSurface[] Sources;
        public required Vector3 Normal, Center, U, V;
        public required Vector2[][] Boundaries;
        public Dictionary<(int X, int Y), Cell> Cells { get; } = [];
        public StructureSurfaceKind Kind => Sources[0].Kind;
        public Vector2 Local(Vector3 p) => new(Vector3.Dot(p - Center, U), Vector3.Dot(p - Center, V));
        public Vector3 World(int x, int y, double step) => Center + U * (float)((x + .5) * step) + V * (float)((y + .5) * step);
        public bool Contains(Vector2 p, double margin = 0) => Boundaries.Any(b => Inside(b, p, margin));
    }

    public static IndoorSurfacePreview Build(ScanProject project, StructureAnalysisDocument document, RoomReconstructionOptions options,
        CancellationToken token = default)
    {
        options.Validate(); project.Validate(token); token.ThrowIfCancellationRequested();
        if (StructureProvenance.Refresh(document, project, token).IsStale)
            throw new InvalidDataException("Сначала обновите анализ поверхностей.");
        var activeRoom = document.Room is not null && RoomPlanner.IsCurrent(document) ? document.Room : null;
        var room = options.LimitToRoom ? activeRoom : null;
        if (options.LimitToRoom && room is not { HasHeight: true })
            throw new InvalidDataException("Для ограничения комнаты нужен актуальный план с известной высотой.");
        var surfaces = document.Result.Surfaces.Where(s => s.Kind != StructureSurfaceKind.Unknown && s.Review != StructureReviewState.Rejected).ToArray();
        if (surfaces.Length == 0) throw new InvalidDataException("Сначала найдите пол, стены или потолок и проверьте классы.");
        var patches = CreatePatches(surfaces, options.AlignSurfaces);
        var shell = options.CompleteRoomShell ? CreateShell(patches, activeRoom) : null;
        if (shell is not null)
            foreach (var patch in new[] { patches.Where(p => p.Kind == StructureSurfaceKind.Floor).MaxBy(p => p.Sources.Sum(s => s.AreaSquareMeters)),
                patches.Where(p => p.Kind == StructureSurfaceKind.Ceiling).MaxBy(p => p.Sources.Sum(s => s.AreaSquareMeters)) }.OfType<Patch>())
                if (Math.Abs(patch.Normal.Z) > .996f) patch.Boundaries = [shell.Contour.Select(p => patch.Local(p)).ToArray()];
        var area = surfaces.Sum(s => s.AreaSquareMeters);
        var visibleCount = project.Objects.Where(o => o.IsVisible).Sum(o => o.PointCount);
        var step = Math.Max(.025, Math.Max(Math.Sqrt(area / 300_000), 1.2 * Math.Sqrt(area / Math.Max(1, visibleCount))));
        var tolerance = Math.Min(.1, Math.Max(.025, document.Result.Options.DistanceTolerance * 2));
        var residual = new List<ScanPoint>(); var residualColors = new List<uint>();
        var cellCount = 0; var outside = 0; var visited = 0;
        bool Excluded(Vector3 p) => room is not null && !RoomTopology.Contains(room, p) || options.ClipOutsideShell && shell is not null && !shell.Contains(p, .12);
        bool Opening(Patch patch, Vector3 p)
        {
            if (activeRoom is not { } current || patch.Kind != StructureSurfaceKind.Wall) return false;
            foreach (var gap in current.Gaps.Where(g => g.IsConfirmedOpening && patch.Sources.Any(s => s.Id == g.WallSurfaceId)))
            {
                var a = current.FloorContour[gap.EdgeIndex]; var b = current.FloorContour[(gap.EdgeIndex + 1) % current.FloorContour.Length];
                var edge = new Vector2(b.X - a.X, b.Y - a.Y); var length = edge.Length();
                var along = Vector2.Dot(new(p.X - a.X, p.Y - a.Y), edge) / (length * length);
                if (along >= gap.Start && along <= gap.End) return true;
            }
            return false;
        }
        foreach (var obj in project.Objects.Where(o => o.IsVisible))
        {
            var matrix = obj.Matrix;
            for (var i = 0; i < obj.Source.Points.Length; i++)
            {
                if ((visited++ & 2047) == 0) token.ThrowIfCancellationRequested();
                if (!obj.Includes(i)) continue;
                var p = Vector3.Transform(obj.Source.Points[i].Position, matrix);
                if (Excluded(p)) { outside++; continue; }
                var rgb = obj.Tint ?? (obj.Source.Colors.Length == 0 ? 0xB5BCC3u : obj.Source.Colors[i]);
                Patch? nearest = null; var distance = tolerance;
                foreach (var patch in patches)
                {
                    var d = patch.Sources.Min(s => Math.Abs(PlanePatch.Distance(s.Normal, s.Offset, p)));
                    if (d >= distance || !patch.Contains(patch.Local(p), step)) continue;
                    nearest = patch; distance = d;
                }
                if (nearest is null || Opening(nearest, p))
                {
                    if (options.KeepObjects) { residual.Add(new(p.X, p.Y, p.Z, obj.Source.Points[i].Intensity)); residualColors.Add(rgb); }
                    continue;
                }
                var local = nearest.Local(p);
                var key = ((int)Math.Floor(local.X / step), (int)Math.Floor(local.Y / step));
                if (!nearest.Cells.TryGetValue(key, out var cell))
                {
                    if (++cellCount > MaximumCells) throw new InvalidDataException("Выберите меньшее помещение: превышен размер реконструкции.");
                    nearest.Cells[key] = cell = new();
                }
                cell.Add(rgb);
            }
        }
        var measured = cellCount; var filled = 0; var skipped = 0; var windows = 0;
        foreach (var patch in patches)
        {
            token.ThrowIfCancellationRequested();
            var diameter = patch.Kind == StructureSurfaceKind.Wall ? options.WallHoleSize : options.FloorHoleSize;
            if (shell is not null && patch.Kind != StructureSurfaceKind.Wall && Math.Abs(patch.Normal.Z) > .996f)
                CompleteSupport(patch, step, p => Excluded(p), ref cellCount, ref filled, token);
            FillHoles(patch, step, diameter, p => Excluded(p) || Opening(patch, p), shell, options.RepairWindows, ref windows, ref cellCount, ref filled, ref skipped, token);
        }
        var removed = options.RemoveIsolatedPoints ? FilterIsolated(residual, residualColors, token) : 0;
        var points = new List<ScanPoint>(); var colors = new List<uint>(); var materials = new List<uint>();
        var classes = new List<uint>(); var completion = new List<uint>(); var triangles = new List<int>(); var floor = new List<int>();
        foreach (var patch in patches)
        {
            token.ThrowIfCancellationRequested();
            var indices = new Dictionary<(int X, int Y), int>();
            foreach (var (key, cell) in patch.Cells.OrderBy(c => c.Key.X).ThenBy(c => c.Key.Y))
            {
                var p = patch.World(key.X, key.Y, step);
                if (Excluded(p) || Opening(patch, p)) continue;
                indices[key] = points.Count; points.Add(new(p.X, p.Y, p.Z, 1)); colors.Add(cell.Color);
                materials.Add(IndoorSurfaceBuilder.Material(patch.Kind, p));
                classes.Add(patch.Kind switch { StructureSurfaceKind.Floor => 0x61E08Cu, StructureSurfaceKind.Wall => 0xF5B84Du, _ => 0x66A6FAu });
                completion.Add(cell.Inferred ? 0xF4B860u : cell.Color);
            }
            foreach (var (key, a) in indices)
            {
                if ((triangles.Count & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!indices.TryGetValue((key.X + 1, key.Y), out var b) || !indices.TryGetValue((key.X, key.Y + 1), out var c)) continue;
                Add(a, b, c);
                if (indices.TryGetValue((key.X + 1, key.Y + 1), out var d)) Add(b, d, c);
            }
            void Add(int a, int b, int c)
            {
                if (triangles.Count / 3 >= MaximumTriangles) throw new InvalidDataException("Слишком много треугольников. Выберите одну комнату.");
                triangles.AddRange([a, b, c]);
                if (patch.Kind == StructureSurfaceKind.Floor) floor.AddRange([a, b, c]);
            }
        }
        if (triangles.Count == 0) throw new InvalidDataException("Не хватает связных наблюдений для реконструкции.");
        var metadata = new ImportMetadata("room-reconstruction.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0) { IsSynthetic = true };
        return new(new ImportedScan { Metadata = metadata, Points = points.ToArray(), Colors = colors.ToArray(), Triangles = triangles.ToArray() },
            materials.ToArray(), classes.ToArray(), floor.ToArray())
        {
            ReconstructionOptions = options, CompletionColors = completion.ToArray(), FilledCells = filled, MeasuredCells = measured,
            LeveledSurfaces = options.AlignSurfaces ? patches.Count(p => p.Sources.Any(s => Vector3.DistanceSquared(s.Normal, p.Normal) > 1e-10f)) : 0,
            SuppressedPoints = removed + outside, SkippedHoleRegions = skipped,
            HasRoomShell = shell is not null, ReconstructedWindows = windows,
            RemainingPoints = residual.Count == 0 ? null : new ImportedScan { Metadata = metadata, Points = residual.ToArray(), Colors = residualColors.ToArray() }
        };
    }

    private static Patch[] CreatePatches(StructureSurface[] surfaces, bool align)
    {
        var groups = new List<List<StructureSurface>>();
        static Vector3 AlignedNormal(StructureSurface s) => s.Kind != StructureSurfaceKind.Wall && Math.Abs(s.Normal.Z) > .996f ? Vector3.UnitZ :
            s.Kind == StructureSurfaceKind.Wall && Math.Abs(s.Normal.Z) < .087f ? Vector3.Normalize(new(s.Normal.X, s.Normal.Y, 0)) : s.Normal;
        foreach (var s in surfaces)
        {
            var group = align ? groups.FirstOrDefault(g => g[0].Kind == s.Kind && Math.Abs(Vector3.Dot(AlignedNormal(g[0]), AlignedNormal(s))) > .996f &&
                (s.Kind == StructureSurfaceKind.Wall ? Math.Abs(Vector3.Dot(AlignedNormal(g[0]), s.Center - g[0].Center)) < .08 : Math.Abs(g[0].Center.Z - s.Center.Z) < .12)) : null;
            if (group is null) groups.Add([s]); else group.Add(s);
        }
        return groups.Select(group =>
        {
            var source = group[0]; var normal = source.Normal; var center = source.Center;
            if (align)
            {
                if (source.Kind != StructureSurfaceKind.Wall && Math.Abs(normal.Z) > .996f)
                {
                    normal = Vector3.UnitZ;
                    center.Z = Median(group.SelectMany(s => s.Samples).Select(s => s.Position.Z).ToArray());
                }
                else if (source.Kind == StructureSurfaceKind.Wall && Math.Abs(normal.Z) < .087f)
                {
                    normal = Vector3.Normalize(new(normal.X, normal.Y, 0));
                    var offset = Median(group.SelectMany(s => s.Samples).Select(s => Vector3.Dot(normal, s.Position)).ToArray());
                    center += normal * (offset - Vector3.Dot(normal, center));
                }
            }
            var u = Vector3.Normalize(Vector3.Cross(normal, Math.Abs(normal.Z) < .9f ? Vector3.UnitZ : Vector3.UnitX));
            var v = Vector3.Cross(normal, u);
            return new Patch { Sources = group.ToArray(), Normal = normal, Center = center, U = u, V = v,
                Boundaries = group.Select(s => s.Boundary.Select(p => new Vector2(Vector3.Dot(p - center, u), Vector3.Dot(p - center, v))).ToArray()).ToArray() };
        }).ToArray();
    }
    private static float Median(float[] values) { Array.Sort(values); return values.Length == 0 ? 0 : values[values.Length / 2]; }
    private static bool Inside(Vector2[] polygon, Vector2 point, double margin)
    {
        if (polygon.Length < 3) return false;
        var positive = false; var negative = false;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var edge = polygon[(i + 1) % polygon.Length] - a;
            var cross = edge.X * (double)(point.Y - a.Y) - edge.Y * (double)(point.X - a.X);
            var epsilon = margin * edge.Length() + 1e-7;
            positive |= cross > epsilon; negative |= cross < -epsilon;
            if (positive && negative) return false;
        }
        return true;
    }

    private static void FillHoles(Patch patch, double step, double diameter, Func<Vector3, bool> excluded,
        RoomShell? shell, bool repairWindows, ref int windows, ref int total, ref int filled, ref int skipped, CancellationToken token)
    {
        if (diameter <= 0 || patch.Cells.Count < 4) return;
        var x0 = patch.Cells.Keys.Min(p => p.X) - 1; var y0 = patch.Cells.Keys.Min(p => p.Y) - 1;
        var width = (long)patch.Cells.Keys.Max(p => p.X) - x0 + 2; var height = (long)patch.Cells.Keys.Max(p => p.Y) - y0 + 2;
        if (width * height > MaximumGridArea) { skipped++; return; }
        var w = (int)width; var h = (int)height; var visited = new bool[w * h];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var slot = y * w + x;
            if ((slot & 1023) == 0) token.ThrowIfCancellationRequested();
            var key = (x + x0, y + y0);
            if (visited[slot] || patch.Cells.ContainsKey(key)) continue;
            var queue = new Queue<(int X, int Y)>(); var region = new List<(int X, int Y)>(); var blocked = false;
            visited[slot] = true; queue.Enqueue((x, y)); var minX = x; var maxX = x; var minY = y; var maxY = y;
            while (queue.TryDequeue(out var p))
            {
                if ((region.Count & 1023) == 0) token.ThrowIfCancellationRequested();
                var worldKey = (p.X + x0, p.Y + y0); region.Add(worldKey);
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                blocked |= p.X == 0 || p.Y == 0 || p.X == w - 1 || p.Y == h - 1;
                if (!blocked) blocked = !patch.Contains(new((float)((worldKey.Item1 + .5) * step), (float)((worldKey.Item2 + .5) * step))) || excluded(patch.World(worldKey.Item1, worldKey.Item2, step));
                foreach (var d in Neighbors)
                {
                    var nx = p.X + d.X; var ny = p.Y + d.Y;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || visited[ny * w + nx] || patch.Cells.ContainsKey((nx + x0, ny + y0))) continue;
                    visited[ny * w + nx] = true; queue.Enqueue((nx, ny));
                }
            }
            var window = !blocked && repairWindows && shell is not null && patch.Kind == StructureSurfaceKind.Wall &&
                IsWindow(patch, step, minX + x0, maxX + x0, minY + y0, maxY + y0, region.Count, shell);
            if (blocked || !window && Math.Max(maxX - minX + 1, maxY - minY + 1) * step > diameter) continue;
            if (total + region.Count > MaximumCells) { skipped++; continue; }
            if (window)
            {
                windows++;
                foreach (var p in region)
                {
                    var frame = p.X - x0 - minX < 2 || maxX - (p.X - x0) < 2 || p.Y - y0 - minY < 2 || maxY - (p.Y - y0) < 2 ||
                        Math.Abs(p.X - x0 - (minX + maxX) * .5) < 1;
                    var cell = new Cell { Inferred = true }; cell.Add(frame ? 0xCAD5DCu : 0x52758Cu); patch.Cells.Add(p, cell); total++; filled++;
                }
                continue;
            }
            // Multi-source growth copies nearby observed color; it never invents photographic detail.
            var pending = region.ToHashSet(); var front = new Queue<(int X, int Y)>();
            foreach (var p in region) if (Neighbors.Any(d => patch.Cells.ContainsKey((p.X + d.X, p.Y + d.Y)))) front.Enqueue(p);
            while (front.TryDequeue(out var p))
            {
                if (!pending.Remove(p)) continue;
                var color = new Cell { Inferred = true };
                foreach (var d in Neighbors) if (patch.Cells.TryGetValue((p.X + d.X, p.Y + d.Y), out var neighbor)) color.Add(neighbor.Color);
                if (color.Count == 0) continue;
                patch.Cells.Add(p, color); total++; filled++;
                foreach (var d in Neighbors) if (pending.Contains((p.X + d.X, p.Y + d.Y))) front.Enqueue((p.X + d.X, p.Y + d.Y));
            }
        }
    }

    private static int FilterIsolated(List<ScanPoint> points, List<uint> colors, CancellationToken token)
    {
        static (int X, int Y, int Z) Key(ScanPoint p) => ((int)Math.Floor(p.X / .08), (int)Math.Floor(p.Y / .08), (int)Math.Floor(p.Z / .08));
        var counts = new Dictionary<(int X, int Y, int Z), int>();
        for (var i = 0; i < points.Count; i++)
        {
            if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
            var key = Key(points[i]); counts.TryGetValue(key, out var n); counts[key] = n + 1;
            if (counts.Count > MaximumCells) return 0; // Keep sparse large clouds rather than allocating an unbounded index.
        }
        var write = 0;
        for (var i = 0; i < points.Count; i++)
        {
            if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
            var p = Key(points[i]); var support = 0;
            for (var x = -1; x <= 1 && support < 3; x++) for (var y = -1; y <= 1 && support < 3; y++) for (var z = -1; z <= 1 && support < 3; z++)
                if (counts.TryGetValue((p.X + x, p.Y + y, p.Z + z), out var count)) support += count;
            if (support < 3) continue;
            points[write] = points[i]; colors[write] = colors[i]; write++;
        }
        var removed = points.Count - write; points.RemoveRange(write, removed); colors.RemoveRange(write, removed); return removed;
    }
}
