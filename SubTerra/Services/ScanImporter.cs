using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>Reads registered, static scanner exports. It does not register raw sensor frames.</summary>
public sealed class ScanImporter
{
    public Task<ImportedScan> ImportAsync(string path, ScanImportOptions options, CancellationToken token = default,
        IProgress<double>? progress = null) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(options.UpAxis) || !Enum.IsDefined(options.TextLayout)) throw new InvalidDataException("Неизвестные параметры импорта.");
        _ = options.Scale;
        using var file = File.OpenRead(path);
        var builder = new ImportBuilder(path, options);
        if (Path.GetExtension(path).Equals(".ply", StringComparison.OrdinalIgnoreCase)) ReadPly(file, builder, token, progress);
        else if (new[] { ".asc", ".xyz", ".txt" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            ReadText(file, builder, options, token, progress);
        else throw new InvalidDataException("Поддерживаются PLY, ASC, XYZ и TXT. Экспортируйте скан из 3DeVOK Studio в ASC или PLY.");
        var scan = builder.Build(token);
        scan.Validate(token);
        progress?.Report(100);
        return scan;
    }, token);

    private static void ReadText(FileStream file, ImportBuilder builder, ScanImportOptions options, CancellationToken token, IProgress<double>? progress)
    {
        using var reader = new StreamReader(file, Encoding.UTF8, true, 65536, leaveOpen: true);
        var lineNumber = 0;
        while (ReadBoundedLine(reader) is { } line)
        {
            if ((lineNumber++ & 4095) == 0) { token.ThrowIfCancellationRequested(); progress?.Report(file.Position * 95d / Math.Max(1, file.Length)); }
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
            var fields = line.Split([' ', '\t', ';', ','], StringSplitOptions.RemoveEmptyEntries);
            // ASC often carries normals after XYZ. RGB is only read when explicitly selected.
            var required = options.TextLayout switch { TextPointLayout.XyzRgb => 6, TextPointLayout.XyzNormalRgb => 9, _ => 3 };
            if (fields.Length < required)
                throw new InvalidDataException($"Строка {lineNumber}: для выбранного формата ожидается {required} столбцов.");
            try
            {
                uint? rgb = options.TextLayout switch
                {
                    TextPointLayout.XyzRgb => PackColor(Number(fields[3]), Number(fields[4]), Number(fields[5])),
                    TextPointLayout.XyzNormalRgb => PackColor(Number(fields[6]) * 255, Number(fields[7]) * 255, Number(fields[8]) * 255),
                    _ => null
                };
                builder.Add(Number(fields[0]), Number(fields[1]), Number(fields[2]), 1, rgb);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidDataException)
            { throw new InvalidDataException($"Строка {lineNumber}: {ex.Message}", ex); }
        }
    }

    private sealed record Property(string Name, string Type, string? CountType = null);
    private sealed record Element(string Name, int Count, List<Property> Properties);
    private static void ReadPly(FileStream file, ImportBuilder builder, CancellationToken token, IProgress<double>? progress)
    {
        var headerBytes = 0;
        string HeaderLine()
        {
            var bytes = new List<byte>();
            int b;
            while ((b = file.ReadByte()) >= 0 && b != '\n')
            {
                if (++headerBytes > 65536 || bytes.Count > 4096) throw new InvalidDataException("Слишком большой заголовок PLY.");
                if (b != '\r') bytes.Add((byte)b);
            }
            if (b < 0) throw new InvalidDataException("Оборван заголовок PLY.");
            ++headerBytes;
            return Encoding.ASCII.GetString(bytes.ToArray()).Trim();
        }
        if (HeaderLine() != "ply") throw new InvalidDataException("Нет сигнатуры PLY.");
        string? format = null;
        var elements = new List<Element>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var line = HeaderLine();
            if (line == "end_header") break;
            if (line == "comment subterra_contains_synthetic_data") builder.IsSynthetic = true;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] is "comment" or "obj_info") continue;
            if (parts[0] == "format" && parts.Length == 3 && parts[2] == "1.0" && format is null) format = parts[1];
            else if (parts[0] == "element" && parts.Length == 3 && elements.Count < 16 &&
                int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 0 and <= ImportedScan.MaximumPoints &&
                elements.All(e => e.Name != parts[1])) elements.Add(new(parts[1], count, []));
            else if (parts[0] == "property" && elements.Count > 0 && elements[^1].Properties.Count < 64)
            {
                var property = parts.Length == 3 ? new Property(parts[2], parts[1]) :
                    parts.Length == 5 && parts[1] == "list" ? new Property(parts[4], parts[3], parts[2]) : null;
                if (property is null || !PlyBody.IsType(property.Type) ||
                    (property.CountType is not null && !PlyBody.IsIntegerType(property.CountType)) || elements[^1].Properties.Any(p => p.Name == property.Name))
                    throw new InvalidDataException("Некорректное свойство PLY.");
                elements[^1].Properties.Add(property);
            }
            else throw new InvalidDataException("Неподдерживаемый или повреждённый заголовок PLY.");
        }
        if (format is not ("ascii" or "binary_little_endian" or "binary_big_endian")) throw new InvalidDataException("Неподдерживаемый формат PLY.");
        var vertex = elements.FirstOrDefault(e => e.Name == "vertex");
        if (vertex is null || vertex.Count == 0 || new[] { "x", "y", "z" }.Any(n => !vertex.Properties.Any(p => p.Name == n && p.CountType is null)))
            throw new InvalidDataException("В PLY отсутствуют вершины XYZ.");
        var colorNames = new[] { "red", "green", "blue" };
        var hasColor = colorNames.All(n => vertex.Properties.Any(p => p.Name == n && p.CountType is null));
        var hasFaceList = false;
        using var body = new PlyBody(file, format);
        foreach (var element in elements)
        {
            if (element.Count > 0 && element.Properties.Count == 0) throw new InvalidDataException("Пустое описание элемента PLY.");
            for (var i = 0; i < element.Count; i++)
            {
                if ((i & 4095) == 0) { token.ThrowIfCancellationRequested(); progress?.Report(file.Position * 95d / Math.Max(1, file.Length)); }
                double x = 0, y = 0, z = 0, intensity = 1, red = 0, green = 0, blue = 0;
                foreach (var property in element.Properties)
                {
                    if (property.CountType is not null)
                    {
                        var length = body.Read(property.CountType);
                        if (length < 0 || length > 4096 || length != Math.Truncate(length)) throw new InvalidDataException("Недопустимая длина списка PLY.");
                        var face = element.Name == "face" && property.Name is "vertex_indices" or "vertex_index";
                        if (face && !PlyBody.IsIntegerType(property.Type)) throw new InvalidDataException("Индексы PLY должны быть целыми.");
                        var polygon = face ? new int[(int)length] : null;
                        for (var j = 0; j < (int)length; j++)
                        {
                            var value = body.Read(property.Type);
                            if (!face) continue;
                            if (value < 0 || value >= vertex.Count || value != Math.Truncate(value)) throw new InvalidDataException("Неверный индекс вершины PLY.");
                            polygon![j] = (int)value;
                        }
                        if (face)
                        {
                            hasFaceList = true;
                            if (length < 3) throw new InvalidDataException("Грань PLY содержит меньше трёх вершин.");
                            if (length == 3) builder.AddTriangle(polygon![0], polygon[1], polygon[2]);
                            else builder.AddPolygon(polygon!);
                        }
                    }
                    else
                    {
                        var value = body.Read(property.Type);
                        if (element.Name != "vertex") continue;
                        switch (property.Name)
                        {
                            case "x": x = value; break; case "y": y = value; break; case "z": z = value; break;
                            case "intensity": intensity = Math.Clamp(value / (property.Type is "uchar" or "uint8" ? 255d : property.Type is "ushort" or "uint16" ? 65535d : 1), 0, 1); break;
                            case "red": red = ColorValue(value, property.Type); break;
                            case "green": green = ColorValue(value, property.Type); break;
                            case "blue": blue = ColorValue(value, property.Type); break;
                        }
                    }
                }
                if (element.Name == "vertex") builder.Add(x, y, z, intensity, hasColor ? PackColor(red, green, blue) : null);
            }
        }
        if (elements.Any(e => e.Name == "face" && e.Count > 0) && !hasFaceList) throw new InvalidDataException("Список граней PLY не распознан.");
    }
    private static double ColorValue(double value, string type) => type is "float" or "float32" or "double" or "float64" ? value * 255 : value;
    private static uint PackColor(double r, double g, double b)
    {
        if (r is < 0 or > 255 || g is < 0 or > 255 || b is < 0 or > 255) throw new InvalidDataException("RGB должен быть в диапазоне 0–255.");
        return (uint)Math.Round(r) << 16 | (uint)Math.Round(g) << 8 | (uint)Math.Round(b);
    }
    private static double Number(string text)
    {
        var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value)) throw new InvalidDataException("Число должно быть конечным.");
        return value;
    }
    private static string? ReadBoundedLine(StreamReader reader)
    {
        var line = new StringBuilder(); int c;
        while ((c = reader.Read()) >= 0 && c != '\n')
        {
            if (line.Length >= 16384) throw new InvalidDataException("Слишком длинная строка данных.");
            if (c != '\r') line.Append((char)c);
        }
        return c < 0 && line.Length == 0 ? null : line.ToString();
    }

    private sealed class ImportBuilder(string path, ScanImportOptions options)
    {
        private readonly List<ScanPoint> _points = [];
        private readonly List<uint> _colors = [];
        private readonly List<int> _triangles = [];
        private readonly List<int[]> _polygons = [];
        private int _pendingTriangles;
        private double _ox, _oy, _oz;
        private bool _hasColor;
        public bool IsSynthetic { get; set; }
        public void Add(double x, double y, double z, double intensity, uint? color)
        {
            if (_points.Count >= ImportedScan.MaximumPoints) throw new InvalidDataException("Лимит импорта — 5 млн точек. Уменьшите плотность в 3DeVOK Studio.");
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) || Math.Abs(x) > 1e12 || Math.Abs(y) > 1e12 || Math.Abs(z) > 1e12)
                throw new InvalidDataException("Некорректные исходные координаты.");
            if (_points.Count == 0 && options.LocalOrigin) { _ox = x; _oy = y; _oz = z; }
            var px = (x - _ox) * options.Scale; var py = (y - _oy) * options.Scale; var pz = (z - _oz) * options.Scale;
            if (options.UpAxis == ScanUpAxis.Y) (py, pz) = (-pz, py);
            if (Math.Abs(px) > 100_000 || Math.Abs(py) > 100_000 || Math.Abs(pz) > 100_000) throw new InvalidDataException("Координаты выходят за 100 км. Проверьте единицы и включите локальное начало.");
            _points.Add(new((float)px, (float)py, (float)pz, (float)intensity));
            _hasColor |= color.HasValue;
            _colors.Add(color ?? 0xffffff);
        }
        public void AddTriangle(int a, int b, int c)
        {
            if (_triangles.Count / 3 + _pendingTriangles >= ImportedScan.MaximumTriangles) throw new InvalidDataException("Лимит импорта — 5 млн треугольников. Упростите сетку в 3DeVOK Studio.");
            _triangles.Add(a); _triangles.Add(b); _triangles.Add(c);
        }
        public void AddPolygon(int[] polygon)
        {
            if ((long)_triangles.Count / 3 + _pendingTriangles + polygon.Length - 2 > ImportedScan.MaximumTriangles)
                throw new InvalidDataException("Лимит импорта — 5 млн треугольников. Упростите сетку в 3DeVOK Studio.");
            _pendingTriangles += polygon.Length - 2;
            _polygons.Add(polygon);
        }
        public ImportedScan Build(CancellationToken token)
        {
            foreach (var polygon in _polygons)
            {
                token.ThrowIfCancellationRequested();
                _pendingTriangles -= polygon.Length - 2;
                Triangulate(polygon, token);
            }
            return new() { Metadata = new(Path.GetFileName(path), options.Unit, options.UpAxis, _ox, _oy, _oz) { IsSynthetic = IsSynthetic },
                Points = _points.ToArray(), Colors = _hasColor ? _colors.ToArray() : [], Triangles = _triangles.ToArray() };
        }
        private void Triangulate(int[] polygon, CancellationToken token)
        {
            var normal = Vector3.Zero;
            var origin = _points[polygon[0]].Position;
            for (var i = 0; i < polygon.Length; i++) normal += Vector3.Cross(_points[polygon[i]].Position - origin, _points[polygon[(i + 1) % polygon.Length]].Position - origin);
            var axis = Math.Abs(normal.X) >= Math.Abs(normal.Y) && Math.Abs(normal.X) >= Math.Abs(normal.Z) ? 0 : Math.Abs(normal.Y) >= Math.Abs(normal.Z) ? 1 : 2;
            var projected = polygon.Select(index =>
            {
                var p = _points[index].Position - origin;
                return axis switch { 0 => new Vector2(p.Y, p.Z), 1 => new Vector2(p.X, p.Z), _ => new Vector2(p.X, p.Y) };
            }).ToArray();
            var extent = projected.Max(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));
            if (extent <= 0) throw new InvalidDataException("Вырожденная грань PLY.");
            for (var i = 0; i < projected.Length; i++) projected[i] /= extent;
            static double Cross(Vector2 a, Vector2 b, Vector2 c) => ((double)b.X - a.X) * (c.Y - a.Y) - ((double)b.Y - a.Y) * (c.X - a.X);
            double area = 0;
            for (var i = 0; i < projected.Length; i++) area += Cross(Vector2.Zero, projected[i], projected[(i + 1) % projected.Length]);
            if (Math.Abs(area) < 1e-10) throw new InvalidDataException("Вырожденная грань PLY.");
            var sign = Math.Sign(area);
            if (Enumerable.Range(0, polygon.Length).All(i => Cross(projected[i], projected[(i + 1) % polygon.Length], projected[(i + 2) % polygon.Length]) * sign > 1e-10))
            {
                for (var i = 2; i < polygon.Length; i++) AddTriangle(polygon[0], polygon[i - 1], polygon[i]);
                return;
            }
            var remaining = Enumerable.Range(0, polygon.Length).ToList();
            while (remaining.Count > 3)
            {
                token.ThrowIfCancellationRequested();
                var removed = false;
                for (var i = 0; i < remaining.Count; i++)
                {
                    var a = remaining[(i + remaining.Count - 1) % remaining.Count];
                    var b = remaining[i]; var c = remaining[(i + 1) % remaining.Count];
                    var turn = Cross(projected[a], projected[b], projected[c]) * sign;
                    if (Math.Abs(turn) < 1e-10) { remaining.RemoveAt(i); removed = true; break; }
                    if (turn < 0) continue;
                    if (remaining.Any(p => p != a && p != b && p != c &&
                        Cross(projected[a], projected[b], projected[p]) * sign >= -1e-10 &&
                        Cross(projected[b], projected[c], projected[p]) * sign >= -1e-10 &&
                        Cross(projected[c], projected[a], projected[p]) * sign >= -1e-10)) continue;
                    AddTriangle(polygon[a], polygon[b], polygon[c]); remaining.RemoveAt(i); removed = true; break;
                }
                if (!removed) throw new InvalidDataException("Не удалось разбить грань PLY. Экспортируйте треугольную сетку из Studio.");
            }
            AddTriangle(polygon[remaining[0]], polygon[remaining[1]], polygon[remaining[2]]);
        }
    }

    private sealed class PlyBody : IDisposable
    {
        private readonly FileStream _stream;
        private readonly StreamReader? _text;
        private readonly bool _bigEndian;
        public PlyBody(FileStream stream, string format)
        { _stream = stream; _bigEndian = format == "binary_big_endian"; if (format == "ascii") _text = new(stream, Encoding.ASCII, false, 65536, leaveOpen: true); }
        public static bool IsIntegerType(string type) => type is "char" or "int8" or "uchar" or "uint8" or "short" or "int16" or "ushort" or "uint16" or "int" or "int32" or "uint" or "uint32";
        public static bool IsType(string type) => IsIntegerType(type) || type is "float" or "float32" or "double" or "float64";
        public double Read(string type)
        {
            double value;
            if (_text is not null)
            {
                var token = new StringBuilder(24); int c;
                do { c = _text.Read(); } while (c >= 0 && char.IsWhiteSpace((char)c));
                while (c >= 0 && !char.IsWhiteSpace((char)c))
                { if (token.Length >= 128) throw new InvalidDataException("Слишком длинное число PLY."); token.Append((char)c); c = _text.Read(); }
                if (token.Length == 0) throw new EndOfStreamException("Оборваны данные PLY.");
                try { value = Number(token.ToString()); }
                catch (FormatException ex) { throw new InvalidDataException("Неверное число PLY.", ex); }
            }
            else
            {
                var size = type switch { "char" or "int8" or "uchar" or "uint8" => 1, "short" or "int16" or "ushort" or "uint16" => 2, "double" or "float64" => 8, _ => 4 };
                Span<byte> bytes = stackalloc byte[8]; _stream.ReadExactly(bytes[..size]);
                if (_bigEndian) bytes[..size].Reverse();
                value = type switch
                {
                    "char" or "int8" => (sbyte)bytes[0], "uchar" or "uint8" => bytes[0],
                    "short" or "int16" => BinaryPrimitives.ReadInt16LittleEndian(bytes), "ushort" or "uint16" => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                    "int" or "int32" => BinaryPrimitives.ReadInt32LittleEndian(bytes), "uint" or "uint32" => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                    "float" or "float32" => BinaryPrimitives.ReadSingleLittleEndian(bytes), _ => BinaryPrimitives.ReadDoubleLittleEndian(bytes)
                };
            }
            if (!double.IsFinite(value)) throw new InvalidDataException("Неконечное число PLY.");
            if (IsIntegerType(type) && value != Math.Truncate(value)) throw new InvalidDataException("Нецелое значение PLY.");
            var valid = type switch
            {
                "char" or "int8" => value is >= sbyte.MinValue and <= sbyte.MaxValue,
                "uchar" or "uint8" => value is >= byte.MinValue and <= byte.MaxValue,
                "short" or "int16" => value is >= short.MinValue and <= short.MaxValue,
                "ushort" or "uint16" => value is >= ushort.MinValue and <= ushort.MaxValue,
                "int" or "int32" => value is >= int.MinValue and <= int.MaxValue,
                "uint" or "uint32" => value is >= uint.MinValue and <= uint.MaxValue,
                "float" or "float32" => Math.Abs(value) <= float.MaxValue,
                _ => true
            };
            if (!valid) throw new InvalidDataException("Значение выходит за диапазон типа PLY.");
            return value;
        }
        public void Dispose() => _text?.Dispose();
    }
}
