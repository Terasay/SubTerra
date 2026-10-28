using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SubTerra.Views;

public partial class SessionInspectorPanel : UserControl
{
    public SessionInspectorPanel() => InitializeComponent();
    public event RoutedEventHandler? SaveAsRequested;
    public event RoutedEventHandler? ExportRequested;
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAsRequested?.Invoke(this, e);
    private void Export_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, e);
    private void NumericInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox input) return;
        input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); e.Handled = true;
    }
}
