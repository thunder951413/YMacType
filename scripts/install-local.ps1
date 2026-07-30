[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Workspace = (Split-Path -Parent $PSScriptRoot),
    [string]$InstallDirectory = 'C:\Program Files\MacType',
    [switch]$ConfigurationOnly
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator privileges are required to update the MacType service.'
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path $env:ProgramData "MacType\Backups\$timestamp"
$deployLogRoot = Join-Path $env:ProgramData 'MacType\DeploymentLogs'
$deployLog = Join-Path $deployLogRoot "install-$timestamp.log"
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
New-Item -ItemType Directory -Force -Path $deployLogRoot | Out-Null
$rebootRequired = $false

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class YMacTypeNativeMethods {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveFileEx(
        string existingName, string newName, int flags);
}
'@

$moveFileReplaceExisting = 0x1
$moveFileDelayUntilReboot = 0x4

function Write-DeployLog([string]$Message) {
    $line = "$(Get-Date -Format o) $Message"
    Add-Content -LiteralPath $deployLog -Value $line -Encoding utf8
    Write-Host $line
}

$payload = [ordered]@{
    (Join-Path $Workspace 'Rel+Detours\MacType.Core.dll') =
        (Join-Path $InstallDirectory 'MacType.Core.dll')
    (Join-Path $Workspace 'x64\Rel+Detours\MacType64.Core.dll') =
        (Join-Path $InstallDirectory 'MacType64.Core.dll')
    (Join-Path $Workspace 'Release\macloader.exe') =
        (Join-Path $InstallDirectory 'MacLoader.exe')
    (Join-Path $Workspace 'x64\Release\macloader64.exe') =
        (Join-Path $InstallDirectory 'MacLoader64.exe')
    (Join-Path $Workspace (
        'control-panel\bin\Release\net48\release-publish\' +
        'YMacType.Settings.exe')) =
        (Join-Path $InstallDirectory 'YMacType.Settings.exe')
    (Join-Path $Workspace 'profiles\YMacType-macOS.ini') =
        (Join-Path $InstallDirectory 'ini\YMacType-macOS.ini')
}

if (-not $ConfigurationOnly) {
    foreach ($source in $payload.Keys) {
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Missing deployment input: $source"
        }
    }
}

