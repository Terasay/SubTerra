using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SubTerra.Models;

namespace SubTerra.Views;

/// <summary>A metric, Z-up plan viewer. All strokes and labels remain constant in screen space.</summary>
public sealed class RoomPlanCanvas : FrameworkElement
{
    public static readonly DependencyProperty PlanProperty = DependencyProperty.Register(nameof(Plan), typeof(RoomPlan), typeof(RoomPlanCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((RoomPlanCanvas)d).Fit()));
    private static readonly DependencyPropertyKey SelectionTextPropertyKey = DependencyProperty.RegisterReadOnly(nameof(SelectionText), typeof(string), typeof(RoomPlanCanvas),
        new PropertyMetadata("Выберите вершину · координаты в метрах"));
    public static readonly DependencyProperty SelectionTextProperty = SelectionTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty SelectedVertexIndexProperty = DependencyProperty.Register(nameof(SelectedVertexIndex), typeof(int), typeof(RoomPlanCanvas),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((RoomPlanCanvas)d).RefreshSelection()));
    public RoomPlan? Plan { get => (RoomPlan?)GetValue(PlanProperty); set => SetValue(PlanProperty, value); }
    public int SelectedVertexIndex { get => (int)GetValue(SelectedVertexIndexProperty); set => SetCurrentValue(SelectedVertexIndexProperty, value); }
    public string SelectionText => (string)GetValue(SelectionTextProperty);
    public event EventHandler? SelectionChosen;
    private double _scale = 50;
    private Point _center, _dragStart, _dragCenter;
    private int _selectedVertex = -1;
    private bool _panning, _selectedEdge;
    public RoomPlanCanvas() { ClipToBounds = true; Focusable = true; Cursor = Cursors.Cross; SizeChanged += (_, _) => Fit(); }
    public void Fit()
    {
        RefreshSelection();
        if (Plan is { FloorContour.Length: >= 3 } plan)
        {
            var minX = plan.FloorContour.Min(p => p.X); var maxX = plan.FloorContour.Max(p => p.X);
            var minY = plan.FloorContour.Min(p => p.Y); var maxY = plan.FloorContour.Max(p => p.Y);
            _center = new((minX + (double)maxX) / 2, (minY + (double)maxY) / 2);
            _scale = Math.Clamp(Math.Min(Math.Max(40, ActualWidth - 144) / Math.Max(.01, maxX - minX),
                Math.Max(40, ActualHeight - 144) / Math.Max(.01, maxY - minY)), .0001, 100000);
        }
        InvalidateVisual();
    }
    public void Zoom(double factor) => ZoomAt(factor, new(ActualWidth / 2, ActualHeight / 2));
    private void ZoomAt(double factor, Point anchor)
    {
        var before = World(anchor);
        _scale = Math.Clamp(_scale * factor, .0001, 100000);
        var after = World(anchor);
        _center += before - after;
        InvalidateVisual();
    }
    private Point Screen(double x, double y) => new(ActualWidth / 2 + (x - _center.X) * _scale, ActualHeight / 2 - (y - _center.Y) * _scale);
    private Point World(Point p) => new(_center.X + (p.X - ActualWidth / 2) / _scale, _center.Y - (p.Y - ActualHeight / 2) / _scale);
    protected override void OnMouseWheel(MouseWheelEventArgs e) { ZoomAt(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(this)); e.Handled = true; }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _panning = true; _dragStart = e.GetPosition(this); _dragCenter = _center; CaptureMouse(); Cursor = Cursors.Hand; e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left && Plan is { } plan)
        {
            var mouse = e.GetPosition(this);
            var vertex = Enumerable.Range(0, plan.FloorContour.Length).FirstOrDefault(i =>
                (Screen(plan.FloorContour[i].X, plan.FloorContour[i].Y) - mouse).Length <= 14, -1);
            var edge = vertex < 0 ? Enumerable.Range(0, plan.FloorContour.Length).FirstOrDefault(i =>
            {
                var a = Screen(plan.FloorContour[i].X, plan.FloorContour[i].Y); var b = Screen(plan.FloorContour[(i + 1) % plan.FloorContour.Length].X, plan.FloorContour[(i + 1) % plan.FloorContour.Length].Y);
                var segment = b - a; var fraction = Math.Clamp(Vector.Multiply(mouse - a, segment) / Math.Max(.001, segment.LengthSquared), 0, 1);
                return (mouse - (a + segment * fraction)).Length <= 10;
            }, -1) : -1;
            if (vertex >= 0 || edge >= 0)
            {
                SelectedVertexIndex = vertex >= 0 ? vertex : edge; RefreshSelection(); _selectedEdge = edge >= 0;
                if (_selectedEdge) SetValue(SelectionTextPropertyKey, $"Ребро {_selectedVertex + 1} · проём задаётся от выбранной вершины");
                SelectionChosen?.Invoke(this, EventArgs.Empty);
            }
            InvalidateVisual(); e.Handled = true;
        }
    }
    private void RefreshSelection()
    {
        _selectedVertex = SelectedVertexIndex; _selectedEdge = false;
        SetValue(SelectionTextPropertyKey, Plan is not { } plan || _selectedVertex < 0 || _selectedVertex >= plan.FloorContour.Length ? "Выберите вершину или ребро · координаты в метрах" :
            $"Вершина {_selectedVertex + 1} · X {plan.FloorContour[_selectedVertex].X:F3} · Y {plan.FloorContour[_selectedVertex].Y:F3} · Z {plan.FloorContour[_selectedVertex].Z:F3} м");
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_panning) return;
        var delta = e.GetPosition(this) - _dragStart;
        _center = new(_dragCenter.X - delta.X / _scale, _dragCenter.Y + delta.Y / _scale); InvalidateVisual();
    }
    protected override void OnMouseUp(MouseButtonEventArgs e) { if (_panning) { _panning = false; ReleaseMouseCapture(); Cursor = Cursors.Cross; e.Handled = true; } }
    protected override void OnLostMouseCapture(MouseEventArgs e) { _panning = false; Cursor = Cursors.Cross; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brush("#0C161F"), null, new Rect(RenderSize));
        var step = GridStep(65 / _scale);
        var topLeft = World(new()); var bottomRight = World(new(ActualWidth, ActualHeight));
        var gridPen = new Pen(Brush("#1C2D39"), 1);
        for (var x = Math.Ceiling(topLeft.X / step) * step; x <= bottomRight.X; x += step)
            dc.DrawLine(gridPen, Screen(x, topLeft.Y), Screen(x, bottomRight.Y));
        for (var y = Math.Ceiling(bottomRight.Y / step) * step; y <= topLeft.Y; y += step)
            dc.DrawLine(gridPen, Screen(topLeft.X, y), Screen(bottomRight.X, y));
        if (Plan is not { FloorContour.Length: >= 3 } plan) return;
        var points = plan.FloorContour.Select(p => Screen(p.X, p.Y)).ToArray();
        var shape = new StreamGeometry();
        using (var context = shape.Open()) { context.BeginFigure(points[0], true, true); context.PolyLineTo(points.Skip(1).ToArray(), true, true); }
        dc.DrawGeometry(Brush("#18302F"), new Pen(Brush("#A1E3C5"), 2), shape);
        if (_selectedEdge && _selectedVertex >= 0 && _selectedVertex < points.Length)
            dc.DrawLine(new Pen(Brush("#FFD49B"), 4), points[_selectedVertex], points[(_selectedVertex + 1) % points.Length]);
        foreach (var gap in plan.Gaps)
        {
            var a = points[gap.EdgeIndex]; var b = points[(gap.EdgeIndex + 1) % points.Length];
            dc.DrawLine(new Pen(Brush(gap.IsConfirmedOpening ? "#9DEAFF" : "#FFB46B"), 5) { DashStyle = gap.IsConfirmedOpening ? DashStyles.Solid : DashStyles.Dash }, a + (b - a) * gap.Start, a + (b - a) * gap.End);
        }
        for (var i = 0; i < points.Length; i++)
        {
            var a = plan.FloorContour[i]; var b = plan.FloorContour[(i + 1) % points.Length];
            var distance = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
            var midpoint = points[i] + (points[(i + 1) % points.Length] - points[i]) * .5;
            if ((points[i] - points[(i + 1) % points.Length]).Length > 72) Label(dc, $"{distance:F2} м", midpoint + new Vector(8, -22), "#C4DAD3");
            dc.DrawEllipse(Brush(i == _selectedVertex ? "#FFD49B" : "#0C161F"), new Pen(Brush("#A1E3C5"), 2), points[i], 5, 5);
            Label(dc, (i + 1).ToString(CultureInfo.CurrentCulture), points[i] + new Vector(10, 8), "#B5C9D7");
        }
        Label(dc, plan.Name, new(20, 20), "#E6EEF5");
        Label(dc, plan.IsManuallyEdited ? "Контур исправлен оператором · север не задан" : "Геометрическая гипотеза · север не задан", new(20, 42), "#ACBFCD");
        var scalePixels = step * _scale;
        dc.DrawLine(new Pen(Brush("#ACBFCD"), 2), new(24, ActualHeight - 28), new(24 + scalePixels, ActualHeight - 28));
        Label(dc, $"{step:G3} м", new(24, ActualHeight - 52), "#ACBFCD");
        Label(dc, "Y ↑   X →", new(Math.Max(20, ActualWidth - 104), ActualHeight - 40), "#ACBFCD");
    }
    private static double GridStep(double target)
    {
        var unit = Math.Pow(10, Math.Floor(Math.Log10(target)));
        return (target / unit <= 1 ? 1 : target / unit <= 2 ? 2 : target / unit <= 5 ? 5 : 10) * unit;
    }
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private void Label(DrawingContext dc, string text, Point point, string color) => dc.DrawText(new FormattedText(text,
        CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
}
