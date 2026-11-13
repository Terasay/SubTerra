using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SubTerra.Models;

namespace SubTerra.Services;

public static class SceneUnderstandingService
{
    public const int MaximumPoints = 120_000;
    public const string ModelName = "PTv3 · ScanNet-20";
    public static string RuntimeDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SubTerra", "SceneAi");
    public static string PythonPath => Path.Combine(RuntimeDirectory, ".venv", "Scripts", "python.exe");
    public static string WorkerPath => Path.Combine(AppContext.BaseDirectory, "Assets", "SceneAi", "segment.py");
    public static bool IsInstalled => File.Exists(PythonPath) && File.Exists(Path.Combine(RuntimeDirectory, "ready.json")) && File.Exists(WorkerPath);
    private sealed record ColorDigest(string Value);
    private static readonly ConditionalWeakTable<ImportedScan, ColorDigest> ColorHashes = new();
    private sealed record Prediction(int Version, string Model, byte[] Labels, float[] Scores);

    public static string ColorsHash(ScanProject project)
    {
        var hashes = project.Objects.OrderBy(o => o.Id).Select(o => ColorHashes.GetValue(o.Source, scan =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var bytes = new byte[4096];
            for (var offset = 0; offset < scan.Colors.Length; offset += bytes.Length / 4)
            {
                var count = Math.Min(bytes.Length / 4, scan.Colors.Length - offset);
                Buffer.BlockCopy(scan.Colors, offset * 4, bytes, 0, count * 4);
                hash.AppendData(bytes, 0, count * 4);
            }
            return new ColorDigest(Convert.ToHexString(hash.GetHashAndReset()));
        }).Value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", hashes))));
    }

    public static SceneUnderstandingDocument Refresh(ScanProject project, SceneUnderstandingDocument document) => document with
    {
        IsStale = !document.Inputs.SequenceEqual(StructureProvenance.CaptureInputs(project)) || document.ColorsHash != ColorsHash(project)
    };

