using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Rendering;

/// <summary>Local RGB/depth rasterization from original measurements, independent of WPF/GPU/desktop capture.</summary>
public static class ScanImageRenderer
{
    public static ScanImage Render(ScanProject project, ScanViewPose pose, int size = 416, PointClipPlane? clip = null, CancellationToken token = default)
    {
        if (size is < 64 or > 1024 || !InspectionPoint.From(pose.Position).IsValid || !InspectionPoint.From(pose.Forward).IsValid ||
            !InspectionPoint.From(pose.Up).IsValid || pose.Forward.LengthSquared() < 1e-8 || pose.FieldOfView is < 20 or > 120 || !float.IsFinite(pose.FieldOfView))
            throw new ArgumentException("Некорректный ракурс для анализа.");
        var forward = Vector3.Normalize(pose.Forward); var cross = Vector3.Cross(forward, pose.Up);
        if (cross.LengthSquared() < 1e-8) throw new ArgumentException("Вертикаль совпадает с направлением взгляда.");
        var right = Vector3.Normalize(cross); var up = Vector3.Cross(right, forward);
        var focal = size / (2 * MathF.Tan(pose.FieldOfView * MathF.PI / 360));
        var rgb = new byte[size * size * 3]; var depth = new float[size * size]; var world = new Vector3[size * size];
        Array.Fill(rgb, (byte)114); Array.Fill(depth, float.PositiveInfinity); var visited = 0;
        foreach (var obj in project.Objects.Where(o => o.IsVisible))
        {
            var matrix = obj.Matrix;
            for (var i = 0; i < obj.Source.Points.Length; i++)
            {
                if ((visited++ & 2047) == 0) token.ThrowIfCancellationRequested();
                if (!obj.Includes(i)) continue;
                var p = Vector3.Transform(obj.Source.Points[i].Position, matrix);
                if (clip is { } plane && !plane.Contains(p)) continue;
                var relative = p - pose.Position; var z = Vector3.Dot(relative, forward);
                if (z < .12 || z > 80) continue;
                var x = (int)MathF.Round(size * .5f + focal * Vector3.Dot(relative, right) / z);
                var y = (int)MathF.Round(size * .5f - focal * Vector3.Dot(relative, up) / z);
                if (x < -2 || y < -2 || x >= size + 2 || y >= size + 2) continue;
                var radius = Math.Clamp((int)MathF.Ceiling(focal * .009f / z), 1, 2);
                var color = obj.Source.Colors.Length == 0 ? 0xB5BCC3u : obj.Source.Colors[i];
                for (var dy = -radius; dy <= radius; dy++) for (var dx = -radius; dx <= radius; dx++)
                {
                    var px = x + dx; var py = y + dy;
                    if (px < 0 || py < 0 || px >= size || py >= size) continue;
                    var slot = py * size + px;
                    if (z >= depth[slot]) continue;
                    depth[slot] = z; world[slot] = p;
                    rgb[slot * 3] = (byte)(color >> 16); rgb[slot * 3 + 1] = (byte)(color >> 8); rgb[slot * 3 + 2] = (byte)color;
                }
            }
        }
        return new(pose, size, rgb, depth, world);
    }
}
