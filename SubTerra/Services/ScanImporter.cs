using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
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
        if (Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase))
            return new GlbReader(file, path, options.LocalOrigin, token).Read(progress);
        var builder = new ImportBuilder(path, options);
        if (Path.GetExtension(path).Equals(".ply", StringComparison.OrdinalIgnoreCase)) ReadPly(file, builder, token, progress);
        else if (new[] { ".asc", ".xyz", ".txt" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            ReadText(file, builder, options, token, progress);
        else throw new InvalidDataException("Поддерживаются GLB, PLY, ASC, XYZ и TXT.");
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

/// <summary>Static glTF 2.0 GLB scenes. All resources stay embedded; no external files or network requests.</summary>
internal sealed class GlbReader(FileStream file, string path, bool localOrigin, CancellationToken token)
{
    private JsonElement _root;
    private byte[] _bin = [];
    private int _bufferLength;
    private readonly List<ScanPoint> _points = [];
    private readonly List<Vector2> _uv = [];
    private readonly List<Vector3> _normals = [];
    private readonly List<Vector4> _vertexColors = [];
    private readonly List<int> _indices = [];
    private readonly List<MeshPart> _parts = [];
    private readonly List<MeshMaterial> _materials = [];
    private readonly List<MeshTexture> _textures = [];
    private int _defaultMaterial;
    private Vector3? _origin;
    private readonly HashSet<int> _visited = [];
    private static InvalidDataException Bad(string message) => new("GLB: " + message);
    private static JsonElement Optional(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;
    private static int Integer(JsonElement value, string name, int fallback = 0) => Optional(value, name) is { ValueKind: JsonValueKind.Number } p && p.TryGetInt32(out var result) ? result : Optional(value, name).ValueKind == JsonValueKind.Undefined ? fallback : throw Bad("некорректное поле " + name);
    private static float Scalar(JsonElement value, string name, float fallback) => Optional(value, name) is { ValueKind: JsonValueKind.Number } p && p.TryGetSingle(out var result) && float.IsFinite(result) ? result : Optional(value, name).ValueKind == JsonValueKind.Undefined ? fallback : throw Bad("некорректное поле " + name);
    private static string Text(JsonElement value, string name, string fallback) => Optional(value, name) is { ValueKind: JsonValueKind.String } p ? p.GetString()! : Optional(value, name).ValueKind == JsonValueKind.Undefined ? fallback : throw Bad("некорректное поле " + name);
    private static bool Flag(JsonElement value, string name) => Optional(value, name).ValueKind == JsonValueKind.True;
    private static JsonElement At(JsonElement array, int index) => array.ValueKind == JsonValueKind.Array && (uint)index < array.GetArrayLength() ? array[index] : throw Bad("индекс за пределами массива");
    private JsonElement At(string name, int index) => At(Optional(_root, name), index);
    private static float[] Vector(JsonElement value, string name, params float[] fallback)
    {
        var array = Optional(value, name); if (array.ValueKind == JsonValueKind.Undefined) return fallback;
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != fallback.Length) throw Bad("неверный вектор " + name);
        return array.EnumerateArray().Select(p => p.TryGetSingle(out var v) && float.IsFinite(v) ? v : throw Bad("неверное число в " + name)).ToArray();
    }
    public ImportedScan Read(IProgress<double>? progress)
    {
        try { return ReadScene(progress); }
        catch (Exception ex) when (ex is JsonException or EndOfStreamException or OverflowException or InvalidOperationException or IndexOutOfRangeException or FormatException)
        { throw Bad("файл повреждён или использует неподдерживаемое представление: " + ex.Message); }
    }
    private ImportedScan ReadScene(IProgress<double>? progress)
    {
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        if (file.Length is < 28 or > 536_870_912 || reader.ReadUInt32() != 0x46546C67 || reader.ReadUInt32() != 2 || reader.ReadUInt32() != file.Length)
            throw Bad("ожидается glTF 2.0 GLB размером до 512 МБ");
        byte[]? json = null; var hasBin = false;
        while (file.Position < file.Length)
        {
            token.ThrowIfCancellationRequested();
            var length = reader.ReadUInt32(); var type = reader.ReadUInt32();
            if (length % 4 != 0 || length > file.Length - file.Position) throw Bad("повреждён размер блока");
            if (json is null && type != 0x4E4F534A) throw Bad("первый блок должен содержать JSON");
            if (type == 0x4E4F534A)
            {
                if (json is not null || length > 16 * 1024 * 1024) throw Bad("некорректный блок JSON");
                json = reader.ReadBytes((int)length);
            }
            else if (type == 0x004E4942)
            {
                if (hasBin) throw Bad("повторный бинарный блок");
                hasBin = true; _bin = reader.ReadBytes((int)length);
            }
            else file.Seek(length, SeekOrigin.Current);
        }
        using var document = JsonDocument.Parse(json ?? throw Bad("нет JSON"), new JsonDocumentOptions { MaxDepth = 64 });
        _root = document.RootElement;
        var asset = Optional(_root, "asset");
        if (Text(asset, "version", "") != "2.0" || Text(asset, "minVersion", "2.0") != "2.0") throw Bad("поддерживается glTF 2.0");
        var required = Optional(_root, "extensionsRequired");
        if (required.ValueKind == JsonValueKind.Array)
            foreach (var extension in required.EnumerateArray())
                if (extension.GetString() is not ("KHR_materials_unlit" or "KHR_texture_transform")) throw Bad("нужен экспорт без расширения " + extension.GetString());
        var buffers = Optional(_root, "buffers");
        if (buffers.ValueKind != JsonValueKind.Array || buffers.GetArrayLength() != 1 || Optional(buffers[0], "uri").ValueKind != JsonValueKind.Undefined)
            throw Bad("поддерживаются встроенные ресурсы одного GLB");
        _bufferLength = Integer(buffers[0], "byteLength");
        if (_bufferLength < 1 || _bufferLength > _bin.Length || _bin.Length - _bufferLength > 3) throw Bad("повреждён бинарный буфер");
        ReadTextures(); ReadMaterials(); progress?.Report(10);
        var scenes = Optional(_root, "scenes");
        var scene = At(scenes, Integer(_root, "scene"));
        var roots = Optional(scene, "nodes");
        if (roots.ValueKind != JsonValueKind.Array) throw Bad("сцена не содержит узлов");
        foreach (var node in roots.EnumerateArray()) Visit(node.GetInt32(), Matrix4x4.Identity, 0);
        if (_points.Count == 0 || _indices.Count == 0) throw Bad("в выбранной сцене нет треугольных поверхностей");
        var appearance = new MeshAppearance(_uv.ToArray(), _normals.ToArray(), _vertexColors.ToArray(), _parts.ToArray(), _materials.ToArray(), _textures.ToArray());
        appearance.Validate(_points.Count, _indices.Count, token);
        var colors = new uint[_points.Count]; var colored = new bool[colors.Length]; Array.Fill(colors, 0xB5BCC3u);
        foreach (var part in appearance.Parts)
        {
            var material = appearance.Materials[part.MaterialIndex];
            for (var j = part.IndexOffset; j < part.IndexOffset + part.IndexCount; j++)
            {
                if ((j & 4095) == 0) token.ThrowIfCancellationRequested();
                var i = _indices[j]; if (colored[i]) continue; colored[i] = true;
                colors[i] = Rendering.MeshTextureSampler.Pack(Rendering.MeshTextureSampler.Color(appearance, material, appearance.TextureCoordinates[i], appearance.VertexColors[i]));
            }
        }
        var origin = _origin ?? Vector3.Zero;
        var scan = new ImportedScan { Metadata = new(Path.GetFileName(path), ScanUnit.Meters, ScanUpAxis.Y, origin.X, origin.Y, origin.Z),
            Points = _points.ToArray(), Colors = colors, Triangles = _indices.ToArray(), Appearance = appearance };
        scan.Validate(token); progress?.Report(100); return scan;
    }
    private ReadOnlySpan<byte> View(int index, int offset, int length)
    {
        var view = At("bufferViews", index); var start = Integer(view, "byteOffset"); var size = Integer(view, "byteLength");
        if (Integer(view, "buffer") != 0 || start < 0 || size < 0 || (long)start + size > _bufferLength || offset < 0 || length < 0 || (long)offset + length > size)
            throw Bad("повреждён bufferView");
        return _bin.AsSpan(start + offset, length);
    }
    private sealed record Accessor(int Count, int Components, Func<int, int, double> Value);
    private Accessor ReadAccessor(int index, string expected, bool indices = false)
    {
        var a = At("accessors", index); var count = Integer(a, "count"); var type = Integer(a, "componentType");
        var shape = Text(a, "type", ""); var components = shape switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => 0 };
        var bytes = type switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => 0 };
        var normalized = Flag(a, "normalized");
        if (count < 1 || count > (indices ? ImportedScan.MaximumTriangles * 3 : ImportedScan.MaximumPoints) || components == 0 || bytes == 0 || shape != expected ||
            indices && (normalized || type is not (5121 or 5123 or 5125)) || !indices && type == 5125)
            throw Bad("неподдерживаемый accessor");
        var elementSize = components * bytes; var viewIndex = Integer(a, "bufferView", -1); var offset = Integer(a, "byteOffset");
        var stride = viewIndex < 0 ? elementSize : Integer(At("bufferViews", viewIndex), "byteStride", elementSize);
        if (offset < 0 || offset % bytes != 0 || stride < elementSize || stride > 252 || stride % bytes != 0) throw Bad("неверное выравнивание accessor");
        if (viewIndex >= 0) _ = View(viewIndex, offset, checked((count - 1) * stride + elementSize));
        double Component(ReadOnlySpan<byte> data) => type switch {
            5120 => normalized ? Math.Max(-1, (sbyte)data[0] / 127d) : (sbyte)data[0], 5121 => normalized ? data[0] / 255d : data[0],
            5122 => normalized ? Math.Max(-1, BinaryPrimitives.ReadInt16LittleEndian(data) / 32767d) : BinaryPrimitives.ReadInt16LittleEndian(data),
            5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(data) / 65535d : BinaryPrimitives.ReadUInt16LittleEndian(data),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(data), _ => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data)) };
        var replacements = new Dictionary<int, byte[]>(); var sparse = Optional(a, "sparse");
        if (sparse.ValueKind != JsonValueKind.Undefined)
        {
            var sparseCount = Integer(sparse, "count"); var ids = Optional(sparse, "indices"); var values = Optional(sparse, "values");
            var idType = Integer(ids, "componentType"); var idBytes = idType switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => 0 };
            if (sparseCount < 1 || sparseCount > count || idBytes == 0) throw Bad("некорректный sparse accessor");
            var idData = View(Integer(ids, "bufferView"), Integer(ids, "byteOffset"), checked(sparseCount * idBytes));
            var valueData = View(Integer(values, "bufferView"), Integer(values, "byteOffset"), checked(sparseCount * elementSize)); var previous = -1;
            for (var i = 0; i < sparseCount; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                var id = idBytes == 1 ? idData[i] : idBytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(idData[(i * 2)..]) : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(idData[(i * 4)..]));
                if (id <= previous || id >= count) throw Bad("повреждены индексы sparse accessor");
                replacements[id] = valueData.Slice(i * elementSize, elementSize).ToArray(); previous = id;
            }
        }
        return new(count, components, (i, c) =>
        {
            var value = replacements.TryGetValue(i, out var replacement) ? Component(replacement.AsSpan(c * bytes)) : viewIndex < 0 ? 0 : Component(View(viewIndex, checked(offset + i * stride + c * bytes), bytes));
            return double.IsFinite(value) ? value : throw Bad("координаты содержат NaN или Infinity");
        });
    }
    private void ReadTextures()
    {
        var textures = Optional(_root, "textures"); if (textures.ValueKind == JsonValueKind.Undefined) return;
        if (textures.GetArrayLength() > 64) throw Bad("слишком много текстур");
        long total = 0;
        foreach (var texture in textures.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var image = At("images", Integer(texture, "source", -1)); var mime = Text(image, "mimeType", ""); byte[] data;
            var uri = Optional(image, "uri");
            if (uri.ValueKind == JsonValueKind.String)
            {
                var text = uri.GetString()!; var comma = text.IndexOf(',');
                if (comma < 0 || comma > 64 || text.Length > MeshAppearance.MaximumTextureBytes * 4L / 3 + 128 || !text.StartsWith("data:image/", StringComparison.Ordinal) || !text[..comma].EndsWith(";base64", StringComparison.Ordinal))
                    throw Bad("изображения должны быть встроены в GLB (PNG/JPEG)");
                mime = text[5..(comma - 7)]; data = Convert.FromBase64String(text[(comma + 1)..]);
            }
            else
            {
                var view = Integer(image, "bufferView", -1); var size = Integer(At("bufferViews", view), "byteLength");
                if (size is < 1 or > MeshAppearance.MaximumTextureBytes) throw Bad("слишком большая текстура");
                data = View(view, 0, size).ToArray();
            }
            if (mime is not ("image/png" or "image/jpeg") || data.Length is < 1 or > MeshAppearance.MaximumTextureBytes || (total += data.Length) > MeshAppearance.MaximumTotalTextureBytes)
                throw Bad("текстуры: PNG/JPEG, до 32 МБ каждая и до 128 МБ суммарно");
            var samplerId = Integer(texture, "sampler", -1); var sampler = samplerId < 0 ? default : At("samplers", samplerId);
            var value = new MeshTexture(data, mime, Integer(sampler, "wrapS", 10497), Integer(sampler, "wrapT", 10497));
            _ = Rendering.MeshTextureSampler.Inspect(value);
            _textures.Add(value);
        }
    }
    private int Texture(JsonElement material, string name)
    {
        var info = Optional(material, name); if (info.ValueKind == JsonValueKind.Undefined) return -1;
        if (Integer(Optional(Optional(info, "extensions"), "KHR_texture_transform"), "texCoord", Integer(info, "texCoord")) != 0) throw Bad("для текстур требуется TEXCOORD_0");
        var i = Integer(info, "index", -1); if ((uint)i >= _textures.Count) throw Bad("неверный индекс текстуры"); return i;
    }
    private static JsonElement[] TextureInfos(JsonElement material) => [Optional(Optional(material, "pbrMetallicRoughness"), "baseColorTexture"), Optional(material, "normalTexture"),
        Optional(Optional(material, "pbrMetallicRoughness"), "metallicRoughnessTexture"), Optional(material, "emissiveTexture"), Optional(material, "occlusionTexture")];
    private static JsonElement MasterTransform(JsonElement material) => Optional(Optional(TextureInfos(material).FirstOrDefault(t => t.ValueKind == JsonValueKind.Object), "extensions"), "KHR_texture_transform");
    private void ReadMaterials()
    {
        var materials = Optional(_root, "materials");
        if (materials.ValueKind == JsonValueKind.Array)
        {
            if (materials.GetArrayLength() > 127) throw Bad("слишком много материалов");
            foreach (var m in materials.EnumerateArray())
            {
                var pbr = Optional(m, "pbrMetallicRoughness"); var color = Vector(pbr, "baseColorFactor", 1, 1, 1, 1); var emissive = Vector(m, "emissiveFactor", 0, 0, 0);
                _materials.Add(new(Text(m, "name", "Материал GLB"), color[0], color[1], color[2], color[3], Texture(pbr, "baseColorTexture"), Scalar(pbr, "metallicFactor", 1), Scalar(pbr, "roughnessFactor", 1),
                    Texture(m, "normalTexture"), Texture(pbr, "metallicRoughnessTexture"), Texture(m, "emissiveTexture"), Texture(m, "occlusionTexture"), emissive[0], emissive[1], emissive[2],
                    Flag(m, "doubleSided"), Text(m, "alphaMode", "OPAQUE"), Scalar(m, "alphaCutoff", .5f), Optional(Optional(m, "extensions"), "KHR_materials_unlit").ValueKind != JsonValueKind.Undefined,
                    Scalar(Optional(m, "normalTexture"), "scale", 1), Scalar(Optional(m, "occlusionTexture"), "strength", 1)));
                var master = MasterTransform(m);
                float[] Signature(JsonElement t) => [..Vector(t, "offset", 0, 0), ..Vector(t, "scale", 1, 1), Scalar(t, "rotation", 0)];
                var transform = Signature(master); var samplers = new HashSet<(int,int)>();
                foreach (var info in TextureInfos(m).Where(t => t.ValueKind == JsonValueKind.Object))
                {
                    if (!transform.SequenceEqual(Signature(Optional(Optional(info, "extensions"), "KHR_texture_transform")))) throw Bad("карты одного материала должны использовать одинаковое UV-преобразование");
                    var texture = _textures[Integer(info, "index")]; samplers.Add((texture.WrapS, texture.WrapT));
                }
                if (samplers.Count > 1) throw Bad("карты одного материала должны использовать одинаковый режим повторения текстур");
            }
        }
        _defaultMaterial = _materials.Count; _materials.Add(new("Материал по умолчанию"));
    }
    private void Visit(int index, Matrix4x4 parent, int depth)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 128 || _visited.Count >= 8192 || !_visited.Add(index)) throw Bad("циклический граф или слишком много узлов");
        var node = At("nodes", index);
        if (Optional(node, "skin").ValueKind != JsonValueKind.Undefined) throw Bad("нужна статическая сетка без скелетной анимации");
        Matrix4x4 local;
        if (Optional(node, "matrix").ValueKind != JsonValueKind.Undefined)
        {
            if (new[] { "translation", "rotation", "scale" }.Any(n => Optional(node, n).ValueKind != JsonValueKind.Undefined)) throw Bad("узел содержит одновременно matrix и TRS");
            var m = Vector(node, "matrix", new float[16]); local = new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }
        else
        {
            var t = Vector(node, "translation", 0, 0, 0); var r = Vector(node, "rotation", 0, 0, 0, 1); var s = Vector(node, "scale", 1, 1, 1);
            var q = new Quaternion(r[0], r[1], r[2], r[3]); if (Math.Abs(q.LengthSquared() - 1) > .001) throw Bad("некорректный поворот узла");
            local = Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
        }
        var world = local * parent;
        if (!Matrix4x4.Invert(world, out var inverse) || Math.Abs(world.M14) > 1e-6 || Math.Abs(world.M24) > 1e-6 || Math.Abs(world.M34) > 1e-6 || Math.Abs(world.M44 - 1) > 1e-6) throw Bad("необратимое или перспективное преобразование узла");
        if (Optional(node, "mesh").ValueKind != JsonValueKind.Undefined) Mesh(Integer(node, "mesh"), world, Matrix4x4.Transpose(inverse));
        var children = Optional(node, "children"); if (children.ValueKind == JsonValueKind.Array) foreach (var child in children.EnumerateArray()) Visit(child.GetInt32(), world, depth + 1);
    }
    private void Mesh(int meshIndex, Matrix4x4 transform, Matrix4x4 normalTransform)
    {
        var mesh = At("meshes", meshIndex); var primitives = Optional(mesh, "primitives");
        foreach (var primitive in primitives.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (_parts.Count >= 1024 || Optional(Optional(primitive, "extensions"), "KHR_draco_mesh_compression").ValueKind != JsonValueKind.Undefined)
                throw Bad("нужна сетка без Draco-сжатия, максимум 1024 группы материалов");
            var attributes = Optional(primitive, "attributes"); var position = ReadAccessor(Integer(attributes, "POSITION", -1), "VEC3");
            var normal = Optional(attributes, "NORMAL").ValueKind == JsonValueKind.Undefined ? null : ReadAccessor(Integer(attributes, "NORMAL"), "VEC3");
            var uv = Optional(attributes, "TEXCOORD_0").ValueKind == JsonValueKind.Undefined ? null : ReadAccessor(Integer(attributes, "TEXCOORD_0"), "VEC2");
            var colorIndex = Integer(attributes, "COLOR_0", -1); var color = colorIndex < 0 ? null : ReadAccessor(colorIndex, Text(At("accessors", colorIndex), "type", ""));
            var materialId = Integer(primitive, "material", _defaultMaterial);
            if ((uint)materialId > _defaultMaterial || materialId == _defaultMaterial && Optional(primitive, "material").ValueKind != JsonValueKind.Undefined ||
                normal is not null && normal.Count != position.Count || uv is not null && uv.Count != position.Count || color is not null && (color.Count != position.Count || color.Components is not (3 or 4))) throw Bad("неверный материал или размеры атрибутов сетки");
            var material = _materials[materialId];
            if (new[] { material.BaseColorTexture, material.NormalTexture, material.MetallicRoughnessTexture, material.EmissiveTexture, material.OcclusionTexture }.Any(i => i >= 0) && uv is null) throw Bad("материал с текстурой не содержит TEXCOORD_0");
            if ((long)_points.Count + position.Count > ImportedScan.MaximumPoints) throw Bad("больше 5 млн вершин; упростите модель перед экспортом");
            var start = _points.Count; var firstIndex = _indices.Count;
            var textureTransform = materialId == _defaultMaterial ? default : MasterTransform(At("materials", materialId));
            if (Integer(textureTransform, "texCoord") != 0) throw Bad("преобразование текстуры требует TEXCOORD_0");
            var uvOffset = Vector(textureTransform, "offset", 0, 0); var uvScale = Vector(textureTransform, "scale", 1, 1); var angle = Scalar(textureTransform, "rotation", 0);
            for (var i = 0; i < position.Count; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                var p = Vector3.Transform(new((float)position.Value(i, 0), (float)position.Value(i, 1), (float)position.Value(i, 2)), transform);
                if (!float.IsFinite(p.X + p.Y + p.Z) || Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) > 1e12) throw Bad("некорректные координаты узла");
                _origin ??= localOrigin ? p : Vector3.Zero; p -= _origin.Value;
                _points.Add(new(p.X, -p.Z, p.Y, 1));
                var n = normal is null ? Vector3.Zero : Vector3.TransformNormal(new((float)normal.Value(i, 0), (float)normal.Value(i, 1), (float)normal.Value(i, 2)), normalTransform);
                n = n.LengthSquared() < 1e-16f ? Vector3.Zero : Vector3.Normalize(n); _normals.Add(new(n.X, -n.Z, n.Y));
                var u = uv is null ? 0 : (float)uv.Value(i, 0) * uvScale[0]; var v = uv is null ? 0 : (float)uv.Value(i, 1) * uvScale[1];
                _uv.Add(new(uvOffset[0] + MathF.Cos(angle) * u - MathF.Sin(angle) * v, uvOffset[1] + MathF.Sin(angle) * u + MathF.Cos(angle) * v));
                _vertexColors.Add(color is null ? Vector4.One : new((float)color.Value(i, 0), (float)color.Value(i, 1), (float)color.Value(i, 2), color.Components == 4 ? (float)color.Value(i, 3) : 1));
            }
            if (material.BaseColorTexture >= 0 && color is not null && _vertexColors.Skip(start).Any(c => c != Vector4.One))
            {
                var constant = _vertexColors[start];
                if (_vertexColors.Skip(start).Any(c => c != constant)) throw Bad("экспортируйте текстурированную модель без дополнительного переменного COLOR_0");
                material = material with { Red = material.Red * constant.X, Green = material.Green * constant.Y, Blue = material.Blue * constant.Z, Alpha = material.Alpha * constant.W };
                materialId = _materials.Count; _materials.Add(material);
                for (var i = start; i < _vertexColors.Count; i++) _vertexColors[i] = Vector4.One;
            }
            var input = Optional(primitive, "indices").ValueKind == JsonValueKind.Undefined ? null : ReadAccessor(Integer(primitive, "indices"), "SCALAR", true);
            var count = input?.Count ?? position.Count; var mode = Integer(primitive, "mode", 4);
            if (mode is not (4 or 5 or 6) || count < 3 || mode == 4 && count % 3 != 0) throw Bad("поддерживаются треугольники, triangle strip и triangle fan");
            int Id(int i) { var value = input is null ? i : checked((int)input.Value(i, 0)); if ((uint)value >= position.Count) throw Bad("индекс вершины за пределами сетки"); return start + value; }
            var mirrored = transform.GetDeterminant() < 0;
            for (var i = mode == 4 ? 0 : 2; i < count; i += mode == 4 ? 3 : 1)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                var a = Id(mode == 4 ? i : mode == 6 ? 0 : i - 2); var b = Id(mode == 4 ? i + 1 : i - 1); var c = Id(mode == 4 ? i + 2 : i);
                if (mode == 5 && i % 2 != 0) (a, b) = (b, a); if (mirrored) (b, c) = (c, b);
                if (a == b || b == c || a == c) continue;
                if (_indices.Count / 3 >= ImportedScan.MaximumTriangles) throw Bad("больше 5 млн треугольников");
                _indices.Add(a); _indices.Add(b); _indices.Add(c);
                if (normal is null) { var n = Vector3.Cross(_points[b].Position - _points[a].Position, _points[c].Position - _points[a].Position); _normals[a] += n; _normals[b] += n; _normals[c] += n; }
            }
            if (normal is null) for (var i = start; i < _points.Count; i++) _normals[i] = _normals[i].LengthSquared() < 1e-16f ? Vector3.UnitZ : Vector3.Normalize(_normals[i]);
            if (_indices.Count == firstIndex) throw Bad("пустая группа треугольников");
            _parts.Add(new(firstIndex, _indices.Count - firstIndex, materialId));
        }
    }
}
