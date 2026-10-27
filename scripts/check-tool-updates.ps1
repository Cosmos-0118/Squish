#requires -Version 7.0
<#
.SYNOPSIS
    Looks for newer releases of the encoders pinned in tools/tools-manifest.json.

.DESCRIPTION
    Each manifest entry has an "updateSource" saying where its upstream announces releases. For every tool with a
    newer version the script works out the new download URL (the pinned version is swapped for the new one in "url"
    and "contentRoot"), downloads it, records its SHA256 and checks that the files the app needs are still where the
    manifest expects them.

    Nothing is installed. With -Apply the manifest is rewritten; review the diff (and the release notes) before
    merging, because a new encoder can change file sizes and quality. The weekly tool-updates workflow runs this,
    tests the result against the real encoders and opens a pull request.

.EXAMPLE
    pwsh scripts/check-tool-updates.ps1            # report only
    pwsh scripts/check-tool-updates.ps1 -Apply     # also rewrite the manifest
#>
[CmdletBinding()]
param(
    [string]$ManifestPath = "tools/tools-manifest.json",
    [string[]]$Name,
    [switch]$Apply,
    [string]$SummaryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) '..')).Path
if (-not [System.IO.Path]::IsPathRooted($ManifestPath)) {
    $ManifestPath = Join-Path $RepoRoot $ManifestPath
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$apiHeaders = @{ 'User-Agent' = 'squish-tool-updates'; 'Accept' = 'application/vnd.github+json' }
if ($env:GITHUB_TOKEN) {
    $apiHeaders['Authorization'] = "Bearer $env:GITHUB_TOKEN"
}

# Plain numeric versions only: release candidates and odd tags (e.g. "webp-rfc9649") are never offered.
function ConvertTo-Version([string]$Text) {
    $trimmed = $Text.Trim() -replace '^v', ''
    if ($trimmed -notmatch '^\d+(\.\d+){0,3}$') { return $null }
    $parts = $trimmed.Split('.')
    while ($parts.Count -lt 2) { $parts += '0' }
    return [version]($parts -join '.')
}

function Get-LatestRelease([psobject]$Tool) {
    $source = $Tool.updateSource
    switch ($source.type) {
        'github-release' {
            $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$($source.repo)/releases/latest" -Headers $apiHeaders
            return @{ Version = ($release.tag_name -replace '^v', ''); Notes = $release.html_url }
        }
        'github-tags' {
            $tags = Invoke-RestMethod -Uri "https://api.github.com/repos/$($source.repo)/tags?per_page=100" -Headers $apiHeaders
            $best = $tags | Where-Object { ConvertTo-Version $_.name } | Sort-Object { ConvertTo-Version $_.name } -Descending | Select-Object -First 1
            if (-not $best) { throw "No version tags found in $($source.repo)." }
            return @{ Version = ($best.name -replace '^v', ''); Notes = "https://github.com/$($source.repo)/releases/tag/$($best.name)" }
        }
        'text' {
            $text = (Invoke-WebRequest -Uri $source.url -UseBasicParsing).Content
            if ($text -is [byte[]]) { $text = [System.Text.Encoding]::UTF8.GetString($text) }
            return @{ Version = $text.Trim(); Notes = $source.notes }
        }
        'page-regex' {
            $html = (Invoke-WebRequest -Uri $source.url -UseBasicParsing).Content
            $versions = [regex]::Matches($html, $source.pattern) | ForEach-Object { $_.Groups['version'].Value } |
                Where-Object { ConvertTo-Version $_ } | Sort-Object { ConvertTo-Version $_ } -Descending
            if (-not $versions) { throw "Pattern '$($source.pattern)' matched nothing on $($source.url)." }
            return @{ Version = @($versions)[0]; Notes = $source.url }
        }
        default { throw "Unknown updateSource type '$($source.type)'." }
    }
}

function Get-RemoteFile([string]$Url, [string]$Directory) {
    $path = Join-Path $Directory ([System.IO.Path]::GetFileName(([uri]$Url).AbsolutePath))
    Invoke-WebRequest -Uri $Url -OutFile $path -UseBasicParsing -Headers @{ 'User-Agent' = 'squish-tool-updates' }
    return $path
}

# Catch upstream layout changes (a renamed folder or a missing exe) here rather than in a release build.
function Test-ArchiveLayout([psobject]$Tool, [string]$ArchivePath, [string]$ContentRoot) {
    if ($Tool.archiveType -ne 'zip') { return @() }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    }
    finally {
        $zip.Dispose()
    }

    $problems = @()
    if ($ContentRoot -and -not ($entries | Where-Object { $_.StartsWith("$ContentRoot/", [System.StringComparison]::OrdinalIgnoreCase) })) {
        $problems += "folder '$ContentRoot' is no longer in the archive"
    }
    if ($Tool.PSObject.Properties['include']) {
        foreach ($file in $Tool.include) {
            if (-not ($entries | Where-Object { $_.EndsWith("/$file", [System.StringComparison]::OrdinalIgnoreCase) -or $_ -ieq $file })) {
                $problems += "'$file' is no longer in the archive"
            }
        }
    }
    return $problems
}

