# Localization & Accessibility

Windows now loads UI strings from the shared catalogue inside `src/App/Localization`, keeping wording identical to the macOS app. During development the WPF shell reads `Localization/<culture>.json` from the build output, while production builds embed the English fallback (`strings.en.json`).

- Add or update strings in `src/App/Localization/en.json` first. macOS can import the same file, keeping parity automatic.
- Optional localisations live alongside the English file (`fr.json`, `de.json`, …). When present, the Windows app picks the best match for `CurrentUICulture` and falls back to English.
- In XAML, reference strings with `{loc:Loc key}`. In C#, call `ClopStringCatalog.Get("key")`.

## High-Contrast Support

Theme brushes are centralised in `App/Resources/Theme.Default.xaml`. `ThemeManager` swaps them for `Theme.HighContrast.xaml` whenever Windows switches into High Contrast mode. Always bind colours through the shared brush keys (for example `Background="{DynamicResource Brush.SurfaceBackground}"`). Avoid hard-coded hex values so accessibility tweaks remain automatic.

## Screen Reader Hints

Key surfaces expose `AutomationProperties.*` metadata:

- Navigation (`MainWindow.xaml`) advertises `navigation.list.help` so screen readers announce context.
- The floating HUD exposes a polite live region with `hud.automation.*` strings.
- Settings text boxes, shortcut lists, and compare results now describe their intent for assistive tech.

Add similar hints whenever you introduce new interactive elements so accessibility remains complete.
