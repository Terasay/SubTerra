using System.IO;
using System.Numerics;
using System.Windows.Controls;
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

    [Fact]
    public Task FindingsCanBeReviewedAndTheirRgbInputInspectedWithoutChangingTheProject() => ScanControlTests.OnSta(async () =>
    {
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        try
        {
            using var vm = Vm(); await vm.ImportAsync(StructureAnalysisTests.ClassroomPath, new(ScanUnit.Meters));
            Assert.True(await vm.SaveAsync(Path.Combine(directory, "scan.subterra")));
            await vm.DetectPeopleAsync(); Assert.False(vm.HasNotification, vm.Notification); Assert.True(vm.HasPeopleDetection);
            Assert.Single(vm.PersonFindings); Assert.NotNull(vm.PersonPreview); Assert.NotNull(vm.PersonOutline); Assert.False(vm.HasUnsavedChanges);
            vm.ConfirmPersonCommand.Execute(null); Assert.Equal(StructureReviewState.Confirmed, vm.SelectedPerson!.Review);
            vm.RejectPersonCommand.Execute(null); Assert.Equal(StructureReviewState.Rejected, vm.SelectedPerson!.Review);
            Assert.Contains("ложных: 1", vm.PeopleSummary); vm.ResetPersonReviewCommand.Execute(null);
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
        public Task<PersonDetectionResult> AnalyzeAsync(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces, float threshold, PointClipPlane? clip, IProgress<double>? progress, CancellationToken token) => Task.FromResult(FakeResult(project, poses));
    }
    private sealed class BlockingAnalyzer : IPersonScanAnalyzer
    {
        public ScanProject? Project; public ScanViewPose[]? Poses;
        public TaskCompletionSource<PersonDetectionResult> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PersonDetectionResult> AnalyzeAsync(ScanProject project, ScanViewPose[] poses, StructureSurface[] surfaces, float threshold, PointClipPlane? clip, IProgress<double>? progress, CancellationToken token)
        { Project = project; Poses = poses; return Pending.Task; }
    }
}