# pngquant only publishes an unversioned "latest" link, so a change in the file's checksum is the release signal.
# The pinned copy is kept on the Internet Archive as the fallback URL that fetch-tools uses once upstream moves on.
function Get-ContentChange([psobject]$Tool, [string]$WorkDir) {
    $upstream = @($Tool.urls)[0]
    $archive = Get-RemoteFile -Url $upstream -Directory $WorkDir
    $sha = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sha -eq $Tool.sha256.ToLowerInvariant()) { return $null }

    $version = 'unknown'
    if ($IsWindows -and $Tool.updateSource.PSObject.Properties['versionExe']) {
        $extract = Join-Path $WorkDir 'extract'
        Expand-Archive -Path $archive -DestinationPath $extract -Force
        $exe = Get-ChildItem -Path $extract -Recurse -Filter $Tool.updateSource.versionExe | Select-Object -First 1
        if ($exe) {
            $output = (& $exe.FullName --version 2>&1 | Out-String)
            if ($output -match '(\d+\.\d+(\.\d+)?)') { $version = $Matches[1] }
        }
    }

    $snapshot = $null
    try {
        $response = Invoke-WebRequest -Uri "https://web.archive.org/save/$upstream" -UseBasicParsing -MaximumRedirection 0 -SkipHttpErrorCheck
        $location = "$($response.Headers['Location'])$($response.Headers['Content-Location'])"
        if ($location -match '/web/(\d{14})') { $snapshot = "https://web.archive.org/web/$($Matches[1])id_/$upstream" }
    }
    catch {
        Write-Warning "    Could not archive the new $($Tool.name) download: $($_.Exception.Message)"
    }

    return @{ Version = $version; Sha = $sha; Archive = $archive; Snapshot = $snapshot; Upstream = $upstream }
}

$manifestText = Get-Content -Raw -LiteralPath $ManifestPath
$newline = if ($manifestText.Contains("`r`n")) { "`r`n" } else { "`n" }
$manifest = $manifestText | ConvertFrom-Json
$tools = @($manifest.tools)
if ($Name) {
    $tools = @($tools | Where-Object { $Name -contains $_.name })
    if ($tools.Count -eq 0) {
        throw "No manifest entries match -Name $($Name -join ', ')."
    }
}

$updates = [System.Collections.Generic.List[string]]::new()
$problems = [System.Collections.Generic.List[string]]::new()
$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) "squish-tool-updates-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $workRoot | Out-Null

