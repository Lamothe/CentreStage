using CentreStage.Core;
using Xunit;

namespace CentreStage.Core.Tests;

public class AppearanceControlsTests
{
    [Fact]
    public void CollapsedTabOpensAndBlankPanelSwallowsInput()
    {
        var controls = Create();
        Assert.False(controls.Visible);
        Assert.False(controls.Contains(20f, 100f, 1000, 800));
        Assert.True(Click(controls, controls.Tab));
        Assert.True(controls.Visible);
        Assert.True(controls.HandleMouseDown(controls.Panel.X + 2f, controls.Panel.Y + 2f, 1000, 800));
        Assert.True(controls.Contains(controls.Panel.X + 2f, controls.Panel.Y + 2f, 1000, 800));
        Assert.True(controls.Close());
        Assert.False(controls.Close());
        Assert.False(controls.Contains(controls.Panel.X + 2f, controls.Panel.Y + 2f, 1000, 800));
        Assert.True(controls.Contains(controls.Tab.X + 1f, controls.Tab.Y + 1f, 1000, 800));
    }

    [Theory]
    [InlineData(AppearanceControl.EnvironmentIntensity, 0f, 3f)]
    [InlineData(AppearanceControl.EnvironmentRotation, 0f, 360f)]
    [InlineData(AppearanceControl.Exposure, -3f, 3f)]
    [InlineData(AppearanceControl.SubsurfaceRadius, 0.0001f, 0.01f)]
    [InlineData(AppearanceControl.Saturation, 0f, 1.5f)]
    public void SlidersUseRealRangesAndCaptureOutsidePanel(AppearanceControl control, float min, float max)
    {
        var controls = Create();
        controls.Toggle();
        var bounds = controls.Bounds(control);
        Assert.True(Click(controls, bounds));
        Assert.InRange(controls.Value(control), ((min + max) / 2f) - 0.00001f, ((min + max) / 2f) + 0.00001f);
        Assert.True(controls.IsDragging);
        Assert.True(controls.HandleMouseMove(10000f, -100f));
        Assert.Equal(max, controls.Value(control), 5);
        Assert.True(controls.Contains(-100f, -100f, 1000, 800));
        Assert.True(controls.HandleMouseMove(-100f, -100f));
        Assert.Equal(min, controls.Value(control), 5);
        controls.HandleMouseUp();
        Assert.False(controls.IsDragging);
        Assert.False(controls.HandleMouseMove(-100f, -100f));
        Assert.False(controls.Contains(-100f, -100f, 1000, 800));
    }

    [Fact]
    public void TogglesAndResetPreserveSuppliedInitialSettings()
    {
        var initial = new PresentationSettings { EnvironmentCubemap = "/studio", EnvironmentIntensity = 2.2f, SubsurfaceRadius = 0.003f };
        var controls = Create(initial);
        controls.Toggle();
        Click(controls, controls.Bounds(AppearanceControl.SubsurfaceScattering));
        Assert.Equal(0.65f, controls.Settings.SubsurfaceScattering);
        Click(controls, controls.Bounds(AppearanceControl.SubsurfaceScattering));
        Assert.Equal(0f, controls.Settings.SubsurfaceScattering);
        Click(controls, controls.Bounds(AppearanceControl.SpecularAntialiasing));
        Assert.Equal(1f, controls.Settings.SpecularAntialiasing);
        Click(controls, controls.Bounds(AppearanceControl.SubsurfaceRadius));
        controls.HandleMouseUp();
        Click(controls, controls.Bounds(AppearanceControl.Reset));
        Assert.Equal(initial, controls.Settings);
        Assert.Equal("/studio", controls.Settings.EnvironmentCubemap);
    }

    [Fact]
    public void LogicalHitTestingAgreesWithFramebufferLayoutAtHiDpi()
    {
        var controls = Create();
        controls.Toggle();
        var logical = controls.Bounds(AppearanceControl.Exposure);
        var pixels = controls.ToFramebuffer(logical, 2000, 1600);
        Assert.Equal(logical.X * 2f, pixels.X);
        Assert.Equal(logical.Y * 2f, pixels.Y);
        Assert.Equal(logical.Width * 2f, pixels.Width);
        var x = (pixels.X + (pixels.Width / 2f)) / 2f;
        var y = (pixels.Y + (pixels.Height / 2f)) / 2f;
        Assert.True(controls.HandleMouseDown(x, y, 1000, 800));
        Assert.Equal(0f, controls.Settings.Exposure, 5);
    }

    [Fact]
    public void SmallWindowsKeepAllControlsVisibleAndClickable()
    {
        var controls = new AppearanceControls(new PresentationSettings());
        controls.Layout(240, 320);
        controls.Toggle();
        Assert.True(controls.Panel.X >= 0f);
        Assert.True(controls.Panel.Y + controls.Panel.Height <= 320f);
        foreach (var control in Enum.GetValues<AppearanceControl>())
        {
            var bounds = controls.Bounds(control);
            Assert.True(bounds.X >= 0f && bounds.X + bounds.Width <= 240f);
            Assert.True(bounds.Y >= 0f && bounds.Y + bounds.Height <= 320f);
            Assert.True(controls.HandleMouseDown(bounds.X + (bounds.Width / 2f), bounds.Y + (bounds.Height / 2f), 240, 320));
            controls.HandleMouseUp();
        }
    }

    [Fact]
    public void ClosingCancelsCaptureAndNonFiniteMovesDoNotCorruptSettings()
    {
        var controls = Create();
        controls.Toggle();
        Click(controls, controls.Bounds(AppearanceControl.Exposure));
        var before = controls.Settings;
        Assert.True(controls.HandleMouseMove(float.NaN, 10f));
        Assert.Equal(before, controls.Settings);
        Assert.True(controls.Close());
        Assert.False(controls.IsDragging);
        Assert.False(controls.HandleMouseMove(-10f, -10f));
    }

    private static AppearanceControls Create(PresentationSettings? initial = null)
    {
        var controls = new AppearanceControls(initial ?? new PresentationSettings());
        controls.Layout(1000, 800);
        return controls;
    }

    private static bool Click(AppearanceControls controls, AppearanceRect bounds)
        => controls.HandleMouseDown(bounds.X + (bounds.Width / 2f), bounds.Y + (bounds.Height / 2f), 1000, 800);
}
