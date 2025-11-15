# Clop for Windows – Implementation Roadmap

This roadmap mirrors every macOS capability in `Clop/` and sequences the work so Windows feature parity can be achieved with minimal backtracking. Use the checklist to track progress and keep the Windows tree aligned with upstream Swift sources.

## Parity Goals

| macOS capability                                                | Swift reference                                                                | Windows deliverable                                                                                          |
| --------------------------------------------------------------- | ------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------ |
| Clipboard/image optimiser, floating thumbnails, drag/drop zones | `ClopApp.swift`, `ContentView.swift`, `FloatingResult.swift`, `DropZone.swift` | WPF HUD + background agent mirroring live previews and drop targets                                          |
| Video/PDF/image pipeline w/ aggressive presets, metadata rules  | `Video.swift`, `Images.swift`, `PDF.swift`, `OptimisationUtils.swift`          | `src/Core/` media pipeline with identical presets + EXIF rules and multi-process orchestration               |
| Automation: Shortcuts, CLI, services, Finder extension          | `ClopShortcuts.swift`, `InstallCLI.swift`, `FinderOptimiser/`                  | Windows App Service + CLI bridge + Explorer context integration                                              |
| Settings, migrations, support CTA, telemetry                    | `Settings.swift`, `Migrations.swift`, `ClopApp.swift`                          | `%AppData%` settings store, migration helpers, Sentry-equivalent telemetry (support CTA arrives post-launch) |
| Auto backups, file watchers, upload helpers                     | `Uploads.swift`, `CherryPicks.swift`, `Automation.swift`                       | BackgroundService file watcher with same throttling/backoff semantics                                        |

Treat the left column as the definition of “done” for Windows.

> **Free availability**: Every optimisation feature ships unlocked. The only monetisation surface is an optional "Buy Me a Coffee" link—no trials, paywalls, or feature gating on Windows.

## Windows Solution Layout

```
Clop-Windows/
├─ Roadmap.md
├─ README.md                      # Windows-specific build/run notes
├─ assets/                        # Reused PNGs, GIFs, marketing art
├─ tools/                         # ffmpeg, pngquant, gifski, etc.
├─ docs/
│  ├─ windows-deps.md             # Binary install/licensing instructions
│  └─ architecture.md             # Mapping of Swift modules → C# projects
├─ src/
│  ├─ ClopWindows.sln             # WPF / .NET 8 solution
│  ├─ App/                        # WPF front-end (MVVM, notifications)
│  ├─ BackgroundService/          # Windows Service / packaged agent
│  ├─ Core/                       # Optimisation engine + abstractions
│  ├─ Core/Optimizers/            # Per-format wrappers (ffmpeg, pngquant…)
│  ├─ Integrations/Explorer/      # Shell extension / context menu bridge
│  └─ CliBridge/                  # dotnet CLI for automation & tests
└─ tests/
   ├─ Core.Tests/                 # Unit tests for pipeline + presets
   └─ Integration.Tests/          # Clipboard, drag-drop, and CLI smoke tests
```

## Execution Plan

### Phase 0 – Foundation & Inventory

- [x] **F0.1 – Feature matrix**: Expand `docs/architecture.md` with a table mapping every macOS Swift file to its Windows owner. Note behavioural quirks (e.g., `Defaults` sync, support CTA entry points, floating window animations).
- [x] **F0.2 – Tooling baseline**: Install .NET 8 SDK, Visual Studio 2022 (WPF workload), Windows 11 SDK, and clang/LLVM for native helpers. Record versions + install commands in `docs/windows-deps.md`.
- [x] **F0.3 – Binary audit**: List required third-party binaries (pngquant, jpegoptim, gifski, ffmpeg, libvips, ghostscript). For each, capture command-line args currently used (search `Clop/*.swift`) and Windows equivalents, then plan distribution/licensing notes.

### Phase 1 – Repository Skeleton & Shared Utilities