try {
    foreach ($tool in $tools) {
        if (-not $tool.PSObject.Properties['updateSource']) {
            Write-Host "==> $($tool.name): no updateSource, skipped"
            continue
        }

        $workDir = Join-Path $workRoot $tool.name
        New-Item -ItemType Directory -Path $workDir | Out-Null
        try {
            if ($tool.updateSource.type -eq 'content') {
                $change = Get-ContentChange -Tool $tool -WorkDir $workDir
                if (-not $change) {
                    Write-Host "==> $($tool.name) $($tool.version): up to date"
                    continue
                }

                Write-Host "==> $($tool.name): upstream download changed (now $($change.Version))" -ForegroundColor Yellow
                $layout = Test-ArchiveLayout -Tool $tool -ArchivePath $change.Archive -ContentRoot $(if ($tool.PSObject.Properties['contentRoot']) { $tool.contentRoot })
                if ($layout) {
                    $problems.Add("**$($tool.name)**: the new download needs a manual look: $($layout -join '; ').")
                    continue
                }

                $note = if ($change.Snapshot) { '' } else { ' The archived fallback copy could not be created, so `urls` still lists only the upstream link: add a pinned mirror before merging.' }
                $updates.Add("**$($tool.name)** $($tool.version) → $($change.Version): the unversioned upstream download changed ($($tool.updateSource.notes)).$note")
                if ($Apply) {
                    $tool.version = $change.Version
                    $tool.sha256 = $change.Sha
                    $tool.urls = @($change.Upstream) + @($change.Snapshot | Where-Object { $_ })
                }
                continue
            }

            $latest = Get-LatestRelease -Tool $tool
            $current = ConvertTo-Version $tool.version
            $candidate = ConvertTo-Version $latest.Version
            if (-not $candidate -or ($current -and $candidate -le $current)) {
                Write-Host "==> $($tool.name) $($tool.version): up to date"
                continue
            }

            Write-Host "==> $($tool.name): $($tool.version) -> $($latest.Version)" -ForegroundColor Yellow
            $newUrl = $tool.url.Replace($tool.version, $latest.Version)
            $newRoot = if ($tool.PSObject.Properties['contentRoot']) { $tool.contentRoot.Replace($tool.version, $latest.Version) } else { $null }
            if ($newUrl -eq $tool.url) {
                $problems.Add("**$($tool.name)** $($latest.Version) is out ($($latest.Notes)), but its download link does not contain the version, so it needs a manual update.")
                continue
            }

            try {
                $archive = Get-RemoteFile -Url $newUrl -Directory $workDir
            }
            catch {
                $problems.Add("**$($tool.name)** $($latest.Version) is out ($($latest.Notes)), but ``$newUrl`` could not be downloaded ($($_.Exception.Message)). The asset was probably renamed.")
                continue
            }

            $layout = Test-ArchiveLayout -Tool $tool -ArchivePath $archive -ContentRoot $newRoot
            if ($layout) {
                $problems.Add("**$($tool.name)** $($latest.Version) ($($latest.Notes)) needs a manual look: $($layout -join '; ').")
                continue
            }

            $sha = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
            $updates.Add("**$($tool.name)** $($tool.version) → $($latest.Version) ([release notes]($($latest.Notes)))")
            if ($Apply) {
                $tool.version = $latest.Version
                $tool.url = $newUrl
                $tool.sha256 = $sha
                if ($newRoot) { $tool.contentRoot = $newRoot }
            }
        }
        catch {
            $problems.Add("**$($tool.name)**: the update check failed: $($_.Exception.Message)")
        }
    }
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $workRoot -ErrorAction SilentlyContinue
}

if ($Apply -and $updates.Count -gt 0) {
    # Keep the file's line endings so the pull request only shows the changed values.
    $json = ($manifest | ConvertTo-Json -Depth 10) -replace '\r?\n', $newline
    $json + $newline | Set-Content -LiteralPath $ManifestPath -NoNewline -Encoding utf8NoBOM
}

$lines = @()
if ($updates.Count -gt 0) {
    $lines += 'New encoder releases:', ''
    $lines += $updates | ForEach-Object { "- $_" }
    $lines += '', 'Each download was fetched and its SHA256 pinned. Before merging, read the release notes and, for jpegli, mozjpeg, pngquant and oxipng, compare file sizes on a sample set: a new encoder can change savings and quality scores.'
}
if ($problems.Count -gt 0) {
    $lines += '', 'Needs attention:', ''
    $lines += $problems | ForEach-Object { "- $_" }
}
if ($lines.Count -eq 0) {
    $lines = @('All encoders are up to date.')
}

$summary = $lines -join "`n"
Write-Host ''
Write-Host $summary
if ($SummaryPath) {
    $summary | Set-Content -LiteralPath $SummaryPath -Encoding utf8NoBOM
}
if ($env:GITHUB_ACTIONS -eq 'true') {
    foreach ($problem in $problems) {
        Write-Host "::warning title=Encoder update needs attention::$($problem -replace '\*\*', '')"
    }
}
if ($env:GITHUB_OUTPUT) {
    "updated=$(($Apply -and $updates.Count -gt 0).ToString().ToLowerInvariant())" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    "problems=$(($problems.Count -gt 0).ToString().ToLowerInvariant())" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
}
