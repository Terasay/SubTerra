using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelixToolkit.SharpDX;
using SubTerra.Models;
using SubTerra.Services;

namespace SubTerra.ViewModels;

public sealed record PersonViewChoice(int Index, string Label);

public sealed partial class MainViewModel
{
    private readonly IPersonScanAnalyzer _personAnalyzer;
    private PersonDetectionResult? _people;
    private PersonFinding? _selectedPerson;
    private PersonViewChoice? _selectedPersonView;
    private ImageSource? _personPreview;
    private LineGeometry3D? _personOutline;
    private double _personThreshold = .3;
    private bool _showPeople = true;
    public ObservableCollection<PersonFinding> PersonFindings { get; } = [];
    public ObservableCollection<PersonViewChoice> PersonViews { get; } = [];
    public AsyncRelayCommand DetectPeopleCommand { get; private set; } = null!;
    public AsyncRelayCommand SearchPeopleCommand { get; private set; } = null!;
    public RelayCommand ConfirmPersonCommand { get; private set; } = null!;
    public RelayCommand RejectPersonCommand { get; private set; } = null!;
    public RelayCommand ResetPersonReviewCommand { get; private set; } = null!;
    public RelayCommand FocusPersonViewCommand { get; private set; } = null!;
    public bool CanDetectPeople => IsAdministrator && IsImported && CanInteract && !HasEditPreview && !HasRecovery && !IsScreenSelecting &&
        _scanProject?.Objects.Any(o => o.IsVisible && o.Source.Colors.Length > 0) == true;
    public bool CanSearchPeople => CanDetectPeople && HasStructureAnalysis && !StructureIsStale && StructureSurfaces.Any(s => s.Kind == StructureSurfaceKind.Floor && s.Review != StructureReviewState.Rejected);
    public bool CanReviewPerson => CanDetectPeople && SelectedPerson is not null;
    public bool HasPeopleDetection => _people is not null;
    public bool PersonBoxesVisible => HasPeopleDetection && ShowPeople && PersonOutline is not null;
    public bool ShowPeople { get => _showPeople; set { if (Set(ref _showPeople, value)) Raise(nameof(PersonBoxesVisible)); } }
    public double PersonThreshold { get => _personThreshold; set { if (double.IsFinite(value) && value is >= .1 and <= .9) Set(ref _personThreshold, value); } }
    public ImageSource? PersonPreview { get => _personPreview; private set => Set(ref _personPreview, value); }
    public LineGeometry3D? PersonOutline { get => _personOutline; private set => Set(ref _personOutline, value); }
    public PersonViewChoice? SelectedPersonView { get => _selectedPersonView; set { if (value is not null && PersonViews.Contains(value) && Set(ref _selectedPersonView, value)) RefreshPersonPreview(); } }
    public PersonFinding? SelectedPerson
    {
        get => _selectedPerson;
        set
        {
            if (value is not null && !PersonFindings.Contains(value) || !Set(ref _selectedPerson, value)) return;
            if (value is not null) SelectedPersonView = PersonViews.First(v => v.Index == value.ViewIndex);
            PersonOutline = value is { Minimum: { } min, Maximum: { } max } ? Rendering.SceneGeometry.Lines(BoxLines(min, max)) : null;
            NotifyPeopleState();
        }
    }
    public string PeopleSummary => _people is null ? "Локальная YOLOX-Nano ищет людей на RGB-виде скана. Требуется проверка оператором." :
        $"Ракурсов: {_people.Views.Length}; кандидатов в кадрах: {PersonFindings.Count}. Подтверждено: {PersonFindings.Count(p => p.Review == StructureReviewState.Confirmed)}, " +
        $"ложных: {PersonFindings.Count(p => p.Review == StructureReviewState.Rejected)}, не проверено: {PersonFindings.Count(p => p.Review == StructureReviewState.Proposed)}. " +
        "Один человек может встречаться в нескольких ракурсах. Оценка модели не является точностью.";
    public string PersonViewSummary => _people is not null && SelectedPersonView is { } selected ?
        $"RGB-вид измерений · заполнение кадра {_people.Views[selected.Index].Coverage:P0}. Найдено: {PersonFindings.Count(p => p.ViewIndex == selected.Index)}. " +
        "Отсутствие рамки не доказывает отсутствие человека; скрытые части и склейки людей могут давать ошибки." : "";

