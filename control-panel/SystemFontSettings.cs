using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace YMacType.Settings
{
    /// <summary>
    /// Reads and writes the one global Win32 UI font setting exposed by
    /// Windows in HKCU\Control Panel\Desktop\WindowMetrics.  Windows stores
    /// each category as a LOGFONTW blob; keeping the operation here means the
    /// WPF page never has to know about registry offsets or backup details.
    /// </summary>
    internal static class SystemFontSettings
    {
        private const string WindowMetricsPath =
            @"Control Panel\Desktop\WindowMetrics";
        private const int LogFontSize = 92;
        private const int FaceNameOffset = 28;
        private const int FaceNameByteLength = 64;
        private const int DefaultWeight = 400;
        private const int DefaultCharset = 1;
        private const int DefaultPitchAndFamily = 0;
        private const int WmSettingChange = 0x001A;
        private const int WmFontChange = 0x001D;
        private const uint HwndBroadcast = 0xffff;
        private const uint SmtoAbortIfHung = 0x0002;

        internal static readonly string[] ValueNames =
        {
            "CaptionFont",
            "SmCaptionFont",
            "MenuFont",
            "StatusFont",
            "MessageFont",
            "IconFont"
        };

        private static readonly string BackupRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "MacType", "SystemFontBackups");

        internal static SystemFontSelection ReadCurrent()
        {
            var snapshot = ReadSnapshot();
            var candidate = ValueNames
                .Select(name => snapshot.Values.TryGetValue(name, out var value)
                    ? value
                    : null)
                .FirstOrDefault(value => value != null);
            if (candidate == null)
                return new SystemFontSelection("Segoe UI", 9);

            var family = ReadFaceName(candidate);
            if (string.IsNullOrWhiteSpace(family))
                family = "Segoe UI";
            var pointSize = ReadPointSize(candidate);
            return new SystemFontSelection(family, pointSize);
        }

        internal static string Apply(string familyName, int pointSize)
        {
            if (string.IsNullOrWhiteSpace(familyName))
                throw new ArgumentException("系统字体名称不能为空。", nameof(familyName));
            if (pointSize < 6 || pointSize > 48)
                throw new ArgumentOutOfRangeException(nameof(pointSize));

            var snapshot = ReadSnapshot();
            var backupDirectory = CreateBackup(snapshot);
            var dpi = GetSystemDpi();
            using (var key = Registry.CurrentUser.CreateSubKey(WindowMetricsPath))
            {
                if (key == null)
                    throw new InvalidOperationException(
                        "无法打开 Windows UI 字体配置项。请确认当前用户注册表可写。 ");

                foreach (var name in ValueNames)
                {
                    byte[] bytes;
                    if (snapshot.Values.TryGetValue(name, out var existing))
                        bytes = NormalizeLogFont(existing);
                    else
                        bytes = CreateDefaultLogFont();
                    WriteLogFont(bytes, familyName, pointSize, dpi);
                    key.SetValue(name, bytes, RegistryValueKind.Binary);
                }
            }
            BroadcastChange();
            return backupDirectory;
        }

        internal static string? RestoreLatest()
        {
            var backupDirectory = Directory.Exists(BackupRoot)
                ? Directory.EnumerateDirectories(BackupRoot)
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()
                : null;
            if (backupDirectory == null)
                return null;

            var snapshot = LoadBackup(backupDirectory);
            using (var key = Registry.CurrentUser.CreateSubKey(WindowMetricsPath))
            {
                if (key == null)
                    throw new InvalidOperationException(
                        "无法打开 Windows UI 字体配置项。请确认当前用户注册表可写。 ");

                foreach (var name in ValueNames)
                {
                    if (snapshot.Values.TryGetValue(name, out var value))
                        key.SetValue(name, value, RegistryValueKind.Binary);
                    else
                        key.DeleteValue(name, false);
                }
            }
            BroadcastChange();
            return backupDirectory;
        }

        private static FontSnapshot ReadSnapshot()
        {
            var snapshot = new FontSnapshot();
            using (var key = Registry.CurrentUser.OpenSubKey(WindowMetricsPath))
            {
                if (key == null)
                    return snapshot;
                foreach (var name in ValueNames)
                {
                    if (key.GetValue(name, null) is byte[] value && value.Length != 0)
                        snapshot.Values[name] = (byte[])value.Clone();
                }
            }
            return snapshot;
        }

        private static string CreateBackup(FontSnapshot snapshot)
        {
            Directory.CreateDirectory(BackupRoot);
            var directory = Path.Combine(
                BackupRoot,
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            var lines = new List<string>
            {
                "# YMacType WindowMetrics backup v1"
            };
            foreach (var name in ValueNames)
            {
                if (snapshot.Values.TryGetValue(name, out var value))
                    lines.Add(name + "=" + Convert.ToBase64String(value));
            }
            File.WriteAllLines(
                Path.Combine(directory, "WindowMetrics.snapshot"),
                lines,
                new UTF8Encoding(false));
            return directory;
        }

        private static FontSnapshot LoadBackup(string directory)
        {
            var file = Path.Combine(directory, "WindowMetrics.snapshot");
            if (!File.Exists(file))
                throw new InvalidDataException("找不到系统字体备份文件。 ");

            var snapshot = new FontSnapshot();
            foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
                    continue;
                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;
                var name = line.Substring(0, separator);
                if (!ValueNames.Contains(name, StringComparer.Ordinal))
                    continue;
                try
                {
                    snapshot.Values[name] = Convert.FromBase64String(
                        line.Substring(separator + 1));
                }
                catch (FormatException)
                {
                    throw new InvalidDataException(
                        "系统字体备份文件内容损坏：" + name);
                }
            }
            return snapshot;
        }

        private static byte[] NormalizeLogFont(byte[] value)
        {
            var normalized = new byte[LogFontSize];
            Buffer.BlockCopy(value, 0, normalized, 0,
                Math.Min(value.Length, normalized.Length));
            return normalized;
        }

        private static byte[] CreateDefaultLogFont()
        {
            var bytes = new byte[LogFontSize];
            WriteInt32(bytes, 16, DefaultWeight);
            bytes[23] = DefaultCharset;
            bytes[27] = DefaultPitchAndFamily;
            return bytes;
        }

        private static void WriteLogFont(
            byte[] bytes,
            string familyName,
            int pointSize,
            int dpi)
        {
            var pixelHeight = Math.Max(
                1,
                (int)Math.Round(pointSize * dpi / 72.0,
                    MidpointRounding.AwayFromZero));
            WriteInt32(bytes, 0, -pixelHeight);
            Array.Clear(bytes, FaceNameOffset, FaceNameByteLength);
            var face = familyName.Trim();
            if (face.Length > 31)
                face = face.Substring(0, 31);
            var faceBytes = Encoding.Unicode.GetBytes(face);
            Buffer.BlockCopy(
                faceBytes, 0, bytes, FaceNameOffset,
                Math.Min(faceBytes.Length, FaceNameByteLength - 2));
        }

        private static string ReadFaceName(byte[] bytes)
        {
            if (bytes.Length < FaceNameOffset + FaceNameByteLength)
                return string.Empty;
            return Encoding.Unicode.GetString(
                    bytes, FaceNameOffset, FaceNameByteLength)
                .TrimEnd('\0');
        }

        private static int ReadPointSize(byte[] bytes)
        {
            if (bytes.Length < 4)
                return 9;
            var height = BitConverter.ToInt32(bytes, 0);
            if (height == 0)
                return 9;
            var points = Math.Abs(height) * 72.0 / GetSystemDpi();
            return Math.Max(6, Math.Min(48,
                (int)Math.Round(points, MidpointRounding.AwayFromZero)));
        }

        private static void WriteInt32(byte[] bytes, int offset, int value)
        {
            var encoded = BitConverter.GetBytes(value);
            Buffer.BlockCopy(encoded, 0, bytes, offset, encoded.Length);
        }

        private static int GetSystemDpi()
        {
            try
            {
                var dpi = GetDpiForSystem();
                return dpi > 0 ? (int)dpi : 96;
            }
            catch (DllNotFoundException)
            {
                return 96;
            }
            catch (EntryPointNotFoundException)
            {
                return 96;
            }
        }

        private static void BroadcastChange()
        {
            SendMessageTimeout(
                new IntPtr(HwndBroadcast),
                WmSettingChange,
                IntPtr.Zero,
                "WindowMetrics",
                SmtoAbortIfHung,
                5000,
                out _);
            SendMessageTimeout(
                new IntPtr(HwndBroadcast),
                WmFontChange,
                IntPtr.Zero,
                null,
                SmtoAbortIfHung,
                5000,
                out _);
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            int message,
            IntPtr wParam,
            [MarshalAs(UnmanagedType.LPWStr)] string? lParam,
            uint flags,
            uint timeout,
            out IntPtr result);

        private sealed class FontSnapshot
        {
            internal Dictionary<string, byte[]> Values { get; } =
                new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }
    }

    internal sealed class SystemFontSelection
    {
        internal SystemFontSelection(string family, int pointSize)
        {
            Family = family;
            PointSize = pointSize;
        }

        internal string Family { get; }
        internal int PointSize { get; }
    }
}
