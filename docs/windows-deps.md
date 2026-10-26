# Windows Tooling Baseline (Phase 0)

Install these stacks before touching the WPF solution so that every developer gets identical compiler/runtime behaviour.

## SDKs & Toolchain

| Component                      | Target Version                                             | Purpose                                                                                                       | Verification                                                                                              |
| ------------------------------ | ---------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------- |
| .NET SDK                       | 8.0 LTS (8.0.204 or newer)                                 | Builds the WPF shell, BackgroundService, Core, and CliBridge projects.                                        | `dotnet --list-sdks` should show an `8.0.x` entry.                                                        |
| Visual Studio 2022 (Desktop)   | 17.10+ with `.NET desktop`, `WPF`, and `C++ MFC` workloads | Provides XAML tooling, designers, and Explorer integration projects from a single install.                    | `vswhere -latest -products * -requires Microsoft.VisualStudio.Workload.ManagedDesktop` lists the install. |
| Visual Studio 2022 Build Tools | 17.10+ with `Microsoft.VisualStudio.Workload.VCTools`      | Required for MSIX packaging, Explorer shell extensions, and native helper builds.                             | `vswhere -latest -products * -requires Microsoft.VisualStudio.Workload.VCTools` lists the install.        |
| WebView2 Runtime               | Evergreen                                                  | Powers telemetry opt-in dialogs today and will host the Buy-Me-A-Coffee panel once the post-launch CTA ships. | `reg query "HKLM\SOFTWARE\Microsoft\EdgeUpdate\Clients" /s` mentions WebView2.                            |
| LLVM/Clang                     | 17.x                                                       | Used for native helpers (Explorer bridge, possible SIMD image stubs).                                         | `clang --version` shows 17.x.                                                                             |
| Windows 11 SDK                 | 10.0.26100.\*                                              | Headers/libs for WPF interop, Explorer integration, and desktop packaging.                                    | `Get-ChildItem "$Env:ProgramFiles(x86)\Windows Kits\10\Lib"` lists `26100`.                               |
| Windows SDK tooling (MIDL)     | 10.0.19041+                                                | Provides `midlrt.exe` for COM definitions used by Explorer shell integration.                                 | `where midlrt.exe` prints the path under `Windows Kits\10\bin`.                                           |

### Install Commands

```powershell
# .NET 10 SDK
winget install --id Microsoft.DotNet.SDK.10 --exact
# VS 2022 Community with WPF + desktop workloads (quiet install takes several minutes)
winget install --id Microsoft.VisualStudio.2022.Community --override "--quiet --wait --norestart --includeRecommended --add Microsoft.VisualStudio.Workload.ManagedDesktop --add Microsoft.VisualStudio.Workload.NativeDesktop --add Microsoft.VisualStudio.Workload.NativeGame"
# VS 2022 Build Tools with VC workload (for CI boxes that skip the full IDE)
winget install --id Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --norestart --includeRecommended --add Microsoft.VisualStudio.Workload.VCTools"
# WebView2 runtime (Edge Evergreen)
winget install --id Microsoft.EdgeWebView2Runtime --exact
# LLVM/Clang 17
winget install --id LLVM.LLVM --exact
```

### Verification Commands

```powershell
# SDK + workloads
dotnet --list-sdks
& "$Env:ProgramFiles(x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Workload.ManagedDesktop
& "$Env:ProgramFiles(x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Workload.VCTools
reg query "HKLM\SOFTWARE\Microsoft\EdgeUpdate\Clients" /s | Select-String WebView2
clang --version
where midlrt.exe
```

### Setup Notes

- Run the VS Build Tools installer at least once to accept licenses; otherwise MSIX packaging fails silently.
- Configure a Developer PowerShell prompt that adds `%ProgramFiles%\LLVM\bin` and the VS `VC\Tools` directory to `PATH` so `cl.exe` and `clang.exe` are available during CI builds.
- Replicate macOS' decompression behaviour by extracting third-party binaries into `%LOCALAPPDATA%\Squish\bin\x64` on first launch; mirror the folder names from `Shared.swift` so path concatenation stays the same.

## Third-Party Binary Audit (F0.3)

| Binary / Package               | Responsibility in Squish                                                                                                                                      | Windows Distribution Plan                                                                                                                                                  | Licensing / Notes                                                                                                                   |
| ------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| `ffmpeg` + `ffprobe`           | `VideoOptimiser.cs` performs remuxes, re-encodes, animated exports, and metadata probes; `FfprobeMetadataProbe` shells out to `ffprobe` for stream details. | Stage the BtbN `ffmpeg-master-latest-win64-gpl.zip` payload under `tools/ffmpeg/` so the AMF/NVENC-enabled binaries copy into `%LOCALAPPDATA%\Squish\bin\{arch}` at runtime. | GPL/LGPL depending on codecs. Bundle the upstream GPL notices and document the source URL inside THIRD_PARTY_NOTICES.               |
| `mozjpeg` (`cjpeg-static.exe`) | `AdvancedCodecRunner` uses mozjpeg for progressive JPEG outputs when advanced codecs are enabled.                                                           | Download `mozjpeg-x64.zip` from `garyzyg/mozjpeg-windows`, extract it into `tools/mozjpeg/`, and expose `cjpeg-static.exe` (duplicate/alias as `mozjpeg` if desired).      | BSD-style IJG/libjpeg license. Bundle the upstream README/LICENSE files next to the executable.                                     |
| `cwebp` (libwebp)              | Also invoked by `AdvancedCodecRunner` and by the animated export pipeline to produce WebP replacements for GIFs.                                            | Use `libwebp-1.4.0-windows-x64.zip`, copy the entire folder into `tools/libwebp/`, and keep that directory on PATH so `cwebp.exe` and required DLLs resolve.               | BSD 3-Clause. Include Google's NOTICE/README files inside the installer payload.                                                    |
| `avifenc` (libavif)            | Generates AVIF bitstreams from staged PNGs via `AdvancedCodecRunner`, with profile-driven quality settings.                                                 | Pull `windows-artifacts.zip` from the official `libavif` release, place it in `tools/libavif/`, and reference `avifenc.exe` directly.                                      | BSD 2-Clause (Alliance for Open Media). Keep the upstream NOTICE alongside the binary.                                              |


`scripts/fetch-tools.ps1` together with `tools/tools-manifest.json` now downloads every binary listed above (with SHA256 verification), so `pwsh scripts/fetch-tools.ps1` is the single command required to prepare a workstation or CI agent. During app start the binaries are copied into `%LOCALAPPDATA%\Squish\bin\{arch}`, mirroring macOS' `GLOBAL_BIN_DIR` flow.

