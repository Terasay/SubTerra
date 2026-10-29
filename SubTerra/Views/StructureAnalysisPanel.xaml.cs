using System.Windows.Controls;
using SubTerra.ViewModels;

namespace SubTerra.Views;

public partial class StructureAnalysisPanel : UserControl
{
    public StructureAnalysisPanel() => InitializeComponent();
    private void Surface_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsVisible && DataContext is MainViewModel { IsAnalysisSection: true, SelectedStructureSurface: not null } vm)
            vm.Inspect("Поверхность");
    }
    private void Object_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsVisible && DataContext is MainViewModel { IsAnalysisSection: true, SelectedObjectCandidate: not null } vm)
            vm.Inspect("Предмет");
    }
}
