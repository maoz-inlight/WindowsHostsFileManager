<#
.SYNOPSIS
    Regenerates src/HostsManager/Assets/app.ico from app.svg.

.DESCRIPTION
    app.svg is the source of truth for the mark; app.ico is a build artifact that happens
    to be committed, because the build must not need a browser installed.

    The renderer is headless Edge or Chrome. That looks odd for a build step, but every
    Windows 11 machine has Edge, and the alternatives (Inkscape, ImageMagick, resvg) are
    all installs a contributor would otherwise never need. The SVG uses feGaussianBlur,
    feColorMatrix and radial gradients, so a naive rasterizer would not do.

    Each size is rendered from its own copy of the SVG with width/height set to that size,
    rather than by downscaling one large render. Chromium then rasterizes the geometry at
    the target resolution — strokes and the tile's corner radius land on the pixel grid
    instead of being resampled from a bitmap.

    Frames are stored PNG-compressed at every size, which is what the previous icon did and
    what System.Drawing's Icon reads correctly here (see TrayIcon.LoadIcon).

.EXAMPLE
    ./tools/make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$Svg,
    [string]$Output,

    # The sizes Windows asks for: 16 in the title bar and small taskbar, 32 in the tray,
    # 48 in Explorer's medium view, 256 for the jumbo view and the About dialog.
    [int[]]$Sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $repoRoot 'src\HostsManager\Assets'
if (-not $Svg) { $Svg = Join-Path $assets 'app.svg' }
if (-not $Output) { $Output = Join-Path $assets 'app.ico' }

if (-not (Test-Path $Svg)) { throw "Source not found: $Svg" }

function Find-Browser {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:LocalAppData\Google\Chrome\Application\chrome.exe"
    )

    foreach ($path in $candidates) { if ($path -and (Test-Path $path)) { return $path } }
    throw 'Needs Microsoft Edge or Google Chrome to rasterize the SVG; neither was found.'
}

$browser = Find-Browser
Write-Host "Renderer: $browser"

$work = Join-Path ([IO.Path]::GetTempPath()) "hostsmanager-icon-$([Guid]::NewGuid().ToString('n'))"
New-Item -ItemType Directory -Path $work | Out-Null

try {
    $document = [xml](Get-Content $Svg -Raw)

    # A plain list, not a hashtable keyed by size: an ordered dictionary indexed with an
    # int treats it as a position rather than a key, which silently reads the wrong frame.
    $frames = New-Object System.Collections.Generic.List[psobject]

    foreach ($size in $Sizes) {
        # Only the root width/height change; the viewBox is what actually scales the art.
        $document.DocumentElement.SetAttribute('width', "$size")
        $document.DocumentElement.SetAttribute('height', "$size")

        $sized = Join-Path $work "app-$size.svg"
        $png = Join-Path $work "app-$size.png"
        $document.Save($sized)

        # A throwaway profile keeps this out of the contributor's own browser data, and
        # a fully transparent default background stops Chromium painting white behind the
        # rounded corners. The profile is per size because the previous render's process
        # can still hold the directory, and a second launch against a live profile hands
        # the URL to that instance and exits without ever taking a screenshot.
        & $browser --headless --disable-gpu --hide-scrollbars `
            --user-data-dir="$work\profile-$size" `
            --default-background-color=00000000 `
            --force-device-scale-factor=1 `
            --window-size="$size,$size" `
            --screenshot="$png" `
            ("file:///" + ($sized -replace '\\', '/')) *> $null

        # Edge relaunches itself through a stub, so the command returns before the
        # screenshot is on disk — Chrome writes it synchronously. Wait for the file rather
        # than picking a renderer that happens to behave. Size is checked too: the file
        # appears empty and is filled a moment later.
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ((Test-Path $png) -and (Get-Item $png).Length -gt 0) { break }
            Start-Sleep -Milliseconds 100
        }

        if (-not ((Test-Path $png) -and (Get-Item $png).Length -gt 0)) {
            throw "Rendering $size px produced no file after 30s."
        }

        $bytes = [IO.File]::ReadAllBytes($png)
        $frames.Add([pscustomobject]@{ Size = $size; Bytes = $bytes })
        Write-Host ("  {0,3} px  {1,6:N0} bytes" -f $size, $bytes.Length)
    }

    # ICONDIR, then one 16-byte ICONDIRENTRY per frame, then the PNG payloads. A width or
    # height byte of 0 means 256 — the field is a single byte, so 256 cannot be spelled.
    $stream = [IO.File]::Create($Output)
    try {
        $writer = New-Object IO.BinaryWriter($stream)
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$frames.Count)

        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $writer.Write([byte]($frame.Size -band 0xFF))
            $writer.Write([byte]($frame.Size -band 0xFF))
            $writer.Write([byte]0)      # palette size: none, it is a true-colour image
            $writer.Write([byte]0)      # reserved
            $writer.Write([uint16]1)    # colour planes
            $writer.Write([uint16]32)   # bits per pixel
            $writer.Write([uint32]$frame.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }

        foreach ($frame in $frames) { $writer.Write($frame.Bytes) }
        $writer.Flush()
    }
    finally { $stream.Dispose() }

    Write-Host "`nWrote $Output ($((Get-Item $Output).Length) bytes, $($frames.Count) frames)"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
