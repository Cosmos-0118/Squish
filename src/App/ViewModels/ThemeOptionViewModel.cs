using ClopWindows.Core.Settings;

namespace ClopWindows.App.ViewModels;

public sealed class ThemeOptionViewModel
{
    public ThemeOptionViewModel(AppThemeMode mode, string displayName)
    {
        Mode = mode;
        DisplayName = displayName;
    }

    public AppThemeMode Mode { get; }

    public string DisplayName { get; }
}
