using System.IO;
using System.IO.Compression;
using System.Text.Json;
using SubTerra.Models;

namespace SubTerra.Services;

public sealed partial class SessionStorage
{
    private const string StructureEntry = "structure/analysis.json";
    private const int MaximumStructureBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions StructureJson = new()
    { IncludeFields = true, IgnoreReadOnlyProperties = true, MaxDepth = 16 };

    private static byte[]? PrepareStructure(ScanProject project, CancellationToken token)
    {
        if (project.Structure is not { } document) return null;
        StructureProvenance.Validate(document with { IsStale = true }, project, token);
        document = StructureProvenance.Refresh(document, project, token);
        StructureProvenance.Validate(document, project, token);
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, StructureJson);
        if (bytes.Length > MaximumStructureBytes) throw new InvalidDataException("Анализ структуры превышает допустимый размер.");
        return bytes;
    }
    private static StructureAnalysisDocument ReadStructure(ZipArchive archive, ScanProject project, CancellationToken token)
    {
        var entries = archive.Entries.Where(e => e.FullName == StructureEntry).ToArray();
        if (entries.Length != 1 || entries[0].Length is < 1 or > MaximumStructureBytes)
            throw new InvalidDataException("Нет анализа структуры или его размер некорректен.");
        using var stream = entries[0].Open();
        var bytes = new byte[(int)entries[0].Length]; stream.ReadExactly(bytes);
        token.ThrowIfCancellationRequested();
        StructureAnalysisDocument document;
        try { document = JsonSerializer.Deserialize<StructureAnalysisDocument>(bytes, StructureJson) ?? throw new InvalidDataException("Нет данных анализа."); }
        catch (JsonException ex) { throw new InvalidDataException("Повреждено описание анализа структуры.", ex); }
        // Validate before inspecting provenance: malformed input stamps must never be used as keys.
        StructureProvenance.Validate(document with { IsStale = true }, project, token);
        document = StructureProvenance.Refresh(document, project, token);
        StructureProvenance.Validate(document, project, token);
        return document;
    }
}
