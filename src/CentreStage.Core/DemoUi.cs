namespace CentreStage.Core;

/// <summary>What the demo's mouse controls can trigger.</summary>
public enum DemoAction
{
    Enlarge,
    NextView,
    BackToLive,
}

/// <summary>A rectangle in framebuffer pixels (top-left origin).</summary>
public readonly record struct UiRect(float X, float Y, float W, float H)
{
    public bool Contains(float x, float y)
        => x >= X && x < X + W && y >= Y && y < Y + H;
}

/// <summary>
/// The demo's mouse affordances: the stacked buttons at the top-right and
/// the right-click context menu. Pure geometry and state - no rendering -
/// so the hit-testing is unit-testable; the seam supplies the wording and
/// maps hits to actions.
/// </summary>
public sealed class DemoUi
{
    public const float Margin = 14f;
    public const float ButtonWidth = 320f;
    public const float ButtonHeight = 38f;
    public const float ButtonGap = 8f;
    public const float MenuWidth = 340f;
    public const float MenuPadding = 6f;
    public const float MenuItemHeight = 34f;

    /// <summary>The menu's item order (the seam maps index to wording).</summary>
    public static readonly DemoAction[] MenuActions =
    {
        DemoAction.Enlarge,
        DemoAction.NextView,
        DemoAction.BackToLive,
    };

    private bool _menuOpen;
    private float _menuX;
    private float _menuY;

    public bool MenuOpen => _menuOpen;
    public float MenuX => _menuX;
    public float MenuY => _menuY;

    /// <summary>
    /// The stacked top-right buttons; index 0 is topmost. Clamped onto the
    /// frame so tiny windows still get a clickable (if overlapping) target.
    /// </summary>
    public static UiRect Button(int frameWidth, int index)
        => new(Math.Max(0f, frameWidth - Margin - ButtonWidth),
            Margin + index * (ButtonHeight + ButtonGap),
            ButtonWidth, ButtonHeight);

    /// <summary>Opens the context menu at a click point, clamped into the frame.</summary>
    public void OpenMenu(float x, float y, int frameWidth, int frameHeight, int itemCount)
    {
        var height = MenuPadding * 2f + itemCount * MenuItemHeight;
        _menuX = Math.Clamp(x, 0f, Math.Max(0f, frameWidth - MenuWidth));
        _menuY = Math.Clamp(y, 0f, Math.Max(0f, frameHeight - height));
        _menuOpen = true;
    }

    public void CloseMenu() => _menuOpen = false;

    public UiRect MenuPanel(int itemCount)
        => new(_menuX, _menuY, MenuWidth, MenuPadding * 2f + itemCount * MenuItemHeight);

    public UiRect MenuItem(int index)
        => new(_menuX + MenuPadding,
            _menuY + MenuPadding + index * MenuItemHeight,
            MenuWidth - MenuPadding * 2f, MenuItemHeight);

    /// <summary>The menu item under the cursor, or -1 when outside the items.</summary>
    public int HoveredItem(float x, float y, int itemCount)
    {
        for (var i = 0; i < itemCount; ++i)
        {
            if (MenuItem(i).Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }
}
