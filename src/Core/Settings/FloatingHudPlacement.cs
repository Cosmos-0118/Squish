namespace Squish.Core.Settings;

public enum FloatingHudPlacement
{
    TopLeft = 0,
    TopCenter = 1,
    TopRight = 2,
    MiddleLeft = 3,
    MiddleRight = 4,
    BottomLeft = 5,
    BottomCenter = 6,
    BottomRight = 7,

    /// <summary>Wherever the user last dragged the HUD; the position is stored in <c>floatingHudCustomLeft/Top</c>.</summary>
    Custom = 8
}
