#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ManifestPath = "tools/tools-manifest.json",
    [string]$DestinationRoot = "tools",
    [string[]]$Name,
    [string[]]$Exclude,
    [switch]$ListOnly,
    [switch]$Force,
    [switch]$SkipChecksum,
    [switch]$WriteMissingChecksums
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ScriptRoot = Split-Path -Parent $PSCommandPath
$RepoRoot = (Resolve-Path (Join-Path $ScriptRoot '..')).Path

function New-TemporaryDirectory {
    $path = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ([System.Guid]::NewGuid().ToString())
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return $path
}

function Get-Checksum([string]$Path) {
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Copy-ArchivePayload {
    # Copies an extracted archive into the tool folder. Without "include" everything is copied. With it, only the listed
    # files are kept (flattened into the destination): release archives often bundle dev headers, static libs, docs and
    # sibling tools that Squish never runs, and every byte of that ships in the installer. A plain name takes the first
    # match; a wildcard (for example *.dll) takes every match. A pattern that matches nothing fails the fetch, so a
    # renamed upstream file cannot silently ship a broken tool.
    param(
        [Parameter(Mandatory = $true)] $Tool,
        [Parameter(Mandatory = $true)] [string]$SourcePath,
        [Parameter(Mandatory = $true)] [string]$Destination
    )

    if (-not $Tool.PSObject.Properties['include']) {
        Copy-Item -Path (Join-Path $SourcePath '*') -Destination $Destination -Recurse -Force
        return
    }

    foreach ($fileName in $Tool.include) {
        $found = @(Get-ChildItem -LiteralPath $SourcePath -Recurse -File -Filter $fileName)
        if ($found.Count -eq 0) { throw "'$fileName' not found in the $($Tool.name) archive." }
        if (-not [System.Management.Automation.WildcardPattern]::ContainsWildcardCharacters($fileName)) {
            $found = @($found | Select-Object -First 1)
        }
        foreach ($match in $found) {
            Copy-Item -LiteralPath $match.FullName -Destination $Destination -Force
        }
    }
}

function Resolve-RepoPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path -Path $RepoRoot -ChildPath $Path
}

