using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.ViewModels;
using SubTerra.Views;
using Xunit;

namespace SubTerra.Tests;

public sealed class ScanProjectTests
{
    private static ImportedScan Cloud(params Vector3[] points) => new()
    {
        Metadata = new("test.asc", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0),
        Points = points.Select(p => new ScanPoint(p.X, p.Y, p.Z, .5f)).ToArray(),
        Colors = Enumerable.Repeat(0x44aabbu, points.Length).ToArray()
    };
    private static ScanObject Object(ImportedScan scan) => new(Guid.NewGuid(), "Объект", scan);

    [Fact]
    public void BoxCleaningUsesProjectCoordinatesAndComposesWithTheExistingMask()
    {
        var source = Cloud(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, new(5, 5, 5));
        var o = Object(source) with { Transform = new(10, 0, 0, RotateZ: 90) };
        var mask = ScanCleaner.Region(o, new(new(9, -.01f, -.01f), new(10.01f, .01f, .01f)), true);
        Assert.Equal(new[] { true, false, true, false }, mask.Copy());
        var next = ScanCleaner.Region(o with { Mask = mask }, new(new(9.5f, -1, -1), new(11, 1, 1)), true);
        Assert.Equal(new[] { true, false, false, false }, next.Copy());
        Assert.Equal(4, source.Points.Length); Assert.Equal(3, source.Points.Count(p => p.Z == 0));
        Assert.Equal(2, mask.Count);
    }

    [Fact]
    public void DensityReductionKeepsOriginalRepresentativesAndDoesNotReincludeDeletedPoints()
    {
        var o = Object(Cloud(new(.001f, 0, 0), new(.002f, 0, 0), new(.02f, 0, 0), new(.021f, 0, 0))) with
        { Mask = new([false, true, true, true]) };
        var mask = ScanCleaner.Voxel(o, .01);
        Assert.Equal(new[] { false, true, true, false }, mask.Copy());
        Assert.Equal(4, o.Source.Points.Length);
    }

