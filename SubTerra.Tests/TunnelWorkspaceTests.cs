using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using SubTerra.Models;
using SubTerra.Services;
using SubTerra.Simulation;
using SubTerra.ViewModels;
using SubTerra.Navigation;
using SubTerra.Views;
using Xunit;

namespace SubTerra.Tests;

public sealed class TunnelWorkspaceTests
{
    [Fact]
    public async Task WalkCanPassMeasuredJunctionAndTurnIntoBranch()
    {
        var a = Vector3.Zero; var b = new Vector3(8, 0, 0); var c = new Vector3(4, 0, 0); var e = new Vector3(4, 6, 0);
        bool InsideBranch(Vector3 p) => p.Y > 0 && p.Y < 6 && Math.Pow((p.X - 4) / 2, 2) + Math.Pow(p.Z / 1.5, 2) < .99;
        bool InsideMain(Vector3 p) => p.X > 0 && p.X < 8 && Math.Pow(p.Y / 2, 2) + Math.Pow(p.Z / 1.5, 2) < .99;
        var project = Project(Tube(a, b).Where(p => !InsideBranch(p)).Concat(Tube(c, e).Where(p => !InsideMain(p))));
        var root = TunnelAnalyzer.Create([InspectionPoint.From(a), InspectionPoint.From(b)], new(SectionSpacing: 1)) with
        { Version = 2, Branches = [new(Guid.NewGuid(), "Боковой проход", TunnelAnalyzer.Create([InspectionPoint.From(c), InspectionPoint.From(e)], new(SectionSpacing: 1)))] };
        var result = await TunnelMeshBuilder.BuildAsync(project, root, new(CompletionGap: 0));
        var surface = TunnelWalkSurface.Prepare(result.Reconstruction!);
        Assert.True(surface.TryCreate(new(1, 0, 0), Vector3.UnitX, out var walker));
        for (var i = 0; i < 60; i++) walker!.Move(1, 0, .02f, false);
        Assert.True(walker!.Position.X >= 3.85f && walker.Position.X <= 4.15f, $"Stopped at {walker.Position}: {walker.BlockedBy}");
        walker.Look(MathF.PI / 2, 0);
        for (var i = 0; i < 80; i++) walker.Move(1, 0, .02f, false);
        Assert.InRange(walker.Position.Y, 3.5f, 4.2f);
        Assert.InRange(walker.Position.X, 3.7f, 4.3f);
    }

