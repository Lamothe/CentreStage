using CentreStage.Core;

namespace CentreStage.Core.Tests;

public class TextureColorSpaceTests
{
    private static TextureRef Tex(int index) => new() { TextureIndex = index };

    [Fact]
    public void BaseColor_IsSrgb_Normal_IsNot()
    {
        var mat = new RuntimeMaterial
        {
            BaseColorTexture = Tex(3),
            NormalTexture = Tex(4),
            MetallicRoughnessTexture = Tex(5),
        };

        var srgb = TextureColorSpace.SrgbTextureIndices([mat]);

        Assert.Equal(new[] { 3 }, srgb.OrderBy(i => i));
    }

    [Fact]
    public void Emissive_SheenColor_SpecularColor_AreSrgb()
    {
        var mat = new RuntimeMaterial
        {
            EmissiveTexture = Tex(1),
            SheenColorTexture = Tex(2),
            SpecularColorTexture = Tex(3),
            SheenRoughnessTexture = Tex(4),
            SpecularTexture = Tex(5),
        };

        var srgb = TextureColorSpace.SrgbTextureIndices([mat]);

        Assert.Equal(new[] { 1, 2, 3 }, srgb.OrderBy(i => i));
    }

    [Fact]
    public void SpecGloss_DiffuseAndCombined_AreSrgb()
    {
        var mat = new RuntimeMaterial
        {
            IsSpecularGlossiness = true,
            SgDiffuseTexture = Tex(7),
            SgSpecularGlossinessTexture = Tex(8),
        };

        var srgb = TextureColorSpace.SrgbTextureIndices([mat]);

        Assert.Equal(new[] { 7, 8 }, srgb.OrderBy(i => i));
    }

    [Fact]
    public void SharedTexture_ClassifiedSrgb()
    {
        var colourUse = new RuntimeMaterial { BaseColorTexture = Tex(2) };
        var dataUse = new RuntimeMaterial { OcclusionTexture = Tex(2) };

        var srgb = TextureColorSpace.SrgbTextureIndices([colourUse, dataUse]);

        Assert.Contains(2, srgb);
        Assert.Equal(new[] { 2 }, TextureColorSpace.SharedUsage([colourUse, dataUse]));
    }

    [Fact]
    public void UnsharedTextures_ProduceNoSharedWarning()
    {
        var mat = new RuntimeMaterial
        {
            BaseColorTexture = Tex(1),
            MetallicRoughnessTexture = Tex(2),
        };

        Assert.Empty(TextureColorSpace.SharedUsage([mat]));
    }

    [Fact]
    public void DataOnlyTextures_NotSrgb()
    {
        var mat = new RuntimeMaterial
        {
            NormalTexture = Tex(1),
            OcclusionTexture = Tex(2),
            TransmissionTexture = Tex(3),
            ClearcoatTexture = Tex(4),
            ClearcoatRoughnessTexture = Tex(5),
            ClearcoatNormalTexture = Tex(6),
        };

        var srgb = TextureColorSpace.SrgbTextureIndices([mat]);

        Assert.Empty(srgb);
    }
}
