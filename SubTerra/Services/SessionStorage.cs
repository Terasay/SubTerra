using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SubTerra.Models;
using SubTerra.Simulation;

namespace SubTerra.Services;

public sealed class SessionStorage
{
    private sealed record Manifest(int SchemaVersion, string SourceType, string CoordinateUnit,
        string ScenarioId, ScanSettings Settings, int FrameCount, int PointCount, int SceneVersion = 1,
        ScannerAdjustment[]? Adjustments = null);
    private const int MaximumPoints = 750_000;

    public Task SaveAsync(string path, ScanSession session, int frameCount) => Task.Run(() =>
    {
        if (!SceneVersions.IsSupported(session.SceneVersion)) throw new InvalidDataException("Неизвестная версия виртуальной сцены.");
        var frames = session.Frames.Take(frameCount).ToArray();
        var adjustments = session.Adjustments.Where(a => a.FirstFrame <= frames.Length).ToArray();
        ScannerPlan.Validate(adjustments, session.Settings, frames.Length);
        var manifest = new Manifest(adjustments.Length > 0 ? 2 : 1, "simulation", "m", session.ScenarioId, session.Settings,
            frames.Length, frames.Sum(f => f.Points.Length), session.SceneVersion, adjustments);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                using (var stream = archive.CreateEntry("manifest.json").Open())
                    JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions { WriteIndented = true });
                using var data = archive.CreateEntry("frames.bin", CompressionLevel.Fastest).Open();
                using var writer = new BinaryWriter(data);
                foreach (var frame in frames)
                {
                    writer.Write(frame.Timestamp.ToUnixTimeMilliseconds());
                    WriteVector(writer, frame.Pose.Position);
                    var q = frame.Pose.Orientation;
                    writer.Write(q.X); writer.Write(q.Y); writer.Write(q.Z); writer.Write(q.W);
                    writer.Write(frame.Progress);
                    writer.Write(frame.Points.Length);
                    foreach (var point in frame.Points)
                    {
                        writer.Write(point.X); writer.Write(point.Y);
                        writer.Write(point.Z); writer.Write(point.Intensity);
                    }
                }
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    });

    public Task<ScanSession> LoadAsync(string path) => Task.Run(() =>
    {
        using var archive = ZipFile.OpenRead(path);
        var metadata = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("В файле нет описания сеанса.");
        if (metadata.Length > 262_144) throw new InvalidDataException("Описание сеанса слишком большое.");
        Manifest manifest;
        using (var stream = metadata.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(stream) ?? throw new InvalidDataException("Не удалось прочитать сеанс.");
        if (manifest.SchemaVersion is not (1 or 2) || manifest.SourceType != "simulation" || manifest.CoordinateUnit != "m")
            throw new InvalidDataException("Версия или тип сеанса не поддерживается.");
        if (!SceneVersions.IsSupported(manifest.SceneVersion))
            throw new InvalidDataException("Сеанс использует другую версию виртуальной пещеры. Создайте новый проход в текущем прототипе.");
        if (!ScenarioDefinition.All.Any(s => s.Id == manifest.ScenarioId) || manifest.FrameCount is < 1 or > 360 ||
            manifest.PointCount is < 0 or > MaximumPoints || manifest.Settings is null ||
            manifest.Settings.RangeMeters is < 1 or > 100 || !float.IsFinite(manifest.Settings.RangeMeters) ||
            manifest.Settings.FrameCount is < 2 or > 360 || manifest.Settings.RaysPerFrame is < 32 or > 2048 ||
            manifest.FrameCount > manifest.Settings.FrameCount ||
            !float.IsFinite(manifest.Settings.StartProgress) || manifest.Settings.StartProgress is < 0 or > .85f ||
            manifest.Settings.FrameIntervalMilliseconds is < 25 or > 1000)
            throw new InvalidDataException("Файл содержит некорректные параметры.");
        var adjustments = manifest.Adjustments ?? [];
        if (manifest.SchemaVersion == 1 && adjustments.Length > 0) throw new InvalidDataException("Неверная версия плана сканирования.");
        ScannerPlan.Validate(adjustments, manifest.Settings, manifest.FrameCount);
        var entry = archive.GetEntry("frames.bin") ?? throw new InvalidDataException("В файле отсутствуют измерения.");
        if (entry.Length != manifest.FrameCount * 44L + manifest.PointCount * 16L)
            throw new InvalidDataException("Размер данных не соответствует описанию сеанса.");
        using var data = entry.Open();
        using var reader = new BinaryReader(data);
        var frames = new ScanFrame[manifest.FrameCount];
        var validatedMap = new MapAccumulator(manifest.ScenarioId, manifest.Settings, manifest.SceneVersion);
        var total = 0;
        var previousProgress = manifest.Settings.StartProgress;
        for (var i = 0; i < frames.Length; i++)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64());
            var position = ReadVector(reader);
            var q = new Quaternion(ReadFinite(reader), ReadFinite(reader), ReadFinite(reader), ReadFinite(reader));
            var progress = ReadFinite(reader);
            var count = reader.ReadInt32();
            if (count < 0 || count > 2048 || total + count > manifest.PointCount ||
                progress < previousProgress || progress is <= 0 or > 1 ||
                q.LengthSquared() is < .9f or > 1.1f)
                throw new InvalidDataException("Измерения в файле повреждены.");
            var points = new ScanPoint[count];
            for (var j = 0; j < count; j++)
                points[j] = new(ReadFinite(reader), ReadFinite(reader), ReadFinite(reader), ReadFinite(reader));
            frames[i] = new(time, new(position, q), progress, points);
            validatedMap.Append(frames[i]);
            total += count;
            previousProgress = progress;
        }
        if (total != manifest.PointCount) throw new InvalidDataException("Количество точек в файле не совпадает.");
        return new ScanSession { ScenarioId = manifest.ScenarioId, SceneVersion = manifest.SceneVersion,
            Settings = manifest.Settings, Frames = frames, Adjustments = adjustments };
    });

    public Task ExportPlyAsync(string path, IEnumerable<ScanPoint> points) => Task.Run(() =>
    {
        var snapshot = points.ToArray();
        foreach (var p in snapshot)
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) ||
                !float.IsFinite(p.Intensity) || p.Intensity is < 0 or > 1)
                throw new InvalidDataException("Облако содержит некорректные координаты или интенсивность.");

        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Finish and close the new file before replacing a previous export.
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("ply\nformat ascii 1.0\ncomment Subterra simulated LiDAR scan; units meters");
                writer.WriteLine($"element vertex {snapshot.Length}");
                writer.WriteLine("property float x\nproperty float y\nproperty float z\nproperty float intensity\nend_header");
                foreach (var p in snapshot)
                    writer.WriteLine(FormattableString.Invariant($"{p.X:R} {p.Y:R} {p.Z:R} {p.Intensity:R}"));
            }
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    });

    private static void WriteVector(BinaryWriter writer, Vector3 p)
    { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
    private static Vector3 ReadVector(BinaryReader reader) => new(ReadFinite(reader), ReadFinite(reader), ReadFinite(reader));
    private static float ReadFinite(BinaryReader reader)
    {
        var value = reader.ReadSingle();
        if (!float.IsFinite(value) || MathF.Abs(value) > 100_000)
            throw new InvalidDataException("Некорректная координата в файле.");
        return value;
    }
}
