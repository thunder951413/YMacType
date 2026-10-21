using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace YMacType.Settings
{
// Small append-only log for the settings panel itself
// (C:\ProgramData\MacType\Logs\panel.log). A panel that dies while starting
// otherwise leaves no trace at all.
internal static class PanelLog
{
    private static readonly object Gate = new object();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    // Ends this process immediately, without running DLL detach code. The
    // panel can have the core injected into it, and a normal exit that stalls
    // there would leave an invisible process holding the single-instance
    // mutex.
    public static void TerminateSelf(uint exitCode)
    {
        Write("terminating process, exit code " + exitCode);
        TerminateProcess(GetCurrentProcess(), exitCode);
    }

    // Safety net for an exit that does not finish by itself.
    public static void TerminateAfter(int milliseconds)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(milliseconds);
            TerminateSelf(0);
        })
        {
            IsBackground = true
        };
        watchdog.Start();
    }

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MacType", "Logs", "panel.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var file = Path;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024)
                    File.Delete(file);
                File.AppendAllText(
                    file,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " +
                    message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never be the reason the panel fails.
        }
    }
}
}
