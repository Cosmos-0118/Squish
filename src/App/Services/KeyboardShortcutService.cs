using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Squish.App.Infrastructure;
using Squish.App.Localization;
using Squish.App.ViewModels;
using Squish.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Squish.App.Services;

public sealed class KeyboardShortcutService : IDisposable
{
    private static readonly SettingKey<bool>[] AggressiveOptimisationSettings =
    {
        SettingsRegistry.UseAggressiveOptimisationMp4,
        SettingsRegistry.UseAggressiveOptimisationJpeg,
        SettingsRegistry.UseAggressiveOptimisationPng,
        SettingsRegistry.UseAggressiveOptimisationGif,
    };

    private readonly MainWindowManager _mainWindowManager;
    private readonly FloatingHudController _hudController;
    private readonly ILogger<KeyboardShortcutService> _logger;
    private readonly List<HotkeyRegistration> _registrations = new();
    private bool _disposed;
    private int _nextId;
    private HotkeyWindow? _hotkeyWindow;

    private sealed record HotkeyRegistration(int Id, GlobalShortcutAction Action);

    public KeyboardShortcutService(
        MainWindowManager mainWindowManager,
        FloatingHudController hudController,
        ILogger<KeyboardShortcutService> logger)
    {
        _mainWindowManager = mainWindowManager;
        _hudController = hudController;
        _logger = logger;
        ShortcutCatalog.Initialize();
        ShortcutCatalog.GlobalShortcutsChanged += OnGlobalShortcutsChanged;
    }

    /// <summary>
    /// Registers the global hotkeys against a message-only window, so no WPF window (and no Direct3D device) has to
    /// exist for them to work. Call on the UI thread.
    /// </summary>
    public void Start()
    {
        if (_disposed || _hotkeyWindow is not null)
        {
            return;
        }

        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;
        RebuildHotkeys(_hotkeyWindow.Handle);
    }

    private void RegisterHotKey(IntPtr handle, GlobalShortcutBinding definition)
    {
        var id = ++_nextId;
        var modifierFlags = ConvertModifiers(definition.Modifiers);
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(definition.Key);
        if (!NativeMethods.RegisterHotKey(handle, id, modifierFlags, virtualKey))
        {
            _logger.LogWarning(
                "Unable to register hotkey {Description} ({Gesture}). Error {Error}",
                definition.Description,
                ShortcutParser.ToDisplayString(definition.Modifiers, definition.Key),
                Marshal.GetLastWin32Error());
            return;
        }

        _registrations.Add(new HotkeyRegistration(id, definition.Action));
        _logger.LogInformation(
            "Registered hotkey {Description} as {Gesture}.",
            definition.Description,
            ShortcutParser.ToDisplayString(definition.Modifiers, definition.Key));
    }

    private void OnHotkeyPressed(int id)
    {
        var registration = _registrations.FirstOrDefault(r => r.Id == id);
        if (registration is not null)
        {
            HandleAction(registration.Action);
        }
    }