- [x] **P1.1 – Solution scaffolding**: Folder tree created, `ClopWindows.sln` initialised with `App`, `BackgroundService`, `Core`, `CliBridge`, and `Integrations.Explorer` projects, plus WPF desktop packaging metadata and RID matrix.
- [x] **P1.2 – Shared helpers**: Port `Shared.swift`, `CherryPicks.swift`, `ClopUtils.swift` to C# helpers inside `Core` (e.g., `FilePathExtensions`, `MachPort` → `NamedPipeChannel`). Ensure feature flags and enums (optimisation sources, behaviours, presets) match Swift names for easier diffing.
  - Implemented `Core/Shared/Primitives.cs`, `NumericExtensions.cs`, `StringExtensions.cs`, `NanoId.cs`, `FilePathGenerator.cs`, and `ClopPaths.cs`, plus a `Core/IPC/NamedPipeChannel.cs` bridge and `Core/Processes/ProcessRunner.cs`.
  - Mirrored macOS data sets (crop sizes, device + paper tables) via embedded JSON, added safe filename/templating helpers, and introduced xUnit coverage for the new utilities.
- [x] **P1.3 – Defaults & migrations**: Re-implement `Settings.swift`/`Migrations.swift` using `System.Text.Json` + versioned schema files stored under `%AppData%\Clop\config.json`. Add unit tests covering migrations and default hydration.
  - Added strongly-typed registry (`SettingKey<T>`, `SettingsRegistry`, and `SettingsStore`) plus an app-wide `SettingsHost` so WPF and background components can read/write defaults consistently.
  - Ported macOS migration logic (`SettingsMigrations`, `ClopIgnoreMigration`) and wired it into the new JSON document pipeline so `.clopignore` files are reshaped per media type on first launch.
  - Covered the store with xUnit tests (default hydration, persistence, migration side-effects) and disabled parallel execution to keep the shared `FilePath.Workdir` deterministic.

### Phase 2 – Optimisation Engine

- [x] **P2.1 – Pipeline orchestration**: Translate `OptimisationUtils.swift` + `Optimisable.swift` into a pluggable C# pipeline (request queue, progress reporting, cancellation). Provide interfaces for media-specific optimisers.
  - Added `Core/Optimizers` infrastructure: `OptimisationCoordinator`, `OptimisationRequest/Result`, `ItemType`, and `IOptimiser` contracts backed by `System.Threading.Channels` for multi-worker scheduling.
  - Introduced an execution context that forwards optimiser progress to UI listeners, plus queue status tracking and graceful shutdown semantics.
  - Covered the coordinator with xUnit tests (`OptimisationCoordinatorTests`) verifying success paths, unsupported types, cancellation, and progress notifications.
- [x] **P2.2 – Image pipeline**: Port logic from `Images.swift`, including `pngquant`, `jpegoptim`, EXIF preservation (`copyExif`), Retina downscale options, and crop presets. Validate outputs with golden files in `tests/Core.Tests/Images`. Bundle libvips (or ImageMagick) fallback.
  - Introduced `Core/Optimizers/ImageOptimiser.cs` plus `ImageOptimiserOptions` to encapsulate supported formats, JPEG conversion, metadata rules, and Retina downscale thresholds. The optimiser streams files through `System.Drawing` with progress reporting, preserves EXIF when requested, strips metadata by default, and produces `.clop` outputs only when they beat or enhance the source.
  - Added `Core.Tests/ImageOptimiserTests.cs` covering PNG→JPEG conversions, Retina downscale limits, and metadata preservation to ensure parity behaviours remain stable.
- [x] **P2.3 – Video pipeline**: Mirror `Video.swift` features: ffmpeg progress scraping, FPS caps, Media Engine hardware acceleration (map to Windows Video Acceleration), GIF export via `gifski`. Provide regression tests using sample MOV/MP4 assets.
  - Added `Core/Optimizers/VideoOptimiser.cs` with a configurable `VideoOptimiserOptions` surface, public `IVideoToolchain` contract, ffmpeg progress parsing, GIF export via gifski, and timestamp preservation. Hardware-accelerated encodes default to `d3d11va`/`h264_amf` with software fallback and filter support for resizing, playback-speed changes, and audio stripping.
  - Created `VideoOptimiserTests` with a fake toolchain to verify fps caps, metadata overrides, and GIF conversion semantics, ensuring the coordinator can process MOV/MP4-style inputs without requiring the real binaries during CI.
