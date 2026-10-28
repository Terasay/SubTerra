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
    private readonly Stopwatch _clock = new();
    private readonly MouseLookInput _mouseLook = new();
    private WalkNavigator? _navigator;
    private bool _captured;
    private TimeSpan _lastRenderTime;

    public WalkController(Window window, Viewport3DX viewport, MainViewModel viewModel)
    {
        _window = window; _viewport = viewport; _viewModel = viewModel;
        // Helix handles Q/Z (and WASD) outside InputBindings, even with inertia off.
        // Walking is owned exclusively by WalkNavigator; overview uses mouse pan.
        _viewport.IsMoveEnabled = false;
        _viewport.PreviewMouseLeftButtonDown += MouseDown;
        _viewport.LostMouseCapture += LostCapture;
        _window.Deactivated += Deactivated;
    }

    public void Start()
    {
        Stop();
        _navigator = _viewModel.CreateNavigator();
        _viewport.IsRotationEnabled = _viewport.IsPanEnabled = _viewport.IsZoomEnabled = false;
        _viewport.IsChangeFieldOfViewEnabled = false;
        _viewport.IsInertiaEnabled = false;
        _viewport.ShowViewCube = false;
        _viewModel.UpdateWalkPosition(_navigator);
        _clock.Restart();
        _lastRenderTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += Render;
    }

    public void Stop()
    {
        CompositionTarget.Rendering -= Render;
        ReleaseMouse();
        _navigator = null;
        _clock.Stop();
        _viewport.IsRotationEnabled = _viewport.IsPanEnabled = _viewport.IsZoomEnabled = true;
        _viewport.IsChangeFieldOfViewEnabled = true;
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
            if (!e.IsRepeat) _viewModel.Home();
            return true;
        }
        if (e.Key == Key.Tab && (_captured || _viewport.IsKeyboardFocusWithin))
        {
            if (!e.IsRepeat) { if (_captured) ReleaseMouse(); else CaptureMouse(); }
            return true;
        }
        // With the cursor released, sidebar controls retain their normal keyboard input.
        if (!_captured && !_viewport.IsKeyboardFocusWithin) return false;
        if (e.Key is Key.W or Key.A or Key.S or Key.D or Key.Up or Key.Down or Key.Left or Key.Right
            or Key.LeftShift or Key.RightShift)
        {
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return false;
            return true;
        }
        // Block Helix's vertical movement, zoom and camera-history shortcuts.
        return e.Key is Key.Space or Key.Q or Key.Z or Key.PageUp or Key.PageDown or Key.Back;
    }

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
        _captured = Mouse.Capture(_viewport, CaptureMode.Element);
        UpdateMouseCenter();
        if (_captured && !CenterCursor()) ReleaseMouse();
        _viewport.Cursor = _captured ? Cursors.None : Cursors.Arrow;
        _viewModel.MouseCaptured = _captured;
        _clock.Restart();
    }
    private void ReleaseMouse()
    {
        _captured = false;
        _mouseLook.Reset();
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
    private bool UpdateMouseCenter()
    {
        var screen = _viewport.PointToScreen(Center);
        var dpi = VisualTreeHelper.GetDpi(_viewport);
        return _mouseLook.SetCenter(screen.X, screen.Y, dpi.DpiScaleX, dpi.DpiScaleY);
    }
    private bool CenterCursor() => SetCursorPos(_mouseLook.CenterX, _mouseLook.CenterY);
    private void Render(object? sender, EventArgs e)
    {
        // WPF can raise Rendering more than once for the same composition frame.
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRenderTime) return;
            _lastRenderTime = rendering.RenderingTime;
        }
        var dt = (float)_clock.Elapsed.TotalSeconds;
        _clock.Restart();
        if (!_captured || _navigator is null || !_window.IsActive) return;
        // WPF's cached logical position can lag behind SetCursorPos. Comparing it to a
        // fractional layout center also creates a permanent subpixel delta (especially
        // at 125/150% DPI). Sample and recenter in the same physical coordinate space.
        if (UpdateMouseCenter())
        {
            if (!CenterCursor()) ReleaseMouse();
            return; // Layout/DPI changes must never turn the camera.
        }
        if (!GetCursorPos(out var mouse)) { ReleaseMouse(); return; }
        var delta = _mouseLook.Read(mouse.X, mouse.Y);
        if (Math.Abs(delta.X) < _viewport.ActualWidth / 2 && Math.Abs(delta.Y) < _viewport.ActualHeight / 2)
            _navigator.Look(-delta.X * .0025f, -delta.Y * .0025f);
        if (delta.LengthSquared() > 0 && !CenterCursor()) { ReleaseMouse(); return; }
        // Poll actual key state: autorepeat and a missed KeyUp must not leave movement stuck.
        var forward = (Down(Key.W, Key.Up) ? 1 : 0) - (Down(Key.S, Key.Down) ? 1 : 0);
        var right = (Down(Key.D, Key.Right) ? 1 : 0) - (Down(Key.A, Key.Left) ? 1 : 0);
        var previousPosition = _navigator.Position;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            _navigator.Move(forward, right, dt, Down(Key.LeftShift, Key.RightShift));
        if (delta.LengthSquared() > 0 || _navigator.Position != previousPosition)
            _viewModel.UpdateWalkPosition(_navigator);
    }

    private static bool Down(Key first, Key second) => Keyboard.IsKeyDown(first) || Keyboard.IsKeyDown(second);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out ScreenPoint point);

    public void Dispose()
    {
        Stop();
        _viewport.PreviewMouseLeftButtonDown -= MouseDown;
        _viewport.LostMouseCapture -= LostCapture;
        _window.Deactivated -= Deactivated;
    }
}
