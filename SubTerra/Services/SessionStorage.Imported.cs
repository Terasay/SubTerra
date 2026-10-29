using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SubTerra.Models;

namespace SubTerra.Services;

public sealed partial class SessionStorage
{
    private static void SaveImported(string path, ScanSession session, int frameCount, CancellationToken token, IProgress<double>? progress)
    {
        var scan = session.ImportedScan!;
        scan.Validate(token);
        ValidateAnnotations(session.Annotations);
        if (frameCount != 1 || session.Frames.Length != 0 || session.Adjustments.Length != 0)
            throw new InvalidDataException("Импортированный скан не содержит кадров виртуального датчика.");
        var manifest = new Manifest(4, "import", "m", "imported", new(), 0, scan.Points.Length,
            Annotations: session.Annotations, ImportMetadata: scan.Metadata, TriangleCount: scan.Triangles.Length / 3, HasColors: scan.Colors.Length > 0);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        AtomicWrite(path, temporary =>
        {
            using var archive = ZipFile.Open(temporary, ZipArchiveMode.Create);
            using (var metadata = archive.CreateEntry("manifest.json").Open()) metadata.Write(bytes);
            using (var writer = new BinaryWriter(archive.CreateEntry("points.bin", CompressionLevel.Fastest).Open()))
                for (var i = 0; i < scan.Points.Length; i++)
                {
                    if ((i & 4095) == 0) { token.ThrowIfCancellationRequested(); progress?.Report(i * 65d / scan.Points.Length); }
                    var p = scan.Points[i]; writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); writer.Write(p.Intensity);
                    if (manifest.HasColors) writer.Write(scan.Colors[i]);
                }
            using (var writer = new BinaryWriter(archive.CreateEntry("triangles.bin", CompressionLevel.Fastest).Open()))
                for (var i = 0; i < scan.Triangles.Length; i++)
                {
                    if ((i & 4095) == 0) { token.ThrowIfCancellationRequested(); progress?.Report(65 + i * 30d / Math.Max(1, scan.Triangles.Length)); }
                    writer.Write(scan.Triangles[i]);
                }
        }, token);
        progress?.Report(100);
    }

    private static ScanSession LoadImported(ZipArchive archive, Manifest manifest, CancellationToken token, IProgress<double>? progress, string prefix = "")
    {
        if (manifest.SourceType != "import" || manifest.CoordinateUnit != "m" || manifest.ScenarioId != "imported" ||
            manifest.FrameCount != 0 || manifest.PointCount is < 1 or > ImportedScan.MaximumPoints ||
            manifest.TriangleCount is < 0 or > ImportedScan.MaximumTriangles || manifest.ImportMetadata is null ||
            (manifest.Adjustments?.Length ?? 0) != 0)
            throw new InvalidDataException("Некорректное описание импортированного скана.");
        var annotations = manifest.Annotations ?? [];
        ValidateAnnotations(annotations);
        var pointsEntry = archive.GetEntry(prefix + "points.bin") ?? throw new InvalidDataException("В сеансе нет точек.");
        var trianglesEntry = archive.GetEntry(prefix + "triangles.bin") ?? throw new InvalidDataException("В сеансе нет описания сетки.");
        if (pointsEntry.Length != manifest.PointCount * (manifest.HasColors ? 20L : 16L) || trianglesEntry.Length != manifest.TriangleCount * 12L)
            throw new InvalidDataException("Размер скана не соответствует описанию.");
        var points = new ScanPoint[manifest.PointCount];
        var colors = manifest.HasColors ? new uint[points.Length] : [];
        var triangles = new int[manifest.TriangleCount * 3];
        using (var reader = new BinaryReader(pointsEntry.Open()))
            for (var i = 0; i < points.Length; i++)
            {
                if ((i & 4095) == 0) { token.ThrowIfCancellationRequested(); progress?.Report(i * 65d / points.Length); }
                points[i] = new(ReadFinite(reader), ReadFinite(reader), ReadFinite(reader), ReadFinite(reader));
                if (manifest.HasColors) colors[i] = reader.ReadUInt32();
            }
        using (var reader = new BinaryReader(trianglesEntry.Open()))
            for (var i = 0; i < triangles.Length; i++)
            {
                if ((i & 4095) == 0) { token.ThrowIfCancellationRequested(); progress?.Report(65 + i * 30d / Math.Max(1, triangles.Length)); }
                triangles[i] = reader.ReadInt32();
            }
        var scan = new ImportedScan { Metadata = manifest.ImportMetadata, Points = points, Colors = colors, Triangles = triangles };
        scan.Validate(token);
        progress?.Report(100);
        return scan.ToSession(annotations);
    }

    public Task ExportImportedPlyAsync(string path, ImportedScan scan, CancellationToken token = default,
        IProgress<double>? progress = null) => Task.Run(() =>
    {
        scan.Validate(token);
        AtomicWrite(path, temporary =>
        {
            using var stream = File.Create(temporary);
            using (var header = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true))
            {
                header.WriteLine("ply\nformat binary_little_endian 1.0\ncomment Subterra scan; units meters; Z up; local coordinates");
                if (scan.Metadata.IsSynthetic) header.WriteLine("comment subterra_contains_synthetic_data");
                header.WriteLine($"element vertex {scan.Points.Length}");
                header.WriteLine("property float x\nproperty float y\nproperty float z\nproperty float intensity");
                if (scan.Colors.Length > 0) header.WriteLine("property uchar red\nproperty uchar green\nproperty uchar blue");
                if (scan.HasMesh) header.WriteLine($"element face {scan.Triangles.Length / 3}\nproperty list uchar int vertex_indices");
                header.WriteLine("end_header");
            }
            using var writer = new BinaryWriter(stream);
            for (var i = 0; i < scan.Points.Length; i++)
            {
                if ((i & 4095) == 0) { token.ThrowIfCancellationRequested(); progress?.Report(i * 65d / scan.Points.Length); }
                var p = scan.Points[i]; writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); writer.Write(p.Intensity);
                if (scan.Colors.Length > 0)
                { var rgb = scan.Colors[i]; writer.Write((byte)(rgb >> 16)); writer.Write((byte)(rgb >> 8)); writer.Write((byte)rgb); }
            }
            for (var i = 0; i < scan.Triangles.Length; i += 3)
            {
                if ((i % 4095) == 0) { token.ThrowIfCancellationRequested(); progress?.Report(65 + i * 30d / scan.Triangles.Length); }
                writer.Write((byte)3); writer.Write(scan.Triangles[i]); writer.Write(scan.Triangles[i + 1]); writer.Write(scan.Triangles[i + 2]);
            }
        }, token);
        progress?.Report(100);
    }, token);

    private static void AtomicWrite(string path, Action<string> write, CancellationToken token)
    {
        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            write(temporary);
            token.ThrowIfCancellationRequested();
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
