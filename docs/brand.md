# Brand & Asset Notes

Phase 13 introduced the Windows-specific flavour of the Squish design system. Use this document to keep screenshots, tray icons, and marketing surfaces in sync with the current palette.

## Palette

The app shell is deliberately neutral (Vercel/Linear style): near-black or white surfaces, 1px hairline borders,
and monochrome primary buttons. Neon Green is the one brand colour and is reserved for the logo tile and success states.

| Token        | Hex       | Usage                                                  |
| ------------ | --------- | ------------------------------------------------------ |
| Neon Green   | `#4FFFB0` | Logo tile, success text/badges in dark mode            |
| Ink          | `#171717` | Light-theme text and primary buttons                   |
| Night        | `#0A0A0A` | Dark-theme window background                           |
| Signal Red   | `#F45D5D` | Error states, aggressive-mode warnings                 |
| Ember Orange | `#FF8A3D` | Warnings, high-saturation theme accent                 |
| Royal Purple | `#8C5BFF` | High-saturation theme primary                          |

Every colour the UI uses is a `Brush.*` key defined in each `Resources/Theme.*.xaml` (Light, Dark, High saturation,
High contrast). Spacing, radii, fonts and sidebar widths live in `Theme.Tokens.xaml`; shared text, card, badge and
option-card styles live in `Resources/Styles.xaml`. Views should only reference those keys, never raw hex values.

## App icon

Squish has its own name and artwork. It does not use the Clop name, logo, or any images from Clop for Mac.

The mark is a pill squeezed between two inward chevrons on a green rounded tile. It is vector art, so the app draws it
directly (`Image.SquishLogo` in `src/App/Resources/Brand.xaml`) and stays crisp at every size.

| File                                    | Role                                                                                         |
| --------------------------------------- | -------------------------------------------------------------------------------------------- |
| `src/App/Assets/Brand/squish-icon.svg`  | Vector source of the mark. Keep in sync with `Resources/Brand.xaml`.                         |
| `src/App/Assets/Brand/squish-icon.png`  | 1024×1024 render of the mark, used to build the ICO.                                         |
| `installer/assets/Squish.ico`           | Generated from the PNG (16–256 px). Used for the exe, window, tray, and installer.            |

To change the icon, update the SVG and `Brand.xaml`, export a 1024×1024 PNG over `squish-icon.png`, and run:

```powershell
pwsh scripts/build-icons.ps1
```

## Reference renders

| File                                   | Description                                                                         |
| -------------------------------------- | ----------------------------------------------------------------------------------- |
| `assets/screenshots/settings-dark.svg` | Dark theme settings stack showing the new Appearance controls and responsive cards. |
| `assets/screenshots/hud-acrylic.svg`   | Acrylic floating HUD mock exported from the WPF style guide page.                   |

## Usage guidelines

1. Export PNG/WebP variants from the SVGs above when updating docs, the Microsoft Store listing, or website hero art.
2. The tray uses the full-colour app icon so it stays visible on both light and dark taskbars. Keep other menu glyphs monochrome so the Neon Green accent is reserved for success states.
3. When capturing real screenshots, enable the ThemeManager’s Fluent visuals toggle so the acrylic glow matches the marketing renders.
4. Place updated assets in `assets/screenshots/` and reference them from release notes or docs via relative paths.
