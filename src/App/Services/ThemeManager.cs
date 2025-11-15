using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ClopWindows.Core.Settings;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace ClopWindows.App.Services;

public sealed class ThemeManager : IDisposable
{
    private static readonly IReadOnlyDictionary<AppThemeMode, string> ThemeSources = new Dictionary<AppThemeMode, string>
    {
        { AppThemeMode.Light, "Resources/Theme.Default.xaml" },
        { AppThemeMode.Dark, "Resources/Theme.Dark.xaml" },
        { AppThemeMode.HighSaturation, "Resources/Theme.HighSaturation.xaml" }
    };

    private readonly Dictionary<AppThemeMode, ResourceDictionary> _themeCache = new();
    private readonly ResourceDictionary _tokensDictionary;
    private readonly ResourceDictionary _highContrastTheme;
    private ResourceDictionary? _activeTheme;
    private bool _isHighContrast;
    private AppThemeMode _requestedMode = AppThemeMode.FollowSystem;
    private bool _disposed;

    public ThemeManager()
    {
        if (Application.Current is null)
        {
            throw new InvalidOperationException("Application resources are not available.");
        }

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        _tokensDictionary = ResolveDictionary(dictionaries, "Theme.Tokens.xaml");
        _highContrastTheme = LoadTheme("Resources/Theme.HighContrast.xaml");

        SettingsHost.EnsureInitialized();
        SettingsHost.SettingChanged += OnSettingChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        ApplyCurrentTheme();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SettingsHost.SettingChanged -= OnSettingChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _disposed = true;
    }

    private void ApplyCurrentTheme()
    {
        var mode = SettingsHost.Get(SettingsRegistry.AppThemeMode);
        Apply(mode, SystemParameters.HighContrast);
    }

    private void Apply(AppThemeMode requestedMode, bool highContrast)
    {
        if (Application.Current is null)
        {
            return;
        }

        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => Apply(requestedMode, highContrast));
            return;
        }

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        EnsureMerged(dictionaries, _tokensDictionary);

        if (highContrast)
        {
            if (!_isHighContrast || _activeTheme != _highContrastTheme)
            {
                ReplaceThemeDictionary(_highContrastTheme);
                _isHighContrast = true;
            }
            return;
        }

        _isHighContrast = false;
        _requestedMode = requestedMode;
        var effectiveMode = ResolveEffectiveMode(requestedMode);
        var dictionary = GetThemeDictionary(effectiveMode);
        if (_activeTheme == dictionary)
        {
            return;
        }

        ReplaceThemeDictionary(dictionary);
    }

    private void ReplaceThemeDictionary(ResourceDictionary dictionary)
    {
        if (Application.Current is null)
        {
            return;
        }

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries.Remove(_highContrastTheme);
        if (_activeTheme is not null)
        {
            dictionaries.Remove(_activeTheme);
        }

        if (!dictionaries.Contains(dictionary))
        {
            dictionaries.Add(dictionary);
        }

        _activeTheme = dictionary;
    }

    private static ResourceDictionary ResolveDictionary(IList<ResourceDictionary> dictionaries, string resourceName)
    {
        var existing = dictionaries.FirstOrDefault(d => d.Source?.OriginalString?.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase) == true);
        if (existing is not null)
        {
            return existing;
        }

        var dictionary = LoadTheme($"Resources/{resourceName}");
        dictionaries.Insert(0, dictionary);
        return dictionary;
    }

    private static void EnsureMerged(IList<ResourceDictionary> dictionaries, ResourceDictionary dictionary)
    {
        if (!dictionaries.Contains(dictionary))
        {
            dictionaries.Insert(0, dictionary);
        }
    }

    private static ResourceDictionary LoadTheme(string resourcePath)
    {
        var uri = new Uri($"pack://application:,,,/ClopWindows;component/{resourcePath}", UriKind.Absolute);
        return new ResourceDictionary { Source = uri };
    }

    private ResourceDictionary GetThemeDictionary(AppThemeMode mode)
    {
        if (!_themeCache.TryGetValue(mode, out var dictionary))
        {
            if (!ThemeSources.TryGetValue(mode, out var source))
            {
                source = ThemeSources[AppThemeMode.Light];
            }

            dictionary = LoadTheme(source);
            _themeCache[mode] = dictionary;
        }

        return dictionary;
    }

    private static AppThemeMode ResolveEffectiveMode(AppThemeMode requestedMode)
    {
        if (requestedMode == AppThemeMode.HighSaturation)
        {
            return AppThemeMode.HighSaturation;
        }

        if (requestedMode == AppThemeMode.Dark)
        {
            return AppThemeMode.Dark;
        }

        if (requestedMode == AppThemeMode.Light)
        {
            return AppThemeMode.Light;
        }

        return SystemPrefersLightColors() ? AppThemeMode.Light : AppThemeMode.Dark;
    }

    private static bool SystemPrefersLightColors()
    {
        try
        {
            const string personalizeKey = @"HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";
            var value = Registry.GetValue(personalizeKey, "AppsUseLightTheme", 1);
            return value is int intValue && intValue > 0;
        }
        catch
        {
            return true;
        }
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (string.Equals(e.Name, SettingsRegistry.AppThemeMode.Name, StringComparison.Ordinal))
        {
            ApplyCurrentTheme();
        }
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SystemParameters.HighContrast), StringComparison.Ordinal))
        {
            Apply(_requestedMode, SystemParameters.HighContrast);
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
        {
            if (_requestedMode == AppThemeMode.FollowSystem)
            {
                ApplyCurrentTheme();
            }
        }
    }
}
