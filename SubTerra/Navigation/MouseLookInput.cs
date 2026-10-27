using System.Numerics;

namespace SubTerra.Navigation;

/// <summary>Measures physical screen pixels against the exact integer cursor warp target.</summary>
public sealed class MouseLookInput
{
    public int CenterX { get; private set; }
    public int CenterY { get; private set; }
    private double _dpiX = 1;
    private double _dpiY = 1;
    private bool _ready;

    public bool SetCenter(double screenX, double screenY, double dpiX, double dpiY)
    {
        var x = (int)Math.Round(screenX);
        var y = (int)Math.Round(screenY);
        var changed = !_ready || x != CenterX || y != CenterY || dpiX != _dpiX || dpiY != _dpiY;
        CenterX = x;
        CenterY = y;
        _dpiX = dpiX > 0 && double.IsFinite(dpiX) ? dpiX : 1;
        _dpiY = dpiY > 0 && double.IsFinite(dpiY) ? dpiY : 1;
        _ready = true;
        return changed;
    }

    public Vector2 Read(int screenX, int screenY) => !_ready ? Vector2.Zero :
        new((float)((screenX - CenterX) / _dpiX), (float)((screenY - CenterY) / _dpiY));

    public void Reset() => _ready = false;
}
