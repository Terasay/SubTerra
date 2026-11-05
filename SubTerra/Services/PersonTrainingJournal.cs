using System.IO;
using System.Text.Json;

namespace SubTerra.Services;

public sealed record PersonTrainingMetrics(int TruePositive, int FalsePositive, int FalseNegative, int TrueNegative)
{
    public double BalancedAccuracy => .5 * ((double)TruePositive / (TruePositive + FalseNegative) +
        (double)TrueNegative / (TrueNegative + FalsePositive));
}

public sealed record PersonTrainingRun(Guid Id, DateTimeOffset TrainedAt, double[] Weights, double[] Loss,
    int TrainingCount, int ValidationCount, float Threshold, PersonTrainingMetrics? Baseline, PersonTrainingMetrics? Trained);

public sealed class PersonTrainingJournal(string? path)
{
    private const int MaximumRuns = 10;
    private sealed record Archive(int Version, Guid? ActiveId, PersonTrainingRun[] Runs);
    private Archive _archive = new(1, null, []);
    public IReadOnlyList<PersonTrainingRun> Runs => _archive.Runs;
    public PersonTrainingRun? Active => _archive.Runs.FirstOrDefault(r => r.Id == _archive.ActiveId);

    public void Reload() => _archive = Read();
    private Archive Read()
    {
        if (path is null) return _archive;
        try
        {
            using var stream = File.OpenRead(path);
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
            var runs = archive.Runs.Prepend(run).ToList();
            if (runs.Count > MaximumRuns)
                runs.RemoveAt(runs.FindLastIndex(r => r.Id != archive.ActiveId));
            return archive with { Runs = runs.ToArray() };
        });
    }

    public void Activate(Guid? id) => Update(archive =>
    {
        if (id.HasValue && archive.Runs.All(r => r.Id != id))
            throw new InvalidOperationException("Эта версия уже отсутствует в журнале.");
        return archive with { ActiveId = id };
    });

    private void Update(Func<Archive, Archive> change)
    {
        if (path is not null) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var gate = path is null ? null : new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        var next = change(Read());
        if (path is not null)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, next);
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        _archive = next;
    }
}
