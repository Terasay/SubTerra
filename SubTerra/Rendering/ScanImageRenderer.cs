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
            if (obj.Source.Appearance is not null)
            {
                RenderTexturedMesh(obj, pose, right, up, forward, focal, size, clip, rgb, depth, world, token);
                continue;
            }
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

    private readonly record struct RasterVertex(Vector3 World, Vector3 View, Vector2 Uv, Vector4 Color)
    {
        public static RasterVertex Lerp(RasterVertex a, RasterVertex b, float t) => new(Vector3.Lerp(a.World, b.World, t), Vector3.Lerp(a.View, b.View, t), Vector2.Lerp(a.Uv, b.Uv, t), Vector4.Lerp(a.Color, b.Color, t));
    }
    private static int ClipDepth(ReadOnlySpan<RasterVertex> input, Span<RasterVertex> output, float boundary, bool near)
    {
        var count = 0; var previous = input[^1]; var wasInside = near ? previous.View.Z >= boundary : previous.View.Z <= boundary;
        foreach (var current in input)
        {
            var inside = near ? current.View.Z >= boundary : current.View.Z <= boundary;
            if (wasInside != inside) output[count++] = RasterVertex.Lerp(previous, current, (boundary - previous.View.Z) / (current.View.Z - previous.View.Z));
            if (inside) output[count++] = current;
            previous = current; wasInside = inside;
        }
        return count;
    }
    private static void RenderTexturedMesh(ScanObject obj, ScanViewPose pose, Vector3 right, Vector3 up, Vector3 forward,
        float focal, int size, PointClipPlane? clip, byte[] rgb, float[] depth, Vector3[] world, CancellationToken token)
    {
        var scan = obj.Source; var appearance = scan.Appearance!; var matrix = obj.Matrix;
        Span<RasterVertex> input = stackalloc RasterVertex[8]; Span<RasterVertex> clipped = stackalloc RasterVertex[8];
        RasterVertex Vertex(int i)
        {
            var p = Vector3.Transform(scan.Points[i].Position, matrix); var relative = p - pose.Position;
            return new(p, new(Vector3.Dot(relative, right), Vector3.Dot(relative, up), Vector3.Dot(relative, forward)), appearance.TextureCoordinates[i], appearance.VertexColors[i]);
        }
        foreach (var part in appearance.Parts)
        {
            var material = appearance.Materials[part.MaterialIndex];
            for (var i = part.IndexOffset; i < part.IndexOffset + part.IndexCount; i += 3)
            {
                if ((i % 2049) == 0) token.ThrowIfCancellationRequested();
                var a = scan.Triangles[i]; var b = scan.Triangles[i + 1]; var c = scan.Triangles[i + 2];
                if (!obj.Includes(a) || !obj.Includes(b) || !obj.Includes(c)) continue;
                input[0] = Vertex(a); input[1] = Vertex(b); input[2] = Vertex(c);
                if (!material.DoubleSided && Vector3.Dot(Vector3.Cross(input[1].World - input[0].World, input[2].World - input[0].World), input[0].World - pose.Position) >= 0) continue;
                var count = ClipDepth(input[..3], clipped, .12f, true); if (count < 3) continue;
                count = ClipDepth(clipped[..count], input, 80, false); if (count < 3) continue;
                for (var triangle = 1; triangle < count - 1; triangle++)
                    Rasterize(input[0], input[triangle], input[triangle + 1], appearance, material, focal, size, clip, rgb, depth, world, token);
            }
        }
    }
    private static void Rasterize(RasterVertex a, RasterVertex b, RasterVertex c, MeshAppearance appearance, MeshMaterial material,
        float focal, int size, PointClipPlane? clip, byte[] rgb, float[] depth, Vector3[] world, CancellationToken token)
    {
        Vector2 Project(Vector3 p) => new(size * .5f + focal * p.X / p.Z, size * .5f - focal * p.Y / p.Z);
        static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
        var p0 = Project(a.View); var p1 = Project(b.View); var p2 = Project(c.View); var area = Edge(p0, p1, p2);
        if (!float.IsFinite(area) || Math.Abs(area) < 1e-8f) return;
        var x0 = (int)Math.Clamp(MathF.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X))), 0, size - 1);
        var x1 = (int)Math.Clamp(MathF.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))), 0, size - 1);
        var y0 = (int)Math.Clamp(MathF.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))), 0, size - 1);
        var y1 = (int)Math.Clamp(MathF.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))), 0, size - 1);
        for (var y = y0; y <= y1; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = x0; x <= x1; x++)
            {
                var pixel = new Vector2(x + .5f, y + .5f); var w0 = Edge(p1, p2, pixel) / area; var w1 = Edge(p2, p0, pixel) / area; var w2 = 1 - w0 - w1;
                if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f) continue;
                var inverse = w0 / a.View.Z + w1 / b.View.Z + w2 / c.View.Z; var z = 1 / inverse; var slot = y * size + x;
                if (z >= depth[slot]) continue;
                w0 /= a.View.Z * inverse; w1 /= b.View.Z * inverse; w2 /= c.View.Z * inverse;
                var position = a.World * w0 + b.World * w1 + c.World * w2;
                if (clip is { } plane && !plane.Contains(position)) continue;
                var color = MeshTextureSampler.Color(appearance, material, a.Uv * w0 + b.Uv * w1 + c.Uv * w2, a.Color * w0 + b.Color * w1 + c.Color * w2);
                if (material.AlphaMode == "MASK" && color.W < material.AlphaCutoff || material.AlphaMode == "BLEND" && color.W < .5f) continue;
                var packed = MeshTextureSampler.Pack(color); depth[slot] = z; world[slot] = position;
                rgb[slot * 3] = (byte)(packed >> 16); rgb[slot * 3 + 1] = (byte)(packed >> 8); rgb[slot * 3 + 2] = (byte)packed;
            }
        }
    }
}
