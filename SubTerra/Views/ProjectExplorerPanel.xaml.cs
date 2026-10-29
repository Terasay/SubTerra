using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SubTerra.ViewModels;

namespace SubTerra.Views;

public partial class ProjectExplorerPanel : UserControl
{
    public ProjectExplorerPanel() => InitializeComponent();
    private void Object_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.NewValue is ScanObjectItem scan) { vm.SelectedScanObject = scan; vm.Inspect("Скан"); }
        else if (e.NewValue is SubTerra.Models.StructureSurface surface) { vm.SelectedStructureSurface = surface; vm.Inspect("Поверхность"); }
        else if (e.NewValue is SubTerra.Models.SavedRoom room) { vm.SelectedRoomId = room.Id; vm.Inspect("Комната"); }
        else if (e.NewValue is SubTerra.Models.StructureObject item) { vm.SelectedObjectCandidate = item; vm.Inspect("Предмет"); }
    }
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanManageProject) return;
        var owner = Window.GetWindow(this);
        var file = new OpenFileDialog { Title = "Добавить скан в проект", Filter = "Сканы PLY / ASC / XYZ / TXT|*.ply;*.asc;*.xyz;*.txt", CheckFileExists = true };
        if (file.ShowDialog(owner) != true) return;
        var options = new ScanImportWindow(file.FileName) { Owner = owner };
        if (options.ShowDialog() == true)
        {
            await vm.ImportAsync(file.FileName, options.Options, addToProject: true);
            vm.Inspect("Скан");
        }
    }
}
