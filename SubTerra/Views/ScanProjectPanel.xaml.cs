using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SubTerra.ViewModels;

namespace SubTerra.Views;

public partial class ScanProjectPanel : UserControl
{
    public ScanProjectPanel() => InitializeComponent();
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanManageProject) return;
        var owner = Window.GetWindow(this);
        var file = new OpenFileDialog { Title = "Добавить скан в проект", Filter = "Сканы PLY / ASC / XYZ / TXT|*.ply;*.asc;*.xyz;*.txt", CheckFileExists = true };
        if (file.ShowDialog(owner) != true) return;
        var options = new ScanImportWindow(file.FileName) { Owner = owner };
        if (options.ShowDialog() == true) await vm.ImportAsync(file.FileName, options.Options, addToProject: true);
    }
}
