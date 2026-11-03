using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>RGB-free geometric proposals. Object classes remain hypotheses until reviewed.</summary>
public static class TunnelRecognitionService
{
    public static async Task<TunnelAnalysisDocument> AnalyzeAsync(ScanProject project, TunnelAnalysisDocument root, CancellationToken token = default)
    {
        var documents = new List<TunnelAnalysisDocument>();
        foreach (var passage in TunnelWorkspace.Passages(root))
            documents.Add(await TunnelAnalyzer.AnalyzeAsync(project, passage.Axis, passage.Options with
                { SectionSpacing = .3, SliceThickness = Math.Min(.3, passage.Options.SliceThickness) }, token: token));
        // Root input stamps are also used to validate all derived data on save/load.
        var main = root.HasResults && !root.IsStale ? root : await TunnelAnalyzer.AnalyzeAsync(project, root.Axis, root.Options, token: token);
        main = main with { Version = 2, Branches = root.Branches, Reconstruction = root.Reconstruction };
        var recognition = await Task.Run(() => Analyze(project, documents, main, token), token);
        if (root.Recognition is { IsStale: false } previous && previous.AxisHash == recognition.AxisHash && previous.Inputs.SequenceEqual(recognition.Inputs))
        {
            var reviewed = previous.Objects.Where(o => o.ReviewedClass is not null).ToArray();
            var keys = reviewed.SelectMany(o => o.Points).Select(p => (p.ObjectId, p.PointIndex)).ToHashSet();
            var proposals = recognition.Objects.Select(o => o with { Points = o.Points.Where(p => !keys.Contains((p.ObjectId, p.PointIndex))).ToArray() })
                .Where(o => o.Points.Length > 0).Take(128 - reviewed.Length);
            recognition = recognition with { Surfaces = recognition.Surfaces.Where(p => !keys.Contains((p.ObjectId, p.PointIndex))).ToArray(), Objects = [.. reviewed, .. proposals] };
        }
        return main with { Recognition = recognition };
    }

    private static TunnelRecognition Analyze(ScanProject project, List<TunnelAnalysisDocument> documents, TunnelAnalysisDocument root, CancellationToken token)
    {
        var samples = TunnelWorkspace.SamplePoints(project, 200_000, token); var surfaces = new List<TunnelPointLabel>();
        var remaining = new List<TunnelWorkspace.Sample>();
        var cellSize = (float)documents.Max(d => d.Options.MaximumRadius);
        (int X, int Y, int Z) Key(Vector3 p, float cell) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        var grid = new Dictionary<(int, int, int), List<(TunnelSection Section, TunnelContourPoint?[] Bins)>>();
        foreach (var document in documents) foreach (var section in document.Sections)
        {
            var key = Key(section.Center.Position, cellSize); if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            var bins = new TunnelContourPoint?[section.AngularBins]; foreach (var p in section.Contour) bins[p.BinIndex] = p;
            list.Add((section, bins));
        }
        long visits = 0;
        foreach (var sample in samples)
        {
            token.ThrowIfCancellationRequested(); var key = Key(sample.Position, cellSize);
            var best = double.MaxValue; var label = TunnelPointClass.Unknown; var inside = false;
            for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
            {
                if (!grid.TryGetValue((key.X + x, key.Y + y, key.Z + z), out var sections)) continue;
                foreach (var (s, bins) in sections)
                {
                    if ((++visits & 4095) == 0) token.ThrowIfCancellationRequested();
                    if (visits > 100_000_000) throw new InvalidDataException("Слишком большой объём распознавания. Ограничьте видимые сканы или длину осей.");
                    var delta = sample.Position - s.Center.Position; var axial = Math.Abs(Vector3.Dot(delta, s.Forward.Position));
                    if (axial > .22) continue;
                    var px = Vector3.Dot(delta, s.Right.Position); var py = Vector3.Dot(delta, s.Up.Position);
                    var radial = Math.Sqrt(px * px + py * py); var angle = Math.Atan2(py, px); if (angle < 0) angle += Math.Tau;
                    var bin = Math.Min(bins.Length - 1, (int)(angle / Math.Tau * bins.Length));
                    if (bins[bin] is not { } boundary) continue;
                    var radius = Math.Sqrt(boundary.X * boundary.X + boundary.Y * boundary.Y); var error = Math.Abs(radius - radial);
                    inside |= radial < radius - .22;
                    if (error > .22 || error + axial >= best) continue;
                    best = error + axial;
                    label = py < -Math.Abs(px) ? TunnelPointClass.LowerSurface : py > Math.Abs(px) ? TunnelPointClass.Roof : TunnelPointClass.Side;
                }
            }
            if (label != TunnelPointClass.Unknown) surfaces.Add(new(sample.ObjectId, sample.Index, label));
            else if (inside) remaining.Add(sample);
        }
        var voxels = new Dictionary<(int X, int Y, int Z), List<TunnelWorkspace.Sample>>();
        foreach (var sample in remaining)
        {
            var key = Key(sample.Position, .3f); if (!voxels.TryGetValue(key, out var list)) voxels[key] = list = [];
            list.Add(sample);
        }
        var seen = new HashSet<(int, int, int)>(); var objects = new List<TunnelObjectCandidate>();
        foreach (var key in voxels.Keys)
        {
            if (!seen.Add(key)) continue;
            var queue = new Queue<(int X, int Y, int Z)>(); queue.Enqueue(key); var group = new List<TunnelWorkspace.Sample>();
            while (queue.TryDequeue(out var current))
            {
                token.ThrowIfCancellationRequested(); group.AddRange(voxels[current]);
                for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
                {
                    var next = (current.X + x, current.Y + y, current.Z + z);
                    if (voxels.ContainsKey(next) && seen.Add(next)) queue.Enqueue(next);
                }
            }
            if (group.Count < 12 || objects.Count >= 128) continue;
            var min = group.Select(p => p.Position).Aggregate(Vector3.Min); var max = group.Select(p => p.Position).Aggregate(Vector3.Max);
            var extent = max - min;
            var nearby = documents.SelectMany(d => d.Sections).MinBy(s => Vector3.DistanceSquared(s.Center.Position, (min + max) / 2))!;
            var axialValues = group.Select(p => Vector3.Dot(p.Position, nearby.Forward.Position)).ToArray();
            var radialValues = group.Select(p => Vector3.Dot(p.Position, nearby.Right.Position)).ToArray();
            var thinAcrossAxis = axialValues.Max() - axialValues.Min() < .6;
            var wrapsPassage = radialValues.Max() - radialValues.Min() > 1.2 && extent.Z > 1;
            var suggestion = thinAcrossAxis && wrapsPassage ? TunnelPointClass.Support :
                extent.X > .25 && extent.Y > .25 && extent.Z > .25 ? TunnelPointClass.Equipment : TunnelPointClass.Unknown;
            objects.Add(new(Guid.NewGuid(), $"Группа {objects.Count + 1}", InspectionPoint.From(min), InspectionPoint.From(max),
                group.Select(p => new TunnelPointLabel(p.ObjectId, p.Index, TunnelPointClass.Unknown)).ToArray(), suggestion));
        }
        return new(surfaces.ToArray(), objects.ToArray(), root.Inputs, TunnelWorkspace.AxisHash(root), samples.Length);
    }

