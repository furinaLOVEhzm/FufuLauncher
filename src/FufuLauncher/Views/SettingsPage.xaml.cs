// SettingsPage.xaml.cs — 设置(全新重写)
// 可爱的芙芙
//
// 纯代码后置,直接读写 ConfigService(不再依赖 ViewModel)。
// 分区:外观主题 / 背景(单层图片视频,芙宁娜壁纸兜底) / 内存智能分配(三档推荐+1.5GB预留+预提交) /
//       JVM 多核优化(ZGC/G1 按 Java 版本) / 高级模式自定义 JVM 参数(语法校验+按版本独立保存) /
//       镜像源切换 / 应用日志自动清理(按启动次数触发,保留最新 N 份)。
// 生命周期契约(MainWindow):构造时恢复内存监控,离开页面 Dispose 时暂停。

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FufuLauncher.Interaction;
using FufuLauncher.Services;
using Microsoft.Win32;

namespace FufuLauncher.Views;

/// <summary>高级模式目标版本下拉展示项</summary>
public class InstArgsItem
{
    public GameInstance Inst { get; set; } = null!;
    public string Display { get; set; } = "";
    public override string ToString() => Display;
}

public partial class SettingsPage : Page, IDisposable
{
    private readonly ConfigService _configService;
    private readonly ThemeService _themeService;
    private readonly MemoryMonitorService _memoryMonitor;
    private readonly VersionManifestService _versionManifest;
    private readonly JavaRuntimeService _javaRuntime;
    private readonly LogCleanupService _logCleanup;
    private readonly InstanceService _instanceService;

    private bool _initialized;
    private bool _suppressInstEvents;
    private bool _suppressMemEvents;   // 回退内存上限选中项时避免递归触发校验
    private bool _lowMemoryWarned;   // 内存紧张弹窗每次进入页面最多提示一次

    // Java 镜像下拉:UI 显示名 → config 值
    private static readonly (string Label, string Value)[] JavaMirrors =
    {
        ("华为云镜像(国内推荐)", "Huaweicloud"),
        ("BMCLAPI 镜像", "BMCLAPI"),
        ("官方源 (Adoptium)", "Official")
    };

    public SettingsPage(ConfigService configService, ThemeService themeService,
                        MemoryMonitorService memoryMonitor, VersionManifestService versionManifest,
                        JavaRuntimeService javaRuntime, LogCleanupService logCleanup,
                        InstanceService instanceService)
    {
        _configService = configService;
        _themeService = themeService;
        _memoryMonitor = memoryMonitor;
        _versionManifest = versionManifest;
        _javaRuntime = javaRuntime;
        _logCleanup = logCleanup;
        _instanceService = instanceService;
        InitializeComponent();
        LoadSettings();
        _initialized = true;

        // 内存快照实时更新(设置页可见时才刷新)
        _memoryMonitor.Updated += OnMemoryUpdated;
        _memoryMonitor.Resume();
    }

    /// <summary>页面被导航离开时由 MainWindow 调用:退订事件 + 暂停内存监控</summary>
    public void Dispose()
    {
        _memoryMonitor.Updated -= OnMemoryUpdated;
        _memoryMonitor.Pause();
    }

