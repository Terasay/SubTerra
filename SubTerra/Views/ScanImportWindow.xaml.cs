using System.IO;
using System.Windows;
using SubTerra.Models;

namespace SubTerra.Views;

public partial class ScanImportWindow : Window
{
    private const string ScanPatterns = "*.glb;*.ply;*.asc;*.xyz;*.txt";
    internal const string ScanFilter = "3D-сканы и модели|" + ScanPatterns +
        "|Модель GLB с текстурами|*.glb|Сканы PLY / ASC / XYZ / TXT|*.ply;*.asc;*.xyz;*.txt";
    internal const string ProjectOrScanFilter = "Проекты и 3D-сканы|*.subterra;" + ScanPatterns +
        "|Проект SubTerra|*.subterra|" + ScanFilter;

    public ScanImportOptions Options => new((ScanUnit)Units.SelectedIndex, (ScanUpAxis)UpAxis.SelectedIndex,
        LocalOrigin.IsChecked == true, (TextPointLayout)Columns.SelectedIndex);
    public ScanImportWindow(string path, bool addToProject = false)
    {
        InitializeComponent();
        Title = addToProject ? "Добавить скан в проект" : "Новый проект из скана";
        ImportButton.Content = addToProject ? "Добавить в проект" : "Создать проект";
        FileLabel.Text = Path.GetFileName(path);
        FileLabel.ToolTip = path;
        var glb = Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);
        TextOptions.Visibility = glb || Path.GetExtension(path).Equals(".ply", StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
        if (glb)
        {
            Units.SelectedIndex = (int)ScanUnit.Meters; UpAxis.SelectedIndex = (int)ScanUpAxis.Y;
            Units.IsEnabled = UpAxis.IsEnabled = false;
            CoordinateOptions.Visibility = Visibility.Collapsed;
            FormatTitle.Text = "GLB · модель с материалами";
            FormatHint.Text = "Масштаб задан в метрах. SubTerra повернёт модель из Y вверх в Z вверх и загрузит встроенные текстуры.";
            LimitsHint.Text = "Поддерживаются статические сетки до 5 млн вершин и треугольников со встроенными PNG/JPEG. Для импорта нужен GLB без Draco-сжатия.";
            MinHeight = 360;
            Height = 460;
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
