using System;
using System.Diagnostics;
using System.IO;
using ClopWindows.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClopWindows.App.Services;

public sealed class StartupRegistrationService : IDisposable
{
    private const string RunKeyPath = @"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "ClopWindows";

    private readonly ILogger<StartupRegistrationService> _logger;
    private readonly string _commandLine;
    private bool _disposed;

    public StartupRegistrationService(ILogger<StartupRegistrationService> logger)
    {
        _logger = logger;
        _commandLine = BuildCommandLine();
        SettingsHost.SettingChanged += OnSettingChanged;
        ApplyStartupPreference(SettingsHost.Get(SettingsRegistry.OpenAtStartup));
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Name != SettingsRegistry.OpenAtStartup.Name)
        {
            return;
        }

        var enable = e.Value is bool flag ? flag : SettingsHost.Get(SettingsRegistry.OpenAtStartup);
        ApplyStartupPreference(enable);
    }

    private void ApplyStartupPreference(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key is null)
            {
                _logger.LogWarning("Failed to open startup registry key {Path}", RunKeyPath);
                return;
            }

            if (enable)
            {
                key.SetValue(ValueName, _commandLine, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update Windows startup registration.");
        }
    }

    private static string BuildCommandLine()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            using var current = Process.GetCurrentProcess();
            executable = current.MainModule?.FileName;
        }

        executable ??= Path.Combine(AppContext.BaseDirectory, "ClopWindows.App.exe");
        var executablePath = $"\"{Path.GetFullPath(executable)}\"";
        var backgroundArg = global::ClopWindows.App.App.BackgroundLaunchArgument;
        return $"{executablePath} {backgroundArg}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SettingsHost.SettingChanged -= OnSettingChanged;
    }
}
