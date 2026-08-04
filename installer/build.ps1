<#
.SYNOPSIS
    Publishes Hosts manager for every supported CPU architecture and wraps each build
    in its own MSI.

.DESCRIPTION
    .NET cannot produce one binary that runs on every architecture, so each target gets
    a self-contained single-file executable and a matching installer. A machine installs
    only the one that matches its CPU.

.EXAMPLE
    ./installer/build.ps1
#>
[CmdletBinding()]
param(
    [string[]]$Architectures = @('x64', 'x86', 'arm64'),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\HostsManager\HostsManager.csproj'
$outputRoot = Join-Path $repoRoot 'dist'

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
        --output $publishDir `
        --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    $msi = Join-Path $outputRoot "HostsManager-$arch.msi"

    Write-Host 'Building installer...'
    # -pdbtype none keeps dist to shippable files only.
    wix build (Join-Path $PSScriptRoot 'Package.wxs') `
        -arch $arch `
        -define "SourceDir=$publishDir" `
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
