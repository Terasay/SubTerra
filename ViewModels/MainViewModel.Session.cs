using System.IO;
using System.Numerics;
using SubTerra.Models;
using SubTerra.Services;
using SubTerra.Simulation;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IAccessSession _access;
    private readonly ISessionInteraction _interaction;
    private bool _confirming;
    private bool _loadingSettings;
    private bool _hasUnsavedChanges;
    private string? _sessionPath;
    private List<ScannerAdjustment> _adjustments = [];
    private double _scannerPosition;
    private double _scannerLateral;
    private double _scannerHeight;
    private double _scannerYaw;
    private bool _previewScanner;

    public bool IsAdministrator => _access.IsAdministrator;
    public bool IsGuest => !IsAdministrator;
    public string AccessLabel => IsAdministrator ? "Администратор · admin" : "Гость · просмотр";
    public string AccessButtonText => IsAdministrator ? "Выйти" : "Вход администратора";
    public bool CanStart => IsAdministrator && CanInteract && !IsRunning;
    public bool CanChangeScenario => CanInteract && !IsRunning;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set { if (Set(ref _hasUnsavedChanges, value)) Raise(nameof(SessionSaveLabel)); }
    }
    public string SessionSaveLabel => HasUnsavedChanges ? "● Есть несохранённые изменения" :
        _sessionPath is null ? "Демонстрационная карта" : "Сохранено · " + Path.GetFileName(_sessionPath);

    private void AccessChanged(object? sender, EventArgs e)
    {
        if (!IsAdministrator) Pause();
        Raise(nameof(IsAdministrator)); Raise(nameof(IsGuest)); Raise(nameof(AccessLabel)); Raise(nameof(AccessButtonText));
        NotifyState();
    }

    private async Task<bool> ConfirmReplacementAsync(string action)
    {
        Pause();
        if (!HasUnsavedChanges) return true;
        if (_confirming) return false;
        _confirming = true;
        NotifyState();
        try
        {
            var decision = await _interaction.ConfirmUnsavedAsync(action, IsAdministrator);
            if (decision == UnsavedDecision.Cancel) return false;
            if (decision == UnsavedDecision.Discard) return true;
            if (!IsAdministrator) return false;
            var path = _sessionPath ?? await _interaction.ChooseSavePathAsync();
            return path is not null && await SaveCoreAsync(path);
        }
        catch (Exception ex) { ReportError("Не удалось завершить действие", ex); return false; }
        finally { _confirming = false; NotifyState(); }
    }

    public async Task<bool> RequestCloseAsync()
    {
        if (!CanInteract) return false;
        return await ConfirmReplacementAsync("закрыть приложение");
    }

    public async Task<bool> LogoutAsync()
    {
        if (!IsAdministrator || !CanInteract || !await ConfirmReplacementAsync("выйти из учётной записи")) return false;
        _access.Logout();
        AddEvent("Администратор вышел · включён просмотр");
        return true;
    }

    public bool CanRepositionScanner => IsAdministrator && CanInteract && !IsRunning && IsOverview && !IsReviewing &&
        _dataset is not null && _capturedFrames > 0 && _capturedFrames < _dataset.Session.Settings.FrameCount && !HasPendingSettings;
    public string ScannerControlHint => IsGuest ? "Управление датчиком доступно администратору." : IsRunning ?
        "Поставьте проход на паузу для перестановки." : IsReviewing ? "Вернитесь к последнему кадру для перестановки." : CanRepositionScanner ?
        "Выберите позицию и нажмите «Применить». Запись продолжится в ту же карту." :
        "Доступно на паузе незавершённого прохода с прежними параметрами.";
    public double ScannerPosition
    {
        get => _scannerPosition;
        set { if (CanRepositionScanner && double.IsFinite(value) && Set(ref _scannerPosition, Math.Clamp(value, 1, 98))) PreviewAdjustment(); }
    }
    public double ScannerLateral
    {
        get => _scannerLateral;
        set { if (CanRepositionScanner && double.IsFinite(value) && Set(ref _scannerLateral, Math.Clamp(value, -1, 1))) PreviewAdjustment(); }
    }
    public double ScannerHeight
    {
        get => _scannerHeight;
        set { if (CanRepositionScanner && double.IsFinite(value) && Set(ref _scannerHeight, Math.Clamp(value, -.5, .5))) PreviewAdjustment(); }
    }
    public double ScannerYaw
    {
        get => _scannerYaw;
        set { if (CanRepositionScanner && double.IsFinite(value) && Set(ref _scannerYaw, Math.Clamp(value, -180, 180))) PreviewAdjustment(); }
    }
    private ScannerAdjustment ProposedAdjustment => new(_capturedFrames, (float)(_scannerPosition / 100),
        (float)_scannerLateral, (float)_scannerHeight, (float)_scannerYaw);
    private void PreviewAdjustment() { _previewScanner = true; UpdateScanner(); }
    private void ResetScannerPreview()
    {
        _previewScanner = false;
        if (_dataset is null) return;
        var pending = _adjustments.LastOrDefault(a => a.FirstFrame == _capturedFrames);
        var last = pending is null ? Math.Max(0, _capturedFrames - 1) : _capturedFrames;
        _previewScanner = pending is not null;
        var active = ScannerPlan.Active(_adjustments, last);
        _scannerPosition = Math.Clamp(ScannerPlan.PositionAt(_dataset.Session.Settings, _adjustments, last) * 100d, 1, 98);
        _scannerLateral = active?.LateralOffset ?? 0;
        _scannerHeight = active?.HeightOffset ?? 0;
        _scannerYaw = active?.YawDegrees ?? 0;
        Raise(nameof(ScannerPosition)); Raise(nameof(ScannerLateral)); Raise(nameof(ScannerHeight)); Raise(nameof(ScannerYaw));
        UpdateScanner();
    }

    public async Task ApplyScannerAdjustmentAsync()
    {
        if (!CanRepositionScanner) return;
        var adjustment = ProposedAdjustment;
        IsBusy = true;
        try
        {
            await _captureTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (!await Task.Run(() => ScannerPlan.IsClear(_dataset!.Scene, adjustment)))
                throw new InvalidOperationException("Датчик слишком близко к стене или внутри выступа. Уменьшите смещение.");
            if (!IsAdministrator || _disposed) return;
            _adjustments.RemoveAll(a => a.FirstFrame >= _capturedFrames);
            _adjustments.Add(adjustment);
            _dataset = new(_dataset!.Scene, _map!.Snapshot(_adjustments.ToArray()));
            HasUnsavedChanges = true;
            _previewScanner = true;
            Notification = "";
            Status = "Позиция применена · продолжите сканирование";
            AddEvent($"Перестановка перед кадром {_capturedFrames + 1} · {_scannerPosition:F0}% маршрута");
        }
        catch (Exception ex) { ReportError("Не удалось переставить датчик", ex); }
        finally { IsBusy = false; UpdateScanner(); }
    }

    private ScanCoverage[] VisibleCoverage => _dataset is null ? [] : ScannerPlan.Coverage(_dataset.Session.Settings, _adjustments, _viewFrames);
    private ScanCoverage WalkCoverage => VisibleCoverage.FirstOrDefault(r => RouteProgress >= r.Start && RouteProgress <= r.End);
    public HelixToolkit.SharpDX.LineGeometry3D? ScannerHeading { get; private set; }
}
