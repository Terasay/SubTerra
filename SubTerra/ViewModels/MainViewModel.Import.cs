using System.Diagnostics;
using System.IO;
using System.Numerics;
using SubTerra.Models;
using SubTerra.Rendering;

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
    public bool IsImported => _dataset?.Session.ImportedScan is not null;
    public bool IsSimulation => !IsImported;
    public bool CanShowSurface => !IsImported || _dataset!.Session.ImportedScan!.HasMesh;
    public string SurfaceModeText => IsImported ? "Сетка" : "Пещера";
    public string DataSourceText => IsImported ? "Реальный скан · метры · локальные координаты" : "Симуляция LiDAR · данные на этом компьютере";
    public string DistanceMetricTitle => IsImported ? "ГАБАРИТ X" : "ПРОЙДЕНО";
    public string DurationMetricTitle => IsImported ? "ГАБАРИТ Y" : "ВРЕМЯ СКАНА";
    public string CoverageMetricTitle => IsImported ? "ГАБАРИТ Z" : "ПОКРЫТИЕ";
    public string CoverageMetricText => IsImported ? $"{(_importedBounds.Maximum.Z - _importedBounds.Minimum.Z):F3} м" : $"{CoveragePercent:F0}%";
    public string ImportDetails => IsImported ? $"{_dataset!.Session.ImportedScan!.Metadata.FileName}\n" +
        $"{_dataset.Session.ImportedScan.Points.Length:N0} точек · {_dataset.Session.ImportedScan.Triangles.Length / 3:N0} треугольников\n" +
        "Просмотр ограничивает детализацию. Сохранение и экспорт включают весь скан." : "3DeVOK MQ → Studio → ASC / PLY → SubTerra";
    public string ExportButtonText => IsImported ? "Экспорт скана в PLY" : "Экспорт точек в PLY";
    public string ImportTransformText => IsImported ? $"Исходные единицы: {_dataset!.Session.ImportedScan!.Metadata.Unit switch { ScanUnit.Meters => "м", ScanUnit.Centimeters => "см", _ => "мм" }} · вверх: {_dataset.Session.ImportedScan.Metadata.UpAxis}\n" +
        $"Смещение XYZ: {_dataset.Session.ImportedScan.Metadata.OriginX:G10}; {_dataset.Session.ImportedScan.Metadata.OriginY:G10}; {_dataset.Session.ImportedScan.Metadata.OriginZ:G10}" : "";

    public async Task ImportAsync(string path, ScanImportOptions options)
    {
        if (!CanInteract || !await ConfirmReplacementAsync("импортировать скан")) return;
        ReturnToOverview(); Pause();
        var token = BeginOperation("Импорт скана · чтение файла");
        try
        {
            var scan = await new Services.ScanImporter().ImportAsync(path, options, token, OperationProgress(token, .9));
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            Assign(new(null, scan.ToSession()), 1);
            _sessionPath = null;
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
    private void AssignImported(ScanDataset dataset)
    {
        var scan = dataset.Session.ImportedScan!;
        _map = null; _geometry = null;
        _importedGeometry = new(scan); _importedBounds = _importedGeometry.Bounds;
        _pointOffsets = [scan.Points.Length];
        _baselineSettings = null;
        SelectedScenario = new("imported", Path.GetFileNameWithoutExtension(scan.Metadata.FileName), "РЕАЛЬНЫЙ 3D-СКАН · ASC / PLY", "Импортированные измерения в метрах");
        Grid = _importedGeometry.Grid(); Route = null; Markers = null; Scanner = null; ScannerHeading = null;
        VisibleMarkers.Clear();
        _isHeight = false; _isSurface = false;
        Raise(nameof(IsHeight)); Raise(nameof(IsSurface)); Raise(nameof(IsPoints)); Raise(nameof(IsCave));
        Raise(nameof(IsPointMode)); Raise(nameof(IsSurfaceMode));
        Raise(nameof(SurfaceMaterial)); Raise(nameof(RenderLabel)); Raise(nameof(ShadowsVisible)); Raise(nameof(SsaoEnabled));
        _capturedFrames = -1; SetCapture(1);
    }
    private void NotifySourceProperties()
    {
        foreach (var name in new[] { nameof(IsImported), nameof(IsSimulation), nameof(CanShowSurface), nameof(SurfaceModeText),
            nameof(DataSourceText), nameof(DistanceMetricTitle), nameof(DurationMetricTitle), nameof(CoverageMetricTitle),
            nameof(CoverageMetricText), nameof(ImportDetails), nameof(ExportButtonText), nameof(HeightMinText), nameof(HeightMaxText),
            nameof(TotalLengthText), nameof(ScannerVisible), nameof(RouteVisible), nameof(MarkersVisible), nameof(ExploreText),
            nameof(CloudQualityVisible), nameof(MarkerCountText), nameof(RecordingText) }) Raise(name);
        Raise(nameof(ClipMinimum)); Raise(nameof(ClipMaximum)); Raise(nameof(ClipStep)); Raise(nameof(NavigationSpeedText));
        Raise(nameof(ImportTransformText)); Raise(nameof(SourceCoordinates));
    }
    private void RefreshImportedGeometry()
    {
        var clock = Stopwatch.StartNew();
        if (IsPoints) Cloud = _viewFrames == 0 ? null : _importedGeometry!.Cloud(EffectivePointLimit, ActiveClipPlane, IsHeight);
        else Surface = _viewFrames == 0 ? null : _importedGeometry!.Mesh(IsHeight);
        _geometryMilliseconds = clock.Elapsed.TotalMilliseconds;
        Raise(nameof(GeometryMilliseconds)); Raise(nameof(PerformanceText)); Raise(nameof(RenderedPointCount)); Raise(nameof(CloudDisplayText));
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