- [x] **P2.4 – PDF pipeline**: Reproduce `PDF.swift` Ghostscript command set, progress parsing, and metadata stripping toggles. Document Ghostscript licensing requirements in `docs/windows-deps.md`.
  - Added `Core/Optimizers/PdfOptimiser.cs` plus injectable `IPdfToolchain` so the coordinator can shell out to Ghostscript with the same lossy/lossless stacks, metadata stripping hooks, and timestamp preservation as macOS.
  - Introduced `PdfOptimiserOptions` for configuring Ghostscript paths, font search directories, and size/metadata policies, with sane defaults sourced from `%LOCALAPPDATA%` + bundled binaries.
  - Created `Core.Tests/PdfOptimiserTests.cs` using a fake toolchain to cover success paths, metadata overrides, and invalid PDF handling without requiring the real binary.
  - Expanded `docs/windows-deps.md` with explicit Ghostscript licensing guidance (AGPL vs commercial Artifex license) so release engineering can choose a compliant distribution path.
- [x] **P2.5 – CLI parity**: Build `CliBridge` commands analogous to macOS CLI (optimize clipboard, files, directories, automation hooks). Ensure command names/flags match for portability.
  - Implemented a System.CommandLine-driven `clop optimise` entry point that mirrors macOS flags (types/include/exclude, recursive, aggressive, playback-speed, JSON, progress, clipboard/UI toggles) and streams work through the existing optimisation coordinator.
  - Added guard rails & warnings for mac-only flags (GUI, clipboard copy, adaptive optimisation) so scripts can pass the same arguments without breaking.
  - Introduced a dedicated `CliBridge.Tests` project with unit coverage for `TypeFilter` and `TargetResolver`, ensuring include/exclude tokens and recursion behave exactly like the Swift CLI.

### Phase 3 – Background Agents & Automation

- [x] **P3.1 – Clipboard watcher**: Implement a background agent (Worker Service with tray hook or packaged desktop task) that watches clipboard changes, honours pause states, and invokes the pipeline, mirroring `ClopApp.swift` clipboard logic.
- [x] **P3.2 – File/directory watchers**: Port `Uploads.swift` + `Automation.swift` semantics (directory allow-lists, size caps, concurrency limits). Use `FileSystemWatcher` with debounce logic equivalent to `EonilFSEvents`.
- [x] **P3.3 – Automation hooks**: Provide App Service endpoints / named pipes for Windows Shortcuts, Power Automate, and Explorer context menu triggers, matching macOS Shortcut identifiers.
- [x] **P3.4 – Finder-equivalent integration**: Create Explorer context menu or Share contract extension mirroring `FinderOptimiser`. Ensure drag/drop promises behave like macOS NSFilePromiseReceiver flows.

### Phase 4 – WPF Experience

- [x] **P4.1 – Floating HUD**: Recreate the SwiftUI floating results (`FloatingResult.swift`, `CompactResult.swift`) as WPF windows with acrylic/compact styling (Win32 composition brushes or WinAppSDK interop where useful). Include progress bars, preview thumbnails, and quick actions.
- [x] **P4.2 – Main shell**: Implement Settings, Onboarding, and Compare views (feature toggles, aggressive optimisation switches, preset editors) in WPF MVVM, mirroring `SettingsView.swift`, `CompareView.swift`, `Onboarding.swift`.
  - Implemented navigation rail, onboarding tour, compare intake, and settings panels bound to `SettingsHost` with DI wiring.
