using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>A supervised correction to the detector log odds. Only explicit operator labels enter the fit.</summary>
public sealed class PersonCandidateLearning(string? path = null)
{
    private sealed record Example(string Key, double[] Features, float DetectorScore, bool Person, string[]? Sources = null);
    private sealed record Archive(int Version, Example[] Examples);
    internal const int FeatureCount = 9;
    private const int MaximumExamples = 2048;
    private const double MaxCorrection = 1.25;
    private readonly ConditionalWeakTable<ScanImage, byte[]> _imageHashes = new();
    private Dictionary<string, Example> _examples = [];
    public int Positives => _examples.Values.Count(s => s.Person);
    public int Negatives => _examples.Count - Positives;
    public int TaggedPositives => _examples.Values.Count(s => s.Person && s.Sources is { Length: > 0 });
    public int TaggedNegatives => _examples.Values.Count(s => !s.Person && s.Sources is { Length: > 0 });
    public int SourceCount => _examples.Values.SelectMany(s => s.Sources ?? []).Distinct().Count();
    public bool Ready => TaggedPositives >= 3 && TaggedNegatives >= 3;

    private Dictionary<string, Example> Read()
    {
        if (path is null) return _examples;
        using var stream = File.OpenRead(path);
        if (stream.Length > 6_000_000) throw new InvalidDataException("Слишком большой файл примеров.");
        var data = JsonSerializer.Deserialize<Archive>(stream);
        if (data?.Version is not (1 or 2) || data.Examples is null || data.Examples.Length > MaximumExamples)
            throw new InvalidDataException("Неизвестный формат примеров обучения.");
        var examples = new Dictionary<string, Example>();
        foreach (var s in data.Examples)
        {
            if (s is null || s.Key is not { Length: 64 } || !s.Key.All(Uri.IsHexDigit) ||
                s.Features is not { Length: FeatureCount } || s.Features.Any(x => !double.IsFinite(x) || Math.Abs(x) > 1) ||
                !float.IsFinite(s.DetectorScore) || s.DetectorScore is < 0 or > 1 || !ValidSources(s.Sources) || !examples.TryAdd(s.Key, s))
                throw new InvalidDataException("Повреждён пример обучения.");
        }
        return examples;
    }

    public void Reload()
    {
        try { _examples = Read(); }
        catch (FileNotFoundException) { _examples = []; }
        catch (DirectoryNotFoundException) { _examples = []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _examples = [];
            throw;
        }
    }

