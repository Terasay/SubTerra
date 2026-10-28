using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HelixToolkit.Wpf.SharpDX;
using SubTerra.Services;
using SubTerra.ViewModels;

namespace SubTerra;

public partial class MainWindow
{
    private bool _drawingSelection;
    private Point _selectionStart;
    private Matrix4x4 _selectionProjection;
    private ScanSelectionTool _drawingTool;
    private float _drawingRadius;
    private readonly List<Vector2> _selectionStroke = [];
    private Rectangle? _selectionRectangle;
    private void InitializeScreenSelection()
    {
        Viewport.PreviewMouseLeftButtonDown += Selection_MouseDown;
        Viewport.PreviewMouseMove += Selection_MouseMove;
        Viewport.PreviewMouseLeftButtonUp += Selection_MouseUp;
        Viewport.LostMouseCapture += (_, _) => { if (_drawingSelection) ClearScreenStroke(); };
        Viewport.PreviewMouseWheel += (_, e) => { if (_viewModel.IsScreenSelecting) e.Handled = true; };
        Viewport.SizeChanged += (_, _) => { if (_drawingSelection) ClearScreenStroke(); };
        Deactivated += (_, _) => ClearScreenStroke();
    }
    private void Selection_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsScreenSelecting) return;
        e.Handled = true;
        ClearScreenStroke();
        var matrix = Viewport.GetViewProjectionMatrix3D();
        _selectionProjection = new((float)matrix.M11, (float)matrix.M12, (float)matrix.M13, (float)matrix.M14,
            (float)matrix.M21, (float)matrix.M22, (float)matrix.M23, (float)matrix.M24,
            (float)matrix.M31, (float)matrix.M32, (float)matrix.M33, (float)matrix.M34,
            (float)matrix.OffsetX, (float)matrix.OffsetY, (float)matrix.OffsetZ, (float)matrix.M44);
        _selectionStart = e.GetPosition(Viewport); _drawingTool = _viewModel.SelectionTool;
        _drawingRadius = (float)_viewModel.BrushRadius; _drawingSelection = true;
        Viewport.Focus();
        if (!Viewport.CaptureMouse()) { ClearScreenStroke(); return; }
        if (_drawingTool == ScanSelectionTool.Brush) AddBrushPoint(_selectionStart);
        else
        {
            _selectionRectangle = new() { Stroke = new SolidColorBrush(Color.FromRgb(153, 225, 173)), Fill = new SolidColorBrush(Color.FromArgb(48, 153, 225, 173)), StrokeThickness = 1.5 };
            SelectionCanvas.Children.Add(_selectionRectangle); UpdateSelectionRectangle(_selectionStart);
        }
    }
    private void Selection_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawingSelection) return;
        e.Handled = true;
        if (!_viewModel.IsScreenSelecting) { ClearScreenStroke(); return; }
        var point = e.GetPosition(Viewport);
        if (_drawingTool == ScanSelectionTool.Brush) AddBrushPoint(point); else UpdateSelectionRectangle(point);
    }
    private async void Selection_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawingSelection) return;
        e.Handled = true;
        if (!_viewModel.IsScreenSelecting) { ClearScreenStroke(); return; }
        var point = e.GetPosition(Viewport);
        if (_drawingTool == ScanSelectionTool.Brush) AddBrushPoint(point);
        var min = new Vector2((float)Math.Min(point.X, _selectionStart.X), (float)Math.Min(point.Y, _selectionStart.Y));
        var max = new Vector2((float)Math.Max(point.X, _selectionStart.X), (float)Math.Max(point.Y, _selectionStart.Y));
        if (_drawingTool == ScanSelectionTool.Rectangle && (max.X - min.X < 3 || max.Y - min.Y < 3)) { ClearScreenStroke(); return; }
        var selection = new ScreenScanSelection(_selectionProjection, (float)Viewport.ActualWidth, (float)Viewport.ActualHeight, min, max,
            _drawingTool == ScanSelectionTool.Brush ? _selectionStroke.ToArray() : null, _drawingRadius);
        ClearScreenStroke();
        await _viewModel.PreviewScreenSelectionAsync(selection);
    }
    private void UpdateSelectionRectangle(Point point)
    {
        if (_selectionRectangle is null) return;
        System.Windows.Controls.Canvas.SetLeft(_selectionRectangle, Math.Min(point.X, _selectionStart.X));
        System.Windows.Controls.Canvas.SetTop(_selectionRectangle, Math.Min(point.Y, _selectionStart.Y));
        _selectionRectangle.Width = Math.Abs(point.X - _selectionStart.X); _selectionRectangle.Height = Math.Abs(point.Y - _selectionStart.Y);
    }
    private void AddBrushPoint(Point point)
    {
        var p = new Vector2((float)point.X, (float)point.Y);
        if (_selectionStroke.Count >= 8192 || (_selectionStroke.Count > 0 && Vector2.DistanceSquared(_selectionStroke[^1], p) < _drawingRadius * _drawingRadius / 16)) return;
        // Interpolate fast pointer motion so the painted stroke has no gaps.
        if (_selectionStroke.Count > 0)
        {
            var last = _selectionStroke[^1]; var steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(last, p) / Math.Max(2, _drawingRadius / 2)));
            for (var i = 1; i < steps && _selectionStroke.Count < 8191; i++) _selectionStroke.Add(Vector2.Lerp(last, p, i / (float)steps));
        }
        _selectionStroke.Add(p);
        var circle = new Ellipse { Width = _drawingRadius * 2, Height = _drawingRadius * 2, Fill = new SolidColorBrush(Color.FromArgb(70, 153, 225, 173)), IsHitTestVisible = false };
        System.Windows.Controls.Canvas.SetLeft(circle, point.X - _drawingRadius); System.Windows.Controls.Canvas.SetTop(circle, point.Y - _drawingRadius);
        SelectionCanvas.Children.Add(circle);
    }
    private void ClearScreenStroke()
    {
        var ownedCapture = _drawingSelection;
        _drawingSelection = false; _selectionStroke.Clear(); _selectionRectangle = null;
        SelectionCanvas.Children.Clear();
        if (ownedCapture && Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
    }
}
