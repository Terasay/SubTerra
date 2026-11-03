using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SubTerra.Models;

namespace SubTerra.Views;

public partial class TunnelAnalysisPanel : UserControl
{
    public TunnelAnalysisPanel() => InitializeComponent();
    private async void ExportMesh_Click(object sender, RoutedEventArgs e) => await ExportMesh(false);
    private async void ExportCompletion_Click(object sender, RoutedEventArgs e) => await ExportMesh(true);
    private async Task ExportMesh(bool completionOnly)
    {
        if (DataContext is not ViewModels.MainViewModel vm || !vm.CanExportTunnelMesh) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Модель PLY (*.ply)|*.ply", DefaultExt = ".ply", FileName = completionOnly ? "tunnel-completion" : "tunnel-mesh" };
        if (dialog.ShowDialog() == true) await vm.ExportTunnelMeshAsync(dialog.FileName, completionOnly);
    }
    private async void ExportLabels_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm || !vm.CanExportTunnelLabels) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "XYZ-разметка CSV (*.csv)|*.csv", DefaultExt = ".csv", FileName = "tunnel-xyz-labels" };
        if (dialog.ShowDialog() == true) await vm.ExportTunnelLabelsAsync(dialog.FileName);
    }
}

/// <summary>Observed radial profile in the section's own metric coordinates; gaps remain open.</summary>
public sealed class TunnelSectionCanvas : FrameworkElement
{
    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(nameof(Section), typeof(TunnelSection), typeof(TunnelSectionCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumContourGapProperty = DependencyProperty.Register(nameof(MaximumContourGap), typeof(double), typeof(TunnelSectionCanvas),
        new FrameworkPropertyMetadata(.75, FrameworkPropertyMetadataOptions.AffectsRender));
    public TunnelSection? Section { get => (TunnelSection?)GetValue(SectionProperty); set => SetValue(SectionProperty, value); }
    public double MaximumContourGap { get => (double)GetValue(MaximumContourGapProperty); set => SetValue(MaximumContourGapProperty, value); }
    public TunnelSectionCanvas() { ClipToBounds = true; SizeChanged += (_, _) => InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        void Label(string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, Brushes.LightSteelBlue, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
        if (Section is not { Contour.Length: > 0 } section) { Label("Нет актуального профиля", 14, 14); return; }
        var minX = Math.Min(0, section.Contour.Min(p => p.X)); var maxX = Math.Max(0, section.Contour.Max(p => p.X));
        var minY = Math.Min(0, section.Contour.Min(p => p.Y)); var maxY = Math.Max(0, section.Contour.Max(p => p.Y));
        var scale = Math.Min(Math.Max(1, ActualWidth - 40) / Math.Max(.1, maxX - minX), Math.Max(1, ActualHeight - 52) / Math.Max(.1, maxY - minY));
        Point Screen(double x, double y) => new(ActualWidth / 2 + (x - (minX + maxX) / 2) * scale,
            (ActualHeight - 22) / 2 - (y - (minY + maxY) / 2) * scale);
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(43, 65, 79)), 1);
        var center = Screen(0, 0);
        dc.DrawLine(grid, new(12, center.Y), new(Math.Max(12, ActualWidth - 12), center.Y));
        dc.DrawLine(grid, new(center.X, 12), new(center.X, Math.Max(12, ActualHeight - 28)));
        var pen = new Pen(section.IsClosed ? Brushes.Aquamarine : Brushes.Gold, 2);
        for (var i = 0; i < section.Contour.Length; i++)
        {
            var a = section.Contour[i]; var start = Screen(a.X, a.Y);
            dc.DrawEllipse(pen.Brush, null, start, 1.7, 1.7);
            if (!section.ConnectsToNext(i, MaximumContourGap)) continue;
            var b = section.Contour[(i + 1) % section.Contour.Length]; dc.DrawLine(pen, start, Screen(b.X, b.Y));
        }
        dc.DrawEllipse(Brushes.LightSkyBlue, null, center, 3, 3);
        Label($"↔ {section.Width:F2} м   ↕ {section.Height:F2} м", 12, Math.Max(12, ActualHeight - 24));
    }
}
