using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HelixToolkit.Wpf.SharpDX;
using SubTerra.ViewModels;

namespace SubTerra.Navigation;

/// <summary>WPF input adapter. Mouse capture is always released on deactivation or exit.</summary>
public sealed class WalkController : IDisposable
{
    private readonly Window _window;
    private readonly Viewport3DX _viewport;
    private readonly MainViewModel _viewModel;
    private readonly HashSet<Key> _keys = [];
    private readonly Stopwatch _clock = new();
    private WalkNavigator? _navigator;
    private bool _captured;

    public WalkController(Window window, Viewport3DX viewport, MainViewModel viewModel)
    {
        _window = window; _viewport = viewport; _viewModel = viewModel;
        _viewport.PreviewMouseLeftButtonDown += MouseDown;
        _viewport.LostMouseCapture += LostCapture;
        _window.PreviewKeyUp += KeyUp;
        _window.Deactivated += Deactivated;
    }

    public void Start()
    {
        Stop();
        _navigator = _viewModel.CreateNavigator();
        _viewport.IsRotationEnabled = _viewport.IsPanEnabled = _viewport.IsZoomEnabled = false;
        _viewport.IsInertiaEnabled = false;
        _viewport.ShowViewCube = false;
        _viewModel.UpdateWalkPosition(_navigator);
        _clock.Restart();
        CompositionTarget.Rendering += Render;
    }

    public void Stop()
    {
        CompositionTarget.Rendering -= Render;
        ReleaseMouse();
        _navigator = null;
        _clock.Stop();
        _viewport.IsRotationEnabled = _viewport.IsPanEnabled = _viewport.IsZoomEnabled = true;
        _viewport.IsInertiaEnabled = false;
        _viewport.ShowViewCube = false;
    }
    public void PlaceAt(float progress)
    {
        ReleaseMouse();
        _navigator?.PlaceAt(progress);
        if (_navigator is not null) _viewModel.UpdateWalkPosition(_navigator);
    }

    public bool HandleKeyDown(KeyEventArgs e)
    {
        if (_navigator is null) return false;
        if (e.Key is Key.Escape or Key.Home)
        {
            _viewModel.Home();
            return true;
        }
        if (e.Key == Key.Tab)
        {
            if (!e.IsRepeat) { if (_captured) ReleaseMouse(); else CaptureMouse(); }
            return true;
        }
        if (e.Key is Key.W or Key.A or Key.S or Key.D or Key.Up or Key.Down or Key.Left or Key.Right
            or Key.LeftShift or Key.RightShift)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return false;
            if (_captured) _keys.Add(e.Key);
            return true;
        }
        return e.Key == Key.Space;
    }

    private void KeyUp(object sender, KeyEventArgs e) => _keys.Remove(e.Key);
    private void MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_navigator is null) return;
        CaptureMouse();
        e.Handled = true;
    }
    private void CaptureMouse()
    {
        if (_navigator is null || !_window.IsActive) return;
        _viewport.Focus();
        CenterCursor();
        _captured = Mouse.Capture(_viewport, CaptureMode.Element);
        _viewport.Cursor = _captured ? Cursors.None : Cursors.Arrow;
        _viewModel.MouseCaptured = _captured;
        _clock.Restart();
    }
    private void ReleaseMouse()
    {
        _captured = false;
        _keys.Clear();
        _viewport.Cursor = Cursors.Arrow;
        if (Mouse.Captured == _viewport) Mouse.Capture(null);
        _viewModel.MouseCaptured = false;
    }
    private void Deactivated(object? sender, EventArgs e) => ReleaseMouse();
    private void LostCapture(object sender, MouseEventArgs e)
    {
        if (_captured) ReleaseMouse();
    }
    private Point Center => new(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2);
    private void CenterCursor()
    {
        var screen = _viewport.PointToScreen(Center);
        SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
    }
    private void Render(object? sender, EventArgs e)
    {
        var dt = (float)_clock.Elapsed.TotalSeconds;
        _clock.Restart();
        if (!_captured || _navigator is null || !_window.IsActive) return;
        var mouse = Mouse.GetPosition(_viewport);
        var delta = mouse - Center;
        if (Math.Abs(delta.X) < _viewport.ActualWidth / 2 && Math.Abs(delta.Y) < _viewport.ActualHeight / 2)
            _navigator.Look((float)-delta.X * .0025f, (float)-delta.Y * .0025f);
        if (delta.LengthSquared > .01) CenterCursor();
        var forward = (_keys.Contains(Key.W) || _keys.Contains(Key.Up) ? 1 : 0)
                    - (_keys.Contains(Key.S) || _keys.Contains(Key.Down) ? 1 : 0);
        var right = (_keys.Contains(Key.D) || _keys.Contains(Key.Right) ? 1 : 0)
                  - (_keys.Contains(Key.A) || _keys.Contains(Key.Left) ? 1 : 0);
        _navigator.Move(forward, right, dt, _keys.Contains(Key.LeftShift) || _keys.Contains(Key.RightShift));
        _viewModel.UpdateWalkPosition(_navigator);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    public void Dispose()
    {
        Stop();
        _viewport.PreviewMouseLeftButtonDown -= MouseDown;
        _viewport.LostMouseCapture -= LostCapture;
        _window.PreviewKeyUp -= KeyUp;
        _window.Deactivated -= Deactivated;
    }
}
