using System.Collections.ObjectModel;
using System.Numerics;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using SubTerra.Models;
using SubTerra.Services;

namespace SubTerra.ViewModels;

public sealed record StructureClassChoice(string Label, StructureSurfaceKind Kind);

public sealed partial class MainViewModel
{
    private readonly IStructureAnalyzer _structureAnalyzer;
    private StructureAnalysisDocument? _structureDocument;
    private StructureAnalysisResult? _structureAnalysis => _structureDocument?.Result;
    private StructureSurface? _selectedStructureSurface;
    private PointGeometry3D? _structureGeometry;
    private LineGeometry3D? _structureOutline;
    private bool _showStructure = true;
    private double _structureTolerance = .025;
    public IReadOnlyList<StructureClassChoice> StructureClasses { get; } =
        [new("Не определено", StructureSurfaceKind.Unknown), new("Пол", StructureSurfaceKind.Floor),
         new("Стена", StructureSurfaceKind.Wall), new("Потолок", StructureSurfaceKind.Ceiling)];
    private StructureClassChoice? _selectedStructureClass;
    public StructureClassChoice? SelectedStructureClass
    {
        get => _selectedStructureClass;
        set { if (value is not null && StructureClasses.Contains(value)) Set(ref _selectedStructureClass, value); }
    }
    public ObservableCollection<StructureSurface> StructureSurfaces { get; } = [];
    public AsyncRelayCommand AnalyzeStructureCommand { get; private set; } = null!;
    public RelayCommand ClearStructureCommand { get; private set; } = null!;
    public RelayCommand ConfirmStructureSurfaceCommand { get; private set; } = null!;
    public RelayCommand RejectStructureSurfaceCommand { get; private set; } = null!;
    public RelayCommand ResetStructureSurfaceCommand { get; private set; } = null!;
    public bool CanAnalyzeStructure => CanManageProject && !IsScreenSelecting &&
        _scanProject?.Objects.Where(o => o.IsVisible).Sum(o => o.PointCount) >= 64;
    public bool HasStructureAnalysis => _structureAnalysis is not null;
    public bool StructureIsStale => _structureDocument?.IsStale == true;
    public bool CanReviewStructure => CanManageProject && !IsScreenSelecting && !StructureIsStale && SelectedStructureSurface is not null;
    public bool StructureVisible => HasStructureAnalysis && !StructureIsStale && ShowStructure && IsImported;
    public string StructureCountsText => _structureAnalysis is { } result ?
        $"Поверхностей: {result.Surfaces.Length} · подтверждено: {result.Surfaces.Count(s => s.Review == StructureReviewState.Confirmed)} · отклонено: {result.Surfaces.Count(s => s.Review == StructureReviewState.Rejected)}" :
        "Пол, стены и потолок одного помещения";
    public string StructureDataText => _structureAnalysis is { } result ?
        $"Выборка {result.SampleCount:N0} из {result.SourcePointCount:N0} точек · допуск {result.Options.DistanceTolerance:F3} м" :
        "Анализ видимых сканов. Вертикаль — Z. Классы проверяет оператор.";
    public bool ShowStructure { get => _showStructure; set { if (Set(ref _showStructure, value)) Raise(nameof(StructureVisible)); } }
    public double StructureTolerance
    {
        get => _structureTolerance;
        set { if (double.IsFinite(value) && value is >= .001 and <= .1) Set(ref _structureTolerance, value); }
    }
    public string StructureSummary => _structureAnalysis is not { } result ?
        "Видимые сканы одного помещения · вертикаль Z. Результаты — геометрические кандидаты." :
        (StructureIsStale ? "АНАЛИЗ УСТАРЕЛ · сканы изменились. Подсветка скрыта; повторите анализ или отмените изменение. " : "") +
        $"Поверхностей: {result.Surfaces.Length} · подтверждено: {result.Surfaces.Count(s => s.Review == StructureReviewState.Confirmed)} · " +
        $"отклонено: {result.Surfaces.Count(s => s.Review == StructureReviewState.Rejected)}. " +
        $"Выборка {result.SampleCount:N0} из {result.SourcePointCount:N0} · допуск {result.Options.DistanceTolerance:F3} м. " +
        (result.Surfaces.Length == 0 ? "Не найдено достаточно крупных плоских участков." :
            result.Surfaces.Any(s => s.Review == StructureReviewState.Proposed) ? "Кандидаты требуют проверки оператором." : "Проверка поверхностей завершена.");
    public StructureSurface? SelectedStructureSurface
    {
        get => _selectedStructureSurface;
        set
        {
            if (value is not null && !StructureSurfaces.Contains(value)) return;
            if (!Set(ref _selectedStructureSurface, value)) return;
            _selectedStructureClass = StructureClasses.FirstOrDefault(c => c.Kind == value?.Kind);
            Raise(nameof(SelectedStructureClass));
            UpdateStructureOutline();
            NotifyStructureState();
        }
    }
    public PointGeometry3D? StructureGeometry { get => _structureGeometry; private set => Set(ref _structureGeometry, value); }
    public LineGeometry3D? StructureOutline { get => _structureOutline; private set => Set(ref _structureOutline, value); }

