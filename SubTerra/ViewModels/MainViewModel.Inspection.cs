using System.Collections.ObjectModel;
using System.Numerics;
using HelixToolkit.SharpDX;
using SubTerra.Models;
using SubTerra.Rendering;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel
{
    private int _inspectionMode;
    private InspectionPoint? _pickedPoint;
    private InspectionPoint? _measurementStart;
    private InspectionPoint? _measurementEnd;
    private string _annotationName = "Метка";
    private bool _clipEnabled;
    private double _clipYaw;
    private double _clipPitch = 90;
    private double _clipOffset;
    private PointGeometry3D? _inspectionPoints;
    private LineGeometry3D? _measurementLine;
    private PointGeometry3D? _annotationGeometry;
    private LineGeometry3D? _clipPlaneGuide;
    public ObservableCollection<UserAnnotation> UserAnnotations { get; } = [];
    public bool IsPicking => _inspectionMode != 0 && IsOverview && !IsBusy;
    public bool CloudQualityVisible => IsPoints && !IsPicking && !HasRecovery;
    public string InspectionHint => _inspectionMode == 2 ? _measurementStart is null ? "Щёлкните первую точку расстояния" :
        "Щёлкните вторую точку · следующее нажатие начнёт новое измерение" : "Щёлкните поверхность или точку облака";
    public string PickedCoordinates => _pickedPoint is { } point ? $"X {point.X:F3} · Y {point.Y:F3} · Z {point.Z:F3} м" : "Точка не выбрана";
    public string MeasurementText => _measurementStart is { } start && _measurementEnd is { } end
        ? $"Расстояние: {Vector3.Distance(start.Position, end.Position):F3} м" : "Выберите две точки";
    public string AnnotationName { get => _annotationName; set => Set(ref _annotationName, value); }
    public bool CanAddAnnotation => IsAdministrator && CanInteract && !HasRecovery && _pickedPoint is not null && _capturedFrames > 0 && UserAnnotations.Count < 256;
    public PointGeometry3D? InspectionPoints { get => _inspectionPoints; private set => Set(ref _inspectionPoints, value); }
    public LineGeometry3D? MeasurementLine { get => _measurementLine; private set => Set(ref _measurementLine, value); }
    public PointGeometry3D? AnnotationGeometry { get => _annotationGeometry; private set => Set(ref _annotationGeometry, value); }
    public LineGeometry3D? ClipPlaneGuide { get => _clipPlaneGuide; private set => Set(ref _clipPlaneGuide, value); }
    public bool ClipEnabled { get => _clipEnabled; set { if (Set(ref _clipEnabled, value)) UpdateClip(); } }
    public double ClipYaw { get => _clipYaw; set { if (double.IsFinite(value) && Set(ref _clipYaw, Math.Clamp(value, -180, 180))) UpdateClip(); } }
    public double ClipPitch { get => _clipPitch; set { if (double.IsFinite(value) && Set(ref _clipPitch, Math.Clamp(value, -90, 90))) UpdateClip(); } }
    public double ClipOffset { get => _clipOffset; set { if (double.IsFinite(value) && Set(ref _clipOffset, Math.Clamp(value, -1000, 1000))) UpdateClip(); } }
    public bool ClipGuideVisible => ClipEnabled && IsPoints;
    private PointClipPlane? ActiveClipPlane => ClipEnabled && IsPoints ? PointClipPlane.FromAngles(ClipYaw, ClipPitch, ClipOffset) : null;

    public void BeginInspection(bool measure)
    {
        if (!CanInteract || _viewFrames == 0) return;
        ReturnToOverview();
        _inspectionMode = measure ? 2 : 1;
        _measurementStart = _measurementEnd = null;
        MeasurementLine = null; InspectionPoints = null;
        Raise(nameof(IsPicking)); Raise(nameof(CloudQualityVisible)); Raise(nameof(InspectionHint)); Raise(nameof(MeasurementText));
    }
    public void EndInspection()
    {
        _inspectionMode = 0;
        Raise(nameof(IsPicking)); Raise(nameof(CloudQualityVisible));
    }
    public void SelectInspectionPoint(Vector3 position)
    {
        var point = InspectionPoint.From(position);
        if (!IsPicking || !point.IsValid) return;
        _pickedPoint = point;
        if (_inspectionMode == 2)
        {
            if (_measurementStart is null || _measurementEnd is not null) { _measurementStart = point; _measurementEnd = null; }
            else _measurementEnd = point;
        }
        var points = new List<Vector3> { position };
        if (_measurementStart is { } start) points.Add(start.Position);
        InspectionPoints = SceneGeometry.Points(points);
        MeasurementLine = _measurementStart is { } a && _measurementEnd is { } b ? SceneGeometry.Lines([a.Position, b.Position]) : null;
        Raise(nameof(PickedCoordinates)); Raise(nameof(MeasurementText)); Raise(nameof(InspectionHint)); Raise(nameof(CanAddAnnotation));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
    private void AddAnnotation()
    {
        if (!CanAddAnnotation || _pickedPoint is not { } point) return;
        var name = AnnotationName.Trim();
        if (name.Length is < 1 or > 120) { Notification = "Название метки: от 1 до 120 символов."; return; }
        UserAnnotations.Add(new(name, point));
        MarkSessionDirty(); UpdateAnnotations();
        Raise(nameof(CanAddAnnotation));
    }
    private void RemoveAnnotation(UserAnnotation annotation)
    {
        if (!IsAdministrator || !CanInteract || HasRecovery || !UserAnnotations.Remove(annotation)) return;
        MarkSessionDirty(); UpdateAnnotations(); Raise(nameof(CanAddAnnotation));
    }
    private void NavigateAnnotation(UserAnnotation annotation)
    {
        if (!CanInteract || !UserAnnotations.Contains(annotation)) return;
        ReturnToOverview();
        var point = annotation.Point.Position;
        MoveCamera(point + new Vector3(3, -4, 2), point, annotation.Name);
    }
    private void AssignAnnotations(ScanSession session)
    {
        EndInspection();
        _pickedPoint = _measurementStart = _measurementEnd = null;
        InspectionPoints = null; MeasurementLine = null;
        UserAnnotations.Clear();
        foreach (var annotation in session.Annotations) UserAnnotations.Add(annotation);
        UpdateAnnotations();
        Raise(nameof(PickedCoordinates)); Raise(nameof(MeasurementText));
    }
    private void UpdateAnnotations() => AnnotationGeometry = SceneGeometry.Points(UserAnnotations.Select(annotation => annotation.Point.Position)
        .Where(point => ActiveClipPlane is not { } plane || plane.Contains(point)));
    private ScanSession SnapshotWithAnnotations() => new()
    {
        ScenarioId = _dataset!.Session.ScenarioId, SceneVersion = _dataset.Session.SceneVersion,
        Settings = _dataset.Session.Settings, Frames = _dataset.Session.Frames,
        Adjustments = _adjustments.ToArray(), Annotations = UserAnnotations.ToArray()
    };
    private void UpdateClip()
    {
        Raise(nameof(ClipGuideVisible));
        var plane = PointClipPlane.FromAngles(ClipYaw, ClipPitch, ClipOffset);
        var helper = Math.Abs(plane.Normal.Z) < .9f ? Vector3.UnitZ : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(plane.Normal, helper)) * 15;
        var up = Vector3.Normalize(Vector3.Cross(plane.Normal, right)) * 15;
        var center = plane.Normal * plane.Offset;
        var corners = new[] { center - right - up, center + right - up, center + right + up, center - right + up };
        ClipPlaneGuide = SceneGeometry.Lines([corners[0], corners[1], corners[1], corners[2], corners[2], corners[3], corners[3], corners[0]]);
        UpdateAnnotations();
        if (IsPoints) RefreshGeometry();
    }
}
