# Phase 13 – UI Modernisation & Theming

This plan captures the concrete work required to complete Phase 13 of the roadmap and links each sub-task to actionable engineering steps. The scope is now **pure WPF + MVVM**—no WinUI 3 harness—which means all theming, composition, and layout polish must be implemented using WPF resource dictionaries, CommunityToolkit helpers, and Windows 11 composition APIs exposed to WPF.

## Sources consulted

- [Fluent Design System guidance for Windows desktop apps](https://learn.microsoft.com/en-us/windows/apps/design)
- [WPF resource dictionary and theming best practices](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/resource-dictionaries)
- [CommunityToolkit.Mvvm + Wpf](https://learn.microsoft.com/en-us/windows/communitytoolkit/mvvm/introduction)

These references inform how we structure theme dictionaries, tie them to MVVM view models, and introduce optional Windows 11 composition features (Mica/Acrylic) without depending on WinUI-specific build tooling.

## Breakdown by roadmap item

### P13.1 – Design system audit

1. **Tokenise colours & typography**
   - Introduce/complete `Resources/Theme.Tokens.xaml` with aliases for the palette (Black Onyx, Neon Green, Signal Red, Ember Orange, Royal Purple), spacing, elevation, opacity, and typography scales.
   - Update `Theme.Default.xaml` to consume tokens instead of hard-coded hex values.
   - Add `Theme.Dark.xaml` and `Theme.HighSaturation.xaml` dictionaries that map the same tokens to variant values.
2. **Theme manager wiring**
   - Extend `App.xaml` resource merges to load dictionaries based on `ThemeManager.CurrentMode`.
   - Surface the theme choice in Settings and persist it via `SettingsHost.AppThemeMode`.

### P13.2 – WPF visual layer

1. **Retire WinUI harness**
   - Delete `src/WinUiTestHarness` and any WinUI-specific package references.
   - Replace it with an optional **WPF Style Guide** page inside the main app (debug-only) that previews HUD, settings cards, and typography against each theme.
2. **Composition effects in WPF**
   - Use `SystemBackdrop` (Mica/Acrylic) and pixel-shader blur effects for floating HUD windows while keeping pure WPF fallbacks for Windows 10.
   - Implement transition animations via `TransitioningContentControl` or `Storyboard` resources bound to MVVM state.

### P13.3 – Layout, typography & MVVM polish

1. **Responsive grids**
   - Replace fixed-width `Grid` definitions in `Views/CompareView.xaml`, `Views/Settings`, and HUD layouts with adaptive grid helpers (min column width + shared spacing tokens).
2. **Typography scale**
   - Map macOS SF Pro sizes to Segoe Fluent / Inter equivalents via the token set, then apply to `TextBlock` styles, `Button` chrome, and HUD components.
3. **MVVM cleanup**
   - Ensure every view binds through a view-model (no code-behind logic) by introducing common base classes and DI registrations for HUD, Settings, and Compare flows.

### P13.4 – Accessibility & localisation sweep

1. **Contrast checks**
   - Use the token palette to run axe / Accessibility Insights scans for light/dark/high-saturation variants.
   - Ensure focus rectangles adopt the Neon Green accent with `HighContrastAdjustment=Auto` and `FocusVisualStyle` overrides.
2. **Localisation sync**
   - Diff `Localization/*.strings` against Windows `.resw`/JSON resources and pull missing entries.
   - Add RTL smoke test cases in `docs/accessibility-checklist.md`.

### P13.5 – Brand collateral refresh

1. **Assets**
   - Capture new screenshots/tray icons from the WPF app (using the style guide page to control layouts) once the refreshed visuals land.
   - Place updated media in `assets/` and document usage in `docs/brand.md`.

## Deliverables checklist

- [x] Token resource dictionaries + theme switching infrastructure (ThemeManager loads `Theme.Tokens`, Light/Dark/HighSaturation palettes, and exposes user-facing controls).
- [x] WPF style guide/debug page showcasing acrylic HUD + adaptive layout, replacing the WinUI harness.
- [x] Updated WPF visual states (navigation rail, floating HUD, settings) now draw from the shared token set and bind through MVVM view-models.
- [x] Accessibility + localisation validation notes captured in `docs/accessibility-checklist.md`.
- [x] Refreshed asset bundle + brand guide section documented in `docs/brand.md` with new SVG renders.

Each checkbox maps to the roadmap entries and must be complete before marking P13 as delivered.
