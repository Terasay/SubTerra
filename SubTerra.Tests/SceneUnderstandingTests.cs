using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
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

public sealed class SceneUnderstandingTests
{
    private static ScanProject Project() => ScanProject.FromSession(new ImportedScan
    {
        Metadata = new("room.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0),
        Points = Enumerable.Range(0, 128).Select(i => new ScanPoint(i * .02f, 3, i % 8 * .1f, 1)).ToArray(),
        Colors = Enumerable.Repeat(0x8899AAu, 128).ToArray()
    }.ToSession());

    private static SceneUnderstandingDocument Document(ScanProject project) => new(1, SceneUnderstandingService.ModelName,
        DateTimeOffset.UtcNow, StructureProvenance.CaptureInputs(project), SceneUnderstandingService.ColorsHash(project),
        Enumerable.Range(0, 128).Select(i => new ScenePoint(new(project.ActiveObjectId, i), SceneClass.Wall, .8f, 1)).ToArray(),
        [new(1, SceneClass.Wall, 128, SceneClass.Cabinet)]);

    [Fact]
    public void SourceIdentitySurvivesPointOrderAndScaniverseExportFormat()
    {
        var first = Project();
        var reverse = first.Replace(first.Active! with { Source = new ImportedScan { Metadata = first.Active!.Source.Metadata, Points = first.Active.Source.Points.Reverse().ToArray(), Colors = first.Active.Source.Colors } });
        Assert.Equal(PersonCaptureIdentity.Capture(first), PersonCaptureIdentity.Capture(reverse));
        ScanProject Named(string name) => first.Replace(first.Active! with
        { Source = new ImportedScan { Metadata = first.Active!.Source.Metadata with { FileName = name }, Points = first.Active.Source.Points, Colors = first.Active.Source.Colors } });
        Assert.Equal(PersonCaptureIdentity.Capture(Named("Scaniverse 2026-11-09 152547.ply")),
            PersonCaptureIdentity.Capture(Named("Scaniverse 2026-11-09 152547.glb")));
        Assert.NotEqual(PersonCaptureIdentity.Capture(Named("Scaniverse 2026-11-09 152547.ply"))[0],
            PersonCaptureIdentity.Capture(Named("Scaniverse 2026-11-10 152547.ply"))[0]);
    }

    [Fact]
    public void CaptureMergePersistsAndLegacyReviewsRequireNewProvenance()
    {
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "examples.json");
            var learning = new PersonCandidateLearning(path);
            var image = new ScanImage(new(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ), 416, new byte[416 * 416 * 3], new float[416 * 416], []);
            var first = new PersonFinding(1, 0, new(10, 10, 50, 150, .8f), null, null);
            var second = new PersonFinding(2, 0, new(60, 10, 100, 150, .5f), null, null);
            learning.Review(image, first, StructureReviewState.Confirmed, [new('A', 64)]);
            learning.Review(image, second, StructureReviewState.Rejected, [new('B', 64)]);
            Assert.Equal(2, learning.SourceCount);
            learning.MergeSources([new('A', 64), new('B', 64)], new('C', 64));
            learning.Reload(); Assert.Equal(1, learning.SourceCount);
            // Reopening an older project or undoing its capture metadata must not undo the dataset's grouping.
            learning.Review(image, first, StructureReviewState.Confirmed, [new('A', 64)]);
            learning.Reload(); Assert.Equal(1, learning.SourceCount);
            var data = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.All(data["Examples"]!.AsArray(), e => Assert.Equal(new string('C', 64), e!["Sources"]![0]!.GetValue<string>()));
            data["Version"] = 2; File.WriteAllText(path, data.ToJsonString());
            learning.Reload();
            Assert.Equal(1, learning.Positives); Assert.Equal(1, learning.Negatives);
            Assert.Equal(0, learning.SourceCount); Assert.Equal(0, learning.TaggedPositives);
            learning.Review(image, first, StructureReviewState.Confirmed, [new('C', 64)]);
            learning.Reload(); Assert.Equal(1, learning.TaggedPositives); Assert.Equal(0, learning.TaggedNegatives);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NeutralLearningRegroupsAfterRemovingWeakCandidates()
    {
        static PersonFinding At(int id, int view, float x, float score) => new(id, view, new(10, 10, 50, 150, score), new(x, 1, 0), new(x + .5f, 1.5f, 1.7f));
        var first = At(1, 0, .3f, .8f); var weak = At(2, 0, 0, .2f); var second = At(3, 1, .1f, .8f);
        var image = new ScanImage(new(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ), 416, new byte[416 * 416 * 3], new float[416 * 416], []);
        var result = new PersonDetectionResult([image, image], PersonScanAnalyzer.GroupAcrossViews([first, weak, second]), "test");
        var model = new PersonTrainingRun(Guid.NewGuid(), DateTimeOffset.UtcNow, new double[9], new double[21], 6, 0, .3f, null, null);
        var corrected = new PersonCandidateLearning().Apply(result, .3f, model, default);
        Assert.Equal(new[] { 1, 3 }, corrected.Findings.Select(f => f.Id));
        Assert.Single(corrected.Findings.Select(f => f.SpatialGroup).Distinct());
    }

    [Fact]
    public async Task UnrelatedFiniteWallKeepsPeopleDepthAndObjectPoints()
    {
        var points = (from x in Enumerable.Range(-20, 41) from z in Enumerable.Range(0, 86) select new ScanPoint(10 + x * .02f, 3, z * .02f, 1)).ToArray();
        var project = Project();
        project = project.Replace(project.Active! with { Source = new ImportedScan { Metadata = project.Active!.Source.Metadata, Points = points, Colors = Enumerable.Repeat(0x906030u, points.Length).ToArray() } });
        var wall = new StructureSurface(1, StructureSurfaceKind.Wall, Vector3.UnitY, -3, new(1, 3, 1),
            [new(0, 3, 0), new(2, 3, 0), new(2, 3, 2), new(0, 3, 2)], 4, 0, [], StructureReviewState.Confirmed);
        var result = await new PersonScanAnalyzer(() => new FixedDetector()).AnalyzeAsync(project,
            [new(new(10, 0, .85f), Vector3.UnitY, Vector3.UnitZ)], [wall], .3f, null, null, default);
        Assert.NotNull(Assert.Single(result.Findings).Minimum);
        var document = new StructureAnalysisDocument(1, StructureAnalysisResult.Algorithm, DateTimeOffset.UtcNow, [], new([wall], points.Length, points.Length, new()));
        Assert.Single(PointObjectAnalyzer.Analyze(project, document, new()));
    }

    [Fact]
    public void SplittingAnObjectUpdatesItsBounds()
    {
        var project = Project(); var obj = project.Active!;
        var candidate = new TunnelObjectCandidate(Guid.NewGuid(), "group", new(0, 3, 0), new(2.54f, 3, .7f),
            Enumerable.Range(0, obj.PointCount).Select(i => new TunnelPointLabel(obj.Id, i, TunnelPointClass.Unknown)).ToArray(), TunnelPointClass.Equipment);
        var recognition = new TunnelRecognition([], [candidate], StructureProvenance.CaptureInputs(project), new('A', 64), obj.PointCount);
        var result = TunnelRecognitionService.AddBox(project, recognition, new(new(-1, 2, -1), new(1, 4, 2)));
        var remaining = result.Objects.Single(o => o.Id == candidate.Id);
        Assert.Equal(remaining.Points.Min(p => obj.Source.Points[p.PointIndex].X), remaining.Minimum.X);
        Assert.True(remaining.Minimum.X > 1);
    }

    [Fact]
    public async Task SceneLabelsAndOperatorClassesSurviveSaveAndLoad()
    {
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        try
        {
            var project = Project(); project = project with { SceneUnderstanding = Document(project) };
            var path = Path.Combine(directory, "scene.subterra"); var storage = new SessionStorage();
            await storage.SaveAsync(path, project.ToSession(), 0);
            var restored = (await storage.LoadAsync(path)).Project!;
            Assert.False(restored.SceneUnderstanding!.IsStale);
            Assert.Equal(project.SceneUnderstanding.Points, restored.SceneUnderstanding.Points);
            Assert.Equal(SceneClass.Cabinet, Assert.Single(restored.SceneUnderstanding.Regions).ReviewedClass);
            Assert.Equal(project.Active!.Source.Points, restored.Active!.Source.Points);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SceneLabelsBecomeStaleAfterGeometryOrRgbChangesAndRejectBadReferences()
    {
        var project = Project(); var document = Document(project); var obj = project.Active!;
        Assert.True(SceneUnderstandingService.Refresh(project.Replace(obj with { Transform = new(1) }), document).IsStale);
        Assert.True(SceneUnderstandingService.Refresh(project.Replace(obj with { Source = new ImportedScan { Metadata = obj.Source.Metadata, Points = obj.Source.Points, Colors = Enumerable.Repeat(0xFF0000u, 128).ToArray() } }), document).IsStale);
        Assert.Throws<InvalidDataException>(() => SceneUnderstandingService.Validate(project, document with
        { Points = [document.Points[0] with { Source = new(obj.Id, 999) }] }));
        Assert.Throws<InvalidDataException>(() => SceneUnderstandingService.Validate(project, document with
        { Points = document.Points.Select((p, i) => i == 0 ? p with { Score = float.NaN } : p).ToArray() }));
        Assert.Throws<InvalidDataException>(() => SceneUnderstandingService.Validate(project, document with
        { Inputs = [document.Inputs[0] with { Transform = new(float.NaN) }], IsStale = true }));
        Assert.Throws<InvalidDataException>(() => SceneUnderstandingService.Validate(project, document with
        { Inputs = [document.Inputs[0] with { PointsHash = "not-a-hash" }], IsStale = true }));
    }

    [Fact]
    public Task ReviewingSceneClassesSupportsUndoAndGuestAccess() => ScanControlTests.OnSta(async () =>
    {
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        try
        {
            var project = Project(); project = project with { SceneUnderstanding = Document(project) };
            var path = Path.Combine(directory, "scene.subterra"); var storage = new SessionStorage();
            await storage.SaveAsync(path, project.ToSession(), 0);
            var access = new TestAccess();
            using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), storage, access: access, interaction: new TestInteraction());
            await vm.OpenAsync(path);
            Assert.True(vm.HasSceneUnderstanding); Assert.True(vm.SceneUnderstandingVisible);
            Assert.NotNull(vm.SceneUnderstandingGeometry);
            vm.SelectedSceneClass = vm.SceneClasses.Single(c => c.Kind == SceneClass.Floor);
            vm.ConfirmSceneRegionCommand.Execute(null);
            Assert.Equal(SceneClass.Floor, Assert.Single(vm.SceneRegions).ReviewedClass);
            await vm.UndoProjectCommand.ExecuteAsync();
            Assert.Equal(SceneClass.Cabinet, Assert.Single(vm.SceneRegions).ReviewedClass);
            vm.SelectSectionCommand.Execute("Анализ");
            var panel = new StructureAnalysisPanel { DataContext = vm };
            var root = new Border { Child = panel, Padding = new Thickness(16), Background = new SolidColorBrush(Color.FromRgb(11, 18, 25)) };
            System.Windows.Documents.TextElement.SetForeground(root, Brushes.White);
            ThemeRenderingTests.Layout(root, 320, 1000);
            var expander = Visuals(panel).OfType<Expander>().Single(e => Equals(e.Header, "ИИ-разметка сцены"));
            Assert.False(expander.IsExpanded); expander.IsExpanded = true;
            ThemeRenderingTests.Layout(root, 320, 1000);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            root.UpdateLayout(); ThemeRenderingTests.CheckReadableText(root);
            Assert.Contains(Visuals(panel).OfType<TextBlock>(), t => t.Text == vm.SelectedSceneRegion!.Title && t.ActualHeight > 0);
            foreach (var element in Visuals(panel))
            {
                var values = element.GetLocalValueEnumerator();
                while (values.MoveNext())
                    if (BindingOperations.GetBindingExpressionBase(element, values.Current.Property) is { } binding)
                        Assert.False(binding.HasError, $"{element.GetType().Name}.{values.Current.Property.Name}");
            }
            var preview = Environment.GetEnvironmentVariable("SUBTERRA_UI_PREVIEW_DIR");
            if (!string.IsNullOrWhiteSpace(preview))
            {
                Directory.CreateDirectory(preview);
                var bitmap = new RenderTargetBitmap(480, 1500, 144, 144, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(preview, "scene-ai-panel.png")); encoder.Save(stream);
            }
            access.Logout();
            Assert.False(vm.ConfirmSceneRegionCommand.CanExecute(null));
            Assert.False(vm.AnalyzeSceneCommand.CanExecute(null));
        }
        finally { Directory.Delete(directory, true); }
    });

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class FixedDetector : IPersonImageDetector
    {
        public ImageBox[] Detect(ScanImage image, float threshold, CancellationToken token) => [new(160, 120, 256, 300, .8f)];
        public void Dispose() { }
    }
}
