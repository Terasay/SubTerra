using System.IO;
using System.Windows;
using SubTerra.Models;

namespace SubTerra.Views;

public partial class ScanImportWindow : Window
{
    public ScanImportOptions Options => new((ScanUnit)Units.SelectedIndex, (ScanUpAxis)UpAxis.SelectedIndex,
        LocalOrigin.IsChecked == true, (TextPointLayout)Columns.SelectedIndex);
    public ScanImportWindow(string path)
    {
        InitializeComponent();
        FileLabel.Text = Path.GetFileName(path);
        TextOptions.Visibility = Path.GetExtension(path).Equals(".ply", StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
