using System.IO;
using System.IO.Compression;
using System.Text.Json;
using SubTerra.Models;

namespace SubTerra.Services;

public sealed partial class SessionStorage
{
    private sealed record ObjectManifest(Guid Id, string Name, bool IsVisible, uint? Tint, ScanTransform? Transform,
        ImportMetadata Metadata, int PointCount, int TriangleCount, bool HasColors, bool HasMask, bool HasAppearance = false);
    private sealed record ProjectManifest(ObjectManifest[] Objects, Guid ActiveObjectId, ImportMetadata? Origin, bool HasStructure = false,
        ProjectAnalysisMode AnalysisMode = ProjectAnalysisMode.Room, bool HasTunnel = false);
    private static string ObjectPrefix(Guid id) => $"objects/{id:N}/";
    private static void SaveProject(string path, ScanSession session, CancellationToken token, IProgress<double>? progress)
    {
        var project = session.Project!;
        project.Validate(token); ValidateAnnotations(session.Annotations);
        var structureBytes = PrepareStructure(project, token);
        var tunnelBytes = PrepareTunnel(project, token);
        if (session.Frames.Length != 0 || session.Adjustments.Length != 0) throw new InvalidDataException("Проект сканов не содержит кадров симулятора.");
        var objects = project.Objects.Select(o => new ObjectManifest(o.Id, o.Name, o.IsVisible, o.Tint, o.Transform,
            o.Source.Metadata, o.Source.Points.Length, o.Source.Triangles.Length / 3, o.Source.Colors.Length > 0, o.Mask is not null, o.Source.Appearance is not null)).ToArray();
        var schema = project.Tunnel?.Version == 2 ? 14 : project.AnalysisMode != ProjectAnalysisMode.Room || tunnelBytes is not null ? 13 :
            objects.Any(o => o.HasAppearance) ? 11 : structureBytes is null ? 5 : project.Structure!.Rooms is not null ? 10 : project.Structure.Objects is not null ? 9 : project.Structure.Room is null ? 6 : project.Structure.Room.IsManuallyEdited ? 8 : 7;
        var manifest = new Manifest(schema, "project", "m", "imported", new(), 0, objects.Sum(o => o.PointCount),
            Annotations: session.Annotations, TriangleCount: objects.Sum(o => o.TriangleCount),
            Project: new(objects, project.ActiveObjectId, project.Origin, structureBytes is not null, project.AnalysisMode, tunnelBytes is not null));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (bytes.Length > MaximumManifestBytes) throw new InvalidDataException("Описание проекта слишком большое.");
        AtomicWrite(path, temporary =>
        {
            using var archive = ZipFile.Open(temporary, ZipArchiveMode.Create);
            using (var stream = archive.CreateEntry("manifest.json").Open()) stream.Write(bytes);
            if (structureBytes is not null)
            {
                token.ThrowIfCancellationRequested();
                using var stream = archive.CreateEntry(StructureEntry, CompressionLevel.Fastest).Open(); stream.Write(structureBytes);
            }
            if (tunnelBytes is not null)
            {
                token.ThrowIfCancellationRequested();
                using var stream = archive.CreateEntry(TunnelEntry, CompressionLevel.Fastest).Open(); stream.Write(tunnelBytes);
            }
            for (var objectIndex = 0; objectIndex < project.Objects.Length; objectIndex++)
            {
                var o = project.Objects[objectIndex]; var prefix = ObjectPrefix(o.Id);
                if (o.Source.Appearance is not null) SaveAppearance(archive, prefix, o.Source.Appearance, token);
                using (var writer = new BinaryWriter(archive.CreateEntry(prefix + "points.bin", CompressionLevel.Fastest).Open()))
                    for (var i = 0; i < o.Source.Points.Length; i++)
                    {
                        if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                        var p = o.Source.Points[i]; writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); writer.Write(p.Intensity);
                        if (o.Source.Colors.Length > 0) writer.Write(o.Source.Colors[i]);
                    }
                using (var writer = new BinaryWriter(archive.CreateEntry(prefix + "triangles.bin", CompressionLevel.Fastest).Open()))
                    for (var i = 0; i < o.Source.Triangles.Length; i++)
                    {
                        if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                        writer.Write(o.Source.Triangles[i]);
                    }
                if (o.Mask is not null)
                {
                    using var stream = archive.CreateEntry(prefix + "mask.bin", CompressionLevel.Fastest).Open();
                    stream.Write(o.Mask.Pack(token));
                }
                progress?.Report((objectIndex + 1d) / project.Objects.Length * 95);
            }
        }, token);
        progress?.Report(100);
    }
    private static ScanSession LoadProject(ZipArchive archive, Manifest manifest, CancellationToken token, IProgress<double>? progress)
    {
        var info = manifest.Project;
        if (manifest.SourceType != "project" || manifest.CoordinateUnit != "m" || manifest.ScenarioId != "imported" || manifest.FrameCount != 0 ||
            info?.Objects is null || info.Objects.Length > ScanProject.MaximumObjects || info.Objects.Any(o => o is null || o.Id == Guid.Empty) ||
            info.Objects.Select(o => o.Id).Distinct().Count() != info.Objects.Length ||
            info.Objects.Any(o => o.PointCount is < 1 or > ImportedScan.MaximumPoints || o.TriangleCount is < 0 or > ImportedScan.MaximumTriangles) ||
            info.Objects.Sum(o => (long)o.PointCount) != manifest.PointCount || manifest.PointCount > ImportedScan.MaximumPoints ||
            info.Objects.Sum(o => (long)o.TriangleCount) != manifest.TriangleCount || manifest.TriangleCount > ImportedScan.MaximumTriangles ||
            (manifest.Adjustments?.Length ?? 0) != 0 || (manifest.SchemaVersion <= 10 && info.HasStructure != (manifest.SchemaVersion is 6 or 7 or 8 or 9 or 10)) ||
            (manifest.SchemaVersion < 13 && (manifest.SchemaVersion == 11) != info.Objects.Any(o => o.HasAppearance)) ||
            !Enum.IsDefined(info.AnalysisMode) || (manifest.SchemaVersion < 13 && (info.AnalysisMode != ProjectAnalysisMode.Room || info.HasTunnel)) ||
            (info.HasTunnel ? archive.Entries.Count(e => e.FullName == TunnelEntry) != 1 : archive.Entries.Any(e => e.FullName == TunnelEntry)))
            throw new InvalidDataException("Некорректное описание проекта сканов.");
        var annotations = manifest.Annotations ?? []; ValidateAnnotations(annotations);
        var objects = new ScanObject[info.Objects.Length];
        long textureBytes = 0;
        for (var i = 0; i < objects.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var o = info.Objects[i]; var prefix = ObjectPrefix(o.Id);
            var sourceManifest = new Manifest(o.HasAppearance ? 12 : 4, "import", "m", "imported", new(), 0, o.PointCount,
                ImportMetadata: o.Metadata, TriangleCount: o.TriangleCount, HasColors: o.HasColors, HasAppearance: o.HasAppearance);
            var scan = LoadImported(archive, sourceManifest, token, null, prefix).ImportedScan!;
            textureBytes += scan.Appearance?.Textures.Sum(t => (long)t.Data.Length) ?? 0;
            if (textureBytes > MeshAppearance.MaximumTotalTextureBytes) throw new InvalidDataException("Текстуры проекта превышают 128 МБ.");
            ScanPointMask? mask = null;
            if (o.HasMask)
            {
                var entry = archive.GetEntry(prefix + "mask.bin") ?? throw new InvalidDataException("Нет маски очистки объекта.");
                if (entry.Length != (o.PointCount + 7L) / 8) throw new InvalidDataException("Размер маски очистки не совпадает.");
                var bytes = new byte[(int)entry.Length]; using var stream = entry.Open(); stream.ReadExactly(bytes);
                mask = ScanPointMask.Unpack(bytes, o.PointCount);
            }
            objects[i] = new(o.Id, o.Name, scan, o.IsVisible, o.Tint, o.Transform, mask);
            progress?.Report((i + 1d) / objects.Length * 95);
        }
        var project = new ScanProject(objects, info.ActiveObjectId, info.Origin) { AnalysisMode = info.AnalysisMode };
        project.Validate(token);
        if (info.HasStructure) project = project with { Structure = ReadStructure(archive, project, token) };
        if (info.HasTunnel) project = project with { Tunnel = ReadTunnel(archive, project, token) };
        if ((manifest.SchemaVersion == 14) != (project.Tunnel?.Version == 2)) throw new InvalidDataException("Версия анализа тоннеля не соответствует схеме проекта.");
        if (manifest.SchemaVersion <= 10 && ((manifest.SchemaVersion == 10) != (project.Structure?.Rooms is not null) ||
            (manifest.SchemaVersion < 10 && ((manifest.SchemaVersion == 9) != (project.Structure?.Objects is not null) ||
            (manifest.SchemaVersion < 9 && ((manifest.SchemaVersion is 7 or 8) != (project.Structure?.Room is not null) ||
            (manifest.SchemaVersion == 8) != (project.Structure?.Room?.IsManuallyEdited == true))))))) throw new InvalidDataException("Анализ не соответствует версии проекта.");
        progress?.Report(100);
        return project.ToSession(annotations);
    }

    private const string TunnelEntry = "tunnel/analysis.json";
    private const int MaximumTunnelBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions TunnelJson = new() { IgnoreReadOnlyProperties = true, MaxDepth = 16 };

    private static byte[]? PrepareTunnel(ScanProject project, CancellationToken token)
    {
        if (project.Tunnel is not { } document) return null;
        TunnelAnalyzer.Validate(document with { IsStale = true }, project, token);
        document = TunnelAnalyzer.Refresh(document, project, token);
        TunnelAnalyzer.Validate(document, project, token);
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, TunnelJson);
        if (bytes.Length > MaximumTunnelBytes) throw new InvalidDataException("Анализ тоннеля превышает допустимый размер.");
        return bytes;
    }

    private static TunnelAnalysisDocument ReadTunnel(ZipArchive archive, ScanProject project, CancellationToken token)
    {
        var entries = archive.Entries.Where(e => e.FullName == TunnelEntry).ToArray();
        if (entries.Length != 1 || entries[0].Length is < 1 or > MaximumTunnelBytes)
            throw new InvalidDataException("Нет анализа тоннеля или его размер некорректен.");
        using var stream = entries[0].Open();
        var bytes = new byte[(int)entries[0].Length]; stream.ReadExactly(bytes);
        token.ThrowIfCancellationRequested();
        TunnelAnalysisDocument document;
        try { document = JsonSerializer.Deserialize<TunnelAnalysisDocument>(bytes, TunnelJson) ?? throw new InvalidDataException("Нет данных анализа тоннеля."); }
        catch (JsonException ex) { throw new InvalidDataException("Повреждено описание анализа тоннеля.", ex); }
        TunnelAnalyzer.Validate(document with { IsStale = true }, project, token);
        document = TunnelAnalyzer.Refresh(document, project, token);
        TunnelAnalyzer.Validate(document, project, token);
        return document;
    }
}
