using System.Collections.ObjectModel;
using System.Numerics;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;

namespace SubTerra.ViewModels;

public sealed record TunnelBranchChoice(Guid Id, string Name);
public sealed record TunnelClassChoice(TunnelPointClass Class, string Name);

public sealed partial class MainViewModel
{
    private Guid _activeTunnelBranchId;
    private bool _syncingTunnelBranches;
    private string _tunnelBranchName = "Основной проход";
    private TunnelReconstruction? _renderedTunnelMesh;
    private TunnelRecognition? _renderedTunnelRecognition;
    private MeshGeometry3D? _tunnelObservedMesh;
    private bool _showTunnelCompletion = true, _showTunnelObserved = true, _showTunnelRecognition = true;
    private double _tunnelMeshSpacing = .3, _tunnelCompletionGap = .5;
    private TunnelObjectCandidate? _selectedTunnelObject;
    private TunnelClassChoice? _selectedTunnelClass;
    private TunnelAnalysisDocument? ActiveTunnelDocument => _activeTunnelBranchId == Guid.Empty ? _scanProject?.Tunnel :
        _scanProject?.Tunnel?.Branches.FirstOrDefault(b => b.Id == _activeTunnelBranchId)?.Analysis;
    private TunnelAnalysisDocument ReplaceActiveTunnel(TunnelAnalysisDocument value) => _scanProject?.Tunnel is { } root ?
        TunnelWorkspace.ReplacePassage(root, _activeTunnelBranchId, value) : value;
    public ObservableCollection<TunnelBranchChoice> TunnelBranches { get; } = [];
    public TunnelBranchChoice? SelectedTunnelBranch
    {
        get => TunnelBranches.FirstOrDefault(b => b.Id == _activeTunnelBranchId);
        set
        {
            if (_syncingTunnelBranches || !CanInteract || HasEditPreview || value is null || !TunnelBranches.Contains(value) || value.Id == _activeTunnelBranchId) return;
            if (TunnelDraftPending) { Notification = "Примените или сбросьте черновик перед переключением ветви."; Raise(nameof(SelectedTunnelBranch)); return; }
            _activeTunnelBranchId = value.Id; SyncTunnelState(true); Notification = "";
        }
    }
    public string TunnelBranchName { get => _tunnelBranchName; set { if (CanEditTunnel && value is not null && value.Length <= 120) Set(ref _tunnelBranchName, value); } }
    public bool CanRemoveTunnelBranch => CanEditTunnel && IsTunnelMode && _activeTunnelBranchId != Guid.Empty;
    public double TunnelMeshSpacing { get => _tunnelMeshSpacing; set { if (CanEditTunnel && double.IsFinite(value)) Set(ref _tunnelMeshSpacing, value); } }
    public double TunnelCompletionGap { get => _tunnelCompletionGap; set { if (CanEditTunnel && double.IsFinite(value)) Set(ref _tunnelCompletionGap, value); } }
    public bool HasTunnelReconstruction => IsTunnelMode && !HasEditPreview && _scanProject?.Tunnel?.Reconstruction is { IsStale: false };
    public bool IsTunnelSurfaceView => HasTunnelReconstruction && IsSurface;
    public bool TunnelCompletionVisible => IsTunnelSurfaceView && ShowTunnelCompletion;
    public bool ShowTunnelCompletion { get => _showTunnelCompletion; set { if (Set(ref _showTunnelCompletion, value)) Raise(nameof(TunnelCompletionVisible)); } }
    public bool ShowTunnelObserved { get => _showTunnelObserved; set { if (Set(ref _showTunnelObserved, value)) RefreshGeometry(); } }
    public bool ShowTunnelRecognition { get => _showTunnelRecognition; set { if (Set(ref _showTunnelRecognition, value)) Raise(nameof(TunnelRecognitionVisible)); } }
    public bool HasTunnelRecognition => IsTunnelMode && !HasEditPreview && _scanProject?.Tunnel?.Recognition is { IsStale: false };
    public bool TunnelRecognitionVisible => HasTunnelRecognition && ShowTunnelRecognition;
    public bool CanExportTunnelMesh => CanSave && HasTunnelReconstruction;
    public bool CanExportTunnelLabels => CanSave && HasTunnelRecognition;
    public string TunnelMeshSummary => _scanProject?.Tunnel?.Reconstruction is { } mesh ?
        (mesh.IsStale ? "Сетка устарела. " : "") + $"По наблюдениям: {mesh.ObservedTriangles.Length / 3:N0} треугольников; достройка: {mesh.CompletedTriangles.Length / 3:N0}. Оранжевый — гипотеза." : "Сетка ещё не построена. Концы ветвей и крупные пропуски остаются открытыми.";
    public string TunnelRecognitionSummary => _scanProject?.Tunnel?.Recognition is { } r ?
        (r.IsStale ? "Распознавание устарело. " : "") + $"Нижняя поверхность: {r.Surfaces.Count(p => p.Class == TunnelPointClass.LowerSurface):N0}; борта: {r.Surfaces.Count(p => p.Class == TunnelPointClass.Side):N0}; свод: {r.Surfaces.Count(p => p.Class == TunnelPointClass.Roof):N0}. Групп: {r.Objects.Length}; проверено: {r.Objects.Count(o => o.ReviewedClass is not null)}." : "Сначала распознайте поверхности по XYZ, затем проверьте группы внутри прохода.";
    public string TunnelPeopleStatus => TunnelRecognition.PeopleStatus;
    public ObservableCollection<TunnelObjectCandidate> TunnelObjects { get; } = [];
    public IReadOnlyList<TunnelClassChoice> TunnelClasses { get; } = Enum.GetValues<TunnelPointClass>().Select(c => new TunnelClassChoice(c, TunnelObjectCandidate.ClassName(c))).ToArray();
    public TunnelClassChoice SelectedTunnelClass { get => _selectedTunnelClass ?? TunnelClasses[0]; set { if (CanEditTunnel && value is not null && TunnelClasses.Contains(value)) Set(ref _selectedTunnelClass, value); } }
    public TunnelObjectCandidate? SelectedTunnelObject
    {
        get => _selectedTunnelObject;
        set
        {
            if (value is not null && !TunnelObjects.Contains(value) || !Set(ref _selectedTunnelObject, value)) return;
            _selectedTunnelClass = TunnelClasses.First(c => c.Class == (value?.ReviewedClass ?? TunnelPointClass.Unknown));
            Raise(nameof(SelectedTunnelClass)); Raise(nameof(TunnelObjectReview)); UpdateTunnelRecognitionGeometry();
        }
    }
    public string TunnelObjectReview => SelectedTunnelObject?.ReviewText ?? "Выберите группу для проверки или задайте область двумя точками измерения.";
    public MeshGeometry3D? TunnelCompletedGeometry { get; private set; }
    public HelixToolkit.Wpf.SharpDX.PhongMaterial TunnelCompletionMaterial => CaveMaterials.Height;
    public PointGeometry3D? TunnelRecognitionGeometry { get; private set; }
    public LineGeometry3D? TunnelObjectOutline { get; private set; }
    public LineGeometry3D? TunnelOtherBranchesGeometry { get; private set; }
    public LineGeometry3D? TunnelDraftGeometry { get; private set; }
    public AsyncRelayCommand SuggestTunnelRouteCommand { get; private set; } = null!;
    public AsyncRelayCommand BuildTunnelMeshCommand { get; private set; } = null!;
    public AsyncRelayCommand RecognizeTunnelCommand { get; private set; } = null!;
    public RelayCommand AddTunnelBranchCommand { get; private set; } = null!;
    public RelayCommand RemoveTunnelBranchCommand { get; private set; } = null!;
    public RelayCommand RenameTunnelBranchCommand { get; private set; } = null!;
    public RelayCommand ReviewTunnelObjectCommand { get; private set; } = null!;
    public RelayCommand ResetTunnelObjectReviewCommand { get; private set; } = null!;
    public AsyncRelayCommand AddTunnelLabelBoxCommand { get; private set; } = null!;
    public RelayCommand FocusTunnelObjectCommand { get; private set; } = null!;

