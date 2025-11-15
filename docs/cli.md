# Windows CLI Reference

The `clop` executable mirrors the macOS CLI while tapping into the Windows optimisation pipeline. Commands are backed by `System.CommandLine` so they inherit standard help output and shell completion support.

## Global Notes

- Requires Windows 7 or later with .NET 8 runtime installed.
- All commands emit non-zero exit codes when failures occur.
- JSON output uses camelCase to match macOS responses.

## optimise

Optimise files and directories on demand.

```powershell
clop optimise <items...> [options]
```

Key flags:

- `--recursive`, `--types`, `--exclude-types` for bulk selection.
- `--aggressive`, `--remove-audio`, `--playback-speed-factor` to tweak presets.
- `--json` to receive a structured payload matching `OptimisationResult`.
- Unsupported macOS-only switches (`--copy`, `--gui`, `--async`, `--adaptive-optimisation`) emit warnings but do not fail.

## watch

Continuously watch directories and optimise new/changed files. Multiple roots are supported; defaults to the current working directory when none are provided.

```powershell
clop watch [paths...] [options]
```

Highlights:

- Debounced `FileSystemWatcher` pipeline that coalesces duplicate events.
- Uses the same optimiser presets as the GUI with JSON streaming via `--json`.
- Guards against recursive triggers by ignoring the app’s work root.
- `--debounce-ms` and `--ready-timeout-ms` allow tuning for slow network shares.

## schema

Generate a JSON schema describing the CLI surface for IDEs or shell completion tools.

```powershell
clop schema [--output <file>] [--pretty]
```

When `--output` is omitted the schema prints to stdout, making it easy to pipe into `jq` or other tooling.
