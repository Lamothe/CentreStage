using CentreStage.Core;
using Xunit;

namespace CentreStage.Core.Tests;

public class DemoUiTests
{
    [Fact]
    public void ButtonSitsTopRightWithMargin()
    {
        var rect = DemoUi.Button(1280, 0);
        Assert.Equal(1280f - DemoUi.Margin - DemoUi.ButtonWidth, rect.X);
        Assert.Equal(DemoUi.Margin, rect.Y);
        Assert.True(rect.Contains(rect.X, rect.Y));
        Assert.False(rect.Contains(rect.X - 1, rect.Y));
        Assert.False(rect.Contains(rect.X + rect.W, rect.Y + rect.H));
    }

    [Fact]
    public void ButtonsStackDownwardsWithoutOverlap()
    {
        var first = DemoUi.Button(800, 0);
        var second = DemoUi.Button(800, 1);
        Assert.True(second.Y >= first.Y + first.H);
        Assert.False(first.Contains(second.X, second.Y + 1));
    }

    [Fact]
    public void ButtonStaysOnFrameInTinyWindows()
    {
        var rect = DemoUi.Button(200, 0);
        Assert.True(rect.X >= 0f);
        Assert.True(rect.Contains(1f, DemoUi.Margin + 1f));
    }

    [Fact]
    public void OpenMenuClampsIntoTheFrame()
    {
        var ui = new DemoUi();
        ui.OpenMenu(1279f, 799f, 1280, 800, 3);
        var panel = ui.MenuPanel(3);
        Assert.True(panel.X + panel.W <= 1280f);
        Assert.True(panel.Y + panel.H <= 800f);
        Assert.True(ui.MenuOpen);
    }

    [Fact]
    public void HoveredItemFindsTheRowAndMissesOutside()
    {
        var ui = new DemoUi();
        ui.OpenMenu(100f, 100f, 1280, 800, 3);
        Assert.Equal(1, ui.HoveredItem(120f, 100f + DemoUi.MenuPadding + DemoUi.MenuItemHeight + 2f, 3));
        Assert.Equal(-1, ui.HoveredItem(5f, 5f, 3));
        // Below the last item but inside the panel padding: no action.
        Assert.Equal(-1, ui.HoveredItem(120f, 100f + DemoUi.MenuPadding + 3 * DemoUi.MenuItemHeight + 1f, 3));
    }

    [Fact]
    public void CloseMenuClearsTheState()
    {
        var ui = new DemoUi();
        ui.OpenMenu(50f, 50f, 1280, 800, 3);
        ui.CloseMenu();
        Assert.False(ui.MenuOpen);
    }

    [Fact]
    public void MenuActionsCoverTheThreeControlsInOrder()
    {
        Assert.Equal(new[]
        {
            DemoAction.Enlarge, DemoAction.NextView, DemoAction.BackToLive,
        }, DemoUi.MenuActions);
    }
}
