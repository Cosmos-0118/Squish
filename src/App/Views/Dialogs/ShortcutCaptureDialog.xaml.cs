using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using ClopWindows.App.Localization;

namespace ClopWindows.App.Views.Dialogs;

public partial class ShortcutCaptureDialog : Window
{
    public ShortcutCaptureDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public string ScopeLabel { get; set; } = string.Empty;

    public string CurrentBinding { get; set; } = string.Empty;

    public string DefaultBinding { get; set; } = string.Empty;

    public ModifierKeys CapturedModifiers { get; private set; }

    public Key CapturedKey { get; private set; }

    public bool IsDisabled { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ScopeText.Text = string.IsNullOrWhiteSpace(ScopeLabel)
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, ClopStringCatalog.Get("shortcuts.capture.scopeFormat"), ScopeLabel);
        CurrentText.Text = string.Format(CultureInfo.CurrentCulture, ClopStringCatalog.Get("settings.shortcuts.currentFormat"), CurrentBinding);
        DefaultText.Text = string.Format(CultureInfo.CurrentCulture, ClopStringCatalog.Get("settings.shortcuts.defaultFormat"), DefaultBinding);
        FocusManager.SetFocusedElement(this, this);
        Keyboard.Focus(this);
    }

    private void OnDisableClick(object sender, RoutedEventArgs e)
    {
        IsDisabled = true;
        CapturedModifiers = ModifierKeys.None;
        CapturedKey = Key.None;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        CapturedModifiers = Keyboard.Modifiers;
        CapturedKey = key;
        DialogResult = true;
        e.Handled = true;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    }
}
