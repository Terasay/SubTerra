using System.IO;
using System.Windows;
using SubTerra.Models;
using SubTerra.Services;

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
        else
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (TextOptions.Visibility == Visibility.Visible) PreviewText(stream);
                else ApplyPlyPreset(stream);
            }
            // File errors are reported by the importer, which opens the file again on confirmation.
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void ApplyPlyPreset(Stream stream)
    {
        var bytes = new byte[65536]; // Maximum header accepted by the PLY reader.
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        var lines = System.Text.Encoding.ASCII.GetString(bytes, 0, count).Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line == "end_header") break;
            if (line is "comment Created with Scaniverse - https://scaniverse.com" or
                "comment Subterra scan; units meters; Z up; local coordinates" or "comment Subterra scan; units meters; Z up")
            {
                Units.SelectedIndex = (int)ScanUnit.Meters;
                break;
            }
        }
    }

    private void PreviewText(Stream stream)
    {
        // Inspect only the prefix even if an export starts with a large block of comments.
        using var reader = new StreamReader(stream);
        var buffer = new char[16 * 1024];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        var prefix = new string(buffer, 0, count);
        if (count == buffer.Length && reader.Peek() != -1)
            prefix = prefix[..(prefix.LastIndexOf('\n') + 1)];
        foreach (var line in prefix.Split('\n'))
        {
            var fields = ScanImporter.TextFields(line);
            if (fields is null) continue;
            SampleRow.Text = $"Первая строка данных ({fields.Length} ст.): {line.Trim()}";
            SampleRow.ToolTip = SampleRow.Text;
            void UpdatePreview()
            {
                try
                {
                    var point = ScanImporter.ParseTextPoint(fields, Options.TextLayout);
                    SampleInterpretation.Text = point.Color is { } color ? $"Цвет первой точки: #{color:X6}." :
                        fields.Length > 3 ? "Будут прочитаны только XYZ. Чтобы сохранить RGB, выберите соответствующие столбцы." :
                        "Будут прочитаны координаты XYZ.";
                    ImportButton.IsEnabled = true;
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException)
                {
                    SampleInterpretation.Text = "Первая точка: " + ex.Message;
                    ImportButton.IsEnabled = false;
                }
            }
            Columns.SelectionChanged += (_, _) => UpdatePreview();
            UpdatePreview();
            break;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
