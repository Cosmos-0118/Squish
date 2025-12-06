using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using ClopWindows.App.ViewModels;
using ClopWindows.Core.Settings;

namespace ClopWindows.App.Infrastructure;

public static class ShortcutCatalog
{
    private static readonly IReadOnlyList<ShortcutDefinition> Definitions = new[]
    {
        new ShortcutDefinition(
            ShortcutId.BrowseFiles,
            ShortcutScope.MainWindow,
            "Browse for files",
            SettingsRegistry.ShortcutBrowseFiles,
            ModifierKeys.Control,
            Key.O),
        new ShortcutDefinition(
            ShortcutId.ShowSettings,
            ShortcutScope.MainWindow,
            "Open Settings",
            SettingsRegistry.ShortcutShowSettings,
            ModifierKeys.Control,
            Key.OemComma),
        new ShortcutDefinition(
            ShortcutId.ShowOnboarding,
            ShortcutScope.MainWindow,
            "Show onboarding",
            SettingsRegistry.ShortcutShowOnboarding,
            ModifierKeys.Control,
            Key.D1),
        new ShortcutDefinition(
            ShortcutId.ShowCompare,
            ShortcutScope.MainWindow,
            "Open Compare view",
            SettingsRegistry.ShortcutShowCompare,
            ModifierKeys.Control,
            Key.D2),
        new ShortcutDefinition(
            ShortcutId.ShowSettingsNavigation,
            ShortcutScope.MainWindow,
            "Jump to Settings",
            SettingsRegistry.ShortcutShowSettingsNavigation,
            ModifierKeys.Control,
            Key.D3),
        new ShortcutDefinition(
            ShortcutId.ShowMainWindow,
            ShortcutScope.Global,
            "Show Clop",
            SettingsRegistry.ShortcutShowMainWindow,
            ModifierKeys.Control | ModifierKeys.Shift,
            Key.Space,
            GlobalShortcutAction.ShowMainWindow),
        new ShortcutDefinition(
            ShortcutId.ToggleFloatingResults,
            ShortcutScope.Global,
            "Toggle floating results",
            SettingsRegistry.ShortcutToggleFloatingResults,
            ModifierKeys.Control | ModifierKeys.Shift,
            Key.F,
            GlobalShortcutAction.ToggleFloatingResults),
        new ShortcutDefinition(
            ShortcutId.ToggleClipboardOptimiser,
            ShortcutScope.Global,
            "Toggle clipboard optimiser",
            SettingsRegistry.ShortcutToggleClipboardOptimiser,
            ModifierKeys.Control | ModifierKeys.Shift,
            Key.C,
            GlobalShortcutAction.ToggleClipboardWatcher),
        new ShortcutDefinition(
            ShortcutId.ToggleAutomationPause,
            ShortcutScope.Global,
            "Pause automation",
            SettingsRegistry.ShortcutToggleAutomationPause,
            ModifierKeys.Control | ModifierKeys.Shift,
            Key.P,
            GlobalShortcutAction.ToggleAutomationPause),
        new ShortcutDefinition(
            ShortcutId.ToggleAggressiveOptimisation,
            ShortcutScope.Global,
            "Toggle aggressive optimisation",
            SettingsRegistry.ShortcutToggleAggressiveOptimisation,
            ModifierKeys.Control | ModifierKeys.Shift,
            Key.A,
            GlobalShortcutAction.ToggleAggressiveOptimisation)
    };

    private static readonly ReadOnlyDictionary<ShortcutId, ShortcutDefinition> DefinitionMap =
        new(Definitions.ToDictionary(d => d.Id));

    private static readonly ReadOnlyDictionary<string, ShortcutId> SettingNameMap =
        new(Definitions.ToDictionary(d => d.SettingKey.Name, d => d.Id, StringComparer.Ordinal));

    private static readonly Dictionary<ShortcutId, MainWindowBindingEntry> MainWindowBindings = new();

    private static bool _initialized;

    public static event EventHandler? GlobalShortcutsChanged;
    public static event EventHandler<ShortcutChangedEventArgs>? ShortcutChanged;

