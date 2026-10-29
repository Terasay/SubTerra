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
    public static string Signature(StructureAnalysisDocument document, int[]? surfaceIds = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(document.Algorithm); writer.Write(document.CreatedAtUtc.UtcTicks);
            writer.Write(document.Result.Options.DistanceTolerance);
            foreach (var s in document.Result.Surfaces.Where(s => s.Review == StructureReviewState.Confirmed && s.Kind != StructureSurfaceKind.Unknown && (surfaceIds is null || surfaceIds.Contains(s.Id))).OrderBy(s => s.Id))
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
        document.Room is { } room && !document.IsStale && room.SourceSignature == Signature(document, room.SurfaceIds);

    public static RoomBuildResult Build(StructureAnalysisDocument document, string name, CancellationToken token = default, int[]? surfaceIds = null, Vector3? interiorPoint = null)
    {
        token.ThrowIfCancellationRequested();
        RoomBuildResult Fail(string message) => new(null, message);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) return Fail("Название комнаты: от 1 до 80 символов.");
        if (document.IsStale) return Fail("Анализ устарел. Повторите его или верните исходную геометрию.");
        if (surfaceIds is not null && (surfaceIds.Length > 24 || surfaceIds.Distinct().Count() != surfaceIds.Length || surfaceIds.Any(id => document.Result.Surfaces.All(s => s.Id != id))))
            return Fail("Некорректный выбор поверхностей комнаты.");
        if (interiorPoint is { } seed && !InspectionPoint.From(seed).IsValid) return Fail("Некорректная точка внутри комнаты.");
        var confirmed = document.Result.Surfaces.Where(s => s.Review == StructureReviewState.Confirmed && (surfaceIds is null || surfaceIds.Contains(s.Id))).ToArray();
        var floors = confirmed.Where(s => s.Kind == StructureSurfaceKind.Floor).ToArray();
        var ceilings = confirmed.Where(s => s.Kind == StructureSurfaceKind.Ceiling).ToArray();
        var sides = confirmed.Where(s => s.Kind == StructureSurfaceKind.Wall).ToArray();
        if (floors.Length != 1) return Fail("Подтвердите ровно один пол для одной комнаты.");
        if (ceilings.Length > 1) return Fail("Подтверждено несколько потолков. Выберите потолок одной комнаты.");
        if (sides.Length < 3) return Fail("Недостаточно стен: нужны минимум три подтверждённые плоскости, замыкающие контур.");
        var floor = floors[0]; var ceiling = ceilings.FirstOrDefault();
        if (Math.Abs(floor.Normal.Z) < .9994 || ceiling is not null && Math.Abs(ceiling.Normal.Z) < .9994 || sides.Any(s => Math.Abs(s.Normal.Z) > .01))
            return Fail("Для первого варианта нужны почти горизонтальные пол/потолок и вертикальные стены. Проверьте вертикаль Z.");
        var walls = new List<Wall>(); var center = new Point(interiorPoint?.X ?? floor.Center.X, interiorPoint?.Y ?? floor.Center.Y);
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
        var room = new RoomPlan(name.Trim(), 1, Signature(document, surfaceIds), floor.Id, ceiling?.Id, edgeWalls, bottom, top, [], [],
            SurfaceIds: surfaceIds?.Order().ToArray(), InteriorPoint: interiorPoint);
        if (room.AreaSquareMeters is < .5 or > 1_000_000)
            return Fail("Опорный участок пола слишком мал для этого контура. Проверьте класс пола и полноту сканирования.");
        foreach (var support in new[] { floor, ceiling }.OfType<StructureSurface>())
        {
            var inside = support.Samples.Where(s => walls.All(w => w.Distance(new(s.Position.X, s.Position.Y)) <= tolerance)).Select(s => s.Position).ToArray();
            if (support.Samples.Length == 0 || (interiorPoint is null && inside.Length < support.Samples.Length * .9))
                return Fail("Опорные точки выходят за контур. Поверхности могут относиться к разным помещениям или содержать перегородку.");
            var area = interiorPoint is null ? support.AreaSquareMeters : inside.Length < 3 ? 0 : PlanePatch.Boundary(new(support.Normal, support.Offset, support.Center), inside).Area;
            if (area < room.AreaSquareMeters * .5) return Fail("Недостаточно опорных точек пола или потолка в выбранной комнате.");
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

/// <summary>Operator edits preserve the automatic result and never modify source measurements.</summary>
public static class RoomPlanEditor
{
    private static RoomPlan Current(StructureAnalysisDocument document)
    {
        if (document.Room is not { } room || !RoomPlanner.IsCurrent(document))
            throw new InvalidDataException("Сначала постройте актуальный план комнаты.");
        return room;
    }
    private static void Index(RoomPlan room, int index)
    {
        if ((uint)index >= room.FloorContour.Length) throw new InvalidDataException("Выберите вершину контура.");
    }
    private static RoomPlan Commit(StructureAnalysisDocument document, RoomPlan changed, CancellationToken token)
    {
        var original = Current(document);
        changed = changed with { Version = 2, AutomaticPlan = original.AutomaticPlan ?? original };
        RoomPlanValidation.Validate(document with { Version = 3, Room = changed }, token);
        return changed;
    }
    public static RoomPlan MoveVertex(StructureAnalysisDocument document, int index, double x, double y, CancellationToken token = default)
    {
        var room = Current(document); Index(room, index);
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 100_000 || Math.Abs(y) > 100_000)
            throw new InvalidDataException("Координаты вершины должны быть конечными, в пределах ±100 км.");
        Vector3 Position(int surfaceId)
        {
            var plane = document.Result.Surfaces.Single(s => s.Id == surfaceId);
            var z = -(plane.Offset + plane.Normal.X * x + plane.Normal.Y * y) / plane.Normal.Z;
            return new((float)x, (float)y, (float)z);
        }
        var bottom = room.FloorContour.ToArray(); bottom[index] = Position(room.FloorSurfaceId);
        var top = room.CeilingContour.ToArray();
        if (room.CeilingSurfaceId is { } ceiling) top[index] = Position(ceiling);
        return Commit(document, room with { FloorContour = bottom, CeilingContour = top }, token);
    }
    public static RoomPlan InsertVertex(StructureAnalysisDocument document, int index, CancellationToken token = default)
    {
        var room = Current(document); Index(room, index);
        if (room.FloorContour.Length >= 24) throw new InvalidDataException("Лимит контура — 24 вершины.");
        var next = (index + 1) % room.FloorContour.Length;
        var bottom = room.FloorContour.ToList(); bottom.Insert(index + 1, (bottom[index] + bottom[next]) * .5f);
        var top = room.CeilingContour.ToList();
        if (room.HasHeight) top.Insert(index + 1, (top[index] + top[next]) * .5f);
        var walls = room.WallSurfaceIds.ToList(); walls.Insert(index + 1, walls[index]);
        var gaps = new List<RoomGap>();
        foreach (var gap in room.Gaps)
        {
            if (gap.EdgeIndex != index) { gaps.Add(gap with { EdgeIndex = gap.EdgeIndex > index ? gap.EdgeIndex + 1 : gap.EdgeIndex }); continue; }
            if (gap.Start < .5) gaps.Add(gap with { Start = gap.Start * 2, End = Math.Min(1, gap.End * 2) });
            if (gap.End > .5) gaps.Add(gap with { EdgeIndex = index + 1, Start = Math.Max(0, gap.Start * 2 - 1), End = gap.End * 2 - 1 });
        }
        return Commit(document, room with { FloorContour = bottom.ToArray(), CeilingContour = top.ToArray(), WallSurfaceIds = walls.ToArray(), Gaps = gaps.ToArray() }, token);
    }
    public static RoomPlan RemoveVertex(StructureAnalysisDocument document, int index, CancellationToken token = default)
    {
        var room = Current(document); Index(room, index);
        if (room.FloorContour.Length <= 3) throw new InvalidDataException("В контуре должны остаться минимум три вершины.");
        var previous = (index + room.FloorContour.Length - 1) % room.FloorContour.Length;
        if (room.Gaps.Any(g => g.EdgeIndex == previous || g.EdgeIndex == index))
            throw new InvalidDataException("Сначала удалите проёмы и разрывы на двух смежных рёбрах.");
        var bottom = room.FloorContour.Where((_, i) => i != index).ToArray();
        var top = room.CeilingContour.Where((_, i) => i != index).ToArray();
        var walls = room.WallSurfaceIds.Where((_, i) => i != index).ToArray();
        return Commit(document, room with { FloorContour = bottom, CeilingContour = top, WallSurfaceIds = walls,
            Gaps = room.Gaps.Select(g => g with { EdgeIndex = g.EdgeIndex > index ? g.EdgeIndex - 1 : g.EdgeIndex }).ToArray(),
            IncompleteWallIds = room.IncompleteWallIds.Where(walls.Contains).ToArray() }, token);
    }
    public static RoomPlan AddOpening(StructureAnalysisDocument document, int edge, double startMeters, double endMeters, CancellationToken token = default)
    {
        var room = Current(document); Index(room, edge);
        var a = room.FloorContour[edge]; var b = room.FloorContour[(edge + 1) % room.FloorContour.Length];
        var length = Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));
        if (!double.IsFinite(startMeters) || !double.IsFinite(endMeters) || startMeters < 0 || endMeters > length || endMeters - startMeters < .01)
            throw new InvalidDataException($"Проём задаётся вдоль ребра: от 0 до {length:F3} м, длиной не меньше 0,01 м.");
        var gap = new RoomGap(room.WallSurfaceIds[edge], edge, startMeters / length, endMeters / length, true);
        if (room.Gaps.Any(g => g.IsConfirmedOpening && g.EdgeIndex == edge && g.Start < gap.End && g.End > gap.Start))
            throw new InvalidDataException("Проём пересекает уже добавленный проём.");
        return Commit(document, room with { Gaps = [.. room.Gaps, gap] }, token);
    }
    public static RoomPlan RemoveGap(StructureAnalysisDocument document, RoomGap gap, CancellationToken token = default)
    {
        var room = Current(document);
        if (!room.Gaps.Contains(gap)) throw new InvalidDataException("Выбранный проём отсутствует в плане.");
        return Commit(document, room with { Gaps = room.Gaps.Where(g => g != gap).ToArray() }, token);
    }
}