    private void HandleAction(GlobalShortcutAction action)
    {
        switch (action)
        {
            case GlobalShortcutAction.ShowMainWindow:
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(BringMainWindowToFront));
                break;
            case GlobalShortcutAction.ToggleFloatingResults:
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(ToggleFloatingResults));
                break;
            case GlobalShortcutAction.ToggleClipboardWatcher:
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(ToggleClipboardWatcher));
                break;
            case GlobalShortcutAction.ToggleAutomationPause:
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(ToggleAutomationPause));
                break;
            case GlobalShortcutAction.ToggleAggressiveOptimisation:
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(ToggleAggressiveOptimisation));
                break;
            case GlobalShortcutAction.ToggleMetadataPreservation:
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(ToggleMetadataPreservation));
                break;
        }
    }

    private void BringMainWindowToFront()
    {
        try
        {
            _mainWindowManager.Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bring main window to front via hotkey.");
        }
    }

    private void ToggleFloatingResults()
    {
        try
        {
            var enabled = SettingsHost.Get(SettingsRegistry.EnableFloatingResults);
            SettingsHost.Set(SettingsRegistry.EnableFloatingResults, !enabled);
            if (enabled)
            {
                _hudController.Hide();
            }
            else
            {
                _hudController.Show();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle floating results via hotkey.");
        }
    }

    private void ToggleClipboardWatcher()
    {
        try
        {
            var enabled = SettingsHost.Get(SettingsRegistry.EnableClipboardOptimiser);
            SettingsHost.Set(SettingsRegistry.EnableClipboardOptimiser, !enabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle clipboard watcher via hotkey.");
        }
    }

    private void ToggleAutomationPause()
    {
        try
        {
            var paused = SettingsHost.Get(SettingsRegistry.PauseAutomaticOptimisations);
            var newValue = !paused;
            SettingsHost.Set(SettingsRegistry.PauseAutomaticOptimisations, newValue);

            var title = SquishStringCatalog.Get("hud.notification.automation.title");
            var messageKey = newValue
                ? "hud.notification.automation.paused"
                : "hud.notification.automation.resumed";
            var message = SquishStringCatalog.Get(messageKey);
            var style = newValue ? FloatingHudNotificationStyle.Warning : FloatingHudNotificationStyle.Success;
            _hudController.ShowNotification(title, message, style);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle automatic optimisations via hotkey.");
        }
    }

    private void ToggleAggressiveOptimisation()
    {
        try
        {
            var currentlyEnabled = AggressiveOptimisationSettings.All(key => SettingsHost.Get(key));
            var newValue = !currentlyEnabled;
            foreach (var key in AggressiveOptimisationSettings)
            {
                SettingsHost.Set(key, newValue);
            }

            var title = SquishStringCatalog.Get("hud.notification.aggressive.title");
            var messageKey = newValue
                ? "hud.notification.aggressive.enabled"
                : "hud.notification.aggressive.disabled";
            var message = SquishStringCatalog.Get(messageKey);
            var style = newValue ? FloatingHudNotificationStyle.Success : FloatingHudNotificationStyle.Info;
            _hudController.ShowNotification(title, message, style);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle aggressive optimisation via hotkey.");
        }
    }

    private void ToggleMetadataPreservation()
    {
        try
        {
            var strip = SettingsHost.Get(SettingsRegistry.StripMetadata);
            var newStripValue = !strip;
            SettingsHost.Set(SettingsRegistry.StripMetadata, newStripValue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle metadata preservation via hotkey.");
        }
    }

    private static uint ConvertModifiers(ModifierKeys modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= NativeMethods.MOD_ALT;
        }
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= NativeMethods.MOD_CONTROL;
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= NativeMethods.MOD_SHIFT;
        }
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= NativeMethods.MOD_WIN;
        }

        // Prevent auto-repeat when user keeps the key pressed.
        result |= NativeMethods.MOD_NOREPEAT;
        return result;
    }

    private void RebuildHotkeys(IntPtr handle)
    {
        UnregisterHotkeys(handle);
        _registrations.Clear();
        _nextId = 0;

        foreach (var shortcut in ShortcutCatalog.GetGlobalShortcuts())
        {
            RegisterHotKey(handle, shortcut);
        }
    }

    private void OnGlobalShortcutsChanged(object? sender, EventArgs e)
    {
        if (_hotkeyWindow is null)
        {
            return;
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || _hotkeyWindow is null || _hotkeyWindow.Handle == IntPtr.Zero)
                {
                    return;
                }

                RebuildHotkeys(_hotkeyWindow.Handle);
            }));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        ShortcutCatalog.GlobalShortcutsChanged -= OnGlobalShortcutsChanged;

        if (_hotkeyWindow is not null)
        {
            _hotkeyWindow.HotkeyPressed -= OnHotkeyPressed;
            UnregisterHotkeys(_hotkeyWindow.Handle);
            _hotkeyWindow.Dispose();
            _hotkeyWindow = null;
        }

        _registrations.Clear();
    }

    private void UnregisterHotkeys(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            foreach (var registration in _registrations)
            {
                NativeMethods.UnregisterHotKey(handle, registration.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error unregistering hotkeys.");
        }
    }

    private static class NativeMethods
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
