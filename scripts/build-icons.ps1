#requires -Version 7.0
<#
.SYNOPSIS
    Builds the multi-resolution Squish.ico from the square master PNG.

.DESCRIPTION
    Resizes the master artwork (ideally 1024x1024 with a transparent background) to every size Windows
    asks for (taskbar, title bar, Explorer, Start, installer) and packs them as PNG frames into one ICO.
    Re-run it whenever src/App/Assets/Brand/squish-icon.png changes.

.EXAMPLE
    pwsh scripts/build-icons.ps1
    pwsh scripts/build-icons.ps1 -Source C:\art\new-icon.png
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\src\App\Assets\Brand\squish-icon.png'),
    [string]$Destination = (Join-Path $PSScriptRoot '..\installer\assets\Squish.ico'),
    [int[]]$Sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'build-icons.ps1 uses System.Drawing and must run on Windows.'
}

Add-Type -AssemblyName System.Drawing

$Source = (Resolve-Path $Source).Path
$master = [System.Drawing.Image]::FromFile($Source)
try {
    if ($master.Width -ne $master.Height) {
        throw "Master icon must be square; got $($master.Width)x$($master.Height)."
    }

    $frames = foreach ($size in ($Sizes | Sort-Object -Unique)) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.DrawImage($master, 0, 0, $size, $size)
            }
            finally {
                $graphics.Dispose()
            }

            $stream = [System.IO.MemoryStream]::new()
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Size = $size; Data = $stream.ToArray() }
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    $master.Dispose()
}

# ICO layout: ICONDIR header, one ICONDIRENTRY per frame, then the PNG payloads.
$output = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Data.Length)
    $writer.Write([uint32]$offset)
    $offset += $frame.Data.Length
}
foreach ($frame in $frames) {
    $writer.Write($frame.Data)
}
$writer.Flush()

$destinationDir = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path (Resolve-Path $destinationDir).Path (Split-Path -Leaf $Destination)), $output.ToArray())
Write-Host "Wrote $Destination ($($frames.Count) sizes: $(($frames | ForEach-Object Size) -join ', '))"
