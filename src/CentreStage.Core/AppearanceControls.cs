namespace CentreStage.Core;

public enum AppearanceControl
{
    EnvironmentIntensity,
    EnvironmentRotation,
    Exposure,
    SubsurfaceScattering,
    SubsurfaceRadius,
    SpecularAntialiasing,
    Saturation,
    Reset,
}

/// <summary>Top-left logical-window coordinates; framebuffer conversion is explicit.</summary>
public readonly record struct AppearanceRect(float X, float Y, float Width, float Height)
{
    public bool Contains(float x, float y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>Pure layout and interaction state shared by drawing and input; no GL dependency.</summary>
public sealed class AppearanceControls
{
    private readonly PresentationSettings _initial;
    private AppearanceControl? _dragging;
    private int _windowWidth = 1;
    private int _windowHeight = 1;

    public AppearanceControls(PresentationSettings initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _initial = initial;
        Settings = initial;
    }

    public PresentationSettings Settings { get; private set; }
    public bool Visible { get; private set; }
    public bool IsDragging => _dragging.HasValue;
    public float Scale { get; private set; } = 1f;
    public AppearanceRect Panel { get; private set; }
    public AppearanceRect Tab { get; private set; }

    public void Layout(int windowWidth, int windowHeight)
    {
        _windowWidth = Math.Max(1, windowWidth);
        _windowHeight = Math.Max(1, windowHeight);
        Scale = MathF.Min(1f, MathF.Min(_windowWidth / 320f, _windowHeight / 660f));
        Panel = new AppearanceRect(_windowWidth - (310f * Scale), 48f * Scale, 300f * Scale, 602f * Scale);
        Tab = new AppearanceRect(10f * Scale, 10f * Scale, 136f * Scale, 30f * Scale);
    }

    public AppearanceRect Rect(float x, float y, float width, float height)
        => new(Panel.X + (x * Scale), Panel.Y + (y * Scale), width * Scale, height * Scale);

    public AppearanceRect ToFramebuffer(AppearanceRect rect, int framebufferWidth, int framebufferHeight)
        => new(rect.X * framebufferWidth / _windowWidth, rect.Y * framebufferHeight / _windowHeight,
            rect.Width * framebufferWidth / _windowWidth, rect.Height * framebufferHeight / _windowHeight);

    public AppearanceRect Bounds(AppearanceControl control) => control switch
    {
        AppearanceControl.EnvironmentIntensity => Rect(20f, 84f, 260f, 22f),
        AppearanceControl.EnvironmentRotation => Rect(20f, 142f, 260f, 22f),
        AppearanceControl.Exposure => Rect(20f, 200f, 260f, 22f),
        AppearanceControl.SubsurfaceScattering => Rect(20f, 286f, 260f, 28f),
        AppearanceControl.SubsurfaceRadius => Rect(20f, 350f, 260f, 22f),
        AppearanceControl.SpecularAntialiasing => Rect(20f, 406f, 260f, 28f),
        AppearanceControl.Saturation => Rect(20f, 510f, 260f, 22f),
        _ => Rect(20f, 552f, 260f, 30f),
    };

    public static (float Min, float Max) Range(AppearanceControl control) => control switch
    {
        AppearanceControl.EnvironmentIntensity => (0f, 3f),
        AppearanceControl.EnvironmentRotation => (0f, 360f),
        AppearanceControl.Exposure => (-3f, 3f),
        AppearanceControl.SubsurfaceRadius => (0.0001f, 0.01f),
        AppearanceControl.Saturation => (0f, 1.5f),
        _ => (0f, 1f),
    };

    public float Value(AppearanceControl control) => control switch
    {
        AppearanceControl.EnvironmentIntensity => Settings.EnvironmentIntensity,
        AppearanceControl.EnvironmentRotation => Settings.EnvironmentRotation,
        AppearanceControl.Exposure => Settings.Exposure,
        AppearanceControl.SubsurfaceScattering => Settings.SubsurfaceScattering,
        AppearanceControl.SubsurfaceRadius => Settings.SubsurfaceRadius,
        AppearanceControl.SpecularAntialiasing => Settings.SpecularAntialiasing,
        AppearanceControl.Saturation => Settings.Saturation,
        _ => 0f,
    };

    public void Toggle()
    {
        Visible = !Visible;
        HandleMouseUp();
    }

    public bool Close()
    {
        if (!Visible)
        {
            return false;
        }
        Visible = false;
        HandleMouseUp();
        return true;
    }

    /// <summary>Includes capture, so wheel/camera input stays suppressed during a drag outside the panel.</summary>
    public bool Contains(float x, float y, int windowWidth, int windowHeight)
    {
        Layout(windowWidth, windowHeight);
        return IsDragging || Tab.Contains(x, y) || (Visible && Panel.Contains(x, y));
    }

    public bool HandleMouseDown(float x, float y, int windowWidth, int windowHeight)
    {
        Layout(windowWidth, windowHeight);
        if (Tab.Contains(x, y))
        {
            Toggle();
            return true;
        }
        if (IsDragging)
        {
            return true;
        }
        if (!Visible || !Panel.Contains(x, y))
        {
            return false;
        }
        foreach (var control in Enum.GetValues<AppearanceControl>())
        {
            if (!Bounds(control).Contains(x, y))
            {
                continue;
            }
            switch (control)
            {
                case AppearanceControl.Reset:
                    Settings = _initial;
                    break;
                case AppearanceControl.SubsurfaceScattering:
                    Settings = Settings with { SubsurfaceScattering = Settings.SubsurfaceScattering > 0f ? 0f : 0.65f };
                    break;
                case AppearanceControl.SpecularAntialiasing:
                    Settings = Settings with { SpecularAntialiasing = Settings.SpecularAntialiasing > 0f ? 0f : 1f };
                    break;
                default:
                    _dragging = control;
                    HandleMouseMove(x, y);
                    break;
            }
            break;
        }
        return true;
    }

    public bool HandleMouseMove(float x, float y)
    {
        if (_dragging is not { } control)
        {
            return Tab.Contains(x, y) || (Visible && Panel.Contains(x, y));
        }
        if (!float.IsFinite(x))
        {
            return true;
        }
        var bounds = Bounds(control);
        var (min, max) = Range(control);
        var value = min + (Math.Clamp((x - bounds.X) / bounds.Width, 0f, 1f) * (max - min));
        Settings = control switch
        {
            AppearanceControl.EnvironmentIntensity => Settings with { EnvironmentIntensity = value },
            AppearanceControl.EnvironmentRotation => Settings with { EnvironmentRotation = value },
            AppearanceControl.Exposure => Settings with { Exposure = value },
            AppearanceControl.SubsurfaceRadius => Settings with { SubsurfaceRadius = value },
            AppearanceControl.Saturation => Settings with { Saturation = value },
            _ => Settings,
        };
        return true;
    }

    public void HandleMouseUp() => _dragging = null;
}
