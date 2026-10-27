#requires -Version 7.0
<#
.SYNOPSIS
    Hashes the release files, writes SHA256SUMS.txt and refreshes the checksum block in README.md.

.DESCRIPTION
    Used by the release workflow after the installer and portable zip are built. It

      1. computes the SHA256 of every file passed in -Files,
      2. writes them to -ChecksumsPath in `sha256sum` format (hash, space, asterisk, file name), so the file can be
         checked with `sha256sum -c` as well as by hand,
      3. rewrites the text between the release-integrity markers in the README with the new version, file names,
         hashes and the matching verify commands.

    The README is only touched when the block actually changes, so running it twice is harmless.

.EXAMPLE
    pwsh scripts/update-release-integrity.ps1 -Version 2.3.0 -Files artifacts/Squish-Setup-2.3.0.exe,artifacts/Squish-2.3.0-win-x64.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string[]]$Files,
    [string]$FromChecksums,
    [string]$ReadmePath = "README.md",
    [string]$ChecksumsPath,
    [string]$Repository = "Cosmos-0118/Clop-For-Windows"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) '..')).Path
if (-not [System.IO.Path]::IsPathRooted($ReadmePath)) {
    $ReadmePath = Join-Path $RepoRoot $ReadmePath
}

$Version = $Version.TrimStart('v')
$startMarker = '<!-- release-integrity:start -->'
$endMarker = '<!-- release-integrity:end -->'

if ($FromChecksums) {
    # Reuse a SHA256SUMS.txt published with the release instead of hashing the files again.
    $entries = foreach ($line in Get-Content -LiteralPath $FromChecksums) {
        if ($line -match '^(?<hash>[0-9a-fA-F]{64}) [ *](?<name>.+)$') {
            [pscustomobject]@{ Name = $Matches.name; Hash = $Matches.hash.ToLowerInvariant() }
        }
    }
} elseif ($Files) {
    $entries = foreach ($file in $Files) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Release file not found: $file" }
        [pscustomobject]@{
            Name = Split-Path -Leaf $file
            Hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
} else {
    throw 'Pass -Files or -FromChecksums.'
}
if (-not $entries) { throw 'No release files to describe.' }

if ($ChecksumsPath) {
    # LF line endings and no BOM, so `sha256sum -c` accepts the file on every platform.
    $text = (($entries | ForEach-Object { "$($_.Hash) *$($_.Name)" }) -join "`n") + "`n"
    [System.IO.File]::WriteAllText($ChecksumsPath, $text, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Wrote $ChecksumsPath"
}

$installer = $entries | Where-Object { $_.Name -like '*Setup*.exe' } | Select-Object -First 1
if (-not $installer) { $installer = $entries | Select-Object -First 1 }

$tag = "v$Version"
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("- **Latest release:** [Squish $Version](https://github.com/$Repository/releases/tag/$tag)")
$lines.Add("- **SHA256 checksums:**")
$lines.Add("")
$lines.Add("  | File | SHA256 |")
$lines.Add("  | --- | --- |")
foreach ($entry in $entries) {
    $lines.Add("  | ``$($entry.Name)`` | ``$($entry.Hash)`` |")
}
$lines.Add("")
$lines.Add("  The same values are published as ``SHA256SUMS.txt`` on the release page.")
$lines.Add("")
$lines.Add("- **Verify locally** (run it in the folder you downloaded to):")
$lines.Add("")
$lines.Add("  ``````powershell")
$lines.Add("  CertUtil -hashfile $($installer.Name) SHA256")
$lines.Add("  ``````")
$lines.Add("")
$lines.Add("  Or have PowerShell compare it for you; it prints ``True`` when the file is genuine:")
$lines.Add("")
$lines.Add("  ``````powershell")
$lines.Add("  (Get-FileHash .\$($installer.Name) -Algorithm SHA256).Hash -eq '$($installer.Hash)'")
$lines.Add("  ``````")
$block = $lines -join "`n"

$readme = [System.IO.File]::ReadAllText($ReadmePath)
$eol = if ($readme.Contains("`r`n")) { "`r`n" } else { "`n" }
$start = $readme.IndexOf($startMarker, [StringComparison]::Ordinal)
$end = $readme.IndexOf($endMarker, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -lt $start) {
    throw "README is missing the '$startMarker' / '$endMarker' markers."
}

$replacement = $startMarker + $eol + ($block -replace "`n", $eol) + $eol + $endMarker
$updated = $readme.Substring(0, $start) + $replacement + $readme.Substring($end + $endMarker.Length)

if ($updated -ceq $readme) {
    Write-Host "README already up to date for $tag."
} else {
    [System.IO.File]::WriteAllText($ReadmePath, $updated, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Updated $ReadmePath for $tag."
}

if ($env:GITHUB_OUTPUT) {
    "installer=$($installer.Name)" | Add-Content $env:GITHUB_OUTPUT
    "sha256=$($installer.Hash)" | Add-Content $env:GITHUB_OUTPUT
}