- [x] **P4.3 – Keyboard + gesture support**: Map hotkeys (`SauceKey` equivalents) to Windows accelerator keys and global shortcuts, keeping defaults in sync. Document interplay with Windows input APIs.
  - Expanded `ShortcutCatalog` into a settings-backed registry with dynamic bindings, remapping helpers, and conflict detection, plus live re-registration in `KeyboardShortcutService`.
  - Surfaced in-app and global shortcuts inside Settings with capture/clear/reset UI and a shortcut recording dialog so users can rebind keys without touching JSON.
  - Documented drop-zone pointer gestures (Alt to reveal overlay, Ctrl for preset zones, right-click for HUD context menus) alongside keyboard coverage in `docs/architecture.md`.
- [x] **P4.4 – Localization & accessibility**: Ensure UI texts reuse the macOS strings and pass Windows accessibility checks (High Contrast, screen readers).

### Phase 9 – Image Optimiser 2.0

- [x] **P9.1 – Switch core processing to ImageSharp or WebP/AVIF-native libraries**: Replace the `System.Drawing` pipeline in `Core/Optimizers/ImageOptimiser.cs` with ImageSharp + libvips bindings to unlock SIMD, better colour management, and 16-bit/channel handling. Preserve retina/resolution logic from macOS `Images.swift`.
- [x] **P9.2 – Advanced codec support**: Integrate mozjpeg, avifenc, cwebp, and heif-convert with automatic capability detection. Add heuristics to pick WEBP/AVIF for photographic inputs while falling back to PNG for UI assets, mirroring macOS presets.
- [x] **P9.3 – Perceptual quality guards**: Use SSIM/MS-SSIM thresholds to reject outputs that fall below visual targets when `RequireSizeImprovement` is true. Pipe quality metrics back into the floating HUD so users know when aggressive mode trades fidelity for size.
- [x] **P9.4 – Smart segmentation & crop presets**: Port macOS `PresetZones.swift` logic and add ONNX/WinML-powered foreground segmentation (e.g., document edge detection, human subject isolation) to automate crop suggestions. Cache masks so repeated files avoid reprocessing.
- [x] **P9.5 – Metadata policy refinements**: Align with macOS EXIF/xmp rules, add per-profile toggles (retain colour profiles, GPS stripping), and write tests covering ICC preservation to avoid washed colours when using high-end monitors.
- [x] **P9.6 – WIC-assisted fast paths**: Detect when an input already uses a Windows-native codec (JPEG, PNG, BMP, GIF, TIFF) and short-circuit `ImageOptimiser` to a WIC decode/re-encode pipeline so we can leverage hardware colour conversion, progressive JPEG copy, and PNG chunk stripping without re-rendering pixels through ImageSharp. Surface heuristics that skip recompressing lossless formats when the delta would be <2 % to avoid wasting encode time on assets that are already compact.

### Phase 10 – Video Optimiser Enhancements

- [x] **P10.1 – Multi-encoder strategy**: Extend `VideoOptimiserOptions` to support AV1 (svt-av1/libaom), HEVC (x265/AMF), and VP9, picking the optimal encoder based on hardware (DXVA2, NVENC, Intel QSV). Mirror macOS heuristics for aggressive vs gentle pipelines.
- [x] **P10.2 – Scene-cut aware bitrate control**: Add two-pass or lookahead support for ffmpeg with `-pass` or `-rc-lookahead`, ensuring aggressive modes maintain perceived sharpness on high-motion clips.
- [x] **P10.3 – Intelligent frame decimation**: Implement motion-based frame culling using ffmpeg `mpdecimate`/`vidstab` filters to trim redundant frames while keeping output smooth, gated behind benchmark validation from Phase 8.
- [x] **P10.4 – Audio pipeline parity**: Support AAC/Opus re-encode, loudness normalisation, and channel down-mix options surfaced in the UI, matching macOS advanced toggles.
- [x] **P10.5 – GIF modernisation**: Replace the manual png frame staging with gifski library bindings or libimagequant to reduce artefacts, and offer APNG/WebP animated exports when quality thresholds demand it.
- [x] **P10.6 – Container-aware remuxing**: Extend `VideoOptimiser` to probe codec/container pairs up front and, when the source is already H.264/H.265 + AAC, remux MOV/MKV/AVI/WebM inputs straight to MP4/Matroska via `ffmpeg -c copy` instead of forcing a full transcode. Fall back to the current re-encode path only when filters (fps cap, resize, audio removal) are requested or when the target truly needs a different codec, dramatically speeding up non-MP4 workflows.
- [x] **P10.7 – Format-specific encoder tuning**: Add presets for VP9/WebM and DNx/ProRes footage that keep their native containers but switch to the most compatible Windows encoders (libvpx-vp9, AMF HEVC, or software x265) with bitrate targets derived from the existing `VideoOptimiserPlan`. Include heuristics that downshift to mezzanine-quality encodes when size savings would be <5 % so we stop wasting time on long GOP sources that already meet the constraints.

