using System;
using System.IO;
using System.Text;

namespace YMacType.Settings
{
// Small append-only log for the settings panel itself
// (C:\ProgramData\MacType\Logs\panel.log). A panel that dies while starting
// otherwise leaves no trace at all.
internal static class PanelLog
{
    private static readonly object Gate = new object();

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
