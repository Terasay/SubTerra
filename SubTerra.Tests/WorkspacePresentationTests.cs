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
        vm.Inspect("Комната");
        foreach (var view in new[] { "План 2D", "Рядом", "3D" })
        {
            vm.SelectWorkspaceViewCommand.Execute(view); WorkspaceLayout.Apply(root, vm, width); ThemeRenderingTests.Layout(root, width, height);
            await Dispatcher.Yield(DispatcherPriority.DataBind); root.UpdateLayout(); ThemeRenderingTests.CheckReadableText(root);
            var planColumn = (ColumnDefinition)root.FindName("PlanColumn"); var sceneColumn = (ColumnDefinition)root.FindName("Scene3DColumn");
            if (view == "План 2D") Assert.True(planColumn.ActualWidth > 400 && sceneColumn.ActualWidth == 0);
            if (view == "Рядом") Assert.True(planColumn.ActualWidth > 250 && sceneColumn.ActualWidth > 250);
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

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
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
    private static void SavePreview(FrameworkElement root, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SUBTERRA_UI_PREVIEW_DIR"); if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth * 2, (int)root.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
}
