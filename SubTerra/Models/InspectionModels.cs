using System.Numerics;
using System.Text.Json.Serialization;

namespace SubTerra.Models;

public readonly record struct InspectionPoint(float X, float Y, float Z)
{
    [JsonIgnore] public Vector3 Position => new(X, Y, Z);
    [JsonIgnore] public bool IsValid => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) &&
                           Math.Abs(X) <= 100_000 && Math.Abs(Y) <= 100_000 && Math.Abs(Z) <= 100_000;
    public static InspectionPoint From(Vector3 point) => new(point.X, point.Y, point.Z);
}

public sealed record UserAnnotation(string Name, InspectionPoint Point);

/// <summary>Keeps the half-space dot(normal, point) &lt;= offset; source measurements are untouched.</summary>
public readonly record struct PointClipPlane(Vector3 Normal, float Offset)
{
    private const float Epsilon = .00001f;
    public bool Contains(Vector3 point) => Vector3.Dot(Normal, point) <= Offset + Epsilon;
    public bool TryClipSegment(ref Vector3 start, ref Vector3 end)
    {
        var insideStart = Contains(start); var insideEnd = Contains(end);
        if (!insideStart && !insideEnd) return false;
        if (insideStart != insideEnd)
        {
            var cut = Vector3.Lerp(start, end, (Offset + Epsilon - Vector3.Dot(Normal, start)) / Vector3.Dot(Normal, end - start));
            if (insideStart) end = cut; else start = cut;
        }
        return true;
    }
    public static PointClipPlane FromAngles(double yawDegrees, double pitchDegrees, double offset)
    {
        var yaw = (float)(yawDegrees * Math.PI / 180);
        var pitch = (float)(pitchDegrees * Math.PI / 180);
        return new(new(MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch)), (float)offset);
    }
}
