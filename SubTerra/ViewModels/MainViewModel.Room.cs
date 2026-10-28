using System.Windows;
using System.Windows.Media;
using HelixToolkit.SharpDX;
using SubTerra.Models;
using SubTerra.Services;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel
{
    private string _roomName = "Комната 01", _roomBuildMessage = "";
    private bool _showRoom = true, _roomIsStale;
    private LineGeometry3D? _roomOutline, _roomGapOutline;
    private PointCollection? _roomPlanPoints;
    private Geometry? _roomPlanGaps;
    public AsyncRelayCommand BuildRoomCommand { get; private set; } = null!;
    public RelayCommand RemoveRoomCommand { get; private set; } = null!;
    public bool CanBuildRoom => CanManageProject && !IsScreenSelecting && HasStructureAnalysis && !StructureIsStale;
    public bool HasRoom => _structureDocument?.Room is not null;
    public bool RoomIsStale => HasRoom && _roomIsStale;
    public bool RoomVisible => HasRoom && !RoomIsStale && ShowRoom && IsImported;
    public bool RoomPlanVisible => HasRoom && !RoomIsStale;
    public string RoomName { get => _roomName; set { if (value is not null) Set(ref _roomName, value); } }
    public string RoomBuildMessage { get => _roomBuildMessage; private set => Set(ref _roomBuildMessage, value); }
    public bool ShowRoom { get => _showRoom; set { if (Set(ref _showRoom, value)) Raise(nameof(RoomVisible)); } }
    public LineGeometry3D? RoomOutline { get => _roomOutline; private set => Set(ref _roomOutline, value); }
    public LineGeometry3D? RoomGapOutline { get => _roomGapOutline; private set => Set(ref _roomGapOutline, value); }
    public PointCollection? RoomPlanPoints { get => _roomPlanPoints; private set => Set(ref _roomPlanPoints, value); }
    public Geometry? RoomPlanGaps { get => _roomPlanGaps; private set => Set(ref _roomPlanGaps, value); }
    public string RoomAreaText => _structureDocument?.Room is { } r ? $"≈ {r.AreaSquareMeters:F2} м²" : "—";
    public string RoomPerimeterText => _structureDocument?.Room is { } r ? $"≈ {r.PerimeterMeters:F2} м" : "—";
    public string RoomHeightText => _structureDocument?.Room is { MinimumHeight: { } min, MaximumHeight: { } max } ?
        max - min < .02 ? $"≈ {(min + max) / 2:F2} м" : $"≈ {min:F2}–{max:F2} м" : "Неизвестна · нет потолка";
    public string RoomSummary => !HasRoom ? "Подтвердите один пол, стены и, при наличии, потолок. Стены должны замыкать выпуклый контур одной комнаты." :
        RoomIsStale ? "ПЛАН УСТАРЕЛ · изменились сканы или подтверждённые поверхности. Постройте план заново или отмените изменение." :
        $"{_structureDocument!.Room!.Name} · границы по {_structureDocument.Room.WallSurfaceIds.Length} опорным плоскостям. Это геометрическая гипотеза, требующая проверки.";
    public string RoomWarnings => _structureDocument?.Room is not { } r ? "" :
        (!r.HasHeight ? "Потолок не подтверждён: высота неизвестна.\n" : "") +
        (r.Gaps.Length > 0 ? $"Пропусков в нижней части стен: {r.Gaps.Length}. Возможны проёмы или недостаточное сканирование (оранжевые участки).\n" : "") +
        (r.IncompleteWallIds.Length > 0 ? $"Неполные данные по высоте стен: {r.IncompleteWallIds.Length}.\n" : "") +
        "Пересечения плоскостей не подтверждают непрерывность стен. Площадь и периметр приблизительные.";

    private void InitializeRoomCommands()
    {
        BuildRoomCommand = new(BuildRoomAsync, () => CanBuildRoom, ex => ReportError("Не удалось построить комнату", ex));
        RemoveRoomCommand = new(() => CommitStructureDocument(_structureDocument! with { Version = 1, Room = null }, "удаление плана комнаты"), () => CanManageProject && HasRoom && !IsScreenSelecting);
    }
    public async Task BuildRoomAsync()
    {
        if (!CanBuildRoom || _scanProject is not { } project || _structureDocument is not { } document) return;
        var name = RoomName; var token = BeginOperation("Построение плана комнаты");
        try
        {
            var result = await Task.Run(() => RoomPlanner.Build(document, name, token), token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator || !ReferenceEquals(project, _scanProject) || !ReferenceEquals(document, _structureDocument)) return;
            if (result.Room is not null)
            {
                CommitStructureDocument(document with { Version = 2, Room = result.Room }, "построение плана комнаты");
                ShowRoom = true;
            }
            RoomBuildMessage = result.Message; Status = result.Message;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось построить комнату", ex); }
        finally { EndOperation(); }
    }
    private void RefreshRoomDocument()
    {
        RoomBuildMessage = "";
        _roomIsStale = _structureDocument is { Room: not null } document && !RoomPlanner.IsCurrent(document);
        if (_structureDocument?.Room is { } room) RoomName = room.Name;
        NotifyRoomState();
    }
    private void UpdateRoomDisplay()
    {
        if (!HasRoom || RoomIsStale) { RoomOutline = RoomGapOutline = null; RoomPlanPoints = null; RoomPlanGaps = null; return; }
        var room = _structureDocument!.Room!;
        (RoomOutline, RoomGapOutline) = Rendering.RoomGeometry.Create(room, ActiveClipPlane);
        var xs = room.FloorContour.Select(p => p.X); var ys = room.FloorContour.Select(p => p.Y);
        var minX = xs.Min(); var maxY = ys.Max(); var width = xs.Max() - minX; var height = maxY - ys.Min();
        var scale = Math.Min(220 / Math.Max(.001, width), 120 / Math.Max(.001, height));
        var offsetX = (240 - width * scale) / 2; var offsetY = (140 - height * scale) / 2;
        var points = new PointCollection(room.FloorContour.Select(p => new Point(offsetX + (p.X - minX) * scale, offsetY + (maxY - p.Y) * scale)));
        points.Freeze(); RoomPlanPoints = points;
        var gaps = new GeometryGroup();
        foreach (var gap in room.Gaps)
        {
            var a = points[gap.EdgeIndex]; var b = points[(gap.EdgeIndex + 1) % points.Count];
            gaps.Children.Add(new LineGeometry(a + (b - a) * gap.Start, a + (b - a) * gap.End));
        }
        gaps.Freeze(); RoomPlanGaps = gaps;
    }
    private void NotifyRoomState()
    {
        foreach (var name in new[] { nameof(CanBuildRoom), nameof(HasRoom), nameof(RoomIsStale), nameof(RoomVisible), nameof(RoomPlanVisible),
            nameof(RoomAreaText), nameof(RoomPerimeterText), nameof(RoomHeightText), nameof(RoomSummary), nameof(RoomWarnings) }) Raise(name);
    }
}
