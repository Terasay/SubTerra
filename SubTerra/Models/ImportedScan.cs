using System.IO;
using System.Numerics;

namespace SubTerra.Models;

public enum ScanUnit { Meters, Centimeters, Millimeters }
public enum ScanUpAxis { Z, Y }
public enum TextPointLayout { Xyz, XyzRgb, XyzNormalRgb }
public sealed record ScanImportOptions(ScanUnit Unit = ScanUnit.Millimeters, ScanUpAxis UpAxis = ScanUpAxis.Z,
    bool LocalOrigin = true, TextPointLayout TextLayout = TextPointLayout.Xyz)
{
    public double Scale => Unit switch { ScanUnit.Meters => 1, ScanUnit.Centimeters => .01, ScanUnit.Millimeters => .001,
        _ => throw new InvalidDataException("Неизвестные единицы импорта.") };
}

// Source coordinates are retained in double precision. Local geometry is always Z-up, in meters.
public sealed record ImportMetadata(string FileName, ScanUnit Unit, ScanUpAxis UpAxis,
    double OriginX, double OriginY, double OriginZ)
{
    public bool IsSynthetic { get; init; }
}

public sealed class ImportedScan
{
    public const int MaximumPoints = 5_000_000;
    public const int MaximumTriangles = 5_000_000;
    public required ImportMetadata Metadata { get; init; }
    public required ScanPoint[] Points { get; init; }
    public uint[] Colors { get; init; } = [];
    public int[] Triangles { get; init; } = [];
    public bool HasMesh => Triangles.Length > 0;
    public (Vector3 Minimum, Vector3 Maximum) Bounds
    {
        get
        {
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            foreach (var p in Points) { min = Vector3.Min(min, p.Position); max = Vector3.Max(max, p.Position); }
            return (min, max);
        }
    }
    public void Validate(CancellationToken token = default)
    {
        if (Metadata is null || string.IsNullOrWhiteSpace(Metadata.FileName) || Metadata.FileName.Length > 260 ||
            !Enum.IsDefined(Metadata.Unit) || !Enum.IsDefined(Metadata.UpAxis) ||
            !double.IsFinite(Metadata.OriginX) || !double.IsFinite(Metadata.OriginY) || !double.IsFinite(Metadata.OriginZ) ||
            Math.Abs(Metadata.OriginX) > 1e12 || Math.Abs(Metadata.OriginY) > 1e12 || Math.Abs(Metadata.OriginZ) > 1e12 ||
            Points is null || Points.Length is < 1 or > MaximumPoints ||
            Colors is null || (Colors.Length != 0 && Colors.Length != Points.Length) ||
            Triangles is null || Triangles.Length % 3 != 0 || Triangles.Length / 3 > MaximumTriangles)
            throw new InvalidDataException("Некорректное описание импортированного скана.");
        for (var i = 0; i < Points.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var p = Points[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) ||
                Math.Abs(p.X) > 100_000 || Math.Abs(p.Y) > 100_000 || Math.Abs(p.Z) > 100_000 ||
                !float.IsFinite(p.Intensity) || p.Intensity is < 0 or > 1 || (Colors.Length > 0 && Colors[i] > 0xffffff))
                throw new InvalidDataException("Скан содержит некорректные координаты или цвет.");
        }
        for (var i = 0; i < Triangles.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if ((uint)Triangles[i] >= Points.Length) throw new InvalidDataException("Повреждены индексы поверхности.");
        }
    }
    public ScanSession ToSession(UserAnnotation[]? annotations = null) => new()
    {
        ScenarioId = "imported", Settings = new(), Frames = [], ImportedScan = this, Annotations = annotations ?? []
    };
}