    public static IReadOnlyList<ShortcutDescriptor> Descriptors { get; } = Definitions
        .Select(d => new ShortcutDescriptor(
            d.Id,
            d.Scope,
            d.Description,
            ShortcutParser.ToDisplayString(d.DefaultModifiers, d.DefaultKey)))
        .ToArray();

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        SettingsHost.EnsureInitialized();
        SettingsHost.SettingChanged += OnSettingChanged;
        _initialized = true;
    }

    public static IReadOnlyList<GlobalShortcutBinding> GetGlobalShortcuts()
    {
        Initialize();

        var resolved = new List<GlobalShortcutBinding>();
        foreach (var definition in Definitions.Where(d => d.Scope == ShortcutScope.Global && d.GlobalAction.HasValue))
        {
            var binding = GetBinding(definition);
            if (binding is null)
            {
                continue;
            }

            resolved.Add(new GlobalShortcutBinding(
                definition.Id,
                definition.Description,
                binding.Modifiers,
                binding.Key,
                definition.GlobalAction!.Value));
        }

        return resolved;
    }

    public static string GetDisplayString(ShortcutId id)
    {
        Initialize();

        var definition = DefinitionMap[id];
        var binding = GetBinding(definition);
        if (binding is null)
        {
            return "Disabled";
        }

        return ShortcutParser.ToDisplayString(binding.Modifiers, binding.Key);
    }

    public static ShortcutDescriptor GetDescriptor(ShortcutId id)
    {
        Initialize();
        return Descriptors.First(d => d.Id == id);
    }

    public static ShortcutConflict? FindConflict(ShortcutId id, ModifierKeys modifiers, Key key)
    {
        Initialize();

        var definition = DefinitionMap[id];
        if (key == Key.None)
        {
            return null;
        }

        foreach (var other in Definitions)
        {
            if (other.Id == id || other.Scope != definition.Scope)
            {
                continue;
            }

            var binding = GetBinding(other);
            if (binding is null)
            {
                continue;
            }

            if (binding.Key == key && binding.Modifiers == modifiers)
            {
                return new ShortcutConflict(other.Id, other.Description);
            }
        }

        return null;
    }

    public static void UpdateShortcut(ShortcutId id, ModifierKeys modifiers, Key key)
    {
        Initialize();

        var definition = DefinitionMap[id];
        var serialized = ShortcutParser.ToStorageString(modifiers, key);
        SettingsHost.Set(definition.SettingKey, serialized);
    }

    public static void ClearShortcut(ShortcutId id)
    {
        Initialize();
        var definition = DefinitionMap[id];
        SettingsHost.Set(definition.SettingKey, string.Empty);
    }

    public static void ResetShortcut(ShortcutId id)
    {
        Initialize();
        var definition = DefinitionMap[id];
        var serialized = ShortcutParser.ToStorageString(definition.DefaultModifiers, definition.DefaultKey);
        SettingsHost.Set(definition.SettingKey, serialized);
    }

    public static void ApplyMainWindowBindings(InputBindingCollection bindings, MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(viewModel);

        Initialize();

        foreach (var definition in Definitions.Where(d => d.Scope == ShortcutScope.MainWindow))
        {
            var command = ResolveCommand(viewModel, definition.Id);
            if (command is null)
            {
                continue;
            }

            if (!MainWindowBindings.TryGetValue(definition.Id, out var entry))
            {
                entry = new MainWindowBindingEntry(bindings, command);
                MainWindowBindings[definition.Id] = entry;
            }
            else
            {
                entry.SetOwner(bindings);
            }

            ApplyBinding(entry, definition);
        }
    }

    private static void ApplyBinding(MainWindowBindingEntry entry, ShortcutDefinition definition)
    {
        var binding = GetBinding(definition);

        if (binding is null)
        {
            if (entry.Binding is not null)
            {
                entry.Owner.Remove(entry.Binding);
                entry.Binding = null;
            }
            return;
        }

        if (entry.Binding is null)
        {
            var keyBinding = new KeyBinding(entry.Command, binding.Key, binding.Modifiers);
            entry.Owner.Add(keyBinding);
            entry.Binding = keyBinding;
        }
        else
        {
            entry.Binding.Key = binding.Key;
            entry.Binding.Modifiers = binding.Modifiers;
        }
    }

    private static void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (!SettingNameMap.TryGetValue(e.Name, out var id))
        {
            return;
        }

        ShortcutChanged?.Invoke(null, new ShortcutChangedEventArgs(id));

        var definition = DefinitionMap[id];
        if (definition.Scope == ShortcutScope.MainWindow)
        {
            if (MainWindowBindings.TryGetValue(id, out var entry))
            {
                ApplyBinding(entry, definition);
            }
        }
        else if (definition.Scope == ShortcutScope.Global)
        {
            GlobalShortcutsChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    private static ICommand? ResolveCommand(MainWindowViewModel viewModel, ShortcutId id) => id switch
    {
        ShortcutId.BrowseFiles => viewModel.BrowseFilesCommand,
        ShortcutId.ShowSettings => viewModel.ShowSettingsCommand,
        ShortcutId.ShowOnboarding => viewModel.ShowOnboardingCommand,
        ShortcutId.ShowCompare => viewModel.ShowCompareCommand,
        ShortcutId.ShowSettingsNavigation => viewModel.ShowSettingsCommand,
        _ => null
    };

    private static ShortcutBinding? GetBinding(ShortcutDefinition definition)
    {
        var raw = SettingsHost.Get(definition.SettingKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (!ShortcutParser.TryParse(raw, out var modifiers, out var key))
        {
            modifiers = definition.DefaultModifiers;
            key = definition.DefaultKey;
        }

        if (key == Key.None)
        {
            return null;
        }

        return new ShortcutBinding(modifiers, key);
    }

    private sealed record ShortcutDefinition(
        ShortcutId Id,
        ShortcutScope Scope,
        string Description,
        SettingKey<string> SettingKey,
        ModifierKeys DefaultModifiers,
        Key DefaultKey,
        GlobalShortcutAction? GlobalAction = null);

    private sealed class MainWindowBindingEntry
    {
        public MainWindowBindingEntry(InputBindingCollection owner, ICommand command)
        {
            Owner = owner;
            Command = command;
        }

        public InputBindingCollection Owner { get; private set; }
        public ICommand Command { get; }
        public KeyBinding? Binding { get; set; }

        public void SetOwner(InputBindingCollection owner)
        {
            if (ReferenceEquals(owner, Owner))
            {
                return;
            }

            if (Binding is not null)
            {
                Owner.Remove(Binding);
                owner.Add(Binding);
            }

            Owner = owner;
        }
    }
}

public enum ShortcutScope
{
    MainWindow,
    Global
}

public enum ShortcutId
{
    BrowseFiles,
    ShowSettings,
    ShowOnboarding,
    ShowCompare,
    ShowSettingsNavigation,
    ShowMainWindow,
    ToggleFloatingResults,
    ToggleClipboardOptimiser,
    ToggleAutomationPause,
    ToggleAggressiveOptimisation
}

public enum GlobalShortcutAction
{
    ShowMainWindow,
    ToggleFloatingResults,
    ToggleClipboardWatcher,
    ToggleAutomationPause,
    ToggleAggressiveOptimisation
}

public sealed record GlobalShortcutBinding(ShortcutId Id, string Description, ModifierKeys Modifiers, Key Key, GlobalShortcutAction Action);

public sealed record ShortcutDescriptor(ShortcutId Id, ShortcutScope Scope, string Description, string DefaultDisplay);

public sealed record ShortcutBinding(ModifierKeys Modifiers, Key Key);

public sealed record ShortcutConflict(ShortcutId ConflictingId, string Description);

public sealed class ShortcutChangedEventArgs : EventArgs
{
    public ShortcutChangedEventArgs(ShortcutId id)
    {
        Id = id;
    }

    public ShortcutId Id { get; }
}
