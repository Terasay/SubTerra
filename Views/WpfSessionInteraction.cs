using System.Windows;
using Microsoft.Win32;
using SubTerra.Services;

namespace SubTerra.Views;

public sealed class WpfSessionInteraction(Func<Window> owner) : ISessionInteraction
{
    public Task<UnsavedDecision> ConfirmUnsavedAsync(string action, bool canSave)
    {
        var dialog = new UnsavedSessionWindow(action, canSave) { Owner = owner() };
        dialog.ShowDialog();
        return Task.FromResult(dialog.Decision);
    }
    public Task<string?> ChooseSavePathAsync()
    {
        var dialog = new SaveFileDialog { Title = "Сохранить сеанс", Filter = "Сеанс Subterra (*.subterra)|*.subterra",
            DefaultExt = ".subterra", FileName = $"Subterra_{DateTime.Now:yyyy-MM-dd_HH-mm}" };
        return Task.FromResult(dialog.ShowDialog(owner()) == true ? dialog.FileName : null);
    }
}
