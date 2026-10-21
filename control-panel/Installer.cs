using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;

namespace YMacType.Settings
{
internal static class Installer
{
    private const string InstallDirectory = @"C:\Program Files\MacType";
    private const string TrayTaskName = "YMacType Settings Tray";

    private static readonly string[] ObsoleteFiles =
    {
        "MacTuner.exe",
        "MacWiz.exe",
        "updater.exe",
        "updater.ini",
        "GdiBench.exe",
        "GdiBench.ini",
        "unins000.exe",
        "unins000.dat"
    };

    private static readonly string[] ObsoleteDirectories =
    {
        "updates",
        "UX"
    };

    public static string Install()
    {
        EnsureAdministrator();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "MacType", "CleanupBackups", stamp);
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(InstallDirectory);

        var removed = 0;
        foreach (var name in ObsoleteFiles)
        {
            var path = SafeInstallPath(name);
            if (!File.Exists(path))
                continue;
            File.Copy(path, Path.Combine(backupRoot, name), true);
            File.Delete(path);
            ++removed;
        }

        foreach (var path in Directory.EnumerateFiles(
                     InstallDirectory,
                     "*.ymactype-old-*",
                     SearchOption.TopDirectoryOnly).ToList())
        {
            EnsureInsideInstallDirectory(path);
            File.Copy(path, Path.Combine(backupRoot, Path.GetFileName(path)), true);
            File.Delete(path);
            ++removed;
        }

        foreach (var name in ObsoleteDirectories)
        {
            var path = SafeInstallPath(name);
            if (!Directory.Exists(path))
                continue;
            CopyDirectory(path, Path.Combine(backupRoot, name));
            Directory.Delete(path, recursive: true);
        }

        var source = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException(
                "无法确定设置面板的安装来源。");
        var destination = SafeInstallPath("YMacType.Settings.exe");
        if (!Path.GetFullPath(source).Equals(
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase))
        {
            StopOwnService();
            StopInstalledTray(destination);
            File.Copy(source, destination, true);
        }

        var payload = InstallPayload(
            Path.GetDirectoryName(source) ?? "", backupRoot, stamp);
        InstallBundledProfile(source, backupRoot);
        ReplaceStartMenuShortcuts(destination, backupRoot);
        RemoveLegacyUninstallEntry(backupRoot);
        var serviceCreated = !ServiceInstalled();
        ConfigureService();
        ConfigureTrayStartup(destination);
        StartTray(destination);

        var mode = serviceCreated
            ? "服务模式：Automatic / LocalSystem（已创建 YMacType 服务）"
            : "服务模式：Automatic / LocalSystem";
        return
            $"新设置面板已安装。\n\n" +
            $"已更新核心文件：{payload} 个\n" +
            $"已删除旧组件：{removed} 个\n" +
            $"已清理失效的旧卸载入口\n" +
            $"{mode}\n" +
            $"托盘面板：登录时自动启动\n" +
            $"备份位置：{backupRoot}\n\n" +
            "原版 MacTray 服务如果存在会继续使用；没有时由 YMacType 服务接管。";
    }

    private static void ConfigureTrayStartup(string panelPath)
    {
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("无法连接任务计划服务。");
        dynamic scheduler = Activator.CreateInstance(schedulerType);
        if (scheduler == null)
            throw new InvalidOperationException("无法启动任务计划服务。");
        scheduler.Connect();
        dynamic root = scheduler.GetFolder("\\");
        dynamic task = scheduler.NewTask(0);
        task.RegistrationInfo.Description =
            "Start the YMacType settings tray after interactive logon.";
        task.Settings.Enabled = true;
        task.Settings.StartWhenAvailable = true;
        task.Settings.ExecutionTimeLimit = "PT0S";
        task.Settings.MultipleInstances = 2;

        var user = WindowsIdentity.GetCurrent().Name;
        task.Principal.UserId = user;
        task.Principal.LogonType = 3;
        task.Principal.RunLevel = 1;

        dynamic trigger = task.Triggers.Create(9);
        trigger.UserId = user;
        trigger.Enabled = true;
        dynamic action = task.Actions.Create(0);
        action.Path = panelPath;
        action.Arguments = "--tray";
        action.WorkingDirectory = InstallDirectory;
        root.RegisterTaskDefinition(
            TrayTaskName,
            task,
            6,
            null,
            null,
            3,
            null);
    }

