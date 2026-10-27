using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Shaders;
using HelixToolkit.Wpf.SharpDX;
using SharpDX.DXGI;
using SubTerra.Models;

namespace SubTerra.Rendering;

public static class CaveMaterials
{
    // Generated once per process and reused when switching views and scenarios.
    private static readonly Lazy<LimestoneTexture> Stone = new(() => LimestoneTexture.Create());
    private static readonly Lazy<LimestoneTexture> Gravel = new(() => LimestoneTexture.Create(kind: CaveSurfaceKind.Sediment));
    private static readonly Lazy<LimestoneTexture> Damp = new(() => LimestoneTexture.Create(kind: CaveSurfaceKind.DampRock));

    public static PhongMaterial Limestone() => Create(Stone.Value, "Известняк", .085f, 22, .10);
    public static PhongMaterial Sediment() => Create(Gravel.Value, "Пыль и гравий", .025f, 8, 0);
    public static PhongMaterial DampLimestone() => Create(Damp.Value, "Влажный камень", .36f, 52, .035);

    private static PhongMaterial Create(LimestoneTexture texture, string name, float specular, float shininess, double vertexBlend)
    {
        return new PhongMaterial
        {
            Name = name,
            DiffuseColor = new(1, 1, 1, 1),
            AmbientColor = new(.36f, .38f, .40f, 1),
            SpecularColor = new(specular, specular * .98f, specular * .94f, 1),
            SpecularShininess = shininess,
            DiffuseMap = new TextureModel(texture.Albedo, Format.R8G8B8A8_UNorm, texture.Size, texture.Size),
            NormalMap = new TextureModel(texture.Normals, Format.R8G8B8A8_UNorm, texture.Size, texture.Size),
            SpecularColorMap = texture.Specular is null ? null : new TextureModel(texture.Specular, Format.R8G8B8A8_UNorm, texture.Size, texture.Size),
            RenderSpecularColorMap = texture.Specular is not null,
            DiffuseMapSampler = DefaultSamplers.LinearSamplerWrapAni8,
            RenderNormalMap = true,
            VertexColorBlendingFactor = vertexBlend,
            RenderShadowMap = true,
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
