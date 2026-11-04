using System.Globalization;
using CentreStage.Core;

namespace CentreStage.Core.Tests;

public class PresentationSettingsTests
{
    public static TheoryData<string, float, float, float> NumericSettings => new()
    {
        { "CENTRESTAGE_ENV_INTENSITY", 1.12f, 0f, 16f },
        { "CENTRESTAGE_FALLBACK_LIGHT", 0f, 0f, 16f },
        { "CENTRESTAGE_ENV_ROTATION", 0f, -360f, 360f },
        { "CENTRESTAGE_EXPOSURE", 0f, -8f, 8f },
        { "CENTRESTAGE_BRIGHTNESS", 0.04f, -0.5f, 0.5f },
        { "CENTRESTAGE_CONTRAST", -0.12f, -1f, 1f },
        { "CENTRESTAGE_SATURATION", 0.73f, 0f, 2f },
        { "CENTRESTAGE_SKIN_DIFFUSION", 0f, 0f, 1f },
    };

    public static IEnumerable<object[]> InvalidNumbers()
    {
        foreach (var setting in NumericSettings)
        {
            var name = (string)setting[0];
            var fallback = (float)setting[1];
            var min = (float)setting[2];
            var max = (float)setting[3];
            foreach (var text in new[]
            {
                "invalid", "NaN", "Infinity", "-Infinity", "1e100",
                (min - 1f).ToString(CultureInfo.InvariantCulture),
                (max + 1f).ToString(CultureInfo.InvariantCulture),
            })
            {
                yield return new object[] { name, fallback, text };
            }
        }
    }

