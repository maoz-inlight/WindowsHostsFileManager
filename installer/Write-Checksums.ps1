[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string[]]$Architectures = @('x64', 'x86', 'arm64'),
    [string]$Directory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist')
)
$ErrorActionPreference = 'Stop'
$lines = foreach ($arch in $Architectures) {
    foreach ($kind in @('Portable.exe', 'Setup.msi')) {
        $name = "HostsManager-$Version-$arch-$kind"
        $file = Get-Item -LiteralPath (Join-Path $Directory $name)
        if ($file.Length -eq 0) { throw "Empty artifact: $name" }
        "$( (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() )  $name"
    }
}
$lines | Sort-Object | Set-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS.txt') -Encoding ascii
