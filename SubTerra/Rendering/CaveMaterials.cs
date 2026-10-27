using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Shaders;
using HelixToolkit.Wpf.SharpDX;
using SharpDX.DXGI;

namespace SubTerra.Rendering;

public static class CaveMaterials
{
    // Generated once per process; packed maps take 8 MiB together before GPU mipmaps.
    private static readonly Lazy<LimestoneTexture> Stone = new(() => LimestoneTexture.Create());

    public static PhongMaterial Limestone()
    {
        var texture = Stone.Value;
        return new PhongMaterial
        {
            Name = "Известняк",
            DiffuseColor = new(1, 1, 1, 1),
            AmbientColor = new(.36f, .38f, .40f, 1),
            SpecularColor = new(.10f, .095f, .085f, 1),
            SpecularShininess = 28,
            DiffuseMap = new TextureModel(texture.Albedo, Format.R8G8B8A8_UNorm, texture.Size, texture.Size),
            NormalMap = new TextureModel(texture.Normals, Format.R8G8B8A8_UNorm, texture.Size, texture.Size),
            DiffuseMapSampler = DefaultSamplers.LinearSamplerWrapAni8,
            RenderNormalMap = true,
            VertexColorBlendingFactor = .10,
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
