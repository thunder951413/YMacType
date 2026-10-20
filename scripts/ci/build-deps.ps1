[CmdletBinding()]
param(
    [string]$Workspace = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$WorkDirectory = (Join-Path $env:RUNNER_TEMP 'ymactype-deps')
)

# Builds the third-party static libraries MacType links against and lays them
# out the way gdipp.vcxproj / macloader.vcxproj expect:
#
#   deps\include\ft2build.h, deps\include\freetype\...   FreeType headers
#   deps\include\IniParser\ParseIni.h ...                 IniParser headers
#   deps\lib\freetype.lib / freetype64.lib                FreeType (static, /MT)
#   deps\lib\iniparser.lib / iniparser64.lib              IniParser (static, /MT)
#   deps\lib\wow64ext.lib                                 wow64ext (x86 only)
#   deps\lib\detours.lib / detours64.lib                  Microsoft Detours
#
# Every dependency is pinned to a commit so release builds are reproducible.

$ErrorActionPreference = 'Stop'

$pins = [ordered]@{
    freetype = @{ Url = 'https://github.com/snowie2000/freetype'; Commit = 'ef771574d04721baf45a1b66bfb4692193603088' }
    IniParser = @{ Url = 'https://github.com/snowie2000/IniParser'; Commit = 'a457397ffa9d20e8df43e2c143c60da78c16c059' }
    wow64ext = @{ Url = 'https://github.com/snowie2000/rewolf-wow64ext'; Commit = '667359c7967249dd9d28d8f8cef65b60e7e2d963' }
    Detours = @{ Url = 'https://github.com/microsoft/Detours'; Commit = '1dbd4ea281d73d478aa8d09a8b83fbd265f715f7' }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'Visual Studio with the C++ toolset was not found.' }
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvarsall.bat'

$include = Join-Path $Workspace 'deps\include'
$lib = Join-Path $Workspace 'deps\lib'
New-Item -ItemType Directory -Force -Path $WorkDirectory, $include, $lib | Out-Null

function Get-Source([string]$Name) {
    $pin = $pins[$Name]
    $target = Join-Path $WorkDirectory $Name
    if (-not (Test-Path (Join-Path $target '.git'))) {
        git init -q $target
        git -C $target remote add origin $pin.Url
    }
    git -C $target fetch -q --depth 1 origin $pin.Commit
    if ($LASTEXITCODE -ne 0) { throw "Fetching $Name failed." }
    git -C $target checkout -q --force FETCH_HEAD
    return $target
}

function Invoke-MSBuild([string[]]$Arguments, [string]$What) {
    & $msbuild @Arguments /m /nologo /v:minimal /p:PlatformToolset=v143 /p:WindowsTargetPlatformVersion=10.0
    if ($LASTEXITCODE -ne 0) { throw "$What failed." }
}

# --- FreeType (snowie2000 fork; already carries FT_Glyph_To_BitmapEx) -------
$freetype = Get-Source 'freetype'
$freetypeProject = Join-Path $freetype 'builds\windows\vc2010\freetype.vcxproj'
foreach ($platform in 'Win32', 'x64') {
    Invoke-MSBuild @($freetypeProject, '/t:Rebuild', '/p:Configuration=Release Static', "/p:Platform=$platform") "FreeType $platform"
}
Copy-Item -Force (Join-Path $freetype 'objs\Win32\Release Static\freetype.lib') (Join-Path $lib 'freetype.lib')
Copy-Item -Force (Join-Path $freetype 'objs\x64\Release Static\freetype.lib') (Join-Path $lib 'freetype64.lib')
Copy-Item -Recurse -Force (Join-Path $freetype 'include\*') $include

# --- IniParser -------------------------------------------------------------
$iniParser = Get-Source 'IniParser'
$iniProject = Join-Path $iniParser 'IniParser\IniParser.vcxproj'
$iniOut = Join-Path $WorkDirectory 'iniparser-out'
Invoke-MSBuild @($iniProject, '/t:Rebuild', '/p:Configuration=Release', '/p:Platform=Win32', "/p:OutDir=$iniOut\x86\") 'IniParser Win32'
Invoke-MSBuild @($iniProject, '/t:Rebuild', '/p:Configuration=Release', '/p:Platform=x64', "/p:OutDir=$iniOut\x64\") 'IniParser x64'
Copy-Item -Force (Get-ChildItem "$iniOut\x86\*.lib" | Select-Object -First 1).FullName (Join-Path $lib 'iniparser.lib')
Copy-Item -Force (Get-ChildItem "$iniOut\x64\*.lib" | Select-Object -First 1).FullName (Join-Path $lib 'iniparser64.lib')
$iniHeaders = Join-Path $include 'IniParser'
New-Item -ItemType Directory -Force -Path $iniHeaders | Out-Null
Copy-Item -Force (Join-Path $iniParser 'IniParser\*.h') $iniHeaders

# --- wow64ext (x86 only; the x64 core does not need it) -------------------
$wow64 = Get-Source 'wow64ext'
$wowOut = Join-Path $WorkDirectory 'wow64ext-out'
Invoke-MSBuild @((Join-Path $wow64 'src\wow64ext.vcxproj'), '/t:Rebuild', '/p:Configuration=Release', '/p:Platform=Win32', "/p:OutDir=$wowOut\") 'wow64ext'
Copy-Item -Force (Get-ChildItem "$wowOut\*.lib" | Select-Object -First 1).FullName (Join-Path $lib 'wow64ext.lib')

# --- Microsoft Detours ------------------------------------------------------
$detours = Get-Source 'Detours'
foreach ($arch in @(@{ Vc = 'x86'; Dir = 'lib.X86'; Name = 'detours.lib' },
                    @{ Vc = 'x64'; Dir = 'lib.X64'; Name = 'detours64.lib' })) {
    $command = "call `"$vcvars`" $($arch.Vc) && cd /d `"$detours\src`" && nmake /nologo"
    cmd.exe /c $command
    if ($LASTEXITCODE -ne 0) { throw "Detours $($arch.Vc) failed." }
    Copy-Item -Force (Join-Path $detours "$($arch.Dir)\detours.lib") (Join-Path $lib $arch.Name)
}

Get-ChildItem $lib | Format-Table Name, Length