### Phase 11 – PDF Optimiser Enhancements

- [x] **P11.1 – Ghostscript preset matrix**: Layer additional Ghostscript switches (`-dDetectDuplicateImages`, `-dColorConversionStrategy=/sRGB`, adaptive downsampling) into `PdfOptimiser` based on page count and embedded image DPI so we squeeze more savings from graphics-heavy PDFs without introducing custom native code.
- [x] **P11.2 – Linearisation & structure clean-up**: Chain `qpdf --linearize` (Windows builds already available) ahead of Ghostscript to deduplicate objects, remove unused form XObjects, and prime files for fast web view before we run the existing size/metadata pass.
- [x] **P11.3 – Windows colour management parity**: Use the built-in `WindowsColorSystem` ICC APIs to preserve/convert document profiles post-optimisation, mirroring macOS’s ColorSync path so PDFs viewed in Edge/Reader keep their appearance while still benefiting from Ghostscript compression.

### Phase 12 – Automation, Watchers, and Batch Intelligence

- [x] **P12.1 – Heuristic queueing**: Enhance `BackgroundService` watchers to batch similar files, schedule large encodes during system idle, and prioritise quick wins for better perceived speed.
  - Added `HeuristicScheduler` and idle-aware batching inside `DirectoryOptimisationService`, mirroring macOS queue scoring and last-input heuristics.
- [x] **P12.2 – Cross-app integrations**: Ship native hooks for Microsoft Power Automate, Share targets, and Teams adaptive cards mirroring macOS Shortcuts depth. Include sample flows in `docs/automation-samples.md`.
  - Introduced `CrossAppAutomationHost` HTTP listener with bearer token auth plus Power Automate, Share, and Teams adaptive-card endpoints.
- [ ] **P12.3 – Smart redo suggestions**: Analyse optimisation outcomes and, when savings are minimal, offer alternative presets or remind users to try AVIF/AGGRESSIVE in HUD tooltips.
- [x] **P12.4 – CLI power-user features**: Add watch mode (`clop watch`), JSON schema export, and shell completion scripts. Mirror macOS CLI flags and document them in `docs/cli.md`.
  - Implemented `clop watch` with debounce + JSON streaming and added a schema export command for tooling integrations.

### Phase 13 – UI Modernisation & Theming

## Colour & Theme Guidance (Quick Reference)

