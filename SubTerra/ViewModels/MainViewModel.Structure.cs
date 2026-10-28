using System.Collections.ObjectModel;
using System.Numerics;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using SubTerra.Models;
using SubTerra.Services;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IStructureAnalyzer _structureAnalyzer;
    private StructureAnalysisResult? _structureAnalysis;
    private StructureSurface? _selectedStructureSurface;
    private PointGeometry3D? _structureGeometry;
    private LineGeometry3D? _structureOutline;
    private bool _showStructure = true;
    private double _structureTolerance = .025;
    public ObservableCollection<StructureSurface> StructureSurfaces { get; } = [];
    public AsyncRelayCommand AnalyzeStructureCommand { get; private set; } = null!;
    public RelayCommand ClearStructureCommand { get; private set; } = null!;
    public bool CanAnalyzeStructure => CanManageProject && !IsScreenSelecting &&
        _scanProject?.Objects.Where(o => o.IsVisible).Sum(o => o.PointCount) >= 64;
    public bool HasStructureAnalysis => _structureAnalysis is not null;
    public bool StructureVisible => HasStructureAnalysis && ShowStructure && IsImported;
    public bool ShowStructure { get => _showStructure; set { if (Set(ref _showStructure, value)) Raise(nameof(StructureVisible)); } }
    public double StructureTolerance
    {
        get => _structureTolerance;
        set { if (double.IsFinite(value) && value is >= .001 and <= .1) Set(ref _structureTolerance, value); }
    }
    public string StructureSummary => _structureAnalysis is not { } result ?
        "Видимые сканы одного помещения · вертикаль Z. Результаты — геометрические кандидаты." :
        $"Поверхностей: {result.Surfaces.Length}. Анализ {result.SampleCount:N0} из {result.SourcePointCount:N0} точек. " +
        $"Допуск {result.Options.DistanceTolerance:F3} м. " +
        (result.Surfaces.Length == 0 ? "Не найдено достаточно крупных плоских участков." : "Классы требуют проверки оператором.");
    public StructureSurface? SelectedStructureSurface
    {
        get => _selectedStructureSurface;
        set
        {
            if (value is not null && !StructureSurfaces.Contains(value)) return;
            if (!Set(ref _selectedStructureSurface, value)) return;
            UpdateStructureOutline();
        }
    }
    public PointGeometry3D? StructureGeometry { get => _structureGeometry; private set => Set(ref _structureGeometry, value); }
    public LineGeometry3D? StructureOutline { get => _structureOutline; private set => Set(ref _structureOutline, value); }

    private void InitializeStructureCommands()
    {
        AnalyzeStructureCommand = new(AnalyzeStructureAsync, () => CanAnalyzeStructure,
            ex => ReportError("Не удалось проанализировать структуру", ex));
        ClearStructureCommand = new(ClearStructureAnalysis, () => CanInteract && HasStructureAnalysis);
    }
    public async Task AnalyzeStructureAsync()
    {
        if (!CanAnalyzeStructure || _scanProject is not { } snapshot) return;
        EndInspection(); EndScreenSelection();
        var options = new StructureAnalysisOptions(DistanceTolerance: StructureTolerance);
        var token = BeginOperation("Поиск плоских поверхностей помещения");
        try
        {
            var result = await _structureAnalyzer.AnalyzeAsync(snapshot, options, OperationProgress(token), token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            if (!IsAdministrator || !ReferenceEquals(_scanProject, snapshot))
            { Status = "Результат анализа не применён: проект или доступ изменился"; return; }
            SetStructureAnalysis(result);
            Status = $"Анализ завершён · кандидатов: {result.Surfaces.Length}";
            AddEvent(Status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось проанализировать структуру", ex); }
        finally { EndOperation(); }
    }
    private void SetStructureAnalysis(StructureAnalysisResult result)
    {
        ClearStructureAnalysis();
        _structureAnalysis = result;
        foreach (var surface in result.Surfaces) StructureSurfaces.Add(surface);
        UpdateStructureDisplay();
        SelectedStructureSurface = StructureSurfaces.FirstOrDefault();
        ShowStructure = true;
        NotifyStructureState();
    }
    private void UpdateStructureDisplay()
    {
        if (_structureAnalysis is null) return;
        var positions = new Vector3Collection(); var colors = new Color4Collection();
        foreach (var surface in StructureSurfaces)
        {
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
        var boundary = SelectedStructureSurface?.Boundary ?? [];
        var segments = new List<Vector3>();
        for (var i = 0; i < boundary.Length; i++)
        {
            var a = boundary[i]; var b = boundary[(i + 1) % boundary.Length];
            if (ActiveClipPlane is { } plane)
            {
                var insideA = plane.Contains(a); var insideB = plane.Contains(b);
                if (!insideA && !insideB) continue;
                if (insideA != insideB)
                {
                    var cut = Vector3.Lerp(a, b, (plane.Offset + .00001f - Vector3.Dot(plane.Normal, a)) / Vector3.Dot(plane.Normal, b - a));
                    if (insideA) b = cut; else a = cut;
                }
            }
            segments.Add(a); segments.Add(b);
        }
        StructureOutline = segments.Count == 0 ? null : Rendering.SceneGeometry.Lines(segments);
    }
    private void ClearStructureAnalysis()
    {
        _structureAnalysis = null;
        SelectedStructureSurface = null;
        StructureSurfaces.Clear(); StructureGeometry = null; StructureOutline = null;
        NotifyStructureState();
    }
    private void NotifyStructureState()
    {
        Raise(nameof(CanAnalyzeStructure)); Raise(nameof(HasStructureAnalysis));
        Raise(nameof(StructureVisible)); Raise(nameof(StructureSummary));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
