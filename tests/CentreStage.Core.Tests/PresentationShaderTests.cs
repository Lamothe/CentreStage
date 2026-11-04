namespace CentreStage.Core.Tests;

public class PresentationShaderTests
{
    [Fact]
    public void StudioOccludesEnvironmentReflectionsBelowTheMeshHorizon()
    {
        Assert.Contains("dot(reflected, surfaceNormal)", PresentationShader.Fragment);
        Assert.Contains("horizon *= horizon", PresentationShader.Fragment);
        Assert.Contains("brdf.y) * horizon", PresentationShader.Fragment);
        Assert.DoesNotContain("float horizon", ShaderSources.Fragment);
        Assert.DoesNotContain("float horizon", ShaderSources.CheapFragment);
    }

    [Fact]
    public void LiveVariantBuildsWithOneMainAndVersion()
    {
        var shader = PresentationShader.Fragment;
        Assert.StartsWith("#version 330 core\n", shader);
        Assert.Equal(1, Count(shader, "#version"));
        Assert.Equal(1, Count(shader, "void main()"));
        Assert.Contains("studioDiffuse(environmentDirection(diffuseNormal))", shader);
        Assert.Contains("f0 * brdf.x + f90 * brdf.y", shader);
        Assert.Contains("uClearcoatRoughnessTex", shader);
    }

    [Fact]
    public void PinnedSourcesContainNoPresentationChanges()
    {
        foreach (var shader in new[] { ShaderSources.Fragment, ShaderSources.CheapFragment })
        {
            Assert.DoesNotContain("studioDiffuse", shader);
            Assert.DoesNotContain("presentationMap", shader);
            Assert.DoesNotContain("uSkinDiffusion", shader);
            Assert.Contains("mix(uGroundColor, uSkyColor, n.y * 0.5 + 0.5)", shader);
        }
    }

    [Fact]
    public void CapturedTransmissionIsNotGradedTwiceAndRetainsEmission()
    {
        var shader = PresentationShader.Fragment;
        Assert.Contains("if (transmission > 0.0 && uHasSceneColor == 0)", shader);
        var mapped = shader.IndexOf("color = presentationMap(color);", StringComparison.Ordinal);
        var transmitted = shader.IndexOf("if (transmission > 0.0 && uHasSceneColor != 0)", StringComparison.Ordinal);
        Assert.True(mapped > 0 && transmitted > mapped);
        Assert.Contains("behind * base.rgb + emissionLinear", shader);
        Assert.Contains("mix(srgbToLinear(color), transmittedLinear, t)", shader);
        Assert.Contains("srgbToLinear(presentationMap(emissive))", shader);
    }

    private static int Count(string text, string value) =>
        (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
}
