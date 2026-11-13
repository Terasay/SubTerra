using System.Collections.ObjectModel;
using System.Numerics;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using SubTerra.Models;
using SubTerra.Services;

namespace SubTerra.ViewModels;

public sealed record SceneClassChoice(string Label, SceneClass Kind);

public sealed partial class MainViewModel
{
    private SceneUnderstandingDocument? _sceneDocument;
    private ScanProject? _sceneSnapshot;
    private SceneRegion? _selectedSceneRegion;
    private SceneClassChoice? _selectedSceneClass;
    private bool _showSceneUnderstanding = true;
    public ObservableCollection<SceneRegion> SceneRegions { get; } = [];
    public IReadOnlyList<SceneClassChoice> SceneClasses { get; } = Enum.GetValues<SceneClass>().Select(c => new SceneClassChoice(SceneRegion.Name(c), c)).ToArray();
    public AsyncRelayCommand AnalyzeSceneCommand { get; private set; } = null!;
    public RelayCommand ConfirmSceneRegionCommand { get; private set; } = null!;
    public RelayCommand ResetSceneRegionCommand { get; private set; } = null!;
    public RelayCommand LabelSceneBoxCommand { get; private set; } = null!;
    public RelayCommand FocusSceneRegionCommand { get; private set; } = null!;
    public bool HasSceneUnderstanding => _sceneDocument is not null;
    public bool CanAnalyzeScene => CanManageProject && IsRoomMode && !IsScreenSelecting && _scanProject?.PointCount >= 64;
    public bool CanReviewScene => CanAnalyzeScene && _sceneDocument is { IsStale: false };
    public bool SceneUnderstandingVisible => _sceneDocument is { IsStale: false } && IsImported && IsRoomMode && !HasEditPreview && ShowSceneUnderstanding;
    public bool ShowSceneUnderstanding { get => _showSceneUnderstanding; set { if (Set(ref _showSceneUnderstanding, value)) Raise(nameof(SceneUnderstandingVisible)); } }
    public string SceneUnderstandingSummary => _sceneDocument is null ?
        SceneUnderstandingService.IsInstalled ? "Локальная модель готова. Распознаёт стены, пол и предметы на цветном скане." :
        "Для первого запуска установите локальную модель по инструкции в scripts/Setup-SceneAi.ps1." :
        _sceneDocument.IsStale ? "Скан изменился. Повторите распознавание или отмените изменение." :
        $"{_sceneDocument.Model} · {_sceneDocument.Points.Length:N0} точек выборки · проверено областей {_sceneDocument.Regions.Count(r => r.ReviewedClass.HasValue)} из {_sceneDocument.Regions.Length}";
    public PointGeometry3D? SceneUnderstandingGeometry { get; private set; }
    public LineGeometry3D? SceneRegionOutline { get; private set; }
    public SceneClassChoice? SelectedSceneClass { get => _selectedSceneClass; set { if (value is not null && SceneClasses.Contains(value)) Set(ref _selectedSceneClass, value); } }
    public SceneRegion? SelectedSceneRegion
    {
        get => _selectedSceneRegion;
        set
        {
            if (value is not null && !SceneRegions.Contains(value) || !Set(ref _selectedSceneRegion, value)) return;
            SelectedSceneClass = SceneClasses.FirstOrDefault(c => c.Kind == value?.EffectiveClass) ?? SceneClasses[0];
            UpdateSceneRegionOutline();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private void InitializeSceneUnderstandingCommands()
    {
        SelectedSceneClass = SceneClasses[0];
        AnalyzeSceneCommand = new(AnalyzeSceneAsync, () => CanAnalyzeScene, ex => ReportError("Не удалось распознать сцену", ex));
        ConfirmSceneRegionCommand = new(() => ReviewSceneRegion(SelectedSceneClass?.Kind), () => CanReviewScene && SelectedSceneRegion is not null);
        ResetSceneRegionCommand = new(() => ReviewSceneRegion(null), () => CanReviewScene && SelectedSceneRegion?.ReviewedClass is not null);
        LabelSceneBoxCommand = new(LabelSceneBox, () => CanReviewScene && _measurementStart is not null && _measurementEnd is not null && SelectedSceneClass is not null);
        FocusSceneRegionCommand = new(() =>
        {
            var points = SceneRegionPositions().ToArray();
            if (points.Length == 0) return;
            var min = points.Aggregate(Vector3.Min); var max = points.Aggregate(Vector3.Max); var center = (min + max) / 2;
            ReturnToOverview();
            MoveCamera(center + Vector3.Normalize(new Vector3(1, -2, 1)) * Math.Max(2, (max - min).Length() * 1.5f), center, SelectedSceneRegion!.Title);
        }, () => CanInteract && _sceneDocument is { IsStale: false } && SelectedSceneRegion is not null);
    }

    public async Task AnalyzeSceneAsync()
    {
        if (!CanAnalyzeScene || _scanProject is not { } snapshot) return;
        EndInspection(); EndScreenSelection();
        var token = BeginOperation("ИИ · распознавание сцены");
        try
        {
            var progress = OperationProgress(token);
            var document = await Task.Run(() => SceneUnderstandingService.AnalyzeAsync(snapshot, progress, token), token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator || !ReferenceEquals(snapshot, _scanProject)) return;
            CommitSceneUnderstanding(document, "распознавание сцены");
            ShowSceneUnderstanding = true;
            ShowStructure = false;
            Status = "ИИ-разметка готова. Проверьте классы областей.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось распознать сцену", ex); }
        finally { EndOperation(); }
    }

    private void ReviewSceneRegion(SceneClass? kind)
    {
        if (!CanReviewScene || _sceneDocument is not { } document || SelectedSceneRegion is not { } region) return;
        CommitSceneUnderstanding(document with { Regions = document.Regions.Select(r => r.Id == region.Id ? r with { ReviewedClass = kind } : r).ToArray() }, "проверка области сцены");
    }

    private void LabelSceneBox()
    {
        if (!CanReviewScene || _sceneDocument is not { } document || _scanProject is not { } project ||
            _measurementStart is not { } a || _measurementEnd is not { } b || SelectedSceneClass is not { } kind) return;
        if (document.Regions.Length >= 512) { Notification = "Достигнут предел областей разметки."; return; }
        var box = new ScanBox(Vector3.Min(a.Position, b.Position), Vector3.Max(a.Position, b.Position));
        var objects = project.Objects.ToDictionary(o => o.Id);
        var id = document.Regions.Select(r => r.Id).DefaultIfEmpty().Max() + 1;
        var points = document.Points.Select(p => box.Contains(ScenePosition(objects, p)) ? p with { RegionId = id } : p).ToArray();
        var counts = points.GroupBy(p => p.RegionId).ToDictionary(g => g.Key, g => g.Count());
        if (!counts.ContainsKey(id)) { Notification = "В области нет точек выборки. Уточните границы."; return; }
        var regions = document.Regions.Where(r => counts.ContainsKey(r.Id)).Select(r => r with { PointCount = counts[r.Id] })
            .Append(new SceneRegion(id, SceneClass.Unknown, counts[id], kind.Kind)).ToArray();
        CommitSceneUnderstanding(document with { Points = points, Regions = regions }, "разметка области сцены");
        SelectedSceneRegion = SceneRegions.First(r => r.Id == id);
    }

    private void CommitSceneUnderstanding(SceneUnderstandingDocument document, string description)
    {
        if (!IsAdministrator || _disposed || _scanProject is not { } before) return;
        SceneUnderstandingService.Validate(before, document);
        var next = before with { SceneUnderstanding = document };
        _projectHistory.Commit(before, next, description);
        _scanProject = next; _dataset = new(null, next.ToSession(UserAnnotations.ToArray()));
        MarkSessionDirty(); NotifyProjectState();
        Status = description; AddEvent(description);
    }

    private void SyncSceneUnderstanding()
    {
        Raise(nameof(CanAnalyzeScene)); Raise(nameof(CanReviewScene)); Raise(nameof(SceneUnderstandingVisible));
        if (ReferenceEquals(_sceneSnapshot, _scanProject)) return;
        _sceneSnapshot = _scanProject;
        var selected = SelectedSceneRegion?.Id;
        _sceneDocument = _scanProject?.SceneUnderstanding is { } document ? SceneUnderstandingService.Refresh(_scanProject, document) : null;
        SceneRegions.Clear();
        if (_sceneDocument is not null) foreach (var region in _sceneDocument.Regions) SceneRegions.Add(region);
        SelectedSceneRegion = SceneRegions.FirstOrDefault(r => r.Id == selected) ?? SceneRegions.FirstOrDefault();
        UpdateSceneUnderstandingDisplay();
        Raise(nameof(HasSceneUnderstanding)); Raise(nameof(SceneUnderstandingSummary)); Raise(nameof(SceneUnderstandingVisible));
        Raise(nameof(CanAnalyzeScene)); Raise(nameof(CanReviewScene));
    }

    private static Vector3 ScenePosition(Dictionary<Guid, ScanObject> objects, ScenePoint point)
    {
        var obj = objects[point.Source.ObjectId];
        return Vector3.Transform(obj.Source.Points[point.Source.PointIndex].Position, obj.Matrix);
    }

    private IEnumerable<Vector3> SceneRegionPositions()
    {
        if (_sceneDocument is not { IsStale: false } document || _scanProject is not { } project || SelectedSceneRegion is not { } region) return [];
        var objects = project.Objects.ToDictionary(o => o.Id);
        return document.Points.Where(p => p.RegionId == region.Id).Select(p => ScenePosition(objects, p));
    }

    private void UpdateSceneRegionOutline()
    {
        var points = SceneRegionPositions().ToArray();
        SceneRegionOutline = points.Length > 0 ? Rendering.SceneGeometry.Lines(BoxLines(points.Aggregate(Vector3.Min), points.Aggregate(Vector3.Max))) : null;
        Raise(nameof(SceneRegionOutline));
    }

    private void UpdateSceneUnderstandingDisplay()
    {
        SceneUnderstandingGeometry = null;
        if (_sceneDocument is { IsStale: false } document && _scanProject is { } project)
        {
            var objects = project.Objects.ToDictionary(o => o.Id);
            var regions = document.Regions.ToDictionary(r => r.Id);
            var positions = new Vector3Collection(); var colors = new Color4Collection();
            foreach (var point in document.Points)
            {
                var position = ScenePosition(objects, point);
                if (ActiveClipPlane is { } clip && !clip.Contains(position)) continue;
                var kind = regions.GetValueOrDefault(point.RegionId)?.EffectiveClass ?? SceneClass.Unknown;
                var color = kind switch
                {
                    SceneClass.Unknown => new Color4(.55f, .58f, .62f, 1), SceneClass.Floor => new(.35f, .85f, .55f, 1),
                    SceneClass.Wall => new(.95f, .7f, .3f, 1), SceneClass.Ceiling => new(.45f, .6f, .95f, 1),
                    _ => new(.3f + (int)kind * 37 % 65 / 100f, .3f + (int)kind * 23 % 65 / 100f, .3f + (int)kind * 47 % 65 / 100f, 1)
                };
                positions.Add(position); colors.Add(color);
            }
            SceneUnderstandingGeometry = new() { Positions = positions, Colors = colors };
        }
        UpdateSceneRegionOutline();
        Raise(nameof(SceneUnderstandingGeometry)); Raise(nameof(SceneUnderstandingVisible));
    }
}
