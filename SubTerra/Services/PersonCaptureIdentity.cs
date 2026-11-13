using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SubTerra.Models;

namespace SubTerra.Services;

public static class PersonCaptureIdentity
{
    private sealed record Identity(string Value);
    private static readonly ConditionalWeakTable<ImportedScan, Identity> Cache = new();

    public static string[] Capture(ScanProject project, CancellationToken token = default) => project.Objects
        .Where(o => o.IsVisible && o.PointCount > 0).Select(o => Cache.GetValue(o.Source, s => new(Create(s, token))).Value).Distinct().ToArray();

    private static string Create(ImportedScan scan, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(scan.Metadata.CaptureId)) return Hash("capture:" + scan.Metadata.CaptureId.Trim());
        // Scaniverse uses the acquisition timestamp in both its PLY and GLB export names.
        var match = Regex.Match(Path.GetFileName(scan.Metadata.FileName), @"^Scaniverse (\d{4}-\d{2}-\d{2} \d{6})(?:\D|$)", RegexOptions.CultureInvariant);
        if (match.Success) return Hash("scaniverse:" + match.Groups[1].Value);
        var points = scan.Points.Select(p => p.Position).ToArray();
        token.ThrowIfCancellationRequested();
        Array.Sort(points, (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[12];
        foreach (var point in points)
        {
            token.ThrowIfCancellationRequested();
            BitConverter.TryWriteBytes(bytes, point.X + 0f);
            BitConverter.TryWriteBytes(bytes[4..], point.Y + 0f);
            BitConverter.TryWriteBytes(bytes[8..], point.Z + 0f);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
