using System.Diagnostics;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel
{
    private long _lastCameraInteraction;
    private bool _isCameraMoving;
    private string _frameRateText = "Кадры: ожидание движения";
    public bool IsCameraMoving => _isCameraMoving;
    public bool SceneLightingVisible => !IsWalking;
    public bool SsaoEnabled => IsCave && (!AdaptiveQuality || !IsCameraMoving);
    public string FrameRateText { get => _frameRateText; private set => Set(ref _frameRateText, value); }
    public string QualityStateText => IsCameraMoving ? "Движение · плотность точек сохранена" : "Плотность по выбранному лимиту";
    private int EffectivePointLimit => PointQuality.PointLimit;

    public void NotifyCameraInteraction()
    {
        if (_disposed || IsBusy || _dataset is null) return;
        _lastCameraInteraction = Stopwatch.GetTimestamp();
        if (_isCameraMoving) return;
        _isCameraMoving = true;
        RefreshAdaptiveQuality();
    }
    public void SettleCameraInteraction()
    {
        if (!_isCameraMoving || Stopwatch.GetElapsedTime(_lastCameraInteraction).TotalMilliseconds < 250) return;
        _isCameraMoving = false;
        RefreshAdaptiveQuality();
    }
    private void RefreshAdaptiveQuality()
    {
        Raise(nameof(IsCameraMoving)); Raise(nameof(QualityStateText)); Raise(nameof(SsaoEnabled)); Raise(nameof(ShadowsVisible));
        // Motion only changes lighting effects. Reuse the point buffers, including millions of points.
        if (IsImported && IsSurface) RefreshGeometry();
    }
    public void UpdateFrameTiming((double Fps, double FrameMilliseconds)? timing) => FrameRateText = timing is { } value
        ? $"{value.Fps:F0} FPS · кадр {value.FrameMilliseconds:F1} мс" : "Кадры: покой";
}
