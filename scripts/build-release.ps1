[CmdletBinding()]
param(
    [string]$Workspace = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} (
    'Microsoft Visual Studio\Installer\vswhere.exe')
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer vswhere.exe was not found.'
}
$msbuild = & $vswhere -latest -products * `
    -requires Microsoft.Component.MSBuild `
    -find 'MSBuild\**\Bin\MSBuild.exe' |
    Select-Object -First 1
if (-not $msbuild) {
    throw 'MSBuild was not found.'
}

$solution = Join-Path $Workspace 'gdipp.sln'
foreach ($platform in @('Win32', 'x64')) {
    $log = Join-Path $Workspace (
        "build-release-$($platform.ToLowerInvariant()).log")
    & $msbuild $solution /t:Rebuild /m:1 /v:minimal `
        /p:Configuration='Rel+Detours' /p:Platform=$platform *>&1 |
        Tee-Object -FilePath $log
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed for $platform. See $log"
    }
    $compilerIssues = Select-String -LiteralPath $log `
        -Pattern '(warning|error) (C|LNK)\d+'
    if ($compilerIssues) {
        throw "Release build was not warning-clean for $platform. See $log"
    }
}

Write-Host 'Release x86/x64 build completed without compiler warnings.'
