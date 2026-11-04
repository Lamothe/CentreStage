using System.Globalization;

namespace CentreStage.Core;

/// <summary>Asset-independent studio presentation; radiance is linear, exposure is in stops.</summary>
public sealed record PresentationSettings
{
    public bool Enabled { get; init; } = true;
    public float EnvironmentIntensity { get; init; } = 1.12f;
    public float EnvironmentRotation { get; init; } = 0f;
    public float FallbackLightIntensity { get; init; } = 0f;
    public float Exposure { get; init; } = 0f;
    public float Brightness { get; init; } = 0.04f;
    public float Contrast { get; init; } = -0.12f;
    public float Saturation { get; init; } = 0.73f;
    public float SpecularAntialiasing { get; init; } = 0f;
    public string EnvironmentCubemap { get; init; } = "";
    public float CubemapIntensity { get; init; } = 8f;
    public bool TrilinearFiltering { get; init; } = false;
    public float SubsurfaceScattering { get; init; } = 0f;
    public float SubsurfaceRadius { get; init; } = 0.002f;
    public float SkinDiffusion { get; init; } = 0f;
    public string SkinFamilies { get; init; } = "skin,head,face,body";

    /// <summary>Do not invent a screen-space optical distance for live glass.
    /// The legacy offset is retained for pinned evidence.</summary>
    public float DefaultRefractionScale => Enabled ? 0f : 0.02f;

    /// <summary>Do not add a synthetic hard key on top of IBL unless requested.
    /// Legacy/pinned views retain their original light intensity.</summary>
    public float EffectiveFallbackLightIntensity => Enabled ? FallbackLightIntensity : 2.5f;

    public static PresentationSettings Legacy { get; } = new() { Enabled = false, SkinDiffusion = 0f };

    public static readonly IReadOnlyList<string> Variables = Array.AsReadOnly(new[]
    {
        "CENTRESTAGE_SSS", "CENTRESTAGE_SSS_RADIUS", "CENTRESTAGE_ENV_CUBEMAP", "CENTRESTAGE_CUBEMAP_INTENSITY", "CENTRESTAGE_SPECULAR_AA", "CENTRESTAGE_TRILINEAR", "CENTRESTAGE_FALLBACK_LIGHT", "CENTRESTAGE_PRESENTATION", "CENTRESTAGE_ENV_INTENSITY", "CENTRESTAGE_ENV_ROTATION",
        "CENTRESTAGE_EXPOSURE", "CENTRESTAGE_BRIGHTNESS", "CENTRESTAGE_CONTRAST",
        "CENTRESTAGE_SATURATION", "CENTRESTAGE_SKIN_DIFFUSION", "CENTRESTAGE_SKIN_MATERIAL",
    });

    public static PresentationSettings FromEnvironment() => Parse(Environment.GetEnvironmentVariable, Console.Error.WriteLine);

    public static PresentationSettings Parse(Func<string, string?> read, Action<string>? warn = null)
    {
        var defaults = new PresentationSettings();
        float Number(string name, float fallback, float min, float max)
        {
            var text = read(name);
            if (string.IsNullOrEmpty(text))
            {
                return fallback;
            }
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && float.IsFinite(value) && value >= min && value <= max)
            {
                return value;
            }
            warn?.Invoke($"warning: {name}={text} is outside [{min}, {max}]; using {fallback}");
            return fallback;
        }

        var enabled = read("CENTRESTAGE_PRESENTATION");
        if (enabled is not (null or "" or "0" or "1"))
        {
            warn?.Invoke("warning: CENTRESTAGE_PRESENTATION expects 0 or 1; using 1");
        }
        var trilinear = read("CENTRESTAGE_TRILINEAR");
        if (trilinear is not (null or "" or "0" or "1"))
        {
            warn?.Invoke("warning: CENTRESTAGE_TRILINEAR expects 0 or 1; using 0");
        }
        return defaults with
        {
            Enabled = enabled != "0",
            SubsurfaceScattering = Number("CENTRESTAGE_SSS", defaults.SubsurfaceScattering, 0f, 1f),
            SubsurfaceRadius = Number("CENTRESTAGE_SSS_RADIUS", defaults.SubsurfaceRadius, 0.0001f, 0.01f),
            EnvironmentCubemap = read("CENTRESTAGE_ENV_CUBEMAP") ?? "",
            CubemapIntensity = Number("CENTRESTAGE_CUBEMAP_INTENSITY", defaults.CubemapIntensity, 0f, 128f),
            SpecularAntialiasing = Number("CENTRESTAGE_SPECULAR_AA", defaults.SpecularAntialiasing, 0f, 1f),
            TrilinearFiltering = trilinear == "1",
            EnvironmentIntensity = Number("CENTRESTAGE_ENV_INTENSITY", defaults.EnvironmentIntensity, 0f, 16f),
            EnvironmentRotation = Number("CENTRESTAGE_ENV_ROTATION", defaults.EnvironmentRotation, -360f, 360f),
            FallbackLightIntensity = Number("CENTRESTAGE_FALLBACK_LIGHT", defaults.FallbackLightIntensity, 0f, 16f),
            Exposure = Number("CENTRESTAGE_EXPOSURE", defaults.Exposure, -8f, 8f),
            Brightness = Number("CENTRESTAGE_BRIGHTNESS", defaults.Brightness, -0.5f, 0.5f),
            Contrast = Number("CENTRESTAGE_CONTRAST", defaults.Contrast, -1f, 1f),
            Saturation = Number("CENTRESTAGE_SATURATION", defaults.Saturation, 0f, 2f),
            SkinDiffusion = Number("CENTRESTAGE_SKIN_DIFFUSION", defaults.SkinDiffusion, 0f, 1f),
            SkinFamilies = read("CENTRESTAGE_SKIN_MATERIAL") ?? defaults.SkinFamilies,
        };
    }
}
