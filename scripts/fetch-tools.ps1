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

function New-TemporaryDirectory {
    $path = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ([System.Guid]::NewGuid().ToString())
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return $path
}

function Get-Checksum([string]$Path) {
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-HasChildItems([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }
    $child = Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue | Select-Object -First 1
    return $null -ne $child
}

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "Manifest not found: $ManifestPath"
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
    $contentRoot = $null
    if ($tool.PSObject.Properties['contentRoot']) {
        $contentRoot = $tool.contentRoot
    }

    Write-Host "==> $($tool.name) v$($tool.version)" -ForegroundColor Cyan
    Write-Host "    Source: $($tool.url)"
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
            if ($current.version -eq $tool.version) {
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
        $downloadPath = Join-Path -Path $tempDir -ChildPath (Split-Path -Path $tool.url -Leaf)
        Write-Host "    Downloading..."
        Invoke-WebRequest -Uri $tool.url -OutFile $downloadPath -UseBasicParsing

        $computedSha = Get-Checksum -Path $downloadPath
        Write-Host "    SHA256: $computedSha"

        if ($tool.sha256 -and -not $SkipChecksum) {
            if ($computedSha -ne $tool.sha256.ToLowerInvariant()) {
                throw "Checksum mismatch for $($tool.name). Expected $($tool.sha256)"
            }
        }
        elseif (-not $SkipChecksum) {
            Write-Warning "    Manifest missing SHA256. Add '$computedSha' for $($tool.name) or run with -WriteMissingChecksums."
            if ($WriteMissingChecksums) {
                $tool.sha256 = $computedSha
                $manifestChanged = $true
            }
        }

        if (Test-Path -LiteralPath $dest) {
            Remove-Item -Recurse -Force -LiteralPath $dest
        }
        New-Item -ItemType Directory -Path $dest -Force | Out-Null

        switch ($tool.archiveType) {
            'zip' {
                $expandDir = New-TemporaryDirectory
                Expand-Archive -LiteralPath $downloadPath -DestinationPath $expandDir -Force
                if ($contentRoot) {
                    $sourcePath = Join-Path -Path $expandDir -ChildPath $contentRoot
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
                Copy-Item -Path (Join-Path $sourcePath '*') -Destination $dest -Recurse -Force
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
                $process = Start-Process -FilePath $downloadPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
                if ($process.ExitCode -ne 0) {
                    throw "Installer for $($tool.name) exited with code $($process.ExitCode)"
                }
                $sourcePath = $null
                if ($contentRoot) {
                    $candidate = Join-Path -Path $expandDir -ChildPath $contentRoot
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
            'msi' {
                $expandDir = New-TemporaryDirectory
                $arguments = "/a `"$downloadPath`" /qn TARGETDIR=`"$expandDir`""
                Write-Host "    Extracting MSI contents..."
                $process = Start-Process -FilePath "msiexec.exe" -ArgumentList $arguments -Wait -PassThru
                if ($process.ExitCode -ne 0) {
                    throw "MSI extraction for $($tool.name) failed with code $($process.ExitCode)"
                }
                if ($contentRoot) {
                    $sourcePath = Join-Path -Path $expandDir -ChildPath $contentRoot
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
                throw "Unsupported archiveType '$($tool.archiveType)' for $($tool.name)."
            }
        }

        $metadata = [ordered]@{
            name      = $tool.name
            version   = $tool.version
            source    = $tool.url
            sha256    = $computedSha
            fetchedAt = (Get-Date).ToString('o')
        }
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
