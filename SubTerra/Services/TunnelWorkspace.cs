using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>Shared world-coordinate samples and bounded, versioned tunnel derivatives.</summary>
public static class TunnelWorkspace
{
    public const int MaximumBranches = 16;
    public readonly record struct Sample(Guid ObjectId, int Index, Vector3 Position);
    public static IEnumerable<TunnelAnalysisDocument> Passages(TunnelAnalysisDocument d) =>
        new[] { d }.Concat(d.Branches.Select(b => b.Analysis));

    public static string AxisHash(TunnelAnalysisDocument d) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(Passages(d).Select(p => new { p.Axis, p.Options }).ToArray())));

    public static Sample[] SamplePoints(ScanProject project, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); project.Validate(token);
        var objects = project.Objects.Where(o => o.IsVisible && o.PointCount > 0).ToArray();
        var total = objects.Sum(o => o.PointCount); var count = Math.Min(total, limit);
        var result = new Sample[count]; var next = 0; var ordinal = 0;
        foreach (var obj in objects)
        {
            var matrix = obj.Matrix;
            for (var i = 0; i < obj.Source.Points.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (!obj.Includes(i)) continue;
                if (next < count && ordinal == (long)next * total / count)
                {
                    var position = Vector3.Transform(obj.Source.Points[i].Position, matrix);
                    if (!InspectionPoint.From(position).IsValid) throw new InvalidDataException("Точки выходят за допустимые координаты.");
                    result[next++] = new(obj.Id, i, position);
                }
                ordinal++;
            }
        }
        if (count == 0) throw new InvalidDataException("Нет видимых точек для анализа.");
        return result;
    }

    public static TunnelAnalysisDocument ReplacePassage(TunnelAnalysisDocument root, Guid id, TunnelAnalysisDocument value)
    {
        var previous = id == Guid.Empty ? root : root.Branches.First(b => b.Id == id).Analysis;
        var unchanged = previous.Axis.SequenceEqual(value.Axis) && previous.Options == value.Options && previous.Inputs.SequenceEqual(value.Inputs);
        return (id == Guid.Empty ? value with { Branches = root.Branches } : root with
        {
            Branches = root.Branches.Select(b => b.Id == id ? b with { Analysis = value } : b).ToArray()
        }) with { Version = 2, Reconstruction = unchanged ? root.Reconstruction : null, Recognition = unchanged ? root.Recognition : null };
    }

    internal static void ValidateExtensions(TunnelAnalysisDocument d, ScanProject project, CancellationToken token)
    {
        static InvalidDataException Bad() => new("Повреждены ветви, сетка или 3D-разметка тоннеля.");
        if (d.Branches is null || d.Branches.Length > MaximumBranches - 1 ||
            d.Branches.Any(b => b is null || b.Id == Guid.Empty || string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 120 ||
                b.Analysis is null || b.Analysis.Branches is null || b.Analysis.Branches.Length != 0 ||
                b.Analysis.Reconstruction is not null || b.Analysis.Recognition is not null) ||
            d.Branches.Select(b => b.Id).Distinct().Count() != d.Branches.Length ||
            d.Version == 1 && (d.Branches.Length != 0 || d.Reconstruction is not null || d.Recognition is not null)) throw Bad();
        foreach (var branch in d.Branches) TunnelAnalyzer.Validate(branch.Analysis with { IsStale = true }, project, token);
        void Provenance(StructureInputStamp[] inputs, string hash)
        {
            if (hash != AxisHash(d) || inputs is null || inputs.Length is < 1 or > ScanProject.MaximumObjects || inputs.Any(i => i is null) ||
                !inputs.SequenceEqual(d.Inputs)) throw Bad();
        }
        if (d.Reconstruction is { } m)
        {
            Provenance(m.Inputs, m.AxisHash);
            if (m.Options is null) throw Bad();
            try { m.Options.Validate(); } catch (ArgumentException ex) { throw new InvalidDataException("Некорректные параметры сетки.", ex); }
            if (m.Vertices is null || m.Vertices.Length is < 1 or > 500_000 || m.ObservedTriangles is null || m.CompletedTriangles is null ||
                m.ObservedTriangles.Length % 3 != 0 || m.CompletedTriangles.Length % 3 != 0 ||
                (long)m.ObservedTriangles.Length + m.CompletedTriangles.Length > 3_000_000 ||
                m.Vertices.Any(p => !p.IsValid) || m.Options.CompletionGap == 0 && m.CompletedTriangles.Length > 0) throw Bad();
            foreach (var indices in new[] { m.ObservedTriangles, m.CompletedTriangles })
                for (var i = 0; i < indices.Length; i += 3)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    if ((uint)indices[i] >= m.Vertices.Length || (uint)indices[i + 1] >= m.Vertices.Length || (uint)indices[i + 2] >= m.Vertices.Length) throw Bad();
                    var a = m.Vertices[indices[i]].Position; var b = m.Vertices[indices[i + 1]].Position; var c = m.Vertices[indices[i + 2]].Position;
                    var max = m.Options.MaximumEdge * m.Options.MaximumEdge * 2 + 1e-5;
                    if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-12 ||
                        Vector3.DistanceSquared(a, b) > max || Vector3.DistanceSquared(a, c) > max || Vector3.DistanceSquared(b, c) > max) throw Bad();
                }
        }
        if (d.Recognition is { } r)
        {
            Provenance(r.Inputs, r.AxisHash);
            if (r.Surfaces is null || r.Objects is null || r.SampleCount is < 1 or > 200_000 || r.Objects.Length > 128 ||
                r.Objects.Any(o => o is null || o.Id == Guid.Empty || o.Points is null || o.Points.Length == 0 ||
                    string.IsNullOrWhiteSpace(o.Name) || o.Name.Length > 120 || !o.Minimum.IsValid || !o.Maximum.IsValid ||
                    o.Minimum.X > o.Maximum.X || o.Minimum.Y > o.Maximum.Y || o.Minimum.Z > o.Maximum.Z ||
                    !Enum.IsDefined(o.SuggestedClass) || o.SuggestedClass == TunnelPointClass.Person ||
                    o.ReviewedClass is { } c && !Enum.IsDefined(c)) || r.Objects.Select(o => o.Id).Distinct().Count() != r.Objects.Length) throw Bad();
            var labels = r.Surfaces.Concat(r.Objects.SelectMany(o => o.Points)).ToArray();
            if (labels.Length > r.SampleCount || labels.Any(p => p is null || !Enum.IsDefined(p.Class)) ||
                labels.Select(p => (p.ObjectId, p.PointIndex)).Distinct().Count() != labels.Length ||
                r.Surfaces.Any(p => p.Class is not (TunnelPointClass.LowerSurface or TunnelPointClass.Side or TunnelPointClass.Roof))) throw Bad();
            foreach (var p in labels)
            {
                var stamp = r.Inputs.FirstOrDefault(i => i.ObjectId == p.ObjectId);
                if (stamp is null || p.PointIndex < 0 || p.PointIndex >= stamp.PointCount) throw Bad();
            }
        }
    }
}
