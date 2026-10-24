using SubTerra.Services;

namespace SubTerra.Tests;

internal sealed class TestAccess(bool administrator = true) : IAccessSession
{
    public bool IsAdministrator { get; private set; } = administrator;
    public event EventHandler? AccessChanged;
    public void Logout() { IsAdministrator = false; AccessChanged?.Invoke(this, EventArgs.Empty); }
}

internal sealed class TestInteraction : ISessionInteraction
{
    public UnsavedDecision Decision { get; set; } = UnsavedDecision.Discard;
    public string? SavePath { get; set; }
    public int Prompts { get; private set; }
    public Task<UnsavedDecision> ConfirmUnsavedAsync(string action, bool canSave)
    { Prompts++; return Task.FromResult(Decision); }
    public Task<string?> ChooseSavePathAsync() => Task.FromResult(SavePath);
}
