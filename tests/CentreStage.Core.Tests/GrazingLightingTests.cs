using System.Numerics;
using CentreStage;

namespace CentreStage.Core.Tests;

public class GrazingLightingTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.01f)]
    [InlineData(0.04f)]
    public void ReflectanceDependentFresnelPreservesNormalIncidence(float f0)
    {
        var f90 = Math.Clamp(f0 * 50f, 0f, 1f);
        static float Fresnel(float f0, float f90, float cosine) =>
            f0 + ((f90 - f0) * MathF.Pow(1f - cosine, 5f));
        Assert.Equal(f0, Fresnel(f0, f90, 1f));
        Assert.Equal(f90, Fresnel(f0, f90, 0f));
        Assert.Contains("vec3 fresnel = f0 + (vec3(f90) - f0)", PresentationShader.Fragment);
    }

    [Theory]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    [InlineData(0.8f)]
    public void RoughDielectricDoesNotLoseAllDiffuseAtGrazing(float roughness)
    {
        const float f0 = 0.04f;
        var t = (roughness * new Vector4(-1f, -0.0275f, -0.572f, 0.022f))
            + new Vector4(1f, 0.0425f, 1.04f, -0.04f);
        var a = (MathF.Min(t.X * t.X, 1f) * t.X) + t.Y;
        var reflectance = (f0 * Math.Clamp((-1.04f * a) + t.Z, 0f, 1f))
            + Math.Clamp((1.04f * a) + t.W, 0f, 1f);
        Assert.InRange(1f - reflectance, 0.1f, 1f);
        Assert.Contains("vec3 envFresnel = clamp(f0 * brdf.x + f90 * brdf.y", PresentationShader.Fragment);
        Assert.DoesNotContain("vec3 envFresnel = clamp", ShaderSources.Fragment);
    }
}