- **Black Onyx (#050505)** base for shells and floating HUD backplates.
- **Neon Green (#4FFFB0)** primary accent for success states and progress.
- **Signal Red (#F45D5D)** alerts, aggressive-mode warnings.
- **Ember Orange (#FF8A3D)** neutral emphasis (hover, pending actions).
- **Royal Purple (#8C5BFF)** secondary accent for selection highlights and compare view tabs.

Map these into `Brush.*` resources with light/dark variants and ensure High Contrast theme inherits correctly.

- [x] **P13.1 – Design system audit**: Define a colour + typography token set (Black Onyx, Neon Green, Signal Red, Ember Orange, Royal Purple) and contrast rules. Update `Theme.Default.xaml` and add `Theme.Dark.xaml`/`Theme.HighSaturation.xaml` so users can toggle vibrant themes entirely within WPF resource dictionaries.
- [x] **P13.2 – WPF visual layer**: Replace the WinUI harness with a WPF-first composition strategy (Mica/Acrylic backdrops via `SystemBackdrop`, `TransitioningContentControl` animations, HUD blur shaders). Ensure every window pulls brushes from the shared token dictionaries and wire a debug-only style guide page for manual inspection.
- [x] **P13.3 – Layout & typography polish**: Adopt responsive grids, dynamic spacing, and typography scale from macOS (SF Pro equivalents → Segoe Fluent, Inter). Revisit `FloatingHud` to better match macOS translucency and depth using WPF MVVM patterns.
- [x] **P13.4 – Accessibility & localisation sweep**: Revalidate contrast, keyboard focus cues, and screen reader labels in the new theme. Sync translations with macOS `Localization/` strings and add RTL testing matrix.
- [x] **P13.5 – Brand collateral refresh**: Update `assets/` with new screenshots, hero images, and tray icons that reflect the updated colour direction and WPF visuals. Document usage guidelines in `docs/brand.md`.

### Phase 5 – Telemetry & Packaging

- [ ] **P5.1 – Telemetry**: Integrate Sentry or Azure App Center equivalent, mirroring events emitted in `ClopApp.swift`. Offer opt-in controls consistent with macOS preferences.
- [ ] **P5.2 – Installers & updates**: Produce MSIX/App Installer packages, optional winget manifest, and scriptable install (PowerShell). Include automatic update strategy akin to Sparkle.
- [ ] **P5.3 – QA matrix**: Draft test checklist (multi-monitor, HDR, touch, battery saver, offline). Automate core scenarios via WinAppDriver / Playwright where feasible.

### Phase 14 – Delivery, QA, and Observability

- [ ] **P14.1 – Continuous size regression tests**: Run the benchmark harness nightly, publish dashboards (Power BI or Grafana) so size deltas trigger alerts.
- [ ] **P14.2 – Crash/telemetry roll-out**: Finish P5.1 by integrating Sentry/App Center with the new optimisers and UI events, ensuring user opt-in is respected.
- [ ] **P14.3 – Installer extensions**: Bundle optional codec packs (AV1/HEIF) with hashed integrity checks. Offer modular downloads so users on constrained systems install only what they need.
- [ ] **P14.4 – Cross-platform parity review**: Schedule quarterly parity checkpoints with the macOS repo. Diff presets, defaults, and UI flows, logging mismatches in `docs/parity-audits/`.
- [ ] **P14.5 – Performance budgets**: Set and enforce budgets (per-file encode time, HUD render FPS, memory usage). Fail CI when new features exceed thresholds without explicit waivers.

### Phase 6 – Release Readiness

- [ ] **P6.1 – Docs & onboarding**: Update `Clop-Windows/README.md` with build/run steps, screenshots, and troubleshooting. Add migration guide for macOS users switching platforms.
- [ ] **P6.2 – Feature lock + regression pass**: Run parity tests comparing macOS outputs vs Windows outputs on the same sample set (images, videos, PDFs). Store diff results in `tests/fixtures/results`.
- [ ] **P6.3 – Release packaging**: Publish MSI/MSIX artifacts, update `Releases/appcast.xml` (or Windows-equivalent feed), and append Windows notes to `ReleaseNotes/`.

### Phase 7 – Post-launch Monetisation

- [ ] **P7.1 – Support CTA**: Replace the legacy Pro button with a "Buy Me a Coffee" link in the tray menu, Settings, and onboarding screens. All optimisation features stay free; the CTA is a simple browser link / WebView wrapper with zero gating logic.

Each task references the macOS source of truth; check it first, then reproduce behaviour in the Windows project. Track progress by checking the boxes above and linking commits/issues for traceability.
