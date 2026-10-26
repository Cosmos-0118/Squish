<h1 align="center">Squish</h1>
<p align="center">Clipboard, image, and video optimisation for Windows 11</p>
<p align="center"><em>⚠️ Stable enough for daily use, but expect occasional bugs while we keep polishing.</em></p>

> Squish is usable today. Features are in place, but you may still hit rough edges or small regressions as we keep refining performance.

> **Squish is an independent project.** It is a Windows reimplementation inspired by <a href="https://github.com/FuzzyIdeas/Clop">Clop for Mac</a> by The Low Tech Guys, and is not affiliated with or endorsed by them. "Clop" is their name; Squish uses its own name and artwork.

## Why Squish exists

- macOS already has an excellent experience with <a href="https://github.com/FuzzyIdeas/Clop">Clop for Mac</a>. Windows creators deserve the same clipboard-first workflow, so Squish brings it to Windows 11.
- The codebase follows the structure of the original Swift app: a WPF shell for the HUD, a background worker that watches the clipboard and file system, a CLI bridge for automation, and shared optimisers in `src/Core`.
- Behaviour and file layout stay close to the original project so it is easy to compare against and credit the original authors.

## Where things live

| Area                     | Project / Path                                               | Notes                                                                                              |
| ------------------------ | ------------------------------------------------------------ | -------------------------------------------------------------------------------------------------- |
| WPF shell & floating HUD | `src/App/Squish.App.csproj`                             | Hosts the tray UI, floating results, drag/drop zones, onboarding, and settings pages.              |
| Background work          | `src/BackgroundService/Squish.BackgroundService.csproj` | Runs clipboard/file-system watchers plus automation bridges while the UI is closed.                |
| Optimisation engine      | `src/Core/Squish.Core.csproj`                           | Shared optimisers for images, video, metadata, and migrations.                                |
| CLI bridge               | `src/CliBridge/Squish.CliBridge.csproj`                 | Provides a `squish` command that forwards requests to the running optimiser service via named pipes. |
| Explorer integration     | `src/Integrations/Explorer`                                  | Adds context-menu hooks so files can be optimised directly from Windows Explorer.                  |
| Tests                    | `tests/*`                                                    | Covers shared primitives, optimisers, CLI target resolution, and string catalogs.                  |

## Development setup

1. Install the tooling listed in `docs/windows-deps.md` (✅ .NET 10 SDK, Visual Studio 2022 with WPF + C++ workloads, LLVM/Clang 17, Windows 11 SDK, WebView2 runtime).
2. Restore the optimiser binaries once per machine:

   ```powershell
   pwsh scripts/fetch-tools.ps1
   ```

   The script downloads `ffmpeg`, `mozjpeg`, `libwebp`, `libavif`, `jpegli`, `oxipng`, `pngquant`, and `gifsicle`, then copies the needed binaries during app startup into `%LOCALAPPDATA%\Squish\bin`.

3. Open `src/Squish.sln` in Visual Studio 2022 (or run `dotnet build src/Squish.sln`).
4. Set `App/Squish.App` as the startup project for the WPF shell and `BackgroundService/Squish.BackgroundService` for watcher testing.
5. Optional: run the CLI directly with `dotnet run --project src/CliBridge/Squish.CliBridge.csproj -- --help` to verify IPC connectivity.

## Running the app locally

- **GUI:** Launch the WPF app from Visual Studio. The floating HUD appears near the clipboard history area and will downscale/optimise new clipboard entries.
- **Background service:** Start `Squish.BackgroundService` to simulate the always-on helper that manages clipboard events and communicates with the GUI over named pipes.
- **Explorer extension:** Build `Integrations/Explorer` after installing the Windows 11 SDK; registration scripts live inside that folder.
- **Automation & CLI:** `CliBridge` shares the same request envelopes as macOS Shortcuts. Use it to trigger `OptimisationCoordinator` jobs without touching the UI.

## Testing

```powershell
dotnet test src/Squish.sln
```

- `tests/Core.Tests` covers file-path helpers, optimiser heuristics, and settings migrations.
- `tests/CliBridge.Tests` verifies target resolution logic used by the CLI.
- `tests/App.Tests` keeps UI string catalogs and localisation data in sync.

## Credits & licensing

- Massive credit to <a href="https://github.com/FuzzyIdeas/Clop">Clop for macOS</a> by The Low Tech Guys. Squish uses their GPLv3 project as the reference implementation for behaviour, UI flow, and third-party binary choices. If you like Squish, consider supporting them first.
- Squish is not affiliated with or endorsed by The Low Tech Guys. The Clop name, logo, and artwork belong to them and are not used by Squish.
- Please keep the original license headers and notices intact when reusing or submitting patches.
- Squish inherits the licensing terms documented in `LICENSE` plus the third-party notices in `THIRD_PARTY_NOTICES.md` and `tools/licenses/`.

### Third-party tool credits

| Tool                    | Purpose                                                    | Upstream                                         | License                      |
| ----------------------- | ---------------------------------------------------------- | ------------------------------------------------ | ---------------------------- |
| FFmpeg                  | Video/GIF re-encoding, audio stripping, metadata probes    | <https://ffmpeg.org/>                            | GPL / LGPL (build-dependent) |
| mozjpeg                 | Progressive JPEG encoding when advanced codecs are enabled | <https://github.com/mozilla/mozjpeg>             | IJG / BSD-style              |
| libwebp (`cwebp`)       | Static + animated WebP output                              | <https://developers.google.com/speed/webp>       | BSD 3-Clause                 |
| libavif (`avifenc`)     | AVIF encoding for advanced codec pipeline                  | <https://github.com/AOMediaCodec/libavif>        | BSD 2-Clause                 |
| pngquant                | Palette quantisation for PNG workflows                     | <https://pngquant.org/>                          | GPL v3 / Commercial          |

If you spot behaviour that differs from Clop for Mac or want to help wire up parity items, open an issue or a draft PR. Thanks for following along while I build this hobby project!

## Installer integrity & false positives

- **SHA256 checksum (latest public build):**
  - Installer: `Clop-Setup_1.1.0.exe` (released before the rename to Squish)
  - SHA256: `7a82332765fbbbc1e438e36f3cbc20f90b4995926c464f90bc0e1a89b39deb5f`
- **Verify locally:**

  ```powershell
  CertUtil -hashfile Clop-Setup_1.1.0.exe SHA256
  ```

- **False positive notice:** This is a hobby, unsigned Windows port. Some antivirus engines may flag the installer heuristically because it bundles binaries and watches the clipboard. There is no malicious intent. Verify the checksum above and feel free to upload the installer to VirusTotal yourself.