    private static bool ValidSources(string[]? sources) => sources is null || (sources.Length <= ScanProject.MaximumObjects &&
        sources.All(s => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')) &&
        sources.Distinct().Count() == sources.Length);

    public void Review(ScanImage view, PersonFinding finding, StructureReviewState label, string[]? sources = null)
    {
        if (!ValidSources(sources)) throw new InvalidDataException("Некорректные идентификаторы сканов.");
        if (path is not null) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var gate = path is null ? null : new FileStream(path + ".lock", FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        Reload();
        var next = new Dictionary<string, Example>(_examples);
        var key = Key(view, finding.Box);
        if (label == StructureReviewState.Proposed) next.Remove(key);
        else
        {
            var provenance = sources is { Length: > 0 } ? sources : next.GetValueOrDefault(key)?.Sources;
            next[key] = new(key, Features(view, finding), finding.DetectorScore ?? finding.Box.Score,
                label == StructureReviewState.Confirmed, provenance?.ToArray());
        }
        if (next.Count > MaximumExamples) throw new InvalidDataException($"Сохранено максимальное число примеров ({MaximumExamples}).");
        if (path is not null)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, new Archive(2, next.Values.ToArray()));
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        _examples = next;
    }

    private string Key(ScanImage view, ImageBox box)
    {
        Span<byte> key = stackalloc byte[48];
        _imageHashes.GetValue(view, v => SHA256.HashData(v.Rgb)).AsSpan().CopyTo(key);
        BinaryPrimitives.WriteInt32LittleEndian(key[32..], (int)Math.Round(box.Left));
        BinaryPrimitives.WriteInt32LittleEndian(key[36..], (int)Math.Round(box.Top));
        BinaryPrimitives.WriteInt32LittleEndian(key[40..], (int)Math.Round(box.Right));
        BinaryPrimitives.WriteInt32LittleEndian(key[44..], (int)Math.Round(box.Bottom));
        return Convert.ToHexString(SHA256.HashData(key));
    }

    private static double[] Features(ScanImage view, PersonFinding f)
    {
        var box = f.Box;
        var width = Math.Max(1, box.Right - box.Left);
        var height = Math.Max(1, box.Bottom - box.Top);
        double sum = 0, squares = 0, horizontal = 0, vertical = 0;
        int measured = 0;
        Span<double> row = stackalloc double[12];
        for (var y = 0; y < 12; y++)
        {
            double previous = 0;
            for (var x = 0; x < 12; x++)
            {
                var px = (int)Math.Clamp(box.Left + (x + .5) * width / 12, 0, view.Size - 1);
                var py = (int)Math.Clamp(box.Top + (y + .5) * height / 12, 0, view.Size - 1);
                var pixel = py * view.Size + px;
                var offset = 3 * pixel;
                var light = (.299 * view.Rgb[offset] + .587 * view.Rgb[offset + 1] + .114 * view.Rgb[offset + 2]) / 255;
                sum += light; squares += light * light;
                if (x > 0) horizontal += Math.Abs(light - previous);
                if (y > 0) vertical += Math.Abs(light - row[x]);
                row[x] = previous = light;
                if (float.IsFinite(view.Depth[pixel])) measured++;
            }
        }
        var extent = f.Maximum - f.Minimum;
        return [1, Math.Clamp(Math.Log(height / width) / 2, -1, 1),
            Math.Clamp(2 * Math.Sqrt(Math.Max(0, squares / 144 - Math.Pow(sum / 144, 2))), 0, 1),
            Math.Min(1, horizontal / 33), Math.Min(1, vertical / 33), 2 * measured / 144d - 1,
            extent.HasValue ? 1 : 0, Math.Clamp((extent?.Z ?? 0) - 1, -1, 1),
            Math.Clamp(Math.Max(extent?.X ?? 0, extent?.Y ?? 0) - .75, -1, 1)];
    }

    private static double LogOdds(float score)
    {
        var p = Math.Clamp((double)score, .0001, .9999);
        return Math.Log(p / (1 - p));
    }
    private static double Probability(double logOdds) => 1 / (1 + Math.Exp(-logOdds));
    public static float ProposalThreshold(float threshold) =>
        Math.Max(.1f, (float)Probability(LogOdds(threshold) - MaxCorrection));

    public PersonTrainingRun Train(float threshold, IProgress<double>? progress, CancellationToken token)
    {
        if (!float.IsFinite(threshold) || threshold is < .1f or > .9f) throw new ArgumentOutOfRangeException(nameof(threshold));
        var tagged = _examples.Values.Where(s => s.Sources is { Length: > 0 }).OrderBy(s => s.Key).ToArray();
        static bool Enough(Example[] rows) => rows.Count(s => s.Person) >= 3 && rows.Count(s => !s.Person) >= 3;
        if (!Enough(tagged)) throw new InvalidOperationException("Для обучения нужны минимум три примера каждого класса с известным сканом.");
        var training = tagged;
        Example[] validation = [];
        // Only single-source examples can enter validation. Remove every training example that saw that scan.
        foreach (var group in tagged.Where(s => s.Sources!.Length == 1).GroupBy(s => s.Sources![0])
                     .Where(g => g.Any(s => s.Person) && g.Any(s => !s.Person))
                     .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var candidates = tagged.Where(s => !s.Sources!.Contains(group.Key)).ToArray();
            if (!Enough(candidates)) continue;
            training = candidates;
            validation = group.ToArray();
            break;
        }
        var (weights, loss) = Fit(training, progress, token);
        token.ThrowIfCancellationRequested();
        return new(Guid.NewGuid(), DateTimeOffset.UtcNow, weights, loss, training.Length, validation.Length, threshold,
            validation.Length > 0 ? Evaluate(validation, threshold, null) : null,
            validation.Length > 0 ? Evaluate(validation, threshold, weights) : null);
    }

    private static PersonTrainingMetrics Evaluate(Example[] rows, float threshold, double[]? weights)
    {
        var tp = 0; var fp = 0; var fn = 0; var tn = 0;
        foreach (var row in rows)
        {
            var predicted = Score(row.DetectorScore, row.Features, weights) >= threshold;
            if (predicted && row.Person) tp++;
            else if (predicted) fp++;
            else if (row.Person) fn++;
            else tn++;
        }
        return new(tp, fp, fn, tn);
    }

    // Class-balanced logistic loss with a regularizer around the original YOLOX estimate.
    private static (double[] Weights, double[] Loss) Fit(Example[] training, IProgress<double>? progress, CancellationToken token)
    {
        const int steps = 100;
        var weights = new double[FeatureCount];
        var loss = new double[steps / 5 + 1];
        var positives = training.Count(s => s.Person);
        var negatives = training.Length - positives;
        var rows = training.Select(s => (s, prior: LogOdds(s.DetectorScore),
            weight: .5 / (s.Person ? positives : negatives))).ToArray();
        var gradient = new double[FeatureCount];
        for (var epoch = 0; epoch <= steps; epoch++)
        {
            token.ThrowIfCancellationRequested();
            Array.Clear(gradient);
            double objective = .03 * Dot(weights, weights);
            foreach (var (s, prior, weight) in rows)
            {
                var probability = Probability(prior + Dot(weights, s.Features));
                var error = (probability - (s.Person ? 1 : 0)) * weight;
                if (epoch % 5 == 0) objective -= weight * Math.Log(Math.Max(1e-12, s.Person ? probability : 1 - probability));
                for (var j = 0; j < FeatureCount; j++) gradient[j] += error * s.Features[j];
            }
            if (epoch % 5 == 0) { loss[epoch / 5] = objective; progress?.Report(epoch); }
            if (epoch == steps) break;
            for (var j = 0; j < FeatureCount; j++) weights[j] -= .45 * (gradient[j] + .06 * weights[j]);
        }
        return (weights, loss);
    }

    private static double Dot(double[] weights, double[] features)
    {
        double value = 0;
        for (var i = 0; i < weights.Length; i++) value += weights[i] * features[i];
        return value;
    }

    private static float Score(float original, double[] features, double[]? weights) => weights is null ? original :
        (float)Probability(LogOdds(original) + Math.Clamp(Dot(weights, features), -MaxCorrection, MaxCorrection));

    public PersonDetectionResult Apply(PersonDetectionResult result, float threshold, PersonTrainingRun? model, CancellationToken token)
    {
        if (_examples.Count == 0 && model is null) return result;
        var findings = new List<PersonFinding>();
        foreach (var f in result.Findings)
        {
            token.ThrowIfCancellationRequested();
            var view = result.Views[f.ViewIndex];
            var score = f.Box.Score;
            if (model is not null) score = Score(score, Features(view, f), model.Weights);
            if (score < threshold) continue;
            var review = _examples.TryGetValue(Key(view, f.Box), out var example)
                ? (example.Person ? StructureReviewState.Confirmed : StructureReviewState.Rejected) : f.Review;
            findings.Add(f with { DetectorScore = f.Box.Score, Box = f.Box with { Score = score }, Review = review });
        }
        return result with { Findings = findings.ToArray() };
    }
}
