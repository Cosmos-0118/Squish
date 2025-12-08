# Automation samples

These examples show practical ways to drive Clop from scripts and schedulers. All commands assume the `clop` CLI is on `PATH` (or replace `clop` with the full path to `ClopWindows.CliBridge.exe`). Keep the background helper running: either start the GUI once or launch `ClopWindows.BackgroundService.exe` so requests can be processed.

## One-off optimisations

Optimise a set of files and emit JSON for logging or CI pipelines:

```powershell
clop optimise "$env:USERPROFILE\Pictures\wallpapers" --recursive --types jpg,png --json > optimise-log.json
```

The command returns non-zero on failure, making it safe for `Invoke-Build`/CI steps.

## Watch a folder for new files

Keep your Downloads directory tidy with a long-running watcher:

```powershell
clop watch "$env:USERPROFILE\Downloads" --types jpg,png,mp4,gif --debounce-ms 1500 --json
```

Use `--exclude-types` to skip formats you do not want touched. Stop the watcher with `Ctrl+C`.

## Nightly scheduled task

Create a nightly clean-up that re-optimises working folders. Save the following as `C:\Scripts\clop-nightly.ps1`:

```powershell
$targets = @(
  "$env:USERPROFILE\Pictures\Screenshots",
  "$env:USERPROFILE\Videos\Captures"
)
foreach ($path in $targets) {
  if (Test-Path $path) {
    clop optimise $path --recursive --aggressive --json
  }
}
```

Then register the task (runs at 2:00 AM daily):

```powershell
schtasks /Create /TN "ClopNightly" /TR "pwsh -NoLogo -File C:\Scripts\clop-nightly.ps1" /SC DAILY /ST 02:00 /F
```

## Combining with Explorer integration

The Explorer context menu extension is already included in releases. For automation flows, prefer the CLI examples above; the extension is meant for manual right-click actions and does not expose additional switches.
