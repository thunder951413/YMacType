using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace YMacType.Settings
{
// "Application launcher" page: creates desktop shortcuts that start a program
// through MacLoader (so YMacType is loaded at the program's entry point) and,
// for Chromium-based browsers, with the renderer code-integrity check turned
// off so the unsigned core can be loaded into the page renderers.
public partial class MainWindow
{
    private const string ChromiumRendererArguments =
        "--disable-features=RendererCodeIntegrity";

    private const string ChromiumNote =
        "Chromium 内核浏览器。网页渲染器默认只允许微软签名的模块，需要加上参数才能处理网页正文；" +
        "该参数关闭的是这一层保护，请自行权衡。";

    private const string ElectronNote =
        "Electron 应用。一般没有签名限制，通过 MacLoader 在启动时加载即可。";

    private List<LauncherApp>? _launcherApps;
    private LauncherApp? _launcherSelection;

    private void LoadLauncherApps()
    {
        if (_launcherApps != null)
            return;

        var programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        _launcherApps = new List<LauncherApp>
        {
            LauncherApp.Create(
                "Google Chrome", "chrome.exe", ChromiumRendererArguments,
                ChromiumNote,
                Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(programFilesX86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(local, @"Google\Chrome\Application\chrome.exe")),
            LauncherApp.Create(
                "Microsoft Edge", "msedge.exe", ChromiumRendererArguments,
                ChromiumNote + " Edge 是否接受这个参数需要实测。",
                Path.Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(programFiles, @"Microsoft\Edge\Application\msedge.exe")),
            LauncherApp.Create(
                "Brave", "brave.exe", ChromiumRendererArguments,
                ChromiumNote,
                Path.Combine(programFiles, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe")),
            LauncherApp.Create(
                "Vivaldi", "vivaldi.exe", ChromiumRendererArguments,
                ChromiumNote,
                Path.Combine(local, @"Vivaldi\Application\vivaldi.exe"),
                Path.Combine(programFiles, @"Vivaldi\Application\vivaldi.exe")),
            LauncherApp.Create(
                "Opera", "opera.exe", ChromiumRendererArguments,
                ChromiumNote,
                Path.Combine(local, @"Programs\Opera\opera.exe"),
                Path.Combine(programFiles, @"Opera\opera.exe")),
            LauncherApp.Create(
                "Visual Studio Code", "Code.exe", "",
                ElectronNote,
                Path.Combine(local, @"Programs\Microsoft VS Code\Code.exe"),
                Path.Combine(programFiles, @"Microsoft VS Code\Code.exe")),
            LauncherApp.Create(
                "Obsidian", "Obsidian.exe", "",
                ElectronNote,
                Path.Combine(local, @"Programs\Obsidian\Obsidian.exe"),
                Path.Combine(programFiles, @"Obsidian\Obsidian.exe")),
        };

        // Registered applications (Start > Run) cover non-default install
        // locations for the browsers above.
        foreach (var app in _launcherApps.Where(item => !item.Found))
            app.Path = RegisteredApplicationPath(app.ExecutableName);

        LauncherGrid.ItemsSource = _launcherApps;
    }

    private static string? RegisteredApplicationPath(string executableName)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" +
                    executableName);
                var value = key?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var path = value!.Trim('"');
                    if (File.Exists(path))
                        return path;
                }
            }
            catch
            {
                // Unreadable registry keys just mean "not registered".
            }
        }
        return null;
    }

    private void LauncherGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        SelectLauncher(LauncherGrid.SelectedItem as LauncherApp);
    }

    private void SelectLauncher(LauncherApp? app)
    {
        _launcherSelection = app;
        LauncherResultText.Text = "";
        if (app == null)
        {
            CreateShortcutButton.IsEnabled = false;
            LauncherNoteText.Text = "从上面选一个程序，或者选择其它程序。";
            return;
        }

        LauncherArgsBox.Text = app.Arguments;
        LauncherNoteText.Text = app.Found
            ? app.Note + "\n位置：" + app.Path
            : app.Note + "\n没有在常见位置找到它，可以用“选择其它程序…”指定。";
        CreateShortcutButton.IsEnabled = app.Found;
    }

    private void BrowseLauncher_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要通过 YMacType 启动的程序",
            Filter = "程序 (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var path = dialog.FileName;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var app = LauncherApp.Create(
            name, System.IO.Path.GetFileName(path), "",
            "自定义程序。如果是 Chromium 内核的浏览器，请在参数里加上 " +
            ChromiumRendererArguments + "。",
            path);
        _launcherApps!.RemoveAll(item => item.IsCustom);
        app.IsCustom = true;
        _launcherApps.Add(app);
        LauncherGrid.ItemsSource = null;
        LauncherGrid.ItemsSource = _launcherApps;
        LauncherGrid.SelectedItem = app;
    }

    private void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        var app = _launcherSelection;
        if (app == null || !app.Found || app.Path == null)
            return;

        try
        {
            var arguments = LauncherArgsBox.Text.Trim();
            var viaLoader = LauncherViaLoader.IsChecked == true;
            var shortcutPath = CreateLauncherShortcut(
                app, arguments, viaLoader);
            LauncherResultText.Text =
                "已在桌面创建：" + System.IO.Path.GetFileName(shortcutPath) +
                (IsProcessRunning(app.ExecutableName)
                    ? "。注意：" + app.ExecutableName +
                      " 正在运行，请先完全退出再用快捷方式启动。"
                    : "。");
        }
        catch (Exception exception)
        {
            LauncherResultText.Text = "创建失败：" + exception.Message;
        }
    }

    private static string CreateLauncherShortcut(
        LauncherApp app,
        string arguments,
        bool viaLoader)
    {
        var target = app.Path!;
        string shortcutTarget;
        string shortcutArguments;
        if (viaLoader)
        {
            var loader = System.IO.Path.Combine(
                InstallDirectory,
                IsPortableExecutable64(target)
                    ? "MacLoader64.exe"
                    : "MacLoader.exe");
            if (!File.Exists(loader))
                throw new FileNotFoundException(
                    "找不到 " + System.IO.Path.GetFileName(loader) +
                    "。请先运行 YMacType.Settings.exe --install。",
                    loader);
            shortcutTarget = loader;
            shortcutArguments =
                "\"" + target + "\"" +
                (arguments.Length != 0 ? " " + arguments : "");
        }
        else
        {
            shortcutTarget = target;
            shortcutArguments = arguments;
        }

        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safeName = new string(
            app.Name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)
                .ToArray());
        var desktop = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = System.IO.Path.Combine(
            desktop, safeName + " (YMacType).lnk");
        ShortcutWriter.Create(
            shortcutPath,
            shortcutTarget,
            shortcutArguments,
            System.IO.Path.GetDirectoryName(target) ?? "",
            target,
            app.Name + "（通过 YMacType 启动）");
        return shortcutPath;
    }

    private static bool IsProcessRunning(string executableName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(executableName);
        var processes = System.Diagnostics.Process.GetProcessesByName(name);
        var running = processes.Length != 0;
        foreach (var process in processes)
            process.Dispose();
        return running;
    }

    // Reads the PE header: 0x8664 is x64, anything else is treated as 32-bit.
    private static bool IsPortableExecutable64(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);
            stream.Position = 0x3C;
            var header = reader.ReadInt32();
            stream.Position = header + 4;
            return reader.ReadUInt16() == 0x8664;
        }
        catch
        {
            return Environment.Is64BitOperatingSystem;
        }
    }

    private sealed class LauncherApp
    {
        public string Name { get; set; } = "";
        public string ExecutableName { get; set; } = "";
        public string? Path { get; set; }
        public string Arguments { get; set; } = "";
        public string Note { get; set; } = "";
        public bool IsCustom { get; set; }

        public bool Found => Path != null && File.Exists(Path);

        public string StatusLabel => Found ? "已找到：" + Path : "未找到";

        public static LauncherApp Create(
            string name,
            string executableName,
            string arguments,
            string note,
            params string[] candidates)
        {
            return new LauncherApp
            {
                Name = name,
                ExecutableName = executableName,
                Arguments = arguments,
                Note = note,
                Path = candidates.FirstOrDefault(File.Exists)
            };
        }
    }
}
}
