# Continuous integration

CI runs only the work a change needs. A documentation fix costs a few seconds; a change to `src/CliBridge` runs the CLI
tests; a change to `src/Core` runs everything downstream of it.

## How a change is planned

The first job of the `ci` workflow runs `scripts/detect-changes.ps1`. It reads the changed files, maps each one to the
project that owns it, follows the `ProjectReference` graph in the `.csproj` files to find every project that depends on
a changed one, and emits a plan:

| A change to | Runs |
| --- | --- |
| `src/Core` | all four test projects, the publish check |
| `src/BackgroundService` | `BackgroundService.Tests`, `Squish.App.Tests`, the publish check |
| `src/App` | `Squish.App.Tests`, the publish check |
| `src/CliBridge` | `CliBridge.Tests`, the publish check |
| `src/Integrations/Explorer` | the publish check (it has no tests) |
| `tests/<Project>` | just that test project |
| `tools/tools-manifest.json`, `scripts/fetch-tools.ps1` | the tests that use the real encoders, the publish check |
| `docs/cli.md` and the other files copied into the package | the publish check |
| `.github/workflows/*.yml`, `scripts/detect-changes.ps1` | lint and the detector self-tests |
| other docs, `README.md`, `installer/`, `.github/release-notes` | nothing |

Everything runs when the plan cannot be trusted to be narrower:

* `ci.yml`, `Directory.*.props/targets`, `global.json`, `nuget.config`, `.editorconfig` or a solution file changed
* a path the script does not recognise changed (it fails safe)
* the diff could not be worked out, for example after a force push
* a manual run (`workflow_dispatch`), and the weekly scheduled run on `main`

Because the graph is read from the project files, adding a project or a reference needs no change to the script. A test
project is given the encoder tools only when its sources use them (`ToolFact`, `SQUISH_TOOLS_DIR`).

Each run's summary page shows what was selected and why.

## Branch protection

Require the single **`ci-ok`** check. It always runs, fails if any job failed or was cancelled, and passes when jobs
were skipped on purpose. Requiring `test (...)` or `publish layout` directly would leave a pull request waiting forever
whenever those jobs are legitimately skipped.

## Other workflows

* **codeql** scans C# only when `src/` or a build input changes, plus a weekly scan of everything.
* **release** (tag `vX.Y.Z[-suffix]`) runs the full build and test with the encoder tools staged, then publishes through
  the same `scripts/publish-layout.ps1` that `ci` validates. Releases are never cancelled part way.
* **tool-updates** (weekly) proposes new encoder versions and tests them against `Core.Tests`, the only project that
  runs the real encoders.

## Working on the planner

```powershell
pwsh scripts/detect-changes.ps1 -Base origin/main                      # what would this branch run?
pwsh scripts/detect-changes.ps1 -ChangedFile src/CliBridge/Program.cs  # what-if for one file
pwsh scripts/test-detect-changes.ps1                                   # the planner's own tests
```

## Clipboard tests

`ClipboardMonitorTests` drive the real Windows clipboard, so they are opt-in (`SQUISH_CLIPBOARD_TESTS=1`) and never run
in CI. Quit the Squish app before running them: it rewrites optimised images on the clipboard and the monitor correctly
reports that as an extra change.
