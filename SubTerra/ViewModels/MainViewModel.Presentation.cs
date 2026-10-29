using SubTerra.Models;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel
{
    private string _workspaceSection = "Проект", _workspaceView = "3D", _inspectorSubject = "Скан";
    private bool _isWelcomeVisible = true, _isSettingsOpen, _isRoomEditing;
    public RelayCommand<string> SelectSectionCommand { get; private set; } = null!;
    public RelayCommand<string> SelectWorkspaceViewCommand { get; private set; } = null!;
    public RelayCommand<string> InspectCommand { get; private set; } = null!;
    public RelayCommand ShowWelcomeCommand { get; private set; } = null!;
    public RelayCommand ContinueWorkspaceCommand { get; private set; } = null!;
    public RelayCommand ToggleSettingsCommand { get; private set; } = null!;
    public bool HasWorkspace => _dataset is not null;
    public bool IsWelcomeVisible { get => _isWelcomeVisible; private set { if (Set(ref _isWelcomeVisible, value)) Raise(nameof(IsWorkspaceVisible)); } }
    public bool IsWorkspaceVisible => !IsWelcomeVisible;
    public bool IsSettingsOpen { get => _isSettingsOpen; set => Set(ref _isSettingsOpen, value); }
    public bool IsRoomEditing { get => _isRoomEditing; set => Set(ref _isRoomEditing, value); }
    public string WorkspaceSection => _workspaceSection;
    public bool IsProjectSection => WorkspaceSection == "Проект";
    public bool IsProcessingSection => WorkspaceSection == "Обработка";
    public bool IsAnalysisSection => WorkspaceSection == "Анализ";
    public bool IsResultsSection => WorkspaceSection == "Результаты";
    public string WorkspaceView => _workspaceView;
    public bool Is3DView => WorkspaceView == "3D";
    public bool IsPlanView => WorkspaceView == "План 2D";
    public bool IsSplitView => WorkspaceView == "Рядом";
    public bool Show3DWorkspace => !IsPlanView;
    public bool ShowPlanWorkspace => !Is3DView;
    public string InspectorTitle => _inspectorSubject == "Поверхность" ? "Свойства поверхности" : _inspectorSubject == "Комната" ? "Свойства комнаты" : _inspectorSubject == "Предмет" ? "Свойства предмета" : "Свойства скана";
    public bool ShowScanInspector => IsImported && _inspectorSubject == "Скан";
    public bool ShowSurfaceInspector => IsImported && _inspectorSubject == "Поверхность";
    public bool ShowRoomInspector => IsImported && _inspectorSubject == "Комната";
    public bool ShowObjectInspector => IsImported && _inspectorSubject == "Предмет";
    public bool ShowSimulationInspector => IsSimulation;
    public RoomPlan? CurrentRoomPlan => RoomPlanVisible ? _structureDocument?.Room : null;
    public string SourceBadge => !HasWorkspace ? "Рабочее пространство" : IsIndoorSurfaceView && HasReconstruction ? "Реконструкция · гипотеза геометрии" : IsImported ? HasSyntheticScans ? "Есть синтетические данные" : "Импортированный скан" : "Синтетическая сцена";
    public string SurfaceAnalysisMethodText => _structureDocument?.Result.Options.UseNeuralClasses == true ? "Классы предложены экспериментальной MLP · требуется проверка" : "Геометрические классы · требуется проверка";
    public string AnalysisProgressText => StructureIsStale ? "Данные изменились · повторите поиск поверхностей" : !HasStructureAnalysis ? "01 / Обнаружение поверхностей" :
        StructureSurfaces.Any(s => s.Review == StructureReviewState.Proposed) ? "02 / Проверка кандидатов оператором" : HasRoom ? "04 / План готов к просмотру" : "03 / Построение плана комнаты";
    public string ReviewProgressText => $"Проверено {StructureSurfaces.Count(s => s.Review != StructureReviewState.Proposed)} из {StructureSurfaces.Count}";
    public string NextAnalysisStep => !HasStructureAnalysis ? "Найдите плоские участки в видимых сканах." : StructureIsStale ? "Геометрия изменилась. Старые результаты скрыты." :
        StructureSurfaces.Any(s => s.Review == StructureReviewState.Proposed) ? "Выберите поверхность и проверьте её класс справа." : "Откройте результаты и постройте план по подтверждённым поверхностям.";

    private void InitializePresentationCommands()
    {
        SelectSectionCommand = new(SelectSection, _ => CanInteract && !IsWelcomeVisible);
        SelectWorkspaceViewCommand = new(SelectWorkspaceView, _ => CanInteract && !HasEditPreview);
        InspectCommand = new(Inspect, _ => CanInteract);
        ShowWelcomeCommand = new(() => { Pause(); ReturnToOverview(); IsSettingsOpen = false; IsWelcomeVisible = true; }, () => CanInteract && !HasEditPreview && !IsScreenSelecting);
        ContinueWorkspaceCommand = new(() => IsWelcomeVisible = false, () => HasWorkspace && CanInteract);
        ToggleSettingsCommand = new(() => IsSettingsOpen = !IsSettingsOpen, () => CanInteract);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CanExport)) Raise(nameof(CanExportReconstruction));
            if (e.PropertyName == nameof(IsNavigating) && IsNavigating && !Is3DView)
            {
                _workspaceView = "3D"; NotifyPresentationState();
            }
            if (e.PropertyName is nameof(HasStructureAnalysis) or nameof(StructureIsStale) or nameof(SelectedStructureSurface) or nameof(HasRoom) or nameof(RoomIsStale) or nameof(RoomPlanPoints) or nameof(HasSyntheticScans) or nameof(HasReconstruction) or nameof(IsIndoorSurfaceView))
                NotifyPresentationState();
            if (e.PropertyName == nameof(IsImported))
            {
                // Project history also raises this notification; only Assign/EnterWorkspace resets navigation.
                NotifyPresentationState();
            }
        };
    }

    private void SelectSection(string section)
    {
        if (section is not ("Проект" or "Обработка" or "Анализ" or "Результаты")) return;
        _workspaceSection = section; IsSidebarOpen = true; IsSettingsOpen = false;
        NotifyPresentationState();
    }
    private void SelectWorkspaceView(string view)
    {
        if (view is not ("3D" or "План 2D" or "Рядом") || view != "3D" && !IsImported) return;
        ReturnToOverview(); EndInspection(); EndScreenSelection();
        if (view != "3D") IsSidebarOpen = false;
        if (view == "Рядом") IsInspectorOpen = false;
        _workspaceView = view;
        NotifyPresentationState();
    }
    public void Inspect(string subject)
    {
        if (subject is not ("Скан" or "Поверхность" or "Комната" or "Предмет")) return;
        _inspectorSubject = subject;
        NotifyPresentationState(); IsInspectorOpen = true;
    }
    private void EnterWorkspace()
    {
        IsSettingsOpen = false; IsRoomEditing = false;
        IsWelcomeVisible = false; _workspaceSection = "Проект"; _workspaceView = "3D"; _inspectorSubject = "Скан";
        NotifyPresentationState();
    }
    private void NotifyPresentationState()
    {
        foreach (var name in new[] { nameof(HasWorkspace), nameof(WorkspaceSection), nameof(IsProjectSection), nameof(IsProcessingSection), nameof(IsAnalysisSection), nameof(IsResultsSection),
            nameof(WorkspaceView), nameof(Is3DView), nameof(IsPlanView), nameof(IsSplitView), nameof(Show3DWorkspace), nameof(ShowPlanWorkspace),
            nameof(InspectorTitle), nameof(ShowScanInspector), nameof(ShowSurfaceInspector), nameof(ShowRoomInspector), nameof(ShowObjectInspector), nameof(ShowSimulationInspector),
            nameof(CurrentRoomPlan), nameof(SourceBadge), nameof(SurfaceAnalysisMethodText), nameof(AnalysisProgressText), nameof(ReviewProgressText), nameof(NextAnalysisStep) }) Raise(name);
    }
}
