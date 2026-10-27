using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using SubTerra.Models;
using SubTerra.Navigation;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.Simulation;
using PerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using OrthographicCamera = HelixToolkit.Wpf.SharpDX.OrthographicCamera;
using ProjectionCamera = HelixToolkit.Wpf.SharpDX.ProjectionCamera;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IScanSource _source;
    private readonly IScanFrameSource _frameSource;
    private readonly SessionStorage _storage;
    private CancellationTokenSource? _captureCancellation;
    private Task _captureTask = Task.CompletedTask;
    private MapAccumulator? _map;
    private int _runId;
    private bool _disposed;
    private ScanSettings? _baselineSettings;
    private int _baselineDensityIndex;
    private CancellationTokenSource? _generation;
    private ScanDataset? _dataset;
    private SceneGeometry? _geometry;
    private int[] _pointOffsets = [];
    private int _capturedFrames;
    private int _viewFrames;
    private bool _isBusy;
    private bool _isRunning;
    private bool _isSurface = true;
    private bool _isHeight;
    private bool _isWalking;
    private bool _mouseCaptured;
    private float _walkTargetProgress;
    private bool _showGrid = true;
    private bool _showRoute = true;
    private bool _showMarkers = true;
    private bool _cutRoof = true;
    private bool _followScanner;
    private bool _isPresentationMode;
    private bool _isInspectorOpen;
    private bool _showShadows = true;
    private double _range = 20;
    private double _speed = 1;
    private int _densityIndex = 1;
    private PointCloudQuality _pointQuality = PointCloudQuality.All[1];
    private double _geometryMilliseconds;
    private double _generationMilliseconds;
    private long _privateMemoryBytes;
    private double _startPosition;
    private int _frameIntervalMilliseconds = 100;
    private double _busyProgress;
    private string _status = "Подготовка сцены";
    private string _notification = "";
    private string _cameraLabel = "Общий вид";
    private ScenarioDefinition _selectedScenario = ScenarioDefinition.All[0];
    private SceneMarker? _selectedMarker;
    private PointGeometry3D? _cloud;
    private PointGeometry3D? _scanner;
    private PointGeometry3D? _markers;
    private MeshGeometry3D? _surface;
    private MeshGeometry3D? _floorSurface;
    private MeshGeometry3D? _dampSurface;
    private LineGeometry3D? _route;
    private LineGeometry3D? _grid;

    public MainViewModel(IScanSource source, SessionStorage storage, IScanFrameSource? frameSource = null,
        IAccessSession? access = null, ISessionInteraction? interaction = null)
    {
        _source = source;
        _frameSource = frameSource ?? source;
        _storage = storage;
        _access = access ?? new AdminAccessService();
        _interaction = interaction ?? new CancelSessionInteraction();
        _access.AccessChanged += AccessChanged;
        PauseCommand = new RelayCommand(Pause, () => IsAdministrator && IsRunning && CanInteract);
        StopCommand = new RelayCommand(Stop, () => IsAdministrator && CanInteract && (IsRunning || CanResume));
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => CanSave, ex => ReportError("Не удалось очистить карту", ex));
        ApplyScannerCommand = new AsyncRelayCommand(ApplyScannerAdjustmentAsync, () => CanRepositionScanner,
            ex => ReportError("Не удалось переставить датчик", ex));
        HomeCommand = new RelayCommand(Home);
        TopCommand = new RelayCommand(Top);
        InsideCommand = new RelayCommand(Inside, () => CanWalk);
        PointsCommand = new RelayCommand(() => SetView(false, false));
        SurfaceCommand = new RelayCommand(() => SetView(true, false));
        HeightCommand = new RelayCommand(() => SetView(true, true));
        LatestCommand = new RelayCommand(ShowLatest, () => !IsBusy && IsReviewing);
        PreviewScannerCommand = new RelayCommand(PreviewScanner, () => CanEditSettings && IsOverview);
        DismissNotificationCommand = new RelayCommand(() => Notification = "");
        PresentationCommand = new RelayCommand(() => IsPresentationMode = !IsPresentationMode);
        Home();
    }

    public PerspectiveCamera Camera { get; } = new()
    { FieldOfView = 47, NearPlaneDistance = .05, FarPlaneDistance = 1200 };
    // A separate light camera keeps shadows aligned with the lamp without altering
    // the user's view. The small offset reveals shadows alongside nearby rocks.
    public PerspectiveCamera LampCamera { get; } = new()
    { FieldOfView = 96, NearPlaneDistance = .08, FarPlaneDistance = 36, UpDirection = new(0, 0, 1) };
    private OrthographicCamera OverviewLightCamera { get; } = new()
    { Position = new(24, -32, 64), LookDirection = new(-24, 32, -64), UpDirection = new(0, 0, 1),
        Width = 92, NearPlaneDistance = .5, FarPlaneDistance = 150 };
    public ProjectionCamera ShadowCamera => IsWalking ? LampCamera : OverviewLightCamera;
    public bool ShowShadows
    {
        get => _showShadows;
        set { if (Set(ref _showShadows, value)) Raise(nameof(ShadowsVisible)); }
    }
    public bool ShadowsVisible => ShowShadows && IsCave;
    public IReadOnlyList<ScenarioDefinition> Scenarios => ScenarioDefinition.All;
    public IReadOnlyList<string> DensityOptions { get; } = ["Лёгкая · 65 тыс.", "Стандарт · 130 тыс.", "Детальная · 230 тыс."];
    public ObservableCollection<int> FrameIntervals { get; } = [200, 100, 50];
    public IReadOnlyList<PointCloudQuality> PointQualities => PointCloudQuality.All;
    public PointCloudQuality PointQuality
    {
        get => _pointQuality;
        set
        {
            if (value is null || !PointQualities.Contains(value) || !Set(ref _pointQuality, value)) return;
            if (IsPoints) RefreshGeometry();
        }
    }
    public int RenderedPointCount => IsPoints ? Cloud?.Positions?.Count ?? 0 : 0;
    public string CloudDisplayText => $"На экране {RenderedPointCount:N0} из {(_viewFrames > 0 ? _pointOffsets[_viewFrames - 1] : 0):N0} точек";
    public double GeometryMilliseconds => _geometryMilliseconds;
    public double GenerationMilliseconds => _generationMilliseconds;
    public string PerformanceText => $"Подготовка 3D: {_geometryMilliseconds:F1} мс · Память: {_privateMemoryBytes / 1048576d:F0} МБ";
    public void UpdateMemory(long privateBytes)
    {
        _privateMemoryBytes = Math.Max(0, privateBytes);
        Raise(nameof(PerformanceText));
    }
    public ObservableCollection<SessionEvent> Events { get; } = [];
    public ObservableCollection<SceneMarker> VisibleMarkers { get; } = [];
    public RelayCommand PauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncRelayCommand ClearCommand { get; }
    public AsyncRelayCommand ApplyScannerCommand { get; }
    public RelayCommand HomeCommand { get; }
    public RelayCommand TopCommand { get; }
    public RelayCommand InsideCommand { get; }
    public RelayCommand PointsCommand { get; }
    public RelayCommand SurfaceCommand { get; }
    public RelayCommand HeightCommand { get; }
    public RelayCommand LatestCommand { get; }
    public RelayCommand PreviewScannerCommand { get; }
    public RelayCommand DismissNotificationCommand { get; }
    public RelayCommand PresentationCommand { get; }

    public ScenarioDefinition SelectedScenario { get => _selectedScenario; private set => Set(ref _selectedScenario, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) NotifyState(); }
    }
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) NotifyState(); }
    }
    public bool CanInteract => !IsBusy && !_confirming;
    public bool CanSave => IsAdministrator && CanInteract && _capturedFrames > 0;
    public bool CanEditSettings => IsAdministrator && CanInteract && !IsRunning;
    public bool IsEmpty => !IsBusy && !IsRunning && _viewFrames == 0;
    public string EmptyTitle => _capturedFrames > 0 ? "Начало записи" : "Готово к сканированию";
    public string EmptyHint => _capturedFrames > 0 ? "Передвиньте временную шкалу или нажмите «К последнему»" :
        "Запустите проход, чтобы увидеть, как формируется карта";
    public bool IsPoints => !IsSurface;
    public bool IsCave => IsSurface && !IsHeight;
    public bool IsHeight => _isHeight;
    public PhongMaterial SurfaceMaterial => IsHeight ? CaveMaterials.Height : _limestone ??= CaveMaterials.Limestone();
    private PhongMaterial? _limestone;
    public PhongMaterial FloorMaterial => _sediment ??= CaveMaterials.Sediment();
    public PhongMaterial DampMaterial => _dampLimestone ??= CaveMaterials.DampLimestone();
    private PhongMaterial? _sediment;
    private PhongMaterial? _dampLimestone;
    public bool IsWalking => _isWalking;
    public bool CanWalk => CanInteract && _viewFrames > 0 && WalkCoverage.End - WalkCoverage.Start >= .06f;
    public bool IsOverview => !IsWalking;
    public bool GridVisible => ShowGrid && !IsWalking;
    public bool RouteVisible => ShowRoute && !IsWalking;
    public bool MarkersVisible => ShowMarkers && !IsWalking;
    public bool ScannerVisible => !IsWalking;
    public bool MouseCaptured
    {
        get => _mouseCaptured;
        set { if (Set(ref _mouseCaptured, value)) { Raise(nameof(WalkPromptVisible)); Raise(nameof(ControlsHint)); } }
    }
    public bool WalkPromptVisible => IsWalking && !MouseCaptured;
    public string WalkPrompt => "Нажмите на сцену, чтобы начать прогулку";
    public float WalkTargetProgress => _walkTargetProgress;
    public string ControlsHint => IsWalking
        ? "WASD / стрелки — идти   ·   Мышь — осмотр   ·   Shift — быстрее   ·   Tab — курсор   ·   Esc — выйти"
        : "ЛКМ — вращение   ·   ПКМ — перемещение   ·   Колесо — масштаб   ·   Home — общий вид";
    public WalkNavigator CreateNavigator() => new(_dataset!.Scene, WalkCoverage.End, WalkCoverage.Start);
    public bool IsSurface
    {
        get => _isSurface;
        private set
        {
            if (!Set(ref _isSurface, value)) return;
            Raise(nameof(IsPoints)); Raise(nameof(IsCave)); Raise(nameof(RenderLabel)); Raise(nameof(ShadowsVisible));
        }
    }
    private void SetView(bool surface, bool height)
    {
        if (IsWalking) Home();
        _isHeight = height;
        IsSurface = surface;
        Raise(nameof(IsHeight)); Raise(nameof(IsCave)); Raise(nameof(SurfaceMaterial)); Raise(nameof(RenderLabel));
        Raise(nameof(ShadowsVisible));
        RefreshGeometry();
    }
    public string RenderLabel => IsPoints ? "ОБЛАКО ТОЧЕК" : IsHeight ? "ОКРАСКА ПО ВЫСОТЕ" : "ВИРТУАЛЬНАЯ ПЕЩЕРА";
    public bool ShowGrid { get => _showGrid; set { if (Set(ref _showGrid, value)) Raise(nameof(GridVisible)); } }
    public bool ShowRoute { get => _showRoute; set { if (Set(ref _showRoute, value)) Raise(nameof(RouteVisible)); } }
    public bool ShowMarkers { get => _showMarkers; set { if (Set(ref _showMarkers, value)) Raise(nameof(MarkersVisible)); } }
    public bool CutRoof { get => _cutRoof; set { if (Set(ref _cutRoof, value)) RefreshGeometry(); } }
    public bool FollowScanner { get => _followScanner; set => Set(ref _followScanner, value); }
    public bool IsPresentationMode { get => _isPresentationMode; set => Set(ref _isPresentationMode, value); }
    public bool IsInspectorOpen { get => _isInspectorOpen; set => Set(ref _isInspectorOpen, value); }
    public double Range
    {
        get => _range;
        set { if ((_loadingSettings || CanEditSettings) && double.IsFinite(value) && Set(ref _range, value)) SettingsChanged(); }
    }
    public double Speed
    {
        get => _speed;
        set { if (IsAdministrator && double.IsFinite(value)) Set(ref _speed, Math.Clamp(value, .5, 3)); }
    }
    public int DensityIndex
    {
        get => _densityIndex;
        set { if ((_loadingSettings || CanEditSettings) && value is >= 0 and <= 2 && Set(ref _densityIndex, value)) SettingsChanged(); }
    }
    public double StartPosition
    {
        get => _startPosition;
        set
        {
            if ((!_loadingSettings && !CanEditSettings) || !double.IsFinite(value)) return;
            if (Set(ref _startPosition, Math.Clamp(value, 0, 85))) SettingsChanged();
        }
    }
    public int FrameIntervalMilliseconds
    {
        get => _frameIntervalMilliseconds;
        set
        {
            if ((!_loadingSettings && !CanEditSettings) || value is < 25 or > 1000) return;
            if (!FrameIntervals.Contains(value))
                FrameIntervals.Insert(FrameIntervals.TakeWhile(interval => interval > value).Count(), value);
            if (Set(ref _frameIntervalMilliseconds, value))
            {
                SettingsChanged();
                Raise(nameof(FrequencyText)); Raise(nameof(PlannedDurationText));
            }
        }
    }
    public string FrequencyText => $"{1000d / FrameIntervalMilliseconds:F0} Гц";
    public string PlannedDurationText => $"Проход: {Settings.FrameCount * FrameIntervalMilliseconds / 1000d:F0} с при скорости 1×";
    public bool HasPendingSettings => _dataset is not null && Settings != _dataset.Session.Settings;
    public string SettingsHint => HasPendingSettings
        ? "Новые параметры применятся после «Сканировать заново»."
        : "Параметры применяются к новому проходу.";
    private void SettingsChanged()
    {
        Raise(nameof(StartText)); Raise(nameof(HasPendingSettings)); Raise(nameof(SettingsHint));
        Raise(nameof(CanRepositionScanner)); Raise(nameof(ScannerControlHint));
        UpdateScanner();
        CommandManager.InvalidateRequerySuggested();
    }
    public double BusyProgress { get => _busyProgress; private set => Set(ref _busyProgress, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Notification
    {
        get => _notification;
        private set { if (Set(ref _notification, value)) Raise(nameof(HasNotification)); }
    }
    public bool HasNotification => Notification.Length > 0;
    public string CameraLabel { get => _cameraLabel; private set => Set(ref _cameraLabel, value); }
    public SceneMarker? SelectedMarker
    {
        get => _selectedMarker;
        set
        {
            if (!Set(ref _selectedMarker, value) || value is null) return;
            if (IsWalking)
            {
                _walkTargetProgress = value.Progress;
                Raise(nameof(WalkTargetProgress));
                return;
            }
            FollowScanner = false;
            var p = value.Position;
            MoveCamera(p + new Vector3(12, -15, 10), p, "Метка сцены");
        }
    }
    public PointGeometry3D? Cloud { get => _cloud; private set => Set(ref _cloud, value); }
    public PointGeometry3D? Scanner { get => _scanner; private set => Set(ref _scanner, value); }
    public PointGeometry3D? Markers { get => _markers; private set => Set(ref _markers, value); }
    public MeshGeometry3D? Surface { get => _surface; private set => Set(ref _surface, value); }
    public MeshGeometry3D? FloorSurface { get => _floorSurface; private set => Set(ref _floorSurface, value); }
    public MeshGeometry3D? DampSurface { get => _dampSurface; private set => Set(ref _dampSurface, value); }
    public LineGeometry3D? Route { get => _route; private set => Set(ref _route, value); }
    public LineGeometry3D? Grid { get => _grid; private set => Set(ref _grid, value); }
    private float SessionStart => _dataset?.Session.Settings.StartProgress ?? 0;
    private float RouteProgress => _dataset is null || _viewFrames == 0 ? SessionStart :
        ScannerPlan.PositionAt(_dataset.Session.Settings, _adjustments, _viewFrames - 1);
    public double Progress => _dataset is null ? 0 : _viewFrames * 100d / _dataset.Session.Settings.FrameCount;
    public string PointCountText => (_viewFrames == 0 ? 0 : _pointOffsets[_viewFrames - 1]).ToString("N0");
    public string DistanceText => $"{(_dataset?.Scene.LengthMeters ?? 0) * VisibleCoverage.Sum(r => r.End - r.Start):F1}";
    public string DurationText => FormatDuration(_viewFrames);
    public string FrameCountText => $"{_capturedFrames} / {_dataset?.Session.Settings.FrameCount ?? 180}";
    public int CapturedFrameCount => Math.Max(0, _capturedFrames);
    public double PlaybackFrame { get => _viewFrames; set => Seek((int)Math.Round(value)); }
    public bool IsReviewing => _viewFrames < _capturedFrames;
    public string TimelineText => $"Кадр {_viewFrames} / {CapturedFrameCount} · {FormatDuration(_viewFrames)}";
    public string RecordingText => $"В сеансе {(_capturedFrames > 0 ? _pointOffsets[_capturedFrames - 1] : 0):N0} точек · {FormatDuration(CapturedFrameCount)}";
    public string ViewStateText => IsReviewing ? "Просмотр истории" : IsRunning ? "Текущий кадр" : "Последний кадр";
    private string FormatDuration(int count) => TimeSpan.FromMilliseconds(count *
        (double)(_dataset?.Session.Settings.FrameIntervalMilliseconds ?? FrameIntervalMilliseconds)).ToString(@"mm\:ss\.f");
    public string HeightMinText => $"{_dataset?.Scene.MinimumHeight ?? -3:F1} м";
    public string HeightMaxText => $"{_dataset?.Scene.MaximumHeight ?? 4:F1} м";
    public string TotalLengthText => $"{(_dataset?.Scene.LengthMeters ?? 0) * (1 - SessionStart):F0} м";
    public string MarkerCountText => $"{VisibleMarkers.Count:00}";
    private ScanSettings Settings => (_baselineSettings ?? new ScanSettings()) with
    {
        RangeMeters = (float)Range,
        RaysPerFrame = _baselineSettings is not null && DensityIndex == _baselineDensityIndex
            ? _baselineSettings.RaysPerFrame : DensityIndex switch { 0 => 360, 2 => 1280, _ => 720 },
        StartProgress = (float)(StartPosition / 100), FrameIntervalMilliseconds = FrameIntervalMilliseconds
    };
    private bool CanResume => _dataset is not null && _capturedFrames > 0 &&
        _capturedFrames < _dataset.Session.Settings.FrameCount && Settings == _dataset.Session.Settings &&
        SelectedScenario.Id == _dataset.Session.ScenarioId;
    public string StartText => IsGuest ? "Нужен вход администратора" : IsRunning ? "Сканирование идёт" : CanResume ? "Продолжить сканирование" :
        _capturedFrames > 0 ? "Сканировать заново" : "Начать сканирование";

    public async Task LoadDemoAsync(ScenarioDefinition? scenario = null)
    {
        if (_disposed || !CanInteract || !await ConfirmReplacementAsync("сменить демонстрационную сцену")) return;
        scenario ??= SelectedScenario;
        ExitWalking();
        StopCapture();
        IsBusy = true;
        Notification = "";
        Raise(nameof(HasNotification));
        BusyProgress = 0;
        Status = "Генерация виртуальной сцены";
        _generation?.Cancel();
        _generation?.Dispose();
        _generation = new CancellationTokenSource();
        try
        {
            await _captureTask.WaitAsync(TimeSpan.FromSeconds(2));
            var progress = new Progress<double>(value => BusyProgress = value);
            var generationClock = Stopwatch.StartNew();
            var dataset = await _source.GenerateAsync(scenario, Settings, progress, _generation.Token);
            if (_disposed) return;
            _generationMilliseconds = generationClock.Elapsed.TotalMilliseconds;
            Raise(nameof(GenerationMilliseconds));
            SelectedScenario = scenario;
            Assign(dataset, dataset.Session.Frames.Length);
            _sessionPath = null;
            HasUnsavedChanges = false;
            Raise(nameof(SessionSaveLabel));
            Status = "Демонстрационная карта готова";
            Home();
            AddEvent("Виртуальная пещера построена");
            AddEvent($"Получено {dataset.Session.PointCount:N0} точек");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError("Не удалось создать сцену", ex); }
        finally { IsBusy = false; }
    }

    public async Task StartAsync()
    {
        if (_disposed || !CanStart) return;
        if (IsWalking) Home();
        var resume = CanResume;
        if (!resume && !await ConfirmReplacementAsync("начать новый проход")) return;
        if (!IsAdministrator) return;
        IsBusy = true;
        Notification = "";
        Status = resume ? "Подготовка продолжения прохода" : "Подготовка виртуального датчика";
        try
        {
            await _captureTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (_disposed || !IsAdministrator) return;
            if (!resume)
            {
                var settings = Settings;
                settings.Validate();
                var scenario = SelectedScenario;
                var scene = _dataset?.Scene.Scenario.Id == scenario.Id && _dataset.Scene.SceneVersion == SceneVersions.Current ? _dataset.Scene :
                    await Task.Run(() => CaveFactory.Create(scenario));
                if (_disposed || !IsAdministrator) return;
                Assign(new(scene, new() { ScenarioId = scenario.Id, Settings = settings, Frames = [] }), 0);
                _sessionPath = null;
                HasUnsavedChanges = false;
                Raise(nameof(SessionSaveLabel));
            }
            _viewFrames = _capturedFrames;
            NotifyView();
            if (!IsAdministrator || _disposed) return;
            _captureCancellation?.Dispose();
            _captureCancellation = new CancellationTokenSource();
            IsRunning = true;
            Status = "Выполняется потоковое сканирование";
            var session = _dataset!.Session;
            var request = new ScanRequest(session.ScenarioId, session.Settings, _capturedFrames,
                session.Frames.FirstOrDefault()?.Timestamp, _adjustments.ToArray(), session.SceneVersion);
            _previewScanner = false;
            _captureTask = CaptureAsync(request, ++_runId, _captureCancellation.Token);
            AddEvent(resume ? "Сканирование продолжено с сохранённого кадра" : "Начат новый поток измерений");
        }
        catch (Exception ex) { ReportError("Не удалось запустить сканирование", ex); }
        finally { IsBusy = false; }
    }

    public void Pause()
    {
        if (!IsRunning) return;
        StopCapture();
        ResetScannerPreview();
        SynchronizeCaptureView();
        Status = "Сканирование на паузе";
        AddEvent("Пауза · измерения сохранены");
    }

    private void Stop()
    {
        if (!IsAdministrator || !CanInteract) return;
        StopCapture();
        ResetScannerPreview();
        SynchronizeCaptureView();
        Status = "Проход остановлен";
        AddEvent("Сканирование остановлено");
    }

    public async Task ClearAsync()
    {
        if (!CanSave || !await ConfirmReplacementAsync("очистить карту") || !IsAdministrator) return;
        if (IsWalking) Home();
        StopCapture();
        if (_dataset is not null)
            Assign(new(_dataset.Scene, new() { ScenarioId = _dataset.Session.ScenarioId,
                SceneVersion = _dataset.Session.SceneVersion, Settings = _dataset.Session.Settings, Frames = [] }), 0);
        Status = "Готово к сканированию";
        _sessionPath = null;
        HasUnsavedChanges = false;
        Raise(nameof(SessionSaveLabel));
        AddEvent("Карта очищена");
    }

    private void StopCapture()
    {
        ++_runId;
        _captureCancellation?.Cancel();
        IsRunning = false;
    }

    private async Task CaptureAsync(ScanRequest request, int runId, CancellationToken cancellationToken)
    {
        var renderClock = Stopwatch.StartNew();
        var frameClock = Stopwatch.StartNew();
        try
        {
            await foreach (var frame in _frameSource.ReadFramesAsync(request, cancellationToken))
            {
                if (_disposed || runId != _runId || cancellationToken.IsCancellationRequested) return;
                _map!.Append(frame);
                _geometry!.AppendFrame(frame);
                _dataset = new(_dataset!.Scene, _map.Snapshot(_adjustments.ToArray()));
                _pointOffsets[_map.FrameCount - 1] = _map.PointCount;
                _capturedFrames = _map.FrameCount;
                _viewFrames = _capturedFrames;
                HasUnsavedChanges = true;
                if (renderClock.ElapsedMilliseconds >= 100 || _capturedFrames == request.Settings.FrameCount)
                {
                    SynchronizeCaptureView();
                    if (FollowScanner) FollowCamera();
                    renderClock.Restart();
                }
                if (_capturedFrames == request.Settings.FrameCount) break;
                var remaining = request.Settings.FrameIntervalMilliseconds / Speed - frameClock.Elapsed.TotalMilliseconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellationToken);
                frameClock.Restart();
            }
            if (_disposed || runId != _runId) return;
            IsRunning = false;
            SynchronizeCaptureView();
            if (_capturedFrames != request.Settings.FrameCount)
                throw new InvalidOperationException("Поток измерений завершился до конца прохода. Полученная часть карты сохранена в сеансе.");
            Status = "Сканирование завершено";
            AddEvent("Поток завершён · карта готова");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed || runId != _runId) return;
            IsRunning = false;
            SynchronizeCaptureView();
            ReportError("Источник измерений остановлен", ex);
        }
    }

    private void SynchronizeCaptureView()
    {
        Raise(nameof(CapturedFrameCount)); Raise(nameof(FrameCountText)); Raise(nameof(RecordingText));
        NotifyView();
    }

    private void Assign(ScanDataset dataset, int capturedFrames)
    {
        // Release previous display buffers, including the currently hidden mode.
        Cloud = null;
        Surface = null;
        FloorSurface = null;
        DampSurface = null;
        _dataset = dataset;
        _adjustments = dataset.Session.Adjustments.ToList();
        _map = new(dataset.Session.ScenarioId, dataset.Session.Settings, dataset.Session.SceneVersion);
        _geometry = new SceneGeometry(dataset.Scene);
        _pointOffsets = new int[dataset.Session.Settings.FrameCount];
        var total = 0;
        for (var i = 0; i < dataset.Session.Frames.Length; i++)
        {
            var frame = dataset.Session.Frames[i];
            _map.Append(frame);
            _geometry.AppendFrame(frame);
            _pointOffsets[i] = total += frame.Points.Length;
        }
        _baselineSettings = dataset.Session.Settings;
        _baselineDensityIndex = DensityIndex;
        Grid = SceneGeometry.Grid(dataset.Scene.MinimumHeight - .5f);
        _capturedFrames = -1;
        SetCapture(capturedFrames);
        Raise(nameof(HeightMinText)); Raise(nameof(HeightMaxText)); Raise(nameof(TotalLengthText));
        SettingsChanged();
        Raise(nameof(PlannedDurationText));
        ResetScannerPreview();
    }

    private void SetCapture(int count)
    {
        if (_capturedFrames == count) return;
        _capturedFrames = count;
        _viewFrames = count;
        Raise(nameof(CapturedFrameCount)); Raise(nameof(FrameCountText)); Raise(nameof(RecordingText));
        NotifyView();
    }
    private void Seek(int count)
    {
        if (IsBusy || _dataset is null) return;
        count = Math.Clamp(count, 0, CapturedFrameCount);
        if (_viewFrames == count) return;
        if (IsWalking) Home();
        Pause();
        _viewFrames = count;
        NotifyView();
    }
    private void ShowLatest() => Seek(CapturedFrameCount);
    private void NotifyView()
    {
        RefreshGeometry();
        Raise(nameof(Progress)); Raise(nameof(PointCountText)); Raise(nameof(DistanceText));
        Raise(nameof(DurationText)); Raise(nameof(PlaybackFrame)); Raise(nameof(TimelineText));
        Raise(nameof(IsReviewing)); Raise(nameof(ViewStateText));
        NotifyState();
    }
    private void NotifyState()
    {
        Raise(nameof(CanInteract)); Raise(nameof(CanSave)); Raise(nameof(CanEditSettings));
        Raise(nameof(CanStart)); Raise(nameof(CanChangeScenario)); Raise(nameof(CanRepositionScanner)); Raise(nameof(ScannerControlHint));
        Raise(nameof(IsEmpty)); Raise(nameof(StartText));
        Raise(nameof(EmptyTitle)); Raise(nameof(EmptyHint));
        Raise(nameof(CanWalk));
        Raise(nameof(ViewStateText));
        UpdateScanner();
        CommandManager.InvalidateRequerySuggested();
    }
    private void RefreshGeometry()
    {
        if (_dataset is null || _geometry is null || _viewFrames < 0) return;
        var clock = Stopwatch.StartNew();
        var coverage = VisibleCoverage;
        if (!IsSurface) Cloud = _geometry.Cloud(_viewFrames == 0 ? 0 : _pointOffsets[_viewFrames - 1], CutRoof, PointQuality.PointLimit);
        else if (IsHeight)
        {
            Surface = _geometry.Surface(coverage, CutRoof && !IsWalking, true);
            FloorSurface = null; DampSurface = null;
        }
        else
        {
            var layers = _geometry.SurfaceLayers(coverage, CutRoof && !IsWalking);
            Surface = layers.Rock; FloorSurface = layers.Floor; DampSurface = layers.Damp;
        }
        Route = _geometry.Route(coverage);
        UpdateScanner();
        var markerCoverage = IsWalking ? new[] { WalkCoverage } : coverage;
        var visible = _dataset.Scene.Markers.Where(m => markerCoverage.Any(r => m.Progress >= r.Start && m.Progress <= r.End)).ToArray();
        Markers = SceneGeometry.Points(visible.Select(m => m.Position));
        if (!VisibleMarkers.SequenceEqual(visible))
        {
            VisibleMarkers.Clear();
            foreach (var marker in visible) VisibleMarkers.Add(marker);
            Raise(nameof(MarkerCountText));
        }
        _geometryMilliseconds = clock.Elapsed.TotalMilliseconds;
        Raise(nameof(GeometryMilliseconds)); Raise(nameof(PerformanceText));
        Raise(nameof(RenderedPointCount)); Raise(nameof(CloudDisplayText));
    }
    private void UpdateScanner()
    {
        if (_dataset is null || _viewFrames < 0) return;
        var preview = !IsRunning && Math.Abs(StartPosition / 100 - SessionStart) > .0001;
        var pose = preview ? CaveFactory.Pose(SelectedScenario.Id, (float)StartPosition / 100 + .002f) :
            _viewFrames > 0 ? _dataset.Session.Frames[_viewFrames - 1].Pose : CaveFactory.Pose(SelectedScenario.Id, SessionStart + .002f);
        if (_previewScanner && !HasPendingSettings && !IsRunning && IsAdministrator && !IsReviewing)
        {
            pose = ScannerPlan.Pose(_dataset.Scene.Scenario.Id, ProposedAdjustment.RoutePosition, ProposedAdjustment);
        }
        Scanner = SceneGeometry.Points([pose.Position]);
        ScannerHeading = SceneGeometry.Lines([pose.Position, pose.Position + Vector3.Transform(Vector3.UnitY, pose.Orientation) * 2]);
        Raise(nameof(ScannerHeading));
        Raise(nameof(ScannerCaption));
    }
    public string ScannerCaption => _previewScanner && !HasPendingSettings && !IsRunning && IsAdministrator && !IsReviewing ? "Позиция следующего кадра" :
        !IsRunning && Math.Abs(StartPosition / 100 - SessionStart) > .0001
        ? $"Новый старт · {StartPosition:F0}% маршрута" : "Виртуальный датчик";
    private void PreviewScanner()
    {
        var p = CaveFactory.Center(SelectedScenario.Id, (float)(StartPosition / 100));
        FollowScanner = false;
        MoveCamera(p + new Vector3(8, -11, 9), p, "Старт нового прохода");
    }

    public void Home()
    {
        ExitWalking();
        FollowScanner = false;
        Camera.FieldOfView = 47;
        var center = new Vector3(0, 1, -1);
        MoveCamera(center + new Vector3(12, -47, 35), center, "Общий вид");
    }
    private void Top()
    {
        ExitWalking();
        Camera.FieldOfView = 47;
        FollowScanner = false;
        var center = SelectedScenario.Id == "aurora" ? new Vector3(3, -1, 0) : Vector3.Zero;
        Camera.UpDirection = new Vector3D(0, 1, 0);
        Camera.Position = new Point3D(center.X, center.Y, 83);
        Camera.LookDirection = new Vector3D(0, 0, -83);
        CameraLabel = "Вид сверху";
    }
    private void Inside()
    {
        if (!CanWalk) return;
        Pause();
        FollowScanner = false;
        _isHeight = false;
        IsSurface = true;
        _isWalking = true;
        Camera.FieldOfView = 72;
        CameraLabel = "Прогулка · высота глаз 1,65 м";
        RefreshGeometry();
        Raise(nameof(IsHeight)); Raise(nameof(IsCave)); Raise(nameof(SurfaceMaterial)); Raise(nameof(RenderLabel));
        NotifyWalking();
    }
    private void ExitWalking()
    {
        if (!_isWalking) return;
        _isWalking = false;
        Camera.FieldOfView = 47;
        NotifyWalking();
        RefreshGeometry();
    }
    private void NotifyWalking()
    {
        Raise(nameof(IsWalking)); Raise(nameof(IsOverview));
        Raise(nameof(ShadowCamera)); Raise(nameof(ShadowsVisible));
        Raise(nameof(GridVisible)); Raise(nameof(RouteVisible)); Raise(nameof(MarkersVisible)); Raise(nameof(ScannerVisible));
        Raise(nameof(ControlsHint)); Raise(nameof(WalkPromptVisible));
        Raise(nameof(CanRepositionScanner)); Raise(nameof(ScannerControlHint));
    }
    public void UpdateWalkPosition(WalkNavigator navigator)
    {
        var p = navigator.Position; var d = navigator.Forward;
        Camera.Position = new(p.X, p.Y, p.Z);
        Camera.LookDirection = new(d.X, d.Y, d.Z);
        Camera.UpDirection = new(0, 0, 1);
        var right = Vector3.Normalize(Vector3.Cross(d, Vector3.UnitZ));
        var lamp = p + right * .18f + Vector3.UnitZ * -.08f;
        LampCamera.Position = new(lamp.X, lamp.Y, lamp.Z);
        LampCamera.LookDirection = new(d.X, d.Y, d.Z);
        CameraLabel = $"Прогулка · {navigator.DistanceTravelled:F1} м";
    }
    private void FollowCamera()
    {
        var t = Math.Clamp(RouteProgress, .01f, .98f);
        var point = CaveFactory.Center(SelectedScenario.Id, t);
        MoveCamera(point + new Vector3(10, -13, 10), point, "Следование за сканером");
    }
    private void MoveCamera(Vector3 position, Vector3 target, string label)
    {
        Camera.UpDirection = new Vector3D(0, 0, 1);
        Camera.Position = new(position.X, position.Y, position.Z);
        var direction = target - position;
        Camera.LookDirection = new(direction.X, direction.Y, direction.Z);
        CameraLabel = label;
    }

    public Task<bool> SaveAsync(string path) => !CanSave ? Task.FromResult(false) : SaveCoreAsync(path);

    private async Task<bool> SaveCoreAsync(string path)
    {
        if (_dataset is null || !IsAdministrator || IsBusy || _capturedFrames == 0) return false;
        Pause();
        IsBusy = true;
        Status = "Сохранение сеанса";
        try
        {
            await _storage.SaveAsync(path, _dataset.Session, _capturedFrames);
            _sessionPath = path;
            HasUnsavedChanges = false;
            Raise(nameof(SessionSaveLabel));
            Status = "Сеанс сохранён"; AddEvent("Сеанс сохранён");
            return true;
        }
        catch (Exception ex) { ReportError("Не удалось сохранить сеанс", ex); return false; }
        finally { IsBusy = false; }
    }
    public async Task OpenAsync(string path)
    {
        if (!CanInteract || !await ConfirmReplacementAsync("открыть другой сеанс")) return;
        ExitWalking();
        Pause();
        IsBusy = true;
        Status = "Открытие сеанса";
        try
        {
            var session = await _storage.LoadAsync(path);
            var scenario = Scenarios.First(s => s.Id == session.ScenarioId);
            var scene = await Task.Run(() => CaveFactory.Create(scenario, session.SceneVersion));
            if (_disposed) return;
            SelectedScenario = scenario;
            _loadingSettings = true;
            Range = session.Settings.RangeMeters;
            DensityIndex = session.Settings.RaysPerFrame <= 360 ? 0 : session.Settings.RaysPerFrame >= 1280 ? 2 : 1;
            StartPosition = session.Settings.StartProgress * 100d;
            FrameIntervalMilliseconds = session.Settings.FrameIntervalMilliseconds;
            _loadingSettings = false;
            Assign(new(scene, session), session.Frames.Length);
            _sessionPath = path;
            HasUnsavedChanges = false;
            Raise(nameof(SessionSaveLabel));
            Status = "Сохранённый сеанс открыт";
            Notification = ""; Raise(nameof(HasNotification));
            Home();
            AddEvent("Из файла восстановлены все измерения");
        }
        catch (Exception ex) { ReportError("Не удалось открыть сеанс", ex); }
        finally { _loadingSettings = false; IsBusy = false; }
    }
    public async Task ExportAsync(string path)
    {
        if (_dataset is null || !CanSave) return;
        Pause();
        IsBusy = true;
        Status = "Экспорт облака точек";
        try
        {
            var points = _dataset.Session.Frames.Take(_capturedFrames).SelectMany(f => f.Points);
            await _storage.ExportPlyAsync(path, points);
            Status = "Облако точек экспортировано";
            AddEvent("Облако точек экспортировано в PLY");
        }
        catch (Exception ex) { ReportError("Не удалось экспортировать точки", ex); }
        finally { IsBusy = false; }
    }
    public void ReportError(string context, Exception ex)
    {
        Notification = $"{context}. {ex.Message}";
        Raise(nameof(HasNotification));
        Status = "Операция не выполнена";
        AddEvent(context);
    }
    private void AddEvent(string message)
    {
        Events.Insert(0, new(DateTime.Now.ToString("HH:mm"), message));
        while (Events.Count > 20) Events.RemoveAt(Events.Count - 1);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _access.AccessChanged -= AccessChanged;
        StopCapture();
        _generation?.Cancel();
        _generation?.Dispose();
        _captureCancellation?.Dispose();
    }
}
