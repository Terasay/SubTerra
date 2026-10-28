using System.Collections.ObjectModel;
using System.Numerics;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;

namespace SubTerra.ViewModels;

public sealed record ScanObjectItem(Guid Id, string Name, bool IsVisible, int PointCount, int OriginalPointCount)
{
    public string VisibilityText => IsVisible ? "●" : "○";
    public string Details => $"{PointCount:N0} / {OriginalPointCount:N0} точек";
}
public sealed record ScanObjectColor(string Name, uint? Value);
public enum ScanSelectionTool { None, Rectangle, Brush }

public sealed partial class MainViewModel
{
    private ScanProject? _scanProject;
    private ScanProjectView? _projectView, _previewView;
    private ScanProject? _previewProject;
    private string _previewDescription = "";
    private readonly ScanProjectHistory _projectHistory = new();
    private ScanObjectItem? _selectedScanObject;
    private string _objectName = "";
    private double _objectX, _objectY, _objectZ, _objectRotateX, _objectRotateY, _objectRotateZ;
    private ScanObjectColor _objectColor = DefaultObjectColors[0];
    private ScanSelectionTool _selectionTool;
    private bool _keepScreenSelection;
    private double _brushRadius = 24;
    private static readonly ScanObjectColor[] DefaultObjectColors = [new("Исходные цвета", null), new("Голубой", 0x71caff), new("Зелёный", 0x9fd478), new("Оранжевый", 0xf1b36b), new("Сиреневый", 0xc2a1f4)];
    public ObservableCollection<ScanObjectItem> ScanObjects { get; } = [];
    public ScanObjectItem? SelectedScanObject
    {
        get => _selectedScanObject;
        set
        {
            if (value is null || _scanProject is null || !CanInteract || HasEditPreview || !_scanProject.Objects.Any(o => o.Id == value.Id)) return;
            _scanProject = _scanProject with { ActiveObjectId = value.Id };
            _dataset = new(null, _scanProject.ToSession(UserAnnotations.ToArray()));
            _selectedScanObject = value; Raise(); LoadObjectDraft();
        }
    }
    private ScanObject? ActiveScanObject => _scanProject?.Active;
    public bool HasEditPreview => _previewProject is not null;
    public bool CanSelectScanObject => CanInteract && !HasEditPreview && !IsScreenSelecting;
    public bool CanManageProject => IsImported && IsAdministrator && CanInteract && IsOverview && !HasRecovery && !HasEditPreview;
    public bool CanEditObject => CanManageProject && ActiveScanObject is not null;
    public bool CanVoxelClean => CanEditObject && ActiveScanObject?.Source.HasMesh == false;
    public bool CanUndoProject => CanManageProject && _projectHistory.UndoChange is not null;
    public bool CanRedoProject => CanManageProject && _projectHistory.RedoChange is not null;
    public string UndoProjectText => _projectHistory.UndoChange is { } c ? "Отменить: " + c.Description : "Отменить действие";
    public string RedoProjectText => _projectHistory.RedoChange is { } c ? "Повторить: " + c.Description : "Повторить действие";
    public string ProjectSummary => _scanProject is null ? "" : $"Объектов: {_scanProject.Objects.Length} · видимых: {_scanProject.Objects.Count(o => o.IsVisible)}";
    public string EditPreviewText => _previewDescription;
    public string ObjectName { get => _objectName; set => Set(ref _objectName, value); }
    public double ObjectX { get => _objectX; set => Set(ref _objectX, value); }
    public double ObjectY { get => _objectY; set => Set(ref _objectY, value); }
    public double ObjectZ { get => _objectZ; set => Set(ref _objectZ, value); }
    public double ObjectRotateX { get => _objectRotateX; set => Set(ref _objectRotateX, value); }
    public double ObjectRotateY { get => _objectRotateY; set => Set(ref _objectRotateY, value); }
    public double ObjectRotateZ { get => _objectRotateZ; set => Set(ref _objectRotateZ, value); }
    public IReadOnlyList<ScanObjectColor> ObjectColors => DefaultObjectColors.Contains(_objectColor) ? DefaultObjectColors : [.. DefaultObjectColors, _objectColor];
    public ScanObjectColor ObjectColor { get => _objectColor; set { if (value is not null) Set(ref _objectColor, value); } }
    public ScanSelectionTool SelectionTool => _selectionTool;
    public bool IsScreenSelecting => _selectionTool != ScanSelectionTool.None && CanEditObject;
    public bool KeepScreenSelection { get => _keepScreenSelection; set => Set(ref _keepScreenSelection, value); }
    public double BrushRadius { get => _brushRadius; set { if (double.IsFinite(value)) Set(ref _brushRadius, Math.Clamp(value, 8, 100)); } }
    public string SelectionHint => _selectionTool == ScanSelectionTool.Brush ? "ЛКМ — кисть · сквозное выделение активного объекта · Esc — отмена" :
        "Протяните рамку ЛКМ · сквозное выделение активного объекта · Esc — отмена";
    public RelayCommand<ScanObjectItem> ToggleScanObjectCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplyObjectCommand { get; private set; } = null!;
    public AsyncRelayCommand RemoveObjectCommand { get; private set; } = null!;
    public RelayCommand FocusObjectCommand { get; private set; } = null!;
    public AsyncRelayCommand UndoProjectCommand { get; private set; } = null!;
    public AsyncRelayCommand RedoProjectCommand { get; private set; } = null!;
    public RelayCommand ApplyCleanPreviewCommand { get; private set; } = null!;
    public RelayCommand CancelCleanPreviewCommand { get; private set; } = null!;
    public RelayCommand RectangleSelectionCommand { get; private set; } = null!;
    public RelayCommand BrushSelectionCommand { get; private set; } = null!;
    public RelayCommand EndScreenSelectionCommand { get; private set; } = null!;