function Invoke-FileDownload {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Urls,
        [Parameter(Mandatory = $true)]
        [string]$OutFile,
        [int]$MaxAttemptsPerUrl = 6,
        [int]$BaseDelaySeconds = 5
    )

    $candidateUrls = @($Urls | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    if ($candidateUrls.Count -eq 0) {
        throw "No download URLs were provided."
    }

    $errors = [System.Collections.Generic.List[string]]::new()

    foreach ($url in $candidateUrls) {
        for ($attempt = 1; $attempt -le $MaxAttemptsPerUrl; $attempt++) {
            try {
                Write-Host "    Downloading (attempt $attempt/$MaxAttemptsPerUrl): $url"
                Invoke-WebRequest -Uri $url -OutFile $OutFile -UseBasicParsing
                return $url
            }
            catch {
                if (Test-Path -LiteralPath $OutFile) {
                    Remove-Item -LiteralPath $OutFile -Force -ErrorAction SilentlyContinue
                }

                # Strict mode is on, so reading .Response directly throws on exceptions that have no such property
                # (timeouts, connection resets, cancelled requests). That used to abort the whole script and skip
                # both the retries and the fallback URLs, so look the property up safely.
                $statusCode = $null
                $responseProperty = $_.Exception.PSObject.Properties['Response']
                if ($responseProperty -and $responseProperty.Value -and $responseProperty.Value.StatusCode) {
                    $statusCode = [int]$responseProperty.Value.StatusCode
                }

                $errorText = "[$url attempt $attempt] $($_.Exception.Message)"
                $errors.Add($errorText) | Out-Null

                if ($statusCode -eq 404) {
                    Write-Warning "    URL not found (404). Trying next candidate URL if available."
                    break
                }

                if ($attempt -lt $MaxAttemptsPerUrl) {
                    # Why the waits are this long: a rarely downloaded GitHub release asset is cold at GitHub's edge. The first
                    # request waits about 11 seconds for storage and gets "504 Gateway Time-out", and that failure is then
                    # served again within a second or two. So quick retries (and a changed query string, which is another
                    # cold URL) all fail; the same URL succeeds once the edge has fetched the file.
                    $delaySeconds = [Math]::Min(60, [int]([Math]::Pow(2, ($attempt - 1)) * $BaseDelaySeconds))
                    Write-Warning "    Download failed. Retrying in $delaySeconds seconds..."
                    Start-Sleep -Seconds $delaySeconds
                    continue
                }
            }
        }
    }

    throw "Failed to download from all candidate URLs.`n$($errors -join "`n")"
}

# Ensure defaults behave consistently no matter where the script is invoked from.
# Relative paths are resolved from the repo root, not the caller's current directory.
if ($SkipChecksum -and $env:CI) {
    throw "-SkipChecksum is not allowed in CI. Downloads must always match the pinned SHA256 in the manifest."
}
if ($SkipChecksum) {
    Write-Warning "Checksum verification is DISABLED. Only use -SkipChecksum on a throwaway machine."
}

$ManifestPath = Resolve-RepoPath $ManifestPath
$DestinationRoot = Resolve-RepoPath $DestinationRoot

function Test-HasChildItems([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }
    $child = Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue | Select-Object -First 1
    return $null -ne $child
}

function Get-SevenZipPath {
    $candidates = @('7z.exe', '7za.exe')
    foreach ($candidate in $candidates) {
        $cmd = Get-Command -Name $candidate -ErrorAction SilentlyContinue
        if ($cmd) {
            return $cmd.Source
        }
    }

    $fallbacks = @()
    if ($env:ProgramFiles) {
        $fallbacks += Join-Path -Path $env:ProgramFiles -ChildPath '7-Zip/7z.exe'
    }
    if (${env:ProgramFiles(x86)}) {
        $fallbacks += Join-Path -Path ${env:ProgramFiles(x86)} -ChildPath '7-Zip/7z.exe'
    }
    foreach ($path in $fallbacks) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }

    throw "7-Zip executable not found on PATH or standard install locations. Install 7-Zip and retry."
}

function Expand-WithSevenZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchivePath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $sevenZip = Get-SevenZipPath
    Write-Host "    Extracting with 7-Zip ($sevenZip)..."
    $arguments = @('x', "-o$DestinationPath", '-y', $ArchivePath)
    $process = Start-Process -FilePath $sevenZip -ArgumentList $arguments -NoNewWindow -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "7-Zip extraction failed with exit code $($process.ExitCode)."
    }
}

function Invoke-InstallerProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string]$Arguments,
        [switch]$ForceInvoker
    )

    $previousCompatLayer = $null
    $hadPrevious = $false
    if ($ForceInvoker) {
        if (Test-Path Env:__COMPAT_LAYER) {
            $previousCompatLayer = $env:__COMPAT_LAYER
            $hadPrevious = $true
        }
        $env:__COMPAT_LAYER = 'RunAsInvoker'
    }

    try {
        return Start-Process -FilePath $FilePath -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    }
    finally {
        if ($ForceInvoker) {
            if ($hadPrevious) {
                $env:__COMPAT_LAYER = $previousCompatLayer
            }
            else {
                Remove-Item Env:__COMPAT_LAYER -ErrorAction SilentlyContinue
            }
        }
    }
}

$checksumContentCache = @{}

function Resolve-ExpectedChecksum([psobject]$tool) {
    if ($tool.PSObject.Properties['sha256'] -and $tool.sha256) {
        return $tool.sha256.ToLowerInvariant()
    }

    if (-not $tool.PSObject.Properties['checksumSource']) {
        return $null
    }

    $source = $tool.checksumSource
    if (-not $source.PSObject.Properties['url'] -or [string]::IsNullOrWhiteSpace($source.url)) {
        throw "checksumSource for $($tool.name) must include a url."
    }

    $url = $source.url
    if (-not $checksumContentCache.ContainsKey($url)) {
        Write-Host "    Downloading checksum manifest: $url"
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing
        $rawContent = $response.Content
        if ($rawContent -is [byte[]]) {
            $encoding = [System.Text.Encoding]::UTF8
            $contentType = $null
            if ($response.PSObject.Properties['ContentType']) {
                $contentType = $response.ContentType
            }
            elseif ($response.Headers -and $response.Headers['Content-Type']) {
                $contentType = $response.Headers['Content-Type']
            }

            if ($contentType -and ($contentType -match 'charset=([^;]+)')) {
                try {
                    $encoding = [System.Text.Encoding]::GetEncoding($Matches[1])
                }
                catch {
                    Write-Warning "    Unknown charset '$($Matches[1])' in checksum manifest. Falling back to UTF-8."
                }
            }

            $checksumContentCache[$url] = $encoding.GetString($rawContent)
        }
        else {
            $checksumContentCache[$url] = $rawContent
        }
    }

    $content = $checksumContentCache[$url]
    if ([string]::IsNullOrWhiteSpace($content)) {
        throw "Checksum manifest at $url was empty."
    }

    $explicitPattern = $null
    if ($source.PSObject.Properties['pattern']) {
        $explicitPattern = $source.pattern
    }

    if ($explicitPattern) {
        $regex = [System.Text.RegularExpressions.Regex]::new($explicitPattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Multiline)
        $match = $regex.Match($content)
        if (-not $match.Success -or -not $match.Groups['sha256']) {
            throw "Pattern '$explicitPattern' did not yield a checksum for $($tool.name)."
        }
        return $match.Groups['sha256'].Value.ToLowerInvariant()
    }

    if (-not $source.PSObject.Properties['fileName'] -or [string]::IsNullOrWhiteSpace($source.fileName)) {
        throw "checksumSource for $($tool.name) must specify either pattern or fileName."
    }

    $needle = $source.fileName
    $lineRegex = [System.Text.RegularExpressions.Regex]::new('^(?<sha>[0-9a-f]{64})\s+\*?(?<file>.+)$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Multiline)
    foreach ($match in $lineRegex.Matches($content)) {
        $manifestFileName = $match.Groups['file'].Value.Trim()
        if ($manifestFileName -eq $needle) {
            return $match.Groups['sha'].Value.Trim().ToLowerInvariant()
        }
    }

    throw "Checksum entry for '$needle' not found in manifest at $url."
}

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "Manifest not found: $ManifestPath"
}

function Get-RecipeHash([string]$ScriptPath) {
    # Fingerprint of a build recipe: every file next to its script (the script, its package list, ...).
    $parts = Get-ChildItem -LiteralPath (Split-Path -Parent $ScriptPath) -File | Sort-Object Name |
        ForEach-Object { "$($_.Name):$(Get-Checksum -Path $_.FullName)" }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($parts -join "`n"))
    return [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($bytes)).Replace('-', '').ToLowerInvariant()
}

