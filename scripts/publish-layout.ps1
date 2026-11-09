#requires -Version 7.0
<#
.SYNOPSIS
    Publishes the app, CLI and Explorer integration into one folder and checks the layout is complete.

.DESCRIPTION
    Shared by the ci and release workflows so both ship exactly the same layout. Every native command is checked: a
    failed `dotnet publish` stops the script instead of surfacing later as a missing file (or not at all). The tools
    folder must already be staged (scripts/fetch-tools.ps1).

.EXAMPLE
    pwsh scripts/publish-layout.ps1 -Output out/publish
#>
[CmdletBinding()]
param(
    [string]$Output = 'out/publish',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) '..')).Path
Push-Location $RepoRoot
try {
    function Invoke-Publish([string]$Project, [string]$Destination, [bool]$SelfContained) {
        Write-Host "Publishing $Project -> $Destination"
        $selfContainedArg = "--self-contained:$($SelfContained.ToString().ToLowerInvariant())"
        dotnet publish $Project --configuration $Configuration --runtime $Runtime $selfContainedArg --output $Destination
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Project (exit code $LASTEXITCODE)." }
    }

    if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
    New-Item -ItemType Directory -Path $Output | Out-Null

    Invoke-Publish 'src/App/Squish.App.csproj' (Join-Path $Output 'app') $false
    # The CLI shares the app folder (Squish.Core.dll, Magick.NET and its native library are already there) instead of a
    # second self-contained copy with its own runtime. It runs on the same .NET runtime the app requires.
    Invoke-Publish 'src/CliBridge/Squish.CliBridge.csproj' (Join-Path $Output 'app') $false
    Invoke-Publish 'src/Integrations/Explorer/Squish.Integrations.Explorer.csproj' (Join-Path $Output 'explorer') $false

    $docsTarget = Join-Path $Output 'docs'
    New-Item -ItemType Directory -Path $docsTarget -Force | Out-Null
    foreach ($doc in 'docs/windows-deps.md', 'docs/cli.md', 'docs/automation-samples.md') {
        Copy-Item $doc $docsTarget -Force
    }

    Copy-Item 'installer/SQUISH_LICENSE.txt' $Output -Force

    if (-not (Test-Path 'tools') -or -not (Get-ChildItem 'tools' -Directory -ErrorAction SilentlyContinue)) {
        throw 'The tools folder is empty. Run scripts/fetch-tools.ps1 first.'
    }
    $toolsTarget = Join-Path $Output 'tools'
    New-Item -ItemType Directory -Path $toolsTarget | Out-Null
    Copy-Item 'tools/*' $toolsTarget -Recurse -Force

    $expected = @(
        'app/Squish.exe',
        'app/Squish.CliBridge.exe',
        'explorer/Squish.Integrations.Explorer.comhost.dll',
        'tools/pngquant',
        'docs/windows-deps.md',
        'docs/cli.md',
        'docs/automation-samples.md',
        'SQUISH_LICENSE.txt'
    )
    $missing = @($expected | Where-Object { -not (Test-Path (Join-Path $Output $_)) })
    if ($missing.Count -gt 0) {
        throw "The publish layout is missing: $($missing -join ', ')"
    }

    Write-Host "Publish layout is complete: $((Resolve-Path $Output).Path)"
} finally {
    Pop-Location
}
