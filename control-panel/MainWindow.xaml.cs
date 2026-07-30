using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YMacType.Settings
{
public partial class MainWindow : Window
{
    private const string InstallDirectory = @"C:\Program Files\MacType";
    private static readonly string MainConfig =
        Path.Combine(InstallDirectory, "MacType.ini");
    private static readonly string ActiveProfile =
        Path.Combine(InstallDirectory, "ini", "YMacType-macOS.ini");
    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "MacType", "Logs");

    private static readonly string[] ChineseSources =
    {
        "Microsoft YaHei", "Microsoft YaHei UI", "DengXian", "SimHei",
        "SimSun", "NSimSun", "FangSong", "KaiTi"
    };

    private static readonly string[] LatinSources =
    {
        "Segoe UI", "Arial", "Tahoma", "Helvetica"
    };

    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var fonts = Fonts.SystemFontFamilies
            .Select(family => family.Source)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        FontSelector.ItemsSource = fonts;
        LoadProfile();
        _loaded = true;
        UpdatePreview();
        RefreshStatus();
    }

    private void LoadProfile()
    {
        var ini = IniDocument.Load(ActiveProfile);
        var font = ini.Get("FontSubstitutes", "Segoe UI",
            ini.Get("FontSubstitutes", "Microsoft YaHei", "PingFang SC"));
        FontSelector.SelectedItem = FontSelector.Items.Cast<string>()
            .FirstOrDefault(item =>
                item.Equals(font, StringComparison.CurrentCultureIgnoreCase))
            ?? "PingFang SC";
        ReplaceLatinFonts.IsChecked =
            !string.IsNullOrEmpty(ini.Get("FontSubstitutes", "Segoe UI"));
        SelectTag(HintingSelector, ini.Get("General", "HintingMode", "1"));
        SelectTag(AntialiasSelector, ini.Get("General", "AntiAliasMode", "0"));
        GammaSlider.Value = Parse(
            ini.Get("General", "GammaValue", "1.2"), 1.2);
        ContrastSlider.Value = Parse(
            ini.Get("General", "Contrast", "1.0"), 1.0);
        WeightSlider.Value = Parse(
            ini.Get("General", "RenderWeight", "1.15"), 1.15);
        DwGammaSlider.Value = Parse(
            ini.Get("DirectWrite", "GammaValue", "1.15"), 1.15);
        DwContrastSlider.Value = Parse(
            ini.Get("DirectWrite", "Contrast", "0.8"), 0.8);
    }

    private void PreviewChanged(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            UpdatePreview();
    }

    private void UpdatePreview()
    {
        var familyName = FontSelector.SelectedItem as string ?? "PingFang SC";
        try
        {
            var family = new FontFamily(familyName);
            PreviewChinese.FontFamily = family;
            PreviewLatin.FontFamily = family;
        }
        catch
        {
            // Keep the current preview family if a malformed font is registered.
        }

        PreviewChinese.FontSize = PreviewSizeSlider.Value;
        PreviewLatin.FontSize = Math.Max(13, PreviewSizeSlider.Value - 7);
        var visualWeight = Math.Max(
            250,
            Math.Min(
                700,
                400 + (WeightSlider.Value - 1.0) * 350));
        PreviewChinese.FontWeight = FontWeight.FromOpenTypeWeight(
            (int)visualWeight);
        PreviewLatin.FontWeight = PreviewChinese.FontWeight;

        var dark = DarkPreview.IsChecked == true;
        PreviewCard.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(dark ? "#1C1D21" : "#FFFFFF"));
        var foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(dark ? "#F2F3F5" : "#172033"));
        PreviewChinese.Foreground = foreground;
        PreviewLatin.Foreground = foreground;

        GammaValueText.Text = GammaSlider.Value.ToString("0.00");
        ContrastValueText.Text = ContrastSlider.Value.ToString("0.00");
        WeightValueText.Text = WeightSlider.Value.ToString("0.00");
        DwGammaValueText.Text = DwGammaSlider.Value.ToString("0.00");
        DwContrastValueText.Text = DwContrastSlider.Value.ToString("0.00");
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ResultText.Text = "正在备份并应用设置…";
            IsEnabled = false;
            var values = CaptureValues();
            await Task.Run(() => ApplyProfile(values));
            ReloadCurrentProcess();
            ResultText.Text = "设置已应用；新启动的程序会立即使用，旧程序请重新打开。";
            RefreshStatus();
        }
        catch (Exception exception)
        {
            ResultText.Text = "应用失败";
            MessageBox.Show(
                this, exception.Message, "YMacType 设置",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private ProfileValues CaptureValues()
    {
        return new ProfileValues
        {
            Font = FontSelector.SelectedItem as string ?? "PingFang SC",
            ReplaceLatin = ReplaceLatinFonts.IsChecked == true,
            Hinting = SelectedTag(HintingSelector, "1"),
            Antialias = SelectedTag(AntialiasSelector, "0"),
            Gamma = GammaSlider.Value,
            Contrast = ContrastSlider.Value,
            Weight = WeightSlider.Value,
            DirectWriteGamma = DwGammaSlider.Value,
            DirectWriteContrast = DwContrastSlider.Value
        };
    }

    private static void ApplyProfile(ProfileValues values)
    {
        var backupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MacType", "ProfileBackups",
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(backupDirectory);
        if (File.Exists(MainConfig))
            File.Copy(MainConfig, Path.Combine(backupDirectory, "MacType.ini"), true);
        if (File.Exists(ActiveProfile))
            File.Copy(
                ActiveProfile,
                Path.Combine(backupDirectory, "YMacType-macOS.ini"),
                true);

        var ini = IniDocument.Load(ActiveProfile);
        ini.Set("General", "Name", "YMacType macOS");
        ini.Set("General", "HookChildProcesses", "1");
        ini.Set("General", "HintingMode", values.Hinting);
        ini.Set("General", "AntiAliasMode", values.Antialias);
        ini.Set("General", "GammaMode", "0");
        ini.Set("General", "GammaValue", Format(values.Gamma));
        ini.Set("General", "Contrast", Format(values.Contrast));
        ini.Set("General", "RenderWeight", Format(values.Weight));
        ini.Set("General", "FontLoader", "0");
        ini.Set("General", "FontSubstitutes", "2");
        ini.Set("General", "FontLink", "1");
        ini.Set("General", "DirectWrite", "1");
        ini.Set("General", "EnableKerning", "1");
        ini.Set("DirectWrite", "GammaValue", Format(values.DirectWriteGamma));
        ini.Set("DirectWrite", "Contrast", Format(values.DirectWriteContrast));
        ini.Set("DirectWrite", "ClearTypeLevel",
            values.Antialias == "0" ? "0.0" : "1.0");
        ini.Set("DirectWrite", "RenderingMode", "5");

        var target = values.Font;
        foreach (var source in ChineseSources)
            ini.Set("FontSubstitutes", source, target);
        foreach (var source in LatinSources)
        {
            if (values.ReplaceLatin)
                ini.Set("FontSubstitutes", source, target);
            else
                ini.Remove("FontSubstitutes", source);
        }
        ini.Set("Individual", target, "1,0,0,0,0,1");
        ini.Save(ActiveProfile);
        EnsureMainProfilePointer();

        RunSc("config MacType start= auto obj= LocalSystem");
        RunSc("failure MacType reset= 86400 actions= restart/5000/restart/15000/restart/60000");
        RunSc("failureflag MacType 1");
        RunSc("stop MacType", allowFailure: true);
        for (var attempt = 0; attempt != 30; ++attempt)
        {
            if (!ServiceRunning())
                break;
            System.Threading.Thread.Sleep(200);
        }
        RunSc("start MacType");
    }

    private static void EnsureMainProfilePointer()
    {
        var bytes = File.ReadAllBytes(MainConfig);
        var utf16 = bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe;
        var text = utf16
            ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
            : Encoding.UTF8.GetString(bytes);
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var index = lines.FindIndex(line =>
            line.StartsWith("AlternativeFile=", StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            lines[index] = @"AlternativeFile=ini\YMacType-macOS.ini";
        else
        {
            var general = lines.FindIndex(line =>
                line.Trim().Equals("[General]", StringComparison.OrdinalIgnoreCase));
            lines.Insert(general >= 0 ? general + 1 : 0,
                @"AlternativeFile=ini\YMacType-macOS.ini");
        }
        var updated = string.Join("\r\n", lines);
        var body = Encoding.Unicode.GetBytes(updated);
        var preamble = Encoding.Unicode.GetPreamble();
        var output = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, output, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, output, preamble.Length, body.Length);
        File.WriteAllBytes(MainConfig, output);
    }

    private static void RunSc(string arguments, bool allowFailure = false)
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
        if (!allowFailure && process.ExitCode != 0)
            throw new InvalidOperationException(
                $"服务配置失败：sc.exe {arguments}\n" +
                process.StandardOutput.ReadToEnd() +
                process.StandardError.ReadToEnd());
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        FontSelector.SelectedItem = FontSelector.Items.Cast<string>()
            .FirstOrDefault(font =>
                font.Equals("PingFang SC", StringComparison.OrdinalIgnoreCase))
            ?? FontSelector.SelectedItem;
        ReplaceLatinFonts.IsChecked = true;
        SelectTag(HintingSelector, "1");
        SelectTag(AntialiasSelector, "0");
        GammaSlider.Value = 1.2;
        ContrastSlider.Value = 1.0;
        WeightSlider.Value = 1.15;
        DwGammaSlider.Value = 1.15;
        DwContrastSlider.Value = 0.8;
        PreviewSizeSlider.Value = 27;
        UpdatePreview();
        ResultText.Text = "已恢复推荐值，点击“应用并刷新服务”保存。";
    }

    private void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var running = ServiceRunning();
        ServiceBadge.Text = running ? "● 服务正在运行" : "● 服务未运行";
        ServiceBadge.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(running ? "#147A43" : "#B42318"));

        var covered = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Modules.Cast<ProcessModule>().Any(module =>
                        string.Equals(
                            module.ModuleName,
                            "MacType.Core.dll",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            module.ModuleName,
                            "MacType64.Core.dll",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    ++covered;
                    names.Add(process.ProcessName);
                }
            }
            catch
            {
                // Protected processes are expected to reject module enumeration.
            }
            finally
            {
                process.Dispose();
            }
        }

        var recentErrors = 0;
        if (Directory.Exists(LogDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(
                         LogDirectory, "*.log", SearchOption.TopDirectoryOnly)
                     .Where(file =>
                         File.GetLastWriteTime(file) > DateTime.Now.AddHours(-1)))
            {
                try
                {
                    recentErrors += File.ReadLines(file).Count(line =>
                        line.IndexOf(
                            "level=ERROR", StringComparison.Ordinal) >= 0);
                }
                catch
                {
                    // A process may rotate a log while it is being read.
                }
            }
        }

        StatusText.Text =
            $"服务：{(running ? "Automatic / LocalSystem / Running" : "Stopped")}\n" +
            $"活动配置：{ActiveProfile}\n" +
            $"已覆盖进程：{covered}（{names.Count} 类）\n" +
            $"最近 1 小时错误：{recentErrors}\n" +
            $"日志目录：{LogDirectory}";
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LogDirectory);
        OpenPath(LogDirectory);
    }

    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        OpenPath(ActiveProfile);
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"",
            UseShellExecute = true
        });
    }

    private static bool ServiceRunning()
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
                RedirectStandardOutput = true
            });
            if (process == null)
                return false;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.IndexOf(
                "RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ReloadCurrentProcess()
    {
        var module = GetModuleHandle("MacType64.Core.dll");
        if (module == IntPtr.Zero)
            return;
        var address = GetProcAddress(module, "ReloadConfig");
        if (address == IntPtr.Zero)
            return;
        Marshal.GetDelegateForFunctionPointer<ReloadProc>(address)();
    }

    private static void SelectTag(ComboBox combo, string value)
    {
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag?.ToString(), value))
            ?? combo.Items[0];
    }

    private static string SelectedTag(ComboBox combo, string fallback)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
    }

    private static double Parse(string value, double fallback)
    {
        return double.TryParse(
            value, NumberStyles.Float, CultureInfo.InvariantCulture,
            out var result) ? result : fallback;
    }

    private static string Format(double value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ReloadProc();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(
        IntPtr module, string procedureName);

    private sealed class ProfileValues
    {
        public string Font { get; set; } = "PingFang SC";
        public bool ReplaceLatin { get; set; }
        public string Hinting { get; set; } = "1";
        public string Antialias { get; set; } = "0";
        public double Gamma { get; set; }
        public double Contrast { get; set; }
        public double Weight { get; set; }
        public double DirectWriteGamma { get; set; }
        public double DirectWriteContrast { get; set; }
    }
}
}
