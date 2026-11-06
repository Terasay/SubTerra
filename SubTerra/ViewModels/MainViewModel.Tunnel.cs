using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Numerics;
using HelixToolkit.SharpDX;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.Simulation;

namespace SubTerra.ViewModels;

public sealed record ProjectModeChoice(string Label, ProjectAnalysisMode Mode);
public sealed record TunnelSectionItem(TunnelSection Section)
{
    public string Title => $"{Section.Chainage:F1} м · {(Section.IsClosed ? "замкнуто" : "неполное")}";
    public string Details => $"Наблюдаемый габарит {Section.Width:F2} × {Section.Height:F2} м · угловое покрытие {Section.Coverage:P0}";
}

public sealed partial class MainViewModel
{
    private string _tunnelAxisText = "";
    private double _tunnelSectionSpacing = 2, _tunnelSliceThickness = .3, _tunnelMaximumRadius = 8;
    private TunnelAnalysisDocument? _loadedTunnel;
    private ProjectAnalysisMode _loadedProjectMode;
    private bool _geometryShading;
    private TunnelSectionItem? _selectedTunnelSection;
    public IReadOnlyList<ProjectModeChoice> ProjectModes { get; } =
        [new("Помещение", ProjectAnalysisMode.Room), new("Тоннель / шахта", ProjectAnalysisMode.Tunnel)];
    public ProjectModeChoice SelectedProjectMode
    {
        get => ProjectModes.First(m => m.Mode == (_scanProject?.AnalysisMode ?? ProjectAnalysisMode.Room));
        set
        {
            if (!CanEditTunnel || value is null || !ProjectModes.Contains(value) || value.Mode == _scanProject!.AnalysisMode) return;
            EndInspection(); EndScreenSelection(); ReturnToOverview();
            CommitTunnelProject(_scanProject with { AnalysisMode = value.Mode }, "режим: " + value.Label);
            SelectAnalysisTool(value.Mode == ProjectAnalysisMode.Tunnel ? "Тоннель" : "Поверхности");
        }
    }
    public bool IsTunnelMode => IsImported && _scanProject?.AnalysisMode == ProjectAnalysisMode.Tunnel;
    public bool IsRoomMode => IsImported && !IsTunnelMode;
    public bool CanEditTunnel => CanManageProject && !IsScreenSelecting;
    public bool CanAnalyzeTunnel => CanEditTunnel && IsTunnelMode && _scanProject!.Objects.Any(o => o.IsVisible && o.PointCount > 0);
    public bool CanAppendTunnelCamera => CanInteract && IsImported && IsAdministrator && !HasRecovery && !HasEditPreview;
    public bool IsGeometryShading
    {
        get => IsImported && _geometryShading;
        set
        {
            if (!IsImported || !CanInteract || !Set(ref _geometryShading, value)) return;
            NotifyReliefState(); RefreshGeometry();
        }
    }
    public bool ReliefLampVisible => IsGeometryShading && IsFlying;
    public string TunnelAxisText
    {
        get => _tunnelAxisText;
        set { if (CanEditTunnel && value is not null && value.Length <= 24000 && Set(ref _tunnelAxisText, value)) { Raise(nameof(TunnelDraftPending)); UpdateTunnelDraftGeometry(); } }
    }
    public double TunnelSectionSpacing
    {
        get => _tunnelSectionSpacing;
        set { if (CanEditTunnel && double.IsFinite(value) && Set(ref _tunnelSectionSpacing, value)) Raise(nameof(TunnelDraftPending)); }
    }
    public double TunnelSliceThickness
    {
        get => _tunnelSliceThickness;
        set { if (CanEditTunnel && double.IsFinite(value) && Set(ref _tunnelSliceThickness, value)) Raise(nameof(TunnelDraftPending)); }
    }
    public double TunnelMaximumRadius
    {
        get => _tunnelMaximumRadius;
        set { if (CanEditTunnel && double.IsFinite(value) && Set(ref _tunnelMaximumRadius, value)) Raise(nameof(TunnelDraftPending)); }
    }
    private TunnelAnalysisOptions TunnelOptions => (ActiveTunnelDocument?.Options ?? new()) with
        { SectionSpacing = TunnelSectionSpacing, SliceThickness = TunnelSliceThickness, MaximumRadius = TunnelMaximumRadius };
    public double TunnelContourGap => ActiveTunnelDocument?.Options.MaximumContourGap ?? .75;
    public bool TunnelDraftPending => ActiveTunnelDocument is { } d ?
        TunnelAxisText != FormatAxis(d.Axis) || TunnelOptions != d.Options : TunnelAxisText.Length > 0 || TunnelOptions != new TunnelAnalysisOptions();
    public bool TunnelIsStale => ActiveTunnelDocument?.IsStale == true;
    public bool HasTunnelSections => IsTunnelMode && !TunnelIsStale && !HasEditPreview && TunnelSections.Count > 0;
    public string TunnelSummary => ActiveTunnelDocument is not { } d ? "Предложите маршрут автоматически или задайте ось внутри прохода." :
        (d.IsStale ? "СКАНЫ ИЗМЕНИЛИСЬ · повторите расчёт. " : "") +
        $"Ось {d.AxisLength:F2} м · сечений: {d.Sections.Length} · замкнутых: {d.Sections.Count(s => s.IsClosed)}. " +
        (d.Sections.Length == 0 ? "Ось сохранена; сечения ещё не рассчитаны." : $"Выборка {d.SampleCount:N0} из {d.SourcePointCount:N0} точек.");
    public string TunnelSectionMetrics => TunnelIsStale ? "Сечения устарели. Повторите расчёт по текущим сканам." : SelectedTunnelSection?.Section is not { } s ? "Выберите сечение." :
        $"От начала оси: {s.Chainage:F2} м\nНаблюдаемая ширина: {s.Width:F2} м\nНаблюдаемая высота: {s.Height:F2} м\n" +
        (s.AreaSquareMeters is { } area ? $"Площадь замкнутого профиля: ≈ {area:F2} м²" : "Полная площадь неизвестна · профиль не замкнут") +
        $"\nУклон оси: {s.SlopeDegrees:F1}° · точек: {s.PointCount:N0}\nУгловое покрытие: {s.Coverage:P0}";
    public ObservableCollection<TunnelSectionItem> TunnelSections { get; } = [];
    public TunnelSectionItem? SelectedTunnelSection
    {
        get => _selectedTunnelSection;
        set
        {
            if (value is not null && !TunnelSections.Contains(value) || !Set(ref _selectedTunnelSection, value)) return;
            Raise(nameof(TunnelSectionMetrics)); Raise(nameof(CurrentTunnelSection)); UpdateTunnelGeometry();
        }
    }
    public TunnelSection? CurrentTunnelSection => HasTunnelSections ? SelectedTunnelSection?.Section : null;
    public LineGeometry3D? TunnelAxisGeometry { get; private set; }
    public PointGeometry3D? TunnelAxisNodes { get; private set; }
    public LineGeometry3D? TunnelSectionGeometry { get; private set; }
    public RelayCommand ApplyTunnelAxisCommand { get; private set; } = null!;
    public AsyncRelayCommand AnalyzeTunnelCommand { get; private set; } = null!;
    public RelayCommand AppendTunnelCameraCommand { get; private set; } = null!;
    public RelayCommand AppendTunnelMidpointCommand { get; private set; } = null!;
    public RelayCommand FocusTunnelSectionCommand { get; private set; } = null!;
    public RelayCommand ResetTunnelDraftCommand { get; private set; } = null!;
    public AsyncRelayCommand LoadTunnelDemoCommand { get; private set; } = null!;

