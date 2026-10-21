using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

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
    private static readonly string AbStateFile =
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
            "MacType", "compare-state.txt");

    private static readonly string[] ChineseSources =
    {
        "Microsoft YaHei", "Microsoft YaHei UI", "DengXian", "SimHei",
        "SimSun", "NSimSun", "FangSong", "KaiTi"
    };

    private static readonly string[] LatinSources =
    {
        "Segoe UI", "Arial", "Tahoma", "Helvetica"
    };

    // Must match profiles/YMacType-macOS.ini.
    private static readonly string[] DefaultChain =
    {
        "PingFang SC", "Source Han Sans SC", "Noto Sans SC",
        "Noto Sans CJK SC", "HarmonyOS Sans SC", "MiSans"
    };

    private static readonly int[] SystemFontSizes =
        Enumerable.Range(6, 43).ToArray();

    private static readonly RecommendedFont[] RecommendedFonts =
    {
        new RecommendedFont("思源黑体 Source Han Sans SC",
            new[] { "Source Han Sans SC", "思源黑体" },
            "Adobe 与 Google 联合开发，SIL 开源许可，字重齐全。",
            "https://github.com/adobe-fonts/source-han-sans/releases"),
        new RecommendedFont("Noto Sans SC",
            new[] { "Noto Sans SC", "Noto Sans CJK SC" },
            "与思源黑体同源的 Google 版本，SIL 开源许可。",
            "https://fonts.google.com/noto/specimen/Noto+Sans+SC"),
        new RecommendedFont("HarmonyOS Sans SC",
            new[] { "HarmonyOS Sans SC" },
            "华为界面字体，笔画干净，可免费商用（需遵守其许可协议）。",
            "https://developer.huawei.com/consumer/cn/design/resource/"),
        new RecommendedFont("MiSans",
            new[] { "MiSans" },
            "小米界面字体，可免费商用（需遵守其许可协议）。",
            "https://hyperos.mi.com/font/")
    };

    private bool _loaded;
    private bool _allowClose;
    private bool _updatingTrayStartup;
    private bool _refreshingEffectiveApplications;
    private bool _effectiveScanned;
    private bool _serviceRunning;
    private bool _serviceInstalled = true;
    private HashSet<string> _installedFonts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<FontChainEntry> _chain =
        new ObservableCollection<FontChainEntry>();
    private List<ApplicationRuleInfo> _applicationRules =
        new List<ApplicationRuleInfo>();
    private List<EffectiveProcessInfo> _effectiveProcesses =
        new List<EffectiveProcessInfo>();
    private List<DiagnosticEntry> _diagnostics = new List<DiagnosticEntry>();
    private readonly Forms.NotifyIcon _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "版本 " +
            typeof(MainWindow).Assembly.GetName().Version;
        FontChainList.ItemsSource = _chain;
        var icon = LoadTrayIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "YMacType 字体渲染",
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(
            "显示已生效应用",
            null,
            (_, __) => Dispatcher.BeginInvoke(
                new Action(ShowEffectiveApplications)));
        menu.Items.Add(
            "打开设置",
            null,
            (_, __) => Dispatcher.BeginInvoke(
                new Action(ShowSettings)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(
            "刷新应用列表",
            null,
            (_, __) => Dispatcher.BeginInvoke(
                new Action(async () =>
                    await RefreshEffectiveApplicationsAsync())));
        menu.Items.Add(
            "退出 YMacType 设置",
            null,
            (_, __) => Dispatcher.BeginInvoke(
                new Action(ExitFromTray)));
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(
                    new Action(ShowEffectiveApplications));
        };

        Application.Current.SessionEnding +=
            (_, __) => _allowClose = true;
    }

    // ------------------------------------------------------------------
    // Window lifecycle and theme
    // ------------------------------------------------------------------

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        ApplyTitleBarTheme();
    }

    // The tray icon comes from the embedded app.ico at the size the tray
    // actually uses (16 px at 100% scale, larger on high-DPI displays), so it
    // is not a blurry downscale of the 32 px icon.
    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            using var stream = typeof(MainWindow).Assembly
                .GetManifestResourceStream("app.ico");
            if (stream != null)
                return new System.Drawing.Icon(
                    stream, Forms.SystemInformation.SmallIconSize);
        }
        catch
        {
            // Fall through to the executable's own icon.
        }

        var executable = Process.GetCurrentProcess().MainModule?.FileName;
        var associated = !string.IsNullOrEmpty(executable)
            ? System.Drawing.Icon.ExtractAssociatedIcon(executable)
            : null;
        return associated ?? System.Drawing.SystemIcons.Application;
    }

    public void OnThemeChanged()
    {
        ApplyTitleBarTheme();
        if (!_loaded)
            return;
        RefreshChainEntries();
        UpdatePreview();
        RefreshServiceBadge(_serviceRunning);
        RefreshHealth();
    }

    private void ApplyTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        var dark = App.IsDarkTheme ? 1 : 0;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+); 19 on older builds.
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _installedFonts = EnumerateInstalledFonts();
        FontSelector.ItemsSource = _installedFonts
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        SystemFontSelector.ItemsSource = FontSelector.ItemsSource;
        SystemFontSizeSelector.ItemsSource = SystemFontSizes;
        LoadProfile();
        LoadSystemFontSettings();
        RecommendedFontList.ItemsSource = RecommendedFonts
            .Select(font => new
            {
                font.Name,
                font.Note,
                font.Url,
                InstalledText = font.FamilyNames.Any(_installedFonts.Contains)
                    ? "已安装"
                    : ""
            })
            .ToList();
        _loaded = true;
        UpdatePreview();
        RefreshStatus();
        RefreshTrayStartup();
        LoadApplicationRules();
        RefreshCodePage();
        RefreshCompareState();
        UpdateApplyBar();
        RefreshHealth();
        _ = RefreshEffectiveApplicationsAsync();
        _ = RefreshDiagnosticsAsync();
    }

    public void ShowEffectiveApplications()
    {
        ShowAndActivate();
        var alreadySelected = ReferenceEquals(
            MainTabs.SelectedItem,
            EffectiveApplicationsTab);
        MainTabs.SelectedItem = EffectiveApplicationsTab;
        EffectiveSearchBox.Focus();
        if (_loaded && alreadySelected)
            _ = RefreshEffectiveApplicationsAsync();
    }

    private void MainTabs_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_loaded || !ReferenceEquals(e.OriginalSource, MainTabs))
            return;
        UpdateApplyBar();
        var selected = MainTabs.SelectedItem;
        if (ReferenceEquals(selected, OverviewTab))
            RefreshHealth();
        else if (ReferenceEquals(selected, ApplicationRulesTab))
            LoadApplicationRules();
        else if (ReferenceEquals(selected, EffectiveApplicationsTab))
            _ = RefreshEffectiveApplicationsAsync();
        else if (ReferenceEquals(selected, LauncherTab))
            LoadLauncherApps();
        else if (ReferenceEquals(selected, TroubleshootTab))
        {
            RefreshCodePage();
            RefreshCompareState();
            _ = RefreshDiagnosticsAsync();
        }
    }

    private void UpdateApplyBar()
    {
        var editing = ReferenceEquals(MainTabs.SelectedItem, FontsTab) ||
                      ReferenceEquals(MainTabs.SelectedItem, RenderTab);
        ApplyButtons.Visibility = editing
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowSettings()
    {
        ShowAndActivate();
        MainTabs.SelectedItem = OverviewTab;
    }

    private void ShowAndActivate()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        Hide();
        ResultText.Text = "设置面板已隐藏到系统托盘。";
    }

    private void ExitFromTray()
    {
        _allowClose = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        PanelLog.Write("exit requested from the tray");
        PanelLog.TerminateAfter(5000);
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------
    // Fonts
    // ------------------------------------------------------------------

    private static HashSet<string> EnumerateInstalledFonts()
    {
        // The core resolves names through GDI, WPF groups some families
        // differently (and GDI+ cannot see CFF-based OpenType fonts such as
        // Source Han Sans), so the union of both views is used.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
        {
            if (!string.IsNullOrWhiteSpace(family.Source))
                names.Add(family.Source);
            foreach (var localized in family.FamilyNames.Values)
            {
                if (!string.IsNullOrWhiteSpace(localized))
                    names.Add(localized);
            }
        }
        try
        {
            using var collection = new System.Drawing.Text.InstalledFontCollection();
            foreach (var family in collection.Families)
                names.Add(family.Name);
        }
        catch
        {
            // GDI+ font enumeration is best effort.
        }
        return names;
    }

    private static List<string> ParseChain(string value)
    {
        return value
            .Split('|')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void SetChain(IEnumerable<string> names)
    {
        _chain.Clear();
        foreach (var name in names)
            _chain.Add(new FontChainEntry { Name = name });
        RefreshChainEntries();
    }

    private void RefreshChainEntries()
    {
        var activeFound = false;
        for (var index = 0; index < _chain.Count; ++index)
        {
            var entry = _chain[index];
            var installed = _installedFonts.Contains(entry.Name);
            var active = installed && !activeFound;
            activeFound |= active;
            entry.Update(index + 1, installed, active);
        }
        FontChainWarning.Visibility = activeFound
            ? Visibility.Collapsed
            : Visibility.Visible;
        FontChainList.Items.Refresh();
        UpdateChainButtons();
    }

    private string? ActiveChainFont()
    {
        return _chain.FirstOrDefault(entry =>
            _installedFonts.Contains(entry.Name))?.Name;
    }

    private void FontChainList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        UpdateChainButtons();
    }

    private void UpdateChainButtons()
    {
        var index = FontChainList.SelectedIndex;
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < _chain.Count - 1;
        RemoveFontButton.IsEnabled = index >= 0 && _chain.Count > 1;
    }

    private void MoveFontUp_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedFont(-1);
    }

    private void MoveFontDown_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedFont(1);
    }

    private void MoveSelectedFont(int offset)
    {
        var index = FontChainList.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _chain.Count)
            return;
        _chain.Move(index, target);
        RefreshChainEntries();
        FontChainList.SelectedIndex = target;
        UpdatePreview();
    }

    private void RemoveFont_Click(object sender, RoutedEventArgs e)
    {
        var index = FontChainList.SelectedIndex;
        if (index < 0 || _chain.Count <= 1)
            return;
        _chain.RemoveAt(index);
        RefreshChainEntries();
        FontChainList.SelectedIndex = Math.Min(index, _chain.Count - 1);
        UpdatePreview();
    }

    private void AddFont_Click(object sender, RoutedEventArgs e)
    {
        if (!(FontSelector.SelectedItem is string name))
        {
            ResultText.Text = "先在下拉框里选一个已安装的字体。";
            return;
        }
        if (_chain.Any(entry => entry.Name.Equals(
                name, StringComparison.OrdinalIgnoreCase)))
        {
            ResultText.Text = $"{name} 已经在链条里。";
            return;
        }
        // A font picked on purpose goes to the top: that is almost always
        // what someone choosing a font wants to see take effect.
        _chain.Insert(0, new FontChainEntry { Name = name });
        RefreshChainEntries();
        FontChainList.SelectedIndex = 0;
        UpdatePreview();
        ResultText.Text = $"已把 {name} 放到链条第一位，点击“应用设置”保存。";
    }

    private void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!((sender as FrameworkElement)?.Tag is string url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ResultText.Text = $"无法打开浏览器：{exception.Message}";
        }
    }

    private void LoadProfile()
    {
        var ini = IniDocument.Load(ActiveProfile);
        var chain = ParseChain(ini.Get("FontSubstitutes", "Microsoft YaHei",
            ini.Get("FontSubstitutes", "Microsoft YaHei UI", "")));
        SetChain(chain.Count > 0 ? chain : DefaultChain.ToList());
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

    // ------------------------------------------------------------------
    // Windows UI font (WindowMetrics)
    // ------------------------------------------------------------------

    private void LoadSystemFontSettings()
    {
        var current = SystemFontSettings.ReadCurrent();
        var fonts = SystemFontSelector.Items.Cast<string>().ToList();
        SystemFontSelector.SelectedItem =
            fonts.FirstOrDefault(item => item.Equals(
                current.Family, StringComparison.CurrentCultureIgnoreCase))
            ?? fonts.FirstOrDefault(item => item.Equals(
                "Segoe UI", StringComparison.CurrentCultureIgnoreCase))
            ?? fonts.FirstOrDefault();
        SystemFontSizeSelector.SelectedItem = SystemFontSizes
            .OrderBy(size => Math.Abs(size - current.PointSize))
            .FirstOrDefault();
        UpdateSystemFontPreview();
        SystemFontStatusText.Text =
            $"Windows 当前使用 {current.Family}，{current.PointSize} 磅。";
    }

    private void SystemFontPreviewChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loaded)
            UpdateSystemFontPreview();
    }

    private void UpdateSystemFontPreview()
    {
        var familyName = SystemFontSelector.SelectedItem as string;
        var pointSize = GetSelectedSystemFontSize();
        // Points to WPF device-independent pixels (1/96 inch).
        var pixels = pointSize * 96.0 / 72.0;
        foreach (var block in new[]
                 {
                     SystemFontPreviewTitle, SystemFontPreviewMenu,
                     SystemFontPreviewBody, SystemFontPreviewStatus
                 })
        {
            if (!string.IsNullOrWhiteSpace(familyName))
            {
                try
                {
                    block.FontFamily = new FontFamily(familyName);
                }
                catch
                {
                    // Keep the current preview family if a font was unregistered.
                }
            }
            block.FontSize = pixels;
        }
    }

    private int GetSelectedSystemFontSize()
    {
        if (SystemFontSizeSelector.SelectedItem is int size)
            return size;
        return int.TryParse(
            Convert.ToString(SystemFontSizeSelector.SelectedItem,
                CultureInfo.InvariantCulture),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? Math.Max(6, Math.Min(48, parsed))
            : 9;
    }

    private async void ApplySystemFont_Click(
        object sender,
        RoutedEventArgs e)
    {
        var family = SystemFontSelector.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(family))
        {
            SystemFontStatusText.Text = "先选择一个字体。";
            return;
        }

        var size = GetSelectedSystemFontSize();
        try
        {
            ApplySystemFontButton.IsEnabled = false;
            RestoreSystemFontButton.IsEnabled = false;
            SystemFontStatusText.Text = $"正在备份并应用 {family}，{size} 磅…";
            await Task.Run(() => SystemFontSettings.Apply(family!, size));
            SystemFontStatusText.Text =
                $"已应用 {family}，{size} 磅。新打开的程序会使用新字体。";
            ResultText.Text = "Windows 界面字体已应用，原设置已备份。";
            UpdateSystemFontPreview();
        }
        catch (Exception exception)
        {
            SystemFontStatusText.Text = "应用失败：" + exception.Message;
            ResultText.Text = "Windows 界面字体应用失败。";
        }
        finally
        {
            ApplySystemFontButton.IsEnabled = true;
            RestoreSystemFontButton.IsEnabled = true;
        }
    }

    private async void RestoreSystemFont_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            ApplySystemFontButton.IsEnabled = false;
            RestoreSystemFontButton.IsEnabled = false;
            SystemFontStatusText.Text = "正在恢复上次的设置…";
            var backupDirectory = await Task.Run(() =>
                SystemFontSettings.RestoreLatest());
            if (backupDirectory == null)
            {
                SystemFontStatusText.Text = "没有可恢复的备份。还没有通过这里修改过 Windows 界面字体。";
                return;
            }
            LoadSystemFontSettings();
            ResultText.Text = "已恢复修改前的 Windows 界面字体。";
        }
        catch (Exception exception)
        {
            SystemFontStatusText.Text = "恢复失败：" + exception.Message;
        }
        finally
        {
            ApplySystemFontButton.IsEnabled = true;
            RestoreSystemFontButton.IsEnabled = true;
        }
    }

    private void PreviewChanged(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            UpdatePreview();
    }

    private void UpdatePreview()
    {
        var installed = _chain
            .Where(entry => _installedFonts.Contains(entry.Name))
            .Select(entry => entry.Name)
            .ToList();
        // WPF resolves a comma-separated family list the same way the core
        // walks the chain: the first installed family wins.
        var familyList = installed.Count > 0
            ? string.Join(", ", installed)
            : "Microsoft YaHei UI";
        try
        {
            var family = new FontFamily(familyList);
            PreviewDisplay.FontFamily = family;
            PreviewChinese.FontFamily = family;
            PreviewLatin.FontFamily = ReplaceLatinFonts.IsChecked == true
                ? family
                : new FontFamily("Segoe UI, " + familyList);
            PreviewUi.FontFamily = family;
        }
        catch
        {
            // Keep the current preview family if a malformed font is registered.
        }

        ResolvedFontText.Text = installed.Count > 0
            ? $"样张字体：{installed[0]}（链条中第 {_chain.ToList().FindIndex(entry => entry.Name.Equals(installed[0], StringComparison.OrdinalIgnoreCase)) + 1} 个）"
            : "链条里没有已安装的字体，样张显示的是系统默认的微软雅黑。";

        PreviewDisplay.FontSize = PreviewSizeSlider.Value;
        var visualWeight = Math.Max(
            250,
            Math.Min(
                700,
                400 + (WeightSlider.Value - 1.0) * 350));
        var weight = FontWeight.FromOpenTypeWeight((int)visualWeight);
        PreviewDisplay.FontWeight = weight;
        PreviewChinese.FontWeight = weight;
        PreviewLatin.FontWeight = weight;
        PreviewUi.FontWeight = weight;

        var dark = DarkPreview.IsChecked == true;
        PreviewCard.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(dark ? "#1B1C1F" : "#FFFFFF"));
        var ink = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(dark ? "#E9EAEC" : "#16181D"));
        var muted = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(dark ? "#9CA1AA" : "#5F6672"));
        PreviewDisplay.Foreground = ink;
        PreviewChinese.Foreground = ink;
        PreviewLatin.Foreground = ink;
        PreviewUi.Foreground = muted;

        GammaValueText.Text = GammaSlider.Value.ToString("0.00");
        ContrastValueText.Text = ContrastSlider.Value.ToString("0.00");
        WeightValueText.Text = WeightSlider.Value.ToString("0.00");
        DwGammaValueText.Text = DwGammaSlider.Value.ToString("0.00");
        DwContrastValueText.Text = DwContrastSlider.Value.ToString("0.00");
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "balanced":
                ApplyPreset("2", "0", 1.4, 1.1, 1.0, 1.4, 1.0, "均衡");
                break;
            case "crisp":
                ApplyPreset("0", "4", 1.6, 1.2, 0.95, 1.8, 1.2, "清晰锐利");
                break;
            default:
                ApplyPreset("1", "0", 1.2, 1.0, 1.15, 1.15, 0.8, "macOS 风格");
                break;
        }
    }

    private void ApplyPreset(
        string hinting,
        string antialias,
        double gamma,
        double contrast,
        double weight,
        double dwGamma,
        double dwContrast,
        string name)
    {
        SelectTag(HintingSelector, hinting);
        SelectTag(AntialiasSelector, antialias);
        GammaSlider.Value = gamma;
        ContrastSlider.Value = contrast;
        WeightSlider.Value = weight;
        DwGammaSlider.Value = dwGamma;
        DwContrastSlider.Value = dwContrast;
        UpdatePreview();
        ResultText.Text = $"已载入“{name}”参数，点击“应用设置”保存。";
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ResultText.Text = "正在备份并应用设置…";
            ApplyButton.IsEnabled = false;
            var values = CaptureValues();
            await Task.Run(() => ApplyProfile(values));
            ReloadCurrentProcess();
            ResultText.Text = values.ActiveFont == null
                ? "已保存。注意：链条里没有已安装的字体，字体替换不会生效。"
                : $"已保存，替换字体为 {values.ActiveFont}。新打开的程序立即生效，已打开的程序请重新启动。";
            RefreshStatus();
            RefreshHealth();
        }
        catch (Exception exception)
        {
            ResultText.Text = "保存失败：" + exception.Message;
            MessageBox.Show(
                this, exception.Message, "YMacType 设置",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    private ProfileValues CaptureValues()
    {
        return new ProfileValues
        {
            Chain = _chain.Select(entry => entry.Name).ToList(),
            ActiveFont = ActiveChainFont(),
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

        // The core accepts "A|B|C" and uses the first installed family.
        var target = string.Join("|", values.Chain);
        foreach (var source in ChineseSources)
            ini.Set("FontSubstitutes", source, target);
        foreach (var source in LatinSources)
        {
            if (values.ReplaceLatin)
                ini.Set("FontSubstitutes", source, target);
            else
                ini.Remove("FontSubstitutes", source);
        }
        foreach (var family in values.Chain)
            ini.Set("Individual", family, "1,0,0,0,0,1");
        ini.Save(ActiveProfile);
        EnsureMainProfilePointer();

        // New target processes read the profile during initialization. Restarting
        // the injection service here can race with this elevated control process
        // and its service-control children, so applying settings is deliberately
        // a configuration-only operation.
    }

    private static void EnsureMainProfilePointer()
    {
        if (!File.Exists(MainConfig))
            throw new FileNotFoundException("找不到 MacType.ini，请重新运行安装程序。", MainConfig);
        var ini = LoadConfig(MainConfig);
        ini.Set("General", "AlternativeFile", @"ini\YMacType-macOS.ini");
        ini.Save(MainConfig);
    }

    private static IniDocument LoadConfig(string path)
    {
        return IniDocument.Load(
            path,
            preferUtf16: string.Equals(
                path, MainConfig, StringComparison.OrdinalIgnoreCase));
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
        if (!process.WaitForExit(15000))
        {
            process.Kill();
            throw new TimeoutException(
                $"服务控制超时：sc.exe {arguments}");
        }
        if (!allowFailure && process.ExitCode != 0)
            throw new InvalidOperationException(
                $"服务配置失败：sc.exe {arguments}\n" +
                process.StandardOutput.ReadToEnd() +
                process.StandardError.ReadToEnd());
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        SetChain(DefaultChain);
        ReplaceLatinFonts.IsChecked = false;
        PreviewSizeSlider.Value = 44;
        ApplyPreset("1", "0", 1.2, 1.0, 1.15, 1.15, 0.8, "macOS 风格");
        ResultText.Text = "已恢复推荐的字体链和渲染参数，点击“应用设置”保存。";
    }

    // ------------------------------------------------------------------
    // Overview
    // ------------------------------------------------------------------

    private void RefreshHealth()
    {
        var items = new List<HealthItem>();
        var running = _serviceRunning;
        items.Add(running
            ? HealthItem.Good("字体服务正在运行", "新打开的程序会自动加载 YMacType。")
            : HealthItem.Bad(
                _serviceInstalled ? "字体服务没有运行" : "字体服务没有安装",
                _serviceInstalled
                    ? "没有服务，所有程序都不会被处理。"
                    : "这台电脑没有 MacType 服务，所有程序都不会被处理。可以由 YMacType 创建。",
                _serviceInstalled ? "启动服务" : "安装并启动服务", "service"));

        var savedChain = ParseChain(IniDocument.Load(ActiveProfile).Get(
            "FontSubstitutes", "Microsoft YaHei", ""));
        var active = savedChain.FirstOrDefault(_installedFonts.Contains);
        items.Add(active != null
            ? HealthItem.Good($"替换字体：{active}",
                "程序请求微软雅黑、等线、宋体等中文字体时会改用它。")
            : HealthItem.Warn("替换字体没有安装",
                "字体链里的字体都没有安装，只有渲染效果会变化，字体不会被替换。",
                "选择字体", "fonts"));

        var encodingOk = IniDocument.HasSafeEncoding(ActiveProfile) &&
                         IniDocument.HasSafeEncoding(MainConfig);
        items.Add(encodingOk
            ? HealthItem.Good("配置文件编码正确",
                "核心能正确读取配置里的中文字体名。")
            : HealthItem.Bad("配置文件缺少 BOM",
                "核心会按 GBK 解读这个文件，中文字体名会变成乱码，相关规则失效。",
                "修复编码", "encoding"));

        if (_effectiveScanned)
        {
            var count = _effectiveProcesses.Count;
            items.Add(count > 0
                ? HealthItem.Good($"{count} 个进程已加载",
                    "包括 " + string.Join("、", _effectiveProcesses
                        .Select(process => process.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(4)) + (count > 4 ? " 等" : "") + "。",
                    "查看", "effective")
                : HealthItem.Warn("还没有进程加载 YMacType",
                    "服务启动之前打开的程序不会被处理，重新打开它们即可。",
                    "查看", "effective"));
        }

        var warnings = _diagnostics.Count(entry => entry.IsWarning);
        var errors = _diagnostics.Count - warnings;
        items.Add(_diagnostics.Count == 0
            ? HealthItem.Good("最近 24 小时没有警告", "日志里没有需要处理的记录。")
            : errors > 0
                ? HealthItem.Bad($"最近 24 小时：{errors} 个错误，{warnings} 个警告",
                    _diagnostics.First(entry => !entry.IsWarning).Summary,
                    "查看", "diagnostics")
                : HealthItem.Warn($"最近 24 小时：{warnings} 个警告",
                    _diagnostics.First().Summary,
                    "查看", "diagnostics"));

        var codePage = ReadCodePage();
        if (codePage.Acp == "65001")
            items.Add(HealthItem.Warn("系统已切换为 UTF-8 编码",
                "老的中文程序（GBK 编码）会出现乱码。这和 YMacType 无关。",
                "了解", "diagnostics"));
        else if (codePage.Acp != "936" && codePage.Acp.Length > 0)
            items.Add(HealthItem.Warn($"系统代码页为 {codePage.Acp}",
                "非 Unicode 的中文程序会显示乱码。",
                "了解", "diagnostics"));

        HealthList.ItemsSource = items;
    }

    private async void HealthAction_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "service":
                await RepairServiceAsync(sender as Button);
                break;
            case "fonts":
                MainTabs.SelectedItem = FontsTab;
                break;
            case "encoding":
                FixProfileEncoding();
                break;
            case "effective":
                MainTabs.SelectedItem = EffectiveApplicationsTab;
                break;
            case "diagnostics":
                MainTabs.SelectedItem = TroubleshootTab;
                break;
        }
    }

    private void FixProfileEncoding()
    {
        try
        {
            foreach (var path in new[] { ActiveProfile, MainConfig })
            {
                if (!File.Exists(path) || IniDocument.HasSafeEncoding(path))
                    continue;
                File.Copy(path, path + ".before-bom", true);
                LoadConfig(path).Save(path);
            }
            ResultText.Text = "编码已修复，原文件备份为 .before-bom。重新打开程序后生效。";
        }
        catch (Exception exception)
        {
            ResultText.Text = "修复失败：" + exception.Message;
        }
        RefreshHealth();
    }

    private void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        RefreshTrayStartup();
        RefreshHealth();
        _ = RefreshEffectiveApplicationsAsync();
        _ = RefreshDiagnosticsAsync();
    }

    private async void TrayStartup_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (!_loaded || _updatingTrayStartup)
            return;
        var enabled = TrayStartupCheckBox.IsChecked == true;
        try
        {
            TrayStartupCheckBox.IsEnabled = false;
            TrayStartupDescription.Text = enabled
                ? "正在启用登录启动…"
                : "正在关闭登录启动…";
            await Task.Run(() =>
                Installer.SetTrayStartupEnabled(enabled));
            ResultText.Text = enabled
                ? "已启用：下次登录后显示托盘图标。"
                : "已关闭：下次登录后不再显示托盘图标。";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "YMacType 自动启动",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            TrayStartupCheckBox.IsEnabled = true;
            RefreshTrayStartup();
            RefreshStatus();
        }
    }

    private void RefreshTrayStartup()
    {
        var enabled = Installer.IsTrayStartupEnabled();
        _updatingTrayStartup = true;
        try
        {
            TrayStartupCheckBox.IsChecked = enabled;
            TrayStartupDescription.Text = enabled
                ? "登录后显示托盘图标。核心字体服务总是随系统启动，与这个开关无关。"
                : "登录后不显示托盘图标。核心字体服务仍会随系统启动并继续工作。";
        }
        finally
        {
            _updatingTrayStartup = false;
        }
    }

    // ------------------------------------------------------------------
    // Effective applications
    // ------------------------------------------------------------------

    private async void RefreshEffective_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RefreshEffectiveApplicationsAsync();
    }

    private void EffectiveSearch_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        ApplyEffectiveApplicationsFilter();
    }

    private async Task RefreshEffectiveApplicationsAsync()
    {
        if (_refreshingEffectiveApplications)
            return;
        _refreshingEffectiveApplications = true;
        try
        {
            var excluded = LoadExcludedExecutables();
            var processes = await Task.Run(
                () => ScanEffectiveApplications(excluded));
            _effectiveProcesses = processes;
            _effectiveScanned = true;
            ApplyEffectiveApplicationsFilter();
            _trayIcon.Text = processes.Count == 0
                ? "YMacType · 暂无已生效应用"
                : $"YMacType · {processes.Count} 个进程已生效";
            RefreshStatus();
            if (ReferenceEquals(MainTabs.SelectedItem, OverviewTab))
                RefreshHealth();
        }
        finally
        {
            _refreshingEffectiveApplications = false;
        }
    }

    private void QuickRule_Click(object sender, RoutedEventArgs e)
    {
        if (!(EffectiveApplicationsGrid.SelectedItem is EffectiveProcessInfo process))
        {
            ResultText.Text = "先在列表里选中一个程序。";
            return;
        }
        var executable = process.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? process.Name
            : process.Name + ".exe";
        var mode = ((sender as FrameworkElement)?.Tag as string) == "RenderOnly"
            ? ApplicationRuleMode.RenderOnly
            : ApplicationRuleMode.Blocked;
        try
        {
            UpdateApplicationRule(executable, mode);
            LoadApplicationRules();
            ResultText.Text = mode == ApplicationRuleMode.Blocked
                ? $"{executable} 以后不再被处理。完全退出它再打开后生效。"
                : $"{executable} 改为只渲染、不替换字体。完全退出它再打开后生效。";
        }
        catch (Exception exception)
        {
            ResultText.Text = "保存规则失败：" + exception.Message;
        }
    }

    private static HashSet<string> LoadExcludedExecutables()
    {
        try
        {
            var ini = IniDocument.Load(ActiveProfile);
            return new HashSet<string>(
                ini.GetEntries("ExcludeModule")
                    .Concat(ini.GetEntries("UnloadDll")),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------
    // Application rules
    // ------------------------------------------------------------------

    private void LoadApplicationRules()
    {
        try
        {
            var profile = IniDocument.Load(ActiveProfile);
            var blocked = new HashSet<string>(
                profile.GetEntries("UnloadDll")
                    .Concat(profile.GetEntries("ExcludeModule")),
                StringComparer.OrdinalIgnoreCase);
            var renderOnly = new HashSet<string>(
                profile.GetEntries("ExcludeSub"),
                StringComparer.OrdinalIgnoreCase);
            _applicationRules = blocked
                .Select(name => new ApplicationRuleInfo
                {
                    ExecutableName = name,
                    Mode = ApplicationRuleMode.Blocked
                })
                .Concat(renderOnly
                    .Where(name => !blocked.Contains(name))
                    .Select(name => new ApplicationRuleInfo
                    {
                        ExecutableName = name,
                        Mode = ApplicationRuleMode.RenderOnly
                    }))
                .OrderBy(
                    rule => rule.ExecutableName,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            ApplyApplicationRuleFilter();
            if (RuleModeSelector.SelectedIndex < 0)
                RuleModeSelector.SelectedIndex = 0;
        }
        catch (Exception exception)
        {
            ResultText.Text = $"无法读取应用规则：{exception.Message}";
        }
    }

    private void RuleSearch_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_loaded)
            ApplyApplicationRuleFilter();
    }

    private void ApplyApplicationRuleFilter()
    {
        var query = RuleSearchBox.Text.Trim();
        ApplicationRulesGrid.ItemsSource = string.IsNullOrEmpty(query)
            ? _applicationRules
            : _applicationRules.Where(rule =>
                    rule.ExecutableName.IndexOf(
                        query,
                        StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    rule.ModeLabel.IndexOf(
                        query,
                        StringComparison.CurrentCultureIgnoreCase) >= 0)
                .ToList();
    }

    private void ApplicationRule_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!(ApplicationRulesGrid.SelectedItem is ApplicationRuleInfo rule))
            return;
        RuleExecutableBox.Text = rule.ExecutableName;
        RuleModeSelector.SelectedIndex =
            rule.Mode == ApplicationRuleMode.Blocked ? 0 : 1;
    }

    private void BrowseRuleExecutable_Click(
        object sender,
        RoutedEventArgs e)
    {
        var picked = PickExecutable();
        if (picked != null)
            RuleExecutableBox.Text = picked;
    }

    private string? PickExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择程序",
            Filter = "Windows 程序 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(this) == true
            ? Path.GetFileName(dialog.FileName)
            : null;
    }

    private void SaveApplicationRule_Click(
        object sender,
        RoutedEventArgs e)
    {
        var executable = NormalizeExecutableName(
            RuleExecutableBox.Text);
        if (executable == null)
            return;
        var mode = SelectedTag(RuleModeSelector, "Blocked") ==
                   "RenderOnly"
            ? ApplicationRuleMode.RenderOnly
            : ApplicationRuleMode.Blocked;
        try
        {
            UpdateApplicationRule(executable, mode);
            LoadApplicationRules();
            SelectApplicationRule(executable);
            ResultText.Text = mode == ApplicationRuleMode.Blocked
                ? $"{executable} 以后不再被处理。完全退出它再打开后生效。"
                : $"{executable} 改为只渲染、不替换字体。完全退出它再打开后生效。";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "YMacType 应用规则",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RemoveApplicationRule_Click(
        object sender,
        RoutedEventArgs e)
    {
        var selected =
            ApplicationRulesGrid.SelectedItem as ApplicationRuleInfo;
        var executable = selected?.ExecutableName ??
                         NormalizeExecutableName(
                             RuleExecutableBox.Text,
                             showError: false);
        if (executable == null || executable.Length == 0)
        {
            ResultText.Text = "先在列表里选中要删除的规则。";
            return;
        }
        try
        {
            RemoveApplicationRule(executable);
            RuleExecutableBox.Clear();
            LoadApplicationRules();
            ResultText.Text =
                $"已删除 {executable} 的规则。完全退出它再打开后生效。";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "YMacType 应用规则",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private string? NormalizeExecutableName(
        string value,
        bool showError = true)
    {
        try
        {
            var executable = Path.GetFileName(value.Trim());
            if (!string.IsNullOrEmpty(executable) &&
                executable.EndsWith(
                    ".exe",
                    StringComparison.OrdinalIgnoreCase) &&
                executable.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                return executable;
        }
        catch (ArgumentException)
        {
            // The validation message below also covers malformed paths.
        }
        if (showError)
            ResultText.Text = "请输入 .exe 程序名，例如 chrome.exe，或点“选择程序…”。";
        return null;
    }

    private static void UpdateApplicationRule(
        string executable,
        ApplicationRuleMode mode)
    {
        foreach (var path in new[] { ActiveProfile, MainConfig })
        {
            var ini = LoadConfig(path);
            ini.RemoveEntry("UnloadDll", executable);
            ini.RemoveEntry("ExcludeModule", executable);
            ini.RemoveEntry("ExcludeSub", executable);
            ini.AddEntry(
                mode == ApplicationRuleMode.Blocked
                    ? "UnloadDll"
                    : "ExcludeSub",
                executable);
            ini.Save(path);
        }
    }

    private static void RemoveApplicationRule(string executable)
    {
        foreach (var path in new[] { ActiveProfile, MainConfig })
        {
            var ini = LoadConfig(path);
            ini.RemoveEntry("UnloadDll", executable);
            ini.RemoveEntry("ExcludeModule", executable);
            ini.RemoveEntry("ExcludeSub", executable);
            ini.Save(path);
        }
    }

    private void SelectApplicationRule(string executable)
    {
        var selected = ApplicationRulesGrid.Items
            .Cast<ApplicationRuleInfo>()
            .FirstOrDefault(rule => rule.ExecutableName.Equals(
                executable,
                StringComparison.OrdinalIgnoreCase));
        if (selected == null)
            return;
        ApplicationRulesGrid.SelectedItem = selected;
        ApplicationRulesGrid.ScrollIntoView(selected);
    }

    private static List<EffectiveProcessInfo> ScanEffectiveApplications(
        HashSet<string> excluded)
    {
        var result = new List<EffectiveProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var executableName = process.ProcessName.EndsWith(
                    ".exe",
                    StringComparison.OrdinalIgnoreCase)
                    ? process.ProcessName
                    : process.ProcessName + ".exe";
                if (excluded.Contains(executableName))
                    continue;
                var core = process.Modules.Cast<ProcessModule>()
                    .FirstOrDefault(module =>
                        string.Equals(
                            module.ModuleName,
                            "MacType.Core.dll",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            module.ModuleName,
                            "MacType64.Core.dll",
                            StringComparison.OrdinalIgnoreCase));
                if (core == null)
                    continue;
                string path;
                try
                {
                    path = process.MainModule?.FileName ?? "";
                }
                catch
                {
                    path = "";
                }
                result.Add(new EffectiveProcessInfo
                {
                    Name = process.ProcessName,
                    ProcessId = process.Id,
                    Architecture = string.Equals(
                        core.ModuleName,
                        "MacType64.Core.dll",
                        StringComparison.OrdinalIgnoreCase)
                        ? "x64"
                        : "x86",
                    Path = path
                });
            }
            catch
            {
                // Protected and terminating processes may reject enumeration.
            }
            finally
            {
                process.Dispose();
            }
        }
        return result
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.ProcessId)
            .ToList();
    }

    private void ApplyEffectiveApplicationsFilter()
    {
        if (EffectiveApplicationsGrid == null ||
            EffectiveSearchBox == null)
            return;
        var query = EffectiveSearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _effectiveProcesses
            : _effectiveProcesses.Where(item =>
                    item.Name.IndexOf(
                        query,
                        StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    item.ProcessId.ToString(
                        CultureInfo.InvariantCulture).IndexOf(
                            query,
                            StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.Path.IndexOf(
                        query,
                        StringComparison.CurrentCultureIgnoreCase) >= 0)
                .ToList();
        EffectiveApplicationsGrid.ItemsSource = filtered;
        NoEffectiveApplicationsText.Visibility =
            filtered.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // Troubleshooting
    // ------------------------------------------------------------------

    private void BrowseAbExecutable_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickExecutable();
        if (picked != null)
            AbExecutableBox.Text = picked;
    }

    private static readonly string[] RuleSections =
    {
        "UnloadDll", "ExcludeModule", "ExcludeSub"
    };

    // Remembers exactly which rule sections an application was in, in both
    // configuration files, so "恢复" puts it back the way it was.
    private sealed class CompareState
    {
        public string Executable { get; set; } = "";
        public string[] ProfileSections { get; set; } = Array.Empty<string>();
        public string[] MainSections { get; set; } = Array.Empty<string>();
    }

    private static string[] CaptureSections(string path, string executable)
    {
        var ini = LoadConfig(path);
        return RuleSections
            .Where(section => ini.GetEntries(section).Any(entry =>
                entry.Equals(executable, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static void RestoreSections(string path, string executable, string[] sections)
    {
        var ini = LoadConfig(path);
        foreach (var section in RuleSections)
            ini.RemoveEntry(section, executable);
        foreach (var section in sections)
            ini.AddEntry(section, executable);
        ini.Save(path);
    }

    private void AbPause_Click(object sender, RoutedEventArgs e)
    {
        var executable = NormalizeExecutableName(AbExecutableBox.Text);
        if (executable == null)
            return;
        try
        {
            var state = ReadCompareState();
            if (state != null && !state.Executable.Equals(
                    executable, StringComparison.OrdinalIgnoreCase))
            {
                AbStatusText.Text = $"正在对比 {state.Executable}，先点“恢复”结束那一次对比。";
                return;
            }
            if (state == null)
            {
                WriteCompareState(new CompareState
                {
                    Executable = executable,
                    ProfileSections = CaptureSections(ActiveProfile, executable),
                    MainSections = CaptureSections(MainConfig, executable)
                });
            }
            UpdateApplicationRule(executable, ApplicationRuleMode.Blocked);
            LoadApplicationRules();
            RefreshCompareState();
        }
        catch (Exception exception)
        {
            AbStatusText.Text = "暂停失败：" + exception.Message;
        }
    }

    private void AbRenderOnly_Click(object sender, RoutedEventArgs e)
    {
        var executable = NormalizeExecutableName(AbExecutableBox.Text);
        if (executable == null)
            return;
        var state = ReadCompareState();
        if (state != null && !state.Executable.Equals(
                executable, StringComparison.OrdinalIgnoreCase))
        {
            AbStatusText.Text = $"正在对比 {state.Executable}，先点“恢复”结束那一次对比。";
            return;
        }
        try
        {
            UpdateApplicationRule(executable, ApplicationRuleMode.RenderOnly);
            ClearCompareState();
            LoadApplicationRules();
            RefreshCompareState();
            AbStatusText.Text =
                $"{executable} 已改为只渲染、不替换字体。完全退出再打开，看文字是否正常。";
        }
        catch (Exception exception)
        {
            AbStatusText.Text = "保存失败：" + exception.Message;
        }
    }

    private void AbRestore_Click(object sender, RoutedEventArgs e)
    {
        var state = ReadCompareState();
        if (state == null)
        {
            AbStatusText.Text = "当前没有进行中的对比。";
            RefreshCompareState();
            return;
        }
        try
        {
            RestoreSections(ActiveProfile, state.Executable, state.ProfileSections);
            RestoreSections(MainConfig, state.Executable, state.MainSections);
            ClearCompareState();
            LoadApplicationRules();
            RefreshCompareState();
            AbStatusText.Text = $"{state.Executable} 已恢复为对比前的设置。完全退出再打开后生效。";
        }
        catch (Exception exception)
        {
            AbStatusText.Text = "恢复失败：" + exception.Message;
        }
    }

    private void RefreshCompareState()
    {
        var state = ReadCompareState();
        AbRestoreButton.IsEnabled = state != null;
        if (state == null)
        {
            if (string.IsNullOrEmpty(AbStatusText.Text))
                AbStatusText.Text = "当前没有进行中的对比。";
            return;
        }
        AbExecutableBox.Text = state.Executable;
        AbStatusText.Text =
            $"正在对比：{state.Executable} 已暂停处理。完全退出它再打开，看乱码是否消失。";
    }

    private static CompareState? ReadCompareState()
    {
        try
        {
            if (!File.Exists(AbStateFile))
                return null;
            var parts = File.ReadAllText(AbStateFile, Encoding.UTF8).Trim().Split('|');
            if (parts.Length < 3 || parts[0].Length == 0)
                return null;
            string[] Split(string value) => value
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(section => RuleSections.Contains(section))
                .ToArray();
            return new CompareState
            {
                Executable = parts[0],
                ProfileSections = Split(parts[1]),
                MainSections = Split(parts[2])
            };
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCompareState(CompareState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AbStateFile)!);
        File.WriteAllText(
            AbStateFile,
            state.Executable + "|" +
            string.Join(",", state.ProfileSections) + "|" +
            string.Join(",", state.MainSections),
            Encoding.UTF8);
    }

    private static void ClearCompareState()
    {
        if (File.Exists(AbStateFile))
            File.Delete(AbStateFile);
    }

    private static (string Acp, string Oemcp) ReadCodePage()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Nls\CodePage");
            return ((key?.GetValue("ACP") as string) ?? "",
                (key?.GetValue("OEMCP") as string) ?? "");
        }
        catch
        {
            return ("", "");
        }
    }

    private void RefreshCodePage()
    {
        var (acp, oemcp) = ReadCodePage();
        CodePageText.Text = acp.Length == 0
            ? "无法读取系统代码页。"
            : $"非 Unicode 程序使用的代码页：{acp}（{DescribeCodePage(acp)}），控制台代码页：{oemcp}。";
        CodePageAdvice.Text = acp switch
        {
            "65001" =>
                "已开启“Beta 版：使用 Unicode UTF-8 提供全球语言支持”。新程序更好，但不少老的中文程序会把文字显示成“浣犲ソ”这样的乱码。遇到这种情况，在区域设置的“管理”页关闭该选项并重启。",
            "936" =>
                "老的中文程序可以正常显示。个别程序或文本文件按 UTF-8 保存却被当作 GBK 读取时会出现“锟斤拷”式乱码，需要在那个程序里改编码，改系统设置没有帮助。",
            "" => "",
            _ =>
                "老的中文程序会显示乱码。在区域设置的“管理”页，把“非 Unicode 程序的语言”改为“中文(简体，中国)”并重启。"
        };
    }

    private static string DescribeCodePage(string acp)
    {
        return acp switch
        {
            "936" => "简体中文 GBK",
            "950" => "繁体中文 Big5",
            "65001" => "UTF-8",
            "1252" => "西欧",
            "932" => "日文 Shift-JIS",
            "949" => "韩文",
            _ => "其他"
        };
    }

    private void OpenRegionSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "intl.cpl")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ResultText.Text = "无法打开区域设置：" + exception.Message;
        }
    }

    private async void RefreshDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDiagnosticsAsync();
    }

    private async Task RefreshDiagnosticsAsync()
    {
        var entries = await Task.Run(() => ReadRecentDiagnostics());
        _diagnostics = entries;
        DiagnosticsGrid.ItemsSource = entries;
        NoDiagnosticsText.Visibility = entries.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (ReferenceEquals(MainTabs.SelectedItem, OverviewTab))
            RefreshHealth();
    }

    private static List<DiagnosticEntry> ReadRecentDiagnostics()
    {
        var result = new List<DiagnosticEntry>();
        if (!Directory.Exists(LogDirectory))
            return result;
        var since = DateTime.Now.AddHours(-24);
        foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log*")
                     .Where(file => File.GetLastWriteTime(file) > since))
        {
            var process = ProcessFromLogName(Path.GetFileName(file));
            foreach (var line in ReadSharedLines(file))
            {
                var entry = DiagnosticEntry.TryParse(line, process);
                if (entry != null && entry.Timestamp > since)
                    result.Add(entry);
            }
        }
        // One line per process and code is enough to act on; repeated
        // identical warnings from every child process are noise.
        return result
            .OrderByDescending(entry => entry.Timestamp)
            .GroupBy(entry => (entry.Process.ToLowerInvariant(), entry.Code))
            .Select(group => group.First())
            .Take(200)
            .ToList();
    }

    private static string ProcessFromLogName(string name)
    {
        // mactype-<exe>-<pid>.log[.1]
        var trimmed = name;
        if (trimmed.StartsWith("mactype-", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(8);
        var logIndex = trimmed.IndexOf(".log", StringComparison.OrdinalIgnoreCase);
        if (logIndex > 0)
            trimmed = trimmed.Substring(0, logIndex);
        var dash = trimmed.LastIndexOf('-');
        return dash > 0 ? trimmed.Substring(0, dash) : trimmed;
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        var lines = new List<string>();
        try
        {
            // Injected processes keep their log open for writing.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
                lines.Add(line);
        }
        catch
        {
            // A process may rotate a log while it is being read.
        }
        return lines;
    }

    // ------------------------------------------------------------------
    // Service status
    // ------------------------------------------------------------------

    private async void RepairService_Click(object sender, RoutedEventArgs e)
    {
        await RepairServiceAsync(sender as Button);
    }

    private async Task RepairServiceAsync(Button? button)
    {
        try
        {
            if (button != null)
                button.IsEnabled = false;
            ResultText.Text = "正在检查并修复服务…";
            await Task.Run(() =>
            {
                // No MacType service on this PC: register YMacType's own.
                Installer.EnsureServiceRegistered();

                RunSc("config MacType start= auto obj= LocalSystem");
                RunSc(
                    "failure MacType reset= 86400 " +
                    "actions= restart/5000/restart/15000/restart/60000");
                RunSc("failureflag MacType 1");
                if (!ServiceRunning())
                    RunSc("start MacType");
                for (var attempt = 0; attempt != 40; ++attempt)
                {
                    if (ServiceRunning())
                        return;
                    System.Threading.Thread.Sleep(250);
                }
                throw new TimeoutException("MacType 服务未能在 10 秒内进入运行状态。");
            });
            ResultText.Text = "服务已修复并正常运行。";
        }
        catch (Exception exception)
        {
            ResultText.Text = "服务修复失败";
            MessageBox.Show(
                this, exception.Message, "YMacType 设置",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (button != null)
                button.IsEnabled = true;
            RefreshStatus();
            RefreshHealth();
        }
    }

    private void RefreshServiceBadge(bool running)
    {
        ServiceBadge.Text = running
            ? "服务正在运行"
            : (_serviceInstalled ? "服务未运行" : "服务未安装");
        ServiceDot.SetResourceReference(
            System.Windows.Shapes.Shape.FillProperty, running ? "GoodBrush" : "BadBrush");
        ServiceBadgeBorder.SetResourceReference(
            Border.BackgroundProperty, running ? "GoodSoftBrush" : "BadSoftBrush");
    }

    private void RefreshStatus()
    {
        var installed = Installer.ServiceInstalled();
        _serviceInstalled = installed;
        var running = installed && ServiceRunning();
        _serviceRunning = running;
        RefreshServiceBadge(running);
        var covered = _effectiveProcesses.Count;
        var names = _effectiveProcesses
            .Select(process => process.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var modeText = installed
            ? (running ? "自动启动 · LocalSystem · 运行中" : "已停止")
            : "未安装，点击“启动并修复服务”创建";
        StatusText.Text =
            $"服务      {modeText}\n" +
            $"配置      {ActiveProfile}\n" +
            $"已加载    {covered} 个进程（{names} 个程序）\n" +
            $"日志      {LogDirectory}";
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

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

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

    private static Brush ResourceBrush(string key)
    {
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ReloadProc();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(
        IntPtr module, string procedureName);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);

    private sealed class ProfileValues
    {
        public List<string> Chain { get; set; } = new List<string>();
        public string? ActiveFont { get; set; }
        public bool ReplaceLatin { get; set; }
        public string Hinting { get; set; } = "1";
        public string Antialias { get; set; } = "0";
        public double Gamma { get; set; }
        public double Contrast { get; set; }
        public double Weight { get; set; }
        public double DirectWriteGamma { get; set; }
        public double DirectWriteContrast { get; set; }
    }

    private sealed class FontChainEntry
    {
        public string Name { get; set; } = "";
        public string Position { get; private set; } = "";
        public string StatusText { get; private set; } = "";
        public FontWeight NameWeight { get; private set; } = FontWeights.Normal;
        public Brush BadgeBackground { get; private set; } = Brushes.Transparent;
        public Brush BadgeForeground { get; private set; } = Brushes.Gray;

        public void Update(int position, bool installed, bool active)
        {
            Position = position.ToString(CultureInfo.InvariantCulture);
            NameWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            if (active)
            {
                StatusText = "使用中";
                BadgeBackground = ResourceBrush("GoodSoftBrush");
                BadgeForeground = ResourceBrush("GoodBrush");
            }
            else if (installed)
            {
                StatusText = "备用";
                BadgeBackground = ResourceBrush("HoverBrush");
                BadgeForeground = ResourceBrush("MutedBrush");
            }
            else
            {
                StatusText = "未安装";
                BadgeBackground = Brushes.Transparent;
                BadgeForeground = ResourceBrush("MutedBrush");
            }
        }
    }

    private sealed class RecommendedFont
    {
        public RecommendedFont(string name, string[] familyNames, string note, string url)
        {
            Name = name;
            FamilyNames = familyNames;
            Note = note;
            Url = url;
        }

        public string Name { get; }
        public string[] FamilyNames { get; }
        public string Note { get; }
        public string Url { get; }
    }

    private enum HealthState
    {
        Good,
        Warn,
        Bad
    }

    private sealed class HealthItem
    {
        public string Title { get; private set; } = "";
        public string Detail { get; private set; } = "";
        public HealthState State { get; private set; }
        public string ActionLabel { get; private set; } = "";
        public string ActionKey { get; private set; } = "";
        public Visibility ActionVisibility =>
            ActionKey.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        public Brush StateBrush => ResourceBrush(State switch
        {
            HealthState.Good => "GoodBrush",
            HealthState.Warn => "WarnBrush",
            _ => "BadBrush"
        });

        public static HealthItem Good(
            string title, string detail, string action = "", string key = "") =>
            Create(HealthState.Good, title, detail, action, key);

        public static HealthItem Warn(
            string title, string detail, string action = "", string key = "") =>
            Create(HealthState.Warn, title, detail, action, key);

        public static HealthItem Bad(
            string title, string detail, string action = "", string key = "") =>
            Create(HealthState.Bad, title, detail, action, key);

        private static HealthItem Create(
            HealthState state, string title, string detail, string action, string key)
        {
            return new HealthItem
            {
                State = state,
                Title = title,
                Detail = detail,
                ActionLabel = action,
                ActionKey = key
            };
        }
    }

    private sealed class DiagnosticEntry
    {
        public DateTime Timestamp { get; private set; }
        public string Time => Timestamp.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        public string Process { get; private set; } = "";
        public int Code { get; private set; }
        public bool IsWarning { get; private set; }
        public string Summary { get; private set; } = "";

        // 2026-10-20 12:34:56.789 pid=1 tid=2 level=WARN code=700 error=0 message
        public static DiagnosticEntry? TryParse(string line, string process)
        {
            var levelIndex = line.IndexOf(" level=", StringComparison.Ordinal);
            if (line.Length < 23 || levelIndex < 0)
                return null;
            var isWarning = line.IndexOf(" level=WARN ", StringComparison.Ordinal) >= 0;
            var isError = line.IndexOf(" level=ERROR ", StringComparison.Ordinal) >= 0;
            if (!isWarning && !isError)
                return null;
            if (!DateTime.TryParseExact(
                    line.Substring(0, 23),
                    "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var timestamp))
                return null;
            var code = 0;
            var codeIndex = line.IndexOf(" code=", StringComparison.Ordinal);
            if (codeIndex >= 0)
            {
                var end = line.IndexOf(' ', codeIndex + 6);
                int.TryParse(
                    end > 0
                        ? line.Substring(codeIndex + 6, end - codeIndex - 6)
                        : line.Substring(codeIndex + 6),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out code);
            }
            var errorIndex = line.IndexOf(" error=", StringComparison.Ordinal);
            var message = "";
            if (errorIndex >= 0)
            {
                var space = line.IndexOf(' ', errorIndex + 7);
                message = space > 0 ? line.Substring(space + 1).Trim() : "";
            }
            return new DiagnosticEntry
            {
                Timestamp = timestamp,
                Process = process,
                Code = code,
                IsWarning = isWarning,
                Summary = Describe(code, message)
            };
        }

        private static string Describe(int code, string message)
        {
            switch (code)
            {
                case 500:
                    return "程序启用了严格的安全策略（只允许签名模块或禁止动态代码），已跳过。这类进程无法处理。";
                case 600:
                    return "加载器无法把 YMacType 注入这个程序：" + message;
                case 700:
                    return "替换字体没有安装，这条替换规则被跳过：" + message;
                case 701:
                    return "没有任何字体替换规则生效，请检查字体链。";
                case 103:
                    return "YMacType 在这个程序里初始化失败：" + message;
                case 200:
                case 301:
                    return "文字接口挂接失败，这个程序的部分文字不会被处理：" + message;
                case 402:
                    return "子进程注入失败：" + message;
                default:
                    return message.Length > 0 ? message : $"诊断代码 {code}";
            }
        }
    }

    private sealed class EffectiveProcessInfo
    {
        public string Name { get; set; } = "";
        public int ProcessId { get; set; }
        public string Architecture { get; set; } = "";
        public string Path { get; set; } = "";
    }

    private enum ApplicationRuleMode
    {
        Blocked,
        RenderOnly
    }

    private sealed class ApplicationRuleInfo
    {
        public string ExecutableName { get; set; } = "";
        public ApplicationRuleMode Mode { get; set; }
        public string ModeLabel => Mode == ApplicationRuleMode.Blocked
            ? "完全不处理"
            : "只渲染，不替换字体";
    }
}
}
