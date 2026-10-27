using System.Numerics;
using SubTerra.Rendering;
using Xunit;

namespace SubTerra.Tests;

public sealed class LimestoneTextureTests
{
    [Fact]
    public void WarpedStoneRepeatsContinuouslyAcrossBothTileEdges()
    {
        for (var i = 0; i < 40; i++)
        {
            var t = i / 40f;
            var left = LimestoneTexture.Sample(0, t);
            var right = LimestoneTexture.Sample(1, t);
            var top = LimestoneTexture.Sample(t, 0);
            var bottom = LimestoneTexture.Sample(t, 1);
            Assert.InRange(Math.Abs(left.Height - right.Height), 0, .0001f);
            Assert.InRange(Vector3.Distance(left.Color, right.Color), 0, .0001f);
            Assert.InRange(Math.Abs(top.Height - bottom.Height), 0, .0001f);
            Assert.InRange(Vector3.Distance(top.Color, bottom.Color), 0, .0001f);
        }
    }

    [Fact]
    public void PackedMapsAreOpaqueDeterministicAndHaveValidNormals()
    {
        var texture = LimestoneTexture.Create(64);
        var repeat = LimestoneTexture.Create(64);
        Assert.Equal(64 * 64 * 4, texture.Albedo.Length);
        Assert.Equal(texture.Albedo, repeat.Albedo);
        Assert.Equal(texture.Normals, repeat.Normals);
        for (var i = 0; i < texture.Normals.Length; i += 4)
        {
            Assert.Equal(255, texture.Albedo[i + 3]);
            Assert.Equal(255, texture.Normals[i + 3]);
            var normal = new Vector3(texture.Normals[i], texture.Normals[i + 1], texture.Normals[i + 2]) / 255 * 2 - Vector3.One;
            Assert.InRange(normal.Length(), .985f, 1.015f);
            Assert.True(normal.Z > 0);
        }
    }
}
