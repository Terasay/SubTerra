namespace SubTerra.Services;

public enum UnsavedDecision { Save, Discard, Cancel }

public interface ISessionInteraction
{
    Task<UnsavedDecision> ConfirmUnsavedAsync(string action, bool canSave);
    Task<string?> ChooseSavePathAsync();
}

/// <summary>Headless callers must explicitly provide an interaction to discard unsaved work.</summary>
public sealed class CancelSessionInteraction : ISessionInteraction
{
    public Task<UnsavedDecision> ConfirmUnsavedAsync(string action, bool canSave) => Task.FromResult(UnsavedDecision.Cancel);
    public Task<string?> ChooseSavePathAsync() => Task.FromResult<string?>(null);
}
