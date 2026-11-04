using System.Globalization;
using System.Numerics;
using CentreStage.Core;

namespace CentreStage;

/// <summary>A live, non-owning overlay; the host retains and disposes StatusOverlay.</summary>
internal sealed class AppearancePanel
{
    private static readonly Vector4 Background = new(0.055f, 0.067f, 0.085f, 0.97f);
    private static readonly Vector4 Surface = new(0.12f, 0.15f, 0.19f, 1f);
    private static readonly Vector4 Text = new(0.9f, 0.93f, 0.96f, 1f);
    private static readonly Vector4 Muted = new(0.57f, 0.65f, 0.73f, 1f);
    private static readonly Vector4 Accent = new(0.35f, 0.72f, 0.83f, 1f);
    private readonly StatusOverlay _overlay;
    private readonly AppearanceControls _controls;
    private int _windowWidth;
    private int _windowHeight;

    public AppearancePanel(StatusOverlay overlay, PresentationSettings initial)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        _overlay = overlay;
        _controls = new AppearanceControls(initial);
    }

    public PresentationSettings Settings => _controls.Settings;
    public bool Visible => _controls.Visible;
    public void Toggle() => _controls.Toggle();
    public bool Close() => _controls.Close();
    public void HandleMouseUp() => _controls.HandleMouseUp();
    public bool HandleMouseMove(float x, float y) => _controls.HandleMouseMove(x, y);

    public bool HandleMouseDown(float x, float y, int windowWidth, int windowHeight)
    {
        SetWindowSize(windowWidth, windowHeight);
        return _controls.HandleMouseDown(x, y, windowWidth, windowHeight);
    }

    /// <summary>Call before routing wheel input; also supplies logical dimensions for HiDPI drawing.</summary>
    public bool Contains(float x, float y, int windowWidth, int windowHeight)
    {
        SetWindowSize(windowWidth, windowHeight);
        return _controls.Contains(x, y, windowWidth, windowHeight);
    }

    private void SetWindowSize(int width, int height)
    {
        _windowWidth = Math.Max(1, width);
        _windowHeight = Math.Max(1, height);
        _controls.Layout(_windowWidth, _windowHeight);
    }

    public void Draw(int framebufferWidth, int framebufferHeight)
    {
        if (framebufferWidth <= 0 || framebufferHeight <= 0)
        {
            return;
        }
        // Until the host supplies logical dimensions via Contains/input, assume 1:1.
        _controls.Layout(_windowWidth > 0 ? _windowWidth : framebufferWidth,
            _windowHeight > 0 ? _windowHeight : framebufferHeight);
        _overlay.DrawPanel(framebufferWidth, framebufferHeight, overlay =>
        {
            AppearanceRect Pixels(AppearanceRect rect) => _controls.ToFramebuffer(rect, framebufferWidth, framebufferHeight);
            void Fill(AppearanceRect rect, Vector4 colour)
            {
                var p = Pixels(rect);
                overlay.FillRect(p.X, p.Y, p.Width, p.Height, colour);
            }
            void Label(string text, AppearanceRect rect, float size, Vector4 colour, bool right = false)
            {
                var p = Pixels(rect);
                var fontSize = size * p.Height / rect.Height * _controls.Scale;
                // Keep arbitrary material-family names inside the narrow panel.
                while (text.Length > 0 && overlay.TextWidth(text, fontSize) > p.Width)
                {
                    text = text[..^1];
                }
                overlay.DrawText(text, right ? p.X + p.Width - overlay.TextWidth(text, fontSize) : p.X, p.Y, fontSize, colour);
            }
            void Heading(string text, float y)
            {
                Label(text, _controls.Rect(20f, y, 260f, 22f), 17f, Accent);
            }
            void Slider(AppearanceControl control, string title, string format)
            {
                var bounds = _controls.Bounds(control);
                var value = _controls.Value(control);
                var label = bounds with { Y = bounds.Y - (23f * _controls.Scale) };
                Label(title, label, 14f, Text);
                Label(value.ToString(format, CultureInfo.InvariantCulture), label, 14f, Muted, right: true);
                var (min, max) = AppearanceControls.Range(control);
                var fraction = Math.Clamp((value - min) / (max - min), 0f, 1f);
                var track = bounds with { Y = bounds.Y + (9f * _controls.Scale), Height = 4f * _controls.Scale };
                Fill(track, Surface);
                Fill(track with { Width = track.Width * fraction }, Accent);
                Fill(new AppearanceRect(bounds.X + (bounds.Width * fraction) - (3f * _controls.Scale),
                    bounds.Y + (5f * _controls.Scale), 6f * _controls.Scale, 12f * _controls.Scale), Text);
            }
            void ToggleControl(AppearanceControl control, string title)
            {
                var bounds = _controls.Bounds(control);
                Label(title, bounds with { Width = bounds.Width - (52f * _controls.Scale), Y = bounds.Y + (4f * _controls.Scale) }, 14f, Text);
                var on = _controls.Value(control) > 0f;
                var button = bounds with { X = bounds.X + bounds.Width - (46f * _controls.Scale), Width = 46f * _controls.Scale };
                Fill(button, on ? Accent : Surface);
                Label(on ? " On" : " Off", button with { Y = button.Y + (4f * _controls.Scale) }, 14f, on ? Background : Text);
            }

            Fill(_controls.Tab, Visible ? Surface : Background);
            Label(Visible ? "Appearance  -" : "Appearance  +",
                _controls.Tab with { X = _controls.Tab.X + (10f * _controls.Scale), Y = _controls.Tab.Y + (5f * _controls.Scale), Width = 120f * _controls.Scale }, 15f, Text);
            if (!Visible)
            {
                return;
            }
            Fill(_controls.Panel, Background);
            Heading("Lighting", 20f);
            Slider(AppearanceControl.EnvironmentIntensity, "Environment", "0.00");
            Slider(AppearanceControl.EnvironmentRotation, "Rotation (degrees)", "0");
            Slider(AppearanceControl.Exposure, "Exposure (stops)", "+0.00;-0.00;0.00");
            Heading("Skin", 240f);
            Label($"Material family: {Settings.SkinFamilies}", _controls.Rect(20f, 264f, 260f, 18f), 12f, Muted);
            ToggleControl(AppearanceControl.SubsurfaceScattering, "Subsurface scattering");
            Slider(AppearanceControl.SubsurfaceRadius, "Radius (model proportion)", "0.0000");
            Label("Diffuse only; reflections stay sharp", _controls.Rect(20f, 382f, 260f, 18f), 12f, Muted);
            ToggleControl(AppearanceControl.SpecularAntialiasing, "Specular antialiasing");
            Heading("Colour", 462f);
            Slider(AppearanceControl.Saturation, "Saturation", "0.00");
            var reset = _controls.Bounds(AppearanceControl.Reset);
            Fill(reset, Surface);
            Label("Reset appearance", reset with { X = reset.X + (12f * _controls.Scale), Y = reset.Y + (5f * _controls.Scale), Width = reset.Width - (24f * _controls.Scale) }, 14f, Text);
        });
    }
}
