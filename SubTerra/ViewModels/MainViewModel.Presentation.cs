using SubTerra.Models;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel
{
    private string _workspaceSection = "Проект", _workspaceView = "3D", _inspectorSubject = "Скан";
    private string _analysisTool = "Поверхности";
    private string _processingTool = "Очистка";
    private bool _isWelcomeVisible = true, _isSettingsOpen, _isRoomEditing;
    public RelayCommand<string> SelectSectionCommand { get; private set; } = null!;
    public RelayCommand<string> SelectWorkspaceViewCommand { get; private set; } = null!;
    public RelayCommand<string> InspectCommand { get; private set; } = null!;
    public RelayCommand ShowWelcomeCommand { get; private set; } = null!;
    public RelayCommand ContinueWorkspaceCommand { get; private set; } = null!;
    public RelayCommand ToggleSettingsCommand { get; private set; } = null!;
    public RelayCommand<string> SelectAnalysisToolCommand { get; private set; } = null!;
    public RelayCommand<string> SelectProcessingToolCommand { get; private set; } = null!;
    public RelayCommand ToggleProjectPanelCommand { get; private set; } = null!;
    public RelayCommand OpenPeopleAnalysisCommand { get; private set; } = null!;
    public RelayCommand OpenRoomPlanNextStepCommand { get; private set; } = null!;
    public RelayCommand RequestAdminAccessCommand { get; private set; } = null!;
    public bool HasWorkspace => _dataset is not null;
    public bool IsWelcomeVisible { get => _isWelcomeVisible; private set { if (Set(ref _isWelcomeVisible, value)) Raise(nameof(IsWorkspaceVisible)); } }
    public bool IsWorkspaceVisible => !IsWelcomeVisible;
    public bool IsSettingsOpen { get => _isSettingsOpen; set => Set(ref _isSettingsOpen, value); }
    public bool IsRoomEditing { get => _isRoomEditing; set => Set(ref _isRoomEditing, value); }
    public string WorkspaceSection => _workspaceSection;
    public bool IsProjectSection => WorkspaceSection == "Проект";
    public bool IsProcessingSection => WorkspaceSection == "Обработка";
    public bool IsAnalysisSection => WorkspaceSection == "Анализ";
    public bool IsSurfaceAnalysis => _analysisTool == "Поверхности";
    public bool IsPeopleAnalysis => _analysisTool == "Люди";
    public bool IsTunnelAnalysis => _analysisTool == "Тоннель";
    public bool IsResultsSection => WorkspaceSection == "Результаты";
    public bool IsCleaningTool => _processingTool == "Очистка";
    public bool IsAlignmentTool => _processingTool == "Совмещение";
    public bool IsClipTool => _processingTool == "Срез";
    public string ProjectPanelLabel => IsImported ? "Объекты" : "Сцена";
    public string TaskPanelTitle => IsProjectSection ? ProjectPanelLabel :
        IsProcessingSection ? IsSimulation ? "Виртуальный сканер" : _processingTool switch
        { "Совмещение" => "Совмещение сканов", "Срез" => "Срез облака", _ => "Очистка скана" } :
        IsAnalysisSection ? _analysisTool switch { "Люди" => "Поиск людей", "Тоннель" => "Сечения тоннеля", _ => "Поверхности помещения" } :
        IsSimulation ? "Измерения и метки" : IsTunnelMode ? "Результаты тоннеля" : "План помещения";
    public bool ShowActiveScanPicker => IsImported && !IsProjectSection && ScanObjects.Count > 1;
    public string WorkspaceView => _workspaceView;
    public bool Is3DView => WorkspaceView == "3D";
    public bool IsPlanView => WorkspaceView == "План 2D";
    public bool IsSplitView => WorkspaceView == "Рядом";
    public bool Show3DWorkspace => !IsPlanView;
    public bool ShowPlanWorkspace => !Is3DView;
    public string PlanViewLabel => IsTunnelMode ? "Сечение" : "План";
    public string SceneSummary => $"{PointCountText} точек  ·  {SourceBadge}";
    public string InspectorTitle => _inspectorSubject == "Поверхность" ? "Свойства поверхности" : _inspectorSubject == "Комната" ? "Свойства комнаты" : _inspectorSubject == "Предмет" ? "Свойства предмета" : "Свойства скана";
    public bool ShowScanInspector => IsImported && _inspectorSubject == "Скан";
    public bool ShowSurfaceInspector => IsImported && !IsTunnelMode && _inspectorSubject == "Поверхность";
    public bool ShowRoomInspector => IsImported && !IsTunnelMode && _inspectorSubject == "Комната";
    public bool ShowObjectInspector => IsImported && _inspectorSubject == "Предмет";
    public bool ShowSimulationInspector => IsSimulation;
    public RoomPlan? CurrentRoomPlan => RoomPlanVisible ? _structureDocument?.Room : null;
    public string SourceBadge => !HasWorkspace ? "Рабочее пространство" : IsGeometryShading ? HasSyntheticScans ? "Синтетические данные · рельеф без RGB" : "Рельеф · нейтральный материал" : UsesOriginalMaterials ? "Исходная модель · материалы и текстуры" : IsIndoorSurfaceView && HasReconstruction ? "Реконструкция · гипотеза геометрии" : IsImported ? HasSyntheticScans ? "Есть синтетические данные" : "Импортированный скан" : "Синтетическая сцена";
    public string SurfaceAnalysisMethodText => _structureDocument?.Result.Options.UseNeuralClasses == true ? "Классы предложены экспериментальной MLP · требуется проверка" : "Геометрические классы · требуется проверка";
    public bool HasCurrentStructureAnalysis => IsRoomMode && HasStructureAnalysis && !StructureIsStale;
    public string AnalysisProgressText => StructureIsStale ? "Данные изменились" : !HasStructureAnalysis ? "Найдите поверхности" :
        HasRoom && !RoomIsStale ? "План готов" : HasRoomPlanInputs ? "Можно построить план" : "Проверьте поверхности";
    public string ReviewProgressText => $"Проверено {StructureSurfaces.Count(s => s.Review != StructureReviewState.Proposed)} из {StructureSurfaces.Count}";
    public string NextAnalysisStep => !HasStructureAnalysis ? "Найдите плоские участки в видимых сканах." : StructureIsStale ? "Геометрия изменилась. Старые результаты скрыты." :
        HasRoom && !RoomIsStale ? "План готов. Откройте его переключателем над сценой." : HasRoomPlanInputs ? "Постройте план по подтверждённым поверхностям." : "Подтвердите пол и стены нужной комнаты.";
    public bool HasRoomPlanInputs => HasCurrentStructureAnalysis &&
        StructureSurfaces.Any(s => s.Kind == StructureSurfaceKind.Floor && s.Review == StructureReviewState.Confirmed) &&
        StructureSurfaces.Count(s => s.Kind == StructureSurfaceKind.Wall && s.Review == StructureReviewState.Confirmed) >= 3;
    public string RoomPlanNextAction => IsGuest ? "Войти для построения" : HasRoomPlanInputs ? "К построению плана" : "К анализу поверхностей";
    public string RoomPlanEmptyHint => IsGuest ? "Для построения плана нужны права администратора." :
        HasRoomPlanInputs ? "Постройте план по проверенным поверхностям." : "Найдите и подтвердите пол и стены одной комнаты.";

    private void InitializePresentationCommands()
    {
        SelectSectionCommand = new(SelectSection, _ => CanInteract && !IsWelcomeVisible);
        ToggleProjectPanelCommand = new(() =>
        {
            if (IsProjectSection && IsSidebarOpen && !IsPresentationMode) IsSidebarOpen = false;
            else SelectSection("Проект");
        }, () => CanInteract && !IsWelcomeVisible);
        SelectProcessingToolCommand = new(tool =>
        {
            if (tool is not ("Очистка" or "Совмещение" or "Срез")) return;
            EndInspection(); EndScreenSelection();
            _processingTool = tool;
            SelectWorkspaceView("3D");
            SelectSection("Обработка");
        }, tool => IsImported && CanInteract && !IsWelcomeVisible && !HasEditPreview && tool is "Очистка" or "Совмещение" or "Срез");
        SelectWorkspaceViewCommand = new(SelectWorkspaceView, _ => CanInteract && !HasEditPreview);
        InspectCommand = new(Inspect, _ => CanInteract);
        ShowWelcomeCommand = new(() => { Pause(); ReturnToOverview(); IsSettingsOpen = false; IsWelcomeVisible = true; }, () => CanInteract && !HasEditPreview && !IsScreenSelecting);
        ContinueWorkspaceCommand = new(() => IsWelcomeVisible = false, () => HasWorkspace && CanInteract);
        ToggleSettingsCommand = new(() => IsSettingsOpen = !IsSettingsOpen, () => CanInteract);
        SelectAnalysisToolCommand = new(SelectAnalysisTool, tool => IsImported && CanInteract && !IsWelcomeVisible && IsAnalysisToolAvailable(tool));
        OpenPeopleAnalysisCommand = new(() => SelectAnalysisTool("Люди"), () => IsImported && CanInteract && !IsWelcomeVisible);
        OpenRoomPlanNextStepCommand = new(() =>
        {
            if (IsGuest) RequestAdminAccessCommand.Execute(null);
            else if (HasRoomPlanInputs) SelectSection("Результаты");
            else
            {
                if (IsPlanView) SelectWorkspaceView("3D");
                SelectAnalysisTool("Поверхности");
            }
        }, () => IsRoomMode && CanInteract);
        RequestAdminAccessCommand = new(() => AdministratorAccessRequested?.Invoke(this, EventArgs.Empty), () => IsGuest && CanInteract);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CanExport)) Raise(nameof(CanExportReconstruction));
            if (e.PropertyName is nameof(PointCountText) or nameof(SourceBadge)) Raise(nameof(SceneSummary));
            if (e.PropertyName == nameof(IsGuest)) { Raise(nameof(RoomPlanNextAction)); Raise(nameof(RoomPlanEmptyHint)); }
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
        _workspaceSection = section; IsPresentationMode = false; IsSidebarOpen = true; IsInspectorOpen = false; IsSettingsOpen = false;
        NotifyPresentationState();
    }
    private void SelectAnalysisTool(string tool)
    {
        if (!IsAnalysisToolAvailable(tool)) return;
        _analysisTool = tool;
        if (tool == "Люди") { SelectWorkspaceView("3D"); IsInspectorOpen = false; }
        SelectSection("Анализ");
    }
    private bool IsAnalysisToolAvailable(string? tool) => tool == "Люди" || tool == (IsTunnelMode ? "Тоннель" : "Поверхности");
    private void SelectWorkspaceView(string view)
    {
        if (view is not ("3D" or "План 2D" or "Рядом") || view != "3D" && !IsImported) return;
        ReturnToOverview(); EndInspection(); EndScreenSelection();
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
        IsSidebarOpen = false; IsInspectorOpen = false; _processingTool = "Очистка";
        _analysisTool = _dataset?.Session.Project?.AnalysisMode == ProjectAnalysisMode.Tunnel ? "Тоннель" : "Поверхности";
        IsWelcomeVisible = false; _workspaceSection = "Проект"; _workspaceView = "3D"; _inspectorSubject = "Скан";
        NotifyPresentationState();
    }
    private void NotifyPresentationState()
    {
        if (!IsAnalysisToolAvailable(_analysisTool)) _analysisTool = IsTunnelMode ? "Тоннель" : "Поверхности";
        foreach (var name in new[] { nameof(HasWorkspace), nameof(WorkspaceSection), nameof(IsProjectSection), nameof(IsProcessingSection), nameof(IsAnalysisSection), nameof(IsResultsSection), nameof(IsSurfaceAnalysis), nameof(IsPeopleAnalysis), nameof(IsTunnelAnalysis),
            nameof(WorkspaceView), nameof(Is3DView), nameof(IsPlanView), nameof(IsSplitView), nameof(Show3DWorkspace), nameof(ShowPlanWorkspace),
            nameof(ShowActiveScanPicker), nameof(PlanViewLabel), nameof(TaskPanelTitle), nameof(ProjectPanelLabel), nameof(IsCleaningTool), nameof(IsAlignmentTool), nameof(IsClipTool), nameof(HasCurrentStructureAnalysis), nameof(HasRoomPlanInputs), nameof(RoomPlanNextAction), nameof(RoomPlanEmptyHint),
            nameof(InspectorTitle), nameof(ShowScanInspector), nameof(ShowSurfaceInspector), nameof(ShowRoomInspector), nameof(ShowObjectInspector), nameof(ShowSimulationInspector),
            nameof(CurrentRoomPlan), nameof(SourceBadge), nameof(SurfaceAnalysisMethodText), nameof(AnalysisProgressText), nameof(ReviewProgressText), nameof(NextAnalysisStep) }) Raise(name);
    }
}
