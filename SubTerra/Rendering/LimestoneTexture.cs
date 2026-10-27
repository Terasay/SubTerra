using System.Numerics;
using SubTerra.Simulation;
using SubTerra.Models;

namespace SubTerra.Rendering;

/// <summary>Tileable stone albedo and tangent-space normals, stored as compact RGBA8.</summary>
public sealed record LimestoneTexture(int Size, byte[] Albedo, byte[] Normals, byte[]? Specular = null)
{
    public static LimestoneTexture Create(int size = 1024, CaveSurfaceKind kind = CaveSurfaceKind.Rock)
    {
        if (size < 32 || size > 2048) throw new ArgumentOutOfRangeException(nameof(size));
        var relief = new float[size * size];
        var albedo = new byte[size * size * 4];
        var normals = new byte[albedo.Length];
        var specular = kind == CaveSurfaceKind.DampRock ? new byte[albedo.Length] : null;
        Parallel.For(0, size, y =>
        {
            for (var x = 0; x < size; x++)
            {
                var u = x / (float)size; var v = y / (float)size;
                var sample = Sample(u, v, kind);
                relief[y * size + x] = sample.Height;
                Store(albedo, (y * size + x) * 4, sample.Color);
                if (specular is not null)
                    Store(specular, (y * size + x) * 4, new Vector3(.38f + .25f * RockNoise.Tile(u * 3, v, 7)));
            }
        });
        Parallel.For(0, size, y =>
        {
            for (var x = 0; x < size; x++)
            {
                // Wrap derivatives too, so the normal map has no seam at tile edges.
                var dx = relief[y * size + (x + 1) % size] - relief[y * size + (x + size - 1) % size];
                var dy = relief[((y + 1) % size) * size + x] - relief[((y + size - 1) % size) * size + x];
                var strength = size * (kind == CaveSurfaceKind.Sediment ? .007f : .018f);
                var normal = Vector3.Normalize(new(-dx * strength, -dy * strength, 1));
                Store(normals, (y * size + x) * 4, normal * .5f + new Vector3(.5f));
            }
        });
        return new(size, albedo, normals, specular);
    }

    public static (float Height, Vector3 Color) Sample(float u, float v, CaveSurfaceKind kind = CaveSurfaceKind.Rock)
    {
        if (kind == CaveSurfaceKind.Sediment) return Sediment(u, v);
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
        if (kind == CaveSurfaceKind.DampRock)
        {
            var drip = RockNoise.Tile(u * 3, v, 5) * .5f + .5f;
            return (height * .84f, tint * value * (.74f + drip * .12f));
        }
        return (height, tint * value);
    }

    private static (float Height, Vector3 Color) Sediment(float u, float v)
    {
        const int cells = 43;
        var x = u * cells; var y = v * cells;
        var ix = (int)MathF.Floor(x); var iy = (int)MathF.Floor(y);
        var nearest = 10f; var variation = 0f;
        for (var oy = -1; oy <= 1; oy++)
        for (var ox = -1; ox <= 1; ox++)
        {
            var cx = ix + ox; var cy = iy + oy;
            var dx = cx + .2f + Cell(cx, cy, 17) * .6f - x;
            var dy = cy + .2f + Cell(cx, cy, 71) * .6f - y;
            var distance = dx * dx + dy * dy;
            if (distance >= nearest) continue;
            nearest = distance; variation = Cell(cx, cy, 133);
        }
        var pebble = MathF.Pow(Math.Max(0, 1 - MathF.Sqrt(nearest) * 2.4f), .7f);
        var dust = RockNoise.Tile(u, v, 11);
        var grain = RockNoise.Tile(u, v, 229);
        var value = .34f + dust * .07f + pebble * (.06f + variation * .12f) + grain * .035f;
        var color = Vector3.Lerp(new(1.07f, .99f, .85f), new(.92f, .94f, .93f), variation) * value;
        return (pebble * .15f + dust * .035f + grain * .018f, color);

        static float Cell(int x, int y, uint salt)
        {
            unchecked
            {
                var h = (uint)((x % cells + cells) % cells) * 374761393u +
                        (uint)((y % cells + cells) % cells) * 668265263u + salt * 1274126177u;
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xffffff) / 16777215f;
            }
        }
    }

    private static void Store(byte[] target, int index, Vector3 color)
    {
        target[index] = (byte)Math.Clamp((int)MathF.Round(color.X * 255), 0, 255);
        target[index + 1] = (byte)Math.Clamp((int)MathF.Round(color.Y * 255), 0, 255);
        target[index + 2] = (byte)Math.Clamp((int)MathF.Round(color.Z * 255), 0, 255);
        target[index + 3] = 255;
    }
}