    private void InitializeProjectCommands()
    {
        ToggleScanObjectCommand = new(o => _ = ToggleObjectAsync(o.Id), _ => CanManageProject);
        ApplyObjectCommand = new(ApplyObjectAsync, () => CanEditObject, ex => ReportError("Не удалось изменить объект", ex));
        RemoveObjectCommand = new(RemoveObjectAsync, () => CanEditObject, ex => ReportError("Не удалось удалить объект", ex));
        FocusObjectCommand = new(FocusObject, () => IsImported && CanInteract && ActiveScanObject is not null);
        UndoProjectCommand = new(() => ReplayProjectAsync(false), () => CanUndoProject, ex => ReportError("Не удалось отменить действие", ex));
        RedoProjectCommand = new(() => ReplayProjectAsync(true), () => CanRedoProject, ex => ReportError("Не удалось повторить действие", ex));
        ApplyCleanPreviewCommand = new(ApplyCleanPreview, () => HasEditPreview && IsAdministrator && CanInteract && !HasRecovery);
        CancelCleanPreviewCommand = new(CancelCleanPreview, () => HasEditPreview && CanInteract);
        RectangleSelectionCommand = new(() => BeginScreenSelection(ScanSelectionTool.Rectangle), () => CanEditObject);
        BrushSelectionCommand = new(() => BeginScreenSelection(ScanSelectionTool.Brush), () => CanEditObject);
        EndScreenSelectionCommand = new(EndScreenSelection);
    }
    private void RefreshProjectItems()
    {
        ScanObjects.Clear();
        if (_scanProject is not null)
            foreach (var o in _scanProject.Objects) ScanObjects.Add(new(o.Id, o.Name, o.IsVisible, o.PointCount, o.Source.Points.Length));
        _selectedScanObject = ScanObjects.FirstOrDefault(o => o.Id == _scanProject?.ActiveObjectId);
        Raise(nameof(SelectedScanObject)); LoadObjectDraft(); NotifyProjectState();
    }
    private void LoadObjectDraft()
    {
        var o = ActiveScanObject; var t = o?.Transform ?? new();
        _objectName = o?.Name ?? ""; _objectX = t.X; _objectY = t.Y; _objectZ = t.Z;
        _objectRotateX = t.RotateX; _objectRotateY = t.RotateY; _objectRotateZ = t.RotateZ;
        _objectColor = DefaultObjectColors.FirstOrDefault(c => c.Value == o?.Tint) ?? new("Цвет из проекта", o?.Tint);
        foreach (var name in new[] { nameof(ObjectName), nameof(ObjectX), nameof(ObjectY), nameof(ObjectZ), nameof(ObjectRotateX), nameof(ObjectRotateY), nameof(ObjectRotateZ), nameof(ObjectColors), nameof(ObjectColor), nameof(SourceCoordinates), nameof(ImportTransformText), nameof(CanEditObject), nameof(CanVoxelClean) }) Raise(name);
        ResetCleanBox();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
    private void NotifyProjectState()
    {
        foreach (var name in new[] { nameof(CanManageProject), nameof(CanEditObject), nameof(CanVoxelClean), nameof(CanUndoProject), nameof(CanRedoProject), nameof(UndoProjectText), nameof(RedoProjectText), nameof(ProjectSummary), nameof(HasEditPreview), nameof(EditPreviewText), nameof(IsScreenSelecting), nameof(SelectionTool), nameof(SelectionHint), nameof(CanSave), nameof(CanExport), nameof(CanSelectScanObject), nameof(CanAddAnnotation), nameof(CloudQualityVisible) }) Raise(name);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
    private async Task ApplyObjectAsync()
    {
        if (!CanEditObject || ActiveScanObject is not { } o) return;
        var changed = o with { Name = ObjectName.Trim(), Tint = ObjectColor.Value,
            Transform = new((float)ObjectX, (float)ObjectY, (float)ObjectZ, (float)ObjectRotateX, (float)ObjectRotateY, (float)ObjectRotateZ) };
        await ChangeProjectAsync(_scanProject!.Replace(changed), "свойства объекта");
    }
    public async Task ToggleObjectAsync(Guid id)
    {
        if (!CanManageProject || _scanProject?.Objects.FirstOrDefault(o => o.Id == id) is not { } o) return;
        await ChangeProjectAsync(_scanProject.Replace(o with { IsVisible = !o.IsVisible }), o.IsVisible ? "скрытие объекта" : "показ объекта");
    }
    private async Task RemoveObjectAsync()
    {
        if (!CanEditObject || _scanProject is null) return;
        var objects = _scanProject.Objects.Where(o => o.Id != _scanProject.ActiveObjectId).ToArray();
        await ChangeProjectAsync(_scanProject with { Objects = objects, ActiveObjectId = objects.FirstOrDefault()?.Id ?? Guid.Empty }, "удаление объекта");
    }
    private async Task ChangeProjectAsync(ScanProject next, string description)
    {
        var before = _scanProject!;
        EndScreenSelection();
        var token = BeginOperation("Подготовка проекта · " + description);
        try
        {
            var view = await ScanProjectView.PrepareAsync(next, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator) return;
            _projectHistory.Commit(before, next, description);
            SetProject(next, view); MarkSessionDirty(); Status = "Применено: " + description; AddEvent(Status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось изменить проект", ex); }
        finally { EndOperation(); NotifyProjectState(); }
    }
    private async Task ReplayProjectAsync(bool redo)
    {
        if (!(redo ? CanRedoProject : CanUndoProject)) return;
        var change = (redo ? _projectHistory.RedoChange : _projectHistory.UndoChange)!;
        var next = redo ? change.After : change.Before;
        EndScreenSelection();
        var token = BeginOperation(redo ? "Повтор действия" : "Отмена действия");
        try
        {
            var view = await ScanProjectView.PrepareAsync(next, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator) return;
            if (redo) _projectHistory.CompleteRedo(); else _projectHistory.CompleteUndo();
            SetProject(next, view); MarkSessionDirty(); Status = (redo ? "Повторено: " : "Отменено: ") + change.Description;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        finally { EndOperation(); NotifyProjectState(); }
    }
    private void SetProject(ScanProject project, ScanProjectView view)
    {
        _scanProject = project; _projectView = view;
        _dataset = new(null, project.ToSession(UserAnnotations.ToArray()));
        ApplyProjectView(view); RefreshProjectItems(); NotifySourceProperties(); NotifySessionProperties();
    }
    private void ApplyProjectView(ScanProjectView view)
    {
        _importedGeometry = view.Geometry; _importedBounds = view.Data.Bounds;
        _pointOffsets = [view.Data.VisibleScan?.Points.Length ?? 0];
        Grid = view.Geometry?.Grid(); Cloud = null; Surface = null; CloudOnly = null;
        FloorSurface = null; DampSurface = null;
        if (IsSurface && !CanShowSurface) { _isSurface = false; Raise(nameof(IsSurface)); Raise(nameof(IsPoints)); Raise(nameof(IsPointMode)); Raise(nameof(IsSurfaceMode)); }
        _capturedFrames = -1; SetCapture(view.Data.VisibleScan is null ? 0 : 1);
    }
    private void FocusObject()
    {
        if (ActiveScanObject is not { } o || !CanInteract) return;
        ReturnToOverview();
        var bounds = ScanProjectEvaluator.ObjectBounds(o);
        var previous = _importedBounds; _importedBounds = bounds;
        FitImported(); _importedBounds = previous; CameraLabel = o.Name;
    }
    private void BeginScreenSelection(ScanSelectionTool tool)
    {
        if (!CanEditObject) return;
        EndInspection(); _selectionTool = tool; NotifyProjectState();
    }
    public void EndScreenSelection() { _selectionTool = ScanSelectionTool.None; NotifyProjectState(); }
}
