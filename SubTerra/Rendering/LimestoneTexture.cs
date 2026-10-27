using System.Numerics;
using SubTerra.Simulation;

namespace SubTerra.Rendering;

/// <summary>Tileable stone albedo and tangent-space normals, stored as compact RGBA8.</summary>
public sealed record LimestoneTexture(int Size, byte[] Albedo, byte[] Normals)
{
    public static LimestoneTexture Create(int size = 1024)
    {
        if (size < 32 || size > 2048) throw new ArgumentOutOfRangeException(nameof(size));
        var relief = new float[size * size];
        var albedo = new byte[size * size * 4];
        var normals = new byte[albedo.Length];
        Parallel.For(0, size, y =>
        {
            for (var x = 0; x < size; x++)
            {
                var sample = Sample(x / (float)size, y / (float)size);
                relief[y * size + x] = sample.Height;
                Store(albedo, (y * size + x) * 4, sample.Color);
            }
        });
        Parallel.For(0, size, y =>
        {
            for (var x = 0; x < size; x++)
            {
                // Wrap derivatives too, so the normal map has no seam at tile edges.
                var dx = relief[y * size + (x + 1) % size] - relief[y * size + (x + size - 1) % size];
                var dy = relief[((y + 1) % size) * size + x] - relief[((y + size - 1) % size) * size + x];
                var strength = size * .018f;
                var normal = Vector3.Normalize(new(-dx * strength, -dy * strength, 1));
                Store(normals, (y * size + x) * 4, normal * .5f + new Vector3(.5f));
            }
        });
        return new(size, albedo, normals);
    }

    public static (float Height, Vector3 Color) Sample(float u, float v)
    {
        // Periodic domain warping breaks up regular bands without introducing seams.
        var warpX = RockNoise.Tile(u, v, 4);
        var warpY = RockNoise.Tile(u + .37f, v + .19f, 4);
        var x = u + warpX * .065f;
        var y = v + warpY * .065f;
        var body = RockNoise.Tile(x, y, 7);
        var chips = RockNoise.Tile(x, y, 27);
        var pores = RockNoise.Tile(x, y, 91);
        var grain = RockNoise.Tile(x, y, 251);
        var fissure = MathF.Pow(1 - MathF.Abs(RockNoise.Tile(x, y, 13)), 24);
        var height = body * .46f + chips * .26f + pores * .075f + grain * .022f - fissure * .085f;
        var value = Math.Clamp(.52f + body * .16f + chips * .13f + pores * .055f + grain * .025f - fissure * .075f, .22f, .78f);
        var mineral = RockNoise.Tile(u + .2f, v, 3) * .5f + .5f;
        var tint = Vector3.Lerp(new(.87f, .89f, .88f), new(1.09f, 1.0f, .82f), mineral);
        return (height, tint * value);
    }

    private static void Store(byte[] target, int index, Vector3 color)
    {
        target[index] = (byte)Math.Clamp((int)MathF.Round(color.X * 255), 0, 255);
        target[index + 1] = (byte)Math.Clamp((int)MathF.Round(color.Y * 255), 0, 255);
        target[index + 2] = (byte)Math.Clamp((int)MathF.Round(color.Z * 255), 0, 255);
        target[index + 3] = 255;
    }
}
