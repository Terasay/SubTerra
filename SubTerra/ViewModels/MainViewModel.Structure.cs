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
    public bool UseNeuralClasses { get; set; }
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
    public RelayCommand FocusStructureSurfaceCommand { get; private set; } = null!;
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
        InitializeIndoorCommands();
        InitializeRoomCommands();
        InitializeObjectAnalysisCommands();
        AnalyzeStructureCommand = new(AnalyzeStructureAsync, () => CanAnalyzeStructure,
            ex => ReportError("Не удалось проанализировать структуру", ex));
        ClearStructureCommand = new(() => CommitStructureDocument(null, "удаление анализа"), () => CanManageProject && !IsScreenSelecting && HasStructureAnalysis);
        ConfirmStructureSurfaceCommand = new(() => ReviewStructureSurface(StructureReviewState.Confirmed), () => CanReviewStructure);
        RejectStructureSurfaceCommand = new(() => ReviewStructureSurface(StructureReviewState.Rejected), () => CanReviewStructure);
        ResetStructureSurfaceCommand = new(() => ReviewStructureSurface(StructureReviewState.Proposed), () => CanReviewStructure);
        FocusStructureSurfaceCommand = new(FocusStructureSurface, () => CanInteract && !StructureIsStale && SelectedStructureSurface is not null);
    }
    private void FocusStructureSurface()
    {
        if (!CanInteract || StructureIsStale || SelectedStructureSurface is not { } surface) return;
        ReturnToOverview();
        var previous = _importedBounds;
        _importedBounds = (surface.Boundary.Aggregate(new Vector3(float.MaxValue), Vector3.Min),
            surface.Boundary.Aggregate(new Vector3(float.MinValue), Vector3.Max));
        FitImported(); _importedBounds = previous; CameraLabel = surface.Title;
    }
    public async Task AnalyzeStructureAsync()
    {
        if (!CanAnalyzeStructure || _scanProject is not { } snapshot) return;
        EndInspection(); EndScreenSelection();
        var options = new StructureAnalysisOptions(DistanceTolerance: StructureTolerance, MaximumPlanes: UseNeuralClasses ? 24 : 12, UseNeuralClasses: UseNeuralClasses);
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
        document = document?.NormalizeVersion();
        var next = before with { Structure = document };
        _projectHistory.Commit(before, next, description);
        _scanProject = next; _dataset = new(null, next.ToSession(UserAnnotations.ToArray()));
        SetStructureDocument(document); MarkSessionDirty(); NotifyProjectState();
        Status = description; AddEvent(description);
    }
    private void SetStructureDocument(StructureAnalysisDocument? document)
    {
        InvalidateIndoorPreview(document);
        var selectedId = SelectedStructureSurface?.Id;
        var selectedObjectId = _selectedObjectCandidate?.Id;
        var selectedVertex = _roomVertexIndex;
        ClearStructureAnalysis(true);
        _structureDocument = document;
        if (document is null) return;
        _roomVertexIndex = selectedVertex;
        UseNeuralClasses = document.Result.Options.UseNeuralClasses; Raise(nameof(UseNeuralClasses));
        StructureTolerance = document.Result.Options.DistanceTolerance;
        foreach (var surface in document.Result.Surfaces) StructureSurfaces.Add(surface);
        RefreshObjectCandidates(selectedObjectId);
        RefreshRoomDocument();
        UpdateStructureDisplay();
        SelectedStructureSurface = StructureSurfaces.FirstOrDefault(s => s.Id == selectedId) ?? StructureSurfaces.FirstOrDefault();
        NotifyStructureState();
    }
    private void UpdateStructureDisplay()
    {
        UpdateRoomDisplay();
        UpdateObjectDisplay();
        if (_structureAnalysis is null || StructureIsStale) { StructureGeometry = null; StructureOutline = null; return; }
        if (IsIndoorSurfaceView) { StructureGeometry = null; UpdateStructureOutline(); return; }
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
    private void ClearStructureAnalysis(bool preservePreview = false)
    {
        if (!preservePreview) InvalidateIndoorPreview(null);
        _structureDocument = null;
        SelectedStructureSurface = null;
        StructureSurfaces.Clear(); StructureGeometry = null; StructureOutline = null;
        RefreshRoomDocument();
        UpdateRoomDisplay();
        RefreshObjectCandidates();
        NotifyStructureState();
    }
    private void NotifyStructureState()
    {
        NotifyRoomState();
        NotifyObjectState();
        NotifyIndoorState();
        Raise(nameof(CanAnalyzeStructure)); Raise(nameof(HasStructureAnalysis));
        Raise(nameof(StructureVisible)); Raise(nameof(StructureSummary));
        Raise(nameof(StructureCountsText)); Raise(nameof(StructureDataText));
        Raise(nameof(StructureIsStale)); Raise(nameof(CanReviewStructure));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}

public sealed record IndoorStyleChoice(string Label, Rendering.IndoorSurfaceStyle Style);

public sealed partial class MainViewModel
{
    private Rendering.IndoorSurfacePreview? _indoorPreview;
    private StructureAnalysisDocument? _indoorDocument;
    private readonly Dictionary<Rendering.IndoorSurfaceStyle, MeshGeometry3D> _indoorMeshes = [];
    private Simulation.TriangleIndex? _indoorCollisions, _indoorFloor;
    private Vector3 _indoorStart;
    public IReadOnlyList<IndoorStyleChoice> IndoorStyles { get; } =
        [new("Условная отделка", Rendering.IndoorSurfaceStyle.Materials), new("Цвета скана", Rendering.IndoorSurfaceStyle.SourceColors), new("Классы поверхностей", Rendering.IndoorSurfaceStyle.Classes)];
    private IndoorStyleChoice? _indoorStyle;
    public IndoorStyleChoice IndoorStyle
    {
        get => _indoorStyle ?? IndoorStyles[0];
        set { if (value is not null && IndoorStyles.Contains(value) && Set(ref _indoorStyle, value) && HasIndoorPreview) RefreshImportedGeometry(); }
    }
    public bool HasIndoorPreview => _indoorPreview is not null;
    public bool IsIndoorSurfaceView => IsImported && IsSurface && HasIndoorPreview;
    public bool CanPrepareIndoorPreview => IsImported && CanInteract && IsOverview && !HasEditPreview && !IsScreenSelecting && HasStructureAnalysis && !StructureIsStale;
    public string IndoorPreviewSummary => HasIndoorPreview ?
        $"Предварительная сетка: {_indoorPreview!.Scan.Triangles.Length / 3:N0} треугольников. Непросканированные участки остаются открытыми; отделка условная." :
        "Построение поверхностей по наблюдаемым точкам пола, стен и потолка. Исходные точки и RGB сохраняются.";
    public AsyncRelayCommand PrepareIndoorPreviewCommand { get; private set; } = null!;
    public AsyncRelayCommand AnalyzeIndoorCommand { get; private set; } = null!;
    private void InitializeIndoorCommands()
    {
        PrepareIndoorPreviewCommand = new(PrepareIndoorPreviewAsync, () => CanPrepareIndoorPreview, ex => ReportError("Не удалось построить поверхности", ex));
        AnalyzeIndoorCommand = new(async () =>
        {
            if (!CanAnalyzeStructure) return;
            UseNeuralClasses = true; Raise(nameof(UseNeuralClasses));
            var previous = _structureDocument;
            await AnalyzeStructureAsync();
            if (_structureDocument is not null && !ReferenceEquals(previous, _structureDocument)) await PrepareIndoorPreviewAsync();
        }, () => CanAnalyzeStructure, ex => ReportError("Не удалось подготовить осмотр", ex));
    }
    public async Task PrepareIndoorPreviewAsync()
    {
        if (!CanPrepareIndoorPreview || _scanProject is not { } project || _structureDocument is not { } document) return;
        var token = BeginOperation("Предварительные поверхности · исходные точки сохраняются");
        try
        {
            var prepared = await Task.Run(() =>
            {
                var preview = Rendering.IndoorSurfaceBuilder.Build(project, document, token);
                var original = new Rendering.ImportedGeometry(preview.Scan).Mesh(false);
                MeshGeometry3D WithColors(uint[] rgb)
                {
                    token.ThrowIfCancellationRequested();
                    var colors = new Color4Collection(preview.Scan.Triangles.Length);
                    foreach (var i in preview.Scan.Triangles) colors.Add(new((rgb[i] >> 16 & 255) / 255f, (rgb[i] >> 8 & 255) / 255f, (rgb[i] & 255) / 255f, 1));
                    return new() { Positions = original.Positions, Normals = original.Normals, Indices = original.Indices, Colors = colors };
                }
                var materials = WithColors(preview.MaterialColors); var classes = WithColors(preview.ClassColors);
                var positions = preview.Scan.Points.Select(p => p.Position).ToArray();
                var collisions = new Simulation.TriangleIndex(positions, preview.Scan.Triangles);
                var floor = preview.FloorTriangles.Length == 0 ? null : new Simulation.TriangleIndex(positions, preview.FloorTriangles);
                var start = Vector3.Zero; var distance = float.MaxValue;
                var support = document.Result.Surfaces.Where(s => s.Kind == StructureSurfaceKind.Floor && s.Review != StructureReviewState.Rejected).MaxBy(s => s.AreaSquareMeters);
                for (var i = 0; i < preview.FloorTriangles.Length; i += 3)
                {
                    var center = (positions[preview.FloorTriangles[i]] + positions[preview.FloorTriangles[i + 1]] + positions[preview.FloorTriangles[i + 2]]) / 3;
                    var d = Vector3.DistanceSquared(center, support!.Center);
                    if (d < distance) { distance = d; start = center + Vector3.UnitZ * 1.65f; }
                }
                return (preview, original, materials, classes, collisions, floor, start);
            }, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(project, _scanProject) || !ReferenceEquals(document, _structureDocument)) return;
            _indoorPreview = prepared.preview; _indoorDocument = document;
            _indoorMeshes[Rendering.IndoorSurfaceStyle.SourceColors] = prepared.original;
            _indoorMeshes[Rendering.IndoorSurfaceStyle.Materials] = prepared.materials;
            _indoorMeshes[Rendering.IndoorSurfaceStyle.Classes] = prepared.classes;
            _indoorCollisions = prepared.collisions; _indoorFloor = prepared.floor; _indoorStart = prepared.start;
            NotifyIndoorState(); SetView(true, false);
            Status = "Поверхности готовы · условная отделка · доступна прогулка по наблюдаемому полу"; AddEvent(Status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось построить поверхности", ex); }
        finally { EndOperation(); NotifyIndoorState(); }
    }
    private void InvalidateIndoorPreview(StructureAnalysisDocument? next)
    {
        if (!HasIndoorPreview || ReferenceEquals(next, _indoorDocument) && next?.IsStale == false) return;
        _indoorPreview = null; _indoorDocument = null; _indoorMeshes.Clear(); _indoorCollisions = null; _indoorFloor = null;
        Surface = null; CloudOnly = null;
        if (IsImported && !CanShowSurface) IsSurface = false;
        if (IsNavigating && IsImported) ReturnToOverview();
        NotifyIndoorState();
        if (IsImported && _importedGeometry is not null) RefreshImportedGeometry();
    }
    private void NotifyIndoorState()
    {
        foreach (var property in new[] { nameof(HasIndoorPreview), nameof(IsIndoorSurfaceView), nameof(CanPrepareIndoorPreview), nameof(IndoorPreviewSummary),
            nameof(CanShowSurface), nameof(SurfaceModeText), nameof(MeshPreviewText), nameof(CanWalk), nameof(CanFly), nameof(ExploreText) }) Raise(property);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
    public Navigation.ICameraNavigator CreateIndoorNavigator() => new Navigation.IndoorNavigator(_indoorStart, Vector3.UnitX,
        _indoorCollisions ?? throw new InvalidOperationException("Нет поверхностей для прогулки."), _indoorFloor!, (float)WalkSpeed);
}

public sealed partial class MainViewModel
{
    private StructureObject? _selectedObjectCandidate;
    private int _mergeObjectId;
    private string _objectCandidateLabel = "";
    private double _objectCellSize = .15;
    private int _objectMinimumPoints = 20;
    private bool _excludeObjectPlanes = true, _showObjectCandidates = true;
    private PointGeometry3D? _objectCandidateGeometry;
    private LineGeometry3D? _objectCandidateOutline;
    public ObservableCollection<StructureObject> ObjectCandidates { get; } = [];
    public StructureObject? SelectedObjectCandidate
    {
        get => _selectedObjectCandidate;
        set
        {
            if (value is not null && !ObjectCandidates.Contains(value)) return;
            if (!Set(ref _selectedObjectCandidate, value)) return;
            ObjectCandidateLabel = value?.Label ?? ""; UpdateObjectDisplay(); NotifyObjectState();
        }
    }
    public int MergeObjectId { get => _mergeObjectId; set => Set(ref _mergeObjectId, value); }
    public string ObjectCandidateLabel { get => _objectCandidateLabel; set => Set(ref _objectCandidateLabel, value); }
    public double ObjectCellSize { get => _objectCellSize; set => Set(ref _objectCellSize, value); }
    public int ObjectMinimumPoints { get => _objectMinimumPoints; set => Set(ref _objectMinimumPoints, value); }
    public bool ExcludeObjectPlanes { get => _excludeObjectPlanes; set => Set(ref _excludeObjectPlanes, value); }
    public bool ShowObjectCandidates { get => _showObjectCandidates; set { if (Set(ref _showObjectCandidates, value)) Raise(nameof(ObjectCandidatesVisible)); } }
    public bool HasObjectCandidates => _structureDocument?.Objects is not null;
    public bool ObjectCandidatesAreStale => HasObjectCandidates && (_structureDocument!.IsStale || _structureDocument.ObjectSourceSignature != RoomPlanner.Signature(_structureDocument));
    public bool ObjectCandidatesVisible => HasObjectCandidates && !ObjectCandidatesAreStale && ShowObjectCandidates && IsImported;
    public bool CanAnalyzeObjects => CanManageProject && !IsScreenSelecting && HasStructureAnalysis && !StructureIsStale;
    public bool CanReviewObject => CanAnalyzeObjects && !ObjectCandidatesAreStale && SelectedObjectCandidate is not null;
    public string ObjectCandidatesSummary => !HasObjectCandidates ? "Выделение отдельных групп точек. Сначала подтвердите опорные поверхности." :
        ObjectCandidatesAreStale ? "ПРЕДМЕТЫ УСТАРЕЛИ · изменились сканы или подтверждённые поверхности. Повторите поиск." :
        $"Групп: {ObjectCandidates.Count} · точек выборки: {ObjectCandidates.Sum(o => o.Samples.Length):N0}. Выборка до 30000 точек, максимум 64 группы. Классы нейросетью не определяются.";
    public PointGeometry3D? ObjectCandidateGeometry { get => _objectCandidateGeometry; private set => Set(ref _objectCandidateGeometry, value); }
    public LineGeometry3D? ObjectCandidateOutline { get => _objectCandidateOutline; private set => Set(ref _objectCandidateOutline, value); }
    public AsyncRelayCommand AnalyzeObjectsCommand { get; private set; } = null!;
    public RelayCommand ConfirmObjectCandidateCommand { get; private set; } = null!;
    public RelayCommand RejectObjectCandidateCommand { get; private set; } = null!;
    public RelayCommand SplitObjectCandidateCommand { get; private set; } = null!;
    public RelayCommand MergeObjectCandidatesCommand { get; private set; } = null!;
    public RelayCommand FocusObjectCandidateCommand { get; private set; } = null!;
    private void InitializeObjectAnalysisCommands()
    {
        AnalyzeObjectsCommand = new(AnalyzeObjectsAsync, () => CanAnalyzeObjects, ex => ReportError("Не удалось выделить предметы", ex));
        ConfirmObjectCandidateCommand = new(() => ReviewObjectCandidate(false), () => CanReviewObject);
        RejectObjectCandidateCommand = new(() => ReviewObjectCandidate(true), () => CanReviewObject);
        SplitObjectCandidateCommand = new(SplitObjectCandidate, () => CanReviewObject && SelectedObjectCandidate!.Samples.Length >= 6 && ObjectCandidates.Count < PointObjectAnalyzer.MaximumObjects);
        MergeObjectCandidatesCommand = new(MergeObjectCandidates, () => CanReviewObject && ObjectCandidates.Any(o => o.Id == MergeObjectId && o != SelectedObjectCandidate));
        FocusObjectCandidateCommand = new(() =>
        {
            if (SelectedObjectCandidate is not { } o) return;
            ReturnToOverview(); var previous = _importedBounds; _importedBounds = (o.Minimum, o.Maximum); FitImported(); _importedBounds = previous; CameraLabel = o.Label;
        }, () => CanInteract && SelectedObjectCandidate is not null && !ObjectCandidatesAreStale);
    }
    public async Task AnalyzeObjectsAsync()
    {
        if (!CanAnalyzeObjects || _scanProject is not { } project || _structureDocument is not { } document) return;
        var options = new ObjectGroupingOptions(ObjectCellSize, ObjectMinimumPoints, ExcludeObjectPlanes);
        EndInspection(); EndScreenSelection(); var token = BeginOperation("Выделение отдельных предметов");
        try
        {
            var groups = await Task.Run(() => PointObjectAnalyzer.Analyze(project, document, options, token), token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator || !ReferenceEquals(_scanProject, project) || !ReferenceEquals(_structureDocument, document)) return;
            CommitStructureDocument(document with { Objects = groups, ObjectOptions = options, ObjectSourceSignature = RoomPlanner.Signature(document) }, "выделение предметов");
            ShowObjectCandidates = true; Notification = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось выделить предметы", ex); }
        finally { EndOperation(); }
    }
    private void RefreshObjectCandidates(int? selectedId = null)
    {
        var id = selectedId ?? _selectedObjectCandidate?.Id;
        ObjectCandidates.Clear();
        foreach (var o in _structureDocument?.Objects ?? []) ObjectCandidates.Add(o);
        _selectedObjectCandidate = ObjectCandidates.FirstOrDefault(o => o.Id == id) ?? ObjectCandidates.FirstOrDefault();
        _objectCandidateLabel = _selectedObjectCandidate?.Label ?? "";
        if (_structureDocument?.ObjectOptions is { } options)
        { _objectCellSize = options.CellSize; _objectMinimumPoints = options.MinimumPoints; _excludeObjectPlanes = options.ExcludeConfirmedPlanes; }
        foreach (var name in new[] { nameof(SelectedObjectCandidate), nameof(ObjectCandidateLabel), nameof(ObjectCellSize), nameof(ObjectMinimumPoints), nameof(ExcludeObjectPlanes) }) Raise(name);
        UpdateObjectDisplay(); NotifyObjectState();
    }
    private void ReviewObjectCandidate(bool reject)
    {
        if (!CanReviewObject || SelectedObjectCandidate is not { } selected) return;
        var label = ObjectCandidateLabel.Trim();
        if (label.Length is < 1 or > 80) { ReportError("Название предмета", new InvalidOperationException("От 1 до 80 символов.")); return; }
        CommitObjectCandidates(ObjectCandidates.Select(o => o.Id == selected.Id ? o with { Label = label, Review = reject ? StructureReviewState.Rejected : StructureReviewState.Confirmed } : o).ToArray(), reject ? "отклонение предмета" : "проверка предмета оператором");
    }
    private void CommitObjectCandidates(StructureObject[] groups, string description)
    {
        if (!CanReviewObject || _structureDocument is not { } document || _scanProject is null) return;
        var next = (document with { Objects = groups }).NormalizeVersion();
        StructureProvenance.Validate(next, _scanProject); CommitStructureDocument(next, description);
    }
    private void SplitObjectCandidate()
    {
        if (!CanReviewObject || SelectedObjectCandidate is not { } selected || selected.Samples.Length < 6 || ObjectCandidates.Count >= 64) return;
        var size = selected.Maximum - selected.Minimum;
        var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        var sorted = selected.Samples.OrderBy(s => axis == 0 ? s.Position.X : axis == 1 ? s.Position.Y : s.Position.Z).ToArray();
        var middle = sorted.Length / 2; var id = ObjectCandidates.Max(o => o.Id) + 1;
        CommitObjectCandidates([.. ObjectCandidates.Where(o => o != selected), PointObjectAnalyzer.Create(selected.Id, selected.Label[..Math.Min(78, selected.Label.Length)] + " A", sorted[..middle]),
            PointObjectAnalyzer.Create(id, $"Предмет {id}", sorted[middle..])], "разделение группы предмета");
    }
    private void MergeObjectCandidates()
    {
        if (!CanReviewObject || SelectedObjectCandidate is not { } selected || ObjectCandidates.FirstOrDefault(o => o.Id == MergeObjectId && o != selected) is not { } other) return;
        CommitObjectCandidates([.. ObjectCandidates.Where(o => o != selected && o != other), PointObjectAnalyzer.Create(selected.Id, selected.Label, [.. selected.Samples, .. other.Samples])], "объединение групп предметов");
    }
    private void UpdateObjectDisplay()
    {
        if (!HasObjectCandidates || ObjectCandidatesAreStale) { ObjectCandidateGeometry = null; ObjectCandidateOutline = null; return; }
        var positions = new Vector3Collection(); var colors = new Color4Collection();
        foreach (var o in ObjectCandidates.Where(o => o.Review != StructureReviewState.Rejected))
            foreach (var sample in o.Samples)
                if (ActiveClipPlane is not { } clip || clip.Contains(sample.Position))
                { positions.Add(sample.Position); colors.Add(new((o.Color >> 16 & 255) / 255f, (o.Color >> 8 & 255) / 255f, (o.Color & 255) / 255f, 1)); }
        ObjectCandidateGeometry = new() { Positions = positions, Colors = colors };
        if (_selectedObjectCandidate is not { Review: not StructureReviewState.Rejected } selected) { ObjectCandidateOutline = null; return; }
        var a = selected.Minimum; var b = selected.Maximum;
        Vector3[] corners = [new(a.X,a.Y,a.Z), new(b.X,a.Y,a.Z), new(b.X,b.Y,a.Z), new(a.X,b.Y,a.Z), new(a.X,a.Y,b.Z), new(b.X,a.Y,b.Z), new(b.X,b.Y,b.Z), new(a.X,b.Y,b.Z)];
        int[] edges = [0,1,1,2,2,3,3,0,4,5,5,6,6,7,7,4,0,4,1,5,2,6,3,7]; var segments = new List<Vector3>();
        for (var i = 0; i < edges.Length; i += 2)
        {
            var x = corners[edges[i]]; var y = corners[edges[i + 1]];
            if (ActiveClipPlane is { } clip && !clip.TryClipSegment(ref x, ref y)) continue;
            segments.Add(x); segments.Add(y);
        }
        ObjectCandidateOutline = Rendering.SceneGeometry.Lines(segments);
    }
    private void NotifyObjectState()
    {
        foreach (var name in new[] { nameof(HasObjectCandidates), nameof(ObjectCandidatesAreStale), nameof(ObjectCandidatesVisible), nameof(CanAnalyzeObjects), nameof(CanReviewObject), nameof(ObjectCandidatesSummary) }) Raise(name);
    }
}
