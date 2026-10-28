using System.Numerics;

namespace SubTerra.Navigation;

/// <summary>Camera motion independent of the viewport's orbit controller.</summary>
public interface ICameraNavigator
{
    Vector3 Position { get; }
    Vector3 Forward { get; }
    float DistanceTravelled { get; }
    void PlaceAt(float progress);
    void Look(float yawDelta, float pitchDelta);
    void Move(float forward, float right, float deltaSeconds, bool fast, float up = 0);
}
