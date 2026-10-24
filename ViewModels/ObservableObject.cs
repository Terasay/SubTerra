using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SubTerra.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

public sealed record SessionEvent(string Time, string Message);

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onError) : ICommand
{
    private bool _running;
    public bool CanExecute(object? parameter) => !_running && canExecute();
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try { await execute(); }
        catch (Exception ex) { onError(ex); }
        finally { _running = false; CommandManager.InvalidateRequerySuggested(); }
    }
    public async void Execute(object? parameter) => await ExecuteAsync();
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
