using System.IO;
using System.Windows;
using System.Windows.Controls;
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

public sealed class RoomWorkflowTests
{
    [Fact]
    public Task SwitchingRoomsPreservesOtherPlansAndGuestAccess() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); var project = await RoomPlannerTests.TwoRooms(); var path = Path.Combine(files.Directory, "rooms.subterra");
        await new SessionStorage().SaveAsync(path, project.ToSession(), 1);
        using var vm = Vm(); await vm.OpenAsync(path); Assert.Equal(2, vm.SavedRooms.Count);
        var firstId = vm.SelectedRoomId; var secondId = vm.SavedRooms.First(r => r.Id != firstId).Id;
        var second = vm.SavedRooms.First(r => r.Id == secondId).Plan;
        await vm.InsertRoomVertexCommand.ExecuteAsync(); Assert.Equal(5, vm.RoomVertices.Count);
        vm.SelectedRoomId = secondId; Assert.Equal(4, vm.RoomVertices.Count);
        vm.RoomName = "Лаборатория"; vm.RoomFloor = "Первый этаж"; vm.ApplyRoomIdentityCommand.Execute(null);
        vm.SelectedRoomId = firstId; Assert.Equal(5, vm.RoomVertices.Count); Assert.True(vm.HasRoomEdits);
        vm.ShowAllRooms = true; Assert.NotNull(vm.RoomOutline); Assert.True(vm.RoomOutline!.Positions!.Count > 24);
        Assert.True(await vm.SaveAsync(path));
        using var guest = Vm(new(false)); await guest.OpenAsync(path); guest.SelectedRoomId = secondId;
        Assert.Equal("Лаборатория", guest.RoomName); Assert.Equal("Первый этаж", guest.RoomFloor);
        Assert.False(guest.HasUnsavedChanges); Assert.False(guest.NewRoomCommand.CanExecute(null)); Assert.False(guest.ApplyRoomIdentityCommand.CanExecute(null));
        Assert.False(guest.HasNotification, guest.Notification);
        Assert.Equal(second.FloorContour, guest.SavedRooms.First(r => r.Id == secondId).Plan.FloorContour);
    });

    [Fact]
    public Task ManualRoomEditingSupportsHistoryPersistenceAndExplicitReset() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); using var vm = Vm(); await Prepare(vm); await vm.BuildRoomAsync();
        var area = vm.RoomAreaText; vm.RoomVertexIndex = 0;
        await vm.InsertRoomVertexCommand.ExecuteAsync(); Assert.True(vm.HasRoomEdits); Assert.Equal(5, vm.RoomVertices.Count);
        await vm.BuildRoomAsync(); Assert.True(vm.HasRoomEdits); Assert.Equal(5, vm.RoomVertices.Count); Assert.NotEmpty(vm.RoomBuildMessage);
        vm.RoomOpeningStart = .1; vm.RoomOpeningEnd = .4;
        await vm.AddRoomOpeningCommand.ExecuteAsync(); Assert.Single(vm.RoomOpenings);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.Empty(vm.RoomOpenings);
        await vm.RedoProjectCommand.ExecuteAsync(); Assert.Single(vm.RoomOpenings);
        var path = Path.Combine(files.Directory, "edited.subterra"); Assert.True(await vm.SaveAsync(path));
        using var restored = Vm(); await restored.OpenAsync(path);
        Assert.True(restored.HasRoomEdits); Assert.Equal(5, restored.RoomVertices.Count); Assert.Single(restored.RoomOpenings);
        restored.ResetRoomEditsCommand.Execute(null); Assert.False(restored.HasRoomEdits); Assert.Equal(4, restored.RoomVertices.Count);
        Assert.Equal(area, restored.RoomAreaText); await restored.UndoProjectCommand.ExecuteAsync(); Assert.True(restored.HasRoomEdits);
        Assert.False(restored.HasNotification, restored.Notification);
    });

    private static MainViewModel Vm(TestAccess? access = null, WorkspaceStateStore? store = null) => new(
        new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: access ?? new(), interaction: new TestInteraction(), workspaceStore: store);
    private static async Task Prepare(MainViewModel vm)
    {
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters, LocalOrigin: false)); await vm.AnalyzeStructureAsync();
        foreach (var surface in vm.StructureSurfaces.Where(s => s.Kind != StructureSurfaceKind.Unknown).ToArray())
        { vm.SelectedStructureSurface = vm.StructureSurfaces.Single(s => s.Id == surface.Id); vm.ConfirmStructureSurfaceCommand.Execute(null); }
    }

    [Fact]
    public Task BuildSaveOpenClipReviewAndUndoPreserveTheRoomAndItsSource() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); using var vm = Vm(); await Prepare(vm);
        var path = Path.Combine(files.Directory, "room.subterra"); Assert.True(await vm.SaveAsync(path));
        vm.RoomName = "Аудитория 101"; await vm.BuildRoomCommand.ExecuteAsync();
        Assert.False(vm.HasNotification, vm.Notification); Assert.True(vm.HasRoom); Assert.False(vm.RoomIsStale); Assert.True(vm.RoomVisible);
        Assert.NotNull(vm.RoomOutline); Assert.Equal(4, vm.RoomPlanPoints!.Count); Assert.True(vm.HasUnsavedChanges);
        Assert.True(await vm.SaveAsync(path)); vm.ShowRoom = false; Assert.False(vm.RoomVisible); Assert.False(vm.HasUnsavedChanges); vm.ShowRoom = true;
        vm.ClipOffset = 1.5; vm.ClipEnabled = true; Assert.All(vm.RoomOutline!.Positions!, p => Assert.True(p.Z <= 1.5001f)); vm.ClipEnabled = false;
        vm.SelectedStructureSurface = vm.StructureSurfaces.First(s => s.Kind == StructureSurfaceKind.Wall); vm.RejectStructureSurfaceCommand.Execute(null);
        Assert.True(vm.RoomIsStale); Assert.False(vm.RoomVisible); Assert.Null(vm.RoomOutline);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.False(vm.RoomIsStale); Assert.NotNull(vm.RoomOutline);
        vm.ObjectX = 1; await vm.ApplyObjectCommand.ExecuteAsync(); Assert.True(vm.RoomIsStale);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.False(vm.RoomIsStale);
        using var restored = Vm(); await restored.OpenAsync(path); Assert.True(restored.HasRoom); Assert.False(restored.RoomIsStale); Assert.Equal("Аудитория 101", restored.RoomName);
        Assert.Equal(3747, (await new SessionStorage().LoadAsync(path)).Project!.Active!.Source.Points.Length);
        vm.RemoveRoomCommand.Execute(null); Assert.False(vm.HasRoom); await vm.UndoProjectCommand.ExecuteAsync(); Assert.True(vm.HasRoom);
    });

    [Fact]
    public Task GuestAndRevokedAccessCannotCreateOrDeleteARoom() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); var path = Path.Combine(files.Directory, "room.subterra"); var access = new TestAccess();
        using var admin = Vm(access); await Prepare(admin); await admin.BuildRoomAsync(); Assert.True(await admin.SaveAsync(path));
        using var guest = Vm(new(false)); await guest.OpenAsync(path); Assert.True(guest.HasRoom); Assert.True(guest.RoomVisible);
        Assert.False(guest.BuildRoomCommand.CanExecute(null)); Assert.False(guest.RemoveRoomCommand.CanExecute(null));
        await guest.BuildRoomAsync(); guest.RemoveRoomCommand.Execute(null); Assert.True(guest.HasRoom); Assert.False(guest.HasUnsavedChanges);
        access.Logout(); admin.RemoveRoomCommand.Execute(null); Assert.True(admin.HasRoom); Assert.False(admin.CanBuildRoom);
    });

    [Fact]
    public Task FailedAndCancelledBuildsDoNotInventRoomData() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters)); await vm.AnalyzeStructureAsync();
        await vm.BuildRoomAsync(); Assert.False(vm.HasRoom); Assert.NotEmpty(vm.RoomBuildMessage);
        await Prepare(vm); var pending = vm.BuildRoomAsync(); vm.CancelOperation(); await pending;
        Assert.False(vm.HasRoom); Assert.False(vm.IsBusy); Assert.False(vm.HasNotification, vm.Notification);
    });

    [Fact]
    public Task RecoveryRestoresTheRoomAndPanelBindingsWithoutOpeningAWindow() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); var store = new WorkspaceStateStore(files.Directory);
        using (var vm = Vm(store: store)) { await Prepare(vm); await vm.BuildRoomAsync(); await vm.AutoSaveAsync(); Assert.True(store.HasRecovery); }
        using var restored = Vm(store: new WorkspaceStateStore(files.Directory)); await restored.RestoreRecoveryAsync();
        Assert.True(restored.HasRoom); Assert.False(restored.RoomIsStale); Assert.False(restored.HasNotification, restored.Notification);
        var panel = new RoomPlanPanel { DataContext = restored, Width = 300, Height = 1000, Foreground = Brushes.White };
        panel.Measure(new Size(300, 1000)); panel.Arrange(new Rect(0, 0, 300, 1000)); panel.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.DataBind);
        void Expand(DependencyObject element)
        {
            if (element is Expander expander) expander.IsExpanded = true;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Expand(VisualTreeHelper.GetChild(element, i));
        }
        Expand(panel); panel.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.DataBind);
        void Check(DependencyObject root)
        {
            var values = root.GetLocalValueEnumerator();
            while (values.MoveNext()) { var binding = BindingOperations.GetBindingExpressionBase(root, values.Current.Property); if (binding is not null) Assert.False(binding.HasError, $"{root.GetType().Name}.{values.Current.Property.Name}"); }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Check(VisualTreeHelper.GetChild(root, i));
        }
        Check(panel);
        var folder = Environment.GetEnvironmentVariable("SUBTERRA_UI_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder); var root = new Border { Background = new SolidColorBrush(Color.FromRgb(18, 30, 40)), Child = panel, Padding = new Thickness(16) };
            root.Measure(new Size(332, 1032)); root.Arrange(new Rect(0, 0, 332, 1032)); root.UpdateLayout();
            ThemeRenderingTests.CheckReadableText(root);
            var bitmap = new RenderTargetBitmap(498, 1548, 144, 144, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(folder, "room-plan.png")); encoder.Save(stream);
        }
    });
    private sealed class Files : IDisposable
    {
        public string Directory { get; } = WorkspaceFeatureTests.TemporaryDirectory();
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
