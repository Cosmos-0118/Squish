<h1 align="center">Clop for Windows</h1>
<p align="center">Clipboard, image, video, and PDF optimisation for Windows 11</p>
<p align="center"><em>⚠️ Still under heavy development &mdash; this is a personal hobby port.</em></p>

> I am building this project in my spare time to bring the Clop experience to Windows. There are no production-ready builds yet; expect breaking changes, missing UI polish, and the occasional rough edge while parity with the macOS app solidifies.

## Why this port exists

- macOS already has an excellent experience with <a href="https://github.com/FuzzyIdeas/Clop">Clop for Mac</a>. Windows creators deserve the same clipboard-first workflow, so this repo tracks a feature-for-feature reimplementation.
- The codebase mirrors the Swift architecture: a WPF shell for the HUD, a background worker that watches the clipboard and file system, a CLI bridge for automation, and shared optimisers in `src/Core`.
- I keep the naming, file layout, and behaviour intentionally close to the upstream project to make it easy to diff and credit the original authors.

## Current focus

- **Feature parity:** Matching floating HUD behaviour, drop-zone overlays, and preset actions described in `docs/architecture.md`.
- **Media pipelines:** Porting `pngquant`, `mozjpeg`, `libwebp`, `libavif`, `ffmpeg`, `ghostscript`, `qpdf`, and the new LibreOffice-powered document pipeline (see `docs/windows-deps.md` and `tools/tools-manifest.json`).
- **Automation:** Wiring the CLI (`src/CliBridge`) and Explorer integrations so Windows users can script Clop just like macOS Shortcuts users do.
- **Reliability:** Shared logging, migrations, and settings live in `src/Core` so both the GUI app and the background service see the same configuration.

## Where things live

| Area                     | Project / Path                                               | Notes                                                                                              |
| ------------------------ | ------------------------------------------------------------ | -------------------------------------------------------------------------------------------------- |
| WPF shell & floating HUD | `src/App/ClopWindows.App.csproj`                             | Hosts the tray UI, floating results, drag/drop zones, onboarding, and settings pages.              |
| Background work          | `src/BackgroundService/ClopWindows.BackgroundService.csproj` | Runs clipboard/file-system watchers plus automation bridges while the UI is closed.                |
| Optimisation engine      | `src/Core/ClopWindows.Core.csproj`                           | Shared optimisers for images, video, PDF, metadata, and migrations.                                |
| CLI bridge               | `src/CliBridge/ClopWindows.CliBridge.csproj`                 | Provides a `clop` command that forwards requests to the running optimiser service via named pipes. |
| Explorer integration     | `src/Integrations/Explorer`                                  | Adds context-menu hooks so files can be optimised directly from Windows Explorer.                  |
| Tests                    | `tests/*`                                                    | Covers shared primitives, optimisers, CLI target resolution, and string catalogs.                  |

## Development setup

1. Install the tooling listed in `docs/windows-deps.md` (✅ .NET 8 SDK, Visual Studio 2022 with WPF + C++ workloads, LLVM/Clang 17, Windows 11 SDK, WebView2 runtime).
2. Restore the optimiser binaries once per machine:

   ```powershell
   pwsh scripts/fetch-tools.ps1
   ```

   The script downloads `ffmpeg`, `mozjpeg`, `libwebp`, `libavif`, `ghostscript`, `qpdf`, and `LibreOffice`, then copies the needed binaries during app startup into `%LOCALAPPDATA%\Clop\bin`.

3. Open `src/ClopWindows.sln` in Visual Studio 2022 (or run `dotnet build src/ClopWindows.sln`).
4. Set `App/ClopWindows.App` as the startup project for the WPF shell and `BackgroundService/ClopWindows.BackgroundService` for watcher testing.
5. Optional: run the CLI directly with `dotnet run --project src/CliBridge/ClopWindows.CliBridge.csproj -- --help` to verify IPC connectivity.

## Running the app locally

- **GUI:** Launch the WPF app from Visual Studio. The floating HUD appears near the clipboard history area and will downscale/optimise new clipboard entries.
- **Background service:** Start `ClopWindows.BackgroundService` to simulate the always-on helper that manages clipboard events and communicates with the GUI over named pipes.
- **Explorer extension:** Build `Integrations/Explorer` after installing the Windows 11 SDK; registration scripts live inside that folder.
- **Automation & CLI:** `CliBridge` shares the same request envelopes as macOS Shortcuts. Use it to trigger `OptimisationCoordinator` jobs without touching the UI.

## Testing

```powershell
dotnet test src/ClopWindows.sln
```

- `tests/Core.Tests` covers file-path helpers, optimiser heuristics, and settings migrations.
- `tests/CliBridge.Tests` verifies target resolution logic used by the CLI.
- `tests/App.Tests` keeps UI string catalogs and localisation data in sync.

## Credits & licensing

- Massive credit to <a href="https://github.com/FuzzyIdeas/Clop">Clop for macOS</a> by the LowTechGuys team. This Windows port uses their work as a reference implementation for behaviour, UI flow, and third-party binary choices.
- Please keep their original license header and notices intact when reusing or submitting patches. If you like this work, consider supporting the macOS team first.
- Clop for Windows inherits the licensing terms documented in `LICENSE` plus the third-party notices in `THIRD_PARTY_NOTICES.md` and `tools/licenses/`.

### Third-party tool credits

| Tool                    | Purpose                                                    | Upstream                                         | License                      |
| ----------------------- | ---------------------------------------------------------- | ------------------------------------------------ | ---------------------------- |
| FFmpeg                  | Video/GIF re-encoding, audio stripping, metadata probes    | <https://ffmpeg.org/>                            | GPL / LGPL (build-dependent) |
| mozjpeg                 | Progressive JPEG encoding when advanced codecs are enabled | <https://github.com/mozilla/mozjpeg>             | IJG / BSD-style              |
| libwebp (`cwebp`)       | Static + animated WebP output                              | <https://developers.google.com/speed/webp>       | BSD 3-Clause                 |
| libavif (`avifenc`)     | AVIF encoding for advanced codec pipeline                  | <https://github.com/AOMediaCodec/libavif>        | BSD 2-Clause                 |
| pngquant                | Palette quantisation for PNG workflows                     | <https://pngquant.org/>                          | GPL v3 / Commercial          |
| qpdf                    | Optional PDF linearisation before Ghostscript              | <https://github.com/qpdf/qpdf>                   | Apache 2.0                   |
| Ghostscript             | Core PDF optimisation/rasterisation engine                 | <https://www.ghostscript.com/>                   | AGPL v3                      |
| LibreOffice (`soffice`) | Document-to-PDF conversion prior to optimisation           | <https://www.libreoffice.org/about-us/licenses/> | MPL 2.0 / LGPL v3+           |

If you spot mismatches with the macOS experience or want to help wire up parity items, open an issue or a draft PR. Thanks for following along while I build this hobby project!
