using System.IO;
using System.Numerics;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.ViewModels;
using SubTerra.Views;
using Xunit;

namespace SubTerra.Tests;

public sealed class PeopleDetectionTests
{
    [Fact]
    public void WallImageFilterRequiresDenseSupportOnOneFiniteWallAndKeepsForegroundPeople()
    {
        var positions = new Vector3[64 * 64];
        for (var i = 0; i < positions.Length; i++) positions[i] = new(i % 64 / 32f, 3, i / 64 / 32f);
        var image = new ScanImage(new(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ), 64, [],
            Enumerable.Repeat(3f, positions.Length).ToArray(), positions);
        var wall = new StructureSurface(1, StructureSurfaceKind.Wall, Vector3.UnitY, -3, new(1, 3, 1),
            [new(0, 3, 0), new(2, 3, 0), new(2, 3, 2), new(0, 3, 2)], 4, 0, []);
        var box = new ImageBox(2, 4, 60, 62, .7f);
        Assert.True(PersonScanAnalyzer.LiesOnWall(image, box, [wall]));
        Assert.False(PersonScanAnalyzer.LiesOnWall(image, box, [wall with { Review = StructureReviewState.Rejected }]));
        Assert.False(PersonScanAnalyzer.LiesOnWall(image, box, [wall with { Boundary = wall.Boundary.Select(p => p + Vector3.UnitX * 4).ToArray() }]));
        for (var y = 8; y < 58; y++)
            for (var x = 21; x < 43; x++) positions[y * 64 + x].Y -= .25f;
        Assert.False(PersonScanAnalyzer.LiesOnWall(image, box, [wall]));
        Array.Fill(image.Depth, float.PositiveInfinity);
        Assert.False(PersonScanAnalyzer.LiesOnWall(image, box, [wall]));
    }

    [Fact]
    public void CrossViewGroupingKeepsNeighboursUnlocatedBoxesAndSameViewDetectionsSeparate()
    {
        static PersonFinding At(int id, int view, float x) =>
            new(id, view, new(4, 8, 20, 48, .8f), new(x, 1, 0), new(x + .5f, 1.5f, 1.7f));
        var observations = new[] { At(1, 0, 0), At(2, 0, .4f), At(3, 1, .03f),
            At(4, 1, .42f), At(5, 2, 3) with { Minimum = null, Maximum = null }, At(6, 1, .02f) };
        var grouped = PersonScanAnalyzer.GroupAcrossViews(observations);
        Assert.Equal(new int?[] { 1, 2, 1, 2, null, 3 }, grouped.Select(f => f.SpatialGroup));
        Assert.Equal(observations.Select(f => f.Box), grouped.Select(f => f.Box));
    }

