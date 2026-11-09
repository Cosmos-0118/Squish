#requires -Version 7.0
<#
.SYNOPSIS
    Self-tests for scripts/detect-changes.ps1: pins down which jobs each kind of change selects.

.DESCRIPTION
    Runs the detector in what-if mode (-ChangedFile) against the real project graph, so the expectations below also
    break when someone changes the ProjectReference structure in a way that alters what gets tested. Exits non-zero
    on the first mismatch report.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$detector = Join-Path (Split-Path -Parent $PSCommandPath) 'detect-changes.ps1'
$failures = 0

function Get-Plan([string[]]$Files) {
    $json = & pwsh -NoProfile -File $detector -ChangedFile ($Files -join ',') -AsJson
    if ($LASTEXITCODE -ne 0) { throw "detect-changes.ps1 failed for: $($Files -join ', ')" }
    return $json | ConvertFrom-Json
}

function Assert-Plan {
    param(
        [Parameter(Mandatory)][string]$Case,
        [Parameter(Mandatory)][string[]]$Files,
        [string[]]$Tests = @(),
        [bool]$Publish = $false,
        [bool]$Lint = $false,
        [bool]$RunAll = $false,
        [string[]]$Tools = @()
    )

    $plan = Get-Plan $Files
    $actualTests = @($plan.test_matrix.include | ForEach-Object { $_.name } | Sort-Object)
    $actualTools = @($plan.test_matrix.include | Where-Object { $_.tools } | ForEach-Object { $_.name } | Sort-Object)
    $problems = @()

    if (($actualTests -join ',') -ne (@($Tests | Sort-Object) -join ',')) { $problems += "tests: expected [$($Tests -join ', ')] got [$($actualTests -join ', ')]" }
    if (($actualTools -join ',') -ne (@($Tools | Sort-Object) -join ',')) { $problems += "tools: expected [$($Tools -join ', ')] got [$($actualTools -join ', ')]" }
    if ([bool]$plan.publish -ne $Publish) { $problems += "publish: expected $Publish got $($plan.publish)" }
    if ([bool]$plan.lint -ne $Lint) { $problems += "lint: expected $Lint got $($plan.lint)" }
    if ([bool]$plan.run_all -ne $RunAll) { $problems += "run_all: expected $RunAll got $($plan.run_all)" }

    if ($problems.Count -eq 0) {
        Write-Host "PASS  $Case"
    } else {
        $script:failures++
        Write-Host "FAIL  $Case"
        $problems | ForEach-Object { Write-Host "        $_" }
    }
}

$all = 'BackgroundService.Tests', 'CliBridge.Tests', 'Core.Tests', 'Squish.App.Tests'

# Source changes follow the project reference graph.
Assert-Plan 'CLI change runs only CLI tests' -Files 'src/CliBridge/Program.cs' -Tests 'CliBridge.Tests' -Publish $true
Assert-Plan 'Core change runs everything downstream' -Files 'src/Core/Imaging/ImageIO.cs' -Tests $all -Tools 'Core.Tests' -Publish $true
Assert-Plan 'Background service change runs its tests and the app tests' -Files 'src/BackgroundService/Worker.cs' -Tests 'BackgroundService.Tests', 'Squish.App.Tests' -Publish $true
Assert-Plan 'App change runs only app tests' -Files 'src/App/MainWindow.xaml' -Tests 'Squish.App.Tests' -Publish $true
Assert-Plan 'Explorer integration has no tests but still publishes' -Files 'src/Integrations/Explorer/ExplorerRegistration.cs' -Publish $true

# Test-only changes.
Assert-Plan 'Editing one test project runs just that project' -Files 'tests/BackgroundService.Tests/ClipboardMonitorTests.cs' -Tests 'BackgroundService.Tests'
Assert-Plan 'Benchmarks are not a test project' -Files 'tests/Benchmarks/Program.cs'

# Nothing to build.
Assert-Plan 'README only' -Files 'README.md'
Assert-Plan 'Docs only' -Files 'docs/brand.md', 'docs/images/x.png', 'SECURITY.md'
Assert-Plan 'Installer script only' -Files 'installer/SquishInstaller.iss'
Assert-Plan 'Dependabot config only' -Files '.github/dependabot.yml'
Assert-Plan 'Release notes only' -Files '.github/release-notes/v2.3.3.md'
Assert-Plan 'Editor settings only' -Files '.vscode/settings.json'

