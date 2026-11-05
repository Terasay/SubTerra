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
    private readonly PersonCandidateLearning _personLearning;
    private string? _learningError;
    public string PersonLearningSummary => _learningError ??
        $"Отметки · человек: {_personLearning.Positives} · ошибка: {_personLearning.Negatives}. {ActivePersonModelText}.";
    private void LoadPersonExamples()
    {
        try { _personLearning.Reload(); _learningError = null; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.IO.InvalidDataException)
        { _learningError = "Примеры обучения недоступны. Используется базовая модель. " + ex.Message; }
        Raise(nameof(PersonLearningSummary));
        Raise(nameof(TrainingExamplesText));
        Raise(nameof(TrainingExamplesHint));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
    private PersonDetectionResult? _people;
    private string[] _personSources = [];
    private PersonFinding? _selectedPerson;
    private PersonViewChoice? _selectedPersonView;
    private ImageSource? _personPreview;
    private LineGeometry3D? _personOutline;
    private LineGeometry3D? _peopleProposedOutline, _peopleConfirmedOutline, _peopleRejectedOutline;
    private double _personThreshold = .3;
    private bool _showPeople = true, _isPeoplePreviewOpen;
    private string _peopleRunMode = "";
    public ObservableCollection<PersonFinding> PersonFindings { get; } = [];
    public ObservableCollection<PersonViewChoice> PersonViews { get; } = [];
    public AsyncRelayCommand DetectPeopleCommand { get; private set; } = null!;
    public AsyncRelayCommand SearchPeopleCommand { get; private set; } = null!;
    public AsyncRelayCommand FindPeopleCommand { get; private set; } = null!;
    public RelayCommand ShowPeoplePreviewCommand { get; private set; } = null!;
    public RelayCommand ClosePeoplePreviewCommand { get; private set; } = null!;
    public RelayCommand ConfirmPersonCommand { get; private set; } = null!;
    public RelayCommand RejectPersonCommand { get; private set; } = null!;
    public RelayCommand ResetPersonReviewCommand { get; private set; } = null!;
    public RelayCommand FocusPersonViewCommand { get; private set; } = null!;
    public bool CanFindPeople => IsImported && CanInteract && !HasEditPreview && !HasRecovery && !IsScreenSelecting &&
        _scanProject?.Objects.Any(o => o.IsVisible && o.PointCount > 0 && o.Source.Colors.Length > 0) == true;
    public bool CanDetectPeople => IsAdministrator && CanFindPeople;
    public bool CanSearchPeople => CanDetectPeople && !IsTunnelMode && HasStructureAnalysis && !StructureIsStale && StructureSurfaces.Any(s => s.Kind == StructureSurfaceKind.Floor && s.Review != StructureReviewState.Rejected);
    public bool PeopleNeedFloor => CanDetectPeople && !IsTunnelMode && !CanSearchPeople;
    public string PeopleAvailabilityHint => !IsImported ? "Импортируйте цветной скан для поиска людей." :
        !CanInteract ? "Дождитесь завершения текущей операции." : HasRecovery ? "Сначала восстановите или отклоните автосохранение." :
        HasEditPreview ? "Примените или отмените предварительную очистку." : IsScreenSelecting ? "Завершите выделение точек." :
        !CanFindPeople ? "Для поиска нужны видимые точки с исходным цветом RGB." : IsGuest ? "Нажмите «Найти людей», чтобы войти и запустить поиск." :
        IsTunnelMode ? "В тоннеле поиск проверит текущий RGB-вид." : PeopleNeedFloor ?
        "Поиск подготовит ракурсы автоматически. Если пол не найдётся, будет проверен текущий вид." :
        "Обзор помещения: восемь направлений. В длинном проходе поиск добавит ракурсы вдоль пола.";
    public bool CanReviewPerson => CanDetectPeople && SelectedPerson is not null;
    public bool HasPeopleDetection => _people is not null;
    public bool PersonBoxesVisible => HasPeopleDetection && ShowPeople;
    public bool IsPeoplePreviewOpen { get => _isPeoplePreviewOpen; private set { if (Set(ref _isPeoplePreviewOpen, value)) Raise(nameof(IsPeoplePreviewVisible)); } }
    public bool IsPeoplePreviewVisible => IsPeoplePreviewOpen && HasPeopleDetection && IsAnalysisSection && IsPeopleAnalysis && Is3DView && !IsPresentationMode;
    public string PeopleRunMode => _peopleRunMode;
    public IReadOnlyList<PersonFinding> CurrentPersonFindings => PersonFindings.Where(p => p.ViewIndex == SelectedPersonView?.Index).ToArray();
    public bool HasCurrentPersonFindings => CurrentPersonFindings.Count > 0;
    public bool PeopleViewIsEmpty => HasPeopleDetection && !HasCurrentPersonFindings;
    public bool ShowPeople { get => _showPeople; set { if (Set(ref _showPeople, value)) Raise(nameof(PersonBoxesVisible)); } }
    public double PersonThreshold { get => _personThreshold; set { if (double.IsFinite(value) && value is >= .1 and <= .9) Set(ref _personThreshold, value); } }
    public ImageSource? PersonPreview { get => _personPreview; private set => Set(ref _personPreview, value); }
    public LineGeometry3D? PersonOutline { get => _personOutline; private set => Set(ref _personOutline, value); }
    public LineGeometry3D? PeopleProposedOutline { get => _peopleProposedOutline; private set => Set(ref _peopleProposedOutline, value); }
    public LineGeometry3D? PeopleConfirmedOutline { get => _peopleConfirmedOutline; private set => Set(ref _peopleConfirmedOutline, value); }
    public LineGeometry3D? PeopleRejectedOutline { get => _peopleRejectedOutline; private set => Set(ref _peopleRejectedOutline, value); }
    public PersonViewChoice? SelectedPersonView
    {
        get => _selectedPersonView;
        set
        {
            if (value is null || !PersonViews.Contains(value) || !Set(ref _selectedPersonView, value)) return;
            if (SelectedPerson?.ViewIndex != value.Index) SelectedPerson = PersonFindings.FirstOrDefault(p => p.ViewIndex == value.Index);
            RefreshPersonPreview(); RefreshPeopleOutlines(); NotifyPeopleState();
        }
    }
    public PersonFinding? SelectedPerson
    {
        get => _selectedPerson;
        set
        {
            if (value is not null && !PersonFindings.Contains(value) || !Set(ref _selectedPerson, value)) return;
            if (value is not null) SelectedPersonView = PersonViews.First(v => v.Index == value.ViewIndex);
            PersonOutline = value is { Minimum: { } min, Maximum: { } max } ? Rendering.SceneGeometry.Lines(BoxLines(min, max)) : null;
            RefreshPersonPreview(); RefreshPeopleOutlines(); NotifyPeopleState();
        }
    }
    public string PeopleSummary => _people is null ? "Поиск ещё не запускался" :
        $"Рамок: {PersonFindings.Count} · ракурсов: {_people.Views.Length}\n{SpatialSummary}\nПодтверждено: {PersonFindings.Count(p => p.Review == StructureReviewState.Confirmed)} · ложных: {PersonFindings.Count(p => p.Review == StructureReviewState.Rejected)}" +
        (_people.RejectedChairCandidates > 0 ? $"\nПохоже на стул: исключено {_people.RejectedChairCandidates}" : "") +
        (_people.RejectedWallCandidates > 0 ? $"\nПлоское изображение на стене: исключено {_people.RejectedWallCandidates}" : "") +
        (_people.RejectedFurnitureGroups > 0 ? $"\nМебель при повторной проверке: исключено групп {_people.RejectedFurnitureGroups}" : "") +
        (_people.RejectedDecorGroups > 0 ? $"\nРастения и вазы при повторной проверке: исключено групп {_people.RejectedDecorGroups}" : "");
    public string PersonViewSummary => _people is not null && SelectedPersonView is { } selected ?
        $"Ракурс {selected.Index + 1} · кандидатов: {CurrentPersonFindings.Count} · заполнение {_people.Views[selected.Index].Coverage:P0}" : "";
    private string SpatialSummary => $"Групп в 3D: {PersonFindings.Where(p => p.SpatialGroup.HasValue && p.Review != StructureReviewState.Rejected).Select(p => p.SpatialGroup).Distinct().Count()}" +
        $" · рамок без привязки: {PersonFindings.Count(p => !p.SpatialGroup.HasValue && p.Review != StructureReviewState.Rejected)}";

    private void InitializePeopleCommands()
    {
        LoadPersonExamples();
        LoadPersonModels();
        InitializeTrainingCommands();
        DetectPeopleCommand = new(() => DetectPeopleAsync(false), () => CanDetectPeople, ex => ReportError("Не удалось проверить людей", ex));
        SearchPeopleCommand = new(() => DetectPeopleAsync(true), () => CanSearchPeople, ex => ReportError("Не удалось осмотреть комнату", ex));
        FindPeopleCommand = new(FindPeopleAsync, () => CanFindPeople, ex => ReportError("Не удалось запустить поиск людей", ex));
        ShowPeoplePreviewCommand = new(() => { OpenPeopleAnalysisCommand.Execute(null); IsPeoplePreviewOpen = true; }, () => CanInteract && HasPeopleDetection);
        ClosePeoplePreviewCommand = new(() => IsPeoplePreviewOpen = false);
        ConfirmPersonCommand = new(() => ReviewPerson(StructureReviewState.Confirmed), () => CanReviewPerson);
        RejectPersonCommand = new(() => ReviewPerson(StructureReviewState.Rejected), () => CanReviewPerson);
        ResetPersonReviewCommand = new(() => ReviewPerson(StructureReviewState.Proposed), () => CanReviewPerson);
        FocusPersonViewCommand = new(FocusPersonView, () => CanInteract && SelectedPersonView is not null);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsAnalysisSection) or nameof(IsPeopleAnalysis) or nameof(Is3DView) or nameof(IsPresentationMode)) Raise(nameof(IsPeoplePreviewVisible));
            if ((e.PropertyName == nameof(IsNavigating) && IsNavigating) || (e.PropertyName == nameof(IsPicking) && IsPicking)) IsPeoplePreviewOpen = false;
        };
    }
    public async Task FindPeopleAsync()
    {
        if (!CanFindPeople) return;
        OpenPeopleAnalysisCommand.Execute(null);
        if (IsGuest) AdministratorAccessRequested?.Invoke(this, EventArgs.Empty);
        if (!_disposed && CanDetectPeople) await RunPeopleAnalysisAsync(true, true);
    }
    public Task DetectPeopleAsync(bool survey = false) => RunPeopleAnalysisAsync(survey, false);
    private async Task RunPeopleAnalysisAsync(bool survey, bool prepareViews, bool labeling = false)
    {
        if (!(survey && !prepareViews ? CanSearchPeople : CanDetectPeople) || _scanProject is not { } project) return;
        if (IsTunnelMode) { survey = false; prepareViews = false; }
        var poses = new[] { new ScanViewPose(new((float)Camera.Position.X, (float)Camera.Position.Y, (float)Camera.Position.Z),
            new((float)Camera.LookDirection.X, (float)Camera.LookDirection.Y, (float)Camera.LookDirection.Z),
            new((float)Camera.UpDirection.X, (float)Camera.UpDirection.Y, (float)Camera.UpDirection.Z), (float)Camera.FieldOfView) };
        var surfaces = !IsTunnelMode && !StructureIsStale ? StructureSurfaces.ToArray() : [];
        var threshold = labeling ? .1f : (float)PersonThreshold;
        var clip = ActiveClipPlane;
        EndInspection(); ReturnToOverview();
        var token = BeginOperation("Поиск людей · подготовка ракурсов");
        try
        {
            if (survey && prepareViews && (!HasStructureAnalysis || StructureIsStale))
            {
                // Camera preparation is temporary: do not replace the operator's plane analysis or history.
                var planes = await _structureAnalyzer.AnalyzeAsync(project, new(DistanceTolerance: StructureTolerance), OperationProgress(token, .2), token).WaitAsync(token);
                surfaces = planes.Surfaces;
            }
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator || !ReferenceEquals(project, _scanProject)) return;
            if (survey && surfaces.Any(s => s.Kind == StructureSurfaceKind.Floor && s.Review != StructureReviewState.Rejected))
                poses = !StructureIsStale && _structureDocument is not null ? PersonScanAnalyzer.RoomViews(_structureDocument) : PersonScanAnalyzer.RoomViews(surfaces);
            var mode = poses.Length > 1 ? $"Обзор помещения · {poses.Length} ракурсов" : survey ? "Текущий вид · пол для обзора не найден" : "Текущий вид";
            Status = "Поиск людей · проверка RGB-ракурсов";
            LoadPersonExamples();
            LoadPersonModels();
            var model = !labeling && _personModelError is null ? _personModels.Active : null;
            var proposalThreshold = model is null ? threshold : PersonCandidateLearning.ProposalThreshold(threshold);
            var progress = OperationProgress(token);
            var analysis = labeling
                ? _personAnalyzer.AnalyzeForReviewAsync(project, poses, surfaces, proposalThreshold, clip, progress, token)
                : _personAnalyzer.AnalyzeAsync(project, poses, surfaces, proposalThreshold, clip, progress, token);
            var result = await analysis.WaitAsync(token);
            var sources = await Task.Run(() => StructureProvenance.CaptureInputs(project, token)
                .Where(s => s.IsVisible && s.IncludedCount > 0).Select(s => s.PointsHash).Distinct().ToArray(), token);
            if (_learningError is null || model is not null)
            {
                var proposals = result;
                result = await Task.Run(() => _personLearning.Apply(proposals, threshold, model, token), token);
            }
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator || !ReferenceEquals(project, _scanProject)) return;
            ClearPersonDetections();
            _people = result;
            _personSources = sources;
            _peopleRunMode = labeling ? mode + " · разметка от 10 %" : model is null ? mode : mode + " · обученная оценка";
            foreach (var finding in result.Findings) PersonFindings.Add(finding);
            for (var i = 0; i < result.Views.Length; i++) PersonViews.Add(new(i, $"Ракурс {i + 1} · кандидатов {result.Findings.Count(p => p.ViewIndex == i)}"));
            SelectedPersonView = PersonViews.FirstOrDefault(); SelectedPerson = PersonFindings.FirstOrDefault();
            SelectAnalysisTool("Люди"); IsPeoplePreviewOpen = true;
            Status = $"Ракурсов: {result.Views.Length} · рамок: {result.Findings.Length} · {SpatialSummary}";
            Notification = ""; AddEvent(Status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось проверить людей", ex); }
        finally { EndOperation(); NotifyPeopleState(); }
    }
    private void ReviewPerson(StructureReviewState review)
    {
        if (!CanReviewPerson || SelectedPerson is not { } before) return;
        try { _personLearning.Review(_people!.Views[before.ViewIndex], before, review, _personSources); _learningError = null; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.IO.InvalidDataException)
        { ReportError("Не удалось сохранить обучающую отметку", ex); return; }
        NotifyTrainingState();
        var index = PersonFindings.IndexOf(before); var after = before with { Review = review }; PersonFindings[index] = after;
        SelectedPerson = after; RefreshPersonPreview(); NotifyPeopleState();
    }
    private void FocusPersonView()
    {
        if (!CanInteract || _people is null || SelectedPersonView is not { } selected) return;
        IsPeoplePreviewOpen = false; SelectWorkspaceViewCommand.Execute("3D");
        ReturnToOverview(); var pose = _people.Views[selected.Index].Pose;
        MoveCamera(pose.Position, pose.Position + Vector3.Normalize(pose.Forward) * 3, "Ракурс проверки людей");
        Camera.UpDirection = new(pose.Up.X, pose.Up.Y, pose.Up.Z); Camera.FieldOfView = pose.FieldOfView;
    }
    public void SelectPersonAt(double x, double y)
    {
        if (!CanInteract || _people is null || SelectedPersonView is not { } selected || !double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1) return;
        var size = _people.Views[selected.Index].Size;
        var finding = CurrentPersonFindings.Where(p => x * size >= p.Box.Left && x * size <= p.Box.Right && y * size >= p.Box.Top && y * size <= p.Box.Bottom)
            .OrderBy(p => p.Box.Area).FirstOrDefault();
        if (finding is not null) SelectedPerson = finding;
    }
    private void RefreshPeopleOutlines()
    {
        LineGeometry3D? Outline(StructureReviewState review)
        {
            var lines = CurrentPersonFindings.Where(p => p.Review == review && p.Minimum is not null && p.Maximum is not null)
                .SelectMany(p => BoxLines(p.Minimum!.Value, p.Maximum!.Value)).ToArray();
            return lines.Length == 0 ? null : Rendering.SceneGeometry.Lines(lines);
        }
        PeopleProposedOutline = Outline(StructureReviewState.Proposed);
        PeopleConfirmedOutline = Outline(StructureReviewState.Confirmed);
        PeopleRejectedOutline = Outline(StructureReviewState.Rejected);
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
                var isSelected = finding.Id == SelectedPerson?.Id;
                context.DrawRectangle(null, new Pen(brush, isSelected ? 3 : 1.25), new Rect(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top));
                var label = finding.SpatialGroup is { } group ? $"3D №{group}" : $"Рамка {finding.Id:00}";
                var text = new FormattedText(isSelected ? $"{label} · {box.Score:P0}" : label, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), isSelected ? 12 : 10, Brushes.White, 1);
                var x = Math.Clamp(box.Left, 0, Math.Max(0, image.Size - text.Width - 6)); var y = Math.Clamp(box.Top - 18, 0, image.Size - 18);
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 15, 25, 33)), null, new Rect(x, y, text.Width + 6, 18));
                context.DrawText(text, new Point(x + 3, y));
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
        _personSources = [];
        _people = null; _selectedPerson = null; _selectedPersonView = null; _peopleRunMode = ""; IsPeoplePreviewOpen = false;
        PersonFindings.Clear(); PersonViews.Clear(); PersonPreview = null; PersonOutline = null;
        PeopleProposedOutline = null; PeopleConfirmedOutline = null; PeopleRejectedOutline = null;
        Raise(nameof(SelectedPerson)); Raise(nameof(SelectedPersonView)); NotifyPeopleState();
    }
    private void NotifyPeopleState()
    {
        foreach (var name in new[] { nameof(CanFindPeople), nameof(CanDetectPeople), nameof(CanSearchPeople), nameof(PeopleNeedFloor), nameof(PeopleAvailabilityHint), nameof(CanReviewPerson), nameof(HasPeopleDetection), nameof(PersonBoxesVisible), nameof(PeopleSummary), nameof(PersonViewSummary), nameof(PeopleRunMode), nameof(CurrentPersonFindings), nameof(HasCurrentPersonFindings), nameof(PeopleViewIsEmpty), nameof(IsPeoplePreviewVisible) }) Raise(name);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
