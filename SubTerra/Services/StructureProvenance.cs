using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>Sources and masks are immutable snapshots throughout the project/history pipeline.
/// Cache their hashes by identity so checking provenance never walks millions of points on the UI thread.</summary>
public static class StructureProvenance
{
    private sealed record Digest(string Value);
    private static readonly ConditionalWeakTable<ImportedScan, Digest> PointHashes = new();
    private static readonly ConditionalWeakTable<ScanPointMask, Digest> MaskHashes = new();

    public static StructureAnalysisDocument Create(ScanProject project, StructureAnalysisResult result, CancellationToken token = default)
    {
        var initialized = result with { Surfaces = result.Surfaces.Select(s => s with
            { SuggestedKind = s.Kind, Review = StructureReviewState.Proposed }).ToArray() };
        return new(1, StructureAnalysisResult.Algorithm, DateTimeOffset.UtcNow, Capture(project, token), initialized);
    }
    public static StructureAnalysisDocument Refresh(StructureAnalysisDocument document, ScanProject project, CancellationToken token = default) =>
        document with { IsStale = !document.Inputs.OrderBy(i => i.ObjectId).SequenceEqual(Capture(project, token)) };
    public static void WarmHashes(ScanProject project, CancellationToken token) => _ = Capture(project, token);

    private static StructureInputStamp[] Capture(ScanProject project, CancellationToken token) => project.Objects.OrderBy(o => o.Id).Select(o =>
    {
        token.ThrowIfCancellationRequested();
        var points = PointHashes.GetValue(o.Source, source => new(HashPoints(source, token))).Value;
        var mask = o.Mask is null || o.Mask.Count == o.Source.Points.Length ? "all" :
            MaskHashes.GetValue(o.Mask, value => new(Convert.ToHexString(SHA256.HashData(value.Pack(token))))).Value;
        return new StructureInputStamp(o.Id, o.Source.Points.Length, o.PointCount, points, mask, o.Transform ?? new(), o.IsVisible);
    }).ToArray();

