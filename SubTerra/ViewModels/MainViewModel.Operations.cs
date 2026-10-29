using System.IO;
using SubTerra.Models;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _operationCancellation;
    private bool _cancellingOperation;
    private TaskCompletionSource _operationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public RelayCommand CancelOperationCommand { get; private set; } = null!;
    public RelayCommand CoordinatesCommand { get; private set; } = null!;
    public RelayCommand MeasureCommand { get; private set; } = null!;
    public RelayCommand EndInspectionCommand { get; private set; } = null!;
    public RelayCommand AddAnnotationCommand { get; private set; } = null!;
    public RelayCommand<UserAnnotation> RemoveAnnotationCommand { get; private set; } = null!;
    public RelayCommand<UserAnnotation> NavigateAnnotationCommand { get; private set; } = null!;
    public AsyncRelayCommand RestoreRecoveryCommand { get; private set; } = null!;
    public AsyncRelayCommand DismissRecoveryCommand { get; private set; } = null!;
    public RelayCommand<RecentSession> OpenRecentCommand { get; private set; } = null!;
    public bool CanCancelOperation => IsBusy && _operationCancellation is not null && !_cancellingOperation;

    private void InitializeNewCommands()
    {
        InitializeProjectCommands(); InitializeCleaningCommands(); InitializeStructureCommands();
        CancelOperationCommand = new RelayCommand(CancelOperation, () => CanCancelOperation);
        CoordinatesCommand = new RelayCommand(() => BeginInspection(false), () => CanInteract && _viewFrames > 0);
        MeasureCommand = new RelayCommand(() => BeginInspection(true), () => CanInteract && _viewFrames > 0);
        EndInspectionCommand = new RelayCommand(EndInspection);
        AddAnnotationCommand = new RelayCommand(AddAnnotation, () => CanAddAnnotation);
        RemoveAnnotationCommand = new RelayCommand<UserAnnotation>(RemoveAnnotation, _ => IsAdministrator && CanInteract && !HasRecovery);
        NavigateAnnotationCommand = new RelayCommand<UserAnnotation>(NavigateAnnotation, _ => CanInteract);
        RestoreRecoveryCommand = new AsyncRelayCommand(RestoreRecoveryAsync, () => HasRecovery && CanInteract, ex => ReportError("Не удалось восстановить сеанс", ex));
        DismissRecoveryCommand = new AsyncRelayCommand(DismissRecoveryAsync, () => HasRecovery && CanInteract, ex => ReportError("Не удалось удалить копию", ex));
        OpenRecentCommand = new RelayCommand<RecentSession>(file => _ = OpenAsync(file.Path), _ => CanInteract);
    }
    private CancellationToken BeginOperation(string status)
    {
        _operationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _operationCancellation?.Dispose();
        _operationCancellation = new();
        _cancellingOperation = false;
        BusyProgress = 0;
        IsBusy = true;
        Status = status;
        Raise(nameof(CanCancelOperation));
        return _operationCancellation.Token;
    }
    private void EndOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _cancellingOperation = false;
        IsBusy = false;
        _operationFinished.TrySetResult();
        Raise(nameof(CanCancelOperation));
    }
    public void CancelOperation()
    {
        if (!CanCancelOperation) return;
        _cancellingOperation = true;
        _operationCancellation!.Cancel();
        Status = "Отмена операции…";
        Raise(nameof(CanCancelOperation));
    }
    private IProgress<double> OperationProgress(CancellationToken token, double scale = 1) => new Progress<double>(value =>
    {
        if (!_disposed && !token.IsCancellationRequested && _operationCancellation?.Token == token)
            BusyProgress = Math.Clamp(value * scale, 0, 100);
    });
    private void OperationCancelled()
    {
        BusyProgress = 0;
        Status = "Операция отменена · прежний сеанс сохранён";
    }

    private async Task OpenSessionAsync(string path, bool recovery = false)
    {
        if (!CanInteract || !await ConfirmReplacementAsync("открыть другой сеанс")) return;
        ReturnToOverview();
        Pause();
        var token = BeginOperation(recovery ? "Восстановление сеанса" : "Открытие сеанса");
        var prepareIndoor = false;
        try
        {
            var session = await _storage.LoadAsync(path, token, OperationProgress(token, .8)).WaitAsync(token);
            var originalPath = recovery && _workspaceStore is not null
                ? await Task.Run(_workspaceStore.RecoveryOriginalPath, token).WaitAsync(token) : path;
            var scenario = session.ImportedScan is null && session.Project is null ? Scenarios.First(s => s.Id == session.ScenarioId) : null;
            var scene = scenario is null ? null : await Task.Run(() => Simulation.CaveFactory.Create(scenario, session.SceneVersion), token).WaitAsync(token);
            var projectView = scenario is null ? await Rendering.ScanProjectView.PrepareAsync(ScanProject.FromSession(session), token) : null;
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            if (scenario is not null) SelectedScenario = scenario;
            _loadingSettings = true;
            Range = session.Settings.RangeMeters;
            DensityIndex = session.Settings.RaysPerFrame <= 360 ? 0 : session.Settings.RaysPerFrame >= 1280 ? 2 : 1;
            StartPosition = session.Settings.StartProgress * 100d;
            FrameIntervalMilliseconds = session.Settings.FrameIntervalMilliseconds;
            _loadingSettings = false;
            Assign(new(scene, session), scenario is null ? 1 : session.Frames.Length, projectView);
            _sessionPath = originalPath;
            HasUnsavedChanges = recovery;
            if (recovery) { ++_editVersion; HasRecovery = false; }
            else { RememberFile(path); await ClearRecoveryAsync(); }
            NotifySessionProperties();
            Status = recovery ? "Несохранённый сеанс восстановлен · сохраните его" : "Сохранённый сеанс открыт";
            Notification = "";
            Home();
            AddEvent(recovery ? "Восстановлена аварийная копия" : "Из файла восстановлены все измерения");
            prepareIndoor = session.Project?.Structure?.Result.Options.UseNeuralClasses == true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось открыть сеанс", ex); }
        finally { _loadingSettings = false; EndOperation(); }
        if (prepareIndoor && CanPrepareIndoorPreview) await PrepareIndoorPreviewAsync();
    }
}