    [Fact]
    public void Constructor_HasExpectedDefaults()
    {
        var settings = new PresentationSettings();

        Assert.True(settings.Enabled);
        Assert.Equal(1.12f, settings.EnvironmentIntensity);
        Assert.Equal(0f, settings.EnvironmentRotation);
        Assert.Equal(0f, settings.Exposure);
        Assert.Equal(0.04f, settings.Brightness);
        Assert.Equal(-0.12f, settings.Contrast);
        Assert.Equal(0.73f, settings.Saturation);
        Assert.Equal(0f, settings.SkinDiffusion);
        Assert.Equal("skin,head,face,body", settings.SkinFamilies);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_MissingOrEmptyValuesKeepDefaultsWithoutWarnings(string? value)
    {
        var warnings = new List<string>();
        var settings = PresentationSettings.Parse(_ => value, warnings.Add);

        var expected = new PresentationSettings();
        if (value == "")
        {
            expected = expected with { SkinFamilies = "" };
        }
        Assert.Equal(expected, settings);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Legacy_DisablesPresentationAndSkinDiffusion()
    {
        Assert.Equal(new PresentationSettings { Enabled = false, SkinDiffusion = 0f },
            PresentationSettings.Legacy);
    }

    [Theory]
    [MemberData(nameof(InvalidNumbers))]
    public void Parse_InvalidNumberFallsBackAndWarns(string name, float fallback, string text)
    {
        var warnings = new List<string>();
        var settings = PresentationSettings.Parse(key => key == name ? text : null, warnings.Add);

        Assert.Equal(fallback, Number(settings, name));
        Assert.Equal(new PresentationSettings(), settings);
        var warning = Assert.Single(warnings);
        Assert.Contains($"{name}={text}", warning);
        Assert.Contains("using", warning);
        Assert.Equal(settings, PresentationSettings.Parse(key => key == name ? text : null));
    }

    [Theory]
    [MemberData(nameof(NumericSettings))]
    public void Parse_AcceptsInclusiveBoundariesAndInteriorValues(
        string name, float fallback, float min, float max)
    {
        foreach (var value in new[] { min, max, (min + max) / 2f, fallback })
        {
            var warnings = new List<string>();
            var text = value.ToString("R", CultureInfo.InvariantCulture);
            var settings = PresentationSettings.Parse(key => key == name ? text : null, warnings.Add);

            Assert.Equal(value, Number(settings, name));
            Assert.Empty(warnings);
        }
    }

    [Theory]
    [MemberData(nameof(NumericSettings))]
    public void Parse_UsesInvariantCultureForEveryNumericSetting(
        string name, float fallback, float min, float max)
    {
        Assert.InRange(0.25f, min, max);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var warnings = new List<string>();
            var settings = PresentationSettings.Parse(key => key == name ? "0.25" : null, warnings.Add);

            Assert.Equal(0.25f, Number(settings, name));
            Assert.Empty(warnings);

            settings = PresentationSettings.Parse(key => key == name ? "2.5e-1" : null, warnings.Add);
            Assert.Equal(0.25f, Number(settings, name));
            Assert.Empty(warnings);

            settings = PresentationSettings.Parse(key => key == name ? "0,25" : null, warnings.Add);
            Assert.Equal(fallback, Number(settings, name));
            Assert.Single(warnings);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void Parse_ExplicitToggleDoesNotWarn(string value, bool enabled)
    {
        var warnings = new List<string>();
        var settings = PresentationSettings.Parse(
            key => key == "CENTRESTAGE_PRESENTATION" ? value : null, warnings.Add);

        Assert.Equal(new PresentationSettings { Enabled = enabled }, settings);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("2")]
    [InlineData(" 0 ")]
    public void Parse_InvalidToggleEnablesPresentationAndWarns(string value)
    {
        var warnings = new List<string>();
        var settings = PresentationSettings.Parse(
            key => key == "CENTRESTAGE_PRESENTATION" ? value : null, warnings.Add);

        Assert.True(settings.Enabled);
        Assert.Contains("CENTRESTAGE_PRESENTATION expects 0 or 1; using 1", Assert.Single(warnings));
    }

    [Theory]
    [InlineData("Head_Material", true)]
    [InlineData("Face_Material", true)]
    [InlineData("Body", true)]
    [InlineData("Headlight", false)]
    [InlineData("SkinHead", true)]
    [InlineData("Std_Skin_Head", true)]
    [InlineData("skin", true)]
    [InlineData("SKIN_Head", true)]
    [InlineData("Skin2", true)]
    [InlineData("Skincare", false)]
    [InlineData("Moleskin_Trim", false)]
    [InlineData("Hair_Transparency", false)]
    [InlineData("Chair_Fabric", false)]
    [InlineData(null, false)]
    public void DefaultSkinFamily_MatchesTokensNotUnrelatedNames(string? name, bool expected)
    {
        var settings = PresentationSettings.Parse(_ => null);

        Assert.Equal(expected, BlendMaterialFamily.Matches(name, settings.SkinFamilies));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    public void Parse_EmptySkinFamilyDisablesMatching(string family)
    {
        var warnings = new List<string>();
        var settings = PresentationSettings.Parse(
            key => key == "CENTRESTAGE_SKIN_MATERIAL" ? family : null, warnings.Add);

        Assert.Equal(family, settings.SkinFamilies);
        Assert.False(BlendMaterialFamily.Matches("SkinHead", settings.SkinFamilies));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Parse_CustomSkinFamilyOverridesDefault()
    {
        var warnings = new List<string>();
        var settings = PresentationSettings.Parse(
            key => key == "CENTRESTAGE_SKIN_MATERIAL" ? " flesh , body " : null, warnings.Add);

        Assert.True(BlendMaterialFamily.Matches("FleshHead", settings.SkinFamilies));
        Assert.True(BlendMaterialFamily.Matches("Body_Torso", settings.SkinFamilies));
        Assert.False(BlendMaterialFamily.Matches("SkinHead", settings.SkinFamilies));
        Assert.Empty(warnings);
    }

    [Fact]
    public void StudioDefaultHasNoSyntheticPunctualLight()
    {
        var settings = PresentationSettings.Parse(_ => null);
        Assert.Equal(0f, settings.EffectiveFallbackLightIntensity);
        Assert.Equal(0f, settings.FallbackLightIntensity);
        Assert.Contains("CENTRESTAGE_FALLBACK_LIGHT", PresentationSettings.Variables);
    }

    [Fact]
    public void StudioCanExplicitlyRestoreSyntheticKey()
    {
        var settings = PresentationSettings.Parse(key =>
            key == "CENTRESTAGE_FALLBACK_LIGHT" ? "2.5" : null);
        Assert.Equal(2.5f, settings.EffectiveFallbackLightIntensity);
    }

    [Fact]
    public void LegacyAndDisabledPresentationRetainOriginalKeyRegardlessOfOverride()
    {
        var settings = PresentationSettings.Parse(key => key switch
        {
            "CENTRESTAGE_PRESENTATION" => "0",
            "CENTRESTAGE_FALLBACK_LIGHT" => "0",
            _ => null,
        });
        Assert.Equal(2.5f, settings.EffectiveFallbackLightIntensity);
        Assert.Equal(2.5f, PresentationSettings.Legacy.EffectiveFallbackLightIntensity);
    }

    [Fact]
    public void StudioDefaultsPreserveSurfaceDetailAndDoNotInventRefraction()
    {
        var settings = PresentationSettings.Parse(_ => null);
        Assert.Equal(0f, settings.SkinDiffusion);
        Assert.Equal(0f, settings.DefaultRefractionScale);
    }

    [Fact]
    public void LegacyDefaultRefractionRemainsPinned()
    {
        Assert.Equal(0.02f, PresentationSettings.Legacy.DefaultRefractionScale);
        Assert.Equal(0.02f, PresentationSettings.Parse(key =>
            key == "CENTRESTAGE_PRESENTATION" ? "0" : null).DefaultRefractionScale);
    }

    [Fact]
    public void SurfaceSmoothingRemainsExplicitlyAvailable()
    {
        var settings = PresentationSettings.Parse(key =>
            key == "CENTRESTAGE_SKIN_DIFFUSION" ? "0.8" : null);
        Assert.Equal(0.8f, settings.SkinDiffusion);
    }

    [Theory]
    [InlineData("CENTRESTAGE_SSS", 0f, 0f, 1f)]
    [InlineData("CENTRESTAGE_SSS_RADIUS", 0.002f, 0.0001f, 0.01f)]
    [InlineData("CENTRESTAGE_SPECULAR_AA", 0f, 0f, 1f)]
    [InlineData("CENTRESTAGE_CUBEMAP_INTENSITY", 8f, 0f, 128f)]
    public void FilteringDiagnosticsValidateFiniteRanges(string name, float fallback, float min, float max)
    {
        foreach (var value in new[] { min, max })
        {
            var parsed = PresentationSettings.Parse(key => key == name ? value.ToString(CultureInfo.InvariantCulture) : null);
            Assert.Equal(value, Number(parsed, name));
        }
        foreach (var text in new[] { "NaN", "Infinity", "-1", "9999" })
        {
            var warnings = new List<string>();
            var parsed = PresentationSettings.Parse(key => key == name ? text : null, warnings.Add);
            Assert.Equal(fallback, Number(parsed, name));
            Assert.Single(warnings);
        }
    }

    [Fact]
    public void EnvironmentAndFilteringAreOptInAndRegisteredForPinnedRejection()
    {
        var defaults = new PresentationSettings();
        Assert.False(defaults.TrilinearFiltering);
        Assert.Equal(0f, defaults.SpecularAntialiasing);
        Assert.Equal("", defaults.EnvironmentCubemap);
        var parsed = PresentationSettings.Parse(key => key switch
        {
            "CENTRESTAGE_ENV_CUBEMAP" => "/tmp/environment.gz",
            "CENTRESTAGE_TRILINEAR" => "1",
            _ => null,
        });
        Assert.True(parsed.TrilinearFiltering);
        Assert.Equal("/tmp/environment.gz", parsed.EnvironmentCubemap);
        var warnings = new List<string>();
        Assert.False(PresentationSettings.Parse(key => key == "CENTRESTAGE_TRILINEAR" ? "yes" : null, warnings.Add).TrilinearFiltering);
        Assert.Single(warnings);
        foreach (var name in new[] { "CENTRESTAGE_ENV_CUBEMAP", "CENTRESTAGE_CUBEMAP_INTENSITY", "CENTRESTAGE_SPECULAR_AA", "CENTRESTAGE_TRILINEAR" })
        {
            Assert.Contains(name, PresentationSettings.Variables);
        }
    }

    private static float Number(PresentationSettings settings, string name) => name switch
    {
        "CENTRESTAGE_SSS" => settings.SubsurfaceScattering,
        "CENTRESTAGE_SSS_RADIUS" => settings.SubsurfaceRadius,
        "CENTRESTAGE_SPECULAR_AA" => settings.SpecularAntialiasing,
        "CENTRESTAGE_CUBEMAP_INTENSITY" => settings.CubemapIntensity,
        "CENTRESTAGE_ENV_INTENSITY" => settings.EnvironmentIntensity,
        "CENTRESTAGE_FALLBACK_LIGHT" => settings.FallbackLightIntensity,
        "CENTRESTAGE_ENV_ROTATION" => settings.EnvironmentRotation,
        "CENTRESTAGE_EXPOSURE" => settings.Exposure,
        "CENTRESTAGE_BRIGHTNESS" => settings.Brightness,
        "CENTRESTAGE_CONTRAST" => settings.Contrast,
        "CENTRESTAGE_SATURATION" => settings.Saturation,
        "CENTRESTAGE_SKIN_DIFFUSION" => settings.SkinDiffusion,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown numeric setting"),
    };
}