    private static string HashPoints(ImportedScan source, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> block = stackalloc byte[12 * 1024];
        var used = 0;
        for (var i = 0; i < source.Points.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var p = source.Points[i];
            BinaryPrimitives.WriteSingleLittleEndian(block[used..], p.X);
            BinaryPrimitives.WriteSingleLittleEndian(block[(used + 4)..], p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(block[(used + 8)..], p.Z);
            used += 12;
            if (used == block.Length) { hash.AppendData(block); used = 0; }
        }
        hash.AppendData(block[..used]); return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static void Validate(StructureAnalysisDocument document, ScanProject project, CancellationToken token = default)
    {
        static bool Hash(string? value) => value?.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
        static bool Point(Vector3 value) => InspectionPoint.From(value).IsValid;
        [DoesNotReturn] static void Invalid() => throw new System.IO.InvalidDataException("Некорректные данные анализа структуры.");
        if (document.Version is not (1 or 2 or 3 or 4 or 5) || (document.Version == 5) != (document.Rooms is not null) ||
            (document.Version < 5 && (document.Version == 4) != (document.Objects is not null)) ||
            (document.Version < 4 && ((document.Version >= 2) != (document.Room is not null) || (document.Version == 3) != (document.Room?.IsManuallyEdited == true))) ||
            document.Algorithm != StructureAnalysisResult.Algorithm || document.CreatedAtUtc < DateTimeOffset.UnixEpoch ||
            document.Inputs is null || document.Inputs.Length > ScanProject.MaximumObjects || document.Inputs.Any(i => i is null) ||
            document.Inputs.Select(i => i.ObjectId).Distinct().Count() != document.Inputs.Length ||
            document.Inputs.Any(i => i.ObjectId == Guid.Empty || i.PointCount is < 1 or > ImportedScan.MaximumPoints ||
                i.IncludedCount < 0 || i.IncludedCount > i.PointCount || !Hash(i.PointsHash) ||
                (i.MaskHash != "all" && !Hash(i.MaskHash)) || (i.MaskHash == "all" && i.IncludedCount != i.PointCount) || i.Transform is null) ||
            document.Inputs.Sum(i => (long)i.PointCount) > ImportedScan.MaximumPoints) Invalid();
        foreach (var input in document.Inputs) input.Transform.Validate();
        var result = document.Result;
        if (result?.Options is null || result.Surfaces is null) Invalid();
        try { result!.Options.Validate(); }
        catch (ArgumentOutOfRangeException ex) { throw new System.IO.InvalidDataException("Некорректные параметры анализа.", ex); }
        if (result.SampleCount < 0 || result.SampleCount > result.Options.MaximumSamples ||
            result.SourcePointCount != document.Inputs.Where(i => i.IsVisible).Sum(i => i.IncludedCount) ||
            result.SampleCount != Math.Min(result.SourcePointCount, result.Options.MaximumSamples) ||
            result.Surfaces.Length > result.Options.MaximumPlanes || result.Surfaces.Any(s => s is null) ||
            result.Surfaces.Select(s => s.Id).Distinct().Count() != result.Surfaces.Length) Invalid();
        var inputs = document.Inputs.ToDictionary(i => i.ObjectId);
        var objects = project.Objects.ToDictionary(o => o.Id);
        var references = new HashSet<SourcePointReference>();
        var boundaryCount = 0;
        foreach (var surface in result.Surfaces)
        {
            token.ThrowIfCancellationRequested();
            if (surface.Id < 1 || !Enum.IsDefined(surface.Kind) || !Enum.IsDefined(surface.Review) ||
                surface.SuggestedKind is not { } suggested || !Enum.IsDefined(suggested) ||
                (surface.Review == StructureReviewState.Proposed && surface.Kind != suggested) ||
                !Point(surface.Normal) || Math.Abs(surface.Normal.LengthSquared() - 1) > .001 ||
                !double.IsFinite(surface.Offset) || Math.Abs(surface.Offset) > 175_000 || !Point(surface.Center) ||
                !double.IsFinite(surface.AreaSquareMeters) || surface.AreaSquareMeters <= 0 || surface.AreaSquareMeters > 70_000_000_000 ||
                !double.IsFinite(surface.RmsMeters) || surface.RmsMeters is < 0 or > 1 ||
                surface.Samples is null || surface.Samples.Length < result.Options.MinimumSupport ||
                surface.Boundary is null || surface.Boundary.Length < 3) Invalid();
            boundaryCount += surface.Boundary.Length;
            if (boundaryCount > result.SampleCount) Invalid();
            foreach (var p in surface.Boundary)
                if (!Point(p) || Math.Abs(PlanePatch.Distance(surface.Normal, surface.Offset, p)) > .05) Invalid();
            foreach (var sample in surface.Samples)
            {
                if (!Point(sample.Position) || !references.Add(sample.Source) || references.Count > result.SampleCount ||
                    !inputs.TryGetValue(sample.Source.ObjectId, out var input) || !input.IsVisible ||
                    sample.Source.PointIndex < 0 || sample.Source.PointIndex >= input.PointCount) Invalid();
                if (!document.IsStale)
                {
                    if (!objects.TryGetValue(sample.Source.ObjectId, out var obj) || !obj.IsVisible ||
                        sample.Source.PointIndex >= obj.Source.Points.Length || !obj.Includes(sample.Source.PointIndex) ||
                        Vector3.DistanceSquared(sample.Position, Vector3.Transform(obj.Source.Points[sample.Source.PointIndex].Position, obj.Matrix)) > 1e-8f) Invalid();
                }
            }
        }
        if (document.Objects is { } groups)
        {
            if (document.ObjectOptions is null || !Hash(document.ObjectSourceSignature) || groups.Length > PointObjectAnalyzer.MaximumObjects ||
                groups.Any(g => g is null) || groups.Select(g => g.Id).Distinct().Count() != groups.Length) Invalid();
            try { document.ObjectOptions!.Validate(); }
            catch (ArgumentOutOfRangeException ex) { throw new System.IO.InvalidDataException("Некорректные параметры предметов.", ex); }
            var groupedReferences = new HashSet<SourcePointReference>();
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                if (group.Id < 1 || group.Id > 1_000_000 || string.IsNullOrWhiteSpace(group.Label) || group.Label.Length > 80 ||
                    group.Color > 0xffffff || !Enum.IsDefined(group.Review) || group.Samples is null || group.Samples.Length < 3 ||
                    !Point(group.Minimum) || !Point(group.Maximum)) Invalid();
                var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
                foreach (var sample in group.Samples)
                {
                    if (!Point(sample.Position) || !groupedReferences.Add(sample.Source) || groupedReferences.Count > PointObjectAnalyzer.MaximumSamples ||
                        !inputs.TryGetValue(sample.Source.ObjectId, out var input) || !input.IsVisible || sample.Source.PointIndex < 0 || sample.Source.PointIndex >= input.PointCount) Invalid();
                    if (!document.IsStale && (!objects.TryGetValue(sample.Source.ObjectId, out var obj) || !obj.IsVisible || sample.Source.PointIndex >= obj.Source.Points.Length || !obj.Includes(sample.Source.PointIndex) ||
                        Vector3.DistanceSquared(sample.Position, Vector3.Transform(obj.Source.Points[sample.Source.PointIndex].Position, obj.Matrix)) > 1e-8f)) Invalid();
                    min = Vector3.Min(min, sample.Position); max = Vector3.Max(max, sample.Position);
                }
                if (Vector3.DistanceSquared(min, group.Minimum) > 1e-8f || Vector3.DistanceSquared(max, group.Maximum) > 1e-8f) Invalid();
            }
        }
        else if (document.ObjectOptions is not null || document.ObjectSourceSignature is not null) Invalid();
        RoomPlanValidation.Validate(document, token);
        if (document.Rooms is { } rooms)
        {
            if (rooms.Length > 64 || rooms.Any(r => r is null || r.Plan is null || r.Id == Guid.Empty || string.IsNullOrWhiteSpace(r.Floor) || r.Floor.Length > 80) ||
                rooms.Select(r => r.Id).Distinct().Count() != rooms.Length) Invalid();
            foreach (var room in rooms) RoomPlanValidation.Validate(document with { Room = room.Plan }, token);
            if (document.ActiveRoomId == Guid.Empty ? document.Room is not null :
                document.Room is null || rooms.FirstOrDefault(r => r.Id == document.ActiveRoomId) is not { } active || !RoomPlanValidation.Same(active.Plan, document.Room)) Invalid();
        }
        else if (document.ActiveRoomId != Guid.Empty) Invalid();
    }
}
