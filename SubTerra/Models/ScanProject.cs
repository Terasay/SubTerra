using System.IO;
using System.Numerics;
using System.Text.Json.Serialization;

namespace SubTerra.Models;

public sealed record ScanTransform(float X = 0, float Y = 0, float Z = 0,
    float RotateX = 0, float RotateY = 0, float RotateZ = 0)
{
    [JsonIgnore] public Matrix4x4 Matrix => Matrix4x4.CreateRotationX(RotateX * MathF.PI / 180) *
        Matrix4x4.CreateRotationY(RotateY * MathF.PI / 180) * Matrix4x4.CreateRotationZ(RotateZ * MathF.PI / 180) *
        Matrix4x4.CreateTranslation(X, Y, Z);
    public void Validate()
    {
        if (new[] { X, Y, Z, RotateX, RotateY, RotateZ }.Any(v => !float.IsFinite(v)) ||
            Math.Abs(X) > 100_000 || Math.Abs(Y) > 100_000 || Math.Abs(Z) > 100_000 ||
            Math.Abs(RotateX) > 360 || Math.Abs(RotateY) > 360 || Math.Abs(RotateZ) > 360)
            throw new InvalidDataException("Некорректное положение объекта.");
    }
}

/// <summary>Immutable inclusion mask. Histories share original scans and store only small masks.</summary>
public sealed class ScanPointMask
{
    private readonly bool[] _included;
    public int Length => _included.Length;
    public int Count { get; }
    public bool this[int index] => _included[index];
    public ScanPointMask(bool[] included) { _included = (bool[])included.Clone(); Count = _included.Count(v => v); }
    public bool[] Copy() => (bool[])_included.Clone();
    public byte[] Pack(CancellationToken token = default)
    {
        var bytes = new byte[(Length + 7) / 8];
        for (var i = 0; i < Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (_included[i]) bytes[i / 8] |= (byte)(1 << (i % 8));
        }
        return bytes;
    }
    public static ScanPointMask Unpack(byte[] bytes, int length)
    {
        if (bytes.Length != (length + 7) / 8 || (length % 8 != 0 && (bytes[^1] >> (length % 8)) != 0))
            throw new InvalidDataException("Повреждена маска очистки.");
        var included = new bool[length];
        for (var i = 0; i < length; i++) included[i] = (bytes[i / 8] & 1 << (i % 8)) != 0;
        return new(included);
    }
}

public sealed record ScanObject(Guid Id, string Name, ImportedScan Source, bool IsVisible = true,
    uint? Tint = null, ScanTransform? Transform = null, ScanPointMask? Mask = null)
{
    public int PointCount => Mask?.Count ?? Source.Points.Length;
    public Matrix4x4 Matrix => (Transform ?? new()).Matrix;
    public bool Includes(int index) => Mask is null || Mask[index];
}

public sealed record ScanProject(ScanObject[] Objects, Guid ActiveObjectId, ImportMetadata? Origin)
{
    public const int MaximumObjects = 32;
    public ScanObject? Active => Objects.FirstOrDefault(o => o.Id == ActiveObjectId);
    public int PointCount => Objects.Sum(o => o.PointCount);
    public void Validate(CancellationToken token = default)
    {
        if (Objects is null || Objects.Length > MaximumObjects || Objects.Any(o => o is null || o.Source is null) ||
            Objects.Select(o => o.Id).Distinct().Count() != Objects.Length || Objects.Any(o => o.Id == Guid.Empty) ||
            (Objects.Length > 0 ? Active is null : ActiveObjectId != Guid.Empty) ||
            Objects.Sum(o => (long)o.Source.Points.Length) > ImportedScan.MaximumPoints ||
            Objects.Sum(o => (long)o.Source.Triangles.Length / 3) > ImportedScan.MaximumTriangles)
            throw new InvalidDataException("Проект превышает лимиты или содержит некорректные объекты.");
        foreach (var o in Objects)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(o.Name) || o.Name.Length > 120 || o.Tint is > 0xffffff ||
                (o.Mask is not null && o.Mask.Length != o.Source.Points.Length)) throw new InvalidDataException("Некорректные свойства объекта.");
            o.Source.Validate(token); (o.Transform ?? new()).Validate();
        }
        if (Origin is { } origin)
            new ImportedScan { Metadata = origin, Points = [new(0, 0, 0, 1)] }.Validate(token);
    }
    public ScanProject Replace(ScanObject value) => this with { Objects = Objects.Select(o => o.Id == value.Id ? value : o).ToArray() };
    public ScanSession ToSession(UserAnnotation[]? annotations = null) => new()
    {
        ScenarioId = "imported", Settings = new(), Frames = [], Project = this,
        ImportedScan = Active?.Source, Annotations = annotations ?? []
    };
    public static ScanProject FromSession(ScanSession session)
    {
        if (session.Project is { } project) return project;
        var scan = session.ImportedScan ?? throw new InvalidDataException("В сеансе нет скана.");
        var id = Guid.NewGuid();
        var name = Path.GetFileNameWithoutExtension(scan.Metadata.FileName);
        if (string.IsNullOrWhiteSpace(name)) name = "Скан";
        if (name.Length > 120) name = name[..120];
        return new([new(id, name, scan)], id, scan.Metadata);
    }
}

public readonly record struct ScanBox(Vector3 Minimum, Vector3 Maximum)
{
    public bool Contains(Vector3 p) => p.X >= Minimum.X && p.X <= Maximum.X && p.Y >= Minimum.Y && p.Y <= Maximum.Y && p.Z >= Minimum.Z && p.Z <= Maximum.Z;
    public void Validate()
    {
        if (!InspectionPoint.From(Minimum).IsValid || !InspectionPoint.From(Maximum).IsValid || Minimum.X > Maximum.X || Minimum.Y > Maximum.Y || Minimum.Z > Maximum.Z)
            throw new InvalidDataException("Границы области должны быть конечными: минимум не больше максимума.");
    }
}
