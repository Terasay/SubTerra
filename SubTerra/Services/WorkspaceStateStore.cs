using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using SubTerra.Models;

namespace SubTerra.Services;

/// <summary>Small local preferences and an independent, atomic recovery copy.</summary>
public sealed class WorkspaceStateStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private sealed record RecoveryInfo(string? OriginalPath, DateTimeOffset SavedUtc, string ArchiveHash);
    public string RecoveryPath => Path.Combine(_directory, "recovery.subterra");
    public bool HasRecovery => File.Exists(RecoveryPath);
    public string PreferencesPath => Path.Combine(_directory, "workspace.json");

    public WorkspaceStateStore(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Subterra");

    public WorkspacePreferences LoadPreferences()
    {
        try
        {
            if (!File.Exists(PreferencesPath) || new FileInfo(PreferencesPath).Length > 65_536) return new();
            return (JsonSerializer.Deserialize<WorkspacePreferences>(File.ReadAllText(PreferencesPath)) ?? new()).Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }

    public async Task SavePreferencesAsync(WorkspacePreferences preferences, CancellationToken cancellationToken = default)
    {
        await _settingsGate.WaitAsync(cancellationToken);
        try { await WriteJsonAsync(PreferencesPath, preferences.Normalize(), cancellationToken); }
        finally { _settingsGate.Release(); }
    }

    public async Task SaveRecoveryAsync(SessionStorage storage, ScanSession session, string? originalPath)
    {
        await _recoveryGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_directory);
            await storage.SaveAsync(RecoveryPath, session, session.Frames.Length);
            var hash = RecoveryHash();
            await WriteJsonAsync(Path.Combine(_directory, "recovery.json"), new RecoveryInfo(originalPath, DateTimeOffset.UtcNow, hash), CancellationToken.None);
        }
        finally { _recoveryGate.Release(); }
    }

    public string? RecoveryOriginalPath()
    {
        try
        {
            var path = Path.Combine(_directory, "recovery.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 8192) return null;
            var info = JsonSerializer.Deserialize<RecoveryInfo>(File.ReadAllText(path));
            if (info?.ArchiveHash != RecoveryHash() || string.IsNullOrWhiteSpace(info.OriginalPath) || info.OriginalPath.Length > 1024) return null;
            return Path.GetFullPath(info.OriginalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
    private string RecoveryHash()
    {
        using var stream = File.OpenRead(RecoveryPath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public async Task ClearRecoveryAsync()
    {
        await _recoveryGate.WaitAsync();
        try
        {
            File.Delete(RecoveryPath);
            File.Delete(Path.Combine(_directory, "recovery.json"));
        }
        finally { _recoveryGate.Release(); }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
