using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Buffers.Binary;
using System.IO.Compression;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class ScanImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SubterraExportsReopenAtTheirOriginalMeterScale(bool importedMesh) => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); var path = Path.Combine(files.Directory, "export.ply");
        ScanPoint[] points = [new(0, 0, 0, .5f), new(12, 0, 0, .6f), new(12, 8, 3, .7f)];
        var storage = new SessionStorage();
        if (importedMesh)
            await storage.ExportImportedPlyAsync(path, new ImportedScan
            {
                Metadata = new("model.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0) { IsSynthetic = true },
                Points = points, Colors = [0xFF0000, 0x00FF00, 0x0000FF], Triangles = [0, 1, 2]
            });
        else await storage.ExportPlyAsync(path, points);
        var dialog = new SubTerra.Views.ScanImportWindow(path);
        Assert.Equal(ScanUnit.Meters, dialog.Options.Unit); Assert.Equal(ScanUpAxis.Z, dialog.Options.UpAxis);
        var restored = await new ScanImporter().ImportAsync(path, dialog.Options);
        Assert.Equal(points, restored.Points); Assert.Equal(new Vector3(12, 8, 3), restored.Bounds.Maximum);
        Assert.Equal(importedMesh, restored.HasMesh);
    });

    [Fact]
    public Task ScaniverseHeaderPresetsMetersWithoutGuessingFromTheFileName() => ScanControlTests.OnSta(() =>
    {
        using var files = new Files();
        var identified = files.Write("room.ply", "ply\nformat ascii 1.0\ncomment Created with Scaniverse - https://scaniverse.com\nelement vertex 0\nend_header\n");
        var dialog = new SubTerra.Views.ScanImportWindow(identified);
        Assert.Equal(ScanUnit.Meters, dialog.Options.Unit);
        var ordinary = files.Write("Scaniverse.ply", "ply\nformat ascii 1.0\nelement vertex 0\nend_header\n");
        Assert.Equal(ScanUnit.Millimeters, new SubTerra.Views.ScanImportWindow(ordinary).Options.Unit);
        return Task.CompletedTask;
    });

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
    public async Task ConcavePlyFacesDoNotFillTheOpenPartOfAShape(bool reverse)
    {
        using var files = new Files();
        var vertices = "0 0 0\n3 0 0\n3 3 0\n2 3 0\n2 1 0\n1 1 0\n1 3 0\n0 3 0\n";
        var indices = reverse ? "7 6 5 4 3 2 1 0" : "0 1 2 3 4 5 6 7";
        var content = "ply\nformat ascii 1.0\nelement vertex 8\nproperty float x\nproperty float y\nproperty float z\nelement face 1\nproperty list uchar int vertex_indices\nend_header\n" + vertices + "8 " + indices;
        var scan = await new ScanImporter().ImportAsync(files.Write("concave.ply", content), new(ScanUnit.Meters));
        Assert.Equal(18, scan.Triangles.Length);
        double area = 0;
        for (var i = 0; i < scan.Triangles.Length; i += 3)
        {
            var a = scan.Points[scan.Triangles[i]].Position; var b = scan.Points[scan.Triangles[i + 1]].Position; var c = scan.Points[scan.Triangles[i + 2]].Position;
            var cross = Vector3.Cross(b - a, c - a);
            Assert.Equal(reverse ? -1 : 1, Math.Sign(cross.Z));
            area += cross.Length() * .5;
        }
        Assert.Equal(7, area, 6);
    }

    [Fact]
    public async Task ShippedExamplesHaveTheDocumentedSizeColorsAndTopology()
    {
        var importer = new ScanImporter();
        var mesh = await importer.ImportAsync(Path.Combine(AppContext.BaseDirectory, "ScanExamples", "block-100x60x40-mm.ply"), new());
        var cloud = await importer.ImportAsync(Path.Combine(AppContext.BaseDirectory, "ScanExamples", "points-with-normals-and-rgb.asc"), new(TextLayout: TextPointLayout.XyzNormalRgb));
        Assert.Equal(new Vector3(.1f, .06f, .04f), mesh.Bounds.Maximum - mesh.Bounds.Minimum);
        Assert.Equal(8, mesh.Points.Length); Assert.Equal(36, mesh.Triangles.Length); Assert.Equal(8, mesh.Colors.Length);
        Assert.Equal(mesh.Points, cloud.Points); Assert.Equal(8, cloud.Colors.Length); Assert.Empty(cloud.Triangles);
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
    public async Task MillionPointImportAndSessionAreIndependentOfSimulationFrameLimits()
    {
        using var files = new Files();
        const int count = 1_000_000;
        var path = Path.Combine(files.Directory, "million.ply");
        using (var stream = File.Create(path))
        {
            stream.Write(Encoding.ASCII.GetBytes($"ply\nformat binary_little_endian 1.0\nelement vertex {count}\nproperty float x\nproperty float y\nproperty float z\nend_header\n"));
            using var writer = new BinaryWriter(stream);
            for (var i = 0; i < count; i++) { writer.Write((float)i); writer.Write(0f); writer.Write(2000f); }
        }
        var scan = await new ScanImporter().ImportAsync(path, new());
        Assert.Equal(count, scan.Points.Length);
        Assert.InRange(scan.Points[^1].X, 999.998f, 1000);
        Assert.Equal(60_000, new ImportedGeometry(scan).Cloud(60_000, null).Positions!.Count);
        var saved = Path.Combine(files.Directory, "million.subterra");
        var storage = new SessionStorage();
        await storage.SaveAsync(saved, scan.ToSession(), 1);
        var restored = await storage.LoadAsync(saved);
        Assert.Empty(restored.Frames);
        Assert.Equal(count, restored.PointCount);
        Assert.Equal(scan.Points[^1], restored.ImportedScan!.Points[^1]);
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

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GlbImportsEmbeddedTextureUvNodeTransformsAndSparseAccessors(bool sparse)
    {
        using var files = new Files(); var path = Path.Combine(files.Directory, "textured.glb"); CreateGlb(path, sparse: sparse);
        var scan = await new ScanImporter().ImportAsync(path, new()); // GLB units/axis are format-defined, independent of ASC defaults.
        Assert.Equal(ScanUnit.Meters, scan.Metadata.Unit); Assert.Equal(ScanUpAxis.Y, scan.Metadata.UpAxis);
        Assert.Equal(new Vector3(10, 20, 30), new((float)scan.Metadata.OriginX, (float)scan.Metadata.OriginY, (float)scan.Metadata.OriginZ));
        Assert.Equal(new Vector3(-2, 0, 0), scan.Bounds.Minimum); Assert.Equal(new Vector3(0, 0, 3), scan.Bounds.Maximum);
        Assert.Equal(new uint[] { 0xff0000, 0x00ff00, 0xffffff, 0x0000ff }, scan.Colors);
        var appearance = Assert.IsType<MeshAppearance>(scan.Appearance); Assert.Single(appearance.Parts); Assert.Single(appearance.Textures);
        Assert.Equal(new Vector2(.25f, .25f), appearance.TextureCoordinates[0]); Assert.Equal(new Vector3(0, -1, 0), appearance.Normals[0]);
        var cross = Vector3.Cross(scan.Points[scan.Triangles[1]].Position - scan.Points[scan.Triangles[0]].Position,
            scan.Points[scan.Triangles[2]].Position - scan.Points[scan.Triangles[0]].Position);
        Assert.True(Vector3.Dot(cross, appearance.Normals[0]) > 0); // Mirrored node winding follows transformed normals.
        var absolute = await new ScanImporter().ImportAsync(path, new(LocalOrigin: false));
        Assert.Equal(new Vector3(8, -30, 20), absolute.Bounds.Minimum); Assert.Equal(new Vector3(10, -30, 23), absolute.Bounds.Maximum);
        var geometry = new ImportedGeometry(scan); geometry.PrepareMeshPreviews();
        var part = Assert.Single(geometry.MaterialMeshes); Assert.Equal(4, part.Geometry.TextureCoordinates!.Count);
        Assert.NotNull(Assert.IsType<HelixToolkit.Wpf.SharpDX.PBRMaterial>(part.Material).AlbedoMap); Assert.True(part.Material.IsFrozen);
        Assert.Same(scan, MeshPreviewBuilder.Build(scan, 1));
    }

    [Fact]
    public Task GlbRoundTripAndGuestViewKeepTexturesAfterMasksTransformsAndMixedPlyObjects() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); var source = Path.Combine(files.Directory, "model.glb"); CreateGlb(source);
        var scan = await new ScanImporter().ImportAsync(source, new()); var storage = new SessionStorage();
        var single = Path.Combine(files.Directory, "single.subterra"); await storage.SaveAsync(single, scan.ToSession(), 1);
        var restored = (await storage.LoadAsync(single)).ImportedScan!;
        Assert.Equal(scan.Appearance!.Textures[0].Data, restored.Appearance!.Textures[0].Data);
        Assert.Equal(scan.Appearance.TextureCoordinates, restored.Appearance.TextureCoordinates); Assert.Equal(scan.Appearance.Normals, restored.Appearance.Normals);
        var id = Guid.NewGuid(); var other = Guid.NewGuid();
        var project = new ScanProject([new(id, "GLB", scan, Transform: new(2, 0, 0), Mask: new([true, true, true, false])),
            new(other, "PLY", new ImportedScan { Metadata = new("test.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0), Points = [new(0, 0, 0, 1), new(1, 0, 0, 1), new(0, 1, 0, 1)], Triangles = [0, 1, 2] })], id, scan.Metadata);
        var evaluated = ScanProjectEvaluator.Evaluate(project).VisibleScan!;
        Assert.Equal(2, evaluated.Appearance!.Parts.Length); Assert.Equal(6, evaluated.Triangles.Length);
        Assert.Equal(6, evaluated.Appearance.TextureCoordinates.Length);
        project = project with { Structure = StructureProvenance.Create(project, new([], project.PointCount, project.PointCount, new())) };
        var combined = Path.Combine(files.Directory, "combined.subterra"); await storage.SaveAsync(combined, project.ToSession(), 1);
        using var guest = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), storage, access: new TestAccess(false));
        await guest.OpenAsync(combined); Assert.False(guest.HasNotification, guest.Notification); Assert.True(guest.IsSurfaceMode);
        Assert.True(guest.HasStructureAnalysis); Assert.True(guest.UsesOriginalMaterials);
        Assert.Equal(2, guest.TexturedMeshParts.Count); Assert.False(guest.CanManageProject);
        guest.PointsCommand.Execute(null); Assert.Empty(guest.TexturedMeshParts);
        await guest.SurfaceCommand.ExecuteAsync(); Assert.Equal(2, guest.TexturedMeshParts.Count);
    });

    [Theory]
    [InlineData("external")] [InlineData("cycle")] [InlineData("accessor")] [InlineData("missing_uv")] [InlineData("draco")]
    public async Task MalformedOrUnsupportedGlbFailsBeforeReplacingTheScene(string kind)
    {
        using var files = new Files(); var path = Path.Combine(files.Directory, "invalid.glb");
        CreateGlb(path, root =>
        {
            if (kind == "external") root["images"]![0]!["uri"] = "https://example.invalid/private-texture.png";
            if (kind == "cycle") root["nodes"]![1]!["children"] = JsonNode.Parse("[0]");
            if (kind == "accessor") root["accessors"]![0]!["count"] = 5_000_001;
            if (kind == "missing_uv") ((JsonObject)root["meshes"]![0]!["primitives"]![0]!["attributes"]!).Remove("TEXCOORD_0");
            if (kind == "draco") root["meshes"]![0]!["primitives"]![0]!["extensions"] = JsonNode.Parse("{\"KHR_draco_mesh_compression\":{}}");
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new ScanImporter().ImportAsync(path, new()));
    }

    [Fact]
    public async Task GlbAiViewsRasterizeTextureInsideTrianglesAndRespectMasksClippingAndDepth()
    {
        using var files = new Files(); var path = Path.Combine(files.Directory, "room.glb"); CreateGlb(path);
        var scan = await new ScanImporter().ImportAsync(path, new()); var project = ScanProject.FromSession(scan.ToSession());
        var pose = new ScanViewPose(new(-1, -3, 1.5f), Vector3.UnitY, Vector3.UnitZ, 60);
        var image = ScanImageRenderer.Render(project, pose, 128); var slot = 64 * 128 + 64;
        Assert.True(image.Coverage > .45); Assert.InRange(image.Depth[slot], 2.999f, 3.001f); Assert.InRange(image.Positions[slot].Y, -.001f, .001f);
        var masked = project.Replace(project.Active! with { Mask = new([true, true, true, false]) });
        Assert.True(ScanImageRenderer.Render(masked, pose, 128).Coverage < image.Coverage * .6);
        var clipped = ScanImageRenderer.Render(project, pose, 128, new(Vector3.UnitX, -1));
        Assert.InRange(clipped.Coverage, image.Coverage * .4, image.Coverage * .6);
        Assert.ThrowsAny<OperationCanceledException>(() => ScanImageRenderer.Render(project, pose, token: new(true)));
    }

    [Fact]
    public Task GlbImportDialogUsesMetersAndYUpAndHidesTextColumnSettings() => ScanControlTests.OnSta(() =>
    {
        var dialog = new SubTerra.Views.ScanImportWindow("model.glb");
        Assert.Equal(ScanUnit.Meters, dialog.Options.Unit); Assert.Equal(ScanUpAxis.Y, dialog.Options.UpAxis);
        Assert.False(((System.Windows.Controls.ComboBox)dialog.FindName("Units")).IsEnabled);
        Assert.Equal(System.Windows.Visibility.Collapsed, ((System.Windows.Controls.StackPanel)dialog.FindName("TextOptions")).Visibility);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task EightKGlbUsesBoundedPreviewAndPreservesOriginalImageInSession()
    {
        using var files = new Files(); var image = LargeRedPng(8192, 8192); var path = Path.Combine(files.Directory, "large.glb");
        CreateGlb(path, textureBytes: image);
        var scan = await new ScanImporter().ImportAsync(path, new()); var texture = Assert.Single(scan.Appearance!.Textures);
        Assert.Equal(new MeshTextureSampler.ImageInfo(8192, 8192), MeshTextureSampler.Inspect(texture));
        var sampling = MeshTextureSampler.Decode(texture); Assert.Equal(1024, sampling.Width); Assert.Equal(1024, sampling.Height);
        Assert.All(scan.Colors, c => Assert.Equal(0xFF0000u, c));
        var geometry = new ImportedGeometry(scan); geometry.PrepareMeshPreviews();
        Assert.Equal(1, geometry.ReducedTextureCount); Assert.Equal(4096L * 4096, geometry.DisplayTexturePixels);
        var saved = Path.Combine(files.Directory, "large.subterra"); var storage = new SessionStorage();
        await storage.SaveAsync(saved, scan.ToSession(), 1); var restored = (await storage.LoadAsync(saved)).ImportedScan!;
        Assert.Equal(image, Assert.Single(restored.Appearance!.Textures).Data);
        Assert.Equal(scan.Points, restored.Points); Assert.Equal(scan.Triangles, restored.Triangles);
    }

    [Fact]
    public async Task CombinedSourceTexturesAboveOldPixelCapShareDisplayMemoryBudget()
    {
        using var files = new Files(); var image = LargeRedPng(8192, 8192); var path = Path.Combine(files.Directory, "model.glb");
        CreateGlb(path); var original = await new ScanImporter().ImportAsync(path, new());
        var appearance = original.Appearance! with
        {
            Parts = [new(0, 6, 0), new(6, 6, 1), new(12, 6, 2)],
            Materials = [new("One", BaseColorTexture: 0), new("Two", BaseColorTexture: 1), new("Three", BaseColorTexture: 2)],
            Textures = [new(image, "image/png"), new(image, "image/png"), new(image, "image/png")]
        };
        var scan = new ImportedScan { Metadata = original.Metadata, Points = original.Points, Colors = original.Colors,
            Triangles = [.. original.Triangles, .. original.Triangles, .. original.Triangles], Appearance = appearance };
        var geometry = new ImportedGeometry(scan); geometry.PrepareMeshPreviews();
        Assert.Equal(3, geometry.ReducedTextureCount); Assert.Equal(3, geometry.MaterialMeshes.Count);
        Assert.InRange(geometry.DisplayTexturePixels, 1, MeshTextureSampler.MaximumDisplayTotalPixels);
        var storage = new SessionStorage(); var saved = Path.Combine(files.Directory, "several.subterra");
        await storage.SaveAsync(saved, scan.ToSession(), 1);
        var restored = (await storage.LoadAsync(saved)).ImportedScan!;
        Assert.All(restored.Appearance!.Textures, t => Assert.Equal(image, t.Data));
    }

    [Fact]
    public Task BadTextureAndRenderFailureKeepOpenSceneAndAllowPointView() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); var good = Path.Combine(files.Directory, "good.glb"); CreateGlb(good);
        var bad = Path.Combine(files.Directory, "bad.glb"); CreateGlb(bad, textureBytes: [1, 2, 3, 4]);
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: new TestAccess(), interaction: new TestInteraction());
        await vm.ImportAsync(good, new()); Assert.True(vm.UsesOriginalMaterials);
        vm.BeginInspection(false); vm.SelectInspectionPoint(Vector3.Zero); vm.AnnotationName = "Оставить"; vm.AddAnnotationCommand.Execute(null);
        var count = vm.PointCountText; var dirty = vm.HasUnsavedChanges;
        await vm.ImportAsync(bad, new()); Assert.True(vm.HasNotification); Assert.Equal(count, vm.PointCountText); Assert.Single(vm.UserAnnotations);
        vm.RecoverRenderFailure(new InvalidOperationException("Texture allocation failed"));
        Assert.True(vm.IsPoints); Assert.Empty(vm.TexturedMeshParts); Assert.Equal(count, vm.PointCountText);
        Assert.Single(vm.UserAnnotations); Assert.Equal(dirty, vm.HasUnsavedChanges); Assert.True(vm.HasNotification);
        await vm.ShowSurfaceAsync(); Assert.True(vm.UsesOriginalMaterials);
    });

    // Stream a highly compressible image; constructing an 8K source bitmap would make the fixture itself allocate 256 MB.
    private static byte[] LargeRedPng(int width, int height)
    {
        using var result = new MemoryStream(); result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string name, byte[] data)
        {
            Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, data.Length); result.Write(size);
            var type = Encoding.ASCII.GetBytes(name); result.Write(type); result.Write(data);
            uint crc = 0xFFFFFFFF;
            void Update(byte value) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); }
            foreach (var value in type) Update(value); foreach (var value in data) Update(value);
            BinaryPrimitives.WriteUInt32BigEndian(size, ~crc); result.Write(size);
        }
        var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2; Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width * 3 + 1]; for (var x = 0; x < width; x++) row[1 + x * 3] = 255;
            for (var y = 0; y < height; y++) zip.Write(row);
        }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []); return result.ToArray();
    }

    internal static void CreateGlb(string path, Action<JsonObject>? edit = null, bool sparse = false, byte[]? textureBytes = null)
    {
        var views = new JsonArray(); using var binary = new MemoryStream();
        int Add(byte[] data, int? stride = null)
        {
            while (binary.Length % 4 != 0) binary.WriteByte(0);
            var index = views.Count; var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = binary.Length, ["byteLength"] = data.Length };
            if (stride.HasValue) view["byteStride"] = stride.Value;
            views.Add(view); binary.Write(data); return index;
        }
        static byte[] Floats(float[] values) { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); foreach (var v in values) writer.Write(v); return stream.ToArray(); }
        var position = Add(Floats([0,0,0,0,0,1, 1,0,0,0,0,1, 1,1,0,0,0,1, 0,1,0,0,0,1]), 24);
        var uv = Add(Floats([.25f,.25f, .75f,.25f, .75f,.75f, .25f,.75f])); var indices = Add([0,0,1,0,2,0,0,0,2,0,3,0]);
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            new byte[] {0,0,255,255, 0,255,0,255, 255,0,0,255, 255,255,255,255}, 8);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var image = new MemoryStream(); encoder.Save(image); var texture = Add(textureBytes ?? image.ToArray());
        var root = JsonNode.Parse("""
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"translation":[10,20,30],"children":[1]},{"mesh":0,"scale":[-2,3,1]}],
        "meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":1,"TEXCOORD_0":2},"indices":3,"material":0}]}],
        "materials":[{"name":"Paint","pbrMetallicRoughness":{"baseColorTexture":{"index":0},"metallicFactor":0,"roughnessFactor":0.8}}],
        "textures":[{"source":0,"sampler":0}],"samplers":[{"wrapS":33071,"wrapT":33071}],"images":[{"mimeType":"image/png"}]}
        """)!.AsObject();
        root["images"]![0]!["bufferView"] = texture;
        var accessors = new JsonArray(new JsonObject { ["bufferView"] = position, ["componentType"] = 5126, ["count"] = 4, ["type"] = "VEC3" },
            new JsonObject { ["bufferView"] = position, ["byteOffset"] = 12, ["componentType"] = 5126, ["count"] = 4, ["type"] = "VEC3" },
            new JsonObject { ["bufferView"] = uv, ["componentType"] = 5126, ["count"] = 4, ["type"] = "VEC2" },
            new JsonObject { ["bufferView"] = indices, ["componentType"] = 5123, ["count"] = 6, ["type"] = "SCALAR" });
        if (sparse)
        {
            ((JsonObject)accessors[0]!).Remove("bufferView"); var ids = Add([0,1,2,3]); var values = Add(Floats([0,0,0,1,0,0,1,1,0,0,1,0]));
            accessors[0]!["sparse"] = new JsonObject { ["count"] = 4, ["indices"] = new JsonObject { ["bufferView"] = ids, ["componentType"] = 5121 }, ["values"] = new JsonObject { ["bufferView"] = values } };
        }
        root["accessors"] = accessors; root["bufferViews"] = views; root["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = binary.Length }); edit?.Invoke(root);
        var json = Encoding.UTF8.GetBytes(root.ToJsonString()); var jsonLength = (json.Length + 3) & ~3; var binLength = ((int)binary.Length + 3) & ~3;
        using var file = File.Create(path); using var output = new BinaryWriter(file);
        output.Write(0x46546C67u); output.Write(2u); output.Write(12 + 8 + jsonLength + 8 + binLength);
        output.Write(jsonLength); output.Write(0x4E4F534Au); output.Write(json); for (var i = json.Length; i < jsonLength; i++) output.Write((byte)32);
        output.Write(binLength); output.Write(0x004E4942u); output.Write(binary.ToArray()); for (var i = binary.Length; i < binLength; i++) output.Write((byte)0);
    }

    private sealed class DirectProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    private sealed class Files : IDisposable
    {
        public string Directory { get; } = WorkspaceFeatureTests.TemporaryDirectory();
        public string Write(string name, string content) { var path = Path.Combine(Directory, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
