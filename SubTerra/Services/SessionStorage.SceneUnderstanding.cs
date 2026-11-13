using System.IO;
using System.IO.Compression;
using System.Text.Json;
using SubTerra.Models;

namespace SubTerra.Services;

public sealed partial class SessionStorage
{
    private const string SceneUnderstandingEntry = "scene-ai/labels.json";
    private const int MaximumSceneUnderstandingBytes = 32 * 1024 * 1024;

    private static byte[]? PrepareSceneUnderstanding(ScanProject project)
    {
        if (project.SceneUnderstanding is not { } document) return null;
        SceneUnderstandingService.Validate(project, document with { IsStale = true });
        document = SceneUnderstandingService.Refresh(project, document);
        SceneUnderstandingService.Validate(project, document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        if (bytes.Length > MaximumSceneUnderstandingBytes) throw new InvalidDataException("Разметка сцены превышает допустимый размер.");
        return bytes;
    }

    private static SceneUnderstandingDocument ReadSceneUnderstanding(ZipArchive archive, ScanProject project)
    {
        var entries = archive.Entries.Where(e => e.FullName == SceneUnderstandingEntry).ToArray();
        if (entries.Length != 1 || entries[0].Length is < 1 or > MaximumSceneUnderstandingBytes)
            throw new InvalidDataException("Нет разметки сцены или её размер некорректен.");
        using var stream = entries[0].Open();
        var bytes = new byte[(int)entries[0].Length]; stream.ReadExactly(bytes);
        var document = JsonSerializer.Deserialize<SceneUnderstandingDocument>(bytes) ?? throw new InvalidDataException("Нет данных разметки сцены.");
        SceneUnderstandingService.Validate(project, document with { IsStale = true });
        document = SceneUnderstandingService.Refresh(project, document);
        SceneUnderstandingService.Validate(project, document);
        return document;
    }
}
