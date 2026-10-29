using System.Numerics;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel
{
    private double _cleanMinX, _cleanMinY, _cleanMinZ, _cleanMaxX, _cleanMaxY, _cleanMaxZ;
    private double _voxelSize = .001, _outlierRadius = .005;
    private int _minimumNeighbors = 3;
    public double CleanMinX { get => _cleanMinX; set => Set(ref _cleanMinX, value); }
    public double CleanMinY { get => _cleanMinY; set => Set(ref _cleanMinY, value); }
    public double CleanMinZ { get => _cleanMinZ; set => Set(ref _cleanMinZ, value); }
    public double CleanMaxX { get => _cleanMaxX; set => Set(ref _cleanMaxX, value); }
    public double CleanMaxY { get => _cleanMaxY; set => Set(ref _cleanMaxY, value); }
    public double CleanMaxZ { get => _cleanMaxZ; set => Set(ref _cleanMaxZ, value); }
    public double VoxelSize { get => _voxelSize; set => Set(ref _voxelSize, value); }
    public double OutlierRadius { get => _outlierRadius; set => Set(ref _outlierRadius, value); }
    public int MinimumNeighbors { get => _minimumNeighbors; set => Set(ref _minimumNeighbors, value); }
    public AsyncRelayCommand KeepCleanBoxCommand { get; private set; } = null!;
    public AsyncRelayCommand RemoveCleanBoxCommand { get; private set; } = null!;
    public AsyncRelayCommand VoxelCleanCommand { get; private set; } = null!;
    public AsyncRelayCommand RemoveOutliersCommand { get; private set; } = null!;
    public AsyncRelayCommand RestoreObjectCommand { get; private set; } = null!;
    public RelayCommand ResetCleanBoxCommand { get; private set; } = null!;
    private void InitializeCleaningCommands()
    {
        KeepCleanBoxCommand = new(() => PreviewBoxAsync(true), () => CanEditObject, ex => ReportError("Не удалось выделить область", ex));
        RemoveCleanBoxCommand = new(() => PreviewBoxAsync(false), () => CanEditObject, ex => ReportError("Не удалось выделить область", ex));
        VoxelCleanCommand = new(() => PreviewCleanupAsync((o, ct) => ScanCleaner.Voxel(o, VoxelSize, ct), "уменьшение плотности"), () => CanVoxelClean, ex => ReportError("Не удалось уменьшить плотность", ex));
        RemoveOutliersCommand = new(() => PreviewCleanupAsync((o, ct) => ScanCleaner.Isolated(o, OutlierRadius, MinimumNeighbors, ct), "удаление изолированных точек"), () => CanEditObject, ex => ReportError("Не удалось очистить выбросы", ex));
        RestoreObjectCommand = new(async () =>
        {
            if (ActiveScanObject is { Mask: not null } o) await ChangeProjectAsync(_scanProject!.Replace(o with { Mask = null }), "восстановление исходника");
        }, () => CanEditObject && ActiveScanObject?.Mask is not null, ex => ReportError("Не удалось восстановить исходник", ex));
        ResetCleanBoxCommand = new(ResetCleanBox, () => CanEditObject);
    }
    private void ResetCleanBox()
    {
        var bounds = ActiveScanObject is { } o ? ScanProjectEvaluator.ObjectBounds(o) : (Vector3.Zero, Vector3.Zero);
        _cleanMinX = bounds.Item1.X; _cleanMinY = bounds.Item1.Y; _cleanMinZ = bounds.Item1.Z;
        _cleanMaxX = bounds.Item2.X; _cleanMaxY = bounds.Item2.Y; _cleanMaxZ = bounds.Item2.Z;
        foreach (var name in new[] { nameof(CleanMinX), nameof(CleanMinY), nameof(CleanMinZ), nameof(CleanMaxX), nameof(CleanMaxY), nameof(CleanMaxZ) }) Raise(name);
    }
    public Task PreviewBoxAsync(bool keepInside)
    {
        var box = new ScanBox(new((float)CleanMinX, (float)CleanMinY, (float)CleanMinZ), new((float)CleanMaxX, (float)CleanMaxY, (float)CleanMaxZ));
        return PreviewCleanupAsync((o, ct) => ScanCleaner.Region(o, box, keepInside, ct), keepInside ? "оставить область" : "удалить область");
    }
    public Task PreviewScreenSelectionAsync(ScreenScanSelection selection)
    {
        if (!IsScreenSelecting) return Task.CompletedTask;
        var keep = KeepScreenSelection;
        EndScreenSelection();
        return PreviewCleanupAsync((o, ct) => ScanCleaner.Screen(o, selection, keep, ct), keep ? "оставить выделение" : "удалить выделение");
    }
    private async Task PreviewCleanupAsync(Func<ScanObject, CancellationToken, ScanPointMask> filter, string description)
    {
        if (!CanEditObject || ActiveScanObject is not { } o || _scanProject is null) return;
        EndScreenSelection(); EndInspection();
        var token = BeginOperation("Предпросмотр · " + description);
        try
        {
            var mask = await Task.Run(() => filter(o, token), token).WaitAsync(token);
            if (mask.Count == o.PointCount) { Status = "Выбранная операция не меняет объект"; return; }
            var next = _scanProject.Replace(o with { Mask = mask });
            var view = await ScanProjectView.PrepareAsync(next, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator) return;
            _previewProject = next; _previewView = view;
            _previewOperation = "очистка объекта";
            _previewDescription = $"{description}: останется {mask.Count:N0} из {o.PointCount:N0} точек. Удалится {o.PointCount - mask.Count:N0}.";
            ApplyProjectView(view); NotifySourceProperties();
            Status = "Предпросмотр очистки · примените результат или отмените";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось подготовить очистку", ex); }
        finally { EndOperation(); NotifyProjectState(); }
    }
    private void ApplyCleanPreview()
    {
        if (!HasEditPreview || !IsAdministrator || !CanInteract || HasRecovery) return;
        var next = _previewProject!; var view = _previewView!; var description = _previewDescription;
        var operation = _previewOperation;
        _projectHistory.Commit(_scanProject!, next, operation);
        _previewProject = null; _previewView = null; _previewDescription = "";
        SetProject(next, view); MarkSessionDirty(); Status = "Применено: " + operation + " · исходник сохранён"; AddEvent(description);
    }
    public void CancelCleanPreview()
    {
        if (!HasEditPreview || !CanInteract) return;
        _previewProject = null; _previewView = null; _previewDescription = "";
        ApplyProjectView(_projectView!); NotifySourceProperties(); NotifyProjectState(); Status = "Предпросмотр отменён · объект не изменён";
    }
}
