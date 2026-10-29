using System.Diagnostics;
using System.IO;
using System.Numerics;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using HelixToolkit.SharpDX;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel
{
    private ImportedGeometry? _importedGeometry;
    private (Vector3 Minimum, Vector3 Maximum) _importedBounds;
    private double _viewportAspectRatio = 16d / 9;
    public void SetViewportSize(double width, double height)
    {
        if (double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0)
            _viewportAspectRatio = Math.Clamp(width / height, .1, 10);
    }
    public bool IsImported => _dataset?.Session.ImportedScan is not null || _dataset?.Session.Project is not null;
    public bool IsSimulation => !IsImported;
    public bool HasSyntheticScans => _scanProject?.Objects.Any(o => o.Source.Metadata.IsSynthetic) == true;
    public AsyncRelayCommand LoadCollegeBuildingCommand { get; private set; } = null!;
    private ScanProjectView? CurrentProjectView => _previewView ?? _projectView;
    public bool CanShowSurface => !IsImported || HasIndoorPreview || CurrentProjectView?.Data.VisibleScan?.HasMesh == true;
    public bool CanSelectSurface => CanInteract && (!IsImported || CanShowSurface || !HasEditPreview && !IsScreenSelecting && !HasRecovery &&
        (HasStructureAnalysis && !StructureIsStale || IsAdministrator && _scanProject?.Objects.Where(o => o.IsVisible).Sum(o => o.PointCount) >= 64));
    public string SurfaceModeHint => CanShowSurface ? "Показать поверхности" : HasStructureAnalysis && !StructureIsStale ? "Восстановить поверхности по имеющемуся анализу" :
        IsAdministrator ? "Построить поверхности по цветным точкам; анализ можно отменить" : "Первое построение сетки по точкам требует входа администратора";
    public string SurfaceModeText => IsImported ? HasReconstruction ? "Реконструкция" : HasIndoorPreview ? "Поверхности" : CanShowSurface ? "Сетка" : "Построить сетку" : "Пещера";
    public async Task ShowSurfaceAsync()
    {
        if (!CanSelectSurface) return;
        if (!IsImported || HasReconstruction || CurrentProjectView?.Data.VisibleScan?.HasMesh == true) { SetView(true, false); return; }
        ReturnToOverview(); EndInspection();
        if (!CanPrepareIndoorPreview)
        {
            var before = _structureDocument;
            UseNeuralClasses = true; Raise(nameof(UseNeuralClasses));
            await AnalyzeStructureAsync();
            if (_structureDocument is null || ReferenceEquals(before, _structureDocument)) return;
        }
        if (CanPrepareIndoorPreview) await ReconstructRoomAsync();
    }
    public string DataSourceText => IsImported ? HasSyntheticScans ? "Проект содержит синтетическую модель · метры · Z вверх" :
        "Сканы проекта · метры · локальные координаты" : "Симуляция LiDAR · данные на этом компьютере";
    public string DistanceMetricTitle => IsImported ? "ГАБАРИТ X" : "ПРОЙДЕНО";
    public string DurationMetricTitle => IsImported ? "ГАБАРИТ Y" : "ВРЕМЯ СКАНА";
    public string CoverageMetricTitle => IsImported ? "ГАБАРИТ Z" : "ПОКРЫТИЕ";
    public string CoverageMetricText => IsImported ? $"{(_importedBounds.Maximum.Z - _importedBounds.Minimum.Z):F3} м" : $"{CoveragePercent:F0}%";
    public string ImportDetails => IsImported ? $"{ProjectSummary}\nВидимых точек: {CurrentProjectView?.Data.VisibleScan?.Points.Length ?? 0:N0}\n" +
        "Проект хранит исходники и очистку. PLY включает видимые объекты после очистки." : "3DeVOK MQ → Studio → ASC / PLY → SubTerra";
    public string ExportButtonText => IsImported ? "Экспорт скана в PLY" : "Экспорт точек в PLY";
    public string ImportTransformText => ActiveScanObject is { } o ? $"Исходные единицы: {o.Source.Metadata.Unit switch { ScanUnit.Meters => "м", ScanUnit.Centimeters => "см", _ => "мм" }} · вверх: {o.Source.Metadata.UpAxis}\n" +
        $"Смещение XYZ: {o.Source.Metadata.OriginX:G10}; {o.Source.Metadata.OriginY:G10}; {o.Source.Metadata.OriginZ:G10}" : "";
    private PointGeometry3D? _cloudOnly;
    public PointGeometry3D? CloudOnly { get => _cloudOnly; private set => Set(ref _cloudOnly, value); }
    public bool ImportedMeshVisible => IsImported && IsSurface;
    private bool CoarseMesh => AdaptiveQuality && IsOverview && Vector3.Distance(new((float)Camera.Position.X, (float)Camera.Position.Y, (float)Camera.Position.Z),
        (_importedBounds.Minimum + _importedBounds.Maximum) / 2) > Math.Max(.01f, (_importedBounds.Maximum - _importedBounds.Minimum).Length()) * 4;
    public string MeshPreviewText => IsIndoorSurfaceView ? IndoorPreviewSummary : $"Сетка: {_importedGeometry?.MeshTriangleCount(CoarseMesh) ?? 0:N0} из {CurrentProjectView?.Data.VisibleScan?.Triangles.Length / 3 ?? 0:N0} треугольников · упрощение только для просмотра";
    public bool CanExport => CanSave && (!IsImported || CurrentProjectView?.Data.VisibleScan is not null);

    public async Task LoadCollegeBuildingAsync()
    {
        if (!CanChangeScenario || HasRecovery || HasEditPreview || !await ConfirmReplacementAsync("создать модель корпуса")) return;
        ReturnToOverview();
        var token = BeginOperation("Моделирование корпуса · три этажа и лестница");
        try
        {
            var project = await Task.Run(() => Simulation.CollegeBuildingFactory.Create(token), token);
            var view = await ScanProjectView.PrepareAsync(project, token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            Assign(new(null, project.ToSession()), 1, view);
            _sessionPath = null; MarkSessionDirty(); NotifySessionProperties();
            await ClearRecoveryAsync(); Home(); Notification = "";
            Status = "Синтетический корпус: 3 этажа, 18 аудиторий и лестница. Выберите этаж → «Только выбранный» → «Анализ».";
            AddEvent(Status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось создать модель корпуса", ex); }
        finally { EndOperation(); }
    }

    public async Task ImportAsync(string path, ScanImportOptions options, bool addToProject = false)
    {
        var append = addToProject && IsImported;
        if (!CanInteract || (append && !CanManageProject) || (!append && !await ConfirmReplacementAsync("импортировать скан"))) return;
        ReturnToOverview(); Pause();
        var token = BeginOperation("Импорт скана · чтение файла");
        try
        {
            var scan = await new Services.ScanImporter().ImportAsync(path, options, token, OperationProgress(token, .9));
            var next = ScanProject.FromSession(scan.ToSession());
            if (append)
            {
                var o = next.Objects[0];
                if (_scanProject!.Origin is { } origin && origin.Unit == scan.Metadata.Unit && origin.UpAxis == scan.Metadata.UpAxis)
                {
                    var delta = new Vector3((float)((scan.Metadata.OriginX - origin.OriginX) * options.Scale), (float)((scan.Metadata.OriginY - origin.OriginY) * options.Scale), (float)((scan.Metadata.OriginZ - origin.OriginZ) * options.Scale));
                    if (origin.UpAxis == ScanUpAxis.Y) delta = new(delta.X, -delta.Z, delta.Y);
                    o = o with { Transform = new(delta.X, delta.Y, delta.Z) };
                }
                next = _scanProject! with { Objects = [.. _scanProject.Objects, o], ActiveObjectId = o.Id };
            }
            var view = await ScanProjectView.PrepareAsync(next, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || (append && !IsAdministrator)) return;
            if (append) { _projectHistory.Commit(_scanProject!, next, "добавление объекта"); SetProject(next, view); }
            else { Assign(new(null, next.ToSession()), 1, view); _sessionPath = null; }
            MarkSessionDirty();
            NotifySessionProperties();
            await ClearRecoveryAsync();
            Home();
            Status = $"Импортировано {scan.Points.Length:N0} точек · проверьте размер по известному расстоянию";
            Notification = "";
            AddEvent("Импортирован скан " + Path.GetFileName(path));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось импортировать скан", ex); }
        finally { EndOperation(); }
    }
    private void AssignImported(ScanDataset dataset, ScanProjectView? prepared)
    {
        _map = null; _geometry = null;
        _previewProject = null; _previewView = null; _projectHistory.Clear(); EndScreenSelection();
        var project = ScanProject.FromSession(dataset.Session);
        var view = prepared ?? ScanProjectView.PrepareAsync(project).GetAwaiter().GetResult();
        _baselineSettings = null;
        var synthetic = project.Objects.Any(o => o.Source.Metadata.IsSynthetic);
        SelectedScenario = new("imported", synthetic ? "Модель корпуса" : "Проект сканов",
            synthetic ? "СИНТЕТИЧЕСКИЕ ДАННЫЕ · ТЕСТ АНАЛИЗА" : "3D-СКАНЫ · ASC / PLY", "Геометрия в метрах");
        Route = null; Markers = null; Scanner = null; ScannerHeading = null;
        VisibleMarkers.Clear();
        _isHeight = false; _isSurface = false;
        Raise(nameof(IsHeight)); Raise(nameof(IsSurface)); Raise(nameof(IsPoints)); Raise(nameof(IsCave));
        Raise(nameof(IsPointMode)); Raise(nameof(IsSurfaceMode));
        Raise(nameof(SurfaceMaterial)); Raise(nameof(RenderLabel)); Raise(nameof(ShadowsVisible)); Raise(nameof(SsaoEnabled));
        SetProject(project, view);
    }
    private void NotifySourceProperties()
    {
        foreach (var name in new[] { nameof(IsImported), nameof(IsSimulation), nameof(HasSyntheticScans), nameof(Scenarios), nameof(CanShowSurface), nameof(SurfaceModeText),
            nameof(DataSourceText), nameof(WalkAmbientColor), nameof(DistanceMetricTitle), nameof(DurationMetricTitle), nameof(CoverageMetricTitle),
            nameof(CoverageMetricText), nameof(ImportDetails), nameof(ExportButtonText), nameof(HeightMinText), nameof(HeightMaxText),
            nameof(TotalLengthText), nameof(ScannerVisible), nameof(RouteVisible), nameof(MarkersVisible), nameof(ExploreText),
            nameof(CloudQualityVisible), nameof(MarkerCountText), nameof(RecordingText) }) Raise(name);
        Raise(nameof(ClipMinimum)); Raise(nameof(ClipMaximum)); Raise(nameof(ClipStep)); Raise(nameof(NavigationSpeedText));
        Raise(nameof(ImportTransformText)); Raise(nameof(SourceCoordinates));
        Raise(nameof(ImportedMeshVisible)); Raise(nameof(MeshPreviewText)); NotifyProjectState();
    }
    private void RefreshImportedGeometry()
    {
        var clock = Stopwatch.StartNew();
        if (IsPoints) Cloud = _viewFrames == 0 ? null : _importedGeometry?.Cloud(EffectivePointLimit, ActiveClipPlane, IsHeight);
        else Surface = _viewFrames == 0 ? null : IsIndoorSurfaceView ? _indoorMeshes[IndoorStyle.Style] : _importedGeometry?.Mesh(IsHeight, CoarseMesh);
        CloudOnly = !IsSurface ? null : IsIndoorSurfaceView ? _indoorRemaining?.Cloud(EffectivePointLimit, null, IsHeight) : CurrentProjectView?.CloudOnly?.Cloud(EffectivePointLimit, null, IsHeight);
        _geometryMilliseconds = clock.Elapsed.TotalMilliseconds;
        Raise(nameof(GeometryMilliseconds)); Raise(nameof(PerformanceText)); Raise(nameof(RenderedPointCount)); Raise(nameof(CloudDisplayText)); Raise(nameof(MeshPreviewText));
    }
    private void ConfigureNavigationClipping()
    {
        if (!IsImported) return;
        var size = Math.Max(.001, (_importedBounds.Maximum - _importedBounds.Minimum).Length());
        Camera.NearPlaneDistance = Math.Clamp(size / 20_000, .00001, .01);
        Camera.FarPlaneDistance = Math.Max(Camera.FarPlaneDistance, size * 12);
    }
    private void FitImported(bool top = false)
    {
        var center = (_importedBounds.Minimum + _importedBounds.Maximum) * .5f;
        var radius = Math.Max(.005f, (_importedBounds.Maximum - _importedBounds.Minimum).Length() / 2);
        var halfAngle = Math.Atan(Math.Tan(Camera.FieldOfView * Math.PI / 360) / Math.Max(1, _viewportAspectRatio));
        var distance = radius / (float)Math.Sin(halfAngle) * 1.15f;
        Camera.NearPlaneDistance = Math.Clamp(radius / 1000, .00001, .05);
        Camera.FarPlaneDistance = Math.Max(10, distance + radius * 10);
        if (!top) MoveCamera(center + Vector3.Normalize(new Vector3(1, -2, 1.3f)) * distance, center, "Весь скан");
        else
        {
            MoveCamera(center + Vector3.UnitZ * distance, center, "Скан сверху");
            Camera.UpDirection = new(0, 1, 0);
        }
    }
}