    private void InitializePeopleCommands()
    {
        DetectPeopleCommand = new(() => DetectPeopleAsync(false), () => CanDetectPeople, ex => ReportError("Не удалось проверить людей", ex));
        SearchPeopleCommand = new(() => DetectPeopleAsync(true), () => CanSearchPeople, ex => ReportError("Не удалось осмотреть комнату", ex));
        ConfirmPersonCommand = new(() => ReviewPerson(StructureReviewState.Confirmed), () => CanReviewPerson);
        RejectPersonCommand = new(() => ReviewPerson(StructureReviewState.Rejected), () => CanReviewPerson);
        ResetPersonReviewCommand = new(() => ReviewPerson(StructureReviewState.Proposed), () => CanReviewPerson);
        FocusPersonViewCommand = new(FocusPersonView, () => CanInteract && SelectedPersonView is not null);
    }
    public async Task DetectPeopleAsync(bool survey = false)
    {
        if (!(survey ? CanSearchPeople : CanDetectPeople) || _scanProject is not { } project) return;
        var poses = new[] { new ScanViewPose(new((float)Camera.Position.X, (float)Camera.Position.Y, (float)Camera.Position.Z),
            new((float)Camera.LookDirection.X, (float)Camera.LookDirection.Y, (float)Camera.LookDirection.Z),
            new((float)Camera.UpDirection.X, (float)Camera.UpDirection.Y, (float)Camera.UpDirection.Z), (float)Camera.FieldOfView) };
        if (survey)
            poses = PersonScanAnalyzer.RoomViews(_structureDocument!);
        var surfaces = !StructureIsStale ? StructureSurfaces.ToArray() : [];
        var threshold = (float)PersonThreshold; var clip = ActiveClipPlane;
        EndInspection(); ReturnToOverview();
        var token = BeginOperation("Локальный поиск людей · RGB-ракурсы → ONNX");
        try
        {
            var result = await _personAnalyzer.AnalyzeAsync(project, poses, surfaces, threshold, clip, OperationProgress(token), token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator || !ReferenceEquals(project, _scanProject)) return;
            ClearPersonDetections(); _people = result;
            foreach (var finding in result.Findings) PersonFindings.Add(finding);
            for (var i = 0; i < result.Views.Length; i++) PersonViews.Add(new(i, $"Ракурс {i + 1} · кандидатов {result.Findings.Count(p => p.ViewIndex == i)}"));
            SelectedPersonView = PersonViews.FirstOrDefault(); SelectedPerson = PersonFindings.FirstOrDefault();
            Status = $"Проверено ракурсов: {result.Views.Length} · кандидатов людей: {result.Findings.Length} · требуется визуальная проверка";
            Notification = ""; AddEvent(Status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось проверить людей", ex); }
        finally { EndOperation(); NotifyPeopleState(); }
    }
    private void ReviewPerson(StructureReviewState review)
    {
        if (!CanReviewPerson || SelectedPerson is not { } before) return;
        var index = PersonFindings.IndexOf(before); var after = before with { Review = review }; PersonFindings[index] = after;
        SelectedPerson = after; RefreshPersonPreview(); NotifyPeopleState();
    }
    private void FocusPersonView()
    {
        if (!CanInteract || _people is null || SelectedPersonView is not { } selected) return;
        ReturnToOverview(); var pose = _people.Views[selected.Index].Pose;
        MoveCamera(pose.Position, pose.Position + Vector3.Normalize(pose.Forward) * 3, "Ракурс проверки людей");
        Camera.UpDirection = new(pose.Up.X, pose.Up.Y, pose.Up.Z); Camera.FieldOfView = pose.FieldOfView;
    }
    private void RefreshPersonPreview()
    {
        if (_people is null || SelectedPersonView is not { } selected) { PersonPreview = null; return; }
        var image = _people.Views[selected.Index];
        var bitmap = BitmapSource.Create(image.Size, image.Size, 96, 96, PixelFormats.Rgb24, null, image.Rgb, image.Size * 3); bitmap.Freeze();
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawImage(bitmap, new Rect(0, 0, image.Size, image.Size));
            foreach (var finding in PersonFindings.Where(p => p.ViewIndex == selected.Index))
            {
                var box = finding.Box; var brush = finding.Review switch { StructureReviewState.Confirmed => Brushes.LightGreen, StructureReviewState.Rejected => Brushes.IndianRed, _ => Brushes.Orange };
                context.DrawRectangle(null, new Pen(brush, 2), new Rect(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top));
                var text = new FormattedText($"{finding.Id} · человек? {box.Score:P0}", CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, 1);
                var y = Math.Max(0, box.Top - 18); context.DrawRectangle(Brushes.Black, null, new Rect(box.Left, y, Math.Min(image.Size - box.Left, text.Width + 6), 18));
                context.DrawText(text, new Point(box.Left + 2, y));
            }
        }
        drawing.Freeze(); var preview = new DrawingImage(drawing); preview.Freeze(); PersonPreview = preview; Raise(nameof(PersonViewSummary));
    }
    private static IEnumerable<Vector3> BoxLines(Vector3 min, Vector3 max)
    {
        Vector3[] v = [new(min.X,min.Y,min.Z),new(max.X,min.Y,min.Z),new(max.X,max.Y,min.Z),new(min.X,max.Y,min.Z),
            new(min.X,min.Y,max.Z),new(max.X,min.Y,max.Z),new(max.X,max.Y,max.Z),new(min.X,max.Y,max.Z)];
        foreach (var (a,b) in new[] { (0,1),(1,2),(2,3),(3,0),(4,5),(5,6),(6,7),(7,4),(0,4),(1,5),(2,6),(3,7) }) { yield return v[a]; yield return v[b]; }
    }
    private void ClearPersonDetections()
    {
        _people = null; _selectedPerson = null; _selectedPersonView = null; PersonFindings.Clear(); PersonViews.Clear(); PersonPreview = null; PersonOutline = null;
        Raise(nameof(SelectedPerson)); Raise(nameof(SelectedPersonView)); NotifyPeopleState();
    }
    private void NotifyPeopleState()
    {
        foreach (var name in new[] { nameof(CanDetectPeople), nameof(CanSearchPeople), nameof(CanReviewPerson), nameof(HasPeopleDetection), nameof(PersonBoxesVisible), nameof(PeopleSummary), nameof(PersonViewSummary) }) Raise(name);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
