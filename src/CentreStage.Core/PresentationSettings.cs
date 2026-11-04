using System.Globalization;

namespace CentreStage.Core;

/// <summary>Asset-independent studio presentation; radiance is linear, exposure is in stops.</summary>
public sealed record PresentationSettings
{
    public bool Enabled { get; init; } = true;
    public float EnvironmentIntensity { get; init; } = 0.8f;
    public float EnvironmentRotation { get; init; } = 0f;
    public float Exposure { get; init; } = 0f;
    public float Brightness { get; init; } = 0.04f;
    public float Contrast { get; init; } = -0.12f;
    public float Saturation { get; init; } = 0.65f;
    public float SkinDiffusion { get; init; } = 0.8f;
    public string SkinFamilies { get; init; } = "skin";

    public static PresentationSettings Legacy { get; } = new() { Enabled = false, SkinDiffusion = 0f };

    public static readonly IReadOnlyList<string> Variables = Array.AsReadOnly(new[]
    {
        "CENTRESTAGE_PRESENTATION", "CENTRESTAGE_ENV_INTENSITY", "CENTRESTAGE_ENV_ROTATION",
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
        return defaults with
        {
            Enabled = enabled != "0",
            EnvironmentIntensity = Number("CENTRESTAGE_ENV_INTENSITY", defaults.EnvironmentIntensity, 0f, 16f),
            EnvironmentRotation = Number("CENTRESTAGE_ENV_ROTATION", defaults.EnvironmentRotation, -360f, 360f),
            Exposure = Number("CENTRESTAGE_EXPOSURE", defaults.Exposure, -8f, 8f),
            Brightness = Number("CENTRESTAGE_BRIGHTNESS", defaults.Brightness, -0.5f, 0.5f),
            Contrast = Number("CENTRESTAGE_CONTRAST", defaults.Contrast, -1f, 1f),
            Saturation = Number("CENTRESTAGE_SATURATION", defaults.Saturation, 0f, 2f),
            SkinDiffusion = Number("CENTRESTAGE_SKIN_DIFFUSION", defaults.SkinDiffusion, 0f, 1f),
            SkinFamilies = read("CENTRESTAGE_SKIN_MATERIAL") ?? defaults.SkinFamilies,
        };
    }
}
