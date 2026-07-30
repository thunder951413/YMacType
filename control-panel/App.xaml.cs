using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace YMacType.Settings
{
    public partial class App : Application
    {
        private Mutex? _instanceMutex;
        private EventWaitHandle? _showEvent;
        private RegisteredWaitHandle? _showRegistration;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (System.Array.Exists(
                    e.Args,
                    argument => argument.Equals(
                        "--install",
                        System.StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var result = Installer.Install();
                    MessageBox.Show(
                        result,
                        "YMacType 安装完成",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    Shutdown(0);
                    return;
                }
                catch (System.Exception exception)
                {
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