function ConvertTo-MsysPath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    return '/' + $full.Substring(0, 1).ToLowerInvariant() + ($full.Substring(2) -replace '\\', '/')
}

function Install-BuiltTool {
    # Tools compiled here instead of downloaded ("archiveType": "msys2-build"). ffmpeg is built because every public
    # build links in far more than Squish uses (speech recognition, extra AV1/VVC encoders, network and disc stacks):
    # 230 MB of DLLs against about 74 MB for the build in scripts/ffmpeg-slim.
    param(
        [Parameter(Mandatory = $true)] $Tool,
        [Parameter(Mandatory = $true)] [string]$Destination,
        [Parameter(Mandatory = $true)] [string]$MetaFile
    )

    $recipeScript = Join-Path $RepoRoot $Tool.build.script
    if (-not (Test-Path -LiteralPath $recipeScript)) { throw "Build script for $($Tool.name) not found: $recipeScript" }
    $recipe = Get-RecipeHash -ScriptPath $recipeScript
    Write-Host "==> $($Tool.name) v$($Tool.version) (built from source: $($Tool.build.script))" -ForegroundColor Cyan
    Write-Host "    Destination: $Destination"

    $current = $null
    if (Test-Path -LiteralPath $MetaFile) {
        try { $current = Get-Content -Raw -LiteralPath $MetaFile | ConvertFrom-Json } catch { Write-Warning "    Unable to read existing metadata: $_" }
    }
    if ($ListOnly) {
        if ($current) { Write-Host "    Installed version: $($current.version) (built $($current.fetchedAt))" } else { Write-Host "    Not built yet." }
        return
    }
    if (-not $Force -and $current -and $current.version -eq $Tool.version -and
        $current.PSObject.Properties['recipe'] -and $current.recipe -eq $recipe) {
        Write-Host "    Already built from this recipe. Use -Force to rebuild." -ForegroundColor Yellow
        return
    }

    $msysRoot = if ($env:MSYS2_ROOT) { $env:MSYS2_ROOT } else { 'C:\msys64' }
    $bash = Join-Path $msysRoot 'usr\bin\bash.exe'
    if (-not (Test-Path -LiteralPath $bash) -and (Test-Path -LiteralPath (Join-Path $msysRoot 'msys64\usr\bin\bash.exe'))) {
        # Some installers report the folder that contains msys64 instead of msys64 itself.
        $bash = Join-Path $msysRoot 'msys64\usr\bin\bash.exe'
    }
    if (-not (Test-Path -LiteralPath $bash)) {
        throw "$($Tool.name) is built with MSYS2, but '$bash' was not found. Install MSYS2 (https://www.msys2.org), upgrade it with 'pacman -Syu', install the packages in $(Split-Path -Parent $Tool.build.script)/msys2-packages.txt, and set MSYS2_ROOT if it is not in C:\msys64. Or skip it with -Exclude $($Tool.name) and point SQUISH_FFMPEG / SQUISH_FFPROBE at your own ffmpeg."
    }

    $tempDir = New-TemporaryDirectory
    try {
        $stage = Join-Path $tempDir 'stage'
        $work = Join-Path $tempDir 'work'
        $env:MSYSTEM = 'UCRT64'
        $env:CHERE_INVOKING = '1'
        $command = "bash '$(ConvertTo-MsysPath $recipeScript)' '$($Tool.build.tag)' '$(ConvertTo-MsysPath $work)' '$(ConvertTo-MsysPath $stage)'"
        Write-Host "    Building $($Tool.name) $($Tool.build.tag); this takes a while the first time..."
        & $bash -lc $command
        if ($LASTEXITCODE -ne 0) { throw "Building $($Tool.name) failed with code $LASTEXITCODE" }
        foreach ($required in 'ffmpeg.exe', 'ffprobe.exe') {
            if (-not (Test-Path -LiteralPath (Join-Path $stage $required))) { throw "The $($Tool.name) build did not produce $required." }
        }

        if (Test-Path -LiteralPath $Destination) { Remove-Item -Recurse -Force -LiteralPath $Destination }
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        Copy-Item -Path (Join-Path $stage '*') -Destination $Destination -Recurse -Force

        [ordered]@{
            name      = $Tool.name
            version   = $Tool.version
            source    = "built from $($Tool.build.tag) with $($Tool.build.script)"
            recipe    = $recipe
            fetchedAt = (Get-Date).ToString('o')
        } | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 -LiteralPath $MetaFile
        Write-Host "    Installed $($Tool.name) into $Destination" -ForegroundColor Green
    }
    finally {
        if (Test-Path -LiteralPath $tempDir) { Remove-Item -Recurse -Force -LiteralPath $tempDir }
    }
}

$manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
if (-not $manifest.tools) {
    throw "Manifest is missing a 'tools' array."
}

$tools = $manifest.tools
if ($Name) {
    $nameSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $Name | ForEach-Object { [void]$nameSet.Add($_) }
    $tools = $tools | Where-Object { $nameSet.Contains($_.name) }
    if (-not $tools) {
        throw "No manifest entries match the provided -Name filters."
    }
}

if ($Exclude) {
    $excludeSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $Exclude | ForEach-Object { [void]$excludeSet.Add($_) }
    $tools = $tools | Where-Object { -not $excludeSet.Contains($_.name) }
    if (-not $tools) {
        Write-Warning "All manifest entries were excluded. Nothing to do."
        return
    }
}

$manifestChanged = $false

foreach ($tool in $tools) {
    $dest = Join-Path -Path $DestinationRoot -ChildPath $tool.destination
    $metaFile = Join-Path -Path $dest -ChildPath '.toolinfo.json'
    if ($tool.PSObject.Properties['archiveType'] -and $tool.archiveType -eq 'msys2-build') {
        Install-BuiltTool -Tool $tool -Destination $dest -MetaFile $metaFile
        continue
    }
    $contentRoot = $null
    if ($tool.PSObject.Properties['contentRoot']) {
        $contentRoot = $tool.contentRoot
    }
    $downloadUrls = @()
    if ($tool.PSObject.Properties['urls'] -and $tool.urls) {
        $downloadUrls += @($tool.urls)
    }
    if ($tool.PSObject.Properties['url'] -and $tool.url) {
        $downloadUrls += $tool.url
    }
    $downloadUrls = @($downloadUrls | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    if ($downloadUrls.Count -eq 0) {
        throw "Tool '$($tool.name)' is missing a download URL."
    }
    $effectiveContentRoot = $contentRoot
    $selectedSourceUrl = $downloadUrls[0]
    Write-Host "==> $($tool.name) v$($tool.version)" -ForegroundColor Cyan
    Write-Host "    Source: $selectedSourceUrl"
    Write-Host "    Destination: $dest"

    if ($ListOnly) {
        if (Test-Path -LiteralPath $metaFile) {
            try {
                $current = Get-Content -Raw -LiteralPath $metaFile | ConvertFrom-Json
                Write-Host "    Installed version: $($current.version) (fetched $($current.fetchedAt))"
            }
            catch {
                Write-Warning "    Unable to read existing metadata: $_"
            }
        }
        else {
            Write-Host "    Not downloaded yet."
        }
        continue
    }

    $skip = $false
    if (-not $Force -and (Test-Path -LiteralPath $metaFile)) {
        try {
            $current = Get-Content -Raw -LiteralPath $metaFile | ConvertFrom-Json
            # The include list is part of what was installed: tightening it must refresh an existing checkout.
            $wantedInclude = if ($tool.PSObject.Properties['include']) { ($tool.include -join '|') } else { '' }
            $haveInclude = if ($current.PSObject.Properties['include']) { ($current.include -join '|') } else { '' }
            if ($current.version -eq $tool.version -and $haveInclude -eq $wantedInclude) {
                Write-Host "    Already have requested version. Use -Force to re-download." -ForegroundColor Yellow
                $skip = $true
            }
        }
        catch {
            Write-Warning "    Failed to parse metadata. Re-downloading."
        }
    }
    if ($skip) { continue }

    $tempDir = New-TemporaryDirectory
    try {
        $localArchivePath = $null
        if ($tool.PSObject.Properties['localArchive']) {
            $candidate = Resolve-RepoPath -Path $tool.localArchive
            if ($candidate -and (Test-Path -LiteralPath $candidate)) {
                $localArchivePath = $candidate
                Write-Host "    Using prebuilt archive: $localArchivePath"
            }
            else {
                Write-Warning "    Local archive '$($tool.localArchive)' not found. Falling back to download."
            }
        }

        $usingLocalArchive = $null -ne $localArchivePath
        if ($usingLocalArchive) {
            $downloadPath = $localArchivePath
            $computedSha = Get-Checksum -Path $downloadPath
            Write-Host "    SHA256: $computedSha"
            if (-not $SkipChecksum -and $tool.PSObject.Properties['sha256'] -and $tool.sha256 -and $computedSha -ne $tool.sha256.ToLowerInvariant()) {
                throw "Checksum mismatch for local archive of $($tool.name). Expected $($tool.sha256)"
            }
            if ($tool.PSObject.Properties['localContentRoot']) {
                $effectiveContentRoot = $tool.localContentRoot
            }
        }
        else {
            $expectedSha = $null
            if (-not $SkipChecksum) {
                $expectedSha = Resolve-ExpectedChecksum($tool)
            }

            # A pinned mirror listed after an unversioned upstream link only helps if a changed upstream file falls
            # through to it, so a checksum mismatch moves on to the next URL instead of failing straight away.
            $downloadPath = Join-Path -Path $tempDir -ChildPath (Split-Path -Path $downloadUrls[0] -Leaf)
            $remainingUrls = $downloadUrls
            while ($true) {
                $selectedSourceUrl = Invoke-FileDownload -Urls $remainingUrls -OutFile $downloadPath
                $computedSha = Get-Checksum -Path $downloadPath
                Write-Host "    SHA256: $computedSha"
                if (-not $expectedSha -or $computedSha -eq $expectedSha) {
                    break
                }

                $remainingUrls = @($remainingUrls | Select-Object -Skip ([array]::IndexOf($remainingUrls, $selectedSourceUrl) + 1))
                if ($remainingUrls.Count -eq 0) {
                    throw "Checksum mismatch for $($tool.name). Expected $expectedSha"
                }

                Write-Warning "    Checksum mismatch from $selectedSourceUrl. Trying the next URL."
                Remove-Item -LiteralPath $downloadPath -Force
            }

            if (-not $SkipChecksum -and -not $expectedSha) {
                # Fail closed: an unverified binary must never be staged silently.
                if (-not $WriteMissingChecksums) {
                    throw "Manifest entry '$($tool.name)' has no SHA256 (computed: $computedSha). Refusing to install an unverified binary. Verify the download out-of-band, then pin the hash or re-run with -WriteMissingChecksums."
                }

                Write-Warning "    Pinning computed SHA256 '$computedSha' for $($tool.name). Review the manifest diff before committing."
                $tool.sha256 = $computedSha
                $manifestChanged = $true
            }
        }

        if (Test-Path -LiteralPath $dest) {
            Remove-Item -Recurse -Force -LiteralPath $dest
        }
        New-Item -ItemType Directory -Path $dest -Force | Out-Null

        $archiveType = $tool.archiveType
        if ($usingLocalArchive -and $tool.PSObject.Properties['localArchiveType']) {
            $archiveType = $tool.localArchiveType
        }
        elseif ($usingLocalArchive -and $archiveType -eq 'inno') {
            $archiveType = 'zip'
        }

        switch ($archiveType) {
            '7zip' {
                $expandDir = New-TemporaryDirectory
                Expand-WithSevenZip -ArchivePath $downloadPath -DestinationPath $expandDir
                if ($effectiveContentRoot) {
                    $sourcePath = Join-Path -Path $expandDir -ChildPath $effectiveContentRoot
                }
                else {
                    $items = @(Get-ChildItem -LiteralPath $expandDir)
                    if ($items.Count -eq 1 -and $items[0].PSIsContainer) {
                        $sourcePath = $items[0].FullName
                    }
                    else {
                        $sourcePath = $expandDir
                    }
                }
                Copy-ArchivePayload -Tool $tool -SourcePath $sourcePath -Destination $dest
                Remove-Item -Recurse -Force -LiteralPath $expandDir
            }
            'zip' {
                $expandDir = New-TemporaryDirectory
                Expand-Archive -LiteralPath $downloadPath -DestinationPath $expandDir -Force
                if ($effectiveContentRoot) {
                    $sourcePath = Join-Path -Path $expandDir -ChildPath $effectiveContentRoot
                }
                else {
                    $items = @(Get-ChildItem -LiteralPath $expandDir)
                    if ($items.Count -eq 1 -and $items[0].PSIsContainer) {
                        $sourcePath = $items[0].FullName
                    }
                    else {
                        $sourcePath = $expandDir
                    }
                }
                Copy-ArchivePayload -Tool $tool -SourcePath $sourcePath -Destination $dest
                Remove-Item -Recurse -Force -LiteralPath $expandDir
            }
            'raw' {
                $targetFile = if ($tool.rawFileName) { Join-Path -Path $dest -ChildPath $tool.rawFileName } else { Join-Path -Path $dest -ChildPath (Split-Path -Path $downloadPath -Leaf) }
                Copy-Item -LiteralPath $downloadPath -Destination $targetFile -Force
            }
            'inno' {
                $expandDir = New-TemporaryDirectory
                $arguments = $tool.installArgs
                if (-not $arguments) {
                    $arguments = "/SP- /VERYSILENT /NORESTART /SUPPRESSMSGBOXES /DIR=`"$expandDir`""
                }
                else {
                    $arguments = $arguments.Replace('{ExtractDir}', $expandDir)
                }
                Write-Host "    Running installer extraction..."
                $process = Invoke-InstallerProcess -FilePath $downloadPath -Arguments $arguments -ForceInvoker
                if ($process.ExitCode -ne 0) {
                    throw "Installer for $($tool.name) exited with code $($process.ExitCode)"
                }
                $sourcePath = $null
                if ($effectiveContentRoot) {
                    $candidate = Join-Path -Path $expandDir -ChildPath $effectiveContentRoot
                    if (Test-Path -LiteralPath $candidate) {
                        $sourcePath = $candidate
                    }
                    else {
                        Write-Warning "    contentRoot '$contentRoot' not found. Falling back to auto-detect."
                    }
                }
                if (-not $sourcePath) {
                    $installerItems = @(Get-ChildItem -LiteralPath $expandDir)
                    if ($installerItems.Count -eq 1 -and $installerItems[0].PSIsContainer) {
                        $sourcePath = $installerItems[0].FullName
                    }
                    else {
                        $sourcePath = $expandDir
                    }
                }

                $payloadHasContent = Test-HasChildItems -Path $sourcePath
                if (-not $payloadHasContent -and $tool.PSObject.Properties['programFilesFallback']) {
                    $fallbacks = @()
                    if ($env:ProgramFiles) {
                        $fallbacks += Join-Path -Path $env:ProgramFiles -ChildPath $tool.programFilesFallback
                    }
                    if (${env:ProgramFiles(x86)}) {
                        $fallbacks += Join-Path -Path ${env:ProgramFiles(x86)} -ChildPath $tool.programFilesFallback
                    }
                    foreach ($candidate in $fallbacks) {
                        if (Test-HasChildItems -Path $candidate) {
                            Write-Warning "    Extraction directory was empty. Using fallback '$candidate'."
                            $sourcePath = $candidate
                            $payloadHasContent = $true
                            break
                        }
                    }
                }

                if (-not $payloadHasContent) {
                    throw "Installer for $($tool.name) did not produce any files."
                }

                Copy-Item -Path (Join-Path $sourcePath '*') -Destination $dest -Recurse -Force
                Remove-Item -Recurse -Force -LiteralPath $expandDir
            }
            'nsis' {
                $expandDir = New-TemporaryDirectory
                $arguments = $tool.installArgs
                if (-not $arguments) {
                    $arguments = "/S /D=`"{ExtractDir}`""
                }
                $arguments = $arguments.Replace('{ExtractDir}', $expandDir)
                Write-Host "    Running NSIS installer extraction..."
                $process = Invoke-InstallerProcess -FilePath $downloadPath -Arguments $arguments -ForceInvoker
                if ($process.ExitCode -ne 0) {
                    throw "NSIS installer for $($tool.name) exited with code $($process.ExitCode)"
                }

                $sourcePath = $null
                if ($effectiveContentRoot) {
                    $candidate = Join-Path -Path $expandDir -ChildPath $effectiveContentRoot
                    if (Test-Path -LiteralPath $candidate) {
                        $sourcePath = $candidate
                    }
                    else {
                        Write-Warning "    contentRoot '$contentRoot' not found. Falling back to auto-detect."
                    }
                }
                if (-not $sourcePath) {
                    $items = @(Get-ChildItem -LiteralPath $expandDir)
                    if ($items.Count -eq 1 -and $items[0].PSIsContainer) {
                        $sourcePath = $items[0].FullName
                    }
                    else {
                        $sourcePath = $expandDir
                    }
                }

                if (-not (Test-HasChildItems -Path $sourcePath)) {
                    throw "NSIS installer for $($tool.name) did not produce any files."
                }

                Copy-Item -Path (Join-Path $sourcePath '*') -Destination $dest -Recurse -Force
                Remove-Item -Recurse -Force -LiteralPath $expandDir
            }
            'msi' {
                $maxAttempts = 5
                $successfulExpandDir = $null
                $lastExitCode = $null

                for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
                    $attemptExpandDir = New-TemporaryDirectory
                    try {
                        $arguments = "/a `"$downloadPath`" /qn TARGETDIR=`"$attemptExpandDir`""
                        Write-Host "    Extracting MSI contents..."
                        $process = Start-Process -FilePath "msiexec.exe" -ArgumentList $arguments -Wait -PassThru
                        $lastExitCode = $process.ExitCode
                        if ($lastExitCode -eq 0) {
                            $successfulExpandDir = $attemptExpandDir
                            break
                        }

                        if ($lastExitCode -eq 1618 -and $attempt -lt $maxAttempts) {
                            $waitSeconds = 15 * $attempt
                            Write-Warning "    Windows Installer is busy (MSI exit code 1618). Retrying in $waitSeconds seconds... (attempt $attempt/$maxAttempts)"
                            Start-Sleep -Seconds $waitSeconds
                            continue
                        }

                        break
                    }
                    finally {
                        if (-not $successfulExpandDir -and (Test-Path -LiteralPath $attemptExpandDir)) {
                            Remove-Item -Recurse -Force -LiteralPath $attemptExpandDir -ErrorAction SilentlyContinue
                        }
                    }
                }

                if (-not $successfulExpandDir) {
                    throw "MSI extraction for $($tool.name) failed with code $lastExitCode"
                }

                $expandDir = $successfulExpandDir
                if ($effectiveContentRoot) {
                    $sourcePath = Join-Path -Path $expandDir -ChildPath $effectiveContentRoot
                }
                else {
                    $msiItems = @(Get-ChildItem -LiteralPath $expandDir)
                    if ($msiItems.Count -eq 1 -and $msiItems[0].PSIsContainer) {
                        $sourcePath = $msiItems[0].FullName
                    }
                    else {
                        $sourcePath = $expandDir
                    }
                }
                Copy-Item -Path (Join-Path $sourcePath '*') -Destination $dest -Recurse -Force
                Remove-Item -Recurse -Force -LiteralPath $expandDir
            }
            default {
                throw "Unsupported archiveType '$archiveType' for $($tool.name)."
            }
        }

        $source = if ($usingLocalArchive) { $tool.localArchive } else { $selectedSourceUrl }
        $metadata = [ordered]@{
            name      = $tool.name
            version   = $tool.version
            source    = $source
            sha256    = $computedSha
            fetchedAt = (Get-Date).ToString('o')
        }
        if ($tool.PSObject.Properties['include']) { $metadata['include'] = @($tool.include) }
        $metadata | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 -LiteralPath $metaFile
        Write-Host "    Installed $($tool.name) into $dest" -ForegroundColor Green
    }
    finally {
        if (Test-Path -LiteralPath $tempDir) {
            Remove-Item -Recurse -Force -LiteralPath $tempDir
        }
    }
}

if ($manifestChanged) {
    Write-Host "Updating manifest with computed checksums..." -ForegroundColor Yellow
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 -LiteralPath $ManifestPath
}

Write-Host "Done." -ForegroundColor Cyan
