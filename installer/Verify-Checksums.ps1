[CmdletBinding()]
param([string]$Directory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'))
$ErrorActionPreference = 'Stop'
$lines = @(Get-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS.txt'))
if ($lines.Count -eq 0) { throw 'Empty checksum manifest.' }
$seen = @{}
foreach ($line in $lines) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  (HostsManager-[0-9]+\.[0-9]+\.[0-9]+-(?:x64|x86|arm64)-(?:Portable\.exe|Setup\.msi))$') {
        throw "Invalid checksum record: $line"
    }
    $expected = $Matches[1]
    $name = $Matches[2]
    if ($seen.ContainsKey($name)) { throw "Duplicate artifact: $name" }
    $seen[$name] = $true
    $actual = (Get-FileHash -LiteralPath (Join-Path $Directory $name) -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "Checksum mismatch: $name" }
    Write-Host "Verified $name"
}
