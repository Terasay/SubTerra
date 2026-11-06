using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SubTerra.Models;
using SubTerra.Services;
using SubTerra.ViewModels;
using SubTerra.Views;
using Xunit;

namespace SubTerra.Tests;

public sealed class WorkspacePresentationTests
{
    private static MainViewModel Vm(bool admin = true) => new(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(),
        access: new TestAccess(admin), interaction: new TestInteraction());

    [Fact]
    public Task WelcomeAndViewNavigationDoNotModifyDataOrGrantEditAccess() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(false);
        Assert.True(vm.IsWelcomeVisible); Assert.False(vm.HasWorkspace);
        Assert.False(vm.ContinueWorkspaceCommand.CanExecute(null));
        var welcome = ThemeRenderingTests.ShellPreview(vm); ThemeRenderingTests.Layout(welcome, 1040, 640);
        await Dispatcher.Yield(DispatcherPriority.DataBind); welcome.UpdateLayout(); ThemeRenderingTests.CheckReadableText(welcome);
        SavePreview(welcome, "welcome.png");
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        Assert.False(vm.IsWelcomeVisible); Assert.True(vm.HasWorkspace); Assert.False(vm.CanManageProject);
        var dirty = vm.HasUnsavedChanges;
        vm.SelectSectionCommand.Execute("Анализ"); vm.Inspect("Поверхность");
        Assert.True(vm.IsAnalysisSection); Assert.True(vm.ShowSurfaceInspector); Assert.False(vm.CanReviewStructure);
        vm.SelectWorkspaceViewCommand.Execute("План 2D"); Assert.True(vm.IsPlanView); Assert.False(vm.Show3DWorkspace);
        var accessRequests = 0;
        vm.AdministratorAccessRequested += (_, _) => accessRequests++;
        vm.OpenRoomPlanNextStepCommand.Execute(null);
        Assert.Equal(1, accessRequests); Assert.True(vm.IsPlanView);
        vm.FlyCommand.Execute(null); Assert.True(vm.Is3DView); Assert.True(vm.IsFlying); vm.ReturnToOverview();
        vm.SelectWorkspaceViewCommand.Execute("План 2D");
        vm.MeasureCommand.Execute(null); Assert.True(vm.Is3DView); Assert.True(vm.IsPicking);
        vm.EndInspection(); vm.SelectWorkspaceViewCommand.Execute("Рядом"); Assert.True(vm.IsSplitView);
        vm.ShowWelcomeCommand.Execute(null); Assert.True(vm.IsWelcomeVisible);
        vm.ContinueWorkspaceCommand.Execute(null); Assert.False(vm.IsWelcomeVisible);
        Assert.Equal(dirty, vm.HasUnsavedChanges); Assert.False(vm.BuildRoomCommand.CanExecute(null));
        vm.SelectSectionCommand.Execute("неизвестный раздел"); Assert.True(vm.IsAnalysisSection);
    });

    [Theory]
    [InlineData(1040, 640)]
    [InlineData(1440, 900)]
    public Task PeopleShortcutOpensVisibleToolsForGuestEvenWithPanelsHidden(int width, int height) => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(false);
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        var dirty = vm.HasUnsavedChanges;
        vm.IsSidebarOpen = false; vm.IsPresentationMode = true;
        vm.OpenPeopleAnalysisCommand.Execute(null);
        Assert.True(vm.IsAnalysisSection); Assert.True(vm.IsPeopleAnalysis);
        Assert.True(vm.IsSidebarOpen); Assert.False(vm.IsPresentationMode);
        var root = ThemeRenderingTests.ShellPreview(vm);
        WorkspaceLayout.Apply(root, vm, width); ThemeRenderingTests.Layout(root, width, height);
        await Dispatcher.Yield(DispatcherPriority.DataBind); root.UpdateLayout();
        var people = Descendants(root).OfType<PeopleAnalysisPanel>().Single();
        Assert.True(IsShown(people)); Assert.True(people.ActualHeight > 0);
        Assert.Contains(Descendants(root).OfType<TextBlock>(), t => IsShown(t) && t.Text == "Гостевой режим");
        var login = Descendants(root).OfType<Button>().Single(b => IsShown(b) && b.Command == vm.RequestAdminAccessCommand);
        Assert.True(login.IsEnabled); Assert.False(vm.CanDetectPeople); Assert.False(vm.CanSelectSurface);
        var requests = 0; vm.AdministratorAccessRequested += (_, _) => requests++;
        login.Command.Execute(null); Assert.Equal(1, requests);
        foreach (var name in new[] { "FileMenuButton", "ExportMenuButton", "ViewMenuButton", "RenderMenuButton", "ToolsMenuButton" })
        {
            var button = (Button)root.FindName(name);
            button.ContextMenu.PlacementTarget = button;
            ThemeRenderingTests.Layout(button.ContextMenu, 380, 480);
            Assert.Same(vm, button.ContextMenu.DataContext);
        }
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        var renderMenu = ((Button)root.FindName("RenderMenuButton")).ContextMenu;
        var surface = renderMenu.Items.OfType<MenuItem>().Single(b => b.Command == vm.SurfaceCommand);
        Assert.True(surface.IsEnabled);
        await vm.SurfaceCommand.ExecuteAsync(); await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.Equal(2, requests); Assert.True(vm.IsPointMode);
        CheckBindings(root); ThemeRenderingTests.CheckReadableText(root); SavePreview(root, $"people-guest-{width}.png");
        vm.SelectAnalysisToolCommand.Execute("Поверхности"); Assert.True(vm.IsSurfaceAnalysis);
        vm.IsPresentationMode = true; vm.SelectSectionCommand.Execute("Анализ");
        Assert.False(vm.IsPresentationMode); Assert.True(vm.IsSidebarOpen);
        Assert.Equal(dirty, vm.HasUnsavedChanges); Assert.False(vm.HasStructureAnalysis);
    });

    [Theory]
    [InlineData(1040, 640)]
    [InlineData(1440, 900)]
    public Task ProjectsOpenWithAnUnobstructedCanvasAndToolsOpenOnlyTheirOwnTask(int width, int height) => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm();
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        var dirty = vm.HasUnsavedChanges;
        var root = ThemeRenderingTests.ShellPreview(vm);
        async Task Layout()
        {
            WorkspaceLayout.Apply(root, vm, width); ThemeRenderingTests.Layout(root, width, height);
            await Dispatcher.Yield(DispatcherPriority.DataBind); root.UpdateLayout();
        }
        await Layout();
        Assert.False(vm.IsSidebarOpen); Assert.False(vm.IsInspectorOpen);
        Assert.False(IsShown((Border)root.FindName("LeftPanel")));
        Assert.False(IsShown((Border)root.FindName("RightPanel")));
        Assert.True(((Border)root.FindName("Scene3DHost")).ActualWidth >= width - 28);
        Assert.DoesNotContain(Descendants(root).OfType<RadioButton>(), b => b.GroupName is "Workflow" or "AnalysisTool");
        SavePreview(root, $"focused-workspace-{width}.png");
        var objectsPosition = ((Button)root.FindName("ObjectsButton")).TranslatePoint(new Point(), root);
        ((Button)root.FindName("ObjectsButton")).Command.Execute(null);
        await Layout();
        Assert.Equal(objectsPosition, ((Button)root.FindName("ObjectsButton")).TranslatePoint(new Point(), root));
        Assert.True(vm.IsProjectSection && vm.IsSidebarOpen); Assert.False(vm.IsInspectorOpen);
        ((Button)root.FindName("ObjectsButton")).Command.Execute(null);
        await Layout(); Assert.False(vm.IsSidebarOpen);
        var tools = ((Button)root.FindName("ToolsMenuButton")).ContextMenu;
        tools.PlacementTarget = (Button)root.FindName("ToolsMenuButton");
        ThemeRenderingTests.Layout(tools, 340, 480);
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        foreach (var task in new[] { "Совмещение", "Очистка", "Срез" })
        {
            vm.Inspect("Поверхность");
            vm.SelectWorkspaceViewCommand.Execute("План 2D");
            var action = tools.Items.OfType<MenuItem>().Single(item => Equals(item.CommandParameter, task));
            Assert.True(action.IsEnabled); action.Command.Execute(action.CommandParameter);
            await Layout();
            Assert.True(vm.IsSidebarOpen && vm.IsProcessingSection && vm.Is3DView);
            var panel = Descendants(root).OfType<ScanProjectPanel>().Single();
            var buttons = Descendants(panel).OfType<Button>().Where(IsShown).ToArray();
            Assert.Equal(task == "Совмещение", buttons.Any(b => b.Command == vm.PreviewAlignmentCommand));
            Assert.Equal(task == "Очистка", buttons.Any(b => b.Command == vm.RectangleSelectionCommand));
            Assert.Equal(task == "Срез", buttons.Any(b => b.Command == vm.PointsCommand));
            Assert.False(vm.IsInspectorOpen);
            CheckBindings(root); ThemeRenderingTests.CheckReadableText(root);
            if (task == "Очистка") SavePreview(root, $"focused-cleaning-{width}.png");
        }
        Assert.Equal(dirty, vm.HasUnsavedChanges);
        vm.SelectAnalysisToolCommand.Execute("Поверхности");
        await Layout(); Assert.Equal("Поверхности помещения", vm.TaskPanelTitle);
        vm.Inspect("Поверхность"); await Layout();
        Assert.True(((Border)root.FindName("Scene3DHost")).ActualWidth >= 440);
        await vm.LoadTunnelDemoAsync(); await Layout();
        Assert.False(vm.IsSidebarOpen); Assert.False(vm.IsInspectorOpen);
        Assert.True(vm.IsTunnelAnalysis); Assert.Equal("Сечение", vm.PlanViewLabel);
        vm.SelectWorkspaceViewCommand.Execute("План 2D"); await Layout();
        SavePreview(root, $"focused-section-{width}.png");
    });

    [Theory]
    [InlineData(1040, 640)]
    [InlineData(1440, 900)]
    [InlineData(1920, 1080)]
    public Task AllSectionsAndPlanViewsRemainUsableAtSupportedWindowSizes(int width, int height) => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm();
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters, LocalOrigin: false)); await vm.AnalyzeStructureAsync();
        foreach (var surface in vm.StructureSurfaces.Where(s => s.Kind != StructureSurfaceKind.Unknown).ToArray())
        { vm.SelectedStructureSurface = vm.StructureSurfaces.Single(s => s.Id == surface.Id); vm.ConfirmStructureSurfaceCommand.Execute(null); }
        await vm.BuildRoomAsync(); Assert.NotNull(vm.CurrentRoomPlan);
        var root = ThemeRenderingTests.ShellPreview(vm);
        foreach (var section in new[] { "Проект", "Обработка", "Анализ", "Результаты" })
        {
            vm.SelectSectionCommand.Execute(section); vm.Inspect(section == "Анализ" ? "Поверхность" : "Скан");
            WorkspaceLayout.Apply(root, vm, width); ThemeRenderingTests.Layout(root, width, height);
            await Dispatcher.Yield(DispatcherPriority.DataBind); root.UpdateLayout(); ThemeRenderingTests.CheckReadableText(root);
            var scene = (Border)root.FindName("Scene3DHost");
            Assert.True(scene.ActualHeight >= height - 220, "The viewport must retain most of the window height.");
            var left = (Border)root.FindName("LeftPanel"); var right = (Border)root.FindName("RightPanel");
            Assert.True(left.ActualWidth >= 270); Assert.True(right.ActualWidth >= 298);
        }
        vm.OpenPeopleAnalysisCommand.Execute(null);
        WorkspaceLayout.Apply(root, vm, width); ThemeRenderingTests.Layout(root, width, height);
        await Dispatcher.Yield(DispatcherPriority.DataBind); root.UpdateLayout();
        Assert.True(IsShown(Descendants(root).OfType<PeopleAnalysisPanel>().Single()));
        CheckBindings(root); ThemeRenderingTests.CheckReadableText(root); SavePreview(root, $"people-admin-{width}.png");
        vm.Inspect("Комната");
        foreach (var view in new[] { "План 2D", "Рядом", "3D" })
        {
            vm.SelectWorkspaceViewCommand.Execute(view); WorkspaceLayout.Apply(root, vm, width); ThemeRenderingTests.Layout(root, width, height);
            await Dispatcher.Yield(DispatcherPriority.DataBind); root.UpdateLayout(); ThemeRenderingTests.CheckReadableText(root);
            var planColumn = (ColumnDefinition)root.FindName("PlanColumn"); var sceneColumn = (ColumnDefinition)root.FindName("Scene3DColumn");
            if (view == "План 2D") Assert.True(planColumn.ActualWidth >= 380 && sceneColumn.ActualWidth == 0);
            if (view == "Рядом")
            {
                Assert.True(vm.IsSidebarOpen && vm.IsInspectorOpen);
                Assert.True(planColumn.ActualWidth >= 320 && sceneColumn.ActualWidth >= 320);
                var rightColumn = (ColumnDefinition)root.FindName("RightColumn");
                Assert.Equal(width == 1040 ? 0 : 312, rightColumn.ActualWidth);
            }
            if (view == "План 2D") SavePreview(root, $"workspace-plan-{width}.png");
        }
        vm.SelectedStructureSurface = vm.StructureSurfaces.First(s => s.Kind == StructureSurfaceKind.Wall);
        vm.RejectStructureSurfaceCommand.Execute(null); Assert.True(vm.RoomIsStale); Assert.Null(vm.CurrentRoomPlan);
    });

    [Fact]
    public Task LargePlanSelectionAndNumericEditorShareTheSavedRoomAndItsHistory() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm();
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters, LocalOrigin: false)); await vm.AnalyzeStructureAsync();
        foreach (var surface in vm.StructureSurfaces.Where(s => s.Kind != StructureSurfaceKind.Unknown).ToArray())
        { vm.SelectedStructureSurface = vm.StructureSurfaces.Single(s => s.Id == surface.Id); vm.ConfirmStructureSurfaceCommand.Execute(null); }
        await vm.BuildRoomAsync(); vm.SelectWorkspaceViewCommand.Execute("План 2D"); vm.Inspect("Комната"); vm.IsRoomEditing = true;
        var plan = new PlanWorkspace { DataContext = vm }; ThemeRenderingTests.Layout(plan, 720, 600);
        var inspector = new ContextInspectorPanel { DataContext = vm }; ThemeRenderingTests.Layout(inspector, 312, 1200);
        await Dispatcher.Yield(DispatcherPriority.DataBind); inspector.UpdateLayout();
        var canvas = (RoomPlanCanvas)plan.FindName("PlanCanvas");
        canvas.SelectedVertexIndex = 1; await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.Equal(1, vm.RoomVertexIndex);
        var picker = Descendants(inspector).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Вершина комнаты");
        Assert.Equal(1, picker.SelectedIndex);
        Assert.True(vm.CanEditRoom, $"Room editing disabled: {vm.RoomSummary}; busy={vm.IsBusy}; preview={vm.HasEditPreview}; overview={vm.IsOverview}");
        await vm.InsertRoomVertexCommand.ExecuteAsync(); await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.True(vm.HasRoomEdits, vm.Notification + " " + vm.RoomBuildMessage); Assert.Equal(5, canvas.Plan!.FloorContour.Length);
        vm.RoomOpeningStart = .1; vm.RoomOpeningEnd = .4; await vm.AddRoomOpeningCommand.ExecuteAsync();
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.True(Assert.Single(canvas.Plan!.Gaps).IsConfirmedOpening);
        await vm.UndoProjectCommand.ExecuteAsync(); await Dispatcher.Yield(DispatcherPriority.DataBind); Assert.Empty(canvas.Plan!.Gaps);
        await vm.RedoProjectCommand.ExecuteAsync(); await Dispatcher.Yield(DispatcherPriority.DataBind); Assert.Single(canvas.Plan!.Gaps);
        Assert.True(vm.ShowRoomInspector); Assert.True(vm.IsRoomEditing);
        Assert.Equal(1, vm.RoomVertexIndex); Assert.Equal(1, picker.SelectedIndex); Assert.NotNull(picker.SelectedItem);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        CheckBindings(plan); CheckBindings(inspector); ThemeRenderingTests.CheckReadableText(inspector);
        ThemeRenderingTests.Layout(inspector, 312, 1200); SavePreview(inspector, "room-editor.png");
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "merged-room.subterra"); Assert.True(await vm.SaveAsync(path));
            using var guest = Vm(false); await guest.OpenAsync(path); guest.Inspect("Комната"); guest.IsRoomEditing = true;
            Assert.True(guest.HasRoomEdits); Assert.Single(guest.SavedRooms); Assert.True(Assert.Single(guest.RoomOpenings).IsConfirmedOpening);
            inspector.DataContext = guest; await Dispatcher.Yield(DispatcherPriority.DataBind); inspector.UpdateLayout(); CheckBindings(inspector);
            Assert.False(picker.IsEnabled); Assert.False(guest.ApplyRoomVertexCommand.CanExecute(null));
        }
        finally { Directory.Delete(directory, true); }
    });

    [Fact]
    public Task NeuralAndObjectToolsResolveBindingsAfterTheMerge() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        await vm.AnalyzeIndoorCommand.ExecuteAsync(); Assert.True(vm.HasIndoorPreview); Assert.Contains("MLP", vm.SurfaceAnalysisMethodText);
        vm.SelectSectionCommand.Execute("Анализ"); await vm.AnalyzeObjectsAsync(); Assert.NotEmpty(vm.ObjectCandidates);
        vm.Inspect("Предмет"); vm.ObjectCandidateLabel = "Проверенная группа"; vm.ConfirmObjectCandidateCommand.Execute(null);
        var inspector = new ContextInspectorPanel { DataContext = vm }; ThemeRenderingTests.Layout(inspector, 312, 1100);
        await Dispatcher.Yield(DispatcherPriority.DataBind); inspector.UpdateLayout();
        Assert.True(vm.ShowObjectInspector); Assert.Equal("Проверенная группа", vm.SelectedObjectCandidate!.Label);
        CheckBindings(inspector); ThemeRenderingTests.CheckReadableText(inspector); SavePreview(inspector, "object-inspector.png");
        await vm.PrepareIndoorPreviewAsync();
        var settings = new ViewSettingsPanel { DataContext = vm }; ThemeRenderingTests.Layout(settings, 320, 1200);
        await Dispatcher.Yield(DispatcherPriority.DataBind); settings.UpdateLayout();
        Descendants(settings).OfType<Expander>().Single(e => Equals(e.Header, "Поверхности скана")).IsExpanded = true;
        settings.UpdateLayout();
        var style = Descendants(settings).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Оформление предварительных поверхностей");
        style.SelectedIndex = 1; await Dispatcher.Yield(DispatcherPriority.DataBind); Assert.Equal(vm.IndoorStyles[1], vm.IndoorStyle);
        CheckBindings(settings); Assert.False(vm.HasNotification, vm.Notification);
        foreach (var expander in Descendants(settings).OfType<Expander>().ToArray()) expander.IsExpanded = true;
        await vm.ReconstructRoomAsync(); ThemeRenderingTests.Layout(settings, 320, 1900);
        await Dispatcher.Yield(DispatcherPriority.DataBind); settings.UpdateLayout();
        Assert.True(vm.HasReconstruction); Assert.False(vm.HasNotification, vm.Notification);
        CheckBindings(settings); ThemeRenderingTests.CheckReadableText(settings); SavePreview(settings, "reconstruction-settings.png");
        await vm.DetectPeopleAsync(); Assert.True(vm.HasPeopleDetection); Assert.False(vm.HasNotification, vm.Notification);
        var people = new PeopleAnalysisPanel { DataContext = vm }; ThemeRenderingTests.Layout(people, 320, 1700);
        await Dispatcher.Yield(DispatcherPriority.DataBind); people.UpdateLayout();
        CheckBindings(people); ThemeRenderingTests.CheckReadableText(people); SavePreview(people, "people-analysis.png");
    });

    [Fact]
    public Task TunnelDemoSupportsModeHistoryAndReadOnlySavedGuestViewing() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await vm.LoadTunnelDemoAsync();
        Assert.False(vm.HasNotification, vm.Notification);
        Assert.True(vm.IsTunnelMode); Assert.True(vm.IsGeometryShading); Assert.True(vm.HasTunnelSections);
        Assert.False(vm.ReconstructRoomCommand.CanExecute(null));
        var axis = vm.TunnelAxisText; var count = vm.TunnelSections.Count;
        vm.SelectedProjectMode = vm.ProjectModes.Single(m => m.Mode == ProjectAnalysisMode.Room);
        Assert.False(vm.IsTunnelMode); Assert.False(vm.HasTunnelSections);
        Assert.True(vm.IsSurfaceAnalysis); Assert.Equal("План", vm.PlanViewLabel);
        Assert.False(vm.SelectAnalysisToolCommand.CanExecute("Тоннель"));
        await vm.UndoProjectCommand.ExecuteAsync();
        Assert.True(vm.IsTunnelMode); Assert.True(vm.IsGeometryShading); Assert.True(vm.HasTunnelSections);
        Assert.True(vm.IsTunnelAnalysis); Assert.Equal("Сечение", vm.PlanViewLabel);
        Assert.False(vm.SelectAnalysisToolCommand.CanExecute("Поверхности"));
        Assert.Equal(axis, vm.TunnelAxisText); Assert.Equal(count, vm.TunnelSections.Count);
        await vm.RedoProjectCommand.ExecuteAsync(); Assert.False(vm.IsTunnelMode);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.True(vm.IsTunnelMode);
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "tunnel.subterra"); Assert.True(await vm.SaveAsync(path));
            var saved = await new SessionStorage().LoadAsync(path);
            Assert.Equal(ProjectAnalysisMode.Tunnel, saved.Project!.AnalysisMode);
            Assert.All(saved.Project.Objects, obj => { Assert.Empty(obj.Source.Colors); Assert.True(obj.Source.HasMesh); });
            using var guest = Vm(false); await guest.OpenAsync(path);
            Assert.True(guest.IsTunnelMode); Assert.True(guest.HasTunnelSections); Assert.True(guest.IsGeometryShading);
            Assert.Equal(axis, guest.TunnelAxisText); Assert.Equal(count, guest.TunnelSections.Count);
            Assert.False(guest.CanEditTunnel); Assert.False(guest.ApplyTunnelAxisCommand.CanExecute(null));
            Assert.False(guest.AnalyzeTunnelCommand.CanExecute(null));
            var dirty = guest.HasUnsavedChanges;
            guest.SelectedProjectMode = guest.ProjectModes.Single(m => m.Mode == ProjectAnalysisMode.Room);
            guest.TunnelAxisText = "0 0 0\n1 0 0";
            Assert.True(guest.IsTunnelMode); Assert.Equal(axis, guest.TunnelAxisText);
            guest.SelectedTunnelSection = guest.TunnelSections.Last();
            Assert.Same(guest.TunnelSections.Last().Section, guest.CurrentTunnelSection);
            Assert.True(guest.FocusTunnelSectionCommand.CanExecute(null));
            guest.SelectAnalysisToolCommand.Execute("Тоннель");
            guest.SelectWorkspaceViewCommand.Execute("План 2D");
            var root = ThemeRenderingTests.ShellPreview(guest);
            guest.IsInspectorOpen = false;
            WorkspaceLayout.Apply(root, guest, 1440); ThemeRenderingTests.Layout(root, 1440, 900);
            await Dispatcher.Yield(DispatcherPriority.DataBind); root.UpdateLayout();
            ThemeRenderingTests.CheckReadableText(root); SavePreview(root, "tunnel-section.png");
            guest.FocusTunnelSectionCommand.Execute(null);
            Assert.True(guest.Is3DView);
            guest.IsGeometryShading = false; Assert.False(guest.IsGeometryShading);
            Assert.Equal(dirty, guest.HasUnsavedChanges); Assert.False(guest.HasNotification, guest.Notification);
        }
        finally { Directory.Delete(directory, true); }
    });

    [Fact]
    public Task InvalidTunnelDraftPreservesLastResultsAndHistoryUntilValidChangesAreApplied() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await vm.LoadTunnelDemoAsync();
        Assert.True(vm.HasTunnelSections, vm.Notification);
        var axis = vm.TunnelAxisText; var section = vm.CurrentTunnelSection; var count = vm.TunnelSections.Count;
        var undo = vm.UndoProjectText;
        vm.TunnelAxisText = "0 0\n2 0 0"; Assert.True(vm.TunnelDraftPending);
        vm.ApplyTunnelAxisCommand.Execute(null);
        Assert.True(vm.HasNotification); Assert.True(vm.HasTunnelSections); Assert.Same(section, vm.CurrentTunnelSection);
        Assert.Equal(count, vm.TunnelSections.Count); Assert.Equal(undo, vm.UndoProjectText);
        await vm.AnalyzeTunnelAsync();
        Assert.True(vm.HasTunnelSections); Assert.Same(section, vm.CurrentTunnelSection); Assert.Equal(undo, vm.UndoProjectText);
        vm.ResetTunnelDraftCommand.Execute(null);
        Assert.Equal(axis, vm.TunnelAxisText); Assert.False(vm.TunnelDraftPending);
        vm.TunnelSectionSpacing = 0; vm.ApplyTunnelAxisCommand.Execute(null);
        Assert.True(vm.HasTunnelSections); Assert.Same(section, vm.CurrentTunnelSection); Assert.Equal(undo, vm.UndoProjectText);
        vm.ResetTunnelDraftCommand.Execute(null);
        vm.TunnelAxisText = "0 0 2\n3 0 2"; vm.ApplyTunnelAxisCommand.Execute(null);
        Assert.False(vm.HasTunnelSections); Assert.Empty(vm.TunnelSections);
        Assert.False(vm.TunnelDraftPending); Assert.NotEqual(undo, vm.UndoProjectText);
        await vm.UndoProjectCommand.ExecuteAsync();
        Assert.True(vm.HasTunnelSections); Assert.Equal(axis, vm.TunnelAxisText); Assert.Equal(count, vm.TunnelSections.Count);
    });

    [Fact]
    public Task TransformingTunnelScanHidesStaleMeasurementsAndUndoRestoresThem() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await vm.LoadTunnelDemoAsync();
        Assert.True(vm.HasTunnelSections, vm.Notification);
        var count = vm.TunnelSections.Count;
        vm.ObjectX += .4; await vm.ApplyObjectCommand.ExecuteAsync();
        Assert.False(vm.HasNotification, vm.Notification);
        Assert.True(vm.TunnelIsStale); Assert.False(vm.HasTunnelSections); Assert.Null(vm.CurrentTunnelSection);
        Assert.Empty(vm.TunnelSectionGeometry!.Positions!);
        await vm.UndoProjectCommand.ExecuteAsync();
        Assert.False(vm.TunnelIsStale); Assert.True(vm.HasTunnelSections); Assert.NotNull(vm.CurrentTunnelSection);
        Assert.Equal(count, vm.TunnelSections.Count); Assert.NotEmpty(vm.TunnelSectionGeometry!.Positions!);
    });

    [Fact]
    public Task SwitchingReviewedRoomToTunnelHidesRoomGeometryWithoutDeletingSavedRoom() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters, LocalOrigin: false));
        await vm.AnalyzeStructureAsync();
        foreach (var surface in vm.StructureSurfaces.Where(s => s.Kind != StructureSurfaceKind.Unknown).ToArray())
        { vm.SelectedStructureSurface = vm.StructureSurfaces.Single(s => s.Id == surface.Id); vm.ConfirmStructureSurfaceCommand.Execute(null); }
        await vm.BuildRoomAsync(); Assert.True(vm.HasRoom); Assert.True(vm.RoomVisible);
        vm.IsRoomEditing = true;
        vm.SelectedProjectMode = vm.ProjectModes.Single(m => m.Mode == ProjectAnalysisMode.Tunnel);
        Assert.True(vm.IsTunnelMode); Assert.True(vm.HasRoom);
        Assert.False(vm.RoomVisible); Assert.False(vm.RoomPlanVisible); Assert.Null(vm.CurrentRoomPlan);
        Assert.False(vm.CanBuildRoom); Assert.False(vm.CanEditRoom);
        Assert.False(vm.AnalyzeStructureCommand.CanExecute(null)); Assert.False(vm.ReconstructRoomCommand.CanExecute(null));
        await vm.UndoProjectCommand.ExecuteAsync();
        Assert.False(vm.IsTunnelMode); Assert.True(vm.HasRoom); Assert.True(vm.RoomVisible); Assert.NotNull(vm.CurrentRoomPlan);
    });

    [Fact]
    public Task TunnelFlightNotifiesLampAndEditorBindingsAndRestoresEditingOnReturn() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await vm.LoadTunnelDemoAsync();
        Assert.True(vm.IsGeometryShading); Assert.True(vm.CanEditTunnel); Assert.False(vm.ReliefLampVisible);
        var lampStates = new List<bool>(); var editStates = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ReliefLampVisible)) lampStates.Add(vm.ReliefLampVisible);
            if (e.PropertyName == nameof(MainViewModel.CanEditTunnel)) editStates.Add(vm.CanEditTunnel);
        };
        vm.FlyCommand.Execute(null);
        Assert.True(vm.IsFlying); Assert.True(vm.ReliefLampVisible); Assert.False(vm.CanEditTunnel);
        Assert.Contains(true, lampStates); Assert.Contains(false, editStates);
        Assert.True(vm.AppendTunnelCameraCommand.CanExecute(null)); Assert.False(vm.ApplyTunnelAxisCommand.CanExecute(null));
        vm.ReturnToOverview();
        Assert.False(vm.IsFlying); Assert.False(vm.ReliefLampVisible); Assert.True(vm.CanEditTunnel);
        Assert.False(lampStates.Last()); Assert.True(editStates.Last());
        Assert.True(vm.ApplyTunnelAxisCommand.CanExecute(null)); Assert.False(vm.HasNotification, vm.Notification);
    });

    [Fact]
    public Task LoadingSimulationAfterTunnelRemovesAxisProfilesAndTunnelAnalysisSelection() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await vm.LoadTunnelDemoAsync();
        Assert.True(vm.IsTunnelAnalysis); Assert.NotEmpty(vm.TunnelAxisGeometry!.Positions!);
        Assert.NotEmpty(vm.TunnelSectionGeometry!.Positions!);
        await vm.LoadDemoAsync(ScenarioDefinition.All.Single(s => s.Id == "mine"));
        Assert.False(vm.HasNotification, vm.Notification); Assert.True(vm.IsSimulation);
        Assert.False(vm.IsTunnelMode); Assert.False(vm.IsGeometryShading); Assert.False(vm.HasTunnelSections);
        Assert.Empty(vm.TunnelSections); Assert.Null(vm.CurrentTunnelSection); Assert.Empty(vm.TunnelAxisText);
        Assert.Empty(vm.TunnelAxisGeometry!.Positions!); Assert.Empty(vm.TunnelAxisNodes!.Positions!);
        Assert.Empty(vm.TunnelSectionGeometry!.Positions!);
        Assert.True(vm.IsSurfaceAnalysis); Assert.False(vm.IsTunnelAnalysis);
    });

    [Fact]
    public Task CancellingTunnelCleanupRestoresOriginalAxisAndMeasuredSectionWithoutHistoryChanges() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await vm.LoadTunnelDemoAsync();
        var axis = vm.TunnelAxisGeometry!.Positions!.ToArray();
        var section = vm.CurrentTunnelSection; var count = vm.TunnelSections.Count; var undo = vm.UndoProjectText;
        vm.CleanMaxX = (vm.CleanMinX + vm.CleanMaxX) / 2;
        await vm.PreviewBoxAsync(true);
        Assert.False(vm.HasNotification, vm.Notification); Assert.True(vm.HasEditPreview);
        Assert.False(vm.HasTunnelSections); Assert.Null(vm.CurrentTunnelSection);
        Assert.Empty(vm.TunnelAxisGeometry!.Positions!); Assert.Empty(vm.TunnelSectionGeometry!.Positions!);
        vm.CancelCleanPreview();
        Assert.False(vm.HasEditPreview); Assert.False(vm.TunnelIsStale); Assert.True(vm.HasTunnelSections);
        Assert.Same(section, vm.CurrentTunnelSection); Assert.Equal(count, vm.TunnelSections.Count);
        Assert.Equal(axis, vm.TunnelAxisGeometry!.Positions!); Assert.NotEmpty(vm.TunnelSectionGeometry!.Positions!);
        Assert.Equal(undo, vm.UndoProjectText);
    });

    [Theory]
    [InlineData(268)]
    [InlineData(312)]
    public Task TunnelToolsResolveBindingsAndShowSelectedMeasuredProfileInNarrowPanels(int width) => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await vm.LoadTunnelDemoAsync();
        Assert.True(vm.HasTunnelSections, vm.Notification);
        var panel = new TunnelAnalysisPanel { DataContext = vm }; ThemeRenderingTests.Layout(panel, width, 1800);
        await Dispatcher.Yield(DispatcherPriority.DataBind); panel.UpdateLayout();
        CheckBindings(panel); ThemeRenderingTests.CheckReadableText(panel);
        var axisEditor = Descendants(panel).OfType<Expander>().Single(e => Equals(e.Header, "Ось и ветви"));
        Assert.False(axisEditor.IsExpanded);
        axisEditor.IsExpanded = true;
        panel.UpdateLayout();
        var axis = Descendants(panel).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == "Точки оси тоннеля");
        Assert.True(axis.IsEnabled); Assert.True(axis.ActualWidth > 200 && axis.ActualWidth <= width);
        var canvas = Descendants(panel).OfType<TunnelSectionCanvas>().Single();
        Assert.Same(vm.CurrentTunnelSection, canvas.Section);
        vm.SelectedTunnelSection = vm.TunnelSections.Last();
        await Dispatcher.Yield(DispatcherPriority.DataBind); panel.UpdateLayout();
        Assert.Same(vm.CurrentTunnelSection, canvas.Section);
        Assert.True(canvas.ActualWidth > 200 && canvas.ActualWidth <= width);
        Assert.True(canvas.ActualHeight >= 200);
        SavePreview(panel, $"tunnel-panel-{width}.png");
    });

    private static bool IsShown(DependencyObject element)
    {
        // IsVisible also requires a native window, whereas these tests render offscreen.
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void CheckBindings(DependencyObject root)
    {
        foreach (var item in Descendants(root))
        {
            var values = item.GetLocalValueEnumerator();
            while (values.MoveNext())
            {
                var binding = BindingOperations.GetBindingExpressionBase(item, values.Current.Property);
                if (binding is not null) Assert.False(binding.HasError, $"{item.GetType().Name}.{values.Current.Property.Name}");
            }
        }
    }
    internal static void SavePreview(FrameworkElement root, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SUBTERRA_UI_PREVIEW_DIR"); if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth * 2, (int)root.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
}
