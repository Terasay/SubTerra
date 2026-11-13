using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SubTerra.Services;
using SubTerra.Models;

namespace SubTerra.ViewModels;

public sealed record TrainingMetricRow(string Metric, string Baseline, string Trained);

public sealed partial class MainViewModel
{
    private readonly PersonTrainingJournal _personModels;
    private string? _personModelError;
    private bool _isTrainingPanelOpen;
    private PersonTrainingRun? _selectedTrainingRun;
    public bool IsTrainingPanelOpen { get => _isTrainingPanelOpen; private set => Set(ref _isTrainingPanelOpen, value); }
    public RelayCommand OpenTrainingPanelCommand { get; private set; } = null!;
    public RelayCommand CloseTrainingPanelCommand { get; private set; } = null!;
    public AsyncRelayCommand TrainPersonModelCommand { get; private set; } = null!;
    public AsyncRelayCommand CollectPersonExamplesCommand { get; private set; } = null!;
    public RelayCommand ActivatePersonModelCommand { get; private set; } = null!;
    public RelayCommand UseBaselinePersonModelCommand { get; private set; } = null!;
    public RelayCommand ExportPersonModelCommand { get; private set; } = null!;
    public RelayCommand SetPersonCaptureGroupCommand { get; private set; } = null!;
    private string _personCaptureGroup = "";
    public string PersonCaptureGroup { get => _personCaptureGroup; set { if (value.Length <= 128) Set(ref _personCaptureGroup, value); } }
    public event EventHandler? PersonModelExportRequested;
    public IReadOnlyList<PersonTrainingRun> PersonTrainingRuns => _personModelError is null ? _personModels.Runs : [];
    public string ActivePersonModelText => _personModelError ?? (_personModels.Active is { } model ?
        $"Активна модель от {model.TrainedAt.LocalDateTime:g}" : "Активна базовая YOLOX");
    public string BundledPersonModelStatus => _personModels.BundledLoadError ??
        (PersonTrainingRuns.Any(r => _personModels.IsBundled(r.Id)) ? "Модель из поставки доступна в списке версий." : "");
    public string TrainingRunOrigin => SelectedTrainingRun is { } run ?
        _personModels.IsBundled(run.Id) ? "Версия из поставки приложения" : "Обучена на этом компьютере" : "";
    public string TrainingRunNote => SelectedTrainingRun?.Note ?? "";
    public string TrainingExamplesText => _learningError ??
        $"С источником · человек: {_personLearning.TaggedPositives} · ошибка: {_personLearning.TaggedNegatives} · сканов: {_personLearning.SourceCount}";
    public string TrainingExamplesHint => _learningError is not null ? "Проверьте доступ к локальному профилю." :
        $"Для запуска нужны 3 рамки каждого класса. Старых отметок без источника: " +
        $"{_personLearning.Positives + _personLearning.Negatives - _personLearning.TaggedPositives - _personLearning.TaggedNegatives}. " +
        "Такие рамки можно заново подтвердить на исходном скане.";
    public PersonTrainingRun? SelectedTrainingRun
    {
        get => _selectedTrainingRun;
        set
        {
            if (value is not null && !PersonTrainingRuns.Contains(value) || !Set(ref _selectedTrainingRun, value)) return;
            Raise(nameof(TrainingRunDetails));
            Raise(nameof(TrainingLossCurve));
            Raise(nameof(TrainingLossText));
            Raise(nameof(TrainingComparison));
            Raise(nameof(TrainingValidationText));
            Raise(nameof(TrainingRunOrigin));
            Raise(nameof(TrainingRunNote));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string TrainingRunDetails => SelectedTrainingRun is { } run ?
        $"Рамок для обучения: {run.TrainingCount} · для проверки: {run.ValidationCount} · порог сравнения: {run.Threshold:P0}" :
        "Здесь появятся график и результаты первого обучения.";
    public string TrainingLossText => SelectedTrainingRun is { } run ? $"{run.Loss[0]:F3} → {run.Loss[^1]:F3} за 100 шагов" : "";
    public PointCollection TrainingLossCurve
    {
        get
        {
            if (SelectedTrainingRun is not { } run) return [];
            var maximum = Math.Max(1e-9, run.Loss.Max());
            return new(run.Loss.Select((loss, i) => new Point(8 + i * 27.2, 94 - 82 * loss / maximum)));
        }
    }
    public string TrainingValidationText => SelectedTrainingRun switch
    {
        { ValidationCount: > 0 } => "Обе модели проверены при одном пороге на рамках одного скана, исключённого из обучения. Эти числа оценивают проверенные рамки.",
        not null => "Для проверки нужны отметки обоих классов на отдельном скане и минимум по три примера каждого класса на остальных сканах.",
        _ => "После обучения здесь появится сравнение с базовой YOLOX, если хватит данных для отдельной проверки."
    };
    public IReadOnlyList<TrainingMetricRow> TrainingComparison =>
        SelectedTrainingRun is { Baseline: { } baseline, Trained: { } trained } ?
        [
            new("Сбалансированная точность", $"{baseline.BalancedAccuracy:P1}", $"{trained.BalancedAccuracy:P1}"),
            new("Верно принятые рамки", $"{baseline.TruePositive}", $"{trained.TruePositive}"),
            new("Ложные рамки", $"{baseline.FalsePositive}", $"{trained.FalsePositive}"),
            new("Отклонённые рамки с людьми", $"{baseline.FalseNegative}", $"{trained.FalseNegative}")
        ] : [];

    private bool CanTrainPersonModel => IsAdministrator && CanInteract && _learningError is null &&
        _personModelError is null && _personLearning.Ready;

    private void InitializeTrainingCommands()
    {
        OpenTrainingPanelCommand = new(() =>
        {
            Pause(); ReturnToOverview(); IsSettingsOpen = false;
            LoadPersonExamples();
            LoadPersonModels(selectNewest: true);
            PersonCaptureGroup = _scanProject?.Active?.Source.Metadata.CaptureId ?? "";
            IsTrainingPanelOpen = true;
        }, () => IsAdministrator && CanInteract);
        CloseTrainingPanelCommand = new(() => IsTrainingPanelOpen = false, () => CanInteract);
        TrainPersonModelCommand = new(TrainPersonModelAsync, () => CanTrainPersonModel, ex => ReportError("Не удалось обучить модель людей", ex));
        CollectPersonExamplesCommand = new(async () =>
        {
            IsTrainingPanelOpen = false;
            IsWelcomeVisible = false;
            SelectAnalysisTool("Люди");
            await RunPeopleAnalysisAsync(true, true, PeopleSearchMode.Review);
        }, () => IsAdministrator && CanFindPeople, ex => ReportError("Не удалось собрать рамки для проверки", ex));
        ActivatePersonModelCommand = new(() => SetActivePersonModel(SelectedTrainingRun?.Id),
            () => IsAdministrator && CanInteract && _personModelError is null && SelectedTrainingRun is { } run && _personModels.Active?.Id != run.Id);
        UseBaselinePersonModelCommand = new(() => SetActivePersonModel(null),
            () => IsAdministrator && CanInteract && _personModelError is null && _personModels.Active is not null);
        ExportPersonModelCommand = new(() => PersonModelExportRequested?.Invoke(this, EventArgs.Empty),
            () => IsAdministrator && CanInteract && _personModelError is null && SelectedTrainingRun is not null);
        SetPersonCaptureGroupCommand = new(SetPersonCaptureGroup, () => CanManageProject && !string.IsNullOrWhiteSpace(PersonCaptureGroup));
    }

    private void SetPersonCaptureGroup()
    {
        if (!CanManageProject || _scanProject is not { } before || string.IsNullOrWhiteSpace(PersonCaptureGroup)) return;
        var name = PersonCaptureGroup.Trim();
        var next = before with { Objects = before.Objects.Select(o => !o.IsVisible ? o : o with
        {
            Source = new ImportedScan { Metadata = o.Source.Metadata with { CaptureId = name }, Points = o.Source.Points,
                Colors = o.Source.Colors, Triangles = o.Source.Triangles, Appearance = o.Source.Appearance }
        }).ToArray() };
        try
        {
            var sources = PersonCaptureIdentity.Capture(next);
            if (sources.Length != 1) return;
            _personLearning.MergeSources(PersonCaptureIdentity.Capture(before), sources[0]);
            _projectHistory.Commit(before, next, "группа съёмки для обучения");
            _scanProject = next; _dataset = new(null, next.ToSession(UserAnnotations.ToArray()));
            _personSources = sources;
            MarkSessionDirty(); NotifyProjectState(); NotifyTrainingState();
            Status = "Группа съёмки сохранена. Для связанных экспортов укажите такое же имя.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { ReportError("Не удалось сохранить группу съёмки", ex); }
    }

    private void LoadPersonModels(bool selectNewest = false)
    {
        try { _personModels.Reload(); _personModelError = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { _personModelError = "Журнал моделей недоступен. Используется базовая YOLOX. " + ex.Message; }
        NotifyTrainingState(selectNewest);
    }

    private void NotifyTrainingState(bool selectNewest = false)
    {
        var previous = SelectedTrainingRun?.Id;
        Raise(nameof(PersonTrainingRuns));
        SelectedTrainingRun = (selectNewest ? null : PersonTrainingRuns.FirstOrDefault(r => r.Id == previous)) ??
            PersonTrainingRuns.FirstOrDefault();
        Raise(nameof(ActivePersonModelText));
        Raise(nameof(BundledPersonModelStatus));
        Raise(nameof(TrainingRunOrigin));
        Raise(nameof(TrainingRunNote));
        Raise(nameof(PersonLearningSummary));
        Raise(nameof(TrainingExamplesText));
        Raise(nameof(TrainingExamplesHint));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task TrainPersonModelAsync()
    {
        if (!CanTrainPersonModel) return;
        LoadPersonExamples();
        if (!CanTrainPersonModel) return;
        var threshold = (float)PersonThreshold;
        var token = BeginOperation("Обучение модели · 100 шагов");
        try
        {
            var progress = OperationProgress(token);
            var run = await Task.Run(() => _personLearning.Train(threshold, progress, token), token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator) return;
            _personModels.Add(run);
            NotifyTrainingState(selectNewest: true);
            Status = "Обучение завершено · версия сохранена в журнале";
            Notification = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        finally { EndOperation(); }
    }

    private void SetActivePersonModel(Guid? id)
    {
        if (!IsAdministrator || !CanInteract || _personModelError is not null) return;
        try
        {
            _personModels.Activate(id);
            NotifyTrainingState();
            Status = id.HasValue ? "Версия будет применена при следующем поиске" : "Для следующего поиска выбрана базовая YOLOX";
            Notification = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        { ReportError("Не удалось переключить модель", ex); }
    }

    public bool ExportSelectedPersonModel(string destination)
    {
        if (!ExportPersonModelCommand.CanExecute(null) || SelectedTrainingRun is not { } run) return false;
        try
        {
            PersonTrainingJournal.ExportPackage(destination, run);
            Status = "Модель сохранена для передачи · без сканов и обучающих примеров";
            Notification = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { ReportError("Не удалось сохранить модель", ex); return false; }
    }
}
