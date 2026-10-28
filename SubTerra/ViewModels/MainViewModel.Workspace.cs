using System.Collections.ObjectModel;
using System.Diagnostics;
using SubTerra.Models;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel
{
    private WorkspacePreferences _preferences = new();
    private readonly Services.WorkspaceStateStore? _workspaceStore;
    private CancellationTokenSource? _preferenceSave;
    private bool _hasRecovery;
    private bool _autosaving;
    private int _editVersion;
    private int _recoveredVersion = -1;
    private int _sessionEpoch;
    private string _autoSaveText = "Автосохранение готово";
    public ObservableCollection<RecentSession> RecentSessions { get; } = [];
    public bool HasRecovery
    {
        get => _hasRecovery;
        private set
        {
            if (!Set(ref _hasRecovery, value)) return;
            Raise(nameof(CloudQualityVisible)); Raise(nameof(CanStart)); Raise(nameof(CanAddAnnotation)); Raise(nameof(CanRepositionScanner));
            AutoSaveText = value ? "Сначала восстановите или удалите найденную копию" : "Автосохранение готово";
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    public string AutoSaveText { get => _autoSaveText; private set => Set(ref _autoSaveText, value); }
    public double MouseSensitivity { get => _preferences.MouseSensitivity; set => ChangePreferences(_preferences with { MouseSensitivity = value }); }
    public bool InvertMouseY { get => _preferences.InvertMouseY; set => ChangePreferences(_preferences with { InvertMouseY = value }); }
    public double WalkSpeed { get => _preferences.WalkSpeed; set => ChangePreferences(_preferences with { WalkSpeed = value }); }
    public double FlightSpeed { get => _preferences.FlightSpeed; set => ChangePreferences(_preferences with { FlightSpeed = value }); }
    public double FastMultiplier { get => _preferences.FastMultiplier; set => ChangePreferences(_preferences with { FastMultiplier = value }); }
    public bool AutoSaveEnabled { get => _preferences.AutoSaveEnabled; set => ChangePreferences(_preferences with { AutoSaveEnabled = value }); }
    public bool AdaptiveQuality
    {
        get => _preferences.AdaptiveQuality;
        set { ChangePreferences(_preferences with { AdaptiveQuality = value }); RefreshAdaptiveQuality(); }
    }

    private void InitializeWorkspace()
    {
        _preferences = _workspaceStore?.LoadPreferences() ?? new();
        _flightSpeed = (float)_preferences.FlightSpeed;
        foreach (var path in _preferences.RecentFiles) RecentSessions.Add(new(path));
        HasRecovery = _workspaceStore?.HasRecovery == true;
    }
    private void ChangePreferences(WorkspacePreferences preferences)
    {
        _preferences = preferences.Normalize();
        _flightSpeed = (float)_preferences.FlightSpeed;
        foreach (var name in new[] { nameof(MouseSensitivity), nameof(InvertMouseY), nameof(WalkSpeed), nameof(FlightSpeed),
                     nameof(FastMultiplier), nameof(AutoSaveEnabled), nameof(AdaptiveQuality), nameof(NavigationSpeedText) }) Raise(name);
        _preferenceSave?.Cancel();
        _preferenceSave?.Dispose();
        _preferenceSave = new();
        _ = SavePreferencesLaterAsync(_preferenceSave.Token);
    }
    private async Task SavePreferencesLaterAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(300, cancellationToken); await FlushPreferencesAsync(cancellationToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AutoSaveText = "Не удалось сохранить настройки: " + ex.Message; }
    }
    public Task FlushPreferencesAsync(CancellationToken cancellationToken = default) =>
        _workspaceStore?.SavePreferencesAsync(_preferences, cancellationToken) ?? Task.CompletedTask;

    private void RememberFile(string path)
    {
        var files = new[] { System.IO.Path.GetFullPath(path) }.Concat(_preferences.RecentFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
        RecentSessions.Clear();
        foreach (var file in files) RecentSessions.Add(new(file));
        ChangePreferences(_preferences with { RecentFiles = files });
    }
    private void MarkSessionDirty() { ++_editVersion; HasUnsavedChanges = true; }

    public async Task AutoSaveAsync()
    {
        if (_workspaceStore is null || !AutoSaveEnabled || HasRecovery || _autosaving || !HasUnsavedChanges ||
            !IsAdministrator || _dataset is null || _capturedFrames == 0 || IsBusy || _editVersion == _recoveredVersion) return;
        _autosaving = true;
        var version = _editVersion;
        var epoch = _sessionEpoch;
        var session = SnapshotWithAnnotations();
        try
        {
            await _workspaceStore.SaveRecoveryAsync(_storage, session, _sessionPath);
            if (_disposed || epoch != _sessionEpoch) return;
            _recoveredVersion = version;
            AutoSaveText = "Копия восстановления: " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex) { if (!_disposed) AutoSaveText = "Автосохранение не выполнено: " + ex.Message; }
        finally { _autosaving = false; }
    }
    private async Task ClearRecoveryAsync(bool includePending = false)
    {
        if (_workspaceStore is null || (HasRecovery && !includePending)) return;
        try { await _workspaceStore.ClearRecoveryAsync(); HasRecovery = false; }
        catch (Exception ex) { AutoSaveText = "Не удалось удалить старую копию: " + ex.Message; }
    }
    public async Task RestoreRecoveryAsync()
    {
        if (_workspaceStore is null || !HasRecovery || !CanInteract) return;
        await OpenSessionAsync(_workspaceStore.RecoveryPath, recovery: true);
    }
    public Task DismissRecoveryAsync() => CanInteract ? ClearRecoveryAsync(includePending: true) : Task.CompletedTask;
}
