using Squish.Core.Settings;

namespace Squish.App.ViewModels;

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