$service = Get-Service -Name MacType -ErrorAction SilentlyContinue
$wasRunning = $service -and $service.Status -ne 'Stopped'
try {
    if ($wasRunning) {
        Write-DeployLog 'Stopping MacType service.'
        Stop-Service -Name MacType -Force
        (Get-Service -Name MacType).WaitForStatus(
            [ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(30))
    }
    Get-Process MacTray -ErrorAction SilentlyContinue |
        Stop-Process -Force

    foreach ($entry in $payload.GetEnumerator()) {
        if ($ConfigurationOnly) {
            break
        }
        $source = $entry.Key
        $destination = $entry.Value
        $leaf = Split-Path -Leaf $destination
        if (Test-Path -LiteralPath $destination) {
            Copy-Item -Force -LiteralPath $destination -Destination (
                Join-Path $backupRoot $leaf)
        }
        $staged = Join-Path $InstallDirectory (
            ".$leaf.ymactype-new-$timestamp")
        Copy-Item -Force -LiteralPath $source -Destination $staged
        $sourceHash = (Get-FileHash -Algorithm SHA256 `
            -LiteralPath $source).Hash
        $stagedHash = (Get-FileHash -Algorithm SHA256 `
            -LiteralPath $staged).Hash
        if ($sourceHash -ne $stagedHash) {
            throw "Hash mismatch after staging $destination"
        }

        $installedImmediately = $true
        if (Test-Path -LiteralPath $destination) {
            $retired = Join-Path $InstallDirectory (
                ".$leaf.ymactype-old-$timestamp")
            try {
                Move-Item -LiteralPath $destination -Destination $retired
                Move-Item -LiteralPath $staged -Destination $destination
                [void][YMacTypeNativeMethods]::MoveFileEx(
                    $retired, $null, $moveFileDelayUntilReboot)
            }
            catch {
                Write-DeployLog (
                    "$leaf is locked; scheduling replacement for next boot.")
                if (-not [YMacTypeNativeMethods]::MoveFileEx(
                    $destination, $null, $moveFileDelayUntilReboot)) {
                    throw "Unable to schedule removal of $destination"
                }
                if (-not [YMacTypeNativeMethods]::MoveFileEx(
                    $staged, $destination,
                    $moveFileDelayUntilReboot -bor
                    $moveFileReplaceExisting)) {
                    throw "Unable to schedule replacement of $destination"
                }
                $installedImmediately = $false
                $rebootRequired = $true
            }
        }
        else {
            Move-Item -LiteralPath $staged -Destination $destination
        }

        if ($installedImmediately) {
            $installedHash = (Get-FileHash -Algorithm SHA256 `
                -LiteralPath $destination).Hash
            if ($sourceHash -ne $installedHash) {
                throw "Hash mismatch after installing $destination"
            }
        }
        Write-DeployLog "Installed $leaf immediate=$installedImmediately."
    }

    $ini = Join-Path $InstallDirectory 'MacType.ini'
    if (Test-Path -LiteralPath $ini) {
        Copy-Item -Force -LiteralPath $ini -Destination (
            Join-Path $backupRoot 'MacType.ini')
        $content = Get-Content -LiteralPath $ini -Raw
        if ($content -match '(?im)^\s*HookChildProcesses\s*=') {
            $content = $content -replace (
                '(?im)^\s*HookChildProcesses\s*=.*$'),
                'HookChildProcesses=1'
        }
        elseif ($content -match '(?im)^\s*\[General\]\s*$') {
            $content = $content -replace (
                '(?im)^\s*\[General\]\s*$'),
                "[General]`r`nHookChildProcesses=1"
        }
        else {
            $content = "[General]`r`nHookChildProcesses=1`r`n$content"
        }
        if ($content -match '(?im)^\s*AlternativeFile\s*=') {
            $content = $content -replace (
                '(?im)^\s*AlternativeFile\s*=.*$'),
                'AlternativeFile=ini\YMacType-macOS.ini'
        }
        elseif ($content -match '(?im)^\s*\[General\]\s*$') {
            $content = $content -replace (
                '(?im)^\s*\[General\]\s*$'),
                "[General]`r`nAlternativeFile=ini\YMacType-macOS.ini"
        }
        $processExclusions = @(
            'MacLoader.exe',
            'MacLoader64.exe',
            'YMacType.Settings.exe',
            'MSBuild.exe',
            'cl.exe',
            'link.exe',
            'rc.exe',
            'mspdbsrv.exe',
            'git.exe',
            'gh.exe',
            'taskkill.exe',
            'powershell.exe',
            'pwsh.exe',
            'cmd.exe',
            'conhost.exe',
            'OpenConsole.exe',
            'WindowsTerminal.exe'
        )
        if ($content -notmatch '(?im)^\s*\[UnloadDll\]\s*$') {
            $content += "`r`n[UnloadDll]`r`n"
        }
        foreach ($processName in $processExclusions) {
            if ($content -notmatch (
                "(?im)^\s*" + [regex]::Escape($processName) +
                "\s*(?:;.*)?$")) {
                $content = $content -replace (
                    '(?im)^\s*\[UnloadDll\]\s*$'),
                    "[UnloadDll]`r`n$processName"
            }
        }
        Set-Content -LiteralPath $ini -Value $content -Encoding unicode
    }

    $obsoleteFiles = @(
        'MacTuner.exe',
        'MacWiz.exe',
        'updater.exe',
        'updater.ini',
        'GdiBench.exe',
        'GdiBench.ini',
        'unins000.exe',
        'unins000.dat'
    )
    $resolvedInstallDirectory = (
        Resolve-Path -LiteralPath $InstallDirectory).Path.TrimEnd('\')
    foreach ($name in $obsoleteFiles) {
        $path = Join-Path $resolvedInstallDirectory $name
        if (Test-Path -LiteralPath $path) {
            Copy-Item -Force -LiteralPath $path -Destination (
                Join-Path $backupRoot $name)
            Remove-Item -Force -LiteralPath $path
            Write-DeployLog "Removed obsolete component $name."
        }
    }
    Get-ChildItem -LiteralPath $resolvedInstallDirectory -Force -File |
        Where-Object Name -Like '*.ymactype-old-*' |
        ForEach-Object {
            Copy-Item -Force -LiteralPath $_.FullName -Destination (
                Join-Path $backupRoot $_.Name)
            try {
                Remove-Item -Force -LiteralPath $_.FullName
                Write-DeployLog "Removed retired file $($_.Name)."
            }
            catch {
                if (-not [YMacTypeNativeMethods]::MoveFileEx(
                    $_.FullName, $null, $moveFileDelayUntilReboot)) {
                    throw
                }
                $rebootRequired = $true
                Write-DeployLog (
                    "Scheduled locked retired file $($_.Name) for deletion.")
            }
        }
    foreach ($name in @('updates', 'UX')) {
        $path = Join-Path $resolvedInstallDirectory $name
        if (Test-Path -LiteralPath $path) {
            $resolved = (Resolve-Path -LiteralPath $path).Path
            if (-not $resolved.StartsWith(
                "$resolvedInstallDirectory\",
                [StringComparison]::OrdinalIgnoreCase)) {
                throw "Cleanup path escaped install directory: $resolved"
            }
            Copy-Item -Force -Recurse -LiteralPath $resolved -Destination (
                Join-Path $backupRoot $name)
            Remove-Item -Force -Recurse -LiteralPath $resolved
            Write-DeployLog "Removed obsolete directory $name."
        }
    }

    $startMenu = Join-Path (
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::CommonStartMenu)) 'Programs\MacType'
    New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
    $shortcutBackup = Join-Path $backupRoot 'StartMenu'
    New-Item -ItemType Directory -Force -Path $shortcutBackup | Out-Null
    Get-ChildItem -LiteralPath $startMenu -Filter '*.lnk' -File |
        ForEach-Object {
            Copy-Item -Force -LiteralPath $_.FullName `
                -Destination $shortcutBackup
            Remove-Item -Force -LiteralPath $_.FullName
        }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut(
        (Join-Path $startMenu 'YMacType 设置.lnk'))
    $shortcut.TargetPath = Join-Path $InstallDirectory 'YMacType.Settings.exe'
    $shortcut.WorkingDirectory = $InstallDirectory
    $shortcut.Description = 'YMacType 字体与渲染设置'
    $shortcut.IconLocation = "$($shortcut.TargetPath),0"
    $shortcut.Save()

    $legacyUninstallKey = (
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' +
        '{724A2991-CC38-4DD4-B9B4-E30BECA5ED90}_is1')
    if (Test-Path -LiteralPath $legacyUninstallKey) {
        $legacyEntry = Get-ItemProperty -LiteralPath $legacyUninstallKey
        if ($legacyEntry.DisplayName -eq 'MacType' -and
            $legacyEntry.UninstallString -match 'unins000\.exe') {
            $legacyEntry | Format-List * | Out-File -Encoding utf8 (
                Join-Path $backupRoot 'LegacyUninstallEntry.txt')
            Remove-Item -Force -LiteralPath $legacyUninstallKey
            Write-DeployLog 'Removed obsolete MacType uninstall entry.'
        }
    }

    Set-Service -Name MacType -StartupType Automatic
    & sc.exe config MacType start= auto obj= LocalSystem | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to configure MacType as an Automatic LocalSystem service.'
    }
    & sc.exe failure MacType reset= 86400 `
        actions= restart/5000/restart/15000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to configure MacType service recovery.'
    }
    & sc.exe failureflag MacType 1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enable MacType non-crash failure recovery.'
    }
    Start-Service -Name MacType
    (Get-Service -Name MacType).WaitForStatus(
        [ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))
    $trayExecutable = Join-Path $InstallDirectory 'YMacType.Settings.exe'
    $trayTaskName = 'YMacType Settings Tray'
    $trayUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $trayAction = New-ScheduledTaskAction -Execute $trayExecutable `
        -Argument '--tray' -WorkingDirectory $InstallDirectory
    $trayTrigger = New-ScheduledTaskTrigger -AtLogOn -User $trayUser
    $trayPrincipal = New-ScheduledTaskPrincipal -UserId $trayUser `
        -LogonType Interactive -RunLevel Highest
    $traySettings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $trayTaskName -Action $trayAction `
        -Trigger $trayTrigger -Principal $trayPrincipal `
        -Settings $traySettings -Force | Out-Null
    Start-ScheduledTask -TaskName $trayTaskName
    Write-DeployLog "Configured interactive tray task for $trayUser."
    Write-DeployLog "Deployment complete. Backup=$backupRoot"
    if ($rebootRequired) {
        Write-DeployLog (
            'One or more locked files will be replaced before services start ' +
            'at the next boot.')
    }
}
catch {
    Write-DeployLog "Deployment failed: $($_.Exception.Message)"
    foreach ($entry in $payload.GetEnumerator()) {
        $backup = Join-Path $backupRoot (
            Split-Path -Leaf $entry.Value)
        if (Test-Path -LiteralPath $backup) {
            Copy-Item -Force -LiteralPath $backup `
                -Destination $entry.Value
        }
    }
    $iniBackup = Join-Path $backupRoot 'MacType.ini'
    if (Test-Path -LiteralPath $iniBackup) {
        Copy-Item -Force -LiteralPath $iniBackup `
            -Destination (Join-Path $InstallDirectory 'MacType.ini')
    }
    if ($wasRunning) {
        Start-Service -Name MacType -ErrorAction SilentlyContinue
    }
    throw
}

[pscustomobject]@{
    InstallDirectory = $InstallDirectory
    BackupDirectory = $backupRoot
    DeploymentLog = $deployLog
    ServiceStatus = (Get-Service -Name MacType).Status
    RebootRequired = $rebootRequired
}