    [Fact]
    public Task WalkingUsesCurrentObservedMeshAndSupportsSavedGuestViewing() => ScanControlTests.OnSta(async () =>
    {
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: new TestAccess(true), interaction: new TestInteraction());
        await vm.LoadTunnelDemoAsync(); Assert.False(vm.CanWalk);
        await vm.BuildTunnelMeshAsync(); Assert.False(vm.HasNotification, vm.Notification); Assert.True(vm.CanWalk);
        vm.InsideCommand.Execute(null); Assert.True(vm.IsWalking, vm.Notification);
        var walker = vm.CreateTunnelNavigator(); var start = walker.Position;
        walker.Move(1, 0, .02f, false); vm.UpdateWalkPosition(walker);
        Assert.True(Vector3.Distance(start, walker.Position) > .01);
        Assert.Equal((double)walker.Position.Z - .08, vm.LampCamera.Position.Z, 4);
        vm.ShowTunnelObserved = false; Assert.False(vm.IsWalking); Assert.False(vm.CanWalk);
        vm.ShowTunnelObserved = true; Assert.True(vm.CanWalk);
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "walk.subterra"); Assert.True(await vm.SaveAsync(path));
            using var guest = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: new TestAccess(false));
            await guest.OpenAsync(path); Assert.True(guest.CanWalk);
            var dirty = guest.HasUnsavedChanges; guest.InsideCommand.Execute(null); Assert.True(guest.IsWalking, guest.Notification);
            guest.ReturnToOverview(); Assert.Equal(dirty, guest.HasUnsavedChanges);
        }
        finally { Directory.Delete(directory, true); }
        vm.ObjectX += 1; await vm.ApplyObjectCommand.ExecuteAsync(); Assert.False(vm.CanWalk);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.True(vm.CanWalk);
        await vm.RedoProjectCommand.ExecuteAsync(); Assert.False(vm.CanWalk);
    });

    [Fact]
    public Task LegacyOutwardMeshKeepsInteriorLightingAndExportsCorrectFaceDirections() => ScanControlTests.OnSta(async () =>
    {
        var project = TunnelDemoFactory.Create();
        var document = await TunnelMeshBuilder.BuildAsync(project, project.Tunnel!, new(CompletionGap: 0));
        var mesh = document.Reconstruction!;
        var reversed = (int[])mesh.ObservedTriangles.Clone();
        for (var i = 0; i < reversed.Length; i += 3) (reversed[i + 1], reversed[i + 2]) = (reversed[i + 2], reversed[i + 1]);
        var legacy = mesh with { ObservedTriangles = reversed, HasInteriorWinding = false };
        var freshExport = TunnelMeshBuilder.Export(mesh); var legacyExport = TunnelMeshBuilder.Export(legacy);
        for (var i = 0; i < freshExport.Triangles.Length; i++)
            Assert.Equal(freshExport.Points[freshExport.Triangles[i]], legacyExport.Points[legacyExport.Triangles[i]]);
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        try
        {
            var storage = new SessionStorage(); var freshPath = Path.Combine(directory, "fresh.subterra"); var oldPath = Path.Combine(directory, "old.subterra");
            await storage.SaveAsync(freshPath, (project with { Tunnel = document }).ToSession(), 1);
            await storage.SaveAsync(oldPath, (project with { Tunnel = document with { Reconstruction = legacy } }).ToSession(), 1);
            using (var zip = ZipFile.Open(oldPath, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("tunnel/analysis.json")!; JsonNode json;
                using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(reader.ReadToEnd())!;
                json["Reconstruction"]!.AsObject().Remove("HasInteriorWinding"); entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("tunnel/analysis.json").Open()); writer.Write(json.ToJsonString());
            }
            using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), storage, access: new TestAccess(false));
            await vm.OpenAsync(freshPath); await vm.ShowSurfaceAsync(); var normals = vm.Surface!.Normals!.ToArray();
            await vm.OpenAsync(oldPath); await vm.ShowSurfaceAsync();
            Assert.False(vm.HasNotification, vm.Notification); var restoredNormals = vm.Surface!.Normals!.ToArray();
            Assert.Equal(normals.Length, restoredNormals.Length);
            for (var i = 0; i < normals.Length; i++) Assert.True(Vector3.DistanceSquared(normals[i], restoredNormals[i]) < 1e-10f);
            Assert.True(vm.CanWalk);
        }
        finally { Directory.Delete(directory, true); }
    });
    private static readonly InspectionPoint[] Axis = [new(0, 0, 0), new(8, 0, .8f)];
    private static IEnumerable<Vector3> Tube(Vector3 start, Vector3 end, Func<double, double, bool>? keep = null, bool niche = false)
    {
        var forward = Vector3.Normalize(end - start); var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ)); var up = Vector3.Cross(right, forward);
        var steps = (int)Math.Ceiling(Vector3.Distance(start, end) / .05);
        for (var i = 0; i <= steps; i++) for (var bin = 0; bin < 144; bin++)
        {
            var t = (double)i / steps; var angle = (bin + .5) * Math.Tau / 144;
            if (keep?.Invoke(t, angle) == false) continue;
            var bulge = niche && t > .35 && t < .65 && Math.Cos(angle) > .85 ? .7f : 0;
            yield return Vector3.Lerp(start, end, (float)t) + right * (float)((2 + bulge) * Math.Cos(angle)) + up * (float)(1.5 * Math.Sin(angle));
        }
    }
    private static ScanProject Project(IEnumerable<Vector3> points)
    {
        var scan = new ImportedScan { Metadata = new("synthetic-tunnel.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0) { IsSynthetic = true },
            Points = points.Select(p => new ScanPoint(p.X, p.Y, p.Z, 1)).ToArray() };
        var obj = new ScanObject(Guid.NewGuid(), "Тоннель", scan);
        return new([obj], obj.Id, scan.Metadata) { AnalysisMode = ProjectAnalysisMode.Tunnel, Tunnel = TunnelAnalyzer.Create(Axis, new(SectionSpacing: 1)) };
    }

    [Fact]
    public async Task CurvedMeshRetainsSlopeRoofAndNicheWithoutModifyingSource()
    {
        var project = Project(Tube(Axis[0].Position, Axis[1].Position, niche: true)); var original = project.Active!.Source.Points.ToArray();
        var result = await TunnelMeshBuilder.BuildAsync(project, project.Tunnel!, new(CompletionGap: 0)); var mesh = result.Reconstruction!;
        Assert.True(mesh.ObservedTriangles.Length > 1000); Assert.Empty(mesh.CompletedTriangles);
        Assert.True(mesh.HasInteriorWinding);
        var axisDirection = Vector3.Normalize(Axis[1].Position - Axis[0].Position);
        for (var i = 0; i < mesh.ObservedTriangles.Length; i += 3 * 31)
        {
            var a = mesh.Vertices[mesh.ObservedTriangles[i]].Position; var b = mesh.Vertices[mesh.ObservedTriangles[i + 1]].Position; var c = mesh.Vertices[mesh.ObservedTriangles[i + 2]].Position;
            var center = (a + b + c) / 3; var along = Vector3.Dot(center - Axis[0].Position, axisDirection);
            Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), Axis[0].Position + axisDirection * along - center) > 0, "Tunnel faces must receive a light placed inside the passage.");
        }
        Assert.Contains(mesh.Vertices, p => p.Y < -2.5); Assert.Contains(mesh.Vertices, p => p.X > 6 && p.Z > 2);
        Assert.Equal(original, project.Active.Source.Points); Assert.Empty(project.Active.Source.Triangles); Assert.Empty(project.Active.Source.Colors);
        TunnelAnalyzer.Validate(result, project);
        var exported = TunnelMeshBuilder.Export(mesh); exported.Validate(); Assert.True(exported.Metadata.IsSynthetic);
    }

    [Fact]
    public async Task SmallBoundedGapsAreSeparateAndMissingRoofNeverBecomesACap()
    {
        var project = Project(Tube(Axis[0].Position, Axis[1].Position, (_, angle) => angle < Math.Tau * 18 / 72 || angle >= Math.Tau * 19 / 72));
        var result = await TunnelMeshBuilder.BuildAsync(project, project.Tunnel!, new()); var mesh = result.Reconstruction!;
        Assert.NotEmpty(mesh.ObservedTriangles); Assert.NotEmpty(mesh.CompletedTriangles);
        var completion = TunnelMeshBuilder.Export(mesh, true); Assert.All(completion.Colors, c => Assert.Equal(0xF5A742u, c));
        var disabled = await TunnelMeshBuilder.BuildAsync(project, project.Tunnel!, new(CompletionGap: 0)); Assert.Empty(disabled.Reconstruction!.CompletedTriangles);
        var open = Project(Tube(Axis[0].Position, Axis[1].Position, (_, angle) => angle < Math.PI / 4 || angle > Math.PI * 3 / 4));
        var openResult = await TunnelMeshBuilder.BuildAsync(open, open.Tunnel!, new());
        Assert.Empty(openResult.Reconstruction!.CompletedTriangles);
        Assert.DoesNotContain(openResult.Reconstruction.Vertices, p => p.X > 2 && p.X < 5 && p.Z - p.X * .1 > 1.3);
        TunnelAnalyzer.Validate(result, project);
    }

    [Fact]
    public async Task MultiplePassagesKeepTheirAxesAndLeaveJunctionOpen()
    {
        var a = Vector3.Zero; var b = new Vector3(8, 0, 0); var c = new Vector3(4, 0, 0); var e = new Vector3(4, 6, 0);
        bool InsideBranch(Vector3 p) => p.Y > 0 && p.Y < 6 && Math.Pow((p.X - 4) / 2, 2) + Math.Pow(p.Z / 1.5, 2) < .99;
        bool InsideMain(Vector3 p) => p.X > 0 && p.X < 8 && Math.Pow(p.Y / 2, 2) + Math.Pow(p.Z / 1.5, 2) < .99;
        var project = Project(Tube(a, b).Where(p => !InsideBranch(p)).Concat(Tube(c, e).Where(p => !InsideMain(p))));
        var branch = new TunnelBranch(Guid.NewGuid(), "Боковой проход", TunnelAnalyzer.Create([InspectionPoint.From(c), InspectionPoint.From(e)], new(SectionSpacing: 1)));
        var root = TunnelAnalyzer.Create([InspectionPoint.From(a), InspectionPoint.From(b)], new(SectionSpacing: 1)) with { Version = 2, Branches = [branch] };
        var result = await TunnelMeshBuilder.BuildAsync(project, root, new(CompletionGap: 0));
        Assert.Equal(branch.Id, Assert.Single(result.Branches).Id); Assert.Equal(branch.Analysis.Axis, result.Branches[0].Analysis.Axis);
        var mesh = result.Reconstruction!; Assert.Contains(mesh.Vertices, p => p.Y > 5);
        for (var i = 0; i < mesh.ObservedTriangles.Length; i += 3)
        {
            var p = (mesh.Vertices[mesh.ObservedTriangles[i]].Position + mesh.Vertices[mesh.ObservedTriangles[i + 1]].Position + mesh.Vertices[mesh.ObservedTriangles[i + 2]].Position) / 3;
            Assert.False(p.Y > .5 && p.Y < 1.5 && Math.Abs(p.X - 4) < 1 && Math.Abs(p.Z) < .8, $"Junction sealed at {p}");
        }
        TunnelAnalyzer.Validate(result, project);
    }

    [Fact]
    public async Task AutomaticRouteFollowsCurvedSlopingPassageAndIgnoresRgb()
    {
        var project = TunnelDemoFactory.Create(); var route = await TunnelRoutePlanner.SuggestAsync(project);
        Assert.InRange(route.Length, 2, 128); Assert.True(Math.Abs(route[^1].X - route[0].X) > 25);
        foreach (var point in route)
        {
            Assert.InRange(Math.Abs(point.Y - 3 * Math.Sin(point.X / 8)), 0, 2.5);
            Assert.InRange(Math.Abs(point.Z - (2 + .12 * point.X)), 0, .4);
        }
        var source = project.Active!.Source;
        var colored = project.Replace(project.Active with { Tint = 0xFF0000, Source = new() { Metadata = source.Metadata, Points = source.Points, Colors = Enumerable.Repeat(0x123456u, source.Points.Length).ToArray() } });
        Assert.Equal(route, await TunnelRoutePlanner.SuggestAsync(colored));
        Assert.Equal(31, project.Tunnel!.Axis.Length);
    }

    [Fact]
    public async Task RouteRejectsDisconnectedAnchorsAndUsesVisibleWorldSpace()
    {
        var project = Project(Tube(Vector3.Zero, new(4, 0, 0)).Concat(Tube(new(8, 0, 0), new(12, 0, 0))));
        await Assert.ThrowsAsync<InvalidDataException>(() => TunnelRoutePlanner.SuggestAsync(project, [new(1, 0, 0), new(10, 0, 0)]));
        var shifted = Project(Tube(Vector3.Zero, new(8, 0, 0)));
        shifted = shifted.Replace(shifted.Active! with { Transform = new(20, 3, 2) });
        var route = await TunnelRoutePlanner.SuggestAsync(shifted, [new(21, 3, 2), new(27, 3, 2)]);
        Assert.All(route, p => { Assert.InRange(p.X, 20, 28); Assert.InRange(p.Z, 1.8, 2.2); });
    }

    [Fact]
    public async Task RecognitionSeparatesGeometryFromReviewedTrainingLabelsAndNeverGuessesPeople()
    {
        var obstacle = from x in Enumerable.Range(0, 8) from y in Enumerable.Range(0, 8) from z in Enumerable.Range(0, 8)
                       select new Vector3(3 + x * .08f, -.3f + y * .08f, -.1f + z * .08f);
        var project = Project(Tube(Axis[0].Position, Axis[1].Position).Concat(obstacle));
        var document = await TunnelRecognitionService.AnalyzeAsync(project, project.Tunnel!); var r = document.Recognition!;
        foreach (var type in new[] { TunnelPointClass.LowerSurface, TunnelPointClass.Side, TunnelPointClass.Roof }) Assert.Contains(r.Surfaces, p => p.Class == type);
        Assert.NotEmpty(r.Objects); Assert.DoesNotContain(r.Objects, o => o.SuggestedClass == TunnelPointClass.Person || o.ReviewedClass is not null);
        r = TunnelRecognitionService.AddBox(project, r, new(new(2.95f, -.35f, -.15f), new(3.65f, .35f, .55f)));
        var selected = r.Objects.Last(); r = r with { Objects = r.Objects.Select(o => o.Id == selected.Id ? o with { ReviewedClass = TunnelPointClass.Person } : o).ToArray() };
        document = document with { Recognition = r }; TunnelAnalyzer.Validate(document, project);
        var repeated = await TunnelRecognitionService.AnalyzeAsync(project, document);
        Assert.Equal(TunnelPointClass.Person, repeated.Recognition!.Objects.Single(o => o.Id == selected.Id).ReviewedClass);
        var rebuilt = await TunnelMeshBuilder.BuildAsync(project, repeated, new());
        Assert.Equal(TunnelPointClass.Person, rebuilt.Recognition!.Objects.Single(o => o.Id == selected.Id).ReviewedClass);
        var directory = WorkspaceFeatureTests.TemporaryDirectory(); var path = Path.Combine(directory, "labels.csv");
        try
        {
            await TunnelRecognitionService.ExportDatasetAsync(path, project, r);
            var lines = await File.ReadAllLinesAsync(path); Assert.StartsWith("# ", lines[0]);
            var rows = lines.Skip(2).Select(line => line.Split(',')).ToArray();
            Assert.Contains(rows, row => row[5] == "6" && row[8] == "operator");
            Assert.All(rows.Where(row => row[8] == "unreviewed"), row => Assert.Equal("0", row[5]));
            Assert.Contains(rows, row => row[5] == "0" && row[6] == "3");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TunnelRecognitionService.ExportDatasetAsync(path, project, r, cts.Token));
            Assert.Equal(lines, await File.ReadAllLinesAsync(path)); Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MeshBranchesAndReviewsRoundTripAndRefreshWithSourceHistory()
    {
        var project = Project(Tube(Axis[0].Position, Axis[1].Position));
        var branchId = Guid.NewGuid();
        project = project with { Tunnel = project.Tunnel! with { Version = 2, Branches = [new(branchId, "Продолжение", TunnelAnalyzer.Create([new(4, 0, .4f), new(8, 0, .8f)]))] } };
        var root = await TunnelMeshBuilder.BuildAsync(project, project.Tunnel!, new());
        root = await TunnelRecognitionService.AnalyzeAsync(project, root);
        root = root with { Recognition = TunnelRecognitionService.AddBox(project, root.Recognition!, new(new(1, -2.2f, -1.5f), new(2, 2.2f, 2))) };
        root = root with { Recognition = root.Recognition with { Objects = root.Recognition.Objects.Select(o => o with { ReviewedClass = TunnelPointClass.Unknown }).ToArray() } };
        var directory = WorkspaceFeatureTests.TemporaryDirectory(); var path = Path.Combine(directory, "tunnel.subterra");
        try
        {
            project = project with { Tunnel = root }; var storage = new SessionStorage(); await storage.SaveAsync(path, project.ToSession(), 1);
            using (var zip = ZipFile.OpenRead(path))
            using (var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open())) Assert.Equal(14, JsonNode.Parse(reader.ReadToEnd())!["SchemaVersion"]!.GetValue<int>());
            var loaded = (await storage.LoadAsync(path)).Project!;
            Assert.Equal(branchId, Assert.Single(loaded.Tunnel!.Branches).Id); Assert.Equal("Продолжение", loaded.Tunnel.Branches[0].Name);
            Assert.Equal(root.Reconstruction!.ObservedTriangles, loaded.Tunnel!.Reconstruction!.ObservedTriangles);
            Assert.True(loaded.Tunnel.Reconstruction.HasInteriorWinding);
            Assert.Equal(root.Recognition.Objects[0].ReviewedClass, loaded.Tunnel.Recognition!.Objects[0].ReviewedClass);
            var changed = project.Replace(project.Active! with { IsVisible = false }); var stale = TunnelAnalyzer.Refresh(root, changed);
            Assert.True(stale.Reconstruction!.IsStale); Assert.True(stale.Recognition!.IsStale);
            Assert.False(TunnelAnalyzer.Refresh(stale, project).Reconstruction!.IsStale);
            await Assert.ThrowsAsync<InvalidDataException>(() => TunnelRecognitionService.ExportDatasetAsync(Path.Combine(directory, "bad.csv"), changed, root.Recognition));
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("tunnel/analysis.json")!; JsonNode json;
                using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(reader.ReadToEnd())!;
                json["Reconstruction"]!["ObservedTriangles"]![0] = -1; entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("tunnel/analysis.json").Open()); writer.Write(json.ToJsonString());
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => storage.LoadAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public Task WorkspaceKeepsDraftBranchesHistoryAndGuestProtection() => ScanControlTests.OnSta(async () =>
    {
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: new TestAccess(true), interaction: new TestInteraction());
        await vm.LoadTunnelDemoAsync(); var primary = vm.TunnelAxisText;
        vm.TunnelAxisText = ""; await vm.SuggestTunnelRouteAsync(); Assert.True(vm.TunnelDraftPending, vm.Notification); Assert.NotEmpty(vm.TunnelDraftGeometry!.Positions!);
        vm.ResetTunnelDraftCommand.Execute(null); Assert.Equal(primary, vm.TunnelAxisText);
        vm.AddTunnelBranchCommand.Execute(null); Assert.Equal(2, vm.TunnelBranches.Count); Assert.Empty(vm.TunnelAxisText);
        vm.TunnelAxisText = "0 0 2\n3 1 2.3"; vm.ApplyTunnelAxisCommand.Execute(null); var secondary = vm.TunnelAxisText;
        vm.SelectedTunnelBranch = vm.TunnelBranches[0]; Assert.Equal(primary, vm.TunnelAxisText);
        vm.SelectedTunnelBranch = vm.TunnelBranches[1]; Assert.Equal(secondary, vm.TunnelAxisText);
        vm.RemoveTunnelBranchCommand.Execute(null); Assert.Single(vm.TunnelBranches);
        await vm.UndoProjectCommand.ExecuteAsync(); Assert.Equal(2, vm.TunnelBranches.Count);
        var panel = new TunnelAnalysisPanel { DataContext = vm }; ThemeRenderingTests.Layout(panel, 312, 4000);
        await Dispatcher.Yield(DispatcherPriority.DataBind); panel.UpdateLayout(); ThemeRenderingTests.CheckReadableText(panel);
        WorkspacePresentationTests.SavePreview(panel, "tunnel-workspace-full.png");
        using var guest = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: new TestAccess(false), interaction: new TestInteraction());
        await guest.LoadTunnelDemoAsync();
        Assert.False(guest.BuildTunnelMeshCommand.CanExecute(null)); Assert.False(guest.RecognizeTunnelCommand.CanExecute(null));
        Assert.False(guest.SuggestTunnelRouteCommand.CanExecute(null)); Assert.False(guest.AddTunnelBranchCommand.CanExecute(null));
        Assert.False(guest.ReviewTunnelObjectCommand.CanExecute(null)); Assert.False(guest.CanExportTunnelLabels);
    });

    [Fact]
    public async Task CancelledOperationsDoNotMutateTheOriginalProject()
    {
        var project = Project(Tube(Axis[0].Position, Axis[1].Position)); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TunnelMeshBuilder.BuildAsync(project, project.Tunnel!, new(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TunnelRecognitionService.AnalyzeAsync(project, project.Tunnel!, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TunnelRoutePlanner.SuggestAsync(project, token: cts.Token));
        Assert.Null(project.Tunnel!.Reconstruction); Assert.Null(project.Tunnel.Recognition); Assert.Empty(project.Tunnel.Branches);
    }

    [Theory]
    [InlineData("mesh")] [InlineData("recognition")] [InlineData("route")]
    public Task LosingAccessDuringTunnelOperationKeepsProjectAndHistory(string operation) => ScanControlTests.OnSta(async () =>
    {
        var access = new TestAccess(true);
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(), access: access, interaction: new TestInteraction());
        await vm.LoadTunnelDemoAsync(); var axis = vm.TunnelAxisText; var undo = vm.UndoProjectText;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsBusy) && vm.IsBusy) access.Logout(); };
        if (operation == "mesh") await vm.BuildTunnelMeshAsync();
        else if (operation == "recognition") await vm.RecognizeTunnelAsync();
        else await vm.SuggestTunnelRouteAsync();
        Assert.False(vm.HasTunnelReconstruction); Assert.False(vm.HasTunnelRecognition); Assert.False(vm.IsBusy);
        Assert.Equal(axis, vm.TunnelAxisText); Assert.Equal(undo, vm.UndoProjectText);
    });

    [Fact]
    public async Task CurvedSurfaceVerticesRemainNearActualMeasurements()
    {
        var project = TunnelDemoFactory.Create();
        var result = await TunnelMeshBuilder.BuildAsync(project, project.Tunnel!, new(CompletionGap: 0));
        var mesh = result.Reconstruction!; Assert.NotEmpty(mesh.ObservedTriangles);
        foreach (var point in mesh.Vertices.Where((_, i) => i % Math.Max(1, mesh.Vertices.Length / 120) == 0))
            Assert.True(project.Active!.Source.Points.Min(p => Vector3.DistanceSquared(p.Position, point.Position)) < .023,
                $"Mesh drifted away from observations at {point}");
    }

    [Theory]
    [InlineData("nested")] [InlineData("duplicate")] [InlineData("people")]
    public async Task InvalidBranchesAndAutomaticPeopleLabelsAreRejected(string kind)
    {
        var project = Project(Tube(Axis[0].Position, Axis[1].Position));
        var document = await TunnelRecognitionService.AnalyzeAsync(project, project.Tunnel!);
        var branch = new TunnelBranch(Guid.NewGuid(), "Ветвь", TunnelAnalyzer.Create(Axis));
        if (kind == "nested") document = document with { Branches = [branch with { Analysis = branch.Analysis with { Version = 2, Branches = [branch] } }] };
        if (kind == "duplicate") document = document with { Branches = [branch, branch] };
        if (kind == "people")
        {
            var recognition = TunnelRecognitionService.AddBox(project, document.Recognition!, new(new(1, -3, -2), new(2, 3, 3)));
            document = document with { Recognition = recognition with { Objects = recognition.Objects.Select(o => o with { SuggestedClass = TunnelPointClass.Person }).ToArray() } };
        }
        Assert.Throws<InvalidDataException>(() => TunnelAnalyzer.Validate(document, project));
    }
}
