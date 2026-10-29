using System.IO;
using System.Numerics;
using SubTerra.Models;
using SubTerra.Services;
using SubTerra.Simulation;
using Xunit;

namespace SubTerra.Tests;

public sealed class StructureAnalysisTests
{
    [Theory]
    [InlineData(1, 0, .01, 1, StructureSurfaceKind.Floor)]
    [InlineData(1, 1, .01, 1, StructureSurfaceKind.Ceiling)]
    [InlineData(.01, .5, 1, .2, StructureSurfaceKind.Wall)]
    [InlineData(1, .27, .01, .1, StructureSurfaceKind.Unknown)]
    [InlineData(.01, .5, .3, .1, StructureSurfaceKind.Unknown)]
    public void SyntheticNeuralModelDistinguishesStructureFromPartialPatches(double normal, double height, double span, double area, StructureSurfaceKind expected)
    {
        var result = RoomNeuralClassifier.Predict(normal, height, span, area);
        Assert.Equal(expected, result.Kind); Assert.InRange(result.Score!.Value, .9, 1);
        Assert.Null(RoomNeuralClassifier.Predict(double.NaN, height, span, area).Score);
        Assert.Null(RoomNeuralClassifier.Predict(normal, 2, span, area).Score);
    }

    [Fact]
    public async Task NeuralResultsAndObservedSurfacePreviewPreserveSourcesAndDoorGaps()
    {
        var project = CollegeBuildingFactory.Create();
        project = project with { Objects = project.Objects.Select((o, i) => o with { IsVisible = i == 0 }).ToArray() };
        var original = project.Active!.Source.Points.ToArray();
        var result = await Analyze(project, new(UseNeuralClasses: true));
        var document = StructureProvenance.Create(project, result);
        Assert.Equal(StructureAnalysisResult.NeuralAlgorithm, document.Algorithm);
        Assert.Contains(result.Surfaces, s => s.Kind == StructureSurfaceKind.Floor && s.NeuralScore >= .9);
        var preview = SubTerra.Rendering.IndoorSurfaceBuilder.Build(project, document);
        Assert.NotEmpty(preview.FloorTriangles); Assert.True(preview.Scan.HasMesh);
        Assert.Equal(preview.Scan.Points.Length, preview.MaterialColors.Length);
        var index = new SubTerra.Simulation.TriangleIndex(preview.Scan.Points.Select(p => p.Position).ToArray(), preview.Scan.Triangles);
        Assert.False(index.Cast(new(3, 6, 1), -Vector3.UnitY, 2, out _));
        Assert.True(index.Cast(new(1, 6, 1), -Vector3.UnitY, 2, out _));
        Assert.Equal(original, project.Active.Source.Points);
        Assert.ThrowsAny<OperationCanceledException>(() => SubTerra.Rendering.IndoorSurfaceBuilder.Build(project, document, new CancellationToken(true)));
        var changed = project.Replace(project.Active with { Transform = new(1) });
        Assert.Throws<InvalidDataException>(() => SubTerra.Rendering.IndoorSurfaceBuilder.Build(changed, document));
        Assert.Throws<InvalidDataException>(() => StructureProvenance.Validate(document with { Algorithm = StructureAnalysisResult.Algorithm }, project));
        var invalid = result.Surfaces.Select((s, i) => i == 0 ? s with { NeuralScore = 1.1 } : s).ToArray();
        Assert.Throws<InvalidDataException>(() => StructureProvenance.Validate(document with { Result = result with { Surfaces = invalid } }, project));
    }