    public static TunnelRecognition AddBox(ScanProject project, TunnelRecognition recognition, ScanBox box, CancellationToken token = default)
    {
        box.Validate(); if (recognition.IsStale) throw new InvalidDataException("Повторите 3D-анализ перед разметкой.");
        if (recognition.Objects.Length >= 128) throw new InvalidDataException("Максимум 128 групп. Удалите или объедините лишние группы.");
        var points = TunnelWorkspace.SamplePoints(project, 200_000, token).Where(p => box.Contains(p.Position))
            .Select(p => new TunnelPointLabel(p.ObjectId, p.Index, TunnelPointClass.Unknown)).ToArray();
        if (points.Length == 0) throw new InvalidDataException("В указанной области нет точек выборки.");
        var keys = points.Select(p => (p.ObjectId, p.PointIndex)).ToHashSet();
        var kept = recognition.Objects.Select(o => o with { Points = o.Points.Where(p => !keys.Contains((p.ObjectId, p.PointIndex))).ToArray() })
            .Where(o => o.Points.Length > 0).ToArray();
        return recognition with
        {
            Surfaces = recognition.Surfaces.Where(p => !keys.Contains((p.ObjectId, p.PointIndex))).ToArray(),
            Objects = [.. kept, new(Guid.NewGuid(), "Область оператора", InspectionPoint.From(box.Minimum), InspectionPoint.From(box.Maximum), points, TunnelPointClass.Unknown)]
        };
    }

    /// <summary>Only reviewed classes become training labels. Automatic proposals remain separate.</summary>
    public static async Task ExportDatasetAsync(string path, ScanProject project, TunnelRecognition recognition, CancellationToken token = default)
    {
        if (recognition.IsStale || !recognition.Inputs.SequenceEqual(StructureProvenance.CaptureInputs(project, token)))
            throw new InvalidDataException("Разметка устарела; экспорт для обучения заблокирован.");
        var labels = recognition.Surfaces.ToDictionary(p => (p.ObjectId, p.PointIndex), p => (Label: TunnelPointClass.Unknown, Suggestion: p.Class, Instance: Guid.Empty, Reviewed: false));
        foreach (var obj in recognition.Objects) foreach (var p in obj.Points)
            labels[(p.ObjectId, p.PointIndex)] = (obj.ReviewedClass ?? TunnelPointClass.Unknown, obj.SuggestedClass, obj.Id, obj.ReviewedClass is not null);
        var samples = TunnelWorkspace.SamplePoints(project, 200_000, token);
        var metadata = JsonSerializer.Serialize(new
        {
            schema = "subterra-xyz-labels-v1", coordinateUnit = "m", upAxis = "Z", split = "unassigned",
            method = TunnelRecognition.Method, axisHash = recognition.AxisHash, inputs = recognition.Inputs,
            sourceObjects = project.Objects.Select(o => new { o.Id, o.Name, o.Source.Metadata.IsSynthetic }),
            classes = Enum.GetValues<TunnelPointClass>().Select(c => new { id = (int)c, name = c.ToString() }),
            note = "Only label_source=operator is reviewed ground truth. Split by independent scans/sites, never by points. No RGB or people model is included."
        });
        var fullPath = Path.GetFullPath(path); var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
            {
                await writer.WriteLineAsync("# " + metadata);
                await writer.WriteLineAsync("object_id,point_index,x,y,z,label,suggested_label,instance_id,label_source");
                foreach (var sample in samples)
                {
                    token.ThrowIfCancellationRequested(); var label = labels.GetValueOrDefault((sample.ObjectId, sample.Index)); var p = sample.Position;
                    await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{sample.ObjectId},{sample.Index},{p.X:G9},{p.Y:G9},{p.Z:G9},{(int)label.Label},{(int)label.Suggestion},{label.Instance},{(label.Reviewed ? "operator" : "unreviewed")}"));
                }
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, fullPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