public static class RoomTopology
{
    public static RoomConnection[] Connections(StructureAnalysisDocument document)
    {
        var rooms = (document.Rooms ?? []).Where(r => RoomPlanner.IsCurrent(document with { Room = r.Plan })).ToArray();
        var result = new List<RoomConnection>();
        for (var i = 0; i < rooms.Length; i++) for (var j = i + 1; j < rooms.Length; j++)
        {
            if (!string.Equals(rooms[i].Floor, rooms[j].Floor, StringComparison.OrdinalIgnoreCase)) continue;
            var first = rooms[i].Plan; var second = rooms[j].Plan; var connected = false; var opening = false;
            for (var edge = 0; edge < first.FloorContour.Length; edge++) for (var other = 0; other < second.FloorContour.Length; other++)
            {
                var a = first.FloorContour[edge]; var b = first.FloorContour[(edge + 1) % first.FloorContour.Length];
                var c = second.FloorContour[other]; var d = second.FloorContour[(other + 1) % second.FloorContour.Length];
                var direction = new Vector2(b.X - a.X, b.Y - a.Y); var length = direction.Length(); direction /= length;
                double Projection(Vector3 p) => (p.X - a.X) * direction.X + (p.Y - a.Y) * direction.Y;
                double Off(Vector3 p) => Math.Abs((p.X - a.X) * direction.Y - (p.Y - a.Y) * direction.X);
                if (Off(c) > .1 || Off(d) > .1 || Math.Abs(a.Z - c.Z) > .3) continue;
                var start = Math.Max(0, Math.Min(Projection(c), Projection(d))); var end = Math.Min(length, Math.Max(Projection(c), Projection(d)));
                if (end - start < .2) continue;
                connected = true;
                opening |= first.Gaps.Any(g => g.IsConfirmedOpening && g.EdgeIndex == edge && Math.Min(end, g.End * length) - Math.Max(start, g.Start * length) > .1) ||
                    second.Gaps.Any(g => g.IsConfirmedOpening && g.EdgeIndex == other &&
                    Math.Min(end, Math.Max(Projection(Vector3.Lerp(c, d, (float)g.Start)), Projection(Vector3.Lerp(c, d, (float)g.End)))) -
                    Math.Max(start, Math.Min(Projection(Vector3.Lerp(c, d, (float)g.Start)), Projection(Vector3.Lerp(c, d, (float)g.End)))) > .1);
            }
            if (connected) result.Add(new(rooms[i].Id, rooms[j].Id, opening));
        }
        return result.ToArray();
    }
    public static bool Contains(RoomPlan room, Vector3 point)
    {
        if (!room.HasHeight || point.Z < room.FloorContour.Min(p => p.Z) - .1f || point.Z > room.CeilingContour.Max(p => p.Z) + .1f) return false;
        for (var i = 0; i < room.FloorContour.Length; i++)
        {
            var a = room.FloorContour[i]; var b = room.FloorContour[(i + 1) % room.FloorContour.Length];
            if ((b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X) < -.0001f) return false;
        }
        return true;
    }
}
