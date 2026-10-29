using System.Windows;
using System.Collections.ObjectModel;
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
    private string _roomFloor = "Этаж 1";
    private bool _useRoomInteriorPoint, _showAllRooms;
    public ObservableCollection<SavedRoom> SavedRooms { get; } = [];
    public ObservableCollection<RoomSurfaceChoice> RoomSurfaceChoices { get; } = [];
    public string RoomFloor { get => _roomFloor; set { if (value is not null) Set(ref _roomFloor, value); } }
    public bool UseRoomInteriorPoint { get => _useRoomInteriorPoint; set => Set(ref _useRoomInteriorPoint, value); }
    public bool ShowAllRooms { get => _showAllRooms; set { if (Set(ref _showAllRooms, value)) { UpdateRoomDisplay(); Raise(nameof(RoomVisible)); } } }
    public Guid SelectedRoomId
    {
        get => _structureDocument?.ActiveRoomId ?? Guid.Empty;
        set
        {
            if (!CanInteract || HasEditPreview || value == SelectedRoomId || _structureDocument is not { } document || document.Rooms?.FirstOrDefault(r => r.Id == value) is not { } selected) return;
            var next = document with { ActiveRoomId = value, Room = selected.Plan };
            _scanProject = _scanProject! with { Structure = next }; _dataset = new(null, _scanProject.ToSession(UserAnnotations.ToArray()));
            SetStructureDocument(next);
        }
    }
    public string RoomsSummary => $"Сохранено комнат: {SavedRooms.Count} · этажей: {SavedRooms.Select(r => r.Floor).Distinct(StringComparer.OrdinalIgnoreCase).Count()}";
    public string RoomConnectionsText => _structureDocument is not { } document ? "" : string.Join("\n", RoomTopology.Connections(document).Select(c =>
        $"{SavedRooms.First(r => r.Id == c.FirstRoomId).Plan.Name} ↔ {SavedRooms.First(r => r.Id == c.SecondRoomId).Plan.Name}: " + (c.HasConfirmedOpening ? "есть проём от оператора" : "общая граница, проём не подтверждён")));
    public string RoomObjectMembershipText => _structureDocument?.Room is not { HasHeight: true } room ? "Для привязки предметов нужна высота комнаты." :
        ObjectCandidatesAreStale || RoomIsStale ? "Привязка предметов устарела." : $"Предметов внутри по центру габаритов: {ObjectCandidates.Count(o => o.Review != StructureReviewState.Rejected && RoomTopology.Contains(room, (o.Minimum + o.Maximum) / 2))}. Граничные предметы требуют проверки.";
    public RelayCommand NewRoomCommand { get; private set; } = null!;
    public RelayCommand ApplyRoomIdentityCommand { get; private set; } = null!;
    private int _roomVertexIndex;
    private double _roomVertexX, _roomVertexY, _openingStart, _openingEnd = 1;
    public bool CanEditRoom => CanManageProject && !IsScreenSelecting && HasRoom && !RoomIsStale;
    public bool HasRoomEdits => _structureDocument?.Room?.IsManuallyEdited == true;
    public bool CanResetRoomEdits => CanManageProject && !IsScreenSelecting && HasRoomEdits;
    public IReadOnlyList<RoomVertexChoice> RoomVertices => _structureDocument?.Room?.FloorContour.Select((p, i) => new RoomVertexChoice(i, $"Вершина {i + 1} · X {p.X:F3}, Y {p.Y:F3}")).ToArray() ?? [];
    public int RoomVertexIndex
    {
        get => _roomVertexIndex;
        set { if (value >= 0 && value < RoomVertices.Count && Set(ref _roomVertexIndex, value)) LoadRoomVertex(); }
    }
    public double RoomVertexX { get => _roomVertexX; set => Set(ref _roomVertexX, value); }
    public double RoomVertexY { get => _roomVertexY; set => Set(ref _roomVertexY, value); }
    public double RoomOpeningStart { get => _openingStart; set => Set(ref _openingStart, value); }
    public double RoomOpeningEnd { get => _openingEnd; set => Set(ref _openingEnd, value); }
    public IReadOnlyList<RoomGap> RoomOpenings => _structureDocument?.Room?.Gaps ?? [];
    public AsyncRelayCommand ApplyRoomVertexCommand { get; private set; } = null!;
    public AsyncRelayCommand InsertRoomVertexCommand { get; private set; } = null!;
    public AsyncRelayCommand RemoveRoomVertexCommand { get; private set; } = null!;
    public AsyncRelayCommand AddRoomOpeningCommand { get; private set; } = null!;
    public RelayCommand<RoomGap> RemoveRoomOpeningCommand { get; private set; } = null!;
    public RelayCommand ResetRoomEditsCommand { get; private set; } = null!;
    public AsyncRelayCommand BuildRoomCommand { get; private set; } = null!;
    public RelayCommand RemoveRoomCommand { get; private set; } = null!;
    public bool CanBuildRoom => CanManageProject && !IsScreenSelecting && HasStructureAnalysis && !StructureIsStale;
    public bool HasRoom => _structureDocument?.Room is not null;
    public bool RoomIsStale => HasRoom && _roomIsStale;
    public bool RoomVisible => ShowRoom && IsImported && (HasRoom && !RoomIsStale || ShowAllRooms && _structureDocument is { } d && SavedRooms.Any(r => RoomPlanner.IsCurrent(d with { Room = r.Plan })));
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
        $"{_structureDocument!.Room!.Name} · границы по {_structureDocument.Room.WallSurfaceIds.Distinct().Count()} опорным плоскостям. Это геометрическая гипотеза, требующая проверки.";
    public string RoomWarnings => _structureDocument?.Room is not { } r ? "" :
        (r.IsManuallyEdited ? "Контур исправлен оператором; автоматический результат сохранён отдельно.\n" : "") +
        (!r.HasHeight ? "Потолок не подтверждён: высота неизвестна.\n" : "") +
        (r.Gaps.Any(g => !g.IsConfirmedOpening) ? $"Предполагаемых проёмов / пропусков: {r.Gaps.Count(g => !g.IsConfirmedOpening)} (оранжевые участки).\n" : "") +
        (r.Gaps.Any(g => g.IsConfirmedOpening) ? $"Проёмов от оператора: {r.Gaps.Count(g => g.IsConfirmedOpening)} (голубые участки на плане).\n" : "") +
        (r.IncompleteWallIds.Length > 0 ? $"Неполные данные по высоте стен: {r.IncompleteWallIds.Length}.\n" : "") +
        "Пересечения плоскостей не подтверждают непрерывность стен. Площадь и периметр приблизительные.";

    private void InitializeRoomCommands()
    {
        ApplyRoomIdentityCommand = new(() =>
        {
            if (!CanEditRoom || _structureDocument?.Room is not { } room) return;
            var name = RoomName.Trim(); var floor = RoomFloor.Trim();
            if (name.Length is < 1 or > 80 || floor.Length is < 1 or > 80) { RoomBuildMessage = "Название комнаты и этажа: от 1 до 80 символов."; return; }
            var id = SelectedRoomId == Guid.Empty ? Guid.NewGuid() : SelectedRoomId; var renamed = room with { Name = name };
            CommitStructureDocument(_structureDocument with { Room = renamed, ActiveRoomId = id,
                Rooms = [.. (_structureDocument.Rooms ?? []).Where(r => r.Id != id), new SavedRoom(id, floor, renamed)] }, "название комнаты и этажа");
        }, () => CanEditRoom);
        NewRoomCommand = new(() =>
        {
            if (!CanBuildRoom || _structureDocument is not { } document) return;
            var rooms = document.Rooms ?? (document.Room is { } old ? [new SavedRoom(Guid.NewGuid(), RoomFloor, old)] : []);
            if (rooms.Length >= 64) { RoomBuildMessage = "Лимит проекта — 64 комнаты."; return; }
            CommitStructureDocument(document with { Rooms = rooms, ActiveRoomId = Guid.Empty, Room = null }, "новая комната");
            RoomName = $"Комната {rooms.Length + 1:00}";
        }, () => CanBuildRoom);
        ApplyRoomVertexCommand = new(() => EditRoomAsync((d, t) => RoomPlanEditor.MoveVertex(d, RoomVertexIndex, RoomVertexX, RoomVertexY, t), "перемещение вершины комнаты"), () => CanEditRoom, ex => ReportError("Не удалось изменить вершину", ex));
        InsertRoomVertexCommand = new(() => EditRoomAsync((d, t) => RoomPlanEditor.InsertVertex(d, RoomVertexIndex, t), "добавление вершины комнаты"), () => CanEditRoom && RoomVertices.Count < 24, ex => ReportError("Не удалось добавить вершину", ex));
        RemoveRoomVertexCommand = new(() => EditRoomAsync((d, t) => RoomPlanEditor.RemoveVertex(d, RoomVertexIndex, t), "удаление вершины комнаты"), () => CanEditRoom && RoomVertices.Count > 3, ex => ReportError("Не удалось удалить вершину", ex));
        AddRoomOpeningCommand = new(() => EditRoomAsync((d, t) => RoomPlanEditor.AddOpening(d, RoomVertexIndex, RoomOpeningStart, RoomOpeningEnd, t), "добавление проёма комнаты"), () => CanEditRoom, ex => ReportError("Не удалось добавить проём", ex));
        RemoveRoomOpeningCommand = new(gap => _ = EditRoomAsync((d, t) => RoomPlanEditor.RemoveGap(d, gap, t), "удаление проёма комнаты"), _ => CanEditRoom);
        ResetRoomEditsCommand = new(() =>
        {
            if (!CanResetRoomEdits || _structureDocument?.Room?.AutomaticPlan is not { } automatic) return;
            CommitStructureDocument(_structureDocument with { Version = 2, Room = automatic }, "возврат автоматического контура");
        }, () => CanResetRoomEdits);
        BuildRoomCommand = new(BuildRoomAsync, () => CanBuildRoom, ex => ReportError("Не удалось построить комнату", ex));
        RemoveRoomCommand = new(() =>
        {
            if (_structureDocument is not { } document) return;
            var rooms = document.Rooms?.Where(r => r.Id != document.ActiveRoomId).ToArray(); var selected = rooms?.FirstOrDefault();
            CommitStructureDocument(document with { Room = selected?.Plan, Rooms = rooms, ActiveRoomId = selected?.Id ?? Guid.Empty }, "удаление плана комнаты");
        }, () => CanManageProject && HasRoom && !IsScreenSelecting);
    }
    public async Task BuildRoomAsync()
    {
        if (!CanBuildRoom || _scanProject is not { } project || _structureDocument is not { } document) return;
        if (HasRoomEdits)
        {
            RoomBuildMessage = "Сначала явно верните автоматический контур. Ручные правки не заменяются пересчётом; возврат можно отменить.";
            return;
        }
        var name = RoomName; var floorName = RoomFloor.Trim(); var selectedSurfaces = RoomSurfaceChoices.Where(s => s.IsSelected).Select(s => s.Id).ToArray();
        var seed = UseRoomInteriorPoint ? _pickedPoint?.Position : null;
        if (floorName.Length is < 1 or > 80) { RoomBuildMessage = "Название этажа: от 1 до 80 символов."; return; }
        if (UseRoomInteriorPoint && seed is null) { RoomBuildMessage = "Сначала выберите в 3D точку внутри комнаты."; return; }
        var token = BeginOperation("Построение плана комнаты");
        try
        {
            var result = await Task.Run(() => RoomPlanner.Build(document, name, token, selectedSurfaces, seed), token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator || !ReferenceEquals(project, _scanProject) || !ReferenceEquals(document, _structureDocument)) return;
            if (result.Room is not null)
            {
                var next = document with { Version = 2, Room = result.Room };
                var rooms = document.Rooms ?? [];
                {
                    var id = document.ActiveRoomId == Guid.Empty ? Guid.NewGuid() : document.ActiveRoomId;
                    if (document.ActiveRoomId == Guid.Empty && rooms.Length >= 64) { RoomBuildMessage = "Лимит проекта — 64 комнаты."; return; }
                    next = next with { ActiveRoomId = id, Rooms = [.. rooms.Where(r => r.Id != id), new SavedRoom(id, floorName, result.Room)] };
                }
                CommitStructureDocument(next, "построение плана комнаты");
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
        SavedRooms.Clear(); foreach (var saved in _structureDocument?.Rooms ?? []) SavedRooms.Add(saved);
        if (SavedRooms.FirstOrDefault(r => r.Id == SelectedRoomId) is { } active) RoomFloor = active.Floor;
        var scoped = _structureDocument?.Room?.SurfaceIds;
        RoomSurfaceChoices.Clear();
        foreach (var surface in _structureDocument?.Result.Surfaces.Where(s => s.Review == StructureReviewState.Confirmed && s.Kind != StructureSurfaceKind.Unknown) ?? [])
            RoomSurfaceChoices.Add(new(surface.Id, surface.Title, scoped is null || scoped.Contains(surface.Id)));
        _roomVertexIndex = Math.Clamp(_roomVertexIndex, 0, Math.Max(0, RoomVertices.Count - 1));
        Raise(nameof(RoomVertices)); Raise(nameof(RoomVertexIndex)); Raise(nameof(RoomOpenings)); LoadRoomVertex();
        NotifyRoomState();
    }
    private void LoadRoomVertex()
    {
        if (_structureDocument?.Room is not { } room || _roomVertexIndex >= room.FloorContour.Length) return;
        var p = room.FloorContour[_roomVertexIndex]; _roomVertexX = p.X; _roomVertexY = p.Y;
        Raise(nameof(RoomVertexX)); Raise(nameof(RoomVertexY));
    }
    private async Task EditRoomAsync(Func<StructureAnalysisDocument, CancellationToken, RoomPlan> edit, string description)
    {
        if (!CanEditRoom || _scanProject is not { } project || _structureDocument is not { } document) return;
        var token = BeginOperation("Правка плана комнаты");
        try
        {
            var room = await Task.Run(() => edit(document, token), token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator || !ReferenceEquals(_scanProject, project) || !ReferenceEquals(_structureDocument, document)) return;
            CommitStructureDocument(document with { Version = 3, Room = room }, description);
            Notification = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось изменить план комнаты", ex); }
        finally { EndOperation(); }
    }
    private void UpdateRoomDisplay()
    {
        if (_structureDocument is { } document && ShowAllRooms && SavedRooms.Count > 0)
        {
            var outlines = new List<System.Numerics.Vector3>(); var openings = new List<System.Numerics.Vector3>();
            foreach (var saved in SavedRooms.Where(r => RoomPlanner.IsCurrent(document with { Room = r.Plan })))
            {
                var geometry = Rendering.RoomGeometry.Create(saved.Plan, ActiveClipPlane);
                outlines.AddRange(geometry.Outline.Positions!); openings.AddRange(geometry.Gaps.Positions!);
            }
            RoomOutline = Rendering.SceneGeometry.Lines(outlines); RoomGapOutline = Rendering.SceneGeometry.Lines(openings);
        }
        else if (HasRoom && !RoomIsStale) (RoomOutline, RoomGapOutline) = Rendering.RoomGeometry.Create(_structureDocument!.Room!, ActiveClipPlane);
        else RoomOutline = RoomGapOutline = null;
        if (!HasRoom || RoomIsStale) { RoomPlanPoints = null; RoomPlanGaps = null; return; }
        var room = _structureDocument!.Room!;
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
            nameof(RoomAreaText), nameof(RoomPerimeterText), nameof(RoomHeightText), nameof(RoomSummary), nameof(RoomWarnings),
            nameof(CanEditRoom), nameof(HasRoomEdits), nameof(CanResetRoomEdits), nameof(SelectedRoomId), nameof(RoomsSummary), nameof(RoomConnectionsText), nameof(RoomObjectMembershipText) }) Raise(name);
    }
}

public sealed record RoomVertexChoice(int Index, string Label);
public sealed class RoomSurfaceChoice(int id, string label, bool selected) : ObservableObject
{
    private bool _selected = selected;
    public int Id { get; } = id;
    public string Label { get; } = label;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}
