[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Workspace = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$packageRoot = Join-Path $Workspace "artifacts\YMacType-$Version"
$archive = Join-Path $Workspace "artifacts\YMacType-$Version-win11-x86-x64.zip"

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null

$files = [ordered]@{
    'Rel+Detours\MacType.Core.dll'       = 'MacType.Core.dll'
    'x64\Rel+Detours\MacType64.Core.dll' = 'MacType64.Core.dll'
    'Release\macloader.exe'              = 'MacLoader.exe'
    'x64\Release\macloader64.exe'        = 'MacLoader64.exe'
    'architecture\REFACTORING.md'         = 'REFACTORING.md'
}

foreach ($entry in $files.GetEnumerator()) {
    $source = Join-Path $Workspace $entry.Key
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing release input: $source"
    }
    Copy-Item -Force -LiteralPath $source -Destination (
        Join-Path $packageRoot $entry.Value)
}

$checksums = Get-ChildItem -LiteralPath $packageRoot -File |
    Sort-Object Name |
    ForEach-Object {
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName
        "$($hash.Hash.ToLowerInvariant())  $($_.Name)"
    }
Set-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS.txt') `
    -Value $checksums -Encoding ascii

if (Test-Path -LiteralPath $archive) {
    Remove-Item -Force -LiteralPath $archive
}
Compress-Archive -Path (Join-Path $packageRoot '*') `
    -DestinationPath $archive -CompressionLevel Optimal

$archiveHash = Get-FileHash -Algorithm SHA256 -LiteralPath $archive
[pscustomobject]@{
    Version = $Version
    PackageDirectory = $packageRoot
    Archive = $archive
    SHA256 = $archiveHash.Hash.ToLowerInvariant()
}
