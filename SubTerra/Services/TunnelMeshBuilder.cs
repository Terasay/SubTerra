using System.IO;
using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>Locally curved radial strips. No end caps, planar flattening or unbounded hole filling.</summary>
public static class TunnelMeshBuilder
{
    public static async Task<TunnelAnalysisDocument> BuildAsync(ScanProject project, TunnelAnalysisDocument root,
        TunnelMeshOptions options, CancellationToken token = default)
    {
        options.Validate(); token.ThrowIfCancellationRequested();
        var originals = TunnelWorkspace.Passages(root).ToArray(); var dense = new List<TunnelAnalysisDocument>();
        var analyzed = new List<TunnelAnalysisDocument>();
        foreach (var passage in originals)
        {
            if (passage.Axis.Length < 2) throw new InvalidDataException("Сначала задайте ось каждой ветви или удалите пустую ветвь.");
            analyzed.Add(await TunnelAnalyzer.AnalyzeAsync(project, passage.Axis, passage.Options, token: token));
            dense.Add(await TunnelAnalyzer.AnalyzeAsync(project, passage.Axis, passage.Options with
            {
                SectionSpacing = options.Spacing, SliceThickness = Math.Min(options.Spacing, passage.Options.SliceThickness)
            }, token: token));
        }
        var result = analyzed[0] with { Version = 2, Branches = root.Branches.Select((b, i) => b with { Analysis = analyzed[i + 1] }).ToArray() };
        var mesh = await Task.Run(() => Build(dense, options, result, token), token);
        return result with { Reconstruction = mesh, Recognition = root.Recognition is { IsStale: false } r &&
            r.AxisHash == mesh.AxisHash && r.Inputs.SequenceEqual(mesh.Inputs) ? r : null };
    }

