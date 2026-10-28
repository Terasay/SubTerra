using System.IO;
using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Services;

public sealed record ScreenScanSelection(Matrix4x4 ViewProjection, float Width, float Height,
    Vector2 Minimum, Vector2 Maximum, Vector2[]? BrushCenters = null, float BrushRadius = 20)
{
    public bool IsBrush => BrushCenters is { Length: > 0 };
}

public static class ScanCleaner
{
    private readonly record struct Cell(long X, long Y, long Z)
    {
        // Round the quotient to the precision of the stored coordinates before flooring.
        // Otherwise, e.g. 0.02f / 0.01 lands just below cell 2 in double precision.
        public static Cell At(Vector3 p, double size) => new((long)MathF.Floor((float)(p.X / size)),
            (long)MathF.Floor((float)(p.Y / size)), (long)MathF.Floor((float)(p.Z / size)));
    }
    public static ScanPointMask Region(ScanObject o, ScanBox box, bool keepInside, CancellationToken token = default)
    {
        box.Validate();
        var matrix = o.Matrix;
        return Filter(o, i => box.Contains(Vector3.Transform(o.Source.Points[i].Position, matrix)) == keepInside, token);
    }
    public static ScanPointMask Voxel(ScanObject o, double cellSize, CancellationToken token = default)
    {
        ValidateDistance(cellSize);
        if (o.Source.HasMesh) throw new InvalidDataException("Уменьшение плотности применяется к облаку без сетки. Детализация сетки меняется только для просмотра.");
        var cells = new HashSet<Cell>(); var matrix = o.Matrix;
        return Filter(o, i => cells.Add(Cell.At(Vector3.Transform(o.Source.Points[i].Position, matrix), cellSize)), token);
    }
    public static ScanPointMask Isolated(ScanObject o, double radius, int minimumNeighbors, CancellationToken token = default)
    {
        ValidateDistance(radius);
        if (minimumNeighbors is < 1 or > 64) throw new InvalidDataException("Количество соседей: от 1 до 64.");
        var positions = new Vector3[o.Source.Points.Length]; var matrix = o.Matrix;
        var cells = new Dictionary<Cell, List<int>>();
        for (var i = 0; i < positions.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (!o.Includes(i)) continue;
            positions[i] = Vector3.Transform(o.Source.Points[i].Position, matrix);
            var key = Cell.At(positions[i], radius);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            list.Add(i);
        }
        var radiusSquared = radius * radius;
        return Filter(o, i =>
        {
            var cell = Cell.At(positions[i], radius); var found = 0;
            for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
            {
                if (!cells.TryGetValue(new(cell.X + x, cell.Y + y, cell.Z + z), out var neighbors)) continue;
                foreach (var j in neighbors)
                {
                    if (j != i && Vector3.DistanceSquared(positions[i], positions[j]) <= radiusSquared && ++found >= minimumNeighbors) return true;
                }
            }
            return false;
        }, token);
    }
    public static ScanPointMask Screen(ScanObject o, ScreenScanSelection selection, bool keepInside, CancellationToken token = default)
    {
        if (!float.IsFinite(selection.Width) || !float.IsFinite(selection.Height) || selection.Width <= 0 || selection.Height <= 0 ||
            !float.IsFinite(selection.BrushRadius) || selection.BrushRadius is < 1 or > 500)
            throw new InvalidDataException("Некорректная область экрана.");
        var matrix = o.Matrix * selection.ViewProjection;
        var stroke = new Dictionary<(int X, int Y), List<Vector2>>();
        if (selection.BrushCenters is { } centers)
            foreach (var center in centers)
            {
                var key = ((int)MathF.Floor(center.X / selection.BrushRadius), (int)MathF.Floor(center.Y / selection.BrushRadius));
                if (!stroke.TryGetValue(key, out var list)) stroke[key] = list = [];
                list.Add(center);
            }
        bool Selected(int i)
        {
            var clip = Vector4.Transform(new Vector4(o.Source.Points[i].Position, 1), matrix);
            if (clip.W <= 0 || clip.Z < 0 || clip.Z > clip.W) return false;
            var screen = new Vector2((clip.X / clip.W + 1) * selection.Width / 2, (1 - clip.Y / clip.W) * selection.Height / 2);
            if (!selection.IsBrush) return screen.X >= selection.Minimum.X && screen.Y >= selection.Minimum.Y && screen.X <= selection.Maximum.X && screen.Y <= selection.Maximum.Y;
            var key = ((int)MathF.Floor(screen.X / selection.BrushRadius), (int)MathF.Floor(screen.Y / selection.BrushRadius));
            for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++)
                if (stroke.TryGetValue((key.Item1 + x, key.Item2 + y), out var list) && list.Any(p => Vector2.DistanceSquared(p, screen) <= selection.BrushRadius * selection.BrushRadius)) return true;
            return false;
        }
        return Filter(o, i => Selected(i) == keepInside, token);
    }
    private static ScanPointMask Filter(ScanObject o, Func<int, bool> keep, CancellationToken token)
    {
        var result = new bool[o.Source.Points.Length];
        for (var i = 0; i < result.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            result[i] = o.Includes(i) && keep(i);
        }
        return new(result);
    }
    private static void ValidateDistance(double value)
    {
        if (!double.IsFinite(value) || value is < .000001 or > 100_000) throw new InvalidDataException("Размер в метрах должен быть от 0,000001 до 100000.");
    }
}