    public static async Task<SceneUnderstandingDocument> AnalyzeAsync(ScanProject project, IProgress<double>? progress, CancellationToken token)
    {
        if (!IsInstalled) throw new InvalidOperationException("Установите локальную модель с помощью scripts/Setup-SceneAi.ps1 и повторите анализ.");
        project.Validate(token);
        var inputs = StructureProvenance.CaptureInputs(project, token);
        var total = project.Objects.Where(o => o.IsVisible).Sum(o => o.PointCount);
        if (total < 64) throw new InvalidOperationException("Для анализа нужно хотя бы 64 видимые точки.");
        var count = Math.Min(total, MaximumPoints);
        var sources = new List<SourcePointReference>(count);
        var positions = new List<Vector3>(count);
        var directory = Path.Combine(Path.GetTempPath(), "SubTerra-SceneAi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.bin");
            var outputPath = Path.Combine(directory, "output.json");
            using (var writer = new BinaryWriter(File.Create(inputPath)))
            {
                var ordinal = 0;
                foreach (var obj in project.Objects.Where(o => o.IsVisible))
                {
                    var matrix = obj.Matrix;
                    for (var i = 0; i < obj.Source.Points.Length; i++)
                    {
                        if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                        if (!obj.Includes(i)) continue;
                        if (sources.Count < count && ordinal == (long)sources.Count * total / count)
                        {
                            var p = Vector3.Transform(obj.Source.Points[i].Position, matrix);
                            var color = obj.Source.Colors.Length > 0 ? obj.Source.Colors[i] : 0x808080u;
                            sources.Add(new(obj.Id, i)); positions.Add(p);
                            writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z);
                            writer.Write((float)(color >> 16 & 255)); writer.Write((float)(color >> 8 & 255)); writer.Write((float)(color & 255));
                        }
                        ordinal++;
                    }
                }
            }
            progress?.Report(10);
            var start = new ProcessStartInfo(PythonPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(WorkerPath);
            start.ArgumentList.Add(inputPath); start.ArgumentList.Add(outputPath);
            start.Environment["HF_HOME"] = Path.Combine(RuntimeDirectory, "models");
            start.Environment["HF_HUB_OFFLINE"] = "1";
            start.Environment["PYTHONUTF8"] = "1";
            using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить локальный анализ.");
            using var cancellation = token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var errorTask = process.StandardError.ReadToEndAsync();
            var outputTask = ReadProgress(process.StandardOutput, progress);
            await process.WaitForExitAsync(CancellationToken.None);
            var error = await errorTask;
            await outputTask;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException("Локальная модель завершилась с ошибкой. " + error[..Math.Min(error.Length, 1600)]);
            var file = new FileInfo(outputPath);
            if (!file.Exists || file.Length > 8_000_000) throw new InvalidDataException("Модель вернула некорректный результат.");
            var prediction = JsonSerializer.Deserialize<Prediction>(await File.ReadAllTextAsync(outputPath, token));
            if (prediction is null || prediction.Version != 1 || prediction.Model != "ptv3_scannet" || prediction.Labels is null || prediction.Scores is null || prediction.Labels.Length != count || prediction.Scores.Length != count ||
                prediction.Labels.Any(c => c > 20) || prediction.Scores.Any(c => !float.IsFinite(c) || c is < 0 or > 1))
                throw new InvalidDataException("Классы модели не соответствуют точкам скана.");
            var result = Build(project, inputs, sources.ToArray(), positions.ToArray(), prediction.Labels, prediction.Scores, token);
            progress?.Report(100);
            return result;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task ReadProgress(StreamReader reader, IProgress<double>? progress)
    {
        while (await reader.ReadLineAsync() is { } line)
            if (line.StartsWith("PROGRESS ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(9), out var value)) progress?.Report(Math.Clamp(value, 10, 95));
    }

    internal static SceneUnderstandingDocument Build(ScanProject project, StructureInputStamp[] inputs, SourcePointReference[] sources,
        Vector3[] positions, byte[] labels, float[] scores, CancellationToken token)
    {
        // Connected regions are proposals for review, not learned instance identities.
        var cells = new Dictionary<(byte Class, int X, int Y, int Z), List<int>>();
        for (var i = 0; i < sources.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var p = positions[i];
            var key = (labels[i], (int)MathF.Floor(p.X / .2f), (int)MathF.Floor(p.Y / .2f), (int)MathF.Floor(p.Z / .2f));
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            list.Add(i);
        }
        var seen = new HashSet<(byte Class, int X, int Y, int Z)>();
        var regions = new List<SceneRegion>();
        var points = new ScenePoint[sources.Length];
        var queue = new Queue<(byte Class, int X, int Y, int Z)>();
        foreach (var start in cells.Keys)
        {
            if (!seen.Add(start)) continue;
            var members = new List<int>(); queue.Enqueue(start);
            while (queue.TryDequeue(out var key))
            {
                token.ThrowIfCancellationRequested();
                members.AddRange(cells[key]);
                for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
                {
                    var next = (key.Class, key.X + x, key.Y + y, key.Z + z);
                    if (cells.ContainsKey(next) && seen.Add(next)) queue.Enqueue(next);
                }
            }
            // Small isolated components remain visible as unknown and can be labelled through a box.
            var id = members.Count >= 12 && regions.Count < 256 ? regions.Count + 1 : 0;
            if (id > 0) regions.Add(new(id, (SceneClass)start.Class, members.Count));
            foreach (var index in members) points[index] = new(sources[index], (SceneClass)labels[index], scores[index], id);
        }
        return new(1, ModelName, DateTimeOffset.UtcNow, inputs, ColorsHash(project), points, regions.ToArray());
    }

    public static void Validate(ScanProject project, SceneUnderstandingDocument document)
    {
        static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        if (document.Version != 1 || document.Model != ModelName || document.CreatedAt < DateTimeOffset.UnixEpoch ||
            document.Inputs is null || document.Inputs.Length > ScanProject.MaximumObjects || document.Inputs.Any(i => i is null) ||
            document.Inputs.Select(i => i.ObjectId).Distinct().Count() != document.Inputs.Length ||
            document.Inputs.Any(i => i.ObjectId == Guid.Empty || i.PointCount is < 1 or > ImportedScan.MaximumPoints ||
                i.IncludedCount < 0 || i.IncludedCount > i.PointCount || !Hash(i.PointsHash) ||
                (i.MaskHash != "all" && !Hash(i.MaskHash)) || (i.MaskHash == "all" && i.IncludedCount != i.PointCount) || i.Transform is null) ||
            document.Inputs.Sum(i => (long)i.PointCount) > ImportedScan.MaximumPoints || !Hash(document.ColorsHash) ||
            document.Points is null || document.Points.Length is < 1 or > MaximumPoints || document.Points.Any(p => p is null) ||
            document.Regions is null || document.Regions.Length > 512 || document.Regions.Any(r => r is null) ||
            document.Regions.Select(r => r.Id).Distinct().Count() != document.Regions.Length)
            throw new InvalidDataException("Повреждена разметка сцены.");
        foreach (var input in document.Inputs) input.Transform.Validate();
        var inputs = document.Inputs.ToDictionary(i => i.ObjectId);
        var objects = project.Objects.ToDictionary(o => o.Id);
        var regions = document.Regions.ToDictionary(r => r.Id);
        var references = new HashSet<SourcePointReference>();
        foreach (var point in document.Points)
        {
            if (!references.Add(point.Source) || !Enum.IsDefined(point.SuggestedClass) || point.SuggestedClass > SceneClass.OtherFurniture ||
                !float.IsFinite(point.Score) || point.Score is < 0 or > 1 || point.RegionId < 0 || point.RegionId != 0 && !regions.ContainsKey(point.RegionId) ||
                !inputs.TryGetValue(point.Source.ObjectId, out var stamp) || !stamp.IsVisible || point.Source.PointIndex < 0 || point.Source.PointIndex >= stamp.PointCount ||
                !document.IsStale && (!objects.TryGetValue(point.Source.ObjectId, out var obj) || !obj.IsVisible ||
                    point.Source.PointIndex >= obj.Source.Points.Length || !obj.Includes(point.Source.PointIndex)))
                throw new InvalidDataException("Некорректная привязка разметки к исходному скану.");
        }
        var counts = document.Points.GroupBy(p => p.RegionId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var region in document.Regions)
            if (region.Id < 1 || region.PointCount != counts.GetValueOrDefault(region.Id) || region.PointCount < 1 ||
                !Enum.IsDefined(region.SuggestedClass) || region.ReviewedClass is { } reviewed && !Enum.IsDefined(reviewed))
                throw new InvalidDataException("Некорректная область разметки.");
    }
}
