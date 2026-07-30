using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace YMacType.Settings
{
internal static class Installer
{
    private const string InstallDirectory = @"C:\Program Files\MacType";

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
            File.Copy(source, destination, true);

        InstallBundledProfile(source, backupRoot);
        ReplaceStartMenuShortcuts(destination, backupRoot);
        RemoveLegacyUninstallEntry(backupRoot);
        ConfigureService();

        return
            $"新设置面板已安装。\n\n" +
            $"已删除旧组件：{removed} 个\n" +
            $"已清理失效的旧卸载入口\n" +
            $"服务模式：Automatic / LocalSystem\n" +
            $"备份位置：{backupRoot}\n\n" +
            "MacTray.exe 仅作为无界面的注入服务保留。";
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

        var mainConfig = SafeInstallPath("MacType.ini");
        if (!File.Exists(mainConfig))
            throw new FileNotFoundException("找不到 MacType.ini。", mainConfig);
        var document = IniDocument.Load(mainConfig);
        document.Set(
            "General",
            "AlternativeFile",
            @"ini\YMacType-macOS.ini");
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

    private static void ConfigureService()
    {
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
