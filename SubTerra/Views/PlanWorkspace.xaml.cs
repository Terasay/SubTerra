using System.Windows;
using System.Windows.Controls;
using SubTerra.ViewModels;

namespace SubTerra.Views;

public partial class PlanWorkspace : UserControl
{
    public PlanWorkspace() => InitializeComponent();
    private void Fit_Click(object sender, RoutedEventArgs e) => PlanCanvas.Fit();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => PlanCanvas.Zoom(1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => PlanCanvas.Zoom(1 / 1.2);
    private void Plan_SelectionChosen(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.IsRoomEditing = true; vm.Inspect("Комната");
    }
}
