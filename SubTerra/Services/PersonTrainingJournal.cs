using System.IO;
using System.Text.Json;

namespace SubTerra.Services;

public sealed record PersonTrainingMetrics(int TruePositive, int FalsePositive, int FalseNegative, int TrueNegative)
{
    public double BalancedAccuracy => .5 * ((double)TruePositive / (TruePositive + FalseNegative) +
        (double)TrueNegative / (TrueNegative + FalsePositive));
}

public sealed record PersonTrainingRun(Guid Id, DateTimeOffset TrainedAt, double[] Weights, double[] Loss,
    int TrainingCount, int ValidationCount, float Threshold, PersonTrainingMetrics? Baseline, PersonTrainingMetrics? Trained,
    string? Note = null);

public sealed class PersonTrainingJournal(string? path, string? bundledPath = null)
{
    private const int MaximumRuns = 10;
    private sealed record Archive(int Version, Guid? ActiveId, PersonTrainingRun[] Runs);
    private Archive _archive = new(1, null, []);
    private PersonTrainingRun[] _bundled = [];
    public static string BundledModelsPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "person-models.shared.json");
    public IReadOnlyList<PersonTrainingRun> Runs => _archive.Runs.Concat(_bundled).DistinctBy(r => r.Id)
        .OrderByDescending(r => r.TrainedAt).ToArray();
    public PersonTrainingRun? Active => _archive.Runs.FirstOrDefault(r => r.Id == _archive.ActiveId);
    public string? BundledLoadError { get; private set; }
    public bool IsBundled(Guid id) => _bundled.Any(r => r.Id == id);

    public void Reload()
    {
        _archive = ReadLocal();
        _bundled = [];
        BundledLoadError = null;
        if (bundledPath is null) return;
        try { _bundled = Read(bundledPath).Runs; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { BundledLoadError = "Не удалось загрузить модель из поставки. Локальные версии доступны."; }
    }

    private Archive ReadLocal() => path is null ? _archive : Read(path);
    private static Archive Read(string source)
    {
        try
        {
            using var stream = File.OpenRead(source);
            if (stream.Length > 128_000) throw new InvalidDataException("Журнал моделей слишком большой.");
            var archive = JsonSerializer.Deserialize<Archive>(stream);
            if (archive?.Version != 1 || archive.Runs is null || archive.Runs.Length > MaximumRuns ||
                archive.Runs.Any(r => !Valid(r)) || archive.Runs.Select(r => r.Id).Distinct().Count() != archive.Runs.Length ||
                archive.ActiveId is { } active && archive.Runs.All(r => r.Id != active))
                throw new InvalidDataException("Не удалось прочитать журнал моделей.");
            return archive;
        }
        catch (FileNotFoundException) { return new(1, null, []); }
        catch (DirectoryNotFoundException) { return new(1, null, []); }
    }

    private static bool Valid(PersonTrainingRun? r)
    {
        if (r is null || r.Id == Guid.Empty || r.Weights is not { Length: PersonCandidateLearning.FeatureCount } ||
            r.Weights.Any(w => !double.IsFinite(w) || Math.Abs(w) > 50) ||
            r.Loss is not { Length: 21 } || r.Loss.Any(x => !double.IsFinite(x) || x < 0) ||
            r.Note is { Length: > 1200 } ||
            r.TrainingCount is < 6 or > 2048 || r.ValidationCount is < 0 or > 2048 ||
            !float.IsFinite(r.Threshold) || r.Threshold is < .1f or > .9f) return false;
        if (r.ValidationCount == 0) return r.Baseline is null && r.Trained is null;
        bool ValidMetrics(PersonTrainingMetrics? m) => m is not null &&
            new[] { m.TruePositive, m.TrueNegative, m.FalsePositive, m.FalseNegative }.All(n => n >= 0 && n <= r.ValidationCount) &&
            m.TruePositive + m.FalseNegative > 0 && m.TrueNegative + m.FalsePositive > 0 &&
            m.TruePositive + m.FalseNegative + m.TrueNegative + m.FalsePositive == r.ValidationCount;
        return ValidMetrics(r.Baseline) && ValidMetrics(r.Trained);
    }

    public void Add(PersonTrainingRun run)
    {
        if (!Valid(run)) throw new InvalidDataException("Некорректный результат обучения.");
        Update(archive =>
        {
            if (archive.Runs.Any(r => r.Id == run.Id))
                throw new InvalidOperationException("Версия с таким идентификатором уже существует.");
            return Include(archive, run);
        });
    }

    public void Activate(Guid? id) => Update(archive =>
    {
        if (id.HasValue && archive.Runs.All(r => r.Id != id))
        {
            var bundled = _bundled.FirstOrDefault(r => r.Id == id) ??
                throw new InvalidOperationException("Эта версия уже отсутствует в журнале.");
            // Keep an activated version in the local journal so updating the application cannot replace it.
            archive = Include(archive, bundled);
        }
        return archive with { ActiveId = id };
    });

    public static void ExportPackage(string destination, PersonTrainingRun run)
    {
        if (!Valid(run)) throw new InvalidDataException("Некорректный результат обучения.");
        // Sharing weights never changes the recipient's model selection or transfers their annotations.
        Write(destination, new(1, null, [run]));
    }

    private static Archive Include(Archive archive, PersonTrainingRun run)
    {
        var runs = archive.Runs.Prepend(run).ToList();
        if (runs.Count > MaximumRuns)
            runs.RemoveAt(runs.FindLastIndex(r => r.Id != archive.ActiveId));
        return archive with { Runs = runs.ToArray() };
    }

    private void Update(Func<Archive, Archive> change)
    {
        if (path is not null) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var gate = path is null ? null : new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        var next = change(ReadLocal());
        if (path is not null) Write(path, next);
        _archive = next;
    }

    private static void Write(string destination, Archive archive)
    {
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, archive, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