# Things that ship in the publish layout.
Assert-Plan 'Docs copied into the package publish' -Files 'docs/cli.md' -Publish $true
Assert-Plan 'Publish script' -Files 'scripts/publish-layout.ps1' -Publish $true
Assert-Plan 'Tool manifest re-runs the encoder tests and publish' -Files 'tools/tools-manifest.json' -Tests 'Core.Tests' -Tools 'Core.Tests' -Publish $true
Assert-Plan 'ffmpeg build recipe re-runs the encoder tests and publish' -Files 'scripts/ffmpeg-slim/build-local.sh' -Tests 'Core.Tests' -Tools 'Core.Tests' -Publish $true
Assert-Plan 'ffmpeg toolchain action re-runs the encoder tests and publish' -Files '.github/actions/prepare-ffmpeg-build/action.yml' -Tests 'Core.Tests' -Tools 'Core.Tests' -Publish $true

# Workflow plumbing.
Assert-Plan 'Another workflow lints only' -Files '.github/workflows/codeql.yml' -Lint $true
Assert-Plan 'The detector itself lints only' -Files 'scripts/detect-changes.ps1' -Lint $true

# Fail safe: anything global or unknown runs the lot.
Assert-Plan 'CI definition runs everything' -Files '.github/workflows/ci.yml' -Tests $all -Tools 'Core.Tests' -Publish $true -Lint $true -RunAll $true
Assert-Plan 'Package versions run everything' -Files 'Directory.Packages.props' -Tests $all -Tools 'Core.Tests' -Publish $true -Lint $true -RunAll $true
Assert-Plan 'SDK pin runs everything' -Files 'global.json' -Tests $all -Tools 'Core.Tests' -Publish $true -Lint $true -RunAll $true
Assert-Plan 'Solution file runs everything' -Files 'src/Squish.sln' -Tests $all -Tools 'Core.Tests' -Publish $true -Lint $true -RunAll $true
Assert-Plan 'Unrecognised path runs everything' -Files 'mystery/file.bin' -Tests $all -Tools 'Core.Tests' -Publish $true -Lint $true -RunAll $true
Assert-Plan 'Stray file under src runs everything' -Files 'src/orphan.cs' -Tests $all -Tools 'Core.Tests' -Publish $true -Lint $true -RunAll $true

# Mixed changes combine.
Assert-Plan 'CLI change plus README' -Files 'src/CliBridge/Program.cs', 'README.md' -Tests 'CliBridge.Tests' -Publish $true
Assert-Plan 'App plus CLI change' -Files 'src/App/MainWindow.xaml', 'src/CliBridge/Program.cs' -Tests 'CliBridge.Tests', 'Squish.App.Tests' -Publish $true

# A full run is a full run, and an unusable diff falls back to one.
$full = & pwsh -NoProfile -File $detector -RunAll -AsJson | ConvertFrom-Json
if ($full.run_all -and @($full.test_matrix.include).Count -eq $all.Count) {
    Write-Host 'PASS  -RunAll selects everything'
} else {
    $failures++
    Write-Host 'FAIL  -RunAll selects everything'
}

# A real, empty diff (the same commit on both sides) selects nothing.
$empty = & pwsh -NoProfile -File $detector -Base HEAD -Head HEAD -AsJson | ConvertFrom-Json
if (-not $empty.run_all -and -not $empty.has_tests -and -not $empty.publish -and -not $empty.lint) {
    Write-Host 'PASS  An empty diff selects nothing'
} else {
    $failures++
    Write-Host 'FAIL  An empty diff selects nothing'
}

$noBase =& pwsh -NoProfile -File $detector -Base '0000000000000000000000000000000000000000' -AsJson | ConvertFrom-Json
if ($noBase.run_all) {
    Write-Host 'PASS  A missing base commit selects everything'
} else {
    $failures++
    Write-Host 'FAIL  A missing base commit selects everything'
}

if ($failures -gt 0) {
    Write-Host ''
    Write-Host "$failures check(s) failed."
    exit 1
}

Write-Host ''
Write-Host 'All change-detection checks passed.'
