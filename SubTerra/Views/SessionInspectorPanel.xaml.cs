using System.Windows;
using System.Windows.Controls;

namespace SubTerra.Views;

public partial class SessionInspectorPanel : UserControl
{
    public SessionInspectorPanel() => InitializeComponent();
    public event RoutedEventHandler? SaveAsRequested;
    public event RoutedEventHandler? ExportRequested;
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAsRequested?.Invoke(this, e);
    private void Export_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, e);
}
