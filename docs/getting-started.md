# Getting Started

This guide gets a new contributor from clone to running Clop for Windows locally.

## Prerequisites

- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022 (recommended) with .NET desktop workload, or VS Code + C# Dev Kit
- Git

## Clone the repo

```pwsh
git clone https://github.com/Cosmos-0118/Clop-For-Windows.git
cd Clop-For-Windows
```

## Restore external tools (one-time)

Clop depends on packaged binaries (ffmpeg, qpdf, etc.). Pull them with the helper script:

```pwsh
./scripts/fetch-tools.ps1
```

This populates the `tools/` directory with required executables.

## Build

```pwsh
# Release build (recommended)
dotnet build src/ClopWindows.sln -c Release

# or Debug if you prefer
dotnet build src/ClopWindows.sln -c Debug
```

## Run the app

```pwsh
# From repo root
dotnet run --project src/App/ClopWindows.App.csproj -c Release
```

Optional: start minimized to tray using the background switch:

```pwsh
dotnet run --project src/App/ClopWindows.App.csproj -c Release -- --background
```

## Run background service only (for automation tests)

```pwsh
dotnet run --project src/BackgroundService/ClopWindows.BackgroundService.csproj -c Release
```

## Tests

```pwsh
# Run all tests
dotnet test src/ClopWindows.sln -c Release

# Focus on a test project
cd tests/Core.Tests
 dotnet test -c Release
```

## Packaging

If you need the installer, use the Inno Setup script under `installer/ClopInstaller.iss` (requires Inno Setup installed). The packaged `tools/` must be present before building an installer.

## Troubleshooting

- Missing external binaries: rerun `./scripts/fetch-tools.ps1`.
- Restore issues: `dotnet restore src/ClopWindows.sln`.
- Hotkeys not working at startup: ensure app has been launched at least once with global shortcuts enabled in Settings.

You should now be able to build, run, and iterate on Clop for Windows locally.