    private void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        // 进入页面立即刷一次内存快照
        OnMemoryUpdated(_memoryMonitor.GetCurrent());
        CheckLowMemoryWarning();
    }

    /// <summary>内存紧张警告:当前实时可用内存 ≤ 系统预留线(1.5GB)时弹窗提示,并已自动下调游戏内存上限</summary>
    private void CheckLowMemoryWarning()
    {
        try
        {
            if (_lowMemoryWarned) return;
            if (!_memoryMonitor.IsMemoryTight()) return;
            _lowMemoryWarned = true;
            var info = _memoryMonitor.GetCurrent();
            int safeMb = _memoryMonitor.GetSafeAllocMb();
            App.WriteAppLog($"[设置] 内存紧张警告:可用 {info.AvailableGb:F2} GB ≤ 预留 {_memoryMonitor.ReserveMb() / 1024.0:0.#} GB,已自动下调内存上限");
            FufuMessage.Warn(Window.GetWindow(this), "系统内存资源紧张",
                $"检测到当前系统可用空闲内存仅 {info.AvailableGb:F2} GB(低于 {_memoryMonitor.ReserveMb() / 1024.0:0.#} GB 系统预留线)。\n\n" +
                $"已自动下调游戏内存分配上限,当前可安全分配的最大内存约 {Math.Max(0, safeMb)} MB。\n" +
                "为保证系统与游戏稳定,暂不允许分配大内存,建议关闭其他占用内存的程序后再启动游戏。");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[设置] 内存紧张检测异常:{ex.Message}");
        }
    }

    // ==================== 加载配置到控件 ====================

    private void LoadSettings()
    {
        var cfg = _configService.Config;

        // 主题
        if (cfg.Theme == "Dark") RbThemeDark.IsChecked = true;
        else RbThemeLight.IsChecked = true;

        // 背景
        switch (cfg.BackgroundType)
        {
            case "Image": BgImage.IsChecked = true; break;
            case "Video": BgVideo.IsChecked = true; break;
            default: BgNone.IsChecked = true; break;
        }
        VideoOptionsPanel.Visibility = cfg.BackgroundType == "Video" ? Visibility.Visible : Visibility.Collapsed;
        SldOpacity.Value = cfg.BackgroundOpacity;
        TxtOpacity.Text = $"{cfg.BackgroundOpacity:P0}";
        ChkVideoMuted.IsChecked = cfg.VideoMuted;
        SldVideoSpeed.Value = cfg.VideoSpeed;
        TxtVideoSpeed.Text = $"{cfg.VideoSpeed:F2}x";
        TxtBgFile.Text = string.IsNullOrEmpty(cfg.BackgroundPath)
            ? "未选择自定义背景文件"
            : $"当前背景:{cfg.BackgroundPath}";

        // 内存:智能分配开关 + 三档推荐 + 上限 + 预提交
        ChkAutoMemory.IsChecked = cfg.AutoMemoryMode;
        switch (cfg.MemoryTier)
        {
            case "Vanilla": RbTierVanilla.IsChecked = true; break;
            case "Shader": RbTierShader.IsChecked = true; break;
            default: RbTierModded.IsChecked = true; break;
        }
        SelectComboText(CbMaxLimit, $"{cfg.AutoMemoryMaxMb / 1024} GB");
        ChkPreCommit.IsChecked = cfg.MemoryPreCommit;
        TxtDirectMem.Text = cfg.MaxDirectMemoryMb.ToString();
        RefreshDirectHint();
        CbXms.Text = (cfg.Xms / 1024.0).ToString("0.#");
        CbXmx.Text = (cfg.Xmx / 1024.0).ToString("0.#");
        ManualMemPanel.Visibility = cfg.AutoMemoryMode ? Visibility.Collapsed : Visibility.Visible;
        RefreshSmartHint();

        // JVM 多核优化
        int cores = MemoryMonitorService.GetPhysicalCoreCount();
        int javaMajor = DetectJavaMajor();
        string gcName = javaMajor >= 17 ? "ZGC" : "G1GC";
        TxtCoreInfo.Text = $"检测到 {cores} 个逻辑处理器;启用后启动游戏时自动注入优化参数(当前 Java {javaMajor} → {gcName})。";
        TxtGcPreview.Text = MemoryMonitorService.BuildMultiCoreGcArgs(cores, javaMajor);
        ChkGcOpt.IsChecked = cfg.MultiCoreGcOptimize;
        ChkHighPriority.IsChecked = cfg.HighPriorityProcess;
        ChkCpuAffinity.IsChecked = cfg.CpuAffinityEnabled;
        LoadTargetInstances();

        // 镜像源
        if (cfg.DownloadSource == "Mojang") SrcMojang.IsChecked = true;
        else SrcBmcl.IsChecked = true;
        CbJavaMirror.ItemsSource = JavaMirrors;
        CbJavaMirror.DisplayMemberPath = "Label";
        int idx = Array.FindIndex(JavaMirrors, m => m.Value == cfg.JavaDownloadMirror);
        CbJavaMirror.SelectedIndex = idx >= 0 ? idx : 0;

        // 日志清理(按启动次数触发,保留最新 N 份)
        ChkLogClean.IsChecked = cfg.LogAutoCleanEnabled;
        LogCleanOptions.IsEnabled = cfg.LogAutoCleanEnabled;
        SelectComboText(CbKeepCount, cfg.LogKeepCount.ToString());
        SelectComboText(CbCleanEvery, cfg.LogCleanEveryNLaunches.ToString());
        RefreshLogDirInfo();
    }

    private static void SelectComboText(ComboBox cb, string text)
    {
        foreach (ComboBoxItem item in cb.Items)
        {
            if (item.Content?.ToString() == text)
            {
                cb.SelectedItem = item;
                return;
            }
        }
        cb.Text = text;
    }

    private void RefreshSmartHint()
    {
        try
        {
            var cfg = _configService.Config;
            string tier = cfg.MemoryTier;
            int baseMb = MemoryMonitorService.TierBaseMb(tier);
            int smartXmx = _memoryMonitor.CalculateSmartXmx();
            int safeMb = _memoryMonitor.GetSafeAllocMb();
            string tierName = MemoryMonitorService.TierDisplayName(tier);
            string tight = _memoryMonitor.IsMemoryTight()
                ? " ⚠ 当前可用内存紧张,已自动下调分配上限。"
                : "";
            TxtSmartHint.Text = cfg.AutoMemoryMode
                ? $"✓ 智能模式已启用:{tierName}场景预设 {baseMb / 1024.0:F0} GB,硬性约束 = min(预设档位, 当前可用 − {_memoryMonitor.ReserveMb() / 1024.0:0.#}GB 预留)," +
                  $"本次实际分配 Xmx ≈ {smartXmx / 1024.0:F1} GB(固定堆 Xms=Xmx);当前可安全分配上限 ≈ {safeMb} MB,智能上限 {cfg.AutoMemoryMaxMb / 1024} GB。{tight}"
                : $"智能推荐:{tierName}场景预设 {baseMb / 1024.0:F0} GB,按当前实时可用内存计算约 {smartXmx / 1024.0:F1} GB。当前为手动模式,使用下方 Xms/Xmx 设置(保存时同样校验实时可用内存)。";
        }
        catch { /* 快照失败不影响页面 */ }
    }

    private void RefreshLogDirInfo()
    {
        try
        {
            long total = 0;
            if (Directory.Exists(AppPaths.Logs))
            {
                foreach (var f in new DirectoryInfo(AppPaths.Logs).EnumerateFiles("*", SearchOption.AllDirectories))
                    total += f.Length;
            }
            TxtLogDir.Text = $"日志目录:{AppPaths.Logs}    当前占用:{total / 1024.0 / 1024.0:F2} MB";
        }
        catch
        {
            TxtLogDir.Text = $"日志目录:{AppPaths.Logs}";
        }
    }

    /// <summary>从全局 Java 路径解析主版本(runtimes 目录名约定 jdk-{major}-{arch}),失败回退 config.JavaVersion</summary>
    private int DetectJavaMajor()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_configService.Config.JavaPath ?? "");
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("jdk-", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = name[4..];
                    int dash = rest.IndexOf('-');
                    if (dash > 0 && int.TryParse(rest[..dash], out int v) && v > 0) return v;
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* 忽略解析异常 */ }
        int fallback = _configService.Config.JavaVersion;
        return fallback > 0 ? fallback : 17;
    }

    private void OnMemoryUpdated(MemoryInfo info)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (PbMemory == null) return;
            PbMemory.Value = info.LoadPercent;
            // 两条关键信息:① 整机总内存 ② 当前系统可用空闲内存(实时)
            TxtMemInfo.Text =
                $"① 整机总内存:{info.TotalGb:F1} GB(已用 {info.UsedGb:F1} GB · 负载 {info.LoadPercent}%)\n" +
                $"② 当前系统可用空闲内存:{info.AvailableGb:F1} GB(为系统强制保留 {_memoryMonitor.ReserveMb() / 1024} GB,当前可安全分配 ≈ {_memoryMonitor.GetSafeAllocMb()} MB)";
        });
    }

    // ==================== 1. 主题 ====================

    private void RbThemeLight_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (_configService.Config.Theme == "Light") return;
        _themeService.SetTheme("Light");
        App.WriteAppLog("[设置] 已切换到浅色主题");
    }

    private void RbThemeDark_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (_configService.Config.Theme == "Dark") return;
        _themeService.SetTheme("Dark");
        App.WriteAppLog("[设置] 已切换到深色主题");
    }

    // ==================== 2. 背景 ====================
    
    // 选择背景文件期间挂起 RadioButton 的 Checked 副作用,避免旧背景被重复应用造成闪烁/竞态
    private bool _browsingBg;
    
    private void BtnBrowseBg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "所有支持格式|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.mkv;*.avi;*.wmv;*.mov|图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|视频|*.mp4;*.mkv;*.avi;*.wmv;*.mov|所有文件|*.*"
        };
        if (dlg.ShowDialog() != true) return;
    
        string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        bool isVideo = ext is ".mp4" or ".mkv" or ".avi" or ".wmv" or ".mov";
    
        _browsingBg = true;
        try
        {
            if (isVideo) BgVideo.IsChecked = true;
            else BgImage.IsChecked = true;
            // 素材会被复制到规范 tupian 目录,Config 存的是实际生效路径
            _themeService.SetBackground(dlg.FileName);
        }
        finally { _browsingBg = false; }
    
        // 显示实际生效的路径(而非用户选择的原始路径,避免误导)
        string stored = _configService.Config.BackgroundPath;
        TxtBgFile.Text = string.IsNullOrEmpty(stored) ? $"当前背景:{dlg.FileName}" : $"当前背景:{stored}";
        App.WriteAppLog($"[设置] 背景已切换:{dlg.FileName} → 生效:{stored}");
    }
    
    private void BgNone_Checked(object sender, RoutedEventArgs e)
    {
        if (VideoOptionsPanel != null) VideoOptionsPanel.Visibility = Visibility.Collapsed;
        if (!_initialized || _browsingBg) return;
        // 无背景模式:ThemeService 内部保留芙宁娜标志性壁纸兜底
        _themeService.ClearBackground();
    }
    
    private void BgImage_Checked(object sender, RoutedEventArgs e)
    {
        if (VideoOptionsPanel != null) VideoOptionsPanel.Visibility = Visibility.Collapsed;
        if (!_initialized || _browsingBg) return;
        var cfg = _configService.Config;
        if (!string.IsNullOrEmpty(cfg.BackgroundPath) && cfg.BackgroundType == "Image")
            _themeService.SetBackground(cfg.BackgroundPath);
    }
    
    private void BgVideo_Checked(object sender, RoutedEventArgs e)
    {
        if (VideoOptionsPanel != null) VideoOptionsPanel.Visibility = Visibility.Visible;
        if (!_initialized || _browsingBg) return;
        var cfg = _configService.Config;
        if (!string.IsNullOrEmpty(cfg.BackgroundPath) && cfg.BackgroundType == "Video")
            _themeService.SetBackground(cfg.BackgroundPath);
    }

    private void SldOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtOpacity != null) TxtOpacity.Text = $"{e.NewValue:P0}";
        if (!_initialized) return;
        _themeService.SetOpacity(e.NewValue);
    }

    private void ChkVideoMuted_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _themeService.SetVideoMuted(ChkVideoMuted.IsChecked == true);
    }

    private void SldVideoSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtVideoSpeed != null) TxtVideoSpeed.Text = $"{e.NewValue:F2}x";
        if (!_initialized) return;
        _themeService.SetVideoSpeed(e.NewValue);
    }

    // ==================== 3. 内存智能分配 ====================

    private void ChkAutoMemory_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _configService.Config.AutoMemoryMode = ChkAutoMemory.IsChecked == true;
        ManualMemPanel.Visibility = _configService.Config.AutoMemoryMode
            ? Visibility.Collapsed : Visibility.Visible;
        _configService.Save();
        RefreshSmartHint();
        App.WriteAppLog($"[设置] 智能内存分配:{(_configService.Config.AutoMemoryMode ? "启用" : "关闭")}");
    }

    /// <summary>使用场景档位切换:纯净 / 中型模组 / 大型光影</summary>
    private void Tier_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        string tier =
            RbTierVanilla.IsChecked == true ? "Vanilla" :
            RbTierShader.IsChecked == true ? "Shader" : "Modded";
        if (_configService.Config.MemoryTier == tier) return;
        _configService.Config.MemoryTier = tier;
        _configService.Save();
        RefreshSmartHint();
        App.WriteAppLog($"[设置] 内存档位已切换:{MemoryMonitorService.TierDisplayName(tier)} ({tier})");
    }

    /// <summary>智能分配内存上限(防止给 JVM 分配过多内存);同样校验实时可用内存,超安全值拦截</summary>
    private void CbMaxLimit_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _suppressMemEvents) return;
        if (CbMaxLimit.SelectedItem is not ComboBoxItem item) return;
        string text = item.Content?.ToString()?.Replace("GB", "").Trim() ?? "";
        if (!int.TryParse(text, out int gb) || gb <= 0) return;
        int mb = gb * 1024;
        if (_configService.Config.AutoMemoryMaxMb == mb) return;

        // 硬性校验:上限不得超过当前实时可用内存 − 系统预留,超过直接拦截并回退选中项
        int safeMb = _memoryMonitor.GetSafeAllocMb();
        if (mb > safeMb)
        {
            var info = _memoryMonitor.GetCurrent();
            _suppressMemEvents = true;
            try { SelectComboText(CbMaxLimit, $"{_configService.Config.AutoMemoryMaxMb / 1024} GB"); }
            finally { _suppressMemEvents = false; }
            FufuMessage.Warn(Window.GetWindow(this), "超出安全内存上限",
                $"当前系统实时可用空闲内存仅 {info.AvailableGb:F2} GB,扣除 {_memoryMonitor.ReserveMb() / 1024.0:0.#} GB 系统预留后,\n" +
                $"可安全分配的内存上限为 {safeMb} MB(≈{safeMb / 1024.0:F1} GB)。\n\n" +
                $"所选上限 {gb} GB 已超过安全值,不允许保存,已自动恢复原选项。");
            return;
        }

        _configService.Config.AutoMemoryMaxMb = mb;
        _configService.Save();
        RefreshSmartHint();
        App.WriteAppLog($"[设置] 智能内存上限已设置:{mb} MB");
    }

    /// <summary>内存预提交(AlwaysPreTouch):启动时一次性提交全部堆页</summary>
    private void ChkPreCommit_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _configService.Config.MemoryPreCommit = ChkPreCommit.IsChecked == true;
        _configService.Save();
        App.WriteAppLog($"[设置] 内存预提交(AlwaysPreTouch):{(_configService.Config.MemoryPreCommit ? "启用" : "关闭")}");
    }

    /// <summary>堆外直接内存上限提示:0 = 自动取 Xmx 的 0.75 倍</summary>
    private void RefreshDirectHint()
    {
        try
        {
            int v = _configService.Config.MaxDirectMemoryMb;
            int smartXmx = _memoryMonitor.CalculateSmartXmx();
            TxtDirectHint.Text = v > 0
                ? $"手动锁定 {v} MB(下次启动生效)"
                : $"自动:按当前 Xmx 的 0.75 倍 ≈ {Math.Max(128, (int)(smartXmx * 0.75))} MB";
        }
        catch { /* 提示失败不影响页面 */ }
    }

    /// <summary>保存堆外直接内存上限(0 = 自动,手动范围 64~65536MB)</summary>
    private void TxtDirectMem_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        var cfg = _configService.Config;
        string text = TxtDirectMem.Text.Trim();
        if (!int.TryParse(text, out int v) || v < 0 || v > 65536)
        {
            TxtDirectMem.Text = cfg.MaxDirectMemoryMb.ToString();
            FufuMessage.Warn(Window.GetWindow(this), "输入无效",
                "堆外直接内存上限需为 0~65536 之间的整数 MB。\n0 = 自动取 Xmx 的 0.75 倍,已恢复原值。");
            return;
        }
        if (cfg.MaxDirectMemoryMb == v) { RefreshDirectHint(); return; }
        cfg.MaxDirectMemoryMb = v;
        _configService.Save();
        RefreshDirectHint();
        App.WriteAppLog($"[设置] 堆外直接内存上限 MaxDirectMemorySize:{(v > 0 ? $"{v}MB(手动)" : "自动(Xmx×0.75)")}");
    }

    private void BtnSaveMemory_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseGbToMb(CbXms.Text, out int xms))
        {
            FufuMessage.Warn(Window.GetWindow(this), "输入无效", "Xms 必须是大于 0 且不超过 512 GB 的数值。");
            return;
        }
        if (!TryParseGbToMb(CbXmx.Text, out int xmx) || xmx < xms)
        {
            FufuMessage.Warn(Window.GetWindow(this), "输入无效", "Xmx 必须是 ≥ Xms 且不超过 512 GB 的数值。");
            return;
        }

        // 硬性校验:Xmx 不得超过当前实时可用内存 − 系统预留,超过直接拦截不允许保存
        int safeMb = _memoryMonitor.GetSafeAllocMb();
        if (xmx > safeMb)
        {
            var info = _memoryMonitor.GetCurrent();
            App.WriteAppLog($"[设置] 手动内存被拦截:Xmx={xmx}MB 超过安全值 {safeMb}MB(可用 {info.AvailableGb:F2}GB − {_memoryMonitor.ReserveMb() / 1024.0:0.#}GB 预留)");
            FufuMessage.Warn(Window.GetWindow(this), "超出安全内存上限",
                $"当前系统实时可用空闲内存仅 {info.AvailableGb:F2} GB,扣除 {_memoryMonitor.ReserveMb() / 1024.0:0.#} GB 系统预留后,\n" +
                $"可安全分配的最大内存为 {safeMb} MB(≈{safeMb / 1024.0:F1} GB)。\n\n" +
                $"您输入的 Xmx = {xmx} MB 已超过安全值,为避免系统卡死不允许保存。\n建议关闭其他占用内存的程序,或调低 Xmx 后再试。");
            return;
        }

        _configService.Config.Xms = xms;
        _configService.Config.Xmx = xmx;
        _configService.Save();
        App.WriteAppLog($"[设置] 内存已保存:Xms={xms}MB Xmx={xmx}MB(安全上限 {safeMb}MB)");
        FufuMessage.Success(Window.GetWindow(this), "已保存", $"内存设置已保存:Xms {xms} MB / Xmx {xmx} MB,下次启动游戏生效。");
    }

    private static bool TryParseGbToMb(string text, out int mb)
    {
        mb = 0;
        if (!double.TryParse(text?.Trim(), out double gb)) return false;
        if (double.IsNaN(gb) || double.IsInfinity(gb) || gb <= 0 || gb > 512) return false;
        mb = (int)(gb * 1024);
        return true;
    }

    // ==================== 4. JVM 多核优化 + 高级自定义参数 ====================

    private void JvmOpt_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _configService.Config.MultiCoreGcOptimize = ChkGcOpt.IsChecked == true;
        _configService.Config.HighPriorityProcess = ChkHighPriority.IsChecked == true;
        _configService.Config.CpuAffinityEnabled = ChkCpuAffinity.IsChecked == true;
        _configService.Save();
    }

    /// <summary>填充高级模式目标版本下拉(参数按版本独立保存到 inst.ExtraJvmArgs)</summary>
    private void LoadTargetInstances()
    {
        _suppressInstEvents = true;
        try
        {
            _instanceService.LoadInstances();
            var items = _instanceService.Instances.Select(i => new InstArgsItem
            {
                Inst = i,
                Display = $"{i.Name}  ·  MC {i.VersionId}" +
                          (string.IsNullOrEmpty(i.ModLoader) ? "" : $" · {i.ModLoader}")
            }).ToList();
            CbTargetInst.ItemsSource = items;
            if (items.Count > 0)
            {
                var last = items.FirstOrDefault(x => x.Inst.Id == _configService.Config.LastInstanceId);
                CbTargetInst.SelectedItem = last ?? items[0];
                TxtInstArgs.Text = (last ?? items[0]).Inst.ExtraJvmArgs ?? "";
            }
            else
            {
                TxtInstArgs.Text = "";
                TxtArgsStatus.Text = "暂无已安装的游戏版本,请先到下载中心安装。";
            }
        }
        finally { _suppressInstEvents = false; }
    }

    private InstArgsItem? CurrentTargetInst => CbTargetInst.SelectedItem as InstArgsItem;

    private void CbTargetInst_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _suppressInstEvents) return;
        var item = CurrentTargetInst;
        if (item == null) return;
        TxtInstArgs.Text = item.Inst.ExtraJvmArgs ?? "";
        TxtArgsStatus.Text = $"已加载「{item.Inst.Name}」的自定义参数。";
    }

    private void BtnValidateArgs_Click(object sender, RoutedEventArgs e)
    {
        var (ok, error) = JvmArgsValidator.Validate(TxtInstArgs.Text);
        TxtArgsStatus.Text = ok ? "✓ 语法校验通过" : $"✗ {error}";
        if (!ok) App.WriteAppLog($"[设置] JVM 参数校验失败:{error}");
    }

    private void BtnSaveArgs_Click(object sender, RoutedEventArgs e)
    {
        var item = CurrentTargetInst;
        if (item == null)
        {
            FufuMessage.Warn(Window.GetWindow(this), "提示", "请先选择目标版本(需要先在下载中心安装游戏版本)。");
            return;
        }

        var (ok, error) = JvmArgsValidator.Validate(TxtInstArgs.Text);
        if (!ok)
        {
            TxtArgsStatus.Text = $"✗ {error}";
            FufuMessage.Warn(Window.GetWindow(this), "语法校验未通过", $"{error}\n\n请修正后再保存。");
            return;
        }

        item.Inst.ExtraJvmArgs = TxtInstArgs.Text.Trim();
        _instanceService.SaveInstance(item.Inst);
        TxtArgsStatus.Text = $"✓ 已保存到「{item.Inst.Name}」";
        App.WriteAppLog($"[设置] 自定义 JVM 参数已保存:{item.Inst.Id} → {item.Inst.ExtraJvmArgs}");
        FufuMessage.Success(Window.GetWindow(this), "已保存",
            $"自定义 JVM 参数已保存到版本「{item.Inst.Name}」(按版本独立保存),下次启动该版本生效。");
    }

    // ==================== 5. 镜像源 ====================

    private void SrcBmcl_Checked(object sender, RoutedEventArgs e) => SwitchGameSource("BMCLAPI");

    private void SrcMojang_Checked(object sender, RoutedEventArgs e) => SwitchGameSource("Mojang");

    private void SwitchGameSource(string source)
    {
        if (!_initialized) return;
        if (_configService.Config.DownloadSource == source) return;
        _configService.Config.DownloadSource = source;
        _configService.Save();
        _versionManifest.ClearCache();   // 清空版本清单缓存,下次从新源拉取
        App.WriteAppLog($"[设置] 游戏资源源已切换:{source}(清单缓存已清空)");
    }

    private void CbJavaMirror_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (CbJavaMirror.SelectedItem is not (string Label, string Value)) return;
        if (_configService.Config.JavaDownloadMirror == Value) return;
        _configService.Config.JavaDownloadMirror = Value;
        _configService.Save();
        App.WriteAppLog($"[设置] Java 下载源已切换:{Label} ({_javaRuntime.GetCurrentMirrorLabel()})");
    }

    // ==================== 6. 日志自动清理(按启动次数触发,保留最新 N 份) ====================

    private void ChkLogClean_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _configService.Config.LogAutoCleanEnabled = ChkLogClean.IsChecked == true;
        LogCleanOptions.IsEnabled = _configService.Config.LogAutoCleanEnabled;
        _configService.Save();
        App.WriteAppLog($"[设置] 日志自动清理:{(_configService.Config.LogAutoCleanEnabled ? "启用" : "关闭")}");
    }

    private void LogParam_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        var cfg = _configService.Config;
        if (CbKeepCount.SelectedItem is ComboBoxItem keepItem
            && int.TryParse(keepItem.Content?.ToString(), out int keep) && keep >= 1)
        {
            cfg.LogKeepCount = keep;
        }
        if (CbCleanEvery.SelectedItem is ComboBoxItem everyItem
            && int.TryParse(everyItem.Content?.ToString(), out int every) && every >= 1)
        {
            cfg.LogCleanEveryNLaunches = every;
        }
        _configService.Save();
    }

    private async void BtnCleanNow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            btn.IsEnabled = false;
            btn.Content = "清理中…";
        }
        try
        {
            await _logCleanup.RunCleanupAsync();   // 后台线程执行,不阻塞 UI
            RefreshLogDirInfo();
            FufuMessage.Success(Window.GetWindow(this), "清理完成",
                $"已保留最新 {_configService.Config.LogKeepCount} 份历史日志,更早的归档日志已清理(当前活跃日志不受影响)。");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[设置] 手动清理日志失败:{ex.Message}");
            FufuMessage.Error(Window.GetWindow(this), "清理失败", "清理日志时出错:\n" + ex.Message);
        }
        finally
        {
            if (sender is Button btn2)
            {
                btn2.IsEnabled = true;
                btn2.Content = "🧹 立即清理";
            }
        }
    }
}
