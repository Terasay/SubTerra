using System.IO;
using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>Measured radial profiles around an operator-supplied axis inside one passage.
/// A spatial grid bounds local queries; missing sectors remain gaps instead of fabricated walls.</summary>
public static class TunnelAnalyzer
{
    public const int MaximumSections = 2048;
    private const long MaximumPointVisits = 80_000_000;
    private sealed record Route(Vector3[] Points, double[] Distances)
    {
        public double Length => Distances[^1];
    }
    private readonly record struct Frame(Vector3 Center, Vector3 Forward, Vector3 Right, Vector3 Up);

    public static TunnelAnalysisDocument Create(InspectionPoint[] axis, TunnelAnalysisOptions? options = null)
    {
        options ??= new(); options.Validate(); ValidateAxis(axis, false);
        return new(1, axis.ToArray(), options, [], [], DateTimeOffset.UtcNow, 0, 0);
    }

    public static Task<TunnelAnalysisDocument> AnalyzeAsync(ScanProject project, InspectionPoint[] axis,
        TunnelAnalysisOptions options, IProgress<double>? progress = null, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(options);
        options.Validate(); ValidateAxis(axis, true); project.Validate(token);
        var axisSnapshot = axis.ToArray(); var route = CompileRoute(axisSnapshot); var stations = Stations(route, options);
        var visible = project.Objects.Where(o => o.IsVisible && o.PointCount > 0).ToArray();
        var total = visible.Sum(o => o.PointCount);
        if (total == 0) throw new ArgumentException("В проекте нет видимых точек для анализа тоннеля.");
        var sampleCount = Math.Min(total, options.MaximumSamples); var points = new Vector3[sampleCount];
        var ordinal = 0; var next = 0;
        foreach (var obj in visible)
        {
            var matrix = obj.Matrix;
            for (var i = 0; i < obj.Source.Points.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!obj.Includes(i)) continue;
                if (next < sampleCount && ordinal == (long)next * total / sampleCount)
                {
                    var p = Vector3.Transform(obj.Source.Points[i].Position, matrix);
                    if (!InspectionPoint.From(p).IsValid) throw new InvalidDataException("Точки анализа выходят за допустимые координаты проекта.");
                    points[next++] = p;
                }
                ordinal++;
            }
        }
        progress?.Report(10);
        var grid = new Dictionary<(int X, int Y, int Z), List<int>>();
        var cellSize = (float)options.MaximumRadius;
        (int X, int Y, int Z) Cell(Vector3 p) => ((int)MathF.Floor(p.X / cellSize), (int)MathF.Floor(p.Y / cellSize), (int)MathF.Floor(p.Z / cellSize));
        for (var i = 0; i < points.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var key = Cell(points[i]); if (!grid.TryGetValue(key, out var bucket)) grid.Add(key, bucket = []);
            bucket.Add(i);
        }
        var inputs = StructureProvenance.CaptureInputs(project, token); progress?.Report(20);
        var sections = new TunnelSection[stations.Length]; long visited = 0; var cancellationCounter = 0;
        for (var index = 0; index < stations.Length; index++)
        {
            token.ThrowIfCancellationRequested(); var frame = FrameAt(route, stations[index]);
            var bins = new List<(double X, double Y, double Radius)>?[options.AngularBins]; var count = 0;
            var bound = new Vector3((float)(options.MaximumRadius + options.SliceThickness * .5));
            var low = Cell(frame.Center - bound); var high = Cell(frame.Center + bound);
            var radiusSquared = options.MaximumRadius * options.MaximumRadius;
            for (var x = low.X; x <= high.X; x++) for (var y = low.Y; y <= high.Y; y++) for (var z = low.Z; z <= high.Z; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                if ((visited += bucket.Count) > MaximumPointVisits)
                    throw new InvalidDataException("Слишком большой объём анализа сечений. Увеличьте шаг, уменьшите радиус или анализируйте более короткий участок оси.");
                foreach (var pointIndex in bucket)
                {
                    if ((cancellationCounter++ & 1023) == 0) token.ThrowIfCancellationRequested();
                    var delta = points[pointIndex] - frame.Center;
                    if (Math.Abs(Vector3.Dot(delta, frame.Forward)) > options.SliceThickness * .5 + 1e-5) continue;
                    var px = (double)Vector3.Dot(delta, frame.Right); var py = (double)Vector3.Dot(delta, frame.Up);
                    var r2 = px * px + py * py; if (r2 > radiusSquared || r2 < .0004) continue;
                    var angle = Math.Atan2(py, px); if (angle < 0) angle += Math.Tau;
                    var bin = Math.Min(options.AngularBins - 1, (int)(angle / Math.Tau * options.AngularBins));
                    (bins[bin] ??= []).Add((px, py, r2)); count++;
                }
            }
            var contour = new List<TunnelContourPoint>();
            for (var bin = 0; bin < bins.Length; bin++)
            {
                if (bins[bin] is not { Count: > 0 } values) continue;
                token.ThrowIfCancellationRequested();
                values.Sort((a, b) => a.Radius.CompareTo(b.Radius));
                // Keep an observed outer sample: inner equipment points should not define the passage wall.
                var observed = values[(int)Math.Ceiling((values.Count - 1) * .9)];
                contour.Add(new(observed.X, observed.Y, bin, values.Count));
            }
            var width = contour.Count == 0 ? 0 : contour.Max(p => p.X) - contour.Min(p => p.X);
            var height = contour.Count == 0 ? 0 : contour.Max(p => p.Y) - contour.Min(p => p.Y);
            var section = new TunnelSection(index + 1, stations[index], InspectionPoint.From(frame.Center), InspectionPoint.From(frame.Forward),
                InspectionPoint.From(frame.Right), InspectionPoint.From(frame.Up), contour.ToArray(), count, contour.Count, options.AngularBins, false, width, height, null);
            var closed = contour.Count == options.AngularBins && count >= options.MinimumSectionPoints &&
                Enumerable.Range(0, contour.Count).All(i => section.ConnectsToNext(i, options.MaximumContourGap));
            sections[index] = section with { IsClosed = closed, AreaSquareMeters = closed ? Area(section.Contour) : null };
            progress?.Report(20 + 80d * (index + 1) / stations.Length);
        }
        token.ThrowIfCancellationRequested();
        return new TunnelAnalysisDocument(1, axisSnapshot, options, sections, inputs, DateTimeOffset.UtcNow, total, sampleCount);
    }, token);

    public static TunnelAnalysisDocument Refresh(TunnelAnalysisDocument document, ScanProject project, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var inputs = StructureProvenance.CaptureInputs(project, token);
        bool Stale(StructureInputStamp[] saved) => !saved.OrderBy(i => i.ObjectId).SequenceEqual(inputs);
        return document with
        {
            IsStale = document.HasResults && Stale(document.Inputs),
            Branches = document.Branches.Select(b => b with { Analysis = Refresh(b.Analysis, project, token) }).ToArray(),
            Reconstruction = document.Reconstruction is { } mesh ? mesh with { IsStale = Stale(mesh.Inputs) } : null,
            Recognition = document.Recognition is { } recognition ? recognition with { IsStale = Stale(recognition.Inputs) } : null
        };
    }

    /// <summary>Checks saved axes, frames, coverage and derived dimensions without fabricating observations.</summary>
    public static void Validate(TunnelAnalysisDocument document, ScanProject project, CancellationToken token = default)
    {
        static InvalidDataException Bad() => new("Некорректные данные анализа тоннеля.");
        static bool Hash(string? value) => value?.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
        if (document is null || document.Version is not (1 or 2) || document.Options is null || document.Axis is null ||
            document.Sections is null || document.Inputs is null || document.CreatedAtUtc < DateTimeOffset.UnixEpoch) throw Bad();
        try { document.Options.Validate(); ValidateAxis(document.Axis, document.HasResults); }
        catch (ArgumentException ex) { throw new InvalidDataException("Некорректная ось или параметры тоннеля.", ex); }
        TunnelWorkspace.ValidateExtensions(document, project, token);
        if (!document.HasResults)
        {
            if (document.Inputs.Length != 0 || document.SourcePointCount != 0 || document.SampleCount != 0) throw Bad();
            return;
        }
        if (document.Inputs.Length is < 1 or > ScanProject.MaximumObjects || document.Inputs.Any(i => i is null) ||
            document.Inputs.Select(i => i.ObjectId).Distinct().Count() != document.Inputs.Length ||
            document.Inputs.Any(i => i.ObjectId == Guid.Empty || i.PointCount is < 1 or > ImportedScan.MaximumPoints ||
                i.IncludedCount < 0 || i.IncludedCount > i.PointCount || !Hash(i.PointsHash) ||
                (i.MaskHash != "all" && !Hash(i.MaskHash)) || (i.MaskHash == "all" && i.IncludedCount != i.PointCount) || i.Transform is null) ||
            document.Inputs.Sum(i => (long)i.PointCount) > ImportedScan.MaximumPoints ||
            document.SourcePointCount <= 0 || document.SourcePointCount != document.Inputs.Where(i => i.IsVisible).Sum(i => i.IncludedCount) ||
            document.SampleCount != Math.Min(document.SourcePointCount, document.Options.MaximumSamples)) throw Bad();
        foreach (var input in document.Inputs) input.Transform.Validate();
        var route = CompileRoute(document.Axis); double[] stations;
        try { stations = Stations(route, document.Options); }
        catch (ArgumentException ex) { throw new InvalidDataException("Слишком много сечений тоннеля.", ex); }
        if (document.Sections.Length != stations.Length) throw Bad();
        for (var sectionIndex = 0; sectionIndex < document.Sections.Length; sectionIndex++)
        {
            token.ThrowIfCancellationRequested(); var section = document.Sections[sectionIndex];
            if (section is null || section.Id != sectionIndex + 1 || !double.IsFinite(section.Chainage) ||
                Math.Abs(section.Chainage - stations[sectionIndex]) > 1e-6 || !section.Center.IsValid ||
                !section.Forward.IsValid || !section.Right.IsValid || !section.Up.IsValid ||
                section.Contour is null || section.AngularBins != document.Options.AngularBins ||
                section.CoveredBins != section.Contour.Length || section.Contour.Length > section.AngularBins ||
                section.PointCount < 0 || section.PointCount > document.SampleCount ||
                section.Contour.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
                    p.X * p.X + p.Y * p.Y > document.Options.MaximumRadius * document.Options.MaximumRadius + 1e-5 ||
                    p.X * p.X + p.Y * p.Y < .000399 || p.BinIndex < 0 || p.BinIndex >= section.AngularBins ||
                    p.SupportCount < 1 || p.SupportCount > document.SampleCount) ||
                section.Contour.Sum(p => (long)p.SupportCount) != section.PointCount ||
                !section.Contour.Select(p => p.BinIndex).SequenceEqual(section.Contour.Select(p => p.BinIndex).Distinct().Order())) throw Bad();
            var frame = FrameAt(route, section.Chainage);
            if (Vector3.DistanceSquared(section.Center.Position, frame.Center) > 1e-6 ||
                Vector3.DistanceSquared(section.Forward.Position, frame.Forward) > 1e-8 ||
                Vector3.DistanceSquared(section.Right.Position, frame.Right) > 1e-8 ||
                Vector3.DistanceSquared(section.Up.Position, frame.Up) > 1e-8) throw Bad();
            foreach (var p in section.Contour)
            {
                var angle = Math.Atan2(p.Y, p.X); if (angle < 0) angle += Math.Tau;
                if (Math.Min(section.AngularBins - 1, (int)(angle / Math.Tau * section.AngularBins)) != p.BinIndex) throw Bad();
            }
            var width = section.Contour.Length == 0 ? 0 : section.Contour.Max(p => p.X) - section.Contour.Min(p => p.X);
            var height = section.Contour.Length == 0 ? 0 : section.Contour.Max(p => p.Y) - section.Contour.Min(p => p.Y);
            var closed = section.Contour.Length == section.AngularBins && section.PointCount >= document.Options.MinimumSectionPoints &&
                Enumerable.Range(0, section.Contour.Length).All(i => section.ConnectsToNext(i, document.Options.MaximumContourGap));
            if (!double.IsFinite(section.Width) || !double.IsFinite(section.Height) || Math.Abs(section.Width - width) > 1e-6 ||
                Math.Abs(section.Height - height) > 1e-6 || section.IsClosed != closed ||
                (section.AreaSquareMeters is not null) != closed || section.AreaSquareMeters is { } area &&
                    (!double.IsFinite(area) || area <= 0 || Math.Abs(area - Area(section.Contour)) > 1e-6)) throw Bad();
        }
        if (!document.IsStale && !document.Inputs.OrderBy(i => i.ObjectId).SequenceEqual(StructureProvenance.CaptureInputs(project, token))) throw Bad();
    }

    private static double Area(TunnelContourPoint[] contour)
    {
        double area = 0;
        for (var i = 0; i < contour.Length; i++)
        {
            var a = contour[i]; var b = contour[(i + 1) % contour.Length]; area += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(area) * .5;
    }

    private static void ValidateAxis(InspectionPoint[] axis, bool requiresLine)
    {
        ArgumentNullException.ThrowIfNull(axis);
        if (axis.Length > 128 || requiresLine && axis.Length < 2 || axis.Any(p => !p.IsValid))
            throw new ArgumentException("Ось тоннеля: 2–128 точек XYZ в метрах (для черновика допустимо меньше двух).");
        for (var i = 1; i < axis.Length; i++)
        {
            var delta = axis[i].Position - axis[i - 1].Position;
            if (delta.Length() < .02f || Math.Abs(Vector3.Normalize(delta).Z) > .985)
                throw new ArgumentException("Узлы оси должны отличаться минимум на 2 см. Вертикальные стволы пока не поддерживаются.");
        }
    }
    private static Route CompileRoute(InspectionPoint[] axis)
    {
        var points = axis.Select(p => p.Position).ToArray(); var distances = new double[axis.Length];
        for (var i = 1; i < points.Length; i++) distances[i] = distances[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        return new(points, distances);
    }
    private static double[] Stations(Route route, TunnelAnalysisOptions options)
    {
        var count = (int)Math.Ceiling(route.Length / options.SectionSpacing) + 1;
        if (count > MaximumSections) throw new ArgumentException($"Слишком много сечений: максимум {MaximumSections}. Увеличьте шаг или сократите ось.");
        return Enumerable.Range(0, count).Select(i => Math.Min(i * options.SectionSpacing, route.Length)).ToArray();
    }
    private static Frame FrameAt(Route route, double chainage)
    {
        var segment = 1;
        while (segment < route.Points.Length - 1 && route.Distances[segment] < chainage - 1e-7) segment++;
        var t = (float)((chainage - route.Distances[segment - 1]) / (route.Distances[segment] - route.Distances[segment - 1]));
        var center = Vector3.Lerp(route.Points[segment - 1], route.Points[segment], t);
        var forward = Vector3.Normalize(route.Points[segment] - route.Points[segment - 1]);
        // At a bend use the bisector; near a reversal retain the preceding leg rather than normalize zero.
        if (Math.Abs(chainage - route.Distances[segment]) < 1e-7 && segment < route.Points.Length - 1)
        {
            var next = Vector3.Normalize(route.Points[segment + 1] - route.Points[segment]); var sum = forward + next;
            if (sum.LengthSquared() > .01f && Math.Abs(Vector3.Normalize(sum).Z) <= .985) forward = Vector3.Normalize(sum);
        }
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ)); var up = Vector3.Normalize(Vector3.Cross(right, forward));
        return new(center, forward, right, up);
    }
}
