using System.Numerics;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using SubTerra.Simulation;

namespace SubTerra.Rendering;

public static class CaveMaterials
{
    /// <summary>Seamless procedural limestone: no external assets or network dependencies.</summary>
    public static PhongMaterial Limestone()
    {
        const int size = 512;
        var relief = new float[size * size];
        var diffuse = new Color4[relief.Length];
        var normals = new Color4[relief.Length];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var u = x / (float)size; var v = y / (float)size;
            var coarse = RockNoise.Tile(u, v, 5);
            var medium = RockNoise.Tile(u, v, 19);
            var fine = RockNoise.Tile(u, v, 77);
            var grain = RockNoise.Tile(u, v, 179);
            var vein = MathF.Pow(1 - MathF.Abs(RockNoise.Tile(u, v, 9)), 16);
            var strata = MathF.Sin(v * MathF.Tau * 12 + coarse * 3);
            var h = coarse * .48f + medium * .24f + fine * .10f + grain * .035f - vein * .1f;
            relief[y * size + x] = h;
            var shade = Math.Clamp(.57f + h * .27f + strata * .025f, .25f, .82f);
            diffuse[y * size + x] = new(shade * 1.08f, shade, shade * .85f, 1);
        }
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = relief[y * size + (x + 1) % size] - relief[y * size + (x + size - 1) % size];
            var dy = relief[((y + 1) % size) * size + x] - relief[((y + size - 1) % size) * size + x];
            var n = Vector3.Normalize(new(-dx * 5, -dy * 5, 1));
            normals[y * size + x] = new(n.X * .5f + .5f, n.Y * .5f + .5f, n.Z * .5f + .5f, 1);
        }
        return new PhongMaterial
        {
            Name = "Известняк",
            DiffuseColor = new(1, 1, 1, 1),
            AmbientColor = new(.42f, .42f, .42f, 1),
            SpecularColor = new(.09f, .085f, .075f, 1),
            SpecularShininess = 14,
            DiffuseMap = new TextureModel(diffuse, size, size),
            NormalMap = new TextureModel(normals, size, size),
            RenderNormalMap = true,
            VertexColorBlendingFactor = .18,
            EnableFlatShading = false
        };
    }

    public static PhongMaterial Height { get; } = new()
    {
        DiffuseColor = new(1, 1, 1, 1),
        AmbientColor = new(.5f, .5f, .5f, 1),
        SpecularColor = new(.05f, .05f, .05f, 1),
        VertexColorBlendingFactor = 1
    };
}