    private void InitializeTunnelCommands()
    {
        InitializeTunnelWorkspaceCommands();
        ApplyTunnelAxisCommand = new(ApplyTunnelAxis, () => CanEditTunnel && IsTunnelMode);
        AnalyzeTunnelCommand = new(AnalyzeTunnelAsync, () => CanAnalyzeTunnel, ex => ReportError("Не удалось рассчитать сечения", ex));
        AppendTunnelCameraCommand = new(() => AppendTunnelAxisPoint(new((float)Camera.Position.X, (float)Camera.Position.Y, (float)Camera.Position.Z)), () => CanAppendTunnelCamera && IsTunnelMode);
        AppendTunnelMidpointCommand = new(() =>
        {
            if (_measurementStart is { } a && _measurementEnd is { } b) AppendTunnelAxisPoint((a.Position + b.Position) / 2);
        }, () => CanEditTunnel && IsTunnelMode && _measurementStart is not null && _measurementEnd is not null);
        FocusTunnelSectionCommand = new(FocusTunnelSection, () => CanInteract && CurrentTunnelSection is not null);
        ResetTunnelDraftCommand = new(() => { SyncTunnelState(true); Notification = ""; }, () => CanEditTunnel);
        LoadTunnelDemoCommand = new(LoadTunnelDemoAsync, () => CanChangeScenario && !HasRecovery && !HasEditPreview,
            ex => ReportError("Не удалось открыть учебный тоннель", ex));
    }
    private static string FormatAxis(IEnumerable<InspectionPoint> axis) => string.Join(Environment.NewLine,
        axis.Select(p => FormattableString.Invariant($"{p.X:G9} {p.Y:G9} {p.Z:G9}")));
    private InspectionPoint[] ParseTunnelAxis()
    {
        var lines = TunnelAxisText.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 128) throw new InvalidDataException("Ось: максимум 128 точек.");
        var points = new List<InspectionPoint>();
        foreach (var line in lines)
        {
            var values = line.Split([' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (values.Length != 3) throw new InvalidDataException($"Точка оси {points.Count + 1}: нужны X Y Z в метрах, разделённые пробелами.");
            float Number(string text) => float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number)
                ? number : throw new InvalidDataException($"Точка оси {points.Count + 1}: некорректное число.");
            points.Add(new(Number(values[0]), Number(values[1]), Number(values[2])));
        }
        return points.ToArray();
    }
    private void AppendTunnelAxisPoint(Vector3 point)
    {
        if (!CanAppendTunnelCamera || !IsTunnelMode) return;
        try
        {
            var points = ParseTunnelAxis();
            if (points.Length >= 128 || !InspectionPoint.From(point).IsValid) throw new InvalidDataException("Нельзя добавить точку оси.");
            _tunnelAxisText = FormatAxis([.. points, InspectionPoint.From(point)]);
            Raise(nameof(TunnelAxisText)); Raise(nameof(TunnelDraftPending));
            UpdateTunnelDraftGeometry();
            Status = "Точка добавлена в черновик оси · примените ось или рассчитайте сечения";
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { ReportError("Проверьте ось", ex); }
    }
    private void ApplyTunnelAxis()
    {
        if (!CanEditTunnel || !IsTunnelMode) return;
        try
        {
            var axis = ParseTunnelAxis(); var options = TunnelOptions; options.Validate();
            if (ActiveTunnelDocument is { } current && axis.SequenceEqual(current.Axis) && options == current.Options) { Notification = ""; return; }
            var document = TunnelAnalyzer.Create(axis, options);
            CommitTunnelProject(_scanProject! with { Tunnel = ReplaceActiveTunnel(document) }, "изменение оси тоннеля");
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { ReportError("Проверьте ось и параметры", ex); }
    }
    public async Task AnalyzeTunnelAsync()
    {
        if (!CanAnalyzeTunnel || _scanProject is not { } snapshot) return;
        InspectionPoint[] axis; TunnelAnalysisOptions options;
        try { axis = ParseTunnelAxis(); options = TunnelOptions; options.Validate(); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { ReportError("Проверьте ось и параметры", ex); return; }
        EndInspection();
        var token = BeginOperation("Сечения тоннеля · анализ геометрии без RGB");
        try
        {
            var document = await TunnelAnalyzer.AnalyzeAsync(snapshot, axis, options, OperationProgress(token), token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator || !ReferenceEquals(_scanProject, snapshot)) return;
            CommitTunnelProject(snapshot with { Tunnel = ReplaceActiveTunnel(document) }, "расчёт сечений тоннеля");
            Notification = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось рассчитать сечения", ex); }
        finally { EndOperation(); NotifyTunnelState(); }
    }
    private void CommitTunnelProject(ScanProject next, string description)
    {
        if (_disposed || !IsAdministrator || _scanProject is not { } before || _projectView is null) return;
        _projectHistory.Commit(before, next, description);
        SetProject(next, _projectView); MarkSessionDirty();
        if (IsSurface && !CanShowSurface) SetView(false, false); else RefreshGeometry();
        Notification = ""; Status = description; AddEvent(description);
    }
    private void SyncTunnelState(bool force = false)
    {
        SyncTunnelWorkspace();
        var document = ActiveTunnelDocument; var mode = _scanProject?.AnalysisMode ?? ProjectAnalysisMode.Room;
        if (!force && ReferenceEquals(document, _loadedTunnel) && mode == _loadedProjectMode) return;
        var modeChanged = mode != _loadedProjectMode;
        if (modeChanged) { _geometryShading = mode == ProjectAnalysisMode.Tunnel; NotifyReliefState(); NotifyRoomState(); NotifyIndoorState(); }
        var keepDraft = !force && mode == _loadedProjectMode && document is not null && _loadedTunnel is not null &&
            ReferenceEquals(document.Axis, _loadedTunnel.Axis) && document.Options == _loadedTunnel.Options;
        _loadedTunnel = document; _loadedProjectMode = mode;
        if (!keepDraft)
        {
            _tunnelAxisText = document is null ? "" : FormatAxis(document.Axis);
            var options = document?.Options ?? new();
            _tunnelSectionSpacing = options.SectionSpacing; _tunnelSliceThickness = options.SliceThickness; _tunnelMaximumRadius = options.MaximumRadius;
        }
        var selected = SelectedTunnelSection?.Section.Id;
        TunnelSections.Clear();
        if (document is not null) foreach (var section in document.Sections) TunnelSections.Add(new(section));
        _selectedTunnelSection = TunnelSections.FirstOrDefault(s => s.Section.Id == selected) ?? TunnelSections.FirstOrDefault();
        foreach (var name in new[] { nameof(TunnelAxisText), nameof(TunnelSectionSpacing), nameof(TunnelSliceThickness), nameof(TunnelMaximumRadius), nameof(SelectedTunnelSection) }) Raise(name);
        UpdateTunnelGeometry(); UpdateTunnelDraftGeometry(); NotifyTunnelState();
        if (modeChanged) NotifyPresentationState();
    }
    private void NotifyReliefState()
    {
        foreach (var name in new[] { nameof(IsGeometryShading), nameof(ReliefLampVisible), nameof(SurfaceMaterial), nameof(UsesOriginalMaterials), nameof(SourceBadge) }) Raise(name);
    }
    private void NotifyTunnelState()
    {
        foreach (var name in new[] { nameof(SelectedProjectMode), nameof(IsTunnelMode), nameof(IsRoomMode), nameof(CanEditTunnel), nameof(CanAnalyzeTunnel),
            nameof(CanAppendTunnelCamera), nameof(TunnelDraftPending), nameof(TunnelSummary), nameof(TunnelIsStale), nameof(HasTunnelSections), nameof(TunnelSectionMetrics), nameof(CurrentTunnelSection), nameof(TunnelContourGap) }) Raise(name);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        NotifyTunnelWorkspace();
    }
    private void UpdateTunnelGeometry()
    {
        var document = IsTunnelMode && !HasEditPreview ? ActiveTunnelDocument : null;
        var axis = document?.Axis ?? [];
        var lines = new List<Vector3>();
        for (var i = 1; i < axis.Length; i++) lines.AddRange([axis[i - 1].Position, axis[i].Position]);
        TunnelAxisGeometry = SceneGeometry.Lines(lines); TunnelAxisNodes = SceneGeometry.Points(axis.Select(p => p.Position));
        lines.Clear();
        if (document is { IsStale: false } && _selectedTunnelSection?.Section is { } section)
            for (var i = 0; i < section.Contour.Length; i++)
            {
                if (!section.ConnectsToNext(i, document.Options.MaximumContourGap)) continue;
                Vector3 World(TunnelContourPoint p) => section.Center.Position + section.Right.Position * (float)p.X + section.Up.Position * (float)p.Y;
                lines.AddRange([World(section.Contour[i]), World(section.Contour[(i + 1) % section.Contour.Length])]);
            }
        TunnelSectionGeometry = SceneGeometry.Lines(lines);
        Raise(nameof(TunnelAxisGeometry)); Raise(nameof(TunnelAxisNodes)); Raise(nameof(TunnelSectionGeometry));
        UpdateTunnelBranchGeometry();
    }
    private void FocusTunnelSection()
    {
        if (!CanInteract || CurrentTunnelSection is not { } section) return;
        if (IsPlanView) SelectWorkspaceView("3D");
        ReturnToOverview();
        var distance = Math.Max(1, Math.Max(section.Width, section.Height) * 1.5);
        MoveCamera(section.Center.Position - section.Forward.Position * (float)distance, section.Center.Position, $"Сечение · {section.Chainage:F1} м");
        Camera.UpDirection = new(section.Up.X, section.Up.Y, section.Up.Z);
    }
    public async Task LoadTunnelDemoAsync()
    {
        if (!CanChangeScenario || HasRecovery || HasEditPreview || !await ConfirmReplacementAsync("открыть учебный тоннель")) return;
        ReturnToOverview();
        var token = BeginOperation("Учебный тоннель · уклон и поворот · без RGB");
        try
        {
            var project = await Task.Run(() => TunnelDemoFactory.Create(token), token);
            project = project with { Tunnel = await TunnelAnalyzer.AnalyzeAsync(project, project.Tunnel!.Axis, project.Tunnel.Options, OperationProgress(token), token) };
            var view = await ScanProjectView.PrepareAsync(project, token);
            token.ThrowIfCancellationRequested(); if (_disposed) return;
            Assign(new(null, project.ToSession()), 1, view); SetView(true, false); _sessionPath = null; MarkSessionDirty();
            NotifySessionProperties(); await ClearRecoveryAsync(); Home();
            Status = "Учебный тоннель без RGB · ось и сечения рассчитаны · данные синтетические"; Notification = ""; AddEvent(Status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось открыть учебный тоннель", ex); }
        finally { EndOperation(); }
    }
}
