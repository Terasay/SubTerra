using System.IO;
using System.Text.Json;
using SubTerra.Models;
using SubTerra.Services;

namespace SubTerra.Diagnostics;

internal static class StudioExportCheck
{
    private const string Usage = "--check-studio-export <файл PLY/ASC/XYZ/TXT> <mm|cm|m> <z|y> [xyz|xyz-rgb|xyz-normal-rgb]";

    public static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        if (args.Length is < 4 or > 5) throw new ArgumentException(Usage);
        var path = Path.GetFullPath(args[1]);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".ply" or ".asc" or ".xyz" or ".txt")) throw new ArgumentException(Usage);
        if (extension == ".ply" && args.Length == 5)
            throw new ArgumentException("Для PLY столбцы определяются заголовком. Укажите только единицы и ось.");
        var unit = args[2].ToLowerInvariant() switch
        {
            "mm" => ScanUnit.Millimeters, "cm" => ScanUnit.Centimeters, "m" => ScanUnit.Meters,
            _ => throw new ArgumentException(Usage)
        };
        var axis = args[3].ToLowerInvariant() switch
        {
            "z" => ScanUpAxis.Z, "y" => ScanUpAxis.Y, _ => throw new ArgumentException(Usage)
        };
        var layout = (args.Length == 5 ? args[4].ToLowerInvariant() : "xyz") switch
        {
            "xyz" => TextPointLayout.Xyz, "xyz-rgb" => TextPointLayout.XyzRgb,
            "xyz-normal-rgb" => TextPointLayout.XyzNormalRgb, _ => throw new ArgumentException(Usage)
        };

        var scan = await new ScanImporter().ImportAsync(path, new(unit, axis, TextLayout: layout), token);
        var project = ScanProject.FromSession(scan.ToSession());
        var storage = new SessionStorage();
        var tempSession = Path.Combine(Path.GetTempPath(), $"subterra-studio-check-{Guid.NewGuid():N}.subterra");
        try
        {
            await storage.SaveAsync(tempSession, project.ToSession(), 1, token);
            var restored = (await storage.LoadAsync(tempSession, token)).Project;
            var copy = restored?.Active?.Source;
            if (copy is null || restored?.Origin != project.Origin || copy.Metadata != scan.Metadata ||
                !copy.Points.AsSpan().SequenceEqual(scan.Points) || !copy.Colors.AsSpan().SequenceEqual(scan.Colors) ||
                !copy.Triangles.AsSpan().SequenceEqual(scan.Triangles))
                throw new InvalidDataException("При повторном открытии проекта изменились измерения или параметры импорта.");
        }
        finally { File.Delete(tempSession); }

        var (minimum, maximum) = scan.Bounds;
        var size = maximum - minimum;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            File = Path.GetFileName(path), InputUnit = unit.ToString(), InputUpAxis = axis.ToString(),
            TextLayout = extension == ".ply" ? null : layout.ToString(), Synthetic = scan.Metadata.IsSynthetic,
            Points = scan.Points.Length, ColoredPoints = scan.Colors.Length, Triangles = scan.Triangles.Length / 3,
            SizeMeters = new { size.X, size.Y, size.Z }, ProjectRoundTrip = true
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