    [Theory]
    [InlineData(.01f)] [InlineData(.02f)] [InlineData(.13f)]
    [InlineData(-.01f)] [InlineData(-.02f)] [InlineData(-.13f)]
    public void VoxelBoundariesRespectFloatPrecisionAndKeepAdjacentCellsSeparate(float boundary)
    {
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            var o = Object(Cloud(axis * MathF.BitDecrement(boundary), axis * boundary, axis * MathF.BitIncrement(boundary)));
            Assert.Equal(new[] { true, true, false }, ScanCleaner.Voxel(o, .01).Copy());
        }
    }

    [Fact]
    public void IsolatedPointFilterCountsNeighborsAcrossCellBoundariesWithoutCountingItself()
    {
        var o = Object(Cloud(new(.009f, 0, 0), new(.011f, 0, 0), new(.012f, 0, 0), new(5, 0, 0)));
        Assert.Equal(new[] { true, true, true, false }, ScanCleaner.Isolated(o, .01, 2).Copy());
        Assert.Equal(0, ScanCleaner.Isolated(o, .01, 3).Count);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidFilterDistancesAreRejected(double size)
    {
        var o = Object(Cloud(Vector3.Zero));
        Assert.Throws<InvalidDataException>(() => ScanCleaner.Voxel(o, size));
        Assert.Throws<InvalidDataException>(() => ScanCleaner.Isolated(o, size, 1));
    }

    [Fact]
    public void AllCleaningFiltersRespectCancellationWithoutChangingTheSource()
    {
        var o = Object(Cloud(Vector3.Zero, Vector3.One));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ScanCleaner.Region(o, new(-Vector3.One, Vector3.One), true, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ScanCleaner.Voxel(o, .1, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ScanCleaner.Isolated(o, .1, 1, cancellation.Token));
        Assert.Null(o.Mask); Assert.Equal(2, o.PointCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ScreenSelectionIsThroughTheObjectAndIgnoresPointsBehindTheCamera(bool brush)
    {
        var o = Object(Cloud(new(0, 0, .1f), new(.1f, 0, .9f), new(.8f, 0, .5f), new(0, 0, -.1f)));
        var selection = new ScreenScanSelection(Matrix4x4.Identity, 100, 100, new(40, 40), new(60, 60), brush ? [new(50, 50)] : null, 10);
        Assert.Equal(new[] { true, true, false, false }, ScanCleaner.Screen(o, selection, true).Copy());
        Assert.Equal(new[] { false, false, true, true }, ScanCleaner.Screen(o, selection, false).Copy());
    }

    [Fact]
    public void PackedMasksAreImmutableAndRejectInvalidPadding()
    {
        var flags = new[] { true, false, true, true, false, false, true, false, true };
        var mask = new ScanPointMask(flags); flags[0] = false;
        Assert.True(mask[0]);
        Assert.Equal(mask.Copy(), ScanPointMask.Unpack(mask.Pack(), mask.Length).Copy());
        Assert.Throws<InvalidDataException>(() => ScanPointMask.Unpack([0, 128], 9));
    }

    [Fact]
    public void EvaluationAppliesVisibilityTransformColorAndMeshMaskWithValidReindexing()
    {
        var source = new ImportedScan { Metadata = new("mesh.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0),
            Points = [new(0, 0, 0, 1), new(1, 0, 0, 1), new(0, 1, 0, 1), new(1, 1, 0, 1)], Triangles = [0, 1, 2, 1, 3, 2] };
        var o = Object(source) with { Mask = new([true, true, true, false]), Transform = new(10), Tint = 0xff0000 };
        var hidden = Object(Cloud(new Vector3(100, 100, 100))) with { IsVisible = false };
        var result = ScanProjectEvaluator.Evaluate(new([o, hidden], o.Id, source.Metadata));
        Assert.Equal(3, result.VisibleScan!.Points.Length);
        Assert.Equal(new[] { 0, 1, 2 }, result.VisibleScan.Triangles);
        Assert.Equal(new Vector3(10, 0, 0), result.VisibleScan.Points[0].Position);
        Assert.All(result.VisibleScan.Colors, color => Assert.Equal(0xff0000u, color));
        Assert.Equal(4, source.Points.Length); Assert.Equal(6, source.Triangles.Length);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MeshViewRetainsVerticesThatHaveNoSurvivingFaces(bool cleaned)
    {
        var source = new ImportedScan { Metadata = new("mesh.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0),
            Points = [new(0, 0, 0, 1), new(1, 0, 0, 1), new(0, 1, 0, 1), new(1, 1, 0, 1)], Triangles = [0, 1, 2] };
        var a = Object(source) with { Mask = cleaned ? new([true, false, true, true]) : null };
        var b = Object(source);
        var view = await ScanProjectView.PrepareAsync(new([a, b], a.Id, source.Metadata));
        Assert.True(view.Data.VisibleScan!.HasMesh);
        Assert.Equal(cleaned ? new[] { 0, 1, 2, 6 } : new[] { 3, 7 }, view.Data.CloudOnlyIndices);
        Assert.NotNull(view.CloudOnly);
        Assert.Equal(view.Data.CloudOnlyIndices.Length, view.CloudOnly.Cloud(100, null).Positions!.Count);
        Assert.Equal(4, source.Points.Length);
    }

    [Fact]
    public async Task ProjectRoundTripKeepsOriginalsMasksTransformsHiddenObjectsAndNotes()
    {
        using var files = new Files();
        var a = Object(Cloud(Vector3.Zero, Vector3.One)) with { Mask = new([true, false]), Transform = new(1, 2, 3, 10, 20, 30), Tint = 0xaabbcc };
        var b = Object(Cloud(new Vector3(5, 5, 5))) with { IsVisible = false, Name = "Второй" };
        var project = new ScanProject([a, b], b.Id, a.Source.Metadata);
        var storage = new SessionStorage(); var path = Path.Combine(files.Directory, "project.subterra");
        await storage.SaveAsync(path, project.ToSession([new("Метка", new(1, 2, 3))]), 1);
        var loaded = await storage.LoadAsync(path); var restored = loaded.Project!;
        Assert.Equal(b.Id, restored.ActiveObjectId); Assert.Equal("Метка", Assert.Single(loaded.Annotations).Name);
        Assert.Equal(a.Transform, restored.Objects[0].Transform); Assert.Equal(a.Tint, restored.Objects[0].Tint);
        Assert.Equal(2, restored.Objects[0].Source.Points.Length); Assert.Equal(1, restored.Objects[0].Mask!.Count);
        Assert.False(restored.Objects[1].IsVisible); Assert.Equal("Второй", restored.Objects[1].Name);
        Assert.Equal(project.Origin, restored.Origin);
        var original = restored.Replace(restored.Objects[0] with { Mask = null });
        Assert.Equal(2, ScanProjectEvaluator.Evaluate(original).VisibleScan!.Points.Length);
    }

    [Fact]
    public async Task EmptyProjectCanBeSavedLoadedAndRecovered()
    {
        using var files = new Files(); var storage = new SessionStorage();
        var project = new ScanProject([], Guid.Empty, null);
        var path = Path.Combine(files.Directory, "empty.subterra");
        await storage.SaveAsync(path, project.ToSession(), 0);
        var loaded = await storage.LoadAsync(path);
        Assert.Empty(loaded.Project!.Objects); Assert.Null(ScanProjectEvaluator.Evaluate(loaded.Project).VisibleScan);
        var workspace = new WorkspaceStateStore(Path.Combine(files.Directory, "workspace"));
        await workspace.SaveRecoveryAsync(storage, loaded, path);
        Assert.Empty((await storage.LoadAsync(workspace.RecoveryPath)).Project!.Objects);
    }

    [Theory]
    [InlineData("mask")] [InlineData("duplicate")] [InlineData("count")] [InlineData("transform")]
    public async Task CorruptProjectIsRejected(string kind)
    {
        using var files = new Files(); var storage = new SessionStorage();
        var o = Object(Cloud(Vector3.Zero, Vector3.One)) with { Mask = new([true, false]) };
        var second = Object(Cloud(Vector3.Zero));
        var path = Path.Combine(files.Directory, "corrupt.subterra");
        await storage.SaveAsync(path, new ScanProject([o, second], o.Id, o.Source.Metadata).ToSession(), 1);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            if (kind == "mask")
            {
                var entry = archive.GetEntry($"objects/{o.Id:N}/mask.bin")!; entry.Delete();
                using var stream = archive.CreateEntry($"objects/{o.Id:N}/mask.bin").Open(); stream.WriteByte(128);
            }
            else
            {
                var entry = archive.GetEntry("manifest.json")!; JsonNode json;
                using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(reader.ReadToEnd())!;
                var objects = json["Project"]!["Objects"]!;
                if (kind == "duplicate") objects[1]!["Id"] = objects[0]!["Id"]!.GetValue<string>();
                if (kind == "count") json["PointCount"] = ImportedScan.MaximumPoints + 1;
                if (kind == "transform") objects[0]!["Transform"] = JsonNode.Parse("{\"X\":100001}");
                entry.Delete(); using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()); writer.Write(json.ToJsonString());
            }
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.LoadAsync(path));
    }

    [Fact]
    public void HistoryIsBoundedAndANewEditAfterUndoClearsRedo()
    {
        var o = Object(Cloud(Vector3.Zero)); var project = new ScanProject([o], o.Id, o.Source.Metadata); var history = new ScanProjectHistory();
        for (var i = 0; i < 25; i++) { var next = project.Replace(o with { Name = i.ToString() }); history.Commit(project, next, "имя"); project = next; }
        var count = 0;
        while (history.UndoChange is not null) { history.CompleteUndo(); ++count; }
        Assert.Equal(20, count); Assert.NotNull(history.RedoChange);
        history.Commit(project, project.Replace(o), "новое действие"); Assert.Null(history.RedoChange);
    }

    [Fact]
    public void QuadricPreviewReducesAClosedSurfaceWithoutOpeningRandomHoles()
    {
        var scan = Sphere(); var original = scan.Triangles.ToArray();
        var reduced = MeshPreviewBuilder.Build(scan, 256);
        Assert.InRange(reduced.Triangles.Length / 3, 100, 300);
        var edges = new Dictionary<(int, int), int>(); double volume = 0;
        for (var i = 0; i < reduced.Triangles.Length; i += 3)
        {
            var a = reduced.Triangles[i]; var b = reduced.Triangles[i + 1]; var c = reduced.Triangles[i + 2];
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) }) { var edge = (Math.Min(x, y), Math.Max(x, y)); edges[edge] = edges.GetValueOrDefault(edge) + 1; }
            volume += Vector3.Dot(reduced.Points[a].Position, Vector3.Cross(reduced.Points[b].Position, reduced.Points[c].Position)) / 6d;
        }
        Assert.All(edges.Values, count => Assert.Equal(2, count));
        Assert.InRange(Math.Abs(volume), 3.7, 4.4);
        Assert.All(reduced.Colors, color => Assert.Equal(0x44aabbu, color));
        Assert.Equal(original, scan.Triangles);
    }
    private static ImportedScan Sphere()
    {
        var positions = new List<Vector3> { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        var triangles = new List<int> { 0,2,4, 2,1,4, 1,3,4, 3,0,4, 2,0,5, 1,2,5, 3,1,5, 0,3,5 };
        for (var step = 0; step < 5; step++)
        {
            var middle = new Dictionary<(int, int), int>(); var next = new List<int>();
            int Mid(int a, int b) { var key = (Math.Min(a,b), Math.Max(a,b)); if (middle.TryGetValue(key, out var found)) return found; var index = positions.Count; positions.Add(Vector3.Normalize(positions[a] + positions[b])); middle[key] = index; return index; }
            for (var i = 0; i < triangles.Count; i += 3)
            {
                var a = triangles[i]; var b = triangles[i+1]; var c = triangles[i+2]; var ab = Mid(a,b); var bc = Mid(b,c); var ca = Mid(c,a);
                next.AddRange([a,ab,ca, ab,b,bc, ca,bc,c, ab,bc,ca]);
            }
            triangles = next;
        }
        return new() { Metadata = new("sphere.ply", ScanUnit.Meters, ScanUpAxis.Z, 0,0,0), Points = positions.Select(p => new ScanPoint(p.X,p.Y,p.Z,1)).ToArray(), Colors = Enumerable.Repeat(0x44aabbu, positions.Count).ToArray(), Triangles = triangles.ToArray() };
    }

    [Fact]
    public Task MultipleObjectsVisibilityPropertiesUndoAndPersistenceWorkTogether() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); var storage = new SessionStorage();
        using var vm = Vm(storage);
        await vm.ImportAsync(files.Write("first.asc", "0 0 0\n100 0 0"), new());
        await vm.ImportAsync(files.Write("second.asc", "200 0 0\n300 0 0"), new(), true);
        Assert.False(vm.HasNotification, vm.Notification); Assert.Equal(2, vm.ScanObjects.Count); Assert.Equal("4", vm.PointCountText);
        Assert.InRange(vm.ObjectX, .1999, .2001);
        await vm.ToggleObjectAsync(vm.SelectedScanObject!.Id); Assert.Equal("2", vm.PointCountText);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.Equal("4", vm.PointCountText);
        await vm.RedoProjectCommand.ExecuteAsync(); Assert.Equal("2", vm.PointCountText);
        vm.ObjectName = "Второй проход"; vm.ObjectZ = .03; vm.ObjectColor = vm.ObjectColors[1];
        await vm.ApplyObjectCommand.ExecuteAsync();
        var path = Path.Combine(files.Directory, "project.subterra"); Assert.True(await vm.SaveAsync(path));
        var loaded = (await storage.LoadAsync(path)).Project!;
        Assert.Equal(2, loaded.Objects.Length); Assert.Equal("Второй проход", loaded.Active!.Name); Assert.False(loaded.Active.IsVisible);
        Assert.Equal(.03f, loaded.Active.Transform!.Z); Assert.NotNull(loaded.Active.Tint);
        var exported = Path.Combine(files.Directory, "visible.ply"); await vm.ExportAsync(exported);
        Assert.Equal(2, (await new ScanImporter().ImportAsync(exported, new(ScanUnit.Meters, LocalOrigin:false))).Points.Length);
    });

    [Fact]
    public Task CleanupPreviewApplyUndoRedoAndRestorationKeepTheOriginalAvailable() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); var storage = new SessionStorage(); using var vm = Vm(storage);
        await vm.ImportAsync(files.Write("scan.asc", "0 0 0\n1 0 0\n2 0 0\n3 0 0\n100 0 0"), new(ScanUnit.Meters));
        var path = Path.Combine(files.Directory, "scan.subterra"); Assert.True(await vm.SaveAsync(path));
        vm.CleanMaxX = 2; await vm.PreviewBoxAsync(true);
        Assert.True(vm.HasEditPreview); Assert.False(vm.CanSave); Assert.False(vm.CanExport); Assert.Equal("3", vm.PointCountText);
        Assert.False(vm.HasUnsavedChanges); vm.CancelCleanPreview(); Assert.Equal("5", vm.PointCountText); Assert.False(vm.CanUndoProject);
        vm.CleanMaxX = 2; await vm.PreviewBoxAsync(true); vm.ApplyCleanPreviewCommand.Execute(null);
        Assert.True(vm.HasUnsavedChanges); Assert.Equal("3", vm.PointCountText);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.Equal("5", vm.PointCountText);
        await vm.RedoProjectCommand.ExecuteAsync(); Assert.Equal("3", vm.PointCountText);
        Assert.True(await vm.SaveAsync(path));
        using var reopened = Vm(storage); await reopened.OpenAsync(path); Assert.Equal("3", reopened.PointCountText);
        await reopened.RestoreObjectCommand.ExecuteAsync(); Assert.Equal("5", reopened.PointCountText);
        Assert.Equal(5, (await storage.LoadAsync(path)).Project!.Active!.Source.Points.Length);
    });

    [Fact]
    public Task CancellationAndGuestAccessCannotCommitProjectChanges() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files();
        var path = files.Write("scan.asc", string.Join('\n', Enumerable.Range(0, 10000).Select(i => $"{i} 0 0")));
        using var vm = Vm(new()); await vm.ImportAsync(path, new(ScanUnit.Millimeters));
        var count = vm.PointCountText; var operation = vm.VoxelCleanCommand.ExecuteAsync(); vm.CancelOperation(); await operation;
        Assert.False(vm.HasEditPreview); Assert.Equal(count, vm.PointCountText); Assert.False(vm.CanUndoProject);
        using var guest = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: new TestAccess(false));
        await guest.ImportAsync(path, new()); Assert.False(guest.CanManageProject); Assert.False(guest.RectangleSelectionCommand.CanExecute(null));
        await guest.RemoveObjectCommand.ExecuteAsync(); Assert.Single(guest.ScanObjects);
    });

    [Fact]
    public Task LastObjectRemovalCanBeSavedAndUndone() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); using var vm = Vm(new());
        await vm.ImportAsync(files.Write("scan.asc", "0 0 0"), new()); await vm.RemoveObjectCommand.ExecuteAsync();
        Assert.Empty(vm.ScanObjects); Assert.True(vm.IsImported); Assert.True(vm.CanSave); Assert.False(vm.CanExport);
        Assert.True(await vm.SaveAsync(Path.Combine(files.Directory, "empty.subterra")));
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.Single(vm.ScanObjects); Assert.Equal("1", vm.PointCountText);
    });

    [Theory]
    [InlineData("hide")] [InlineData("clean")]
    public Task ProjectWithoutVisiblePointsCanBeSavedOnCloseAndReopened(string operation) => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); var storage = new SessionStorage();
        var path = Path.Combine(files.Directory, "empty-view.subterra");
        var interaction = new TestInteraction { Decision = UnsavedDecision.Save, SavePath = path };
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), storage, access: new TestAccess(), interaction: interaction);
        await vm.ImportAsync(files.Write("scan.asc", "0 0 0\n1 0 0\n2 0 0"), new(ScanUnit.Meters));
        if (operation == "hide") await vm.ToggleObjectAsync(vm.SelectedScanObject!.Id);
        else { await vm.PreviewBoxAsync(false); vm.ApplyCleanPreviewCommand.Execute(null); }
        Assert.Equal("0", vm.PointCountText); Assert.True(vm.CanSave); Assert.False(vm.CanExport);
        Assert.True(await vm.RequestCloseAsync()); Assert.False(vm.HasUnsavedChanges);
        var saved = (await storage.LoadAsync(path)).Project!;
        Assert.Single(saved.Objects); Assert.Equal(3, saved.Active!.Source.Points.Length);
        Assert.Null(ScanProjectEvaluator.Evaluate(saved).VisibleScan);
        using var reopened = Vm(storage); await reopened.OpenAsync(path);
        Assert.False(reopened.HasNotification, reopened.Notification);
        Assert.True(reopened.IsImported); Assert.Equal("0", reopened.PointCountText);
        if (operation == "hide") await reopened.ToggleObjectAsync(reopened.SelectedScanObject!.Id);
        else await reopened.RestoreObjectCommand.ExecuteAsync();
        Assert.Equal("3", reopened.PointCountText);
    });

    [Fact]
    public Task RevokedAccessCannotCommitAnImportIntoTheCurrentProject() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); var access = new TestAccess(); var storage = new SessionStorage();
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), storage, access: access, interaction: new TestInteraction());
        await vm.ImportAsync(files.Write("first.asc", "0 0 0\n1 0 0"), new(ScanUnit.Meters));
        Assert.True(await vm.SaveAsync(Path.Combine(files.Directory, "original.subterra")));
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsBusy) && vm.IsBusy) access.Logout(); };
        await vm.ImportAsync(files.Write("second.asc", "2 0 0\n3 0 0"), new(ScanUnit.Meters), true);
        Assert.False(vm.IsAdministrator); Assert.Single(vm.ScanObjects);
        Assert.Equal("2", vm.PointCountText); Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.HasNotification, vm.Notification);
    });

    [Fact]
    public Task ProjectPanelLoadsAndResolvesBindingsWithoutOpeningAWindow() => ScanControlTests.OnSta(async () =>
    {
        using var files = new Files(); using var vm = Vm(new()); await vm.ImportAsync(files.Write("scan.asc", "0 0 0\n1 1 1"), new());
        var panel = new ScanProjectPanel { DataContext = vm, Width = 216, Height = 580 };
        panel.Measure(new Size(216,580)); panel.Arrange(new Rect(0,0,216,580)); panel.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        void Check(DependencyObject element)
        {
            var properties = element.GetLocalValueEnumerator();
            while (properties.MoveNext())
            {
                var binding = BindingOperations.GetBindingExpressionBase(element, properties.Current.Property);
                if (binding is not null) Assert.False(binding.HasError, $"Binding failed: {element.GetType().Name}.{properties.Current.Property.Name}");
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Check(VisualTreeHelper.GetChild(element,i));
        }
        Check(panel);
    });

    private static MainViewModel Vm(SessionStorage storage) => new(new WorkspaceFeatureTests.MemoryScanSource(), storage, access: new TestAccess(), interaction: new TestInteraction());
    private sealed class Files : IDisposable
    {
        public string Directory { get; } = WorkspaceFeatureTests.TemporaryDirectory();
        public string Write(string name, string text) { var path = Path.Combine(Directory,name); File.WriteAllText(path,text); return path; }
        public void Dispose() => System.IO.Directory.Delete(Directory,true);
    }
}