    private void InitializeTunnelWorkspaceCommands()
    {
        SuggestTunnelRouteCommand = new(SuggestTunnelRouteAsync, () => CanAnalyzeTunnel, ex => ReportError("Маршрут не найден", ex));
        BuildTunnelMeshCommand = new(BuildTunnelMeshAsync, () => CanAnalyzeTunnel, ex => ReportError("Сетка не построена", ex));
        RecognizeTunnelCommand = new(RecognizeTunnelAsync, () => CanAnalyzeTunnel, ex => ReportError("3D-анализ не выполнен", ex));
        AddTunnelBranchCommand = new(AddTunnelBranch, () => CanEditTunnel && IsTunnelMode && (_scanProject?.Tunnel?.Branches.Length ?? 0) < TunnelWorkspace.MaximumBranches - 1);
        RemoveTunnelBranchCommand = new(RemoveTunnelBranch, () => CanRemoveTunnelBranch);
        RenameTunnelBranchCommand = new(() =>
        {
            if (!CanRemoveTunnelBranch || string.IsNullOrWhiteSpace(TunnelBranchName)) return;
            var root = _scanProject!.Tunnel!;
            CommitTunnelProject(_scanProject with { Tunnel = root with { Branches = root.Branches.Select(b => b.Id == _activeTunnelBranchId ? b with { Name = TunnelBranchName.Trim() } : b).ToArray() } }, "название ветви");
        }, () => CanRemoveTunnelBranch);
        ReviewTunnelObjectCommand = new(() => ReviewTunnelObject(SelectedTunnelClass.Class), () => CanEditTunnel && HasTunnelRecognition && SelectedTunnelObject is not null);
        ResetTunnelObjectReviewCommand = new(() => ReviewTunnelObject(null), () => CanEditTunnel && HasTunnelRecognition && SelectedTunnelObject?.ReviewedClass is not null);
        AddTunnelLabelBoxCommand = new(AddTunnelLabelBoxAsync, () => CanEditTunnel && HasTunnelRecognition && _measurementStart is not null && _measurementEnd is not null,
            ex => ReportError("Не удалось выделить область", ex));
        FocusTunnelObjectCommand = new(() =>
        {
            if (SelectedTunnelObject is not { } obj || !CanInteract || !HasTunnelRecognition) return;
            ReturnToOverview(); var center = (obj.Minimum.Position + obj.Maximum.Position) / 2;
            MoveCamera(center + Vector3.Normalize(new Vector3(1, -2, 1)) * Math.Max(2, (obj.Maximum.Position - obj.Minimum.Position).Length() * 2), center, obj.Name);
        }, () => CanInteract && HasTunnelRecognition && SelectedTunnelObject is not null);
    }
    private void AddTunnelBranch()
    {
        if (!CanEditTunnel || !IsTunnelMode || TunnelDraftPending) { Notification = "Примените или сбросьте черновик перед добавлением ветви."; return; }
        var root = _scanProject!.Tunnel ?? TunnelAnalyzer.Create([]); if (root.Branches.Length >= TunnelWorkspace.MaximumBranches - 1) return;
        var id = Guid.NewGuid();
        var next = root with { Version = 2, Branches = [.. root.Branches, new(id, $"Ветвь {root.Branches.Length + 1}", TunnelAnalyzer.Create([]))], Reconstruction = null, Recognition = null };
        _activeTunnelBranchId = id; CommitTunnelProject(_scanProject with { Tunnel = next }, "добавление ветви тоннеля");
    }
    private void RemoveTunnelBranch()
    {
        if (!CanRemoveTunnelBranch || _scanProject?.Tunnel is not { } root) return;
        var id = _activeTunnelBranchId; _activeTunnelBranchId = Guid.Empty;
        CommitTunnelProject(_scanProject with { Tunnel = root with { Branches = root.Branches.Where(b => b.Id != id).ToArray(), Reconstruction = null, Recognition = null } }, "удаление ветви тоннеля");
    }
    public async Task SuggestTunnelRouteAsync()
    {
        if (!CanAnalyzeTunnel || _scanProject is not { } snapshot) return;
        InspectionPoint[] anchors;
        try { anchors = ParseTunnelAxis(); } catch (Exception ex) { ReportError("Проверьте опорные точки", ex); return; }
        var token = BeginOperation("Предложение маршрута внутри наблюдаемого прохода");
        var accessHandler = WatchTunnelOperationAccess(token);
        try
        {
            var axis = await TunnelRoutePlanner.SuggestAsync(snapshot, anchors, token);
            token.ThrowIfCancellationRequested(); if (_disposed || !IsAdministrator || !ReferenceEquals(_scanProject, snapshot)) return;
            _tunnelAxisText = FormatAxis(axis); Raise(nameof(TunnelAxisText)); Raise(nameof(TunnelDraftPending)); UpdateTunnelDraftGeometry();
            Status = "Маршрут предложен в черновике · проверьте в 3D, исправьте точки и примените ось"; Notification = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось предложить маршрут", ex); }
        finally { _access.AccessChanged -= accessHandler; EndOperation(); NotifyTunnelState(); }
    }
    private TunnelAnalysisDocument RootWithDraft()
    {
        var options = TunnelOptions; options.Validate(); var value = TunnelAnalyzer.Create(ParseTunnelAxis(), options);
        return ReplaceActiveTunnel(value);
    }
    public Task BuildTunnelMeshAsync() => RunTunnelDerivativeAsync(true);
    public Task RecognizeTunnelAsync() => RunTunnelDerivativeAsync(false);
    private async Task RunTunnelDerivativeAsync(bool mesh)
    {
        if (!CanAnalyzeTunnel || _scanProject is not { } snapshot) return;
        TunnelAnalysisDocument root; TunnelMeshOptions options;
        try
        {
            root = TunnelDraftPending || snapshot.Tunnel is null ? RootWithDraft() : snapshot.Tunnel;
            options = new(TunnelMeshSpacing, Math.Max(.85, TunnelMeshSpacing * 1.5), TunnelCompletionGap); options.Validate();
        }
        catch (Exception ex) { ReportError("Проверьте ось и параметры", ex); return; }
        EndInspection(); var token = BeginOperation(mesh ? "Криволинейная сетка всех ветвей" : "3D-анализ · нижняя поверхность, борта, свод и группы");
        var accessHandler = WatchTunnelOperationAccess(token);
        try
        {
            var result = mesh ? await TunnelMeshBuilder.BuildAsync(snapshot, root, options, token) : await TunnelRecognitionService.AnalyzeAsync(snapshot, root, token);
            token.ThrowIfCancellationRequested(); if (_disposed || !IsAdministrator || !ReferenceEquals(_scanProject, snapshot)) return;
            CommitTunnelProject(snapshot with { Tunnel = result }, mesh ? "построение сетки тоннеля" : "распознавание геометрии тоннеля");
            if (mesh) SetView(true, false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError(mesh ? "Не удалось построить сетку" : "Не удалось распознать геометрию", ex); }
        finally { _access.AccessChanged -= accessHandler; EndOperation(); NotifyTunnelState(); }
    }
    private void ReviewTunnelObject(TunnelPointClass? value)
    {
        if (!CanEditTunnel || !HasTunnelRecognition || SelectedTunnelObject is not { } selected || _scanProject?.Tunnel is not { Recognition: { } r } root) return;
        CommitTunnelProject(_scanProject with { Tunnel = root with { Recognition = r with { Objects = r.Objects.Select(o => o.Id == selected.Id ? o with { ReviewedClass = value } : o).ToArray() } } }, "проверка класса 3D-группы");
    }
    private async Task AddTunnelLabelBoxAsync()
    {
        if (!CanEditTunnel || !HasTunnelRecognition || _measurementStart is not { } a || _measurementEnd is not { } b || _scanProject?.Tunnel is not { Recognition: { } r } root) return;
        var snapshot = _scanProject; var token = BeginOperation("Выделение области для XYZ-разметки");
        var accessHandler = WatchTunnelOperationAccess(token);
        try
        {
            var result = await Task.Run(() => TunnelRecognitionService.AddBox(snapshot, r, new(Vector3.Min(a.Position, b.Position), Vector3.Max(a.Position, b.Position)), token), token);
            token.ThrowIfCancellationRequested(); if (_disposed || !IsAdministrator || !ReferenceEquals(snapshot, _scanProject)) return;
            CommitTunnelProject(snapshot with { Tunnel = root with { Recognition = result } }, "область для ручной XYZ-разметки");
            SelectedTunnelObject = TunnelObjects.LastOrDefault();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось выделить область", ex); }
        finally { _access.AccessChanged -= accessHandler; EndOperation(); }
    }
    public async Task ExportTunnelMeshAsync(string path, bool completionOnly = false)
    {
        if (!CanExportTunnelMesh || _scanProject?.Tunnel?.Reconstruction is not { } mesh) return;
        var token = BeginOperation("Экспорт производной сетки тоннеля");
        var accessHandler = WatchTunnelOperationAccess(token);
        try
        {
            var model = await Task.Run(() => TunnelMeshBuilder.Export(mesh, completionOnly), token);
            token.ThrowIfCancellationRequested(); if (_disposed || !IsAdministrator) return;
            await _storage.ExportImportedPlyAsync(path, model, token, OperationProgress(token)); Status = "Сетка тоннеля экспортирована в отдельный PLY";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось экспортировать сетку", ex); }
        finally { _access.AccessChanged -= accessHandler; EndOperation(); }
    }
    public async Task ExportTunnelLabelsAsync(string path)
    {
        if (!CanExportTunnelLabels || _scanProject is not { Tunnel.Recognition: { } recognition } snapshot) return;
        var token = BeginOperation("Экспорт XYZ и проверенных классов");
        var accessHandler = WatchTunnelOperationAccess(token);
        try
        {
            await Task.Run(() => TunnelRecognitionService.ExportDatasetAsync(path, snapshot, recognition, token), token);
            Status = "XYZ-разметка экспортирована · автоматические гипотезы отделены от проверенных классов";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось экспортировать разметку", ex); }
        finally { _access.AccessChanged -= accessHandler; EndOperation(); }
    }
    private EventHandler WatchTunnelOperationAccess(CancellationToken token)
    {
        EventHandler handler = (_, _) => { if (!IsAdministrator && _operationCancellation?.Token == token) CancelOperation(); };
        _access.AccessChanged += handler; handler(this, EventArgs.Empty); return handler;
    }
    private void SyncTunnelWorkspace()
    {
        var root = _scanProject?.Tunnel;
        if (_activeTunnelBranchId != Guid.Empty && root?.Branches.Any(b => b.Id == _activeTunnelBranchId) != true) _activeTunnelBranchId = Guid.Empty;
        _syncingTunnelBranches = true;
        TunnelBranches.Clear(); TunnelBranches.Add(new(Guid.Empty, "Основной проход"));
        if (root is not null) foreach (var branch in root.Branches) TunnelBranches.Add(new(branch.Id, branch.Name));
        _syncingTunnelBranches = false; _tunnelBranchName = SelectedTunnelBranch?.Name ?? "";
        Raise(nameof(SelectedTunnelBranch)); Raise(nameof(TunnelBranchName));
        var mesh = root?.Reconstruction;
        if (!ReferenceEquals(mesh?.Vertices, _renderedTunnelMesh?.Vertices))
        {
            _tunnelObservedMesh = mesh is null ? null : TunnelMeshGeometry(mesh, mesh.ObservedTriangles, new(.66f, .73f, .78f, 1));
            TunnelCompletedGeometry = mesh is null ? null : TunnelMeshGeometry(mesh, mesh.CompletedTriangles, new(.96f, .65f, .26f, 1));
            _tunnelMeshSpacing = mesh?.Options.Spacing ?? .3; _tunnelCompletionGap = mesh?.Options.CompletionGap ?? .5;
            Raise(nameof(TunnelMeshSpacing)); Raise(nameof(TunnelCompletionGap)); Raise(nameof(TunnelCompletedGeometry));
        }
        _renderedTunnelMesh = mesh;
        var recognition = root?.Recognition;
        if (!ReferenceEquals(recognition?.Objects, _renderedTunnelRecognition?.Objects) || !ReferenceEquals(recognition?.Surfaces, _renderedTunnelRecognition?.Surfaces) || recognition?.IsStale != _renderedTunnelRecognition?.IsStale)
        {
            var selected = SelectedTunnelObject?.Id; TunnelObjects.Clear();
            if (recognition is not null) foreach (var obj in recognition.Objects) TunnelObjects.Add(obj);
            _selectedTunnelObject = TunnelObjects.FirstOrDefault(o => o.Id == selected) ?? TunnelObjects.FirstOrDefault();
            _selectedTunnelClass = TunnelClasses.First(c => c.Class == (_selectedTunnelObject?.ReviewedClass ?? TunnelPointClass.Unknown));
            Raise(nameof(SelectedTunnelClass));
            Raise(nameof(SelectedTunnelObject)); Raise(nameof(TunnelObjectReview));
            UpdateTunnelRecognitionGeometry();
        }
        _renderedTunnelRecognition = recognition;
    }
    private static MeshGeometry3D TunnelMeshGeometry(TunnelReconstruction mesh, int[] indices, Color4 color)
    {
        var normals = new Vector3[mesh.Vertices.Length]; var positions = mesh.Vertices.Select(p => p.Position).ToArray();
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2]; var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += normal; normals[b] += normal; normals[c] += normal;
        }
        return new() { Positions = new(positions), Indices = new(indices), Normals = new(normals.Select(n => n.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(n))), Colors = new(Enumerable.Repeat(color, positions.Length)) };
    }
    private void UpdateTunnelDraftGeometry()
    {
        var lines = new List<Vector3>();
        if (IsTunnelMode && TunnelDraftPending && !HasEditPreview)
            try { var axis = ParseTunnelAxis(); for (var i = 1; i < axis.Length; i++) lines.AddRange([axis[i - 1].Position, axis[i].Position]); } catch (Exception ex) when (ex is System.IO.InvalidDataException or ArgumentException) { }
        TunnelDraftGeometry = SceneGeometry.Lines(lines); Raise(nameof(TunnelDraftGeometry));
    }
    private void UpdateTunnelBranchGeometry()
    {
        var lines = new List<Vector3>();
        if (IsTunnelMode && !HasEditPreview && _scanProject?.Tunnel is { } root)
            foreach (var (id, axis) in new[] { (Guid.Empty, root.Axis) }.Concat(root.Branches.Select(b => (b.Id, b.Analysis.Axis))))
                if (id != _activeTunnelBranchId) for (var i = 1; i < axis.Length; i++) lines.AddRange([axis[i - 1].Position, axis[i].Position]);
        TunnelOtherBranchesGeometry = SceneGeometry.Lines(lines); Raise(nameof(TunnelOtherBranchesGeometry));
        UpdateTunnelDraftGeometry(); NotifyTunnelWorkspace();
    }
    private void UpdateTunnelRecognitionGeometry()
    {
        var positions = new Vector3Collection(); var colors = new Color4Collection(); var lines = new List<Vector3>();
        if (_scanProject is { Tunnel.Recognition: { IsStale: false } r } project)
        {
            var objects = project.Objects.ToDictionary(o => o.Id);
            void Add(TunnelPointLabel point, TunnelPointClass label)
            {
                if (!objects.TryGetValue(point.ObjectId, out var obj) || (uint)point.PointIndex >= obj.Source.Points.Length) return;
                positions.Add(Vector3.Transform(obj.Source.Points[point.PointIndex].Position, obj.Matrix));
                colors.Add(label switch { TunnelPointClass.LowerSurface => new(.35f, .8f, .55f, 1), TunnelPointClass.Side => new(.35f, .68f, .95f, 1), TunnelPointClass.Roof => new(.78f, .55f, .96f, 1),
                    TunnelPointClass.Support => new(.95f, .75f, .3f, 1), TunnelPointClass.Equipment => new(.95f, .45f, .25f, 1), TunnelPointClass.Person => new(.95f, .3f, .6f, 1), _ => new(.65f, .65f, .65f, 1) });
            }
            foreach (var point in r.Surfaces) Add(point, point.Class);
            foreach (var obj in r.Objects) foreach (var point in obj.Points) Add(point, obj.ReviewedClass ?? TunnelPointClass.Unknown);
            if (SelectedTunnelObject is { } selected)
            {
                var a = selected.Minimum.Position; var b = selected.Maximum.Position;
                Vector3[] corners = [a, new(b.X, a.Y, a.Z), new(b.X, b.Y, a.Z), new(a.X, b.Y, a.Z), new(a.X, a.Y, b.Z), new(b.X, a.Y, b.Z), b, new(a.X, b.Y, b.Z)];
                foreach (var (i, j) in new[] { (0,1),(1,2),(2,3),(3,0),(4,5),(5,6),(6,7),(7,4),(0,4),(1,5),(2,6),(3,7) }) lines.AddRange([corners[i], corners[j]]);
            }
        }
        TunnelRecognitionGeometry = new() { Positions = positions, Colors = colors }; TunnelObjectOutline = SceneGeometry.Lines(lines);
        Raise(nameof(TunnelRecognitionGeometry)); Raise(nameof(TunnelObjectOutline));
    }
    private void NotifyTunnelWorkspace()
    {
        foreach (var name in new[] { nameof(CanRemoveTunnelBranch), nameof(HasTunnelReconstruction), nameof(IsTunnelSurfaceView), nameof(TunnelCompletionVisible), nameof(HasTunnelRecognition),
            nameof(TunnelRecognitionVisible), nameof(TunnelMeshSummary), nameof(TunnelRecognitionSummary), nameof(CanExportTunnelMesh), nameof(CanExportTunnelLabels), nameof(CanShowSurface), nameof(CanSelectSurface), nameof(CanOpenSurface), nameof(SurfaceModeHint) }) Raise(name);
    }
}