    private static TunnelReconstruction Build(IReadOnlyList<TunnelAnalysisDocument> passages, TunnelMeshOptions options,
        TunnelAnalysisDocument root, CancellationToken token)
    {
        var vertices = new List<InspectionPoint>(); var observed = new List<int>(); var completed = new List<int>();
        var synthetic = new List<bool>();
        // A spatial index restricts junction checks to nearby cross-sections of other branches.
        var radius = (float)passages.Max(p => p.Options.MaximumRadius);
        (int X, int Y, int Z) Key(Vector3 p) => ((int)MathF.Floor(p.X / radius), (int)MathF.Floor(p.Y / radius), (int)MathF.Floor(p.Z / radius));
        var index = new Dictionary<(int, int, int), List<(int Branch, TunnelSection Section)>>();
        for (var branch = 0; branch < passages.Count; branch++) foreach (var section in passages[branch].Sections)
        {
            var key = Key(section.Center.Position); if (!index.TryGetValue(key, out var list)) index[key] = list = [];
            list.Add((branch, section));
        }
        long visits = 0;
        bool InsideAnother(Vector3 point, int own)
        {
            if (passages.Count == 1) return false;
            var key = Key(point);
            for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++) for (var z = -1; z <= 1; z++)
            {
                if (!index.TryGetValue((key.X + x, key.Y + y, key.Z + z), out var sections)) continue;
                foreach (var (branch, section) in sections)
                {
                    if ((++visits & 4095) == 0) token.ThrowIfCancellationRequested();
                    if (visits > 100_000_000) throw new InvalidDataException("Слишком сложное пересечение ветвей. Увеличьте шаг сетки или сократите оси.");
                    // An open profile does not bound an interior. At a junction its floor/roof
                    // returns may span the other passage; treating that as a volume deletes real ground.
                    if (branch == own || !section.IsClosed) continue;
                    var delta = point - section.Center.Position;
                    if (Math.Abs(Vector3.Dot(delta, section.Forward.Position)) > options.Spacing * .55) continue;
                    var px = Vector3.Dot(delta, section.Right.Position); var py = Vector3.Dot(delta, section.Up.Position);
                    var angle = Math.Atan2(py, px); if (angle < 0) angle += Math.Tau;
                    var bin = (int)(angle / Math.Tau * section.AngularBins);
                    var hit = section.Contour.FirstOrDefault(p => p.BinIndex == bin);
                    if (hit is not null && Math.Sqrt(px * px + py * py) < Math.Sqrt(hit.X * hit.X + hit.Y * hit.Y) - .15) return true;
                }
            }
            return false;
        }
        for (var branch = 0; branch < passages.Count; branch++)
        {
            var d = passages[branch]; var rows = d.Sections.Length; var bins = d.Options.AngularBins;
            var grid = new int[rows, bins];
            for (var row = 0; row < rows; row++) for (var bin = 0; bin < bins; bin++) grid[row, bin] = -1;
            int Add(Vector3 position, bool isSynthetic)
            {
                if (vertices.Count >= 500_000) throw new InvalidDataException("Сетка превышает 500000 вершин. Увеличьте шаг.");
                vertices.Add(InspectionPoint.From(position)); synthetic.Add(isSynthetic); return vertices.Count - 1;
            }
            for (var row = 0; row < rows; row++)
            {
                token.ThrowIfCancellationRequested(); var section = d.Sections[row];
                if (section.PointCount < d.Options.MinimumSectionPoints) continue;
                foreach (var point in section.Contour) grid[row, point.BinIndex] = Add(section.WorldPoint(point), false);
                if (options.CompletionGap <= 0) continue;
                // Only bounded angular gaps with observations on both sides; large portals stay open.
                for (var j = 0; j < section.Contour.Length; j++)
                {
                    var a = section.Contour[j]; var b = section.Contour[(j + 1) % section.Contour.Length];
                    var steps = (b.BinIndex - a.BinIndex + bins) % bins;
                    if (steps < 2 || steps > bins / 12 || Vector3.Distance(section.WorldPoint(a), section.WorldPoint(b)) > options.CompletionGap) continue;
                    for (var n = 1; n < steps; n++) grid[row, (a.BinIndex + n) % bins] =
                        Add(Vector3.Lerp(section.WorldPoint(a), section.WorldPoint(b), (float)n / steps), true);
                }
            }
            // Axial completion never extrapolates beyond observations, nor cascades through invented rows.
            if (options.CompletionGap > 0)
                for (var bin = 0; bin < bins; bin++)
                    for (var row = 1; row < rows - 1; row++)
                    {
                        if (grid[row, bin] >= 0 || grid[row - 1, bin] < 0 || synthetic[grid[row - 1, bin]]) continue;
                        var end = row + 1;
                        while (end < rows && grid[end, bin] < 0 && d.Sections[end].Chainage - d.Sections[row - 1].Chainage <= options.CompletionGap) end++;
                        if (end >= rows || grid[end, bin] < 0 || synthetic[grid[end, bin]]) continue;
                        var a = vertices[grid[row - 1, bin]].Position; var b = vertices[grid[end, bin]].Position;
                        if (Vector3.Distance(a, b) > options.CompletionGap) continue;
                        for (var j = row; j < end; j++) grid[j, bin] = Add(Vector3.Lerp(a, b, (float)(d.Sections[j].Chainage - d.Sections[row - 1].Chainage) /
                            (float)(d.Sections[end].Chainage - d.Sections[row - 1].Chainage)), true);
                        row = end;
                    }
            void Triangle(int ia, int ib, int ic)
            {
                var a = vertices[ia].Position; var b = vertices[ib].Position; var c = vertices[ic].Position;
                var limit = options.MaximumEdge * options.MaximumEdge * 2;
                if (Vector3.DistanceSquared(a, b) > limit || Vector3.DistanceSquared(a, c) > limit || Vector3.DistanceSquared(b, c) > limit ||
                    Vector3.Cross(b - a, c - a).LengthSquared() < 1e-12 || InsideAnother((a + b + c) / 3, branch)) return;
                var destination = synthetic[ia] || synthetic[ib] || synthetic[ic] ? completed : observed;
                destination.AddRange([ia, ib, ic]);
                if (observed.Count + completed.Count > 3_000_000) throw new InvalidDataException("Сетка превышает миллион треугольников. Увеличьте шаг.");
            }
            for (var row = 0; row < rows - 1; row++)
            {
                token.ThrowIfCancellationRequested();
                for (var bin = 0; bin < bins; bin++)
                {
                    var next = (bin + 1) % bins; var a = grid[row, bin]; var b = grid[row, next]; var c = grid[row + 1, bin]; var e = grid[row + 1, next];
                    if (a < 0 || b < 0 || c < 0 || e < 0) continue;
                    var edge = options.MaximumEdge * options.MaximumEdge;
                    if (Vector3.DistanceSquared(vertices[a].Position, vertices[b].Position) > edge || Vector3.DistanceSquared(vertices[c].Position, vertices[e].Position) > edge ||
                        Vector3.DistanceSquared(vertices[a].Position, vertices[c].Position) > edge || Vector3.DistanceSquared(vertices[b].Position, vertices[e].Position) > edge) continue;
                    Triangle(a, b, c); Triangle(b, e, c);
                }
            }
        }
        if (observed.Count == 0) throw new InvalidDataException("Недостаточно соседних наблюдений для сетки. Уточните ось, шаг или радиус поиска.");
        return new(options, vertices.ToArray(), observed.ToArray(), completed.ToArray(), root.Inputs, TunnelWorkspace.AxisHash(root), HasInteriorWinding: true);
    }

    public static ImportedScan Export(TunnelReconstruction mesh, bool completionOnly = false)
    {
        if (mesh.IsStale) throw new InvalidDataException("Сетка устарела; повторите построение.");
        var indices = completionOnly ? mesh.CompletedTriangles : mesh.ObservedTriangles.Concat(mesh.CompletedTriangles).ToArray();
        if (indices.Length == 0) throw new InvalidDataException("Нет треугольников для экспорта.");
        // Separate measured and completed vertices so the orange provenance colour cannot bleed onto observations.
        var points = new List<ScanPoint>(); var colors = new List<uint>(); var triangles = new List<int>();
        var remap = new Dictionary<(int, bool), int>();
        for (var i = 0; i < indices.Length; i++)
        {
            var inferred = completionOnly || i >= mesh.ObservedTriangles.Length; var key = (indices[i], inferred);
            if (!remap.TryGetValue(key, out var mapped))
            {
                mapped = points.Count; remap[key] = mapped; var p = mesh.Vertices[key.Item1];
                points.Add(new(p.X, p.Y, p.Z, 1)); colors.Add(inferred ? 0xF5A742u : 0xA8BAC6u);
            }
            triangles.Add(mapped);
        }
        if (!mesh.HasInteriorWinding)
            for (var i = 0; i < triangles.Count; i += 3) (triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);
        return new() { Metadata = new("tunnel-reconstruction.ply", ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0) { IsSynthetic = true },
            Points = points.ToArray(), Colors = colors.ToArray(), Triangles = triangles.ToArray() };
    }
}