    [Fact]
    public void ReviewedExamplesTrainOnOneSetAndChangeDecisionsOnUnseenCandidatesAfterRestart()
    {
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        try
        {
            var path = new WorkspaceStateStore(directory).PersonExamplesPath;
            var training = new PersonCandidateLearning(path);
            var pose = new ScanViewPose(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ);
            ScanImage View(int seed)
            {
                var rgb = new byte[64 * 64 * 3];
                new Random(seed).NextBytes(rgb);
                return new(pose, 64, rgb, new float[64 * 64], []);
            }
            static PersonFinding Candidate(bool tall, float score) => new(tall ? 1 : 2, 0,
                tall ? new(10, 3, 20, 57, score) : new(24, 25, 58, 40, score),
                Vector3.Zero, tall ? new(.5f, .4f, 1.7f) : new(1.2f, .8f, .5f));
            for (var i = 0; i < 3; i++)
            {
                training.Review(View(i), Candidate(true, .5f), StructureReviewState.Confirmed);
                training.Review(View(i), Candidate(false, .5f), StructureReviewState.Rejected);
            }
            var restored = new PersonCandidateLearning(path); restored.Reload();
            Assert.True(restored.Ready);
            var unseen = new PersonDetectionResult([View(99)], [Candidate(true, .46f), Candidate(false, .56f)], "test");
            var corrected = Assert.Single(restored.Apply(unseen, .5f, true, default).Findings);
            Assert.Equal(1, corrected.Id);
            Assert.True(corrected.Box.Score > .5f);
            Assert.Equal(.46f, corrected.DetectorScore);
            Assert.Equal(StructureReviewState.Proposed, corrected.Review);
            Assert.Equal(2, Assert.Single(restored.Apply(unseen, .5f, false, default).Findings).Id);
            restored.Review(View(0), Candidate(false, .5f), StructureReviewState.Confirmed);
            training.Reload();
            Assert.Equal((4, 2), (training.Positives, training.Negatives));
            training.Review(View(0), Candidate(false, .5f), StructureReviewState.Proposed);
            restored.Reload(); Assert.False(restored.Ready);
            Assert.Equal((3, 2), (restored.Positives, restored.Negatives));
            Assert.Equal(2, Assert.Single(restored.Apply(unseen, .5f, true, default).Findings).Id);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ProjectionUsesMeasuredRgbDepthAndMasksWithoutDesktopOrGpu()
    {
        var scan = new ImportedScan { Metadata = new("rgb.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0),
            Points = [new(0, 2, 1.5f, 1), new(0, 4, 1.5f, 1)], Colors = [0xFF0000, 0x0000FF] };
        var project = ScanProject.FromSession(scan.ToSession()); var pose = new ScanViewPose(new(0, 0, 1.5f), Vector3.UnitY, Vector3.UnitZ);
        var rendered = ScanImageRenderer.Render(project, pose); var slot = 208 * 416 + 208;
        Assert.Equal(2, rendered.Depth[slot]); Assert.Equal(255, rendered.Rgb[slot * 3]); Assert.Equal(0, rendered.Rgb[slot * 3 + 2]);
        var masked = project.Replace(project.Active! with { Mask = new([false, true]) });
        var blue = ScanImageRenderer.Render(masked, pose); Assert.Equal(4, blue.Depth[slot]); Assert.Equal(255, blue.Rgb[slot * 3 + 2]);
        Assert.Equal(new Vector3(0, 2, 1.5f), project.Active!.Source.Points[0].Position);
        Assert.ThrowsAny<OperationCanceledException>(() => ScanImageRenderer.Render(project, pose, token: new(true)));
    }

    [Fact]
    public void YoloDecodeUsesPersonClassObjectnessGridAndSuppression()
    {
        var output = new float[3549 * 85];
        void Detection(int row, float x, float score)
        {
            output[row * 85] = x; output[row * 85 + 1] = 12; output[row * 85 + 2] = MathF.Log(5);
            output[row * 85 + 3] = MathF.Log(10); output[row * 85 + 4] = 1; output[row * 85 + 5] = score;
        }
        Detection(0, 10, .9f); Detection(1, 9, .8f); Detection(2, 30, .7f);
        output[3 * 85 + 4] = 1; output[3 * 85 + 6] = .99f; // Another COCO class must not count as a person.
        var boxes = OnnxPersonDetector.Decode(output, .3f);
        Assert.Equal(2, boxes.Length); Assert.Equal(.9f, boxes[0].Score); Assert.Equal(60, boxes[0].Left); Assert.Equal(100, boxes[0].Right);
        Assert.Empty(OnnxPersonDetector.Decode(new float[3549 * 85], .3f));
    }

    [Fact]
    public void StrongerChairClassIsNotReportedAsPersonAndSeatedAspectRatioRemainsEligible()
    {
        var output = new float[3549 * 85];
        void Box(int row, float person, float chair)
        {
            var i = row * 85; output[i] = 10 + row * 12; output[i + 1] = 12;
            output[i + 2] = MathF.Log(6); output[i + 3] = MathF.Log(7); // A seated person need not be a tall, narrow box.
            output[i + 4] = 1; output[i + 5] = person; output[i + 5 + 56] = chair;
        }
        Box(0, .7f, .9f); Box(1, .85f, .2f);
        var boxes = OnnxPersonDetector.Decode(output, .3f);
        Assert.Single(boxes); Assert.Equal(.85f, boxes[0].Score);
    }

    [Fact]
    public void CloseUpRejectsWholeChairEvidenceWithoutRejectingASeatedPersonOrChairBelowThem()
    {
        var target = new ImageBox(90, 50, 320, 365, .8f);
        Assert.True(OnnxPersonDetector.ChairExplainsCandidate(target, [], [new(92, 52, 318, 360, .9f)]));
        Assert.False(OnnxPersonDetector.ChairExplainsCandidate(target, [new(90, 50, 320, 365, .5f)], [new(92, 52, 318, 360, .95f)]));
        Assert.False(OnnxPersonDetector.ChairExplainsCandidate(target, [], [new(100, 240, 310, 360, .95f)]));
        Assert.False(OnnxPersonDetector.ChairExplainsCandidate(target, [], [])); // Missing person evidence alone is not a rejection.
    }

    [Fact]
    public void BundledOfficialModelRunsLocallyAndDoesNotInventPeopleOnBlankImage()
    {
        using var detector = new OnnxPersonDetector();
        var rgb = Enumerable.Repeat((byte)114, 416 * 416 * 3).ToArray();
        var image = new ScanImage(new(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ), 416, rgb, new float[416 * 416], new Vector3[416 * 416]);
        Assert.Empty(detector.Detect(image, .3f, default));
        Assert.ThrowsAny<OperationCanceledException>(() => detector.Detect(image, .3f, new(true)));
    }

    [Fact]
    public Task MeshButtonBuildsRgbPlyAndGuestReceivesNoEditAccess() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(); await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        Assert.False(vm.CanShowSurface); Assert.True(vm.CanSelectSurface); Assert.Contains("Построить", vm.SurfaceModeText);
        vm.FlyCommand.Execute(null); Assert.True(vm.IsFlying); Assert.True(vm.CanSelectSurface);
        await vm.SurfaceCommand.ExecuteAsync(); Assert.False(vm.HasNotification, vm.Notification);
        Assert.True(vm.HasReconstruction); Assert.True(vm.IsIndoorSurfaceView); Assert.True(vm.UseNeuralClasses); Assert.False(vm.IsFlying);
        using var guest = Vm(new TestAccess(false)); await guest.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        Assert.False(guest.CanSelectSurface); Assert.False(guest.CanDetectPeople);
        await guest.ShowSurfaceAsync(); await guest.DetectPeopleAsync(); Assert.False(guest.HasStructureAnalysis); Assert.False(guest.HasPeopleDetection);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task MeshButtonRequestsLoginAndBuildsOnlyAfterAccessIsGranted(bool login) => ScanControlTests.OnSta(async () =>
    {
        var access = new TestAccess(false); using var vm = Vm(access);
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        var prompts = 0; var dirty = vm.HasUnsavedChanges;
        vm.AdministratorAccessRequested += (_, _) => { prompts++; if (login) access.Login(); };
        Assert.True(vm.CanOpenSurface); Assert.False(vm.CanSelectSurface);
        Assert.True(vm.SurfaceCommand.CanExecute(null)); Assert.Contains("Нажмите", vm.SurfaceModeHint);
        await vm.SurfaceCommand.ExecuteAsync();
        Assert.Equal(1, prompts); Assert.Equal(login, vm.IsAdministrator);
        Assert.Equal(login, vm.HasStructureAnalysis); Assert.Equal(login, vm.HasReconstruction);
        Assert.Equal(login, vm.IsIndoorSurfaceView); Assert.False(vm.HasNotification, vm.Notification);
        if (!login) { Assert.True(vm.IsPointMode); Assert.Equal(dirty, vm.HasUnsavedChanges); }
    });

    [Fact]
    public Task FullyMaskedRgbScanDoesNotOfferPeopleSearchOrLogin() => ScanControlTests.OnSta(async () =>
    {
        var access = new TestAccess();
        using var vm = Vm(access);
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        await vm.PreviewBoxAsync(false); // The initial box encloses the whole scan.
        vm.ApplyCleanPreviewCommand.Execute(null);
        Assert.Equal("0", vm.PointCountText);
        Assert.False(vm.FindPeopleCommand.CanExecute(null));
        access.Logout();
        Assert.Contains("RGB", vm.PeopleAvailabilityHint);
    });

    [Fact]
    public Task PeopleAvailabilityExplainsMissingFloorAndUpdatesAfterAnalysisAndLogout() => ScanControlTests.OnSta(async () =>
    {
        var access = new TestAccess(); using var vm = Vm(access);
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        Assert.True(vm.CanDetectPeople); Assert.False(vm.CanSearchPeople); Assert.True(vm.PeopleNeedFloor);
        Assert.Contains("автоматически", vm.PeopleAvailabilityHint);
        vm.OpenPeopleAnalysisCommand.Execute(null);
        var panel = new StructureAnalysisPanel { DataContext = vm }; ThemeRenderingTests.Layout(panel, 252, 450);
        await Dispatcher.Yield(DispatcherPriority.DataBind); panel.UpdateLayout();
        var buttons = WorkspacePresentationTests.Descendants(panel).OfType<Button>()
            .Where(b => b.Command == vm.DetectPeopleCommand || b.Command == vm.FindPeopleCommand).ToArray();
        Assert.Equal(2, buttons.Length);
        foreach (var button in buttons)
            Assert.InRange(button.TransformToAncestor(panel).TransformBounds(new System.Windows.Rect(button.RenderSize)).Bottom, 1, 450);
        await vm.SearchPeopleCommand.ExecuteAsync(); Assert.False(vm.HasPeopleDetection);
        await vm.AnalyzeStructureAsync();
        Assert.True(vm.CanSearchPeople); Assert.False(vm.PeopleNeedFloor);
        Assert.Contains("восемь", vm.PeopleAvailabilityHint);
        access.Logout(); Assert.False(vm.CanDetectPeople); Assert.False(vm.PeopleNeedFloor);
        Assert.Contains("войти", vm.PeopleAvailabilityHint);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task FindPeopleButtonRunsInferenceOnlyAfterLoginAndKeepsPlaneAnalysisUnchanged(bool login) => ScanControlTests.OnSta(async () =>
    {
        var access = new TestAccess(false); var analyzer = new FakeAnalyzer(); using var vm = Vm(access, analyzer);
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        var dirty = vm.HasUnsavedChanges; var prompts = 0;
        vm.AdministratorAccessRequested += (_, _) => { prompts++; if (login) access.Login(); };
        vm.IsSidebarOpen = false; vm.IsInspectorOpen = true;
        Assert.True(vm.FindPeopleCommand.CanExecute(null));
        await vm.FindPeopleCommand.ExecuteAsync();
        Assert.Equal(1, prompts); Assert.Equal(login ? 1 : 0, analyzer.Calls);
        Assert.True(vm.IsPeopleAnalysis); Assert.True(vm.IsSidebarOpen); Assert.False(vm.IsInspectorOpen);
        Assert.Equal(login, vm.HasPeopleDetection); Assert.Equal(login, vm.IsPeoplePreviewVisible);
        Assert.False(vm.HasStructureAnalysis); Assert.Equal(dirty, vm.HasUnsavedChanges);
        Assert.False(vm.HasNotification, vm.Notification);
        if (login) { Assert.Equal(8, analyzer.Poses!.Length); Assert.Contains("8 ракурсов", vm.PeopleRunMode); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task CancelOrLogoutDuringAutomaticViewPreparationNeverStartsInference(bool logout) => ScanControlTests.OnSta(async () =>
    {
        var access = new TestAccess(); var planes = new BlockingPlaneAnalyzer(); var analyzer = new FakeAnalyzer();
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: access,
            interaction: new TestInteraction(), structureAnalyzer: planes, personAnalyzer: analyzer);
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        var running = vm.FindPeopleCommand.ExecuteAsync(); Assert.True(vm.IsBusy);
        if (logout) access.Logout(); else vm.CancelOperation();
        planes.Pending.SetResult(new([], 0, 0, new())); await running;
        Assert.Equal(0, analyzer.Calls); Assert.False(vm.HasPeopleDetection); Assert.False(vm.HasStructureAnalysis); Assert.False(vm.IsBusy);
    });

    [Fact]
    public Task ResultsCanBeSelectedInTheImageAndAllBoxesFollowTheChosenViewAndReview() => ScanControlTests.OnSta(async () =>
    {
        using var vm = Vm(analyzer: new MultipleViewsAnalyzer());
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        await vm.FindPeopleCommand.ExecuteAsync();
        Assert.True(vm.IsPeoplePreviewVisible); Assert.Equal(2, vm.CurrentPersonFindings.Count);
        Assert.Equal(48, vm.PeopleProposedOutline!.Positions!.Count); // Two complete 3D boxes.
        vm.SelectPersonAt(290d / 416, 150d / 416); Assert.Equal(2, vm.SelectedPerson!.Id);
        vm.ConfirmPersonCommand.Execute(null); Assert.NotNull(vm.PeopleConfirmedOutline); Assert.NotNull(vm.PeopleProposedOutline);
        vm.RejectPersonCommand.Execute(null); Assert.Null(vm.PeopleConfirmedOutline); Assert.NotNull(vm.PeopleRejectedOutline);
        vm.SelectedPersonView = vm.PersonViews[1]; Assert.Single(vm.CurrentPersonFindings); Assert.Equal(3, vm.SelectedPerson!.Id);
        vm.SelectedPersonView = vm.PersonViews[2]; Assert.True(vm.PeopleViewIsEmpty); Assert.Null(vm.SelectedPerson);
        Assert.Null(vm.PersonOutline); Assert.Null(vm.PeopleProposedOutline); Assert.Null(vm.PeopleRejectedOutline);
        Assert.NotNull(vm.PersonPreview); Assert.True(vm.IsPeoplePreviewVisible);
        vm.FocusPersonViewCommand.Execute(null); Assert.False(vm.IsPeoplePreviewVisible); Assert.Equal("Ракурс проверки людей", vm.CameraLabel);
        vm.ShowPeoplePreviewCommand.Execute(null); Assert.True(vm.IsPeoplePreviewVisible);
        vm.ClosePeoplePreviewCommand.Execute(null); Assert.False(vm.IsPeoplePreviewVisible);
    });

    [Theory]
    [InlineData(1040, 640)] [InlineData(1440, 900)] [InlineData(1920, 1080)]
    public Task DetectionResultOccupiesTheMainViewportAndToolbarRunsTheDetector(int width, int height) => ScanControlTests.OnSta(async () =>
    {
        var analyzer = new FakeAnalyzer(); using var vm = Vm(analyzer: analyzer);
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        var root = ThemeRenderingTests.ShellPreview(vm); ThemeRenderingTests.Layout(root, width, height);
        var toolbar = WorkspacePresentationTests.Descendants((DependencyObject)root.FindName("Scene3DHost")).OfType<Button>().Single(b => Equals(b.Content, "Найти людей"));
        Assert.Same(vm.FindPeopleCommand, toolbar.Command);
        await ((AsyncRelayCommand)toolbar.Command!).ExecuteAsync();
        WorkspaceLayout.Apply(root, vm, width); ThemeRenderingTests.Layout(root, width, height);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); root.UpdateLayout();
        Assert.Equal(1, analyzer.Calls); Assert.True(vm.IsPeoplePreviewVisible); Assert.False(vm.IsInspectorOpen);
        var host = (Border)root.FindName("PeopleResultHost"); var image = (Image)root.FindName("PeopleResultImage");
        Assert.Equal(Visibility.Visible, host.Visibility); Assert.True(host.ActualWidth >= 500);
        Assert.True(image.ActualWidth >= 150 && image.ActualHeight >= 150, $"Result image: {image.ActualWidth:F0} × {image.ActualHeight:F0}");
        Assert.Same(vm.PersonPreview, image.Source); ThemeRenderingTests.CheckReadableText(root);
        var list = WorkspacePresentationTests.Descendants(root).OfType<ListBox>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Кандидаты людей");
        Assert.Same(vm.SelectedPerson, list.SelectedItem);
        vm.ConfirmPersonCommand.Execute(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); root.UpdateLayout();
        Assert.Equal(StructureReviewState.Confirmed, vm.SelectedPerson?.Review); Assert.Same(vm.SelectedPerson, list.SelectedItem);
        Assert.NotNull(vm.PeopleConfirmedOutline); Assert.Null(vm.PeopleProposedOutline);
        WorkspacePresentationTests.SavePreview(root, $"people-result-{width}.png");
        vm.ClosePeoplePreviewCommand.Execute(null); await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.Equal(Visibility.Collapsed, host.Visibility); Assert.Equal(1, analyzer.Calls);
    });

    [Fact]
    public Task GlbPeopleSearchUsesTheBundledDetectorFromTheWorkspace() => ScanControlTests.OnSta(async () =>
    {
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        try
        {
            var path = Environment.GetEnvironmentVariable("SUBTERRA_REVIEW_GLB");
            int? expectedGroups = null;
            if (string.IsNullOrEmpty(path))
            {
                path = Path.Combine(directory, "flat-texture.glb");
                ScanImportTests.CreateGlb(path);
                expectedGroups = 0;
            }
            else if (int.TryParse(Environment.GetEnvironmentVariable("SUBTERRA_REVIEW_GROUPS"), out var count))
                expectedGroups = count;
            using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(),
                access: new TestAccess(), interaction: new TestInteraction(), workspaceStore: new(directory));
            await vm.ImportAsync(path, new());
            Assert.True(vm.UsesOriginalMaterials);
            await vm.FindPeopleCommand.ExecuteAsync();
            Assert.False(vm.HasNotification, vm.Notification);
            Assert.True(vm.IsPeoplePreviewVisible);
            if (expectedGroups.HasValue)
            {
                Assert.All(vm.PersonFindings, f => Assert.True(f.SpatialGroup.HasValue, vm.PeopleSummary));
                Assert.Equal(expectedGroups.Value, vm.PersonFindings.Select(f => f.SpatialGroup).Distinct().Count());
            }
            Console.WriteLine(vm.PeopleSummary);
            var preview = Environment.GetEnvironmentVariable("SUBTERRA_UI_PREVIEW_DIR");
            if (!string.IsNullOrWhiteSpace(preview))
            {
                Directory.CreateDirectory(preview);
                foreach (var view in vm.PersonViews)
                {
                    vm.SelectedPersonView = view;
                    var visual = new DrawingVisual();
                    using (var drawing = visual.RenderOpen()) drawing.DrawImage(vm.PersonPreview, new Rect(0, 0, 416, 416));
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(416, 416, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(preview, $"people-view-{view.Index + 1:00}.png"));
                    encoder.Save(stream);
                }
            }
        }
        finally { Directory.Delete(directory, true); }
    });

    [Fact]
    public Task FindPeopleRunsTheBundledModelAcrossRoomViewsWithoutChangingTheScan() => ScanControlTests.OnSta(async () =>
    {
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(),
            access: new TestAccess(), interaction: new TestInteraction());
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        var dirty = vm.HasUnsavedChanges;
        await vm.FindPeopleCommand.ExecuteAsync();
        Assert.False(vm.HasNotification, vm.Notification); Assert.True(vm.HasPeopleDetection); Assert.Equal(8, vm.PersonViews.Count);
        Assert.NotNull(vm.PersonPreview); Assert.True(vm.IsPeoplePreviewVisible);
        Assert.False(vm.HasStructureAnalysis); Assert.False(vm.CanUndoProject); Assert.Equal(dirty, vm.HasUnsavedChanges);
    });

    [Fact]
    public Task AutomaticSearchRespectsRejectedFloorsAndPreservesTheOperatorsAnalysis() => ScanControlTests.OnSta(async () =>
    {
        var analyzer = new FakeAnalyzer(); using var vm = Vm(analyzer: analyzer);
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters)); await vm.AnalyzeStructureAsync();
        foreach (var floor in vm.StructureSurfaces.Where(s => s.Kind == StructureSurfaceKind.Floor).ToArray())
        { vm.SelectedStructureSurface = floor; vm.RejectStructureSurfaceCommand.Execute(null); }
        Assert.False(vm.CanSearchPeople); var surfaces = vm.StructureSurfaces.ToArray();
        await vm.FindPeopleCommand.ExecuteAsync();
        Assert.Single(analyzer.Poses!); Assert.Contains("пол для обзора не найден", vm.PeopleRunMode);
        Assert.Equal(surfaces, vm.StructureSurfaces.ToArray()); Assert.True(vm.HasPeopleDetection);
    });

    [Fact]
    public Task TunnelPeopleSearchChecksOnlyCurrentMeasuredRgbViewWithoutRoomAnalysisOrHistoryChanges() => ScanControlTests.OnSta(async () =>
    {
        var planes = new BlockingPlaneAnalyzer(); planes.Pending.SetResult(new([], 0, 0, new()));
        var people = new FakeAnalyzer();
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(),
            access: new TestAccess(), interaction: new TestInteraction(), structureAnalyzer: planes, personAnalyzer: people);
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        vm.SelectedProjectMode = vm.ProjectModes.Single(m => m.Mode == ProjectAnalysisMode.Tunnel);
        Assert.True(vm.CanFindPeople); Assert.False(vm.CanSearchPeople); Assert.False(vm.PeopleNeedFloor);
        var undo = vm.UndoProjectText; var dirty = vm.HasUnsavedChanges;
        await vm.FindPeopleCommand.ExecuteAsync();
        Assert.False(vm.HasNotification, vm.Notification); Assert.True(vm.HasPeopleDetection);
        Assert.Equal(0, planes.Calls); Assert.Equal(1, people.Calls); Assert.Single(people.Poses!); Assert.Empty(people.Surfaces!);
        Assert.False(vm.HasStructureAnalysis); Assert.Equal(undo, vm.UndoProjectText); Assert.Equal(dirty, vm.HasUnsavedChanges);
        await vm.LoadTunnelDemoAsync();
        Assert.True(vm.IsTunnelMode); Assert.True(vm.HasTunnelSections); Assert.False(vm.CanFindPeople);
        Assert.False(vm.FindPeopleCommand.CanExecute(null));
        await vm.FindPeopleCommand.ExecuteAsync(); Assert.Equal(1, people.Calls); Assert.Equal(0, planes.Calls);
    });

    [Fact]
    public Task FindingsCanBeReviewedAndTheirRgbInputInspectedWithoutChangingTheProject() => ScanControlTests.OnSta(async () =>
    {
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        try
        {
            var store = new WorkspaceStateStore(directory);
            using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(),
                access: new TestAccess(), interaction: new TestInteraction(), workspaceStore: store, personAnalyzer: new FakeAnalyzer());
            await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
            Assert.True(await vm.SaveAsync(Path.Combine(directory, "scan.subterra")));
            await vm.DetectPeopleAsync(); Assert.False(vm.HasNotification, vm.Notification); Assert.True(vm.HasPeopleDetection);
            Assert.Single(vm.PersonFindings); Assert.NotNull(vm.PersonPreview); Assert.NotNull(vm.PersonOutline); Assert.False(vm.HasUnsavedChanges);
            vm.ConfirmPersonCommand.Execute(null); Assert.Equal(StructureReviewState.Confirmed, vm.SelectedPerson!.Review);
            vm.RejectPersonCommand.Execute(null); Assert.Equal(StructureReviewState.Rejected, vm.SelectedPerson!.Review);
            var persisted = new PersonCandidateLearning(store.PersonExamplesPath);
            persisted.Reload(); Assert.Equal((0, 1), (persisted.Positives, persisted.Negatives));
            Assert.Contains("ложных: 1", vm.PeopleSummary); vm.ResetPersonReviewCommand.Execute(null);
            persisted.Reload(); Assert.Equal((0, 0), (persisted.Positives, persisted.Negatives));
            var panel = new PeopleAnalysisPanel { DataContext = vm }; ThemeRenderingTests.Layout(panel, 312, 1600);
            await Dispatcher.Yield(DispatcherPriority.DataBind); panel.UpdateLayout(); ThemeRenderingTests.CheckReadableText(panel);
            vm.FocusPersonViewCommand.Execute(null); Assert.Equal("Ракурс проверки людей", vm.CameraLabel);
            await vm.AnalyzeStructureAsync(); Assert.False(vm.HasPeopleDetection); Assert.Null(vm.PersonOutline);
        }
        finally { Directory.Delete(directory, true); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task CancelOrLogoutPreventsLatePeopleResultsFromBeingApplied(bool logout) => ScanControlTests.OnSta(async () =>
    {
        var analyzer = new BlockingAnalyzer(); var access = new TestAccess(); using var vm = Vm(access, analyzer);
        await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
        var running = vm.DetectPeopleAsync(); Assert.True(vm.IsBusy);
        if (logout) access.Logout(); else vm.CancelOperation();
        analyzer.Pending.SetResult(FakeResult(analyzer.Project!, analyzer.Poses!)); await running;
        Assert.False(vm.HasPeopleDetection); Assert.False(vm.IsBusy);
    });

    private static MainViewModel Vm(TestAccess? access = null, IPersonScanAnalyzer? analyzer = null) => new(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(),
        access: access ?? new(), interaction: new TestInteraction(), personAnalyzer: analyzer ?? new FakeAnalyzer());
    private static PersonDetectionResult FakeResult(ScanProject project, ScanViewPose[] poses)
    {
        var view = ScanImageRenderer.Render(project, poses[0]);
        return new([view], [new(1, 0, new(70, 30, 180, 340, .8f), new(1, 1, 0), new(2, 2, 1.8f))], "test detector");
    }
    private sealed class FakeAnalyzer : IPersonScanAnalyzer
    {
        public int Calls; public ScanViewPose[]? Poses; public StructureSurface[]? Surfaces;
        public Task<PersonDetectionResult> AnalyzeAsync(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces, float threshold, PointClipPlane? clip, IProgress<double>? progress, CancellationToken token)
        { Calls++; Poses = poses; Surfaces = surfaces; return Task.FromResult(FakeResult(project, poses)); }
    }
    private sealed class MultipleViewsAnalyzer : IPersonScanAnalyzer
    {
        public Task<PersonDetectionResult> AnalyzeAsync(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces, float threshold, PointClipPlane? clip, IProgress<double>? progress, CancellationToken token) => Task.FromResult(new PersonDetectionResult(
            poses.Take(3).Select(p => ScanImageRenderer.Render(project, p)).ToArray(),
            [new(1, 0, new(70, 30, 180, 340, .8f), new(1, 1, 0), new(2, 2, 1.8f)),
             new(2, 0, new(240, 30, 340, 340, .7f), new(3, 1, 0), new(4, 2, 1.8f)),
             new(3, 1, new(70, 30, 180, 340, .9f), null, null)], "test detector"));
    }
    private sealed class BlockingPlaneAnalyzer : IStructureAnalyzer
    {
        public int Calls;
        public TaskCompletionSource<StructureAnalysisResult> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StructureAnalysisResult> AnalyzeAsync(ScanProject project, StructureAnalysisOptions options, IProgress<double>? progress, CancellationToken token)
        { Calls++; return Pending.Task; }
    }
    private sealed class BlockingAnalyzer : IPersonScanAnalyzer
    {
        public ScanProject? Project; public ScanViewPose[]? Poses;
        public TaskCompletionSource<PersonDetectionResult> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PersonDetectionResult> AnalyzeAsync(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces, float threshold, PointClipPlane? clip, IProgress<double>? progress, CancellationToken token)
        { Project = project; Poses = poses; return Pending.Task; }
    }
}