    [Fact]
    public async Task CollegeClassroomCanBePlannedWithItsDoorOpening()
    {
        var project = CollegeBuildingFactory.Create();
        project = project with { Objects = project.Objects.Select((o, i) => o with { IsVisible = i == 2 }).ToArray() };
        var result = await Analyze(project);
        var doc = StructureProvenance.Create(project, result);
        doc = doc with { Result = result with { Surfaces = doc.Result.Surfaces.Select(s => s with { Review = StructureReviewState.Confirmed }).ToArray() } };
        var wallPoints = new[] { new Vector3(0, 2, 8), new(6, 2, 8), new(3, 0, 8), new(3, 4.5f, 8) };
        var wallIds = wallPoints.Select(p => doc.Result.Surfaces.Where(s => s.Kind == StructureSurfaceKind.Wall)
            .MinBy(s => Math.Abs(Vector3.Dot(s.Normal, p) + s.Offset))!.Id).ToArray();
        Assert.Equal(4, wallIds.Distinct().Count());
        var support = doc.Result.Surfaces.Where(s => s.Kind is StructureSurfaceKind.Floor or StructureSurfaceKind.Ceiling).Select(s => s.Id);
        var built = RoomPlanner.Build(doc, "Аудитория 301", surfaceIds: [.. support, .. wallIds], interiorPoint: new(3, 2, 7.2f));
        var room = Assert.IsType<RoomPlan>(built.Room);
        Assert.InRange(room.AreaSquareMeters, 26.8, 27.2);
        Assert.InRange(room.MinimumHeight!.Value, 3.18, 3.22);
        Assert.Contains(room.Gaps, gap =>
        {
            var a = room.FloorContour[gap.EdgeIndex]; var b = room.FloorContour[(gap.EdgeIndex + 1) % room.FloorContour.Length];
            return Math.Abs(a.Y - 4.5) < .03 && Math.Abs(b.Y - 4.5) < .03;
        });
        StructureProvenance.Validate((doc with { Room = room }).NormalizeVersion(), project, CancellationToken.None);
    }

    [Fact]
    public async Task ObjectGroupingExcludesReviewedPlanesAndKeepsStableSourceReferences()
    {
        var project = await RoomPlannerTests.ReviewedRoom(); var doc = project.Structure!;
        var first = PointObjectAnalyzer.Analyze(project, doc, new());
        var second = PointObjectAnalyzer.Analyze(project, doc, new());
        Assert.NotEmpty(first); Assert.Equal(first.Select(o => o.Id), second.Select(o => o.Id));
        Assert.Equal(first.SelectMany(o => o.Samples), second.SelectMany(o => o.Samples));
        Assert.Equal(first.Sum(o => o.Samples.Length), first.SelectMany(o => o.Samples).Select(s => s.Source).Distinct().Count());
        Assert.All(first.SelectMany(o => o.Samples), sample =>
        {
            Assert.DoesNotContain(doc.Result.Surfaces.Where(s => s.Review == StructureReviewState.Confirmed),
                s => Math.Abs(Vector3.Dot(s.Normal, sample.Position) + s.Offset) <= doc.Result.Options.DistanceTolerance);
            Assert.Equal(project.Active!.Source.Points[sample.Source.PointIndex].Position, sample.Position);
        });
        Assert.True(first.Select(o => o.Color).Distinct().Count() == first.Length);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PointObjectAnalyzer.Analyze(project, doc, new(), cts.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => PointObjectAnalyzer.Analyze(project, doc, new(double.NaN)));
    }

    internal static string ClassroomPath => Path.Combine(AppContext.BaseDirectory, "ScanExamples", "classroom-meters-zup.ply");
    internal static async Task<ScanProject> Classroom() => ScanProject.FromSession(
        (await new ScanImporter().ImportAsync(ClassroomPath, new(ScanUnit.Meters, LocalOrigin: false))).ToSession());
    private static ScanObject Cloud(IEnumerable<Vector3> points) => new(Guid.NewGuid(), "Учебный скан", new()
    {
        Metadata = new("test.asc", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0),
        Points = points.Select(p => new ScanPoint(p.X, p.Y, p.Z, .5f)).ToArray()
    });
    private static ScanProject Project(params ScanObject[] objects) => new(objects, objects.FirstOrDefault()?.Id ?? Guid.Empty, objects.FirstOrDefault()?.Source.Metadata);
    private static Task<StructureAnalysisResult> Analyze(ScanProject project, StructureAnalysisOptions? options = null) =>
        new PlaneStructureAnalyzer().AnalyzeAsync(project, options ?? new(), null, CancellationToken.None);

