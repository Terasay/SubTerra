using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>One convex room with known vertical. No bounding-box fallback or fabricated heights.</summary>
public static class RoomPlanner
{
    private readonly record struct Point(double X, double Y);
    private sealed record Wall(StructureSurface Surface, double X, double Y, double Limit)
    { public double Distance(Point p) => X * p.X + Y * p.Y - Limit; }
    public static string Signature(StructureAnalysisDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(document.Algorithm); writer.Write(document.CreatedAtUtc.UtcTicks);
            writer.Write(document.Result.Options.DistanceTolerance);
            foreach (var s in document.Result.Surfaces.Where(s => s.Review == StructureReviewState.Confirmed && s.Kind != StructureSurfaceKind.Unknown).OrderBy(s => s.Id))
            {
                writer.Write(s.Id); writer.Write((int)s.Kind); writer.Write(s.Normal.X); writer.Write(s.Normal.Y); writer.Write(s.Normal.Z);
                writer.Write(s.Offset); writer.Write(s.Center.X); writer.Write(s.Center.Y); writer.Write(s.Center.Z); writer.Write(s.AreaSquareMeters);
                writer.Write(s.Boundary.Length);
                foreach (var p in s.Boundary) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    public static bool IsCurrent(StructureAnalysisDocument document) =>
        document.Room is { } room && !document.IsStale && room.SourceSignature == Signature(document);

    public static RoomBuildResult Build(StructureAnalysisDocument document, string name, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RoomBuildResult Fail(string message) => new(null, message);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) return Fail("Название комнаты: от 1 до 80 символов.");
        if (document.IsStale) return Fail("Анализ устарел. Повторите его или верните исходную геометрию.");
        var confirmed = document.Result.Surfaces.Where(s => s.Review == StructureReviewState.Confirmed).ToArray();
        var floors = confirmed.Where(s => s.Kind == StructureSurfaceKind.Floor).ToArray();
        var ceilings = confirmed.Where(s => s.Kind == StructureSurfaceKind.Ceiling).ToArray();
        var sides = confirmed.Where(s => s.Kind == StructureSurfaceKind.Wall).ToArray();
        if (floors.Length != 1) return Fail("Подтвердите ровно один пол для одной комнаты.");
        if (ceilings.Length > 1) return Fail("Подтверждено несколько потолков. Выберите потолок одной комнаты.");
        if (sides.Length < 3) return Fail("Недостаточно стен: нужны минимум три подтверждённые плоскости, замыкающие контур.");
        var floor = floors[0]; var ceiling = ceilings.FirstOrDefault();
        if (Math.Abs(floor.Normal.Z) < .9994 || ceiling is not null && Math.Abs(ceiling.Normal.Z) < .9994 || sides.Any(s => Math.Abs(s.Normal.Z) > .01))
            return Fail("Для первого варианта нужны почти горизонтальные пол/потолок и вертикальные стены. Проверьте вертикаль Z.");
        var walls = new List<Wall>(); var center = new Point(floor.Center.X, floor.Center.Y);
        foreach (var side in sides)
        {
            token.ThrowIfCancellationRequested();
            var length = Math.Sqrt(side.Normal.X * (double)side.Normal.X + side.Normal.Y * (double)side.Normal.Y);
            var a = side.Normal.X / length; var b = side.Normal.Y / length;
            var limit = -(side.Offset + side.Normal.Z * (double)floor.Center.Z) / length;
            var distance = a * center.X + b * center.Y - limit;
            if (!double.IsFinite(distance) || Math.Abs(distance) < .1) return Fail("Неоднозначная граница: центр пола лежит на стене. Проверьте классы и принадлежность поверхностей одной комнате.");
            walls.Add(distance < 0 ? new(side, a, b, limit) : new(side, -a, -b, -limit));
        }
        var angles = walls.Select(w => Math.Atan2(w.Y, w.X)).Order().ToArray();
        var maxGap = Enumerable.Range(0, angles.Length).Max(i => (i + 1 == angles.Length ? angles[0] + Math.Tau : angles[i + 1]) - angles[i]);
        if (maxGap >= Math.PI - .0001) return Fail("Стены не замыкают контур. Не хватает подтверждённой границы; площадь не рассчитана.");
        var tolerance = Math.Max(.02, document.Result.Options.DistanceTolerance * 2);
        var intersections = new List<Point>();
        for (var i = 0; i < walls.Count; i++)
            for (var j = i + 1; j < walls.Count; j++)
            {
                var a = walls[i]; var b = walls[j]; var determinant = a.X * b.Y - a.Y * b.X;
                if (Math.Abs(determinant) < 1e-8) continue;
                var point = new Point((a.Limit * b.Y - a.Y * b.Limit) / determinant, (a.X * b.Limit - a.Limit * b.X) / determinant);
                if (double.IsFinite(point.X) && double.IsFinite(point.Y) && walls.All(w => w.Distance(point) <= tolerance)) intersections.Add(point);
            }
        var hull = Hull(intersections);
        if (hull.Length is < 3 or > 24) return Fail("Не удалось получить устойчивый выпуклый контур комнаты.");
        var bottom = hull.Select(p => new Vector3((float)p.X, (float)p.Y, (float)Height(floor, p))).ToArray();
        var top = ceiling is null ? [] : hull.Select(p => new Vector3((float)p.X, (float)p.Y, (float)Height(ceiling, p))).ToArray();
        if (bottom.Concat(top).Any(p => !InspectionPoint.From(p).IsValid)) return Fail("Контур выходит за допустимые координаты проекта.");
        for (var i = 0; i < bottom.Length; i++)
        {
            var a = bottom[i]; var b = bottom[(i + 1) % bottom.Length]; var c = bottom[(i + 2) % bottom.Length];
            if ((b.X - (double)a.X) * (c.Y - (double)b.Y) - (b.Y - (double)a.Y) * (c.X - (double)b.X) <= 1e-7 ||
                Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y)) < .01) return Fail("Недостаточная точность вершин контура. Проверьте масштаб и локальное начало координат.");
        }
        if (top.Length > 0 && bottom.Zip(top, (a, b) => b.Z - a.Z).Any(h => h is < .5f or > 100))
            return Fail("Некорректная высота: потолок должен находиться над полом. Проверьте классы и масштаб.");
        var edgeWalls = new int[hull.Length];
        for (var i = 0; i < hull.Length; i++)
        {
            var next = hull[(i + 1) % hull.Length]; var middle = new Point((hull[i].X + next.X) / 2, (hull[i].Y + next.Y) / 2);
            var closest = walls.MinBy(w => Math.Abs(w.Distance(middle)))!;
            if (Math.Abs(closest.Distance(middle)) > tolerance * 2) return Fail("Часть контура не соответствует подтверждённым стенам.");
            var nextIndex = (i + 1) % hull.Length;
            var endpoints = top.Length == 0 ? new[] { bottom[i], bottom[nextIndex] } : new[] { bottom[i], bottom[nextIndex], top[i], top[nextIndex] };
            if (endpoints.Any(p => Math.Abs(PlanePatch.Distance(closest.Surface.Normal, closest.Surface.Offset, p)) > Math.Max(.05, tolerance * 1.5)))
                return Fail("Наклон или пересечения поверхностей не соответствуют простому плану. Уточните вертикаль и границы.");
            edgeWalls[i] = closest.Surface.Id;
        }
        if (sides.Any(s => !edgeWalls.Contains(s.Id))) return Fail("Есть лишние или дублирующиеся стены. Уточните поверхности одной комнаты.");
        var room = new RoomPlan(name.Trim(), 1, Signature(document), floor.Id, ceiling?.Id, edgeWalls, bottom, top, [], []);
        if (room.AreaSquareMeters is < .5 or > 1_000_000 || floor.AreaSquareMeters < room.AreaSquareMeters * .5)
            return Fail("Опорный участок пола слишком мал для этого контура. Проверьте класс пола и полноту сканирования.");
        foreach (var support in new[] { floor, ceiling }.OfType<StructureSurface>())
        {
            if (support.Samples.Length == 0 || support.Samples.Count(s => walls.All(w => w.Distance(new(s.Position.X, s.Position.Y)) <= tolerance)) < support.Samples.Length * .9)
                return Fail("Опорные точки выходят за контур. Поверхности могут относиться к разным помещениям или содержать перегородку.");
            if (support.AreaSquareMeters < room.AreaSquareMeters * .5) return Fail("Недостаточно опорных точек потолка. Уточните потолок или оставьте высоту неизвестной.");
        }
        var gaps = new List<RoomGap>(); var incomplete = new List<int>();
        var minimumHeight = room.MinimumHeight; var bandTop = Math.Min(1.8, minimumHeight * .7 ?? 1.8);
        for (var edge = 0; edge < hull.Length; edge++)
        {
            token.ThrowIfCancellationRequested();
            var side = sides.Single(s => s.Id == edgeWalls[edge]);
            var a = hull[edge]; var b = hull[(edge + 1) % hull.Length];
            var dx = b.X - a.X; var dy = b.Y - a.Y; var length = Math.Sqrt(dx * dx + dy * dy);
            var bins = Math.Clamp((int)Math.Ceiling(length / .25), 2, 256); var observed = new bool[bins];
            double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;
            foreach (var sample in side.Samples)
            {
                var p = sample.Position; var relativeZ = p.Z - Height(floor, new(p.X, p.Y));
                minZ = Math.Min(minZ, relativeZ); maxZ = Math.Max(maxZ, relativeZ);
                var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (length * length);
                if (relativeZ >= .15 && relativeZ <= bandTop && t >= 0 && t <= 1) observed[Math.Min(bins - 1, (int)(t * bins))] = true;
            }
            for (var i = 0; i < bins;)
            {
                if (observed[i]) { i++; continue; }
                var start = i; while (i < bins && !observed[i]) i++;
                if ((i - start) * length / bins > .55) gaps.Add(new(side.Id, edge, start / (double)bins, i / (double)bins));
            }
            if (minZ > .4 || minimumHeight is { } height && maxZ < height - .4) incomplete.Add(side.Id);
        }
        if (gaps.Count > 256) return Fail("Слишком много разрывов опорных данных. Уточните границы одной комнаты.");
        room = room with { Gaps = gaps.ToArray(), IncompleteWallIds = incomplete.Distinct().ToArray() };
        return new(room, "План построен по опорным плоскостям. Проверьте контур и возможные проёмы.");
    }
    private static double Height(StructureSurface plane, Point p) => -(plane.Offset + plane.Normal.X * p.X + plane.Normal.Y * p.Y) / plane.Normal.Z;
    private static Point[] Hull(List<Point> points)
    {
        var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToArray(); var distinct = new List<Point>();
        foreach (var p in sorted) if (!distinct.Any(q => Math.Abs(q.X - p.X) < .001 && Math.Abs(q.Y - p.Y) < .001)) distinct.Add(p);
        if (distinct.Count < 3) return [];
        static double Cross(Point a, Point b, Point c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        var hull = new List<Point>();
        foreach (var p in distinct) { while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 1e-7) hull.RemoveAt(hull.Count - 1); hull.Add(p); }
        var lower = hull.Count + 1;
        for (var i = distinct.Count - 2; i >= 0; i--) { var p = distinct[i]; while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 1e-7) hull.RemoveAt(hull.Count - 1); hull.Add(p); }
        hull.RemoveAt(hull.Count - 1); return hull.ToArray();
    }
}
