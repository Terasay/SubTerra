using System.IO;
using System.IO.Compression;
using System.Text.Json;
using SubTerra.Models;

namespace SubTerra.Services;

public sealed partial class SessionStorage
{
    private sealed record ObjectManifest(Guid Id, string Name, bool IsVisible, uint? Tint, ScanTransform? Transform,
        ImportMetadata Metadata, int PointCount, int TriangleCount, bool HasColors, bool HasMask);
    private sealed record ProjectManifest(ObjectManifest[] Objects, Guid ActiveObjectId, ImportMetadata? Origin, bool HasStructure = false);
    private static string ObjectPrefix(Guid id) => $"objects/{id:N}/";
    private static void SaveProject(string path, ScanSession session, CancellationToken token, IProgress<double>? progress)
    {
        var project = session.Project!;
        project.Validate(token); ValidateAnnotations(session.Annotations);
        var structureBytes = PrepareStructure(project, token);
        if (session.Frames.Length != 0 || session.Adjustments.Length != 0) throw new InvalidDataException("Проект сканов не содержит кадров симулятора.");
        var objects = project.Objects.Select(o => new ObjectManifest(o.Id, o.Name, o.IsVisible, o.Tint, o.Transform,
            o.Source.Metadata, o.Source.Points.Length, o.Source.Triangles.Length / 3, o.Source.Colors.Length > 0, o.Mask is not null)).ToArray();
        var manifest = new Manifest(structureBytes is null ? 5 : project.Structure!.Rooms is not null ? 10 : project.Structure.Objects is not null ? 9 : project.Structure.Room is null ? 6 : project.Structure.Room.IsManuallyEdited ? 8 : 7, "project", "m", "imported", new(), 0, objects.Sum(o => o.PointCount),
            Annotations: session.Annotations, TriangleCount: objects.Sum(o => o.TriangleCount),
            Project: new(objects, project.ActiveObjectId, project.Origin, structureBytes is not null));
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
            for (var objectIndex = 0; objectIndex < project.Objects.Length; objectIndex++)
            {
                var o = project.Objects[objectIndex]; var prefix = ObjectPrefix(o.Id);
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
            (manifest.Adjustments?.Length ?? 0) != 0 || info.HasStructure != (manifest.SchemaVersion is 6 or 7 or 8 or 9 or 10))
            throw new InvalidDataException("Некорректное описание проекта сканов.");
        var annotations = manifest.Annotations ?? []; ValidateAnnotations(annotations);
        var objects = new ScanObject[info.Objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var o = info.Objects[i]; var prefix = ObjectPrefix(o.Id);
            var sourceManifest = new Manifest(4, "import", "m", "imported", new(), 0, o.PointCount,
                ImportMetadata: o.Metadata, TriangleCount: o.TriangleCount, HasColors: o.HasColors);
            var scan = LoadImported(archive, sourceManifest, token, null, prefix).ImportedScan!;
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
        var project = new ScanProject(objects, info.ActiveObjectId, info.Origin);
        project.Validate(token);
        if (info.HasStructure) project = project with { Structure = ReadStructure(archive, project, token) };
        if ((manifest.SchemaVersion == 10) != (project.Structure?.Rooms is not null) ||
            (manifest.SchemaVersion < 10 && ((manifest.SchemaVersion == 9) != (project.Structure?.Objects is not null) ||
            (manifest.SchemaVersion < 9 && ((manifest.SchemaVersion is 7 or 8) != (project.Structure?.Room is not null) ||
            (manifest.SchemaVersion == 8) != (project.Structure?.Room?.IsManuallyEdited == true)))))) throw new InvalidDataException("Анализ не соответствует версии проекта.");
        progress?.Report(100);
        return project.ToSession(annotations);
    }
}