    [Fact]
    public async Task NoisyClassroomHasSixStructuralPlanesAndAnUnknownTable()
    {
        var project = await Classroom(); var result = await Analyze(project);
        var floor = Assert.Single(result.Surfaces, s => s.Kind == StructureSurfaceKind.Floor);
        var ceiling = Assert.Single(result.Surfaces, s => s.Kind == StructureSurfaceKind.Ceiling);
        Assert.InRange(floor.Center.Z, -.01, .01); Assert.InRange(ceiling.Center.Z, 2.99, 3.01);
        Assert.InRange(floor.AreaSquareMeters, 28, 33); Assert.InRange(ceiling.AreaSquareMeters, 28, 33);
        Assert.Equal(4, result.Surfaces.Count(s => s.Kind == StructureSurfaceKind.Wall));
        var table = Assert.Single(result.Surfaces, s => s.Kind == StructureSurfaceKind.Unknown);
        Assert.InRange(table.Center.Z, .79, .81);
        Assert.All(result.Surfaces, s =>
        {
            Assert.InRange(s.RmsMeters, 0, .01); Assert.InRange(s.Normal.Length(), .9999, 1.0001);
            Assert.True(s.Boundary.Length >= 3);
            Assert.All(s.Boundary, p => Assert.InRange(Math.Abs(Vector3.Dot(s.Normal, p) + s.Offset), 0, .00001));
        });
        var references = result.Surfaces.SelectMany(s => s.Samples).Select(p => p.Source).ToArray();
        Assert.Equal(references.Length, references.Distinct().Count());
        Assert.Equal(project.PointCount, result.SourcePointCount); Assert.Equal(project.PointCount, result.SampleCount);
    }

    [Fact]
    public async Task MissingCeilingDoesNotMakeTheTableACeiling()
    {
        var project = await Classroom(); var o = project.Active!;
        project = project.Replace(o with { Mask = new(o.Source.Points.Select(p => p.Z < 2.95f).ToArray()) });
        var result = await Analyze(project);
        Assert.Contains(result.Surfaces, s => s.Kind == StructureSurfaceKind.Floor);
        Assert.DoesNotContain(result.Surfaces, s => s.Kind == StructureSurfaceKind.Ceiling);
        Assert.Contains(result.Surfaces, s => s.Kind == StructureSurfaceKind.Unknown && Math.Abs(s.Center.Z - .8) < .01);
    }

    [Fact]
    public async Task TabletopAloneAndACollinearScanDoNotInventARoom()
    {
        var table = Cloud(from x in Enumerable.Range(0, 21) from y in Enumerable.Range(0, 11) select new Vector3(x * .1f, y * .1f, .8f));
        var result = await Analyze(Project(table));
        Assert.Equal(StructureSurfaceKind.Unknown, Assert.Single(result.Surfaces).Kind);
        var line = Cloud(Enumerable.Range(0, 300).Select(i => new Vector3(i * .01f, 0, 0)));
        Assert.Empty((await Analyze(Project(line))).Surfaces);
        Assert.Empty((await Analyze(Project())).Surfaces);
    }

