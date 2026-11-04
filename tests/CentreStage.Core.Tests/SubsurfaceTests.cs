using CentreStage;

namespace CentreStage.Core.Tests;

public class SubsurfaceTests
{
    [Fact]
    public void SplitCapturesOnlyDiffuseInLinearSpaceBeforeDisplayMapping()
    {
        var shader = PresentationShader.Fragment;
        Assert.Contains("sssDiffuse += kd * base.rgb / PI * radiance * NoL", shader);
        Assert.Contains("sssDiffuse += ambient * base.rgb", shader);
        Assert.Contains("sssDiffuse *= mix(1.0, ao, uOcclusionStrength)", shader);
        var capture = shader.IndexOf("if (uSssCapture)", StringComparison.Ordinal);
        var map = shader.IndexOf("color = presentationMap(color);", StringComparison.Ordinal);
        Assert.True(capture > 0 && capture < map);
        Assert.Contains("DiffuseOutput = vec4(sssDiffuse, uSssMaterial)", shader);
        Assert.Contains("DiffuseOutput = vec4(0.0, 0.0, 0.0, -1.0)", shader);
        Assert.DoesNotContain("uSssCapture", ShaderSources.Fragment);
    }

    [Fact]
    public void RecombinationPreservesNonDiffuseAndUnlitOutputAndTransfersDepth()
    {
        Assert.Contains("radiance + (softened - original.rgb) * strength * original.a", SubsurfaceShaders.Combine);
        Assert.Contains("if (original.a < 0.0) { result = vec4(radiance, 1.0); return; }", SubsurfaceShaders.Combine);
        Assert.Contains("gl_FragDepth = texture(depthMap, uv).r", SubsurfaceShaders.Combine);
        Assert.Contains("texture(maskMap", SubsurfaceShaders.Blur);
        Assert.Contains("smoothstep(radiusWorld * 0.25, radiusWorld, dz)", SubsurfaceShaders.Blur);
        Assert.DoesNotContain("rand", SubsurfaceShaders.Blur);
    }

    [Fact]
    public void SkinKernelIsNormalisedAndRedTravelsFarther()
    {
        var radii = new[] { 2f, 6f, 12f };
        var variances = new float[3];
        for (var channel = 0; channel < 3; channel++)
        {
            var sum = 0f;
            var moment = 0f;
            for (var i = -8; i <= 8; i++)
            {
                var x = i / 8f;
                var weight = MathF.Exp(-radii[channel] * x * x);
                sum += weight;
                moment += weight * x * x;
            }
            Assert.True(sum > 0f && float.IsFinite(sum));
            Assert.Equal(0.4f, 0.4f * sum / sum, 5);
            variances[channel] = moment / sum;
        }
        Assert.True(variances[0] > variances[1] && variances[1] > variances[2]);
    }
}