    public static bool IsTrayStartupEnabled()
    {
        try
        {
            var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType == null)
                return false;
            dynamic scheduler = Activator.CreateInstance(schedulerType);
            if (scheduler == null)
                return false;
            scheduler.Connect();
            dynamic root = scheduler.GetFolder("\\");
            dynamic task = root.GetTask(TrayTaskName);
            if (task == null)
                return false;
            return Convert.ToBoolean(task.Enabled);
        }
        catch
        {
            return false;
        }
    }

    public static void SetTrayStartupEnabled(bool enabled)
    {
        EnsureAdministrator();
        if (enabled)
        {
            ConfigureTrayStartup(SafeInstallPath("YMacType.Settings.exe"));
            return;
        }

        try
        {
            var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException(
                    "无法连接任务计划服务。");
            dynamic scheduler = Activator.CreateInstance(schedulerType);
            if (scheduler == null)
                throw new InvalidOperationException(
                    "无法启动任务计划服务。");
            scheduler.Connect();
            dynamic root = scheduler.GetFolder("\\");
            dynamic task = root.GetTask(TrayTaskName);
            task.Enabled = false;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // A missing task is already equivalent to startup being disabled.
        }
    }

    private static void StartTray(string panelPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = panelPath,
            Arguments = "--tray",
            WorkingDirectory = InstallDirectory,
            UseShellExecute = true
        });
    }

    // The YMacType service runs from the panel executable that is about to be
    // overwritten. Stop it cleanly (a stop does not trigger the restart
    // actions); ConfigureService starts it again afterwards.
    private static void StopOwnService()
    {
        var config = RunScCapture("qc MacType");
        if (config.IndexOf(
                "YMacType.Settings.exe",
                StringComparison.OrdinalIgnoreCase) < 0)
            return;
        RunScCapture("stop MacType");
        for (var attempt = 0; attempt != 40 && ServiceRunning(); ++attempt)
            System.Threading.Thread.Sleep(250);
    }

    private static string RunScCapture(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "sc.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null)
                return "";
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return output;
        }
        catch
        {
            return "";
        }
    }

    private static void StopInstalledTray(string panelPath)
    {
        var currentProcessId = Process.GetCurrentProcess().Id;
        foreach (var process in Process.GetProcessesByName(
                     "YMacType.Settings"))
        {
            try
            {
                if (process.Id != currentProcessId &&
                    string.Equals(
                        process.MainModule?.FileName,
                        panelPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // A stale higher-integrity tray will be replaced at next logon.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static readonly string[] PayloadFiles =
    {
        "MacType.Core.dll",
        "MacType64.Core.dll",
        "MacLoader.exe",
        "MacLoader64.exe"
    };

    // Copies the core DLLs and loaders that sit next to the installer into
    // the install directory. A loaded DLL cannot be overwritten but can be
    // renamed, so the old file is moved aside first (and removed at the next
    // install); processes that already use it keep working until they exit.
    private static int InstallPayload(
        string sourceDirectory,
        string backupRoot,
        string stamp)
    {
        if (string.Equals(
                Path.GetFullPath(sourceDirectory).TrimEnd('\\'),
                Path.GetFullPath(InstallDirectory).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
            return 0;

        var installed = 0;
        foreach (var name in PayloadFiles)
        {
            var from = Path.Combine(sourceDirectory, name);
            if (!File.Exists(from))
                continue;
            var to = SafeInstallPath(name);
            string? retired = null;
            if (File.Exists(to))
            {
                File.Copy(to, Path.Combine(backupRoot, name), true);
                retired = SafeInstallPath(
                    "." + name + ".ymactype-old-" + stamp);
                File.Move(to, retired);
            }
            try
            {
                File.Copy(from, to, false);
            }
            catch
            {
                if (retired != null && !File.Exists(to))
                    File.Move(retired, to);
                throw;
            }
            if (retired != null)
            {
                try
                {
                    File.Delete(retired);
                }
                catch
                {
                    // Still loaded; the next install removes it.
                }
            }
            ++installed;
        }
        return installed;
    }

    private static void InstallBundledProfile(
        string executablePath,
        string backupRoot)
    {
        var sourceDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("无法确定设置面板目录。");
        var bundledProfile = Path.Combine(
            sourceDirectory, "YMacType-macOS.ini");
        if (!File.Exists(bundledProfile))
            return;

        var profileDirectory = SafeInstallPath("ini");
        Directory.CreateDirectory(profileDirectory);
        var destination = SafeInstallPath(
            Path.Combine("ini", "YMacType-macOS.ini"));
        if (File.Exists(destination))
            File.Copy(
                destination,
                Path.Combine(backupRoot, "YMacType-macOS.ini"),
                true);
        File.Copy(bundledProfile, destination, true);

        // A fresh machine has no MacType.ini yet; Load returns an empty
        // document and Save creates the file (UTF-16 with BOM, as the core
        // expects for the main configuration).
        var mainConfig = SafeInstallPath("MacType.ini");
        var document = IniDocument.Load(mainConfig, preferUtf16: true);
        document.Set(
            "General",
            "AlternativeFile",
            @"ini\YMacType-macOS.ini");
        foreach (var executable in new[]
                 {
                     "TextInputHost.exe",
                     "ctfmon.exe",
                     "wetype_renderer.exe"
                 })
        {
            document.RemoveEntry("Exclude", executable);
            document.RemoveEntry("UnloadDll", executable);
            document.AddEntry("ExcludeSub", executable);
        }
        document.Save(mainConfig);
    }

    private static void RemoveLegacyUninstallEntry(string backupRoot)
    {
        const string keyPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" +
            "{724A2991-CC38-4DD4-B9B4-E30BECA5ED90}_is1";
        using var baseKey = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(keyPath);
        if (key == null)
            return;

        var displayName = key.GetValue("DisplayName") as string;
        var uninstallString = key.GetValue("UninstallString") as string;
        if (!string.Equals(
                displayName, "MacType", StringComparison.OrdinalIgnoreCase) ||
            uninstallString == null ||
            uninstallString.IndexOf(
                "unins000.exe", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        File.WriteAllLines(
            Path.Combine(backupRoot, "LegacyUninstallEntry.txt"),
            key.GetValueNames()
                .Select(name => $"{name}={key.GetValue(name)}"));
        key.Close();
        baseKey.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
    }

    private static void ReplaceStartMenuShortcuts(
        string panelPath,
        string backupRoot)
    {
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", "MacType");
        Directory.CreateDirectory(startMenu);
        var shortcutBackup = Path.Combine(backupRoot, "StartMenu");
        Directory.CreateDirectory(shortcutBackup);
        foreach (var shortcut in Directory.EnumerateFiles(
                     startMenu, "*.lnk", SearchOption.TopDirectoryOnly).ToList())
        {
            File.Copy(
                shortcut,
                Path.Combine(shortcutBackup, Path.GetFileName(shortcut)),
                true);
            File.Delete(shortcut);
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法创建开始菜单快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType);
        if (shell == null)
            throw new InvalidOperationException("无法启动快捷方式服务。");
        dynamic shortcutObject = shell.CreateShortcut(
            Path.Combine(startMenu, "YMacType 设置.lnk"));
        shortcutObject.TargetPath = panelPath;
        shortcutObject.WorkingDirectory = InstallDirectory;
        shortcutObject.Description = "YMacType 字体与渲染设置";
        shortcutObject.IconLocation = panelPath + ",0";
        shortcutObject.Save();
    }

    // Makes sure a "MacType" service exists. An existing one (the original
    // MacTray service, or an earlier YMacType one) is left as it is; when
    // there is none, YMacType registers its own service host.
    internal static bool EnsureServiceRegistered()
    {
        if (ServiceInstalled())
            return false;

        var panel = SafeInstallPath("YMacType.Settings.exe");
        foreach (var name in new[]
                 {
                     "YMacType.Settings.exe",
                     "MacLoader.exe",
                     "MacLoader64.exe",
                     "MacType.Core.dll",
                     "MacType64.Core.dll"
                 })
        {
            if (!File.Exists(SafeInstallPath(name)))
                throw new FileNotFoundException(
                    "安装目录里缺少 " + name + "，无法创建服务。请先运行 " +
                    "YMacType.Settings.exe --install。",
                    SafeInstallPath(name));
        }

        // Two hook hosts for the same DLL would only waste work.
        StopMacTray();
        RunSc(
            "create MacType binPath= \"\\\"" + panel + "\\\" --service\" " +
            "start= auto obj= LocalSystem DisplayName= \"MacType\"");
        return true;
    }

    private static void StopMacTray()
    {
        foreach (var process in Process.GetProcessesByName("MacTray"))
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch
            {
                // It may already be gone, or belong to another user.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void ConfigureService()
    {
        EnsureServiceRegistered();
        RunSc("config MacType start= auto obj= LocalSystem");
        RunSc(
            "failure MacType reset= 86400 " +
            "actions= restart/5000/restart/15000/restart/60000");
        RunSc("failureflag MacType 1");
        RunSc(
            "description MacType " +
            "\"YMacType Windows 11 font rendering injection service\"");
        if (!ServiceRunning())
            RunSc("start MacType");
    }

    // sc.exe reports error 1060 when no service with that name exists.
    internal static bool ServiceInstalled()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "sc.exe"),
                Arguments = "query MacType",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null)
                return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
                return false;
            return process.ExitCode != 1060;
        }
        catch
        {
            return false;
        }
    }

    private static void RunSc(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "sc.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("无法启动服务控制器。");
        process.WaitForExit(15000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"服务配置失败：sc.exe {arguments}\n" +
                process.StandardOutput.ReadToEnd() +
                process.StandardError.ReadToEnd());
    }

    private static bool ServiceRunning()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "sc.exe"),
            Arguments = "query MacType",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        });
        if (process == null)
            return false;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return output.IndexOf(
            "RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string SafeInstallPath(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(InstallDirectory, relative));
        EnsureInsideInstallDirectory(path);
        return path;
    }

    private static void EnsureInsideInstallDirectory(string path)
    {
        var root = Path.GetFullPath(InstallDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(
                root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"拒绝处理安装目录之外的路径：{path}");
    }

    private static void CopyDirectory(string source, string destination)
    {
        EnsureInsideInstallDirectory(source);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(
                file,
                Path.Combine(destination, Path.GetFileName(file)),
                true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void EnsureAdministrator()
    {
        using var identity =
            System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal =
            new System.Security.Principal.WindowsPrincipal(identity);
        if (!principal.IsInRole(
                System.Security.Principal.WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException(
                "安装需要管理员权限，请在 UAC 提示中选择“是”。");
    }
}
}
