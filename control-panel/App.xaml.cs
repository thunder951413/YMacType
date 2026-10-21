using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace YMacType.Settings
{
    public partial class App : Application
    {
        private Mutex? _instanceMutex;
        private EventWaitHandle? _showEvent;
        private RegisteredWaitHandle? _showRegistration;

        private static readonly Dictionary<string, (string Light, string Dark)> Palette =
            new Dictionary<string, (string Light, string Dark)>
            {
                ["PaperBrush"] = ("#F3F4F6", "#1B1C1F"),
                ["SurfaceBrush"] = ("#FFFFFF", "#242529"),
                ["NavBrush"] = ("#ECEEF1", "#202124"),
                ["InkBrush"] = ("#16181D", "#E9EAEC"),
                ["MutedBrush"] = ("#5F6672", "#9CA1AA"),
                ["LineBrush"] = ("#DDE1E6", "#36383D"),
                ["HoverBrush"] = ("#E4E7EB", "#2E3035"),
                ["AccentBrush"] = ("#2B4C7E", "#8DB0E3"),
                ["AccentHoverBrush"] = ("#23406B", "#A4C0EA"),
                ["AccentSoftBrush"] = ("#E3EAF5", "#2A3445"),
                ["OnAccentBrush"] = ("#FFFFFF", "#111722"),
                ["GoodBrush"] = ("#1E7A4C", "#64C795"),
                ["GoodSoftBrush"] = ("#E3F2EA", "#1E3328"),
                ["WarnBrush"] = ("#8A5300", "#E8B460"),
                ["WarnSoftBrush"] = ("#FBF0DC", "#3A301E"),
                ["BadBrush"] = ("#B42318", "#F2877C"),
                ["BadSoftBrush"] = ("#FBE9E7", "#3D2522"),
            };

        public static bool IsDarkTheme { get; private set; }

        private static bool SystemUsesDarkTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyTheme()
        {
            IsDarkTheme = SystemUsesDarkTheme();
            foreach (var entry in Palette)
            {
                var color = (Color)ColorConverter.ConvertFromString(
                    IsDarkTheme ? entry.Value.Dark : entry.Value.Light);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                Resources[entry.Key] = brush;
            }
        }

        private void OnUserPreferenceChanged(
            object sender,
            UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General)
                return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var wasDark = IsDarkTheme;
                ApplyTheme();
                if (wasDark != IsDarkTheme && Current.MainWindow is YMacType.Settings.MainWindow window)
                    window.OnThemeChanged();
            }));
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ApplyTheme();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            var install = System.Array.Exists(
                e.Args,
                argument => argument.Equals(
                    "--install",
                    System.StringComparison.OrdinalIgnoreCase));
            if (!install &&
                e.Args.Length == 0 &&
                !Installer.IsRunningFromInstallDirectory())
            {
                // Started by double-clicking the extracted release folder.
                var answer = MessageBox.Show(
                    "要把 YMacType 安装到 C:\\Program Files\\MacType 吗？\n\n" +
                    "会复制核心文件、加载器和配置，旧文件先备份；" +
                    "没有 MacType 服务时会创建一个并启动。",
                    "安装 YMacType",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    Shutdown(0);
                    return;
                }
                install = true;
            }

            if (install)
            {
                try
                {
                    var result = Installer.Install();
                    MessageBox.Show(
                        result,
                        "YMacType 安装完成",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    try
                    {
                        Installer.OpenInstalledPanel();
                    }
                    catch (System.Exception openError)
                    {
                        PanelLog.Write("could not open installed panel: " + openError);
                    }
                    Shutdown(0);
                    return;
                }
                catch (System.Exception exception)
                {
                    PanelLog.Write("install failed: " + exception);
                    MessageBox.Show(
                        exception.Message,
                        "YMacType 安装失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown(1);
                    return;
                }
            }

            var trayOnly = Array.Exists(
                e.Args,
                argument => argument.Equals(
                    "--tray",
                    StringComparison.OrdinalIgnoreCase));
            var instanceName =
                @"Local\YMacType.Settings." +
                Process.GetCurrentProcess().SessionId;
            var eventName = instanceName + ".Show";
            if (TryActivateExistingInstance(eventName))
            {
                Shutdown(0);
                return;
            }
            _instanceMutex = new Mutex(
                initiallyOwned: true,
                name: instanceName,
                createdNew: out var firstInstance);
            if (!firstInstance)
            {
                try
                {
                    using var showExisting = EventWaitHandle.OpenExisting(
                        eventName);
                    showExisting.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // The first process may still be creating its event.
                }
                Shutdown(0);
                return;
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _showEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                eventName);
            var window = new MainWindow();
            MainWindow = window;
            _showRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showEvent,
                (_, timedOut) =>
                {
                    if (!timedOut)
                        Dispatcher.BeginInvoke(
                            DispatcherPriority.Normal,
                            new Action(window.ShowEffectiveApplications));
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
            if (trayOnly)
            {
                window.ShowInTaskbar = false;
                window.Opacity = 0;
                window.Show();
                window.Hide();
                window.Opacity = 1;
                window.ShowInTaskbar = true;
            }
            else
            {
                window.Show();
            }
        }

        private static bool TryActivateExistingInstance(string eventName)
        {
            using var current = Process.GetCurrentProcess();
            var executable = current.MainModule?.FileName;
            if (string.IsNullOrEmpty(executable))
                return false;
            foreach (var process in Process.GetProcessesByName(
                         current.ProcessName))
            {
                try
                {
                    if (process.Id == current.Id ||
                        !string.Equals(
                            process.MainModule?.FileName,
                            executable,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    var window = process.MainWindowHandle;
                    try
                    {
                        using var showExisting =
                            EventWaitHandle.OpenExisting(eventName);
                        showExisting.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        // Fall back to restoring the native window below.
                    }
                    if (window != IntPtr.Zero)
                    {
                        ShowWindowAsync(window, 9);
                        SetForegroundWindow(window);
                    }
                    return true;
                }
                catch
                {
                    // A process at another integrity level may hide its path.
                }
                finally
                {
                    process.Dispose();
                }
            }
            return false;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _showRegistration?.Unregister(null);
            _showEvent?.Dispose();
            if (_instanceMutex != null)
            {
                _instanceMutex.ReleaseMutex();
                _instanceMutex.Dispose();
            }
            base.OnExit(e);
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(
            IntPtr window,
            int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);
    }
}
