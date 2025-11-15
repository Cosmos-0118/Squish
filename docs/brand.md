# Brand & Asset Notes

Phase 13 introduced the Windows-specific flavour of the Clop design system. Use this document to keep screenshots, tray icons, and marketing surfaces in sync with the current palette.

## Palette

| Token        | Hex       | Usage                                                  |
| ------------ | --------- | ------------------------------------------------------ |
| Black Onyx   | `#050505` | Shell chrome, floating HUD backplate                   |
| Neon Green   | `#4FFFB0` | Primary accent, progress indicators, focus ring        |
| Signal Red   | `#F45D5D` | Error states, aggressive-mode warnings                 |
| Ember Orange | `#FF8A3D` | High-saturation theme accent, warning banners          |
| Royal Purple | `#8C5BFF` | Secondary accent (navigation rail hover, compare tabs) |

All resources map back to the `Brush.*` keys in `Theme.Tokens.xaml` so every WPF surface (main shell, HUD, onboarding, debug style guide) stays aligned.

## Reference renders

| File                                   | Description                                                                         |
| -------------------------------------- | ----------------------------------------------------------------------------------- |
| `assets/screenshots/settings-dark.svg` | Dark theme settings stack showing the new Appearance controls and responsive cards. |
| `assets/screenshots/hud-acrylic.svg`   | Acrylic floating HUD mock exported from the WPF style guide page.                   |

## Usage guidelines

1. Export PNG/WebP variants from the SVGs above when updating docs, the Microsoft Store listing, or website hero art.
2. Keep tray/menu icons monochrome so that the Neon Green accent is reserved for success states.
3. When capturing real screenshots, enable the ThemeManager’s Fluent visuals toggle so the acrylic glow matches the marketing renders.
4. Place updated assets in `assets/screenshots/` and reference them from release notes or docs via relative paths.
