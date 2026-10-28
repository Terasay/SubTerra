using System.IO;
using System.Numerics;
using SubTerra.Models;
using SubTerra.Services;
using Xunit;

namespace SubTerra.Tests;

public sealed class StructureAnalysisTests
{
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
