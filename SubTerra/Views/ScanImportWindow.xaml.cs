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
        var glb = Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);
        TextOptions.Visibility = glb || Path.GetExtension(path).Equals(".ply", StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
        if (glb)
        {
            Units.SelectedIndex = (int)ScanUnit.Meters; UpAxis.SelectedIndex = (int)ScanUpAxis.Y;
            Units.IsEnabled = UpAxis.IsEnabled = false;
            UnitsHint.Text = "В GLB единицы заданы стандартом: метры, Y вверх. Модель автоматически поворачивается в Z вверх. Материалы и встроенные PNG/JPEG-текстуры сохраняются в проекте.";
        }
        // Recognize documented source headers, including SubTerra's own meter-based exports.
        // Units and vertical axis remain visible/editable and must be checked by the operator.
        if (Path.GetExtension(path).Equals(".ply", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var bytes = new byte[8192]; var count = stream.Read(bytes, 0, bytes.Length);
                var header = System.Text.Encoding.ASCII.GetString(bytes, 0, count);
                var end = header.IndexOf("end_header", StringComparison.Ordinal);
                if (end >= 0 && header[..end].Split('\n').Any(line => line.TrimEnd('\r') is
                    "comment Created with Scaniverse - https://scaniverse.com" or
                    "comment Subterra scan; units meters; Z up; local coordinates" or
                    "comment Subterra scan; units meters; Z up"))
                    Units.SelectedIndex = (int)ScanUnit.Meters;
            }
            catch (IOException) { } // The importer reports file errors; the optional preset must not prevent opening its dialog.
            catch (UnauthorizedAccessException) { }
        }
    }
    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