    [Fact]
    public async Task MaskVisibilityAndTransformsKeepReferencesToOriginalPoints()
    {
        var project = await Classroom(); var source = project.Active!;
        var flags = source.Source.Points.Select((_, i) => i % 3 != 0).ToArray();
        var active = source with { Mask = new(flags), Transform = new(12, -7, 4, RotateZ: 35) };
        var hidden = Cloud([new(50, 50, 50)]) with { IsVisible = false };
        var original = (ScanPoint[])source.Source.Points.Clone();
        var result = await Analyze(Project(active, hidden));
        Assert.Equal(flags.Count(v => v), result.SourcePointCount);
        Assert.Contains(result.Surfaces, s => s.Kind == StructureSurfaceKind.Floor && Math.Abs(s.Center.Z - 4) < .01);
        Assert.Contains(result.Surfaces, s => s.Kind == StructureSurfaceKind.Ceiling && Math.Abs(s.Center.Z - 7) < .01);
        Assert.All(result.Surfaces.SelectMany(s => s.Samples), sample =>
        {
            Assert.Equal(source.Id, sample.Source.ObjectId); Assert.True(flags[sample.Source.PointIndex]);
            Assert.Equal(Vector3.Transform(original[sample.Source.PointIndex].Position, active.Matrix), sample.Position);
        });
        Assert.Equal(original, source.Source.Points); Assert.Equal(flags, active.Mask!.Copy()); Assert.Null(source.Mask);
    }

    [Fact]
    public async Task BoundedSamplingIsDeterministicAcrossMultipleSourceObjects()
    {
        var project = await Classroom(); var a = project.Active!;
        var b = a with { Id = Guid.NewGuid(), Transform = new(8) };
        var options = new StructureAnalysisOptions(MaximumSamples: 1000);
        var first = await Analyze(Project(a, b), options); var second = await Analyze(Project(a, b), options);
        Assert.Equal(1000, first.SampleCount); Assert.Equal(a.PointCount * 2, first.SourcePointCount);
        Assert.NotEmpty(first.Surfaces);
        Assert.Equal(first.Surfaces.SelectMany(s => s.Samples).ToArray(), second.Surfaces.SelectMany(s => s.Samples).ToArray());
        Assert.Equal(first.Surfaces.Select(s => (s.Kind, s.Normal, s.Offset, s.AreaSquareMeters)),
            second.Surfaces.Select(s => (s.Kind, s.Normal, s.Offset, s.AreaSquareMeters)));
        Assert.All(first.Surfaces.SelectMany(s => s.Samples), p => Assert.Contains(p.Source.ObjectId, new[] { a.Id, b.Id }));
    }

    [Fact]
    public async Task LeastSquaresFitRecoversAnObliquePlaneInWorldCoordinates()
    {
        var random = new Random(77);
        var plane = Cloud(from x in Enumerable.Range(0, 31) from y in Enumerable.Range(0, 21)
            select new Vector3(x * .1f, y * .1f, .3f * x * .1f + .2f * y * .1f + (float)(random.NextDouble() - .5) * .004f));
        var s = Assert.Single((await Analyze(Project(plane))).Surfaces);
        Assert.Equal(StructureSurfaceKind.Unknown, s.Kind);
        Assert.InRange(Vector3.Dot(s.Normal, Vector3.Normalize(new(-.3f, -.2f, 1))), .9999, 1.0001);
        Assert.InRange(s.AreaSquareMeters, 6.3, 6.5); Assert.InRange(s.RmsMeters, 0, .002);
    }

    [Fact]
    public async Task CancellationBeforeAndDuringAnalysisLeavesSourcesUntouched()
    {
        var project = await Classroom(); using var cts = new CancellationTokenSource();
        var progress = new ImmediateProgress(value => { if (value >= 5) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PlaneStructureAnalyzer().AnalyzeAsync(project, new(), progress, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PlaneStructureAnalyzer().AnalyzeAsync(project, new(), null, cts.Token));
        Assert.Null(project.Active!.Mask); Assert.Equal(3747, project.PointCount);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(.2)]
    public async Task InvalidToleranceIsRejected(double tolerance) => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
        Analyze(Project(Cloud([Vector3.Zero])), new(DistanceTolerance: tolerance)));

    private sealed class ImmediateProgress(Action<double> report) : IProgress<double>
    { public void Report(double value) => report(value); }
}
