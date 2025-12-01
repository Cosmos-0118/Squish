using ClopWindows.Core.Settings;

namespace ClopWindows.App.ViewModels;

public sealed class FloatingHudPlacementOptionViewModel
{
    public FloatingHudPlacementOptionViewModel(FloatingHudPlacement placement, string displayName)
    {
        Placement = placement;
        DisplayName = displayName;
    }

    public FloatingHudPlacement Placement { get; }

    public string DisplayName { get; }
}
