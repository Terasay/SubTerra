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
    public string? CaptureId { get; init; }
}

public sealed record MeshTexture(byte[] Data, string MimeType, int WrapS = 10497, int WrapT = 10497);
public sealed record MeshMaterial(string Name, float Red = 1, float Green = 1, float Blue = 1, float Alpha = 1,
    int BaseColorTexture = -1, float Metallic = 1, float Roughness = 1, int NormalTexture = -1,
    int MetallicRoughnessTexture = -1, int EmissiveTexture = -1, int OcclusionTexture = -1,
    float EmissiveRed = 0, float EmissiveGreen = 0, float EmissiveBlue = 0,
    bool DoubleSided = false, string AlphaMode = "OPAQUE", float AlphaCutoff = .5f, bool Unlit = false,
    float NormalScale = 1, float OcclusionStrength = 1);
public sealed record MeshPart(int IndexOffset, int IndexCount, int MaterialIndex);
public sealed record MeshAppearance(Vector2[] TextureCoordinates, Vector3[] Normals, Vector4[] VertexColors,
    MeshPart[] Parts, MeshMaterial[] Materials, MeshTexture[] Textures)
{
    public const int MaximumTextureBytes = 32 * 1024 * 1024, MaximumTotalTextureBytes = 128 * 1024 * 1024;
    public void Validate(int pointCount, int indexCount, CancellationToken token)
    {
        if (TextureCoordinates is null || Normals is null || VertexColors is null || Parts is null || Materials is null || Textures is null ||
            TextureCoordinates.Length != pointCount || Normals.Length != pointCount || VertexColors.Length != pointCount ||
            Parts.Length is < 1 or > 1024 || Materials.Length is < 1 or > 4096 || Textures.Length > 512 ||
            Textures.Any(t => t is null || t.Data is null || t.Data.Length is < 1 or > MaximumTextureBytes || t.MimeType is not ("image/png" or "image/jpeg") ||
                t.WrapS is not (10497 or 33071 or 33648) || t.WrapT is not (10497 or 33071 or 33648)) ||
            Textures.Sum(t => (long)t.Data.Length) > MaximumTotalTextureBytes)
            throw new InvalidDataException("Некорректные материалы или текстуры сетки.");
        var offset = 0;
        foreach (var part in Parts)
        {
            if (part is null || part.IndexOffset != offset || part.IndexCount <= 0 || part.IndexCount % 3 != 0 ||
                part.IndexCount > indexCount - offset || (uint)part.MaterialIndex >= Materials.Length)
                throw new InvalidDataException("Повреждены группы материалов сетки.");
            offset += part.IndexCount;
        }
        if (offset != indexCount) throw new InvalidDataException("Материалы не покрывают сетку.");
        foreach (var m in Materials)
        {
            if (m is null || m.Name is null || m.Name.Length > 256 || m.AlphaMode is not ("OPAQUE" or "MASK" or "BLEND") ||
                !float.IsFinite(m.NormalScale) || Math.Abs(m.NormalScale) > 100 ||
                new[] { m.Red, m.Green, m.Blue, m.Alpha, m.Metallic, m.Roughness, m.AlphaCutoff, m.EmissiveRed, m.EmissiveGreen, m.EmissiveBlue, m.OcclusionStrength }.Any(f => !float.IsFinite(f) || f is < 0 or > 1) ||
                new[] { m.BaseColorTexture, m.NormalTexture, m.MetallicRoughnessTexture, m.EmissiveTexture, m.OcclusionTexture }.Any(i => i < -1 || i >= Textures.Length))
                throw new InvalidDataException("Некорректный материал сетки.");
        }
        for (var i = 0; i < pointCount; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var uv = TextureCoordinates[i]; var n = Normals[i]; var c = VertexColors[i];
            if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y) || Math.Abs(uv.X) > 1e6 || Math.Abs(uv.Y) > 1e6 ||
                !float.IsFinite(n.X) || !float.IsFinite(n.Y) || !float.IsFinite(n.Z) || n.LengthSquared() > 4 ||
                !float.IsFinite(c.X + c.Y + c.Z + c.W) || Vector4.Min(c, Vector4.Zero) != Vector4.Zero || Vector4.Max(c, Vector4.One) != Vector4.One)
                throw new InvalidDataException("Некорректные UV, нормали или цвета вершин.");
        }
    }
}

public sealed class ImportedScan
{
    public const int MaximumPoints = 5_000_000;
    public const int MaximumTriangles = 5_000_000;
    public required ImportMetadata Metadata { get; init; }
    public required ScanPoint[] Points { get; init; }
    public uint[] Colors { get; init; } = [];
    public int[] Triangles { get; init; } = [];
    public MeshAppearance? Appearance { get; init; }
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
        if (Metadata is null || string.IsNullOrWhiteSpace(Metadata.FileName) || Metadata.FileName.Length > 260 || Metadata.CaptureId is { Length: > 128 } ||
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
        Appearance?.Validate(Points.Length, Triangles.Length, token);
    }
    public ScanSession ToSession(UserAnnotation[]? annotations = null) => new()
    {
        ScenarioId = "imported", Settings = new(), Frames = [], ImportedScan = this, Annotations = annotations ?? []
    };
}
