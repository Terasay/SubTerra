using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace SubTerra.Services;

public interface IAccessSession
{
    bool IsAdministrator { get; }
    void Logout();
    event EventHandler? AccessChanged;
}

public sealed record LoginResult(bool Success, string Message);

/// <summary>Local application access. Credentials belong to the current Windows profile.</summary>
public sealed class AdminAccessService : IAccessSession
{
    private const int Iterations = 600_000;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private sealed record Account(int Version, string Login, int Iterations, byte[] Salt, byte[] Hash,
        int Failures = 0, DateTimeOffset? LockedUntil = null);
    public bool IsAdministrator { get; private set; }
    public bool IsConfigured => File.Exists(_path);
    public event EventHandler? AccessChanged;

    public AdminAccessService(string? path = null, TimeProvider? time = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Subterra", "admin-account.json");
        _time = time ?? TimeProvider.System;
    }

    public async Task<LoginResult> CreateAdministratorAsync(string password)
    {
        if (password.Length is < 10 or > 128 || string.IsNullOrWhiteSpace(password))
            return new(false, "Пароль должен содержать от 10 до 128 символов.");
        await _gate.WaitAsync();
        try
        {
            if (IsConfigured) return new(false, "Администратор уже настроен. Войдите с существующим паролем.");
            var account = await Task.Run(() =>
            {
                var salt = RandomNumberGenerator.GetBytes(16);
                return new Account(1, "admin", Iterations, salt, Hash(password, salt));
            });
            await WriteAsync(account, overwrite: false);
            IsAdministrator = true;
            AccessChanged?.Invoke(this, EventArgs.Empty);
            return new(true, "Администратор создан.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { return new(false, "Не удалось сохранить учётную запись. Проверьте доступ к папке профиля."); }
        finally { _gate.Release(); }
    }

    public async Task<LoginResult> LoginAsync(string login, string password)
    {
        if (password.Length > 128) return new(false, "Неверный логин или пароль.");
        await _gate.WaitAsync();
        try
        {
            if (!IsConfigured) return new(false, "Сначала создайте администратора.");
            if (new FileInfo(_path).Length > 4096) throw new InvalidDataException();
            var account = JsonSerializer.Deserialize<Account>(await File.ReadAllTextAsync(_path));
            if (account is null || account.Version != 1 || account.Login != "admin" || account.Iterations != Iterations ||
                account.Salt?.Length != 16 || account.Hash?.Length != 32 || account.Failures is < 0 or > 5)
                throw new InvalidDataException();
            var now = _time.GetUtcNow();
            if (account.LockedUntil > now)
                return new(false, $"Повторите вход через {Math.Ceiling((account.LockedUntil.Value - now).TotalSeconds):F0} с.");
            var candidate = await Task.Run(() => Hash(password, account.Salt));
            bool matches;
            try { matches = CryptographicOperations.FixedTimeEquals(candidate, account.Hash) & (login.Trim() == "admin"); }
            finally { CryptographicOperations.ZeroMemory(candidate); }
            if (!matches)
            {
                var failures = account.LockedUntil is not null ? 1 : account.Failures + 1;
                await WriteAsync(account with { Failures = failures, LockedUntil = failures >= 5 ? now.AddSeconds(30) : null });
                return new(false, failures >= 5 ? "Слишком много попыток. Повторите вход через 30 с." : "Неверный логин или пароль.");
            }
            await WriteAsync(account with { Failures = 0, LockedUntil = null });
            IsAdministrator = true;
            AccessChanged?.Invoke(this, EventArgs.Empty);
            return new(true, "Вход выполнен.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException)
        { return new(false, "Не удалось прочитать учётную запись. Доступ администратора закрыт."); }
        finally { _gate.Release(); }
    }

    public void Logout()
    {
        IsAdministrator = false;
        AccessChanged?.Invoke(this, EventArgs.Empty);
    }

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    private async Task WriteAsync(Account account, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(account));
            File.Move(temporary, _path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