    private void InitializeStructureCommands()
    {
        InitializeRoomCommands();
        AnalyzeStructureCommand = new(AnalyzeStructureAsync, () => CanAnalyzeStructure,
            ex => ReportError("Не удалось проанализировать структуру", ex));
        ClearStructureCommand = new(() => CommitStructureDocument(null, "удаление анализа"), () => CanManageProject && !IsScreenSelecting && HasStructureAnalysis);
        ConfirmStructureSurfaceCommand = new(() => ReviewStructureSurface(StructureReviewState.Confirmed), () => CanReviewStructure);
        RejectStructureSurfaceCommand = new(() => ReviewStructureSurface(StructureReviewState.Rejected), () => CanReviewStructure);
        ResetStructureSurfaceCommand = new(() => ReviewStructureSurface(StructureReviewState.Proposed), () => CanReviewStructure);
    }
    public async Task AnalyzeStructureAsync()
    {
        if (!CanAnalyzeStructure || _scanProject is not { } snapshot) return;
        EndInspection(); EndScreenSelection();
        var options = new StructureAnalysisOptions(DistanceTolerance: StructureTolerance);
        var token = BeginOperation("Поиск плоских поверхностей помещения");
        try
        {
            var result = await _structureAnalyzer.AnalyzeAsync(snapshot, options, OperationProgress(token, .95), token).WaitAsync(token);
            var document = await Task.Run(() => StructureProvenance.Create(snapshot, result, token), token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            if (!IsAdministrator || !ReferenceEquals(_scanProject, snapshot))
            { Status = "Результат анализа не применён: проект или доступ изменился"; return; }
            CommitStructureDocument(document, "анализ структуры");
            ShowStructure = true;
            Status = $"Анализ завершён · кандидатов: {result.Surfaces.Length}";
            AddEvent(Status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось проанализировать структуру", ex); }
        finally { EndOperation(); }
    }
    private void ReviewStructureSurface(StructureReviewState review)
    {
        if (!CanReviewStructure || SelectedStructureSurface is not { } surface || _structureDocument is not { } document) return;
        var kind = review == StructureReviewState.Proposed ? surface.SuggestedKind!.Value :
            review == StructureReviewState.Confirmed ? SelectedStructureClass?.Kind ?? surface.Kind : surface.Kind;
        var changed = surface with { Kind = kind, Review = review };
        if (changed == surface) return;
        var result = document.Result with { Surfaces = document.Result.Surfaces.Select(s => s.Id == surface.Id ? changed : s).ToArray() };
        CommitStructureDocument(document with { Result = result }, review switch
        {
            StructureReviewState.Confirmed => "подтверждение поверхности",
            StructureReviewState.Rejected => "отклонение поверхности", _ => "возврат исходного класса"
        });
    }
    private void CommitStructureDocument(StructureAnalysisDocument? document, string description)
    {
        if (!IsAdministrator || _disposed || _scanProject is not { } before) return;
        var next = before with { Structure = document };
        _projectHistory.Commit(before, next, description);
        _scanProject = next; _dataset = new(null, next.ToSession(UserAnnotations.ToArray()));
        SetStructureDocument(document); MarkSessionDirty(); NotifyProjectState();
        Status = description; AddEvent(description);
    }
    private void SetStructureDocument(StructureAnalysisDocument? document)
    {
        var selectedId = SelectedStructureSurface?.Id;
        ClearStructureAnalysis();
        _structureDocument = document;
        if (document is null) return;
        foreach (var surface in document.Result.Surfaces) StructureSurfaces.Add(surface);
        RefreshRoomDocument();
        UpdateStructureDisplay();
        SelectedStructureSurface = StructureSurfaces.FirstOrDefault(s => s.Id == selectedId) ?? StructureSurfaces.FirstOrDefault();
        NotifyStructureState();
    }
    private void UpdateStructureDisplay()
    {
        UpdateRoomDisplay();
        if (_structureAnalysis is null || StructureIsStale) { StructureGeometry = null; StructureOutline = null; return; }
        var positions = new Vector3Collection(); var colors = new Color4Collection();
        foreach (var surface in StructureSurfaces)
        {
            if (surface.Review == StructureReviewState.Rejected) continue;
            var color = surface.Kind switch
            {
                StructureSurfaceKind.Floor => new Color4(.38f, .88f, .55f, 1),
                StructureSurfaceKind.Ceiling => new Color4(.40f, .65f, .98f, 1),
                StructureSurfaceKind.Wall => new Color4(.96f, .72f, .30f, 1),
                _ => new Color4(.74f, .55f, .88f, 1)
            };
            foreach (var sample in surface.Samples)
                if (ActiveClipPlane is not { } plane || plane.Contains(sample.Position))
                { positions.Add(sample.Position); colors.Add(color); }
        }
        StructureGeometry = new() { Positions = positions, Colors = colors };
        UpdateStructureOutline();
    }
    private void UpdateStructureOutline()
    {
        var boundary = StructureIsStale || SelectedStructureSurface?.Review == StructureReviewState.Rejected ? [] : SelectedStructureSurface?.Boundary ?? [];
        var segments = new List<Vector3>();
        for (var i = 0; i < boundary.Length; i++)
        {
            var a = boundary[i]; var b = boundary[(i + 1) % boundary.Length];
            if (ActiveClipPlane is { } plane && !plane.TryClipSegment(ref a, ref b)) continue;
            segments.Add(a); segments.Add(b);
        }
        StructureOutline = segments.Count == 0 ? null : Rendering.SceneGeometry.Lines(segments);
    }
    private void ClearStructureAnalysis()
    {
        _structureDocument = null;
        SelectedStructureSurface = null;
        StructureSurfaces.Clear(); StructureGeometry = null; StructureOutline = null;
        RefreshRoomDocument();
        UpdateRoomDisplay();
        NotifyStructureState();
    }
    private void NotifyStructureState()
    {
        NotifyRoomState();
        Raise(nameof(CanAnalyzeStructure)); Raise(nameof(HasStructureAnalysis));
        Raise(nameof(StructureVisible)); Raise(nameof(StructureSummary));
        Raise(nameof(StructureCountsText)); Raise(nameof(StructureDataText));
        Raise(nameof(StructureIsStale)); Raise(nameof(CanReviewStructure));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
