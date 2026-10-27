namespace CentreStage.Core;

/// <summary>
/// glTF colour-space rules: base colour, emissive, sheen/specular colour and the legacy
/// spec-gloss textures are sRGB-encoded; every other slot carries linear data. Uploading
/// colour textures as GL_SRGB8_ALPHA8 lets the GPU decode them on sample, so lighting runs
/// in linear space and the output encode is the only gamma step. (GL_SRGB8_ALPHA8 leaves
/// the alpha channel linear, which is exactly right: strand coverage is not a colour.)
/// </summary>
public static class TextureColorSpace
{
    /// <summary>
    /// Texture indices that must upload as sRGB. A texture used from both a colour slot and
    /// a data slot is classified sRGB — the colour use dominates visually, and the spec
    /// forbids sharing anyway.
    /// </summary>
    public static HashSet<int> SrgbTextureIndices(IReadOnlyList<RuntimeMaterial> materials)
    {
        var srgb = new HashSet<int>();
        foreach (var m in materials)
        {
            Add(srgb, m.BaseColorTexture);
            Add(srgb, m.EmissiveTexture);
            Add(srgb, m.SheenColorTexture);
            Add(srgb, m.SpecularColorTexture);
            Add(srgb, m.SgDiffuseTexture);
            Add(srgb, m.SgSpecularGlossinessTexture);
        }

        return srgb;
    }

    private static void Add(HashSet<int> set, TextureRef? tex)
    {
        if (tex is not null)
        {
            set.Add(tex.TextureIndex);
        }
    }

    /// <summary>
    /// Indices used from both a colour slot and a linear-data slot (across all
    /// materials). Upload classifies these sRGB, so the data consumer reads decoded
    /// values; the renderer warns so the deviation is visible.
    /// </summary>
    public static IReadOnlyList<int> SharedUsage(IReadOnlyList<RuntimeMaterial> materials)
    {
        var srgb = new HashSet<int>();
        var data = new HashSet<int>();
        foreach (var m in materials)
        {
            Add(srgb, m.BaseColorTexture);
            Add(srgb, m.EmissiveTexture);
            Add(srgb, m.SheenColorTexture);
            Add(srgb, m.SpecularColorTexture);
            Add(srgb, m.SgDiffuseTexture);
            Add(srgb, m.SgSpecularGlossinessTexture);
            Add(data, m.MetallicRoughnessTexture);
            Add(data, m.NormalTexture);
            Add(data, m.OcclusionTexture);
            Add(data, m.SpecularTexture);
            Add(data, m.TransmissionTexture);
            Add(data, m.ClearcoatTexture);
            Add(data, m.ClearcoatRoughnessTexture);
            Add(data, m.ClearcoatNormalTexture);
            Add(data, m.SheenRoughnessTexture);
        }

        srgb.IntersectWith(data);
        return srgb.OrderBy(i => i).ToList();
    }
}
