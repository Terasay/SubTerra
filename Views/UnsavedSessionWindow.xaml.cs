using System.Windows;
using SubTerra.Services;

namespace SubTerra.Views;

public partial class UnsavedSessionWindow : Window
{
    public UnsavedDecision Decision { get; private set; } = UnsavedDecision.Cancel;
    public UnsavedSessionWindow(string action, bool canSave)
    {
        InitializeComponent();
        MessageText.Text = $"В сеансе есть несохранённые изменения. Сохраните их, прежде чем {action}." +
            (canSave ? "" : " Для сохранения вернитесь в приложение и войдите администратором.");
        SaveButton.IsEnabled = canSave;
    }
    private void Save_Click(object sender, RoutedEventArgs e) { Decision = UnsavedDecision.Save; DialogResult = true; }
    private void Discard_Click(object sender, RoutedEventArgs e) { Decision = UnsavedDecision.Discard; DialogResult = true; }
}
