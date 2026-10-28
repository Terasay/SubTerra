using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class ScanImportTests
{
    [Fact]
    public async Task AscConvertsMillimetersAndYAxisWithoutLosingLargeCoordinateOffsets()
    {
        using var files = new Files();
        var path = files.Write("scan.asc", "# XYZ plus normals\n1000000000 2000000000 3000000000 0 1 0\n1000000001 2000002000 3000003000 0 1 0\n");
        var scan = await new ScanImporter().ImportAsync(path, new(ScanUnit.Millimeters, ScanUpAxis.Y));
        Assert.Equal(Vector3.Zero, scan.Points[0].Position);
        Assert.Equal(new Vector3(.001f, -3, 2), scan.Points[1].Position);
        Assert.Equal(1e9, scan.Metadata.OriginX);
        Assert.Equal(2e9, scan.Metadata.OriginY);
        Assert.Equal(3e9, scan.Metadata.OriginZ);
        Assert.Empty(scan.Colors); // The six-column ASC normals are never interpreted as RGB.
        Assert.Empty(scan.Triangles);
    }

    [Theory]
    [InlineData(ScanUnit.Meters, 1f)]
    [InlineData(ScanUnit.Centimeters, .01f)]
    [InlineData(ScanUnit.Millimeters, .001f)]
    public async Task ExplicitUnitsAndRgbDoNotDependOnWindowsCulture(ScanUnit unit, float scale)
    {
        using var files = new Files();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var scan = await new ScanImporter().ImportAsync(files.Write("scan.xyz", "1.5;2;3;255;128;0"), new(unit, LocalOrigin: false, TextLayout: TextPointLayout.XyzRgb));
            Assert.Equal(new Vector3(1.5f * scale, 2 * scale, 3 * scale), scan.Points[0].Position);
            Assert.Equal(0xff8000u, Assert.Single(scan.Colors));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private const string ColoredPly = """
        ply
        format ascii 1.0
        comment arbitrary property order
        element vertex 4
        property uchar blue
        property float z
        property double x
        property uchar red
        property float nx
        property double y
        property uchar green
        element face 1
        property list uchar int vertex_indices
        end_header
        0 2 0 255 0 0 0
        0 2 1 0 0 0 255
        255 2 1 0 0 1 0
        255 2 0 255 0 1 255
        4 0 1 2 3
        """;

    [Fact]
    public async Task DevokNineColumnAscReadsNormalizedRgbAfterNormals()
    {
        using var files = new Files();
        var scan = await new ScanImporter().ImportAsync(files.Write("devok.asc", "10 20 30 0 1 0 1 0.5 0"), new(TextLayout: TextPointLayout.XyzNormalRgb));
        Assert.Equal(0xff8000u, Assert.Single(scan.Colors));
        Assert.Equal(Vector3.Zero, Assert.Single(scan.Points).Position);
    }

    [Fact]
    public async Task AsciiPlyPreservesColorAndTriangulatesFacesWithReorderedProperties()
    {
        using var files = new Files();
        var scan = await new ScanImporter().ImportAsync(files.Write("mesh.ply", ColoredPly), new(ScanUnit.Meters, LocalOrigin: false));
        Assert.Equal(4, scan.Points.Length);
        Assert.Equal(new Vector3(1, 1, 2), scan.Points[2].Position);
        Assert.Equal(new uint[] { 0xff0000, 0x00ff00, 0x0000ff, 0xffffff }, scan.Colors);
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, scan.Triangles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BinaryPlySupportsBothByteOrdersAndDoubleCoordinates(bool bigEndian)
    {
        using var files = new Files();
        var path = Path.Combine(files.Directory, "binary.ply");
        using (var stream = File.Create(path))
        {
            var header = Encoding.ASCII.GetBytes($"ply\nformat binary_{(bigEndian ? "big" : "little")}_endian 1.0\nelement vertex 1\nproperty double x\nproperty double y\nproperty double z\nproperty uchar red\nproperty uchar green\nproperty uchar blue\nend_header\n");
            stream.Write(header);
            foreach (var coordinate in new[] { 12.5, -8.25, 3.75 })
            {
                var bytes = BitConverter.GetBytes(coordinate);
                if (bigEndian) Array.Reverse(bytes);
                stream.Write(bytes);
            }
            stream.Write([10, 20, 30]);
        }
        var scan = await new ScanImporter().ImportAsync(path, new(ScanUnit.Meters, LocalOrigin: false));
        Assert.Equal(new Vector3(12.5f, -8.25f, 3.75f), Assert.Single(scan.Points).Position);
        Assert.Equal(0x0a141eu, Assert.Single(scan.Colors));
    }

    [Theory]
    [InlineData("NaN 1 2")]
    [InlineData("1 2")]
    [InlineData("x y z")]
    [InlineData("Infinity 1 2")]
    [InlineData("")]
    [InlineData("1e20 0 0")]
    public async Task InvalidTextScanIsRejected(string content)
    {
        using var files = new Files();
        await Assert.ThrowsAsync<InvalidDataException>(() => new ScanImporter().ImportAsync(files.Write("bad.asc", content), new()));
    }

    [Theory]
    [InlineData("element vertex 5000001\nproperty float x\nproperty float y\nproperty float z\nend_header\n")]
    [InlineData("element vertex 1\nproperty float x\nproperty float y\nend_header\n0 0")]
    [InlineData("element vertex 1\nproperty float x\nproperty float x\nproperty float z\nend_header\n0 0 0")]
    [InlineData("element vertex 1\nproperty float x\nproperty float y\nproperty float z\nelement face 1\nproperty list uchar int vertex_indices\nend_header\n0 0 0\n3 0 1 0")]
    [InlineData("element vertex 1\nproperty float x\nproperty float y\nproperty float z\nproperty uchar red\nproperty uchar green\nproperty uchar blue\nend_header\n0 0 0 256 0 0")]
    public async Task MalformedOrOversizedPlyIsRejectedBeforeItCanBecomeASession(string body)
    {
        using var files = new Files();
        await Assert.ThrowsAsync<InvalidDataException>(() => new ScanImporter().ImportAsync(files.Write("bad.ply", "ply\nformat ascii 1.0\n" + body), new()));
    }

    [Fact]
    public async Task SessionAndBinaryPlyRoundTripsRetainAllDataAndImportMetadata()
    {
        using var files = new Files();
        var importer = new ScanImporter(); var storage = new SessionStorage();
        var scan = await importer.ImportAsync(files.Write("mesh.ply", ColoredPly), new(ScanUnit.Centimeters));
        var path = Path.Combine(files.Directory, "scan.subterra");
        await storage.SaveAsync(path, scan.ToSession([new("Угол", new(0, 0, 0))]), 1);
        var restored = await storage.LoadAsync(path);
        Assert.Empty(restored.Frames);
        Assert.Equal("Угол", Assert.Single(restored.Annotations).Name);
        Assert.Equal(scan.Metadata, restored.ImportedScan!.Metadata);
        Assert.Equal(scan.Points, restored.ImportedScan.Points);
        Assert.Equal(scan.Colors, restored.ImportedScan.Colors);
        Assert.Equal(scan.Triangles, restored.ImportedScan.Triangles);
        var ply = Path.Combine(files.Directory, "export.ply");
        await storage.ExportImportedPlyAsync(ply, restored.ImportedScan);
        var exported = await importer.ImportAsync(ply, new(ScanUnit.Meters, LocalOrigin: false));
        Assert.Equal(scan.Points, exported.Points);
        Assert.Equal(scan.Colors, exported.Colors);
        Assert.Equal(scan.Triangles, exported.Triangles);
    }

    [Fact]
    public async Task CancellationDuringImportExportAndSaveLeavesPreviousFilesUntouched()
    {
        using var files = new Files();
        var scan = new ImportedScan { Metadata = new("test.asc", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0),
            Points = Enumerable.Range(0, 10000).Select(i => new ScanPoint(i, 0, 0, 1)).ToArray() };
        var storage = new SessionStorage();
        foreach (var export in new[] { false, true })
        {
            var path = files.Write(export ? "kept.ply" : "kept.subterra", "previous content");
            using var cancellation = new CancellationTokenSource();
            var progress = new DirectProgress(value => { if (value > 0) cancellation.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export
                ? storage.ExportImportedPlyAsync(path, scan, cancellation.Token, progress)
                : storage.SaveAsync(path, scan.ToSession(), 1, cancellation.Token, progress));
            Assert.Equal("previous content", await File.ReadAllTextAsync(path));
            Assert.Empty(System.IO.Directory.GetFiles(files.Directory, "*.tmp"));
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ScanImporter().ImportAsync("missing.ply", new(), canceled.Token));
    }

    [Fact]
    public void DisplaySamplingAndClipNeverRemoveOriginalPointsOrApplyCaveRoofRules()
    {
        var points = Enumerable.Range(0, 10000).Select(i => new ScanPoint(i / 100f, i % 10, 20, 1)).ToArray();
        var scan = new ImportedScan { Metadata = new("test.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0), Points = points };
        var geometry = new ImportedGeometry(scan);
        var full = geometry.Cloud(500, null);
        Assert.Equal(500, full.Positions!.Count);
        var plane = new PointClipPlane(Vector3.UnitX, 10);
        var clipped = geometry.Cloud(500, plane);
        Assert.Equal(500, clipped.Positions!.Count);
        Assert.All(clipped.Positions, p => Assert.True(plane.Contains(p)));
        Assert.Same(full, geometry.Cloud(500, null));
        Assert.Equal(10000, scan.Points.Length);
        Assert.Empty(geometry.Cloud(500, new(Vector3.UnitZ, -1)).Positions!);
    }

    [Fact]
    public Task RealScanCanBeMeasuredSavedRecoveredAndSwitchedBackToDemo() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files();
        var storage = new SessionStorage();
        var workspace = new WorkspaceStateStore(Path.Combine(files.Directory, "workspace"));
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), storage, access: new TestAccess(),
            interaction: new TestInteraction(), workspaceStore: workspace);
        await vm.LoadDemoAsync();
        await vm.ImportAsync(files.Write("model.asc", "0 0 0\n10 0 0\n0 20 5\n"), new());
        Assert.False(vm.HasNotification, vm.Notification);
        Assert.True(vm.IsImported); Assert.Null(vm.Route); Assert.Null(vm.Scanner);
        Assert.True(vm.IsPoints); Assert.False(vm.CanWalk); Assert.True(vm.CanFly);
        Assert.False(vm.CanStart); Assert.False(vm.CanEditSettings); Assert.False(vm.CanRepositionScanner);
        Assert.Equal("3", vm.PointCountText);
        Assert.Equal(3, vm.RenderedPointCount);
        Assert.InRange(vm.Camera.NearPlaneDistance, .000001, .0001);
        var flight = vm.CreateFlyNavigator();
        Assert.InRange(flight.Speed, .001f, .1f);
        var originalPosition = flight.Position; flight.PlaceAt(.5f); Assert.Equal(originalPosition, flight.Position);
        var orbitDistance = vm.Camera.LookDirection.Length;
        vm.FlyCommand.Execute(null); vm.UpdateWalkPosition(vm.CreateFlyNavigator()); vm.ReturnToOverview();
        Assert.InRange(Math.Abs(vm.Camera.LookDirection.Length - orbitDistance), 0, .00001);
        vm.SurfaceCommand.Execute(null); Assert.True(vm.IsPoints);
        vm.HeightCommand.Execute(null); Assert.True(vm.IsPoints); Assert.True(vm.IsHeight);
        vm.PointsCommand.Execute(null); Assert.True(vm.IsPointMode);
        vm.BeginInspection(true); vm.SelectInspectionPoint(Vector3.Zero); vm.SelectInspectionPoint(new(.01f, 0, 0));
        vm.AnnotationName = "Край"; vm.AddAnnotationCommand.Execute(null);
        Assert.Contains("10,00", vm.MeasurementText.Replace('.', ','));
        await vm.AutoSaveAsync(); Assert.True(workspace.HasRecovery);
        using (var restored = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), storage, access: new TestAccess(),
                   interaction: new TestInteraction(), workspaceStore: workspace))
        {
            await restored.RestoreRecoveryAsync();
            Assert.False(restored.HasNotification, restored.Notification);
            Assert.True(restored.IsImported); Assert.Single(restored.UserAnnotations); Assert.True(restored.HasUnsavedChanges);
        }
        var saved = Path.Combine(files.Directory, "saved.subterra");
        Assert.True(await vm.SaveAsync(saved));
        await vm.LoadDemoAsync(); Assert.True(vm.IsSimulation); Assert.True(vm.CanStart);
        await vm.OpenAsync(saved); Assert.True(vm.IsImported); Assert.Single(vm.UserAnnotations);
        Assert.Equal("3", vm.PointCountText); Assert.False(vm.CanStart);
    });

    [Fact]
    public Task InvalidImportKeepsCurrentSessionAndUnsavedAnnotations() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files();
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: new TestAccess(), interaction: new TestInteraction());
        await vm.ImportAsync(files.Write("good.ply", ColoredPly), new(ScanUnit.Meters));
        vm.BeginInspection(false); vm.SelectInspectionPoint(Vector3.Zero); vm.AnnotationName = "Сохранить"; vm.AddAnnotationCommand.Execute(null);
        var points = vm.PointCountText;
        await vm.ImportAsync(files.Write("bad.asc", "bad data"), new());
        Assert.True(vm.HasNotification); Assert.True(vm.IsImported); Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(points, vm.PointCountText); Assert.Single(vm.UserAnnotations);
    });

    private sealed class DirectProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    private sealed class Files : IDisposable
    {
        public string Directory { get; } = WorkspaceFeatureTests.TemporaryDirectory();
        public string Write(string name, string content) { var path = Path.Combine(Directory, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
