<#
.SYNOPSIS
    Publishes Hosts manager for every supported CPU architecture and wraps each build
    in its own MSI.

.DESCRIPTION
    .NET cannot produce one binary that runs on every architecture, so each target gets
    a self-contained single-file executable and a matching installer. A machine installs
    only the one that matches its CPU.

    Every architecture's MSI shares one UpgradeCode (see Package.wxs), so installing a
    build with a higher -Version over an existing install replaces it in place instead
    of adding a second entry in Programs and Features. By default the version comes from
    Directory.Build.props, so releasing an update is normally just: bump that file, then
    run this script.

.EXAMPLE
    ./installer/build.ps1

.EXAMPLE
    ./installer/build.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param(
    [string[]]$Architectures = @('x64', 'x86', 'arm64'),
    [string]$Configuration = 'Release',
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\HostsManager\HostsManager.csproj'
$outputRoot = Join-Path $repoRoot 'dist'

if (-not $Version) {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    $match = Select-String -Path $propsPath -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if (-not $match) { throw "Could not find <Version> in $propsPath. Pass -Version explicitly." }
    $Version = $match.Matches[0].Groups[1].Value
}

# Windows Installer compares only the first three fields of ProductVersion when deciding
# whether to treat an install as an upgrade; a fourth field is silently ignored. Keeping
# releases to exactly three numeric parts avoids a version bump that MSI doesn't register
# as one.
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' must be three numeric parts (e.g. 1.2.0) for Windows Installer to compare it correctly."
}

Write-Host "Version: $Version" -ForegroundColor Cyan

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

foreach ($arch in $Architectures) {
    $rid = "win-$arch"
    $publishDir = Join-Path $repoRoot "publish\$rid"

    Write-Host "`n=== $rid ===" -ForegroundColor Cyan

    Write-Host 'Publishing...'
    dotnet publish $project `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -p:Version=$Version `
        --output $publishDir `
        --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    $msi = Join-Path $outputRoot "HostsManager-$arch.msi"

    Write-Host 'Building installer...'
    # -pdbtype none keeps dist to shippable files only. Version is passed explicitly
    # rather than bound from the exe's own file version, so the MSI's ProductVersion is
    # guaranteed to be exactly what was requested, not whatever the SDK derived.
    wix build (Join-Path $PSScriptRoot 'Package.wxs') `
        -arch $arch `
        -define "SourceDir=$publishDir" `
        -define "Version=$Version" `
        -ext WixToolset.UI.wixext `
        -pdbtype none `
        -out $msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed for $arch" }

    # Ship the bare executable alongside the installer for anyone who would rather not install.
    Copy-Item (Join-Path $publishDir 'HostsManager.exe') `
        (Join-Path $outputRoot "HostsManager-$arch.exe") -Force
}

Write-Host "`n=== dist ===" -ForegroundColor Cyan
Get-ChildItem $outputRoot | Sort-Object Name |
    Format-Table @{ N = 'File'; E = { $_.Name } }, @{ N = 'Size'; E = { '{0:N1} MB' -f ($_.Length / 1MB) } } -AutoSize
