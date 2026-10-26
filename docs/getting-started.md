# Getting Started

This guide gets a new contributor from clone to running Squish locally.

## Prerequisites

- Windows 10/11
- .NET 10 SDK
- Visual Studio 2022 (recommended) with .NET desktop workload, or VS Code + C# Dev Kit
- Git

## Clone the repo

```pwsh
git clone https://github.com/Cosmos-0118/Clop-For-Windows.git
cd Squish-For-Windows
```

## Restore external tools (one-time)

Squish depends on packaged binaries (ffmpeg, mozjpeg, oxipng, etc.). Pull them with the helper script:

```pwsh
./scripts/fetch-tools.ps1
```

This populates the `tools/` directory with required executables.

## Build

```pwsh
# Release build (recommended)
dotnet build src/Squish.sln -c Release

# or Debug if you prefer
dotnet build src/Squish.sln -c Debug
```

## Run the app

```pwsh
# From repo root
dotnet run --project src/App/Squish.App.csproj -c Release
```

Optional: start minimized to tray using the background switch:

```pwsh
dotnet run --project src/App/Squish.App.csproj -c Release -- --background
```

## Run background service only (for automation tests)

```pwsh
dotnet run --project src/BackgroundService/Squish.BackgroundService.csproj -c Release
```

## Tests

```pwsh
# Run all tests
dotnet test src/Squish.sln -c Release

# Focus on a test project
cd tests/Core.Tests
 dotnet test -c Release
```

## Packaging

If you need the installer, use the Inno Setup script under `installer/SquishInstaller.iss` (requires Inno Setup installed). The packaged `tools/` must be present before building an installer.

## Troubleshooting

- Missing external binaries: rerun `./scripts/fetch-tools.ps1`.
- Restore issues: `dotnet restore src/Squish.sln`.
- Hotkeys not working at startup: ensure app has been launched at least once with global shortcuts enabled in Settings.

You should now be able to build, run, and iterate on Squish locally.
