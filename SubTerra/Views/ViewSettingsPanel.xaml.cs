using System.Windows;
using System.Windows.Controls;

namespace SubTerra.Views;

public partial class ViewSettingsPanel : UserControl
{
    public ViewSettingsPanel() => InitializeComponent();
    public event RoutedEventHandler? ExportReconstructionRequested;
    private void ExportReconstruction_Click(object sender, RoutedEventArgs e) => ExportReconstructionRequested?.Invoke(this, e);
}
