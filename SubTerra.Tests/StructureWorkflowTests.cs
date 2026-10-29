using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SubTerra.Models;
using SubTerra.Services;
using SubTerra.ViewModels;
using SubTerra.Views;
using Xunit;

namespace SubTerra.Tests;

public sealed class StructureWorkflowTests
{
    [Fact]
    public Task NeuralSurfacePreviewCanBeViewedByGuestsAndWalkedWithoutChangingSources() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); using var vm = Vm(); await Import(vm);
        await vm.AnalyzeIndoorCommand.ExecuteAsync();
        Assert.False(vm.HasNotification, vm.Notification); Assert.True(vm.HasIndoorPreview);
        Assert.True(vm.CanWalk); Assert.True(vm.CanFly); Assert.NotNull(vm.Surface);
        Assert.True(vm.UseNeuralClasses); Assert.True(vm.IsIndoorSurfaceView);
        var materials = vm.Surface;
        vm.IndoorStyle = vm.IndoorStyles[1]; Assert.NotSame(materials, vm.Surface);
        vm.InsideCommand.Execute(null); Assert.True(vm.IsWalking);
        var nav = vm.CreateIndoorNavigator(); vm.UpdateWalkPosition(nav); vm.ReturnToOverview();
        Assert.InRange((vm.LampCamera.Position - vm.Camera.Position).Length, .01, 1);
        var path = Path.Combine(files.Directory, "neural-room.subterra"); Assert.True(await vm.SaveAsync(path));
        var saved = await new SessionStorage().LoadAsync(path);
        Assert.Equal(3747, saved.PointCount); Assert.False(saved.Project!.Active!.Source.HasMesh);
        Assert.Equal(StructureAnalysisResult.NeuralAlgorithm, saved.Project.Structure!.Algorithm);
        using var guest = Vm(new(false)); await guest.OpenAsync(path);
        Assert.True(guest.HasIndoorPreview); Assert.True(guest.CanPrepareIndoorPreview);
        Assert.False(guest.HasUnsavedChanges); Assert.False(guest.AnalyzeIndoorCommand.CanExecute(null));
        guest.FlyCommand.Execute(null); Assert.True(guest.IsFlying); guest.ReturnToOverview();
        vm.SelectedStructureSurface = vm.StructureSurfaces.First(); vm.ConfirmStructureSurfaceCommand.Execute(null);
        Assert.False(vm.HasIndoorPreview); Assert.False(vm.CanWalk);
        Assert.False(vm.HasNotification, vm.Notification);
    });

    [Fact]
    public Task CollegeStoreysCanBeIsolatedAnalyzedSavedAndRestored() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); using var vm = Vm();
        await vm.LoadCollegeBuildingAsync();
        Assert.False(vm.HasNotification, vm.Notification);
        Assert.True(vm.HasSyntheticScans); Assert.Equal(4, vm.ScanObjects.Count);
        Assert.False(vm.HasStructureAnalysis); Assert.Contains("СИНТЕТИЧЕСКИЕ", vm.SelectedScenario.Subtitle);
        vm.SelectedScanObject = vm.ScanObjects[1];
        var originalPoints = vm.ScanObjects.Sum(o => o.PointCount);
        await vm.IsolateObjectAsync();
        Assert.Single(vm.ScanObjects, o => o.IsVisible);
        Assert.True(vm.ScanObjects[1].IsVisible);
        Assert.Equal(originalPoints, vm.ScanObjects.Sum(o => o.PointCount));
        await vm.AnalyzeStructureAsync();
        Assert.False(vm.HasNotification, vm.Notification);
        Assert.InRange(Assert.Single(vm.StructureSurfaces, s => s.Kind == StructureSurfaceKind.Floor).Center.Z, 3.59f, 3.61f);
        Assert.InRange(Assert.Single(vm.StructureSurfaces, s => s.Kind == StructureSurfaceKind.Ceiling).Center.Z, 6.79f, 6.81f);
        Assert.True(vm.StructureSurfaces.Count(s => s.Kind == StructureSurfaceKind.Wall) >= 6);
        Assert.All(vm.StructureSurfaces, s => Assert.Equal(StructureReviewState.Proposed, s.Review));
        await vm.ShowAllObjectsAsync(); Assert.True(vm.StructureIsStale);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.False(vm.StructureIsStale);
        var path = Path.Combine(files.Directory, "college.subterra"); Assert.True(await vm.SaveAsync(path));
        using var guest = Vm(new(false)); await guest.OpenAsync(path);
        Assert.True(guest.HasSyntheticScans); Assert.True(guest.HasStructureAnalysis);
        Assert.False(guest.IsolateObjectCommand.CanExecute(null)); Assert.False(guest.ShowAllObjectsCommand.CanExecute(null));
        await guest.ShowAllObjectsAsync(); await guest.IsolateObjectAsync();
        Assert.Single(guest.ScanObjects, o => o.IsVisible); Assert.False(guest.HasUnsavedChanges);
    });

    [Fact]
    public Task CancellingCollegeCreationKeepsThePreviousProject() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await Import(vm);
        var before = vm.ScanObjects.ToArray();
        var pending = vm.LoadCollegeBuildingAsync(); vm.CancelOperation(); await pending;
        Assert.Equal(before, vm.ScanObjects.ToArray()); Assert.False(vm.HasSyntheticScans);
        Assert.False(vm.IsBusy); Assert.False(vm.HasNotification, vm.Notification);
    });

    [Fact]
    public Task CancelledGroupingAndGuestCommandsCannotReplaceAnAnalysis() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); using var vm = Vm(); await Import(vm); await vm.AnalyzeStructureAsync();
        var geometry = vm.StructureGeometry;
        var pending = vm.AnalyzeObjectsAsync(); vm.CancelOperation(); await pending;
        Assert.False(vm.HasObjectCandidates); Assert.Same(geometry, vm.StructureGeometry); Assert.False(vm.IsBusy);
        await vm.AnalyzeObjectsAsync(); Assert.True(vm.HasObjectCandidates);
        var path = Path.Combine(files.Directory, "groups.subterra"); Assert.True(await vm.SaveAsync(path));
        using var guest = Vm(new(false)); await guest.OpenAsync(path);
        var before = guest.ObjectCandidates.ToArray(); Assert.NotEmpty(before);
        foreach (var command in new[] { guest.ConfirmObjectCandidateCommand, guest.RejectObjectCandidateCommand,
            guest.SplitObjectCandidateCommand, guest.MergeObjectCandidatesCommand })
        { Assert.False(command.CanExecute(null)); command.Execute(null); }
        await guest.AnalyzeObjectsAsync(); Assert.Equal(before, guest.ObjectCandidates.ToArray()); Assert.False(guest.HasUnsavedChanges);
        var panel = new StructureAnalysisPanel { DataContext = guest, Width = 268, Height = 1400 };
        panel.Measure(new Size(268, 1400)); panel.Arrange(new Rect(0, 0, 268, 1400)); panel.UpdateLayout();
        T? Find<T>(DependencyObject element) where T : DependencyObject
        {
            if (element is T match) return match;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) if (Find<T>(VisualTreeHelper.GetChild(element, i)) is { } child) return child;
            return null;
        }
        Find<System.Windows.Controls.TabControl>(panel)!.SelectedIndex = 2;
        panel.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.DataBind);
        void Check(DependencyObject element)
        {
            var properties = element.GetLocalValueEnumerator();
            while (properties.MoveNext())
            {
                var binding = BindingOperations.GetBindingExpressionBase(element, properties.Current.Property);
                if (binding is not null) Assert.False(binding.HasError, $"{element.GetType().Name}.{properties.Current.Property.Name}");
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Check(VisualTreeHelper.GetChild(element, i));
        }
        Check(panel); Assert.False(guest.HasNotification, guest.Notification);
    });

    [Fact]
    public Task ColoredObjectsCanBeReviewedSplitMergedSavedAndInvalidated() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); using var vm = Vm(); await Import(vm); await vm.AnalyzeStructureAsync();
        foreach (var s in vm.StructureSurfaces.Where(s => s.Kind != StructureSurfaceKind.Unknown).ToArray())
        { vm.SelectedStructureSurface = s; vm.ConfirmStructureSurfaceCommand.Execute(null); }
        await vm.BuildRoomAsync(); await vm.AnalyzeObjectsAsync();
        Assert.False(vm.HasNotification, vm.Notification); Assert.True(vm.HasObjectCandidates); Assert.NotEmpty(vm.ObjectCandidates);
        Assert.True(vm.ObjectCandidatesVisible); Assert.NotNull(vm.ObjectCandidateOutline);
        var count = vm.ObjectCandidates.Count; var samples = vm.ObjectCandidates.Sum(o => o.Samples.Length);
        vm.ObjectCandidateLabel = "Столешница"; vm.ConfirmObjectCandidateCommand.Execute(null);
        Assert.Equal("Столешница", vm.SelectedObjectCandidate!.Label);
        Assert.Equal(StructureReviewState.Confirmed, vm.SelectedObjectCandidate.Review);
        vm.SplitObjectCandidateCommand.Execute(null); Assert.Equal(count + 1, vm.ObjectCandidates.Count);
        Assert.Equal(samples, vm.ObjectCandidates.Sum(o => o.Samples.Length));
        vm.MergeObjectId = vm.ObjectCandidates.First(o => o != vm.SelectedObjectCandidate).Id;
        vm.MergeObjectCandidatesCommand.Execute(null); Assert.Equal(count, vm.ObjectCandidates.Count);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.Equal(count + 1, vm.ObjectCandidates.Count);
        await vm.RedoProjectCommand.ExecuteAsync(); Assert.Equal(count, vm.ObjectCandidates.Count);
        var full = vm.ObjectCandidateGeometry!.Positions!.Count;
        vm.ClipOffset = .5; vm.ClipEnabled = true;
        Assert.All(vm.ObjectCandidateGeometry!.Positions!, p => Assert.True(p.Z <= .5001));
        vm.ClipEnabled = false; Assert.Equal(full, vm.ObjectCandidateGeometry!.Positions!.Count);
        var path = Path.Combine(files.Directory, "objects.subterra"); Assert.True(await vm.SaveAsync(path));
        using var reopened = Vm(); await reopened.OpenAsync(path);
        Assert.True(reopened.HasRoom); Assert.True(reopened.HasObjectCandidates); Assert.False(reopened.ObjectCandidatesAreStale);
        Assert.Equal(samples, reopened.ObjectCandidates.Sum(o => o.Samples.Length));
        Assert.Equal(vm.ObjectCandidateGeometry.Colors, reopened.ObjectCandidateGeometry!.Colors);
        reopened.ObjectX = 1; await reopened.ApplyObjectCommand.ExecuteAsync();
        Assert.True(reopened.ObjectCandidatesAreStale); Assert.Null(reopened.ObjectCandidateGeometry);
        await reopened.UndoProjectCommand.ExecuteAsync(); Assert.False(reopened.ObjectCandidatesAreStale);
        Assert.NotNull(reopened.ObjectCandidateGeometry); Assert.False(reopened.HasNotification, reopened.Notification);
    });

    private static MainViewModel Vm(TestAccess? access = null, IStructureAnalyzer? analyzer = null) =>
        new(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: access ?? new(),
            interaction: new TestInteraction(), structureAnalyzer: analyzer);
    private static Task Import(MainViewModel vm) => vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters, LocalOrigin: false));

    [Fact]
    public Task AnalysisIsAdminOnlyIncludingDirectCalls() => ScanControlTests.OnSta(async () =>
    {
        var analyzer = new BlockingAnalyzer(); using var vm = Vm(new(false), analyzer);
        await Import(vm); Assert.True(vm.IsImported);
        Assert.False(vm.CanAnalyzeStructure); Assert.False(vm.AnalyzeStructureCommand.CanExecute(null));
        await vm.AnalyzeStructureAsync(); Assert.False(analyzer.Called); Assert.False(vm.HasStructureAnalysis);
    });

    [Fact]
    public Task OverlaySelectionClippingAndLodDoNotModifyTheSavedSource() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); using var vm = Vm(); await Import(vm);
        var path = Path.Combine(files.Directory, "room.subterra"); Assert.True(await vm.SaveAsync(path));
        var before = (await new SessionStorage().LoadAsync(path)).Project!;
        await vm.AnalyzeStructureCommand.ExecuteAsync();
        Assert.False(vm.HasNotification, vm.Notification); Assert.True(vm.HasStructureAnalysis);
        Assert.Equal(7, vm.StructureSurfaces.Count); Assert.True(vm.StructureVisible);
        Assert.NotNull(vm.StructureGeometry); Assert.NotNull(vm.StructureOutline);
        Assert.True(vm.HasUnsavedChanges); Assert.True(vm.CanUndoProject);
        var samples = vm.StructureSurfaces.SelectMany(s => s.Samples).ToArray();
        var fullCount = vm.StructureGeometry.Positions!.Count;
        vm.PointQuality = PointCloudQuality.All[0];
        Assert.Equal(samples, vm.StructureSurfaces.SelectMany(s => s.Samples).ToArray());
        vm.SelectedStructureSurface = vm.StructureSurfaces.First(s => s.Kind == StructureSurfaceKind.Wall);
        vm.ClipOffset = 1.5; vm.ClipEnabled = true;
        Assert.InRange(vm.StructureGeometry.Positions!.Count, 1, fullCount - 1);
        Assert.All(vm.StructureGeometry.Positions!, p => Assert.True(p.Z <= 1.5001f));
        Assert.All(vm.StructureOutline!.Positions!, p => Assert.True(p.Z <= 1.5001f));
        vm.ClipEnabled = false; Assert.Equal(fullCount, vm.StructureGeometry.Positions!.Count);
        vm.ShowStructure = false; Assert.False(vm.StructureVisible); Assert.True(vm.HasStructureAnalysis);
        Assert.True(await vm.SaveAsync(path));
        var after = (await new SessionStorage().LoadAsync(path)).Project!;
        Assert.Equal(before.Active!.Source.Points, after.Active!.Source.Points);
        Assert.Equal(before.Active.Transform, after.Active.Transform); Assert.Null(after.Active.Mask);
        using var reopened = Vm(); await reopened.OpenAsync(path);
        Assert.True(reopened.IsImported); Assert.True(reopened.HasStructureAnalysis); Assert.False(reopened.StructureIsStale);
        Assert.Equal(samples, reopened.StructureSurfaces.SelectMany(s => s.Samples).ToArray());
    });

    [Theory]
    [InlineData("visibility")] [InlineData("transform")] [InlineData("cleanup")] [InlineData("replacement")]
    public Task GeometryChangesInvalidateTheAnalysis(string change) => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await Import(vm); await vm.AnalyzeStructureAsync(); Assert.True(vm.HasStructureAnalysis);
        switch (change)
        {
            case "visibility": await vm.ToggleObjectAsync(vm.SelectedScanObject!.Id); break;
            case "transform": vm.ObjectX = 1; await vm.ApplyObjectCommand.ExecuteAsync(); break;
            case "cleanup":
                vm.CleanMaxX = 3; await vm.PreviewBoxAsync(true); Assert.True(vm.HasEditPreview); Assert.False(vm.HasStructureAnalysis);
                vm.CancelCleanPreview(); Assert.True(vm.HasStructureAnalysis); Assert.False(vm.StructureIsStale);
                vm.CleanMaxX = 3; await vm.PreviewBoxAsync(true); vm.ApplyCleanPreviewCommand.Execute(null); break;
            case "replacement": await vm.LoadDemoAsync(); break;
        }
        Assert.False(vm.HasNotification, vm.Notification);
        if (change == "replacement") { Assert.False(vm.HasStructureAnalysis); Assert.Empty(vm.StructureSurfaces); }
        else
        {
            Assert.True(vm.HasStructureAnalysis); Assert.True(vm.StructureIsStale); Assert.False(vm.StructureVisible);
            Assert.False(vm.CanReviewStructure); Assert.NotEmpty(vm.StructureSurfaces);
        }
        Assert.Null(vm.StructureGeometry); Assert.Null(vm.StructureOutline);
        if (change != "replacement")
        {
            await vm.UndoProjectCommand.ExecuteAsync(); Assert.True(vm.HasStructureAnalysis); Assert.False(vm.StructureIsStale);
            Assert.NotNull(vm.StructureGeometry);
            await vm.RedoProjectCommand.ExecuteAsync(); Assert.True(vm.StructureIsStale); Assert.Null(vm.StructureGeometry);
        }
    });

    [Fact]
    public Task CancellationRejectsALateResultAndKeepsThePreviousAnalysis() => ScanControlTests.OnSta(async () =>
    {
        var analyzer = new BlockingAnalyzer(firstReal: true); using var vm = Vm(analyzer: analyzer);
        await Import(vm); await vm.AnalyzeStructureAsync(); var previous = vm.StructureGeometry;
        var pending = vm.AnalyzeStructureAsync(); Assert.True(vm.IsBusy); Assert.True(analyzer.Called);
        vm.CancelOperation(); await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(vm.IsBusy); Assert.Same(previous, vm.StructureGeometry);
        analyzer.Pending.SetResult(new([], 0, 0, new()));
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.Same(previous, vm.StructureGeometry); Assert.False(vm.HasNotification, vm.Notification);
    });

    [Fact]
    public Task AccessRevokedDuringAnalysisRejectsTheResult() => ScanControlTests.OnSta(async () =>
    {
        var access = new TestAccess(); var analyzer = new BlockingAnalyzer(); using var vm = Vm(access, analyzer);
        await Import(vm); var pending = vm.AnalyzeStructureAsync(); Assert.True(vm.IsBusy);
        access.Logout(); analyzer.Pending.SetResult(new([], 0, 0, new())); await pending;
        Assert.False(vm.IsAdministrator); Assert.False(vm.HasStructureAnalysis); Assert.False(vm.IsBusy);
    });

    [Fact]
    public Task PopulatedStructurePanelResolvesBindingsWithoutOpeningAWindow() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await Import(vm); await vm.AnalyzeStructureAsync(); Assert.NotEmpty(vm.StructureSurfaces);
        var panel = new StructureAnalysisPanel { DataContext = vm, Width = 268, Height = 900 };
        panel.Measure(new Size(268, 900)); panel.Arrange(new Rect(0, 0, 268, 900)); panel.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        void Check(DependencyObject element)
        {
            var properties = element.GetLocalValueEnumerator();
            while (properties.MoveNext())
            {
                var binding = BindingOperations.GetBindingExpressionBase(element, properties.Current.Property);
                if (binding is not null) Assert.False(binding.HasError, $"{element.GetType().Name}.{properties.Current.Property.Name}");
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Check(VisualTreeHelper.GetChild(element, i));
        }
        Check(panel);
    });

    [Fact]
    public Task ReviewCorrectionRejectionResetAndHistoryPreserveTheAlgorithmSuggestion() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await Import(vm); await vm.AnalyzeStructureAsync();
        var table = vm.StructureSurfaces.Single(s => s.Kind == StructureSurfaceKind.Unknown);
        vm.SelectedStructureSurface = table;
        vm.SelectedStructureClass = vm.StructureClasses.Single(c => c.Kind == StructureSurfaceKind.Floor);
        vm.ConfirmStructureSurfaceCommand.Execute(null);
        Assert.Equal(table.Id, vm.SelectedStructureSurface!.Id);
        Assert.Equal(StructureSurfaceKind.Floor, vm.SelectedStructureSurface.Kind);
        Assert.Equal(StructureSurfaceKind.Unknown, vm.SelectedStructureSurface.SuggestedKind);
        Assert.Equal(StructureReviewState.Confirmed, vm.SelectedStructureSurface.Review);
        var count = vm.StructureGeometry!.Positions!.Count;
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.Equal(StructureReviewState.Proposed, vm.SelectedStructureSurface!.Review);
        Assert.Equal(StructureSurfaceKind.Unknown, vm.SelectedStructureSurface.Kind);
        await vm.RedoProjectCommand.ExecuteAsync(); Assert.Equal(StructureReviewState.Confirmed, vm.SelectedStructureSurface!.Review);
        vm.RejectStructureSurfaceCommand.Execute(null);
        Assert.Equal(StructureReviewState.Rejected, vm.SelectedStructureSurface!.Review);
        Assert.Equal(count - table.Samples.Length, vm.StructureGeometry!.Positions!.Count); Assert.Null(vm.StructureOutline);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.Equal(count, vm.StructureGeometry!.Positions!.Count);
        vm.ResetStructureSurfaceCommand.Execute(null);
        Assert.Equal(StructureSurfaceKind.Unknown, vm.SelectedStructureSurface!.Kind);
        Assert.Equal(StructureReviewState.Proposed, vm.SelectedStructureSurface.Review); Assert.False(vm.CanRedoProject);
        vm.ClearStructureCommand.Execute(null); Assert.False(vm.HasStructureAnalysis);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.True(vm.HasStructureAnalysis);
        Assert.False(vm.HasNotification, vm.Notification);
    });

    [Fact]
    public Task ReanalysisCanBeUndoneToRestoreOperatorDecisions() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await Import(vm); await vm.AnalyzeStructureAsync();
        vm.RejectStructureSurfaceCommand.Execute(null); var rejectedId = vm.SelectedStructureSurface!.Id;
        await vm.AnalyzeStructureAsync(); Assert.All(vm.StructureSurfaces, s => Assert.Equal(StructureReviewState.Proposed, s.Review));
        await vm.UndoProjectCommand.ExecuteAsync();
        Assert.Equal(StructureReviewState.Rejected, vm.StructureSurfaces.Single(s => s.Id == rejectedId).Review);
    });

    [Fact]
    public Task SavedReviewsAreViewableByGuestsButAllMutationCommandsAreBlocked() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); var path = Path.Combine(files.Directory, "reviewed.subterra");
        using (var admin = Vm())
        {
            await Import(admin); await admin.AnalyzeStructureAsync(); admin.ConfirmStructureSurfaceCommand.Execute(null);
            Assert.True(await admin.SaveAsync(path));
        }
        using var guest = Vm(new(false)); await guest.OpenAsync(path);
        Assert.True(guest.HasStructureAnalysis); Assert.True(guest.StructureVisible);
        var surfaces = guest.StructureSurfaces.ToArray();
        guest.SelectedStructureClass = guest.StructureClasses.Single(c => c.Kind == StructureSurfaceKind.Unknown);
        foreach (var command in new[] { guest.ConfirmStructureSurfaceCommand, guest.RejectStructureSurfaceCommand,
                     guest.ResetStructureSurfaceCommand, guest.ClearStructureCommand })
        { Assert.False(command.CanExecute(null)); command.Execute(null); }
        await guest.AnalyzeStructureAsync();
        Assert.Equal(surfaces, guest.StructureSurfaces.ToArray()); Assert.False(guest.HasUnsavedChanges);
        Assert.False(guest.CanUndoProject); Assert.False(guest.HasNotification, guest.Notification);
    });

    [Fact]
    public Task RevokingAccessAlsoBlocksReviewOfAnExistingResult() => ScanControlTests.OnSta(async () =>
    {
        var access = new TestAccess(); using var vm = Vm(access); await Import(vm); await vm.AnalyzeStructureAsync();
        access.Logout(); vm.ConfirmStructureSurfaceCommand.Execute(null); vm.RejectStructureSurfaceCommand.Execute(null);
        vm.ClearStructureCommand.Execute(null);
        Assert.True(vm.HasStructureAnalysis); Assert.False(vm.CanReviewStructure);
        Assert.All(vm.StructureSurfaces, s => Assert.Equal(StructureReviewState.Proposed, s.Review));
    });

    [Fact]
    public Task RecoveryRestoresReviewsWithoutOverwritingTheSavedProject() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); var store = new WorkspaceStateStore(files.Directory);
        var path = Path.Combine(files.Directory, "room.subterra"); var storage = new SessionStorage();
        using (var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), storage,
                   access: new TestAccess(), interaction: new TestInteraction(), workspaceStore: store))
        {
            await Import(vm); Assert.True(await vm.SaveAsync(path)); await vm.AnalyzeStructureAsync();
            vm.ConfirmStructureSurfaceCommand.Execute(null); await vm.AutoSaveAsync(); Assert.True(store.HasRecovery);
            Assert.Null((await storage.LoadAsync(path)).Project!.Structure);
        }
        using var restored = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), storage,
            access: new TestAccess(), interaction: new TestInteraction(), workspaceStore: new WorkspaceStateStore(files.Directory));
        await restored.RestoreRecoveryAsync();
        Assert.False(restored.HasNotification, restored.Notification); Assert.True(restored.HasUnsavedChanges);
        Assert.True(restored.HasStructureAnalysis); Assert.False(restored.StructureIsStale);
        Assert.Single(restored.StructureSurfaces, s => s.Review == StructureReviewState.Confirmed);
        Assert.Null((await storage.LoadAsync(path)).Project!.Structure);
        Assert.True(await restored.SaveCurrentAsync()); Assert.NotNull((await storage.LoadAsync(path)).Project!.Structure);
    });

    private sealed class BlockingAnalyzer(bool firstReal = false) : IStructureAnalyzer
    {
        private bool _first = firstReal;
        public bool Called { get; private set; }
        public TaskCompletionSource<StructureAnalysisResult> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StructureAnalysisResult> AnalyzeAsync(ScanProject project, StructureAnalysisOptions options, IProgress<double>? progress, CancellationToken token)
        {
            Called = true;
            if (!_first) return Pending.Task;
            _first = false; return new PlaneStructureAnalyzer().AnalyzeAsync(project, options, progress, token);
        }
    }
    private sealed class Files : IDisposable
    {
        public string Directory { get; } = WorkspaceFeatureTests.TemporaryDirectory();
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
