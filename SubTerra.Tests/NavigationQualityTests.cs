using System.IO;
using System.Numerics;
using System.Xml.Linq;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class NavigationQualityTests
{
    [Fact]
    public void MillionPointCloudIsNotSilentlyCappedAndSmallCloudsReuseTheirBuffers()
    {
        const int count = 1_100_003;
        var scan = new ImportedScan { Metadata = new("dense.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0),
            Points = Enumerable.Range(0, count).Select(i => new ScanPoint(i * .001f, i % 11, i % 7, 1)).ToArray() };
        var geometry = new ImportedGeometry(scan);
        var limited = geometry.Cloud(1_000_000, null); Assert.Equal(1_000_000, limited.Positions!.Count);
        var all = geometry.Cloud(PointCloudQuality.All[^1].PointLimit, null);
        Assert.Equal(count, all.Positions!.Count); Assert.Equal(scan.Points[^1].Position, all.Positions[^1]);
        Assert.Same(all, geometry.Cloud(3_000_000, null)); Assert.Equal(count, scan.Points.Length);
        Assert.Equal(5_000_000, PointCloudQuality.All.Max(q => q.PointLimit));
        Assert.Equal(1_000_000, new WorkspacePreferences { DisplayPointLimit = -1 }.Normalize().DisplayPointLimit);
    }

    [Fact]
    public Task CampusMeshRemainsLitAndDetailedInFlightAndHomeProvidesAnExit() => ScanControlTests.OnSta(async () =>
    {
        using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(),
            access: new TestAccess(false), interaction: new TestInteraction());
        await vm.LoadCollegeBuildingAsync(); vm.SurfaceCommand.Execute(null);
        Assert.True(vm.IsSurface); Assert.True(vm.SceneLightingVisible);
        var sourceView = vm.Surface; Assert.NotNull(sourceView);
        var position = vm.Camera.Position; var direction = vm.Camera.LookDirection;
        vm.NotifyCameraInteraction(); Assert.Same(sourceView, vm.Surface);
        vm.FlyCommand.Execute(null);
        Assert.True(vm.IsFlying); Assert.False(vm.IsOverview); Assert.True(vm.SceneLightingVisible);
        Assert.Equal(position, vm.Camera.Position); Assert.Same(sourceView, vm.Surface);
        Assert.InRange(vm.Camera.NearPlaneDistance, .00001, .01);
        var flight = vm.CreateFlyNavigator(); Assert.True(flight.KeepAltitude);
        var height = flight.Position.Z;
        flight.Move(1, 0, .1f, false); vm.UpdateWalkPosition(flight);
        Assert.Equal(height, flight.Position.Z);
        vm.HomeCommand.Execute(null);
        Assert.False(vm.IsNavigating); Assert.Equal(position, vm.Camera.Position); Assert.Equal(direction, vm.Camera.LookDirection);
        Assert.True(vm.SceneLightingVisible); Assert.False(vm.HasNotification, vm.Notification);

        var shell = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ThemeExamples", "MainWindow.xaml"));
        var lit = shell.Descendants().Where(e => (string?)e.Attribute("IsRendering") == "{Binding SceneLightingVisible}").ToArray();
        Assert.Contains(lit, e => e.Name.LocalName == "AmbientLight3D");
        Assert.Contains(lit, e => e.Name.LocalName == "DirectionalLight3D");
        Assert.Contains(shell.Descendants(), e => (string?)e.Attribute("Command") == "{Binding HomeCommand}" &&
            (string?)e.Attribute("Content") == "Весь объект · Home");
    });

    [Fact]
    public void MeshColorModesShareGeometryWithoutSimplifyingSmallMeshes()
    {
        var scan = new ImportedScan { Metadata = new("mesh.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0),
            Points = [new(0, 0, 0, 1), new(2, 0, 0, 1), new(0, 2, 1, 1)],
            Colors = [0x123456, 0xABCDEF, 0x654321], Triangles = [0, 1, 2] };
        var geometry = new ImportedGeometry(scan); geometry.PrepareMeshPreviews();
        var rgb = geometry.Mesh(false); var height = geometry.Mesh(true);
        Assert.Same(rgb.Positions, height.Positions); Assert.Same(rgb.Normals, height.Normals); Assert.Same(rgb.Indices, height.Indices);
        Assert.NotSame(rgb.Colors, height.Colors); Assert.Same(rgb, geometry.Mesh(false, true));
        Assert.Same(height, geometry.Mesh(true, true)); Assert.Equal(1, geometry.MeshTriangleCount(false));
        Assert.Equal(new uint[] { 0x123456, 0xABCDEF, 0x654321 }, scan.Colors);
    }
}
