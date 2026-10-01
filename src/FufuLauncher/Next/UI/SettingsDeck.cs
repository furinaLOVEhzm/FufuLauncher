// FufuLauncher - 设置 Deck(七节:游戏设置 / Java 运行时 / 路径设置 / 网络设置 / 外观设置 / 下载设置 / 高级设置)
// Copyright © FufuLauncher
//
// 游戏设置:智能/手动内存、堆外内存硬锁、分辨率、JVM 参数、高级优化开关(ConfigService 落盘);
// Java 运行时:已装运行时列表、JDK 在线下载(华为云/官方镜像)、系统 Java 扫描;
// 路径设置:数据目录布局展示与快捷入口(固定布局,不写 C 盘用户目录);
// 网络设置:游戏下载源(BMCLAPI/Mojang)、HTTP 代理;
// 外观设置:预设+自定义主题卡片一键切换(免重启)、HSL 主色、模糊/亮度/卡片透明度、自定义背景、主题导入导出;
// 下载设置:并发线程数、SHA1 校验开关、全局限速;
// 高级设置:自更新检测与应用、环境自检、分级日志、日志清理、目录入口。

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FufuLauncher.Services;
using FufuLauncher.Theme;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class SettingsDeck : UserControl
{
    private readonly ConfigService _config;
    private readonly JvmTemplateService _jvmTemplates;
    private readonly JavaRuntimeService _javaRuntimes;
    private readonly JavaScanService _javaScan;
    private readonly EnvironmentCheckService _envCheck;
    private readonly UpdateService _updater;
    private readonly DownloadService _downloads;
    private readonly MemoryRecommendService _memRec;

    private readonly StackPanel _gameSection = new();
    private readonly StackPanel _javaSection = new();
    private readonly StackPanel _pathsSection = new();
    private readonly StackPanel _networkSection = new();
    private readonly StackPanel _themeSection = new();
    private readonly StackPanel _downloadSection = new();
    private readonly StackPanel _advancedSection = new();
    private readonly StackPanel _aiSection = new();
    private readonly StackPanel[] _sections;
    private readonly WrapPanel _themeCards = new();

    // 外观
    private readonly Slider _opacitySlider = UIKit.Slider();
    private readonly TextBlock _opacityValue = UIKit.Sub("100%", 12);
    private readonly Slider _blurSlider = UIKit.Slider();
    private readonly TextBlock _blurValue = UIKit.Sub("", 12);
    private readonly Slider _cardOpacitySlider = UIKit.Slider();
    private readonly TextBlock _cardOpacityValue = UIKit.Sub("", 12);
    private readonly Slider _brightnessSlider = UIKit.Slider();
    private readonly TextBlock _brightnessValue = UIKit.Sub("", 12);
    private readonly ComboBox _bgModeBox = UIKit.ComboBox();

    // 游戏设置
    private readonly UIKit.ToggleSwitch _autoMemSw = new("智能内存(启动时按可用内存实时计算,推荐)");
    private readonly Button _memApplyBtn = UIKit.Button("填入手动值", primary: false, height: 32);
    /// <summary>智能内存「推荐值 + 计算依据」说明块:把推荐 Xmx 怎么来的讲清楚
    /// (档位 / 预留红线 / 实时可用天花板各自取多少、谁在起作用)</summary>
    private readonly TextBlock _memExplain = UIKit.Sub("", 11.5);
    private readonly Slider _xmsSlider = UIKit.Slider();
    private readonly TextBlock _xmsValue = UIKit.Sub("", 12);
    private readonly Slider _xmxSlider = UIKit.Slider();
    private readonly TextBlock _xmxValue = UIKit.Sub("", 12);
    private readonly Slider _directMemSlider = UIKit.Slider();
    private readonly TextBlock _directMemValue = UIKit.Sub("", 12);
    private readonly TextBox _widthBox = UIKit.TextBox();
    private readonly TextBox _heightBox = UIKit.TextBox();
    private readonly UIKit.ToggleSwitch _fullscreenChk = new("全屏启动");
    private readonly TextBox _jvmArgs = UIKit.TextBox();
    private readonly ComboBox _jvmTplBox = UIKit.ComboBox();
    private readonly TextBlock _jvmTplDesc = UIKit.Sub("", 11.5);
    private readonly UIKit.ToggleSwitch _highPrioritySw = new("高优先级进程");
    private readonly UIKit.ToggleSwitch _affinitySw = new("CPU 亲和性优化");
    private readonly UIKit.ToggleSwitch _multiCoreGcSw = new("多核 GC 优化参数");
    private readonly UIKit.ToggleSwitch _preCommitSw = new("内存预提交(AlwaysPreTouch)");

    // Java 运行时(排版重构:推荐版本单独一列 + 其余版本折叠收纳 + 两列并排铺满页宽)
    private readonly StackPanel _runtimeList = new();
    private readonly StackPanel _recCards = new();        // 左列:推荐 Java 版本(21 / 17 / 8 三档)
    private readonly StackPanel _otherJdkList = new();    // 右列折叠体:推荐之外的其余版本
    private readonly TextBlock _jdkStatus = UIKit.Sub("", 12);
    private readonly StackPanel _sysJavaList = new();
    private readonly ComboBox _mirrorBox = UIKit.ComboBox();
    private readonly TextBlock _mirrorDesc = UIKit.Sub("", 12);
    private Border? _jdkFoldBody;      // 折叠体容器(展开/收起切可见性)
    private TextBlock? _jdkFoldArrow;  // 折叠箭头 ▸ / ▾
    private TextBlock? _jdkFoldCount;  // 折叠头部的版本数量
    private bool _jdkFoldExpanded;     // 折叠态跟刷新保留(下载完成重渲染不把用户展开的列表收回去)
    private List<JdkVersionInfo> _jdkList = new();
    private bool _downloadingJdk;
    private bool _jdkFetching;

    /// <summary>推荐档位(与 JavaRuntimeService.RecommendJavaMajor 的分档完全一致:≥1.20.5→21、1.17~1.20.4→17、≤1.16→8)</summary>
    private static readonly (int Major, string Why)[] RecommendedJdks =
    {
        (21, "适配 1.20.5 及以上 · 新版本主流"),
        (17, "适配 1.17 ~ 1.20.4 · 整合包主流"),
        (8,  "适配 1.16 及以下 · 含 1.12.2 老模组包")
    };

    // 网络设置控件由 BuildNetworkControls 工厂产出(下载源/代理/分类开关/测速统一口径)

    // 下载设置
    private readonly Slider _threadsSlider = UIKit.Slider();
    private readonly TextBlock _threadsValue = UIKit.Sub("", 12);
    private readonly Slider _shardSlider = UIKit.Slider();
    private readonly TextBlock _shardValue = UIKit.Sub("", 12);
    private readonly UIKit.ToggleSwitch _verifySw = new("下载完成后自动 SHA1 完整性校验(推荐)");
    private readonly Slider _rateSlider = UIKit.Slider();
    private readonly TextBlock _rateValue = UIKit.Sub("", 12);

    // 高级设置
    private readonly TextBlock _updateText = UIKit.Sub("", 12);
    private readonly StackPanel _envResultList = new();
    private readonly UIKit.ToggleSwitch _verboseSw = new("记录 Debug 级细节,排查问题时用");
    private readonly UIKit.ToggleSwitch _logCleanSw = new("自动清理旧日志");
    private readonly TextBox _logKeepBox = UIKit.TextBox();

    // 泡芙助理(AI)
    private readonly UIKit.ToggleSwitch _aiGpuSw = new("GPU 加速(CUDA 优先,失败自动回退 CPU;显存全量利用)");
    private readonly TextBlock _aiStatus = UIKit.Sub("", 12);

    public SettingsDeck()
    {
        _config = App.Services.GetRequiredService<ConfigService>();
        _javaRuntimes = App.Services.GetRequiredService<JavaRuntimeService>();
        _javaScan = App.Services.GetRequiredService<JavaScanService>();
        _envCheck = App.Services.GetRequiredService<EnvironmentCheckService>();
        _updater = App.Services.GetRequiredService<UpdateService>();
        _downloads = App.Services.GetRequiredService<DownloadService>();
        _jvmTemplates = App.Services.GetRequiredService<JvmTemplateService>();
        _memRec = App.Services.GetRequiredService<MemoryRecommendService>();

        _sections = new[] { _gameSection, _javaSection, _pathsSection, _networkSection, _themeSection, _downloadSection, _advancedSection, _aiSection };

        BuildGameSection();
        BuildJavaSection();
        BuildPathsSection();
        BuildNetworkSection();
        BuildThemeSection();
        BuildDownloadSection();
        BuildAdvancedSection();
        BuildAiSection();

        var host = new Grid();
        foreach (var s in _sections) host.Children.Add(s);
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = host
        };
        ShowSection(0);

        _downloads.OverallProgressChanged += OnJdkProgress;
        Unloaded += (_, _) => _downloads.OverallProgressChanged -= OnJdkProgress;
    }

    /// <summary>二级菜单入口:0=游戏设置 1=Java运行时 2=路径设置 3=网络设置 4=外观设置 5=下载设置 6=高级设置 7=泡芙助理(叠放交叉过渡);
    /// 单版本维度的「版本设置」已移至版本管理页内分区(与模组管理同款),不再挂在全局设置下</summary>
    public void ShowSection(int idx)
    {
        UIElement[] secs = _sections;   // 数组协变:FrameworkElement[] → UIElement[]
        MotionKit.SwapOverlay(secs, idx);
        if (idx == 0) UpdateMemUi();   // 内存推荐值随实时可用内存变化,进页时重算
        if (idx == 1) { RefreshRuntimes(); FetchJdkList(); }
        if (idx == 4) RefreshThemeCards();
        if (idx == 7) RefreshAiStatus();
    }

    // ==================== 泡芙助理(AI 设置) ====================

    private void BuildAiSection()
    {
        _aiSection.Children.Add(UIKit.PageHeader("泡芙助理", "内置本地 AI 助理:一句话生成模组整合包;模型离线运行,不联网不上传"));

        _aiGpuSw.IsChecked = _config.Config.AiGpuAcceleration;
        _aiGpuSw.Checked += (_, _) => ApplyAiGpuPref(true);
        _aiGpuSw.Unchecked += (_, _) => ApplyAiGpuPref(false);

        _aiStatus.TextWrapping = TextWrapping.Wrap;
        RefreshAiStatus();

        _aiSection.Children.Add(UIKit.Card(UIKit.V(
            UIKit.SettingRow("GPU 加速", "默认开启:有 ≥4GB 显存的独显时走 CUDA 全层卸载(显存有多少用多少,速度最快);关闭后强制 CPU 推理(老电脑兼容)", _aiGpuSw),
            UIKit.SettingRow("引擎状态", "切换开关后模型会按新设置重新加载,稍等几秒", _aiStatus))));
    }

    private void RefreshAiStatus()
    {
        var engine = App.Services.GetRequiredService<AiEngineService>();
        _aiStatus.Text = engine.State == AiEngineService.EngineState.Ready || engine.State == AiEngineService.EngineState.Loading
            ? engine.StatusDetail
            : engine.State == AiEngineService.EngineState.Unloaded && engine.StatusDetail.StartsWith("已静默")
                ? engine.StatusDetail + ",重开泡芙助理页面时自动重载"
                : "模型尚未加载(打开泡芙助理页面时自动加载)";
    }

    private async void ApplyAiGpuPref(bool enable)
    {
        _config.Config.AiGpuAcceleration = enable;
        _config.Save();
        var engine = App.Services.GetRequiredService<AiEngineService>();
        // 模型尚未加载过:只需记住设置,首次加载自动遵循,无需现在重载
        if (engine.State == AiEngineService.EngineState.Unloaded)
        {
            _aiStatus.Text = $"已{(enable ? "开启" : "关闭")} GPU 加速,下次加载模型时生效";
            return;
        }
        _aiStatus.Text = "正在按新设置重新加载模型,稍等几秒…";
        _aiGpuSw.IsEnabled = false;
        try
        {
            await engine.ReloadAsync();
            _aiStatus.Text = engine.StatusDetail;
        }
        catch (Exception ex)
        {
            // async void 方法异常必须就地捕获,否则逃逸到 Dispatcher 直接崩溃
            _aiStatus.Text = $"按新设置重载模型失败:{ex.Message}";
            App.WriteAppLog($"[AI] GPU 设置重载模型异常:{ex.Message}");
        }
        finally { _aiGpuSw.IsEnabled = true; }
    }

    // ==================== 游戏设置 ====================

    private void BuildGameSection()
    {
        var cfg = _config.Config;
        _gameSection.Children.Add(UIKit.PageHeader("游戏设置", "内存、分辨率与 JVM 参数;智能内存模式下手动值仅作兜底"));

        _autoMemSw.IsChecked = cfg.AutoMemoryMode;
        _autoMemSw.Checked += (_, _) => UpdateMemUi();
        _autoMemSw.Unchecked += (_, _) => UpdateMemUi();

        // 「推荐值 + 依据」说明块 + 一键把推荐写进手动滑块(套用后仍可继续手动微调)
        _memExplain.TextWrapping = TextWrapping.Wrap;
        _memApplyBtn.MinWidth = 116;
        _memApplyBtn.ToolTip = "把当前推荐值写进下面的手动滑块,可再自行微调";
        _memApplyBtn.Click += (_, _) => ApplyMemoryRecommend();

        InitMemSlider(_xmsSlider, _xmsValue, Math.Clamp(cfg.Xms, 256, 32768));
        InitMemSlider(_xmxSlider, _xmxValue, Math.Clamp(cfg.Xmx, 256, 32768));

        _directMemSlider.Minimum = 0; _directMemSlider.Maximum = 8192; _directMemSlider.TickFrequency = 128;
        _directMemSlider.IsSnapToTickEnabled = true;
        _directMemSlider.Width = 300;
        _directMemSlider.VerticalAlignment = VerticalAlignment.Center;
        _directMemSlider.Value = Math.Clamp(cfg.MaxDirectMemoryMb, 0, 8192);
        _directMemValue.Text = cfg.MaxDirectMemoryMb > 0 ? $"{cfg.MaxDirectMemoryMb} MB" : "自动(Xmx×0.25,256~2048)";
        _directMemValue.Width = 120; _directMemValue.TextAlignment = TextAlignment.Right;
        _directMemSlider.ValueChanged += (_, _) =>
            _directMemValue.Text = (int)_directMemSlider.Value > 0 ? $"{(int)_directMemSlider.Value} MB" : "自动(Xmx×0.25,256~2048)";

        _widthBox.Text = cfg.GameWidth.ToString(); _widthBox.Width = 110;
        _heightBox.Text = cfg.GameHeight.ToString(); _heightBox.Width = 110;
        _widthBox.Margin = new Thickness(0, 0, 8, 0);
        _fullscreenChk.IsChecked = cfg.Fullscreen;
        _fullscreenChk.VerticalAlignment = VerticalAlignment.Center;
        _fullscreenChk.Margin = new Thickness(16, 0, 0, 0);

        _jvmArgs.Text = cfg.ExtraJvmArgs;
        _jvmArgs.MinWidth = 360;
        _jvmArgs.ToolTip = "追加在启动命令的 JVM 参数,如 -XX:+UseG1GC;留空使用默认推荐参数";

        // JVM 预设模板(Celestial-2):内置性能/GC/低内存/多核/原版 + 用户自定义,套用即填进上面的参数框
        _jvmTplBox.MinWidth = 200;
        _jvmTplBox.VerticalAlignment = VerticalAlignment.Center;
        _jvmTplBox.DisplayMemberPath = nameof(JvmTemplate.Name);
        _jvmTplBox.SelectionChanged += (_, _) => OnJvmTplSelected();
        _jvmTplDesc.TextWrapping = TextWrapping.Wrap;
        _jvmTplDesc.Margin = new Thickness(2, 8, 2, 2);
        var applyTplBtn = UIKit.Button("套用", primary: true, onClick: ApplyJvmTemplate, height: 32); applyTplBtn.MinWidth = 84;
        var saveTplBtn = UIKit.Button("存为模板", primary: false, onClick: SaveJvmTemplate, height: 32); saveTplBtn.MinWidth = 104;
        var delTplBtn = UIKit.Button("删除模板", primary: false, onClick: DeleteJvmTemplate, height: 32); delTplBtn.MinWidth = 104;

        _highPrioritySw.IsChecked = cfg.HighPriorityProcess;
        _affinitySw.IsChecked = cfg.CpuAffinityEnabled;
        _multiCoreGcSw.IsChecked = cfg.MultiCoreGcOptimize;
        _preCommitSw.IsChecked = cfg.MemoryPreCommit;
        // 四个优化开关 2×2 排布(替代一字排开的勾选框,对齐系统设置页的开关组惯例)
        var advGrid = new UniformGrid { Columns = 2 };
        foreach (var sw in new[] { _highPrioritySw, _affinitySw, _multiCoreGcSw, _preCommitSw })
        {
            sw.Margin = new Thickness(0, 5, 12, 5);
            advGrid.Children.Add(sw);
        }

        var saveBtn = Lg(UIKit.Button("保存游戏设置", primary: true, onClick: SaveParams), 200);
        saveBtn.HorizontalAlignment = HorizontalAlignment.Right;
        var saveWrap = new Border { Padding = new Thickness(0, 16, 0, 2), Child = saveBtn };

        _gameSection.Children.Add(UIKit.Card(UIKit.V(
            UIKit.SettingRow("内存模式", "智能模式:启动时按可用内存实时计算,绝不超分", _autoMemSw),
            UIKit.SettingRow("本机内存与推荐", "智能模式的计算依据;点右侧按钮可把推荐值写进下面的手动滑块", UIKit.H(_memApplyBtn)),
            new Border { Padding = new Thickness(4, 0, 4, 10), Child = _memExplain },
            UIKit.SettingRow("初始内存(Xms)", "手动模式生效", UIKit.H(_xmsSlider, _xmsValue)),
            UIKit.SettingRow("最大内存(Xmx)", "手动模式生效;整合包建议 4096 MB 以上", UIKit.H(_xmxSlider, _xmxValue)),
            UIKit.SettingRow("堆外直接内存", "Iris/Sodium 等模组的 Native 缓冲硬锁,防内存失控", UIKit.H(_directMemSlider, _directMemValue)),
            UIKit.SettingRow("游戏窗口分辨率", "游戏首次启动时的窗口大小", UIKit.H(_widthBox, UIKit.Sub("×", 12), _heightBox, _fullscreenChk)),
            UIKit.SettingRow("JVM 附加参数", "高级选项,留空则使用默认推荐参数", _jvmArgs),
            UIKit.SettingRowNoDivider("JVM 预设模板", "选一套点「套用」填进上面的参数框;也可把当前参数「存为模板」随时调用",
                UIKit.H(_jvmTplBox, applyTplBtn, saveTplBtn, delTplBtn)),
            new Border { Padding = new Thickness(4, 0, 4, 10), Child = _jvmTplDesc },
            UIKit.SettingRow("高级优化", "高优先级可能影响系统响应,按需开启", advGrid),
            saveWrap)));
        UpdateMemUi();
        RefreshJvmTemplates();
    }

    private static void InitMemSlider(Slider slider, TextBlock value, int initial)
    {
        slider.Minimum = 256; slider.Maximum = 32768; slider.TickFrequency = 256;
        slider.IsSnapToTickEnabled = true;
        slider.Width = 300;
        slider.VerticalAlignment = VerticalAlignment.Center;
        slider.Value = initial;
        value.Text = $"{initial} MB";
        value.Width = 90; value.TextAlignment = TextAlignment.Right;
        slider.ValueChanged += (_, _) => value.Text = $"{(int)slider.Value} MB";
    }

    private void UpdateMemUi()
    {
        bool manual = _autoMemSw.IsChecked != true;
        _xmsSlider.IsEnabled = manual;
        _xmxSlider.IsEnabled = manual;
        _xmsValue.Opacity = manual ? 1 : 0.45;
        _xmxValue.Opacity = manual ? 1 : 0.45;
        RefreshMemExplain();
    }

    /// <summary>刷新「本机内存与推荐」说明块:展示推荐 Xms/Xmx 与该值的完整计算依据。
    /// 依据直接取 MemoryRecommendService 的 Reason(档位/预留/天花板与启动时同一算法),不在 UI 重算</summary>
    private void RefreshMemExplain()
    {
        try
        {
            var rec = _memRec.RecommendCore(0, -1, _config.Config.JavaPath);
            if (rec.Xmx <= 0)
            {
                _memExplain.Text = "当前可用内存过低,暂时给不出有效推荐;先关掉浏览器等占内存的程序再进本页。";
                _memExplain.SetResourceReference(TextBlock.ForegroundProperty, "T.Warning");
                _memApplyBtn.IsEnabled = false;
                return;
            }
            string mode = _autoMemSw.IsChecked == true ? "智能模式生效中" : "当前为手动模式,以下仅为参考";
            _memExplain.Text = $"{mode}:推荐 {rec.Display}(整机档位 {rec.Tier})\n{rec.Reason}"
                               + (string.IsNullOrEmpty(rec.Warning) ? "" : $"\n⚠ {rec.Warning}");
            _memExplain.SetResourceReference(TextBlock.ForegroundProperty,
                string.IsNullOrEmpty(rec.Warning) ? "T.ForegroundDim" : "T.Warning");
            _memApplyBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _memExplain.Text = "推荐值计算失败,可在下面手动填写内存。";
            _memApplyBtn.IsEnabled = false;
            App.WriteAppLog($"[设置] 内存推荐说明刷新失败:{ex.Message}");
        }
    }

    /// <summary>一键把当前推荐值写进手动滑块(不改配置、不保存,用户点「保存游戏设置」才落盘)</summary>
    private void ApplyMemoryRecommend()
    {
        try
        {
            var rec = _memRec.RecommendCore(0, -1, _config.Config.JavaPath);
            if (rec.Xmx <= 0) { DialogKit.Error("当前可用内存不足以给出有效推荐,请先关掉一些占内存的程序再试。", owner: Window.GetWindow(this)); return; }
            _xmxSlider.Value = Math.Clamp(rec.Xmx, 256, 32768);
            _xmsSlider.Value = Math.Clamp(rec.Xms > 0 ? rec.Xms : rec.Xmx / 2, 256, 32768);
            Shell()?.SetStatus($"已填入推荐内存 {rec.Display},点「保存游戏设置」生效");
            RefreshMemExplain();
        }
        catch (Exception ex) { App.WriteAppLog($"[设置] 填入手动内存推荐失败:{ex.Message}"); }
    }

    private void SaveParams()
    {
        try
        {
            var cfg = _config.Config;
            cfg.AutoMemoryMode = _autoMemSw.IsChecked == true;
            cfg.Xms = (int)_xmsSlider.Value;
            cfg.Xmx = Math.Max(cfg.Xms, (int)_xmxSlider.Value);
            cfg.MaxDirectMemoryMb = (int)_directMemSlider.Value;
            if (int.TryParse(_widthBox.Text.Trim(), out int w) && w >= 320) cfg.GameWidth = w;
            if (int.TryParse(_heightBox.Text.Trim(), out int h) && h >= 240) cfg.GameHeight = h;
            cfg.Fullscreen = _fullscreenChk.IsChecked == true;
            cfg.ExtraJvmArgs = _jvmArgs.Text.Trim();
            cfg.HighPriorityProcess = _highPrioritySw.IsChecked == true;
            cfg.CpuAffinityEnabled = _affinitySw.IsChecked == true;
            cfg.MultiCoreGcOptimize = _multiCoreGcSw.IsChecked == true;
            cfg.MemoryPreCommit = _preCommitSw.IsChecked == true;
            _config.Save();
            Shell()?.SetStatus("游戏设置已保存");
            DialogKit.Info("游戏设置已保存,下次启动生效", owner: Window.GetWindow(this));
            App.WriteAppLog($"[设置] 游戏设置已保存:智能内存={cfg.AutoMemoryMode} Xmx={cfg.Xmx}MB");
        }
        catch (Exception ex) { DialogKit.Error("保存失败: " + ex.Message, owner: Window.GetWindow(this)); }
    }

    // ==================== JVM 预设模板(Celestial-2)====================

    /// <summary>刷新模板下拉:内置(按本机核心数/Java 版本动态生成)+ 用户自定义,尽量保留当前选中</summary>
    private void RefreshJvmTemplates()
    {
        string prevId = (_jvmTplBox.SelectedItem as JvmTemplate)?.Id ?? "";
        var list = _jvmTemplates.AllTemplates();
        _jvmTplBox.Items.Clear();
        foreach (var t in list) _jvmTplBox.Items.Add(t);
        int idx = list.FindIndex(t => t.Id == prevId);
        _jvmTplBox.SelectedIndex = idx >= 0 ? idx : (list.Count > 0 ? 0 : -1);
        OnJvmTplSelected();
    }

    /// <summary>选中模板 → 下方显示说明与 Java 版本兼容提示</summary>
    private void OnJvmTplSelected()
    {
        if (_jvmTplBox.SelectedItem is not JvmTemplate t) { _jvmTplDesc.Text = ""; return; }
        int java = _config.Config.JavaVersion;
        string? warn = JvmTemplateService.VersionWarning(t, java);
        string kind = t.BuiltIn ? "内置模板" : "自定义模板";
        _jvmTplDesc.Text = $"{kind} · {t.Description}" + (warn == null ? "" : "\n⚠ " + warn);
        _jvmTplDesc.SetResourceReference(TextBlock.ForegroundProperty, warn == null ? "T.ForegroundDim" : "T.Warning");
        _jvmTplBox.ToolTip = t.Args;
    }

    /// <summary>套用模板:展开 %CORES%、剔除内存参数、与现有去重后填进「JVM 附加参数」框(仍需点保存生效)</summary>
    private void ApplyJvmTemplate()
    {
        if (_jvmTplBox.SelectedItem is not JvmTemplate t) { DialogKit.Info("请先选择一套模板", owner: Window.GetWindow(this)); return; }
        int java = _config.Config.JavaVersion;
        string? warn = JvmTemplateService.VersionWarning(t, java);
        if (warn != null && !DialogKit.Confirm(warn + "\n\n仍要套用吗?", "版本兼容提示", "套用", danger: false, owner: Window.GetWindow(this))) return;
        string existing = _jvmArgs.Text.Trim();
        var r = _jvmTemplates.Expand(t, existing, java);
        _jvmArgs.Text = string.IsNullOrWhiteSpace(existing) ? r.Args : (existing + " " + r.Args).Trim();
        Shell()?.SetStatus(r.Message);
        DialogKit.Info(r.Message + "\n\n参数已填进「JVM 附加参数」框,记得点「保存游戏设置」生效。\n\n" + r.Args, "模板已套用", Window.GetWindow(this));
    }

    /// <summary>把当前「JVM 附加参数」框内容存成自定义模板</summary>
    private void SaveJvmTemplate()
    {
        string args = _jvmArgs.Text.Trim();
        if (args.Length == 0) { DialogKit.Info("「JVM 附加参数」框是空的,先填点参数再存模板", owner: Window.GetWindow(this)); return; }
        string? name = DialogKit.Input("给这套参数起个名字:", "存为 JVM 模板", "我的参数模板", Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name)) return;
        string desc = DialogKit.Input("模板说明(可留空):", "存为 JVM 模板", "", Window.GetWindow(this)) ?? "";
        var (saved, msg) = _jvmTemplates.AddCustomTemplate(name, desc, args, _config.Config.JavaVersion);
        if (saved == null) { DialogKit.Warn(msg, "保存失败", Window.GetWindow(this)); return; }
        RefreshJvmTemplates();
        Shell()?.SetStatus(msg);
        DialogKit.Success(msg, "模板已保存", Window.GetWindow(this));
    }

    /// <summary>删除选中的自定义模板(内置模板不可删)</summary>
    private void DeleteJvmTemplate()
    {
        if (_jvmTplBox.SelectedItem is not JvmTemplate t) { DialogKit.Info("请先选择要删除的自定义模板", owner: Window.GetWindow(this)); return; }
        if (t.BuiltIn) { DialogKit.Info("内置模板不能删除;选中一套自定义模板再删。", owner: Window.GetWindow(this)); return; }
        if (!DialogKit.Confirm($"确定删除自定义模板「{t.Name}」吗?", "删除模板", "删除", danger: true, owner: Window.GetWindow(this))) return;
        if (_jvmTemplates.DeleteCustomTemplate(t.Id)) { RefreshJvmTemplates(); Shell()?.SetStatus("模板已删除"); }
        else DialogKit.Error("删除失败", owner: Window.GetWindow(this));
    }

    // ==================== Java 运行时 ====================

    private void BuildJavaSection()
    {
        var cfg = _config.Config;
        var scanBtn = Lg(UIKit.Button("扫描系统 Java", primary: false, onClick: ScanSystemJava), 160);
        // 折叠卡头部按钮走 36px 常规档(46px 的 Lg 档在窄头部里过重)
        var fetchBtn = UIKit.Button("刷新可下载列表", primary: false, onClick: FetchJdkList, height: 36);
        fetchBtn.MinWidth = 150;

        // 镜像源三选:华为镜像源 / 清华 TUNA 镜像 / Adoptium 官方源(索引与配置键映射见下方数组)
        string[] mirrorKeys = { "Huaweicloud", "Tsinghua", "Official" };
        _mirrorBox.Items.Add("华为镜像源(国内 OpenJDK · 仅 LTS 8/11/17/21)");
        _mirrorBox.Items.Add("清华 TUNA 镜像(Adoptium Temurin · Java 11+)");
        _mirrorBox.Items.Add("Adoptium 官方源(全版本)");
        int mirrorIdx = Array.IndexOf(mirrorKeys, cfg.JavaDownloadMirror);
        _mirrorBox.SelectedIndex = mirrorIdx >= 0 ? mirrorIdx : 0;
        _mirrorBox.VerticalAlignment = VerticalAlignment.Center;
        _mirrorDesc.TextWrapping = TextWrapping.Wrap;
        UpdateMirrorDesc();
        _mirrorBox.SelectionChanged += (_, _) =>
        {
            int idx = Math.Clamp(_mirrorBox.SelectedIndex, 0, mirrorKeys.Length - 1);
            _config.Config.JavaDownloadMirror = mirrorKeys[idx];
            _config.Save();
            UpdateMirrorDesc();
            Shell()?.SetStatus($"Java 镜像已切换:{_javaRuntimes.GetCurrentMirrorLabel()}");
            FetchJdkList();   // 重新拉取列表,刷新各版本在当前镜像下的可用性
        };

        _javaSection.Children.Add(UIKit.PageHeader("Java 运行时",
            "左列为推荐版本(启动时自动匹配的三档),右列为其余版本折叠收纳与镜像源配置;两列并排铺满页宽"));

        // ---- 左列:推荐 Java 版本 + 已安装运行时 ----
        var recHead = UIKit.V(
            UIKit.Text("推荐 Java 版本", 14, FontWeights.Medium),
            UIKit.Sub("启动游戏时按版本要求自动匹配,优先从这里下载", 12).MarginTop(4));
        var runtimeHead = UIKit.V(
            UIKit.Text("已安装运行时", 14, FontWeights.Medium),
            UIKit.Sub("已下载到 runtimes 目录并就绪的 Java 清单", 12).MarginTop(4));
        var leftCol = UIKit.V(
            UIKit.Card(UIKit.V(recHead,
                new Border { Padding = new Thickness(0, 12, 0, 0), Child = _recCards })),
            UIKit.Card(UIKit.V(runtimeHead,
                new Border { Padding = new Thickness(0, 12, 0, 0), Child = _runtimeList })));

        // ---- 右列:其余版本(折叠收纳) + 下载镜像源 + 系统其他 Java ----
        var mirrorHead = UIKit.V(
            UIKit.Text("下载镜像源", 14, FontWeights.Medium),
            UIKit.Sub("切换后自动刷新可下载版本列表;国内网络推荐华为 / 清华源", 12).MarginTop(4));
        _mirrorBox.Width = double.NaN;                              // 取消全局 320 定宽:窄列下不溢出
        _mirrorBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        var rightCol = UIKit.V(
            BuildJdkFoldCard(fetchBtn),
            UIKit.Card(UIKit.V(mirrorHead,
                new Border { Padding = new Thickness(0, 12, 0, 0), Child = _mirrorBox },
                new Border { Padding = new Thickness(0, 10, 0, 0), Child = UIKit.Panel(_mirrorDesc) })),
            UIKit.Card(UIKit.V(
                UIKit.SettingRowNoDivider("系统其他 Java", "扫描本机已安装的 Java,发现后可供启动使用", scanBtn),
                new Border { Padding = new Thickness(0, 4, 0, 0), Child = _sysJavaList })));

        // 两列等分(右列略宽 4%:折叠头与刷新按钮同行更舒展),各自竖向铺满,右侧不再留大片空白
        var cols = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1.04, GridUnitType.Star) }
            },
            Children = { leftCol, rightCol.Col(1) }
        };
        leftCol.Margin = new Thickness(0, 0, 8, 0);
        rightCol.Margin = new Thickness(8, 0, 0, 0);
        _javaSection.Children.Add(cols);

        RefreshRuntimes();
        RenderJdkCards();   // 列表未拉取时先渲染占位提示,拉取完成后走同一路径重渲染
    }

    /// <summary>其余 Java 版本折叠卡:头部(箭头 + 标题 + 数量 + 刷新按钮)可点展开/收起,默认收起 ——
    /// 十几个非推荐版本不再一屏铺开,把版面让给推荐列(折叠态跟刷新保留)</summary>
    private FrameworkElement BuildJdkFoldCard(Button fetchBtn)
    {
        _jdkFoldArrow = UIKit.Text(_jdkFoldExpanded ? "▾" : "▸", 13, FontWeights.Medium);
        _jdkFoldArrow.VerticalAlignment = VerticalAlignment.Center;
        _jdkFoldArrow.SetResourceReference(TextBlock.ForegroundProperty, "T.ForegroundDim");
        var title = UIKit.Text("其余 Java 版本", 14, FontWeights.Medium);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(8, 0, 0, 0);
        _jdkFoldCount = UIKit.Sub("", 12);
        _jdkFoldCount.VerticalAlignment = VerticalAlignment.Center;
        _jdkFoldCount.Margin = new Thickness(10, 0, 0, 0);
        var foldHint = UIKit.Sub("推荐三档之外的全部版本,点本行展开;展开后点「下载」自动解压就绪", 11.5);
        foldHint.TextWrapping = TextWrapping.Wrap;
        foldHint.Margin = new Thickness(21, 4, 0, 0);
        var headLeft = UIKit.V(UIKit.H(_jdkFoldArrow, title, _jdkFoldCount), foldHint);
        headLeft.VerticalAlignment = VerticalAlignment.Center;
        var headHit = UIKit.GhostButton(headLeft, ToggleJdkFold, height: 44);

        fetchBtn.VerticalAlignment = VerticalAlignment.Center;
        fetchBtn.Margin = new Thickness(12, 0, 0, 0);
        var headGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { headHit, fetchBtn.Col(1) }
        };

        _jdkFoldBody = new Border
        {
            Padding = new Thickness(0, 12, 0, 0),
            Child = _otherJdkList,
            Visibility = _jdkFoldExpanded ? Visibility.Visible : Visibility.Collapsed
        };
        _jdkStatus.TextWrapping = TextWrapping.Wrap;

        return UIKit.Card(UIKit.V(headGrid, _jdkFoldBody,
            new Border { Padding = new Thickness(0, 12, 0, 0), Child = UIKit.Panel(_jdkStatus) }));
    }

    /// <summary>折叠开合:展开自标题行下方长出、收起回到标题行(与导航二级菜单同一方向语义)</summary>
    private void ToggleJdkFold()
    {
        if (_jdkFoldBody == null) return;
        _jdkFoldExpanded = !_jdkFoldExpanded;
        if (_jdkFoldArrow != null) _jdkFoldArrow.Text = _jdkFoldExpanded ? "▾" : "▸";
        if (_jdkFoldExpanded) MotionKit.Enter(_jdkFoldBody, fromY: -8, MotionKit.Normal);
        else MotionKit.Exit(_jdkFoldBody, toY: -8, MotionKit.Fast);
    }

    /// <summary>镜像源说明文案(跟随当前配置键)</summary>
    private void UpdateMirrorDesc()
    {
        _mirrorDesc.Text = _config.Config.JavaDownloadMirror switch
        {
            "Tsinghua" => "当前源:清华 TUNA Adoptium 镜像 — Temurin 构建,支持 Java 11~24;Java 8 需切华为镜像或官方源。",
            "Official" => "当前源:Adoptium 官方 API — 全版本支持,境外服务器,国内网络不佳时建议切换国内镜像。",
            _ => "当前源:华为云 OpenJDK 镜像 — 仅支持 LTS 8/11/17/21(x64),其它版本请切清华源或官方源。"
        };
    }

    private void RefreshRuntimes()
    {
        _runtimeList.Children.Clear();
        List<InstalledJavaEntry> runtimes;
        try { runtimes = _javaRuntimes.ListInstalledRuntimes(); }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Java] 列出运行时失败:{ex.Message}");
            runtimes = new List<InstalledJavaEntry>();
        }
        if (runtimes.Count == 0)
        {
            _runtimeList.Children.Add(UIKit.Sub("尚未安装任何 Java 运行时", 12));
            return;
        }
        foreach (var r in runtimes)
        {
            bool ready = r.Status == "已就绪";
            var icon = IconKit.JavaCup(26);
            var name = UIKit.Text($"{r.Name}  ·  {r.Architecture}", 13);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(10, 0, 10, 0);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = 340;
            name.ToolTip = r.Name;
            var status = UIKit.Sub(r.Status, 12);
            status.VerticalAlignment = VerticalAlignment.Center;
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Children =
                {
                    icon,
                    UIKit.Badge(r.MajorVersion.StartsWith("Java", System.StringComparison.Ordinal) ? r.MajorVersion : $"Java {r.MajorVersion}",
                        ready ? "T.Success" : "T.Warning").Col(1),
                    name.Col(2),
                    status.Col(3)
                }
            };
            var panel = UIKit.Panel(row);
            panel.Margin = new Thickness(0, 0, 0, 8);
            _runtimeList.Children.Add(panel);
        }
    }

    private async void FetchJdkList()
    {
        if (_jdkFetching) return;
        _jdkFetching = true;
        _jdkStatus.Text = "正在获取可下载的 Java 列表…";
        try
        {
            _jdkList = await _javaRuntimes.FetchAvailableJdkVersionsAsync();
            RenderJdkCards();
            _jdkStatus.Text = _jdkList.Count > 0
                ? $"共 {_jdkList.Count} 个可下载版本(当前镜像:{_javaRuntimes.GetCurrentMirrorLabel()})"
                : "当前镜像源暂无可用版本,请切换镜像源后重试";
        }
        catch (Exception ex) { _jdkStatus.Text = "获取失败:" + ex.Message; }
        finally { _jdkFetching = false; }
    }

    /// <summary>一次刷新两列:左列推荐档位 + 右列折叠体内的其余版本(调用点沿用原名,下载流程逻辑不动)</summary>
    private void RenderJdkCards()
    {
        RenderRecommended();
        RenderOtherJdks();
    }

    /// <summary>左列:推荐三档版本。镜像未提供该档位时不隐藏,而是给出可操作的提示(否则用户不知道为什么少了一档)</summary>
    private void RenderRecommended()
    {
        _recCards.Children.Clear();
        if (_jdkList.Count == 0)
        {
            _recCards.Children.Add(UIKit.Sub("正在获取可下载列表…若长时间为空,点右侧「刷新可下载列表」或切换镜像源", 12));
            return;
        }
        var installed = SafeInstalled();
        foreach (var (major, why) in RecommendedJdks)
        {
            var jdk = _jdkList.FirstOrDefault(j => j.MajorVersion == major);
            _recCards.Children.Add(MakeRecommendedRow(major, why, jdk, IsInstalled(installed, major)));
        }
    }

    /// <summary>右列折叠体:推荐三档之外的全部版本,紧凑行铺满列宽(不再用 306px 定宽卡片,避免右侧留白)</summary>
    private void RenderOtherJdks()
    {
        _otherJdkList.Children.Clear();
        var others = _jdkList.Where(j => !RecommendedJdks.Any(r => r.Major == j.MajorVersion)).ToList();
        if (_jdkFoldCount != null) _jdkFoldCount.Text = _jdkList.Count == 0 ? "" : $"{others.Count} 个";
        if (_jdkList.Count == 0)
        {
            _otherJdkList.Children.Add(UIKit.Sub("尚未获取到可下载列表,点右上角「刷新可下载列表」", 12));
            return;
        }
        if (others.Count == 0)
        {
            _otherJdkList.Children.Add(UIKit.Sub("当前镜像源只提供推荐的三档版本", 12));
            return;
        }
        var installed = SafeInstalled();
        foreach (var jdk in others)
            _otherJdkList.Children.Add(MakeJdkRow(jdk, IsInstalled(installed, jdk.MajorVersion)));
    }

    /// <summary>推荐版本行:图标 + 版本 + 推荐/LTS 徽章 + 适配说明 + 下载按钮(整行铺满列宽,未装时主色描边强调)</summary>
    private Border MakeRecommendedRow(int major, string why, JdkVersionInfo? jdk, bool has)
    {
        var icon = IconKit.JavaCup(32);
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;

        var name = UIKit.Text($"Java {major}", 15, FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        var nameRow = UIKit.H(name);
        var rec = UIKit.Badge("推荐", has ? "T.Success" : "T.Primary");
        rec.Margin = new Thickness(8, 0, 0, 0);
        nameRow.Children.Add(rec);
        if (jdk is { IsLts: true })
        {
            var lts = UIKit.Badge("LTS", "T.ForegroundDim");
            lts.Margin = new Thickness(6, 0, 0, 0);
            nameRow.Children.Add(lts);
        }

        var sub = UIKit.Sub(jdk == null ? why + " · 当前镜像源未提供,可在右下切换镜像源" : why, 11.5);
        sub.TextWrapping = TextWrapping.Wrap;
        sub.Margin = new Thickness(0, 4, 0, 0);
        var textCol = UIKit.V(nameRow, sub);
        textCol.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { icon, textCol.Col(1), MakeJdkActionButton(jdk, has, 34).Col(2) }
        };

        var card = UIKit.Panel(grid, pad: 14);
        card.WithRef("T.SurfaceAlt", has ? "T.Success" : (jdk == null ? "T.Border" : "T.Primary"));
        card.BorderThickness = new Thickness(1);
        card.Margin = new Thickness(0, 0, 0, 10);
        return card;
    }

    /// <summary>其余版本行(折叠体内):图标 + 版本 + LTS 徽章 + 构建说明 + 下载按钮,与推荐行同一视觉语言</summary>
    private Border MakeJdkRow(JdkVersionInfo jdk, bool has)
    {
        var icon = IconKit.JavaCup(26);
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;

        var name = UIKit.Text($"Java {jdk.MajorVersion}", 13.5, FontWeights.Medium);
        name.VerticalAlignment = VerticalAlignment.Center;
        var nameRow = UIKit.H(name);
        if (jdk.IsLts)
        {
            var lts = UIKit.Badge("LTS", "T.Primary");
            lts.Margin = new Thickness(8, 0, 0, 0);
            nameRow.Children.Add(lts);
        }
        var sub = UIKit.Sub(string.IsNullOrEmpty(jdk.DisplayName) ? "Temurin / OpenJDK 构建" : jdk.DisplayName, 11);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        sub.ToolTip = jdk.DisplayName;
        sub.Margin = new Thickness(0, 3, 0, 0);
        var textCol = UIKit.V(nameRow, sub);
        textCol.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { icon, textCol.Col(1), MakeJdkActionButton(jdk, has, 32).Col(2) }
        };

        var card = UIKit.Panel(grid, pad: 12);
        card.WithRef("T.SurfaceAlt", has ? "T.Success" : "T.Border");
        card.BorderThickness = new Thickness(1);
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    /// <summary>下载按钮(推荐行与其余版本行共用):已装置灰 / 镜像无该版本置灰并给出出路 / 未装可下载。
    /// 点击仍走原有 DownloadJdk 流程,业务逻辑零改动</summary>
    private Button MakeJdkActionButton(JdkVersionInfo? jdk, bool has, double height)
    {
        Button action;
        if (jdk == null)
        {
            action = UIKit.Button("本源无", primary: false, height: height);
            action.IsEnabled = false;
            action.ToolTip = "当前镜像源不提供该版本,可在下方切换镜像源后重试";
        }
        else if (has)
        {
            action = UIKit.Button("已安装", primary: false, height: height);
            action.IsEnabled = false;
            action.ToolTip = "该版本已在 runtimes 目录就绪,启动时自动匹配";
        }
        else
        {
            var j = jdk;
            action = UIKit.Button("下载", primary: true, height: height, onClick: () => DownloadJdk(j));
        }
        action.MinWidth = 88;
        action.Margin = new Thickness(12, 0, 0, 0);
        action.VerticalAlignment = VerticalAlignment.Center;
        return action;
    }

    /// <summary>已装运行时清单(异常不影响渲染)</summary>
    private List<InstalledJavaEntry> SafeInstalled()
    {
        try { return _javaRuntimes.ListInstalledRuntimes(); }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Java] 列出运行时失败:{ex.Message}");
            return new List<InstalledJavaEntry>();
        }
    }

    /// <summary>已装判定(沿用原卡片的版本号比对语义:去掉尾部 .0 后按字串相等)</summary>
    private static bool IsInstalled(List<InstalledJavaEntry> installed, int major) =>
        installed.Any(r => r.MajorVersion.TrimEnd(".0".ToCharArray())
            .Equals(major.ToString(), StringComparison.Ordinal));

    private void OnJdkProgress(DownloadProgressInfo info)
    {
        if (!_downloadingJdk) return;
        Dispatcher.BeginInvoke(() =>
            _jdkStatus.Text = $"下载中 {info.Progress * 100:0}%  ·  {UIKit.FmtBytes(info.DownloadedBytes)} / {UIKit.FmtBytes(info.TotalBytes)}" +
                              (info.SpeedBytesPerSec > 0 ? $"  ·  {UIKit.FmtBytes((long)info.SpeedBytesPerSec)}/s" : ""));
    }

    private async void DownloadJdk(JdkVersionInfo jdk)
    {
        if (_downloadingJdk)
        {
            DialogKit.Info("已有 Java 正在下载,请等待完成", owner: Window.GetWindow(this));
            return;
        }
        _downloadingJdk = true;
        _jdkStatus.Text = $"正在下载 Java {jdk.MajorVersion}…";
        Shell()?.SetStatus($"正在下载 Java {jdk.MajorVersion}…");
        try
        {
            _downloads.ResetOverallProgress();
            string? path = await _javaRuntimes.DownloadJdkAsync(jdk.MajorVersion);
            if (!string.IsNullOrEmpty(path))
            {
                _jdkStatus.Text = $"Java {jdk.MajorVersion} 下载完成并已就绪";
                Shell()?.SetStatus($"Java {jdk.MajorVersion} 安装完成");
                DialogKit.Success($"Java {jdk.MajorVersion} 已下载到 runtimes 目录并校验就绪", "下载完成", Window.GetWindow(this));
                RefreshRuntimes();
                RenderJdkCards();   // 该版本卡片转为「已安装」态
            }
            else
            {
                // 明确文字提示:服务层已给出具体原因(无适配包/下载失败/解压失败)
                string reason = _javaRuntimes.LastDownloadError;
                if (string.IsNullOrEmpty(reason))
                    reason = "下载失败,请检查网络或在网络设置中切换 Java 镜像源后重试。";
                _jdkStatus.Text = reason.Replace("\n", " ");
                DialogKit.Error(reason, $"Java {jdk.MajorVersion} 下载失败", Window.GetWindow(this));
            }
        }
        catch (Exception ex)
        {
            _jdkStatus.Text = "下载异常:" + ex.Message;
            App.WriteAppLog($"[Java] JDK 下载异常:{ex}");
        }
        finally { _downloadingJdk = false; }
    }

    private async void ScanSystemJava()
    {
        Shell()?.SetStatus("正在扫描系统 Java…");
        try
        {
            await _javaScan.ScanAsync(force: true);
            _sysJavaList.Children.Clear();
            if (_javaScan.FoundJavas.Count == 0)
            {
                _sysJavaList.Children.Add(UIKit.Sub("系统中未发现其他 Java(建议直接下载内置运行时)", 12));
            }
            else
            {
                _sysJavaList.Children.Add(UIKit.Sub($"系统发现 {_javaScan.FoundJavas.Count} 个 Java:", 12));
                foreach (var j in _javaScan.FoundJavas.Take(8))
                {
                    var info = UIKit.Sub($"{j.FullVersion}  ·  {j.Vendor}  ·  {j.Architecture}  ·  {j.Path}", 12);
                    info.VerticalAlignment = VerticalAlignment.Center;
                    info.Margin = new Thickness(10, 0, 0, 0);
                    info.TextTrimming = TextTrimming.CharacterEllipsis;
                    info.ToolTip = j.Path;
                    var row = new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = GridLength.Auto },
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                        },
                        Children = { UIKit.Badge($"Java {j.MajorVersion}", "T.Primary"), info.Col(1) }
                    };
                    var panel = UIKit.Panel(row);
                    panel.Margin = new Thickness(0, 0, 0, 8);
                    _sysJavaList.Children.Add(panel);
                }
            }
            Shell()?.SetStatus($"扫描完成:{_javaScan.FoundJavas.Count} 个 Java");
        }
        catch (Exception ex) { DialogKit.Error("扫描失败:" + ex.Message, owner: Window.GetWindow(this)); }
    }

    // ==================== 路径设置 ====================

    private void BuildPathsSection()
    {
        _pathsSection.Children.Add(UIKit.PageHeader("路径设置", "全部游戏数据固定在程序同级 APP\\MCGAME 目录,不写 C 盘用户目录;移动程序文件夹即整体迁移"));

        var rows = new[]
        {
            ("数据根目录", AppPaths.Root, "versions / instances / saves 等全部数据的根"),
            ("游戏本体", AppPaths.Versions, "版本 json 与 client jar(全版本共享)"),
            ("依赖库", AppPaths.Libraries, "游戏启动所需 libraries(全版本共享)"),
            ("游戏资源", AppPaths.Assets, "贴图 / 声音 / 语言文件"),
            ("存档", AppPaths.Saves, "按版本隔离,删除版本时绝不连带删除"),
            ("Java 运行时", AppPaths.Runtimes, "自动下载的 JDK/JRE"),
            ("日志", AppPaths.Logs, "启动器与游戏日志")
        };

        var list = new StackPanel();
        foreach (var (title, path, desc) in rows)
        {
            var openBtn = Lg(UIKit.Button("打开目录", primary: false, onClick: () => OpenDir(path)), 150);
            var pathText = UIKit.Sub(path, 12);
            pathText.TextTrimming = TextTrimming.CharacterEllipsis;
            pathText.MaxWidth = 560;
            pathText.ToolTip = path;   // 路径过长时悬停看完整值,杜绝裁切后无从查看
            pathText.VerticalAlignment = VerticalAlignment.Center;
            list.Children.Add(UIKit.SettingRow(title, desc, UIKit.H(pathText, openBtn)));
        }
        _pathsSection.Children.Add(UIKit.Card(list));
    }

    // ==================== 网络设置 ====================

    /// <summary>网络设置控件组工厂:设置页「网络设置」的源 / 代理 / 分类开关 / CF Key / 测速
    /// 统一由这里构建与保存,口径集中在一处;
    /// 每次调用返回全新一套控件。onStatus 播报测速状态,onSaved 交给调用方决定怎么提示。</summary>
    public static (FrameworkElement Content, Action Reload) BuildNetworkControls(
        ConfigService config, DownloadService downloads, Action<string> onStatus, Action<string> onSaved)
    {
        var sourceBox = UIKit.ComboBox();
        sourceBox.Items.Add("智能自动(测速选最快源,推荐)");
        sourceBox.Items.Add("BMCLAPI(国内加速)");
        sourceBox.Items.Add("Mojang(官方源)");
        // 2026-09-30:自有镜像——自己同步的 BMCLAPI 布局源,装机与冷门版本都走它
        sourceBox.Items.Add("自有镜像(自建源,需填地址)");
        sourceBox.VerticalAlignment = VerticalAlignment.Center;

        var mirrorBox = UIKit.TextBox(placeholder: "如 https://dl.fufucraft.top,仅支持 https");
        mirrorBox.Width = 340;
        mirrorBox.VerticalAlignment = VerticalAlignment.Center;
        mirrorBox.ToolTip = "自建镜像根地址,目录结构需与 BMCLAPI 一致(versions / maven / assets / fabric-meta / forge / optifine);\n" +
                            "只走 https,镜像缺的文件会自动回落 BMCLAPI 与官方源,不会装不上";

        TextBlock mirrorResult = UIKit.Sub("", 12);
        mirrorResult.TextWrapping = TextWrapping.Wrap;
        mirrorResult.MaxWidth = 340;
        mirrorResult.VerticalAlignment = VerticalAlignment.Center;
        var mirrorCheckBtn = Lg(UIKit.Button("校验镜像地址", primary: false, onClick: CheckMirror), 180);

        var proxyBox = UIKit.TextBox(placeholder: "如 http://127.0.0.1:7890,留空为直连");
        proxyBox.Width = 340;
        proxyBox.VerticalAlignment = VerticalAlignment.Center;
        proxyBox.ToolTip = "HTTP 代理地址,如 http://127.0.0.1:7890;留空为直连";

        // 代理分类开关(BlockHelm-7):游戏核心下载与模组下载可分开独立控制是否走代理
        var proxyGameSw = new UIKit.ToggleSwitch("游戏核心/资源/库/Java 走代理");
        var proxyModSw = new UIKit.ToggleSwitch("模组/整合包 走代理");
        proxyGameSw.VerticalAlignment = VerticalAlignment.Center;
        proxyModSw.VerticalAlignment = VerticalAlignment.Center;
        proxyGameSw.Margin = new Thickness(0, 0, 24, 0);

        var cfKeyBox = UIKit.TextBox(placeholder: "留空则 CF 整合包在线模组需手动补齐");
        cfKeyBox.Width = 340;
        cfKeyBox.VerticalAlignment = VerticalAlignment.Center;
        cfKeyBox.ToolTip = "导入 CurseForge 整合包时自动补齐在线模组所需;console.curseforge.com 免费注册获取";

        TextBlock speedResult = UIKit.Sub("", 12);
        speedResult.TextWrapping = TextWrapping.Wrap;
        speedResult.MaxWidth = 340;
        speedResult.VerticalAlignment = VerticalAlignment.Center;
        var speedTestBtn = Lg(UIKit.Button("下载源测速", primary: false, onClick: SpeedTest), 180);

        var saveBtn = Lg(UIKit.Button("保存网络设置", primary: true, onClick: Save), 200);
        saveBtn.HorizontalAlignment = HorizontalAlignment.Right;
        var saveWrap = new Border { Padding = new Thickness(0, 16, 0, 2), Child = saveBtn };

        var content = UIKit.V(
            UIKit.SettingRow("游戏下载源", "版本清单、client、libraries、assets 的来源", sourceBox),
            UIKit.SettingRow("自有镜像地址", "选「自有镜像」时生效;缺的文件自动回落 BMCLAPI 与官方源", UIKit.H(mirrorBox, mirrorCheckBtn)),
            UIKit.SettingRow("镜像校验结果", "探测清单 / Fabric 元数据 / 库索引三条代表路径", mirrorResult),
            UIKit.SettingRow("HTTP 代理", "网络受限时填写,留空直连", proxyBox),
            UIKit.SettingRow("代理分类开关", "游戏核心下载与模组下载可分开独立控制是否走代理;不开则该类直连", UIKit.H(proxyGameSw, proxyModSw)),
            UIKit.SettingRow("CurseForge API Key", "导入 CF 整合包自动补齐模组用,免费注册获取", cfKeyBox),
            UIKit.SettingRow("下载源测速", "对比各接入点延迟,选择最快的源", UIKit.H(speedTestBtn, speedResult)),
            saveWrap);

        var reload = new Action(() =>
        {
            var c = config.Config;
            sourceBox.SelectedIndex = c.DownloadSource switch
            {
                "Mojang" => 2,
                "BMCLAPI" => 1,
                "Custom" => 3,
                _ => 0
            };
            mirrorBox.Text = c.CustomDownloadBaseUrl;
            proxyBox.Text = c.ProxyUrl;
            proxyGameSw.IsChecked = c.UseProxyForGame;
            proxyModSw.IsChecked = c.UseProxyForMod;
            cfKeyBox.Text = c.CurseForgeApiKey;
        });
        reload();
        return (content, reload);

        void Save()
        {
            var c = config.Config;
            c.DownloadSource = sourceBox.SelectedIndex switch
            {
                1 => "BMCLAPI",
                2 => "Mojang",
                3 => "Custom",
                _ => "Auto"
            };
            // 2026-09-30:地址按 MirrorUrlMap 的同一套规则规范化后落盘——
            // 存下来的就是实际生效的那份,避免「尾部斜杠 / 大小写」这类差异导致两次保存行为不一致
            string? normalized = MirrorUrlMap.NormalizeBase(mirrorBox.Text);
            if (c.DownloadSource == "Custom" && normalized == null)
            {
                onSaved("自有镜像地址无效:" + (string.IsNullOrWhiteSpace(mirrorBox.Text)
                    ? "还没填,请在「自有镜像地址」写完整 https 地址"
                    : "必须以 https:// 开头、不带空格与查询参数") + "\n已保持原下载源不变,填对后再保存一次");
                return;
            }
            c.CustomDownloadBaseUrl = normalized ?? mirrorBox.Text.Trim();
            c.ProxyUrl = proxyBox.Text.Trim();
            c.UseProxyForGame = proxyGameSw.IsChecked == true;
            c.UseProxyForMod = proxyModSw.IsChecked == true;
            c.CurseForgeApiKey = cfKeyBox.Text.Trim();
            config.Save();
            downloads.ReloadProxy();   // 代理地址与分类开关立即生效
            downloads.NotifySourceChanged();
            // 安全审计修复:代理地址可能内嵌 user:pass,展示统一脱敏,避免凭据被截图外传
            onSaved($"网络设置已保存\n游戏下载源:{downloads.CurrentSourceName}\n代理:{(string.IsNullOrEmpty(c.ProxyUrl) ? "直连" : SensitiveData.MaskUrl(c.ProxyUrl))}");
        }

        async void CheckMirror()
        {
            mirrorResult.Text = "校验中…(三条路径并发探测,最长 15 秒)";
            onStatus("正在校验自有镜像地址…");
            var (ok, msg) = await downloads.ValidateCustomMirrorAsync(mirrorBox.Text);
            mirrorResult.Text = msg;
            onStatus(ok ? "自有镜像可用" : "自有镜像校验未通过");
        }

        async void SpeedTest()
        {
            speedResult.Text = "测速中…";
            onStatus("正在测速各下载源…");
            try
            {
                var results = await downloads.TestSourceSpeedAsync();
                var lines = results
                    .OrderBy(r => r.Reachable ? r.LatencyMs : long.MaxValue)
                    .Select(r => r.Reachable ? $"{r.Name} {r.LatencyMs}ms" : $"{r.Name} 不可达");
                speedResult.Text = string.Join("  ·  ", lines);
                onStatus("测速完成");
            }
            catch (Exception ex)
            {
                speedResult.Text = "测速失败:" + ex.Message;
            }
        }
    }

    private void BuildNetworkSection()
    {
        var (content, _) = BuildNetworkControls(_config, _downloads,
            onStatus: t => Shell()?.SetStatus(t),
            onSaved: msg =>
            {
                Shell()?.SetStatus("网络设置已保存");
                DialogKit.Info(msg, owner: Window.GetWindow(this));
            });
        _networkSection.Children.Add(UIKit.PageHeader("网络设置", "游戏下载源与代理配置;国内推荐 BMCLAPI 加速"));
        _networkSection.Children.Add(UIKit.Card(content));
    }

    // ==================== 外观设置 ====================

    private void BuildThemeSection()
    {
        _themeSection.Children.Add(UIKit.PageHeader("外观设置", "点击主题卡片即时切换并自动保存,无需重启;自定义主题可放 tupian\\jm\\*.json 或用下方导入功能"));

        _themeCards.Margin = new Thickness(0, 18, 0, 0);
        _themeSection.Children.Add(_themeCards);

        // ---- 自定义主色(色相滑块实时重算全局主色色阶,保留当前主题明暗风格)----
        var spectrum = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (int i = 0; i <= 6; i++)
            spectrum.GradientStops.Add(new GradientStop(ThemeManager.HslToRgb(i * 60, 0.85, 0.5), i / 6.0));
        spectrum.Freeze();
        // 取色器标准形态:加粗圆角光谱轨 + 白环取色钮(HueSlider 透明轨道铺在其上)
        var hueTrack = new Border { Height = 14, CornerRadius = new CornerRadius(UIKit.R.Pill), Background = spectrum, VerticalAlignment = VerticalAlignment.Center };
        var hueSlider = UIKit.HueSlider();
        hueSlider.Minimum = 0; hueSlider.Maximum = 360; hueSlider.TickFrequency = 15;
        hueSlider.Width = 260;
        hueSlider.VerticalAlignment = VerticalAlignment.Center;
        var hueArea = new Grid { Width = 260, Children = { hueTrack, hueSlider } };

        // 先赋初值再订阅,避免构造期 ValueChanged 误写设置
        hueSlider.Value = ThemeManager.Settings.PrimaryHue >= 0 ? ThemeManager.Settings.PrimaryHue : 215;
        hueSlider.ValueChanged += (_, _) => ThemeManager.SetPrimaryHue(hueSlider.Value);
        var hueReset = Lg(UIKit.Button("恢复主题默认", primary: false, onClick: () =>
        {
            ThemeManager.ResetPrimaryHue();
            Shell()?.SetStatus("已恢复主题默认主色");
        }), 180);

        _themeSection.Children.Add(UIKit.Card(
            UIKit.SettingRowNoDivider("自定义主色", "拖动色相滑块即时改变全局主色(保留当前主题明暗风格)",
                UIKit.H(hueArea, hueReset))));

        // ---- 背景效果:模糊度 / 亮度 / 显示模式 / 卡片透明度(拖动即时生效,自动保存)----
        InitEffectSlider(_blurSlider, _blurValue, 0, 24, ThemeManager.ResolveBlur(), v => $"{(int)v}", v => ThemeManager.SetBackgroundBlur(v));
        InitEffectSlider(_brightnessSlider, _brightnessValue, 40, 100, ThemeManager.ResolveBrightness() * 100, v => $"{(int)v}%", v => ThemeManager.SetBackgroundBrightness(v / 100.0));
        InitEffectSlider(_cardOpacitySlider, _cardOpacityValue, 60, 100, ThemeManager.ResolveCardOpacity() * 100, v => $"{(int)v}%", v => ThemeManager.SetCardOpacity(v / 100.0));

        _bgModeBox.Items.Add("填充(铺满窗口,推荐)");
        _bgModeBox.Items.Add("适应(完整显示,可能留边)");
        _bgModeBox.Items.Add("平铺(原始尺寸重复)");
        _bgModeBox.SelectedIndex = ThemeManager.ResolveBackgroundMode() switch { "Fit" => 1, "Tile" => 2, _ => 0 };
        _bgModeBox.SelectionChanged += (_, _) =>
            ThemeManager.SetBackgroundMode(_bgModeBox.SelectedIndex switch { 1 => "Fit", 2 => "Tile", _ => "Fill" });

        _themeSection.Children.Add(UIKit.Card(UIKit.V(
            UIKit.SettingRow("背景模糊度", "毛玻璃效果,拖动即时预览并自动保存", UIKit.H(_blurSlider, _blurValue)),
            UIKit.SettingRow("背景亮度", "调低可让背景更暗,突出前景内容", UIKit.H(_brightnessSlider, _brightnessValue)),
            UIKit.SettingRow("背景显示模式", "图片任意比例,缩放适配由程序处理", _bgModeBox),
            UIKit.SettingRow("卡片透明度", "全局卡片/侧栏的半透明程度", UIKit.H(_cardOpacitySlider, _cardOpacityValue)),
            new Border { Padding = new Thickness(0, 14, 0, 2), Child = UIKit.SettingRowNoDivider("窗口透明度", "整个窗口的透明度(拖动即时预览)", UIKit.H(_opacitySlider, _opacityValue)) })));

        _opacitySlider.Minimum = 60; _opacitySlider.Maximum = 100; _opacitySlider.TickFrequency = 5;
        _opacitySlider.IsSnapToTickEnabled = true;
        _opacitySlider.Width = 260;
        _opacitySlider.VerticalAlignment = VerticalAlignment.Center;
        _opacitySlider.Value = Math.Round(ThemeManager.ResolveOpacity() * 100);
        _opacityValue.Text = $"{(int)_opacitySlider.Value}%";
        _opacityValue.Width = 46; _opacityValue.TextAlignment = TextAlignment.Right;
        _opacitySlider.ValueChanged += (_, _) =>
        {
            _opacityValue.Text = $"{(int)_opacitySlider.Value}%";
            ThemeManager.Settings.WindowOpacity = _opacitySlider.Value / 100.0;
            if (Window.GetWindow(this) is ShellWindow) ThemeManager.NotifyVisualChanged();
            ThemeManager.SaveSettings();   // 拖动即自动保存
        };

        // ---- 自定义背景:按钮选择 + 拖拽入窗两种入口 ----
        var bgPick = Lg(UIKit.Button("选择背景图", primary: true, onClick: OnPickBackground), 180);
        var bgClear = Lg(UIKit.Button("恢复主题默认背景", primary: false, onClick: () =>
        {
            ThemeManager.SetCustomBackground("");
            Shell()?.SetStatus("已恢复主题默认背景");
        }), 200);

        _themeSection.Children.Add(UIKit.Card(UIKit.V(
            UIKit.SettingRowNoDivider("自定义背景图", "点按钮选择或直接拖拽图片进窗口,覆盖当前主题背景(png/jpg/bmp)", UIKit.H(bgPick, bgClear)))));

        // ---- 主题配置分享:导入 / 导出 json ----
        var exportBtn = Lg(UIKit.Button("导出当前主题配置", primary: false, onClick: OnExportTheme), 200);
        var importBtn = Lg(UIKit.Button("导入主题配置", primary: false, onClick: OnImportTheme), 180);
        _themeSection.Children.Add(UIKit.Card(UIKit.V(
            UIKit.SettingRowNoDivider("主题配置分享", "导出当前主题 json 分享给他人,或导入别人分享的主题配置", UIKit.H(exportBtn, importBtn)))));
    }

    /// <summary>初始化一个效果滑块(先赋初值再订阅,拖动即时生效 + 自动保存)</summary>
    private static void InitEffectSlider(Slider slider, TextBlock value, double min, double max, double initial,
        Func<double, string> format, Action<double> apply)
    {
        slider.Minimum = min; slider.Maximum = max; slider.TickFrequency = (max - min) / 24;
        slider.Width = 260;
        slider.VerticalAlignment = VerticalAlignment.Center;
        slider.Value = initial;
        value.Text = format(initial);
        value.Width = 46; value.TextAlignment = TextAlignment.Right;
        slider.ValueChanged += (_, _) =>
        {
            value.Text = format(slider.Value);
            apply(slider.Value);
        };
    }

    private void RefreshThemeCards()
    {
        _themeCards.Children.Clear();
        foreach (var t in ThemeManager.All)
        {
            bool current = ThemeManager.Current.Id == t.Id;

            var swatch = new Border { Width = 104, Height = 58, CornerRadius = new CornerRadius(UIKit.R.Chip), BorderThickness = new Thickness(1) };
            // 缩略预览:主题背景图存在时用背景图铺底,否则用底色色块
            string? bgFile = string.IsNullOrEmpty(t.BackgroundImage) ? null : Path.Combine(ImageAssets.ThemeDir, t.BackgroundImage);
            var thumb = bgFile != null && File.Exists(bgFile) ? ImageAssets.LoadFromFile(bgFile) : null;
            swatch.Background = thumb == null
                ? ParseBrush(t.Colors.Background)
                : new ImageBrush(thumb) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
            swatch.BorderBrush = ParseBrush(t.Colors.Border);
            var surface = new Border { Width = 62, Height = 26, CornerRadius = new CornerRadius(UIKit.R.Pill), Background = ParseBrush(t.Colors.Surface), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            surface.Margin = new Thickness(8, 8, 0, 0);
            var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(UIKit.R.Pill), Background = ParseBrush(t.Colors.Primary), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            dot.Margin = new Thickness(0, 0, 8, 8);
            var preview = new Grid { Children = { swatch, surface, dot } };

            var name = UIKit.Text(t.Name, 12.5, FontWeights.Medium);
            var tag = UIKit.Sub(t.IsCustom ? "自定义" : (t.IsDark ? "深色预设" : "亮色预设"), 10);
            name.Margin = new Thickness(0, 10, 0, 2);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = t.Name;
            var body = UIKit.V(preview, name, tag);

            var card = new Border
            {
                CornerRadius = new CornerRadius(UIKit.R.Panel),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 14, 14),
                Width = 152,
                BorderThickness = new Thickness(current ? 2 : 1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = body,
                RenderTransform = new TranslateTransform()
            };
            card.SetResourceReference(Border.BackgroundProperty, "T.Surface");
            card.SetResourceReference(Border.BorderBrushProperty, current ? "T.Primary" : "T.Border");
            card.MouseEnter += (_, _) => card.Lift(-2);
            card.MouseLeave += (_, _) => card.Lift(0);

            var id = t.Id;
            card.MouseLeftButtonUp += (_, _) =>
            {
                ThemeManager.SwitchAndPersist(id);
                RefreshThemeCards();
                Shell()?.SetStatus($"主题已切换: {ThemeManager.Current.Name}");
            };
            _themeCards.Children.Add(card);
        }
    }

    private void OnPickBackground()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择背景图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dlg.ShowDialog() != true) return;
        ThemeManager.SetCustomBackground(dlg.FileName);
        Shell()?.SetStatus("自定义背景已应用");
    }

    /// <summary>导出当前主题配置 json(含配色/背景/默认模糊与卡片透明度)</summary>
    private void OnExportTheme()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出主题配置",
                Filter = "主题配置|*.json",
                FileName = $"theme-{ThemeManager.Current.Id}.json"
            };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, ThemeManager.ExportThemeJson());
            Shell()?.SetStatus("主题配置已导出");
            DialogKit.Success($"主题「{ThemeManager.Current.Name}」配置已导出到:\n{dlg.FileName}", "导出成功", Window.GetWindow(this));
        }
        catch (Exception ex) { DialogKit.Error("导出失败: " + ex.Message, owner: Window.GetWindow(this)); }
    }

    /// <summary>导入主题 json:注册为自定义主题并即时应用</summary>
    private void OnImportTheme()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "导入主题配置", Filter = "主题配置|*.json" };
            if (dlg.ShowDialog() != true) return;
            string json = File.ReadAllText(dlg.FileName);
            if (ThemeManager.ImportThemeJson(json, out string error))
            {
                RefreshThemeCards();
                Shell()?.SetStatus($"主题已导入: {ThemeManager.Current.Name}");
                DialogKit.Success($"已导入并应用主题「{ThemeManager.Current.Name}」", "导入成功", Window.GetWindow(this));
            }
            else
            {
                DialogKit.Error(error, "导入失败", Window.GetWindow(this));
            }
        }
        catch (Exception ex) { DialogKit.Error("导入异常: " + ex.Message, owner: Window.GetWindow(this)); }
    }

    private static Brush ParseBrush(string hex)
    {
        try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        catch { return Brushes.Gray; }
    }

    // ==================== 下载设置 ====================

    private void BuildDownloadSection()
    {
        var cfg = _config.Config;

        // 2026-08-28 线程档位与引擎对齐:硬性下限 60,上限 128(步长 4)
        _threadsSlider.Minimum = 60; _threadsSlider.Maximum = 128; _threadsSlider.TickFrequency = 4;
        _threadsSlider.IsSnapToTickEnabled = true;
        _threadsSlider.Width = 260;
        _threadsSlider.VerticalAlignment = VerticalAlignment.Center;
        _threadsSlider.Value = Math.Clamp(cfg.DownloadThreads, 60, 128);
        _threadsValue.Text = $"{(int)_threadsSlider.Value} 线程";
        _threadsValue.Width = 70; _threadsValue.TextAlignment = TextAlignment.Right;
        _threadsSlider.ValueChanged += (_, _) => _threadsValue.Text = $"{(int)_threadsSlider.Value} 线程";

        // 大文件分片数(4~32,步长 4):≥4MB 文件自动分片并发,单文件峰值并发不超过该值
        _shardSlider.Minimum = 4; _shardSlider.Maximum = 32; _shardSlider.TickFrequency = 4;
        _shardSlider.IsSnapToTickEnabled = true;
        _shardSlider.Width = 260;
        _shardSlider.VerticalAlignment = VerticalAlignment.Center;
        _shardSlider.Value = Math.Clamp(cfg.DownloadShardCount, 4, 32);
        _shardValue.Text = $"{(int)_shardSlider.Value} 片";
        _shardValue.Width = 70; _shardValue.TextAlignment = TextAlignment.Right;
        _shardSlider.ValueChanged += (_, _) => _shardValue.Text = $"{(int)_shardSlider.Value} 片";

        // 预设档:一键把线程数与分片数套到同一组合,免去逐项试参数
        void ApplyPreset(int threads, int shards)
        {
            _threadsSlider.Value = Math.Clamp(threads, 60, 128);
            _shardSlider.Value = Math.Clamp(shards, 4, 32);
            Shell()?.SetStatus($"已套用下载预设:线程 {threads} · 分片 {shards}(点保存生效)");
        }
        var presetBalanced = UIKit.Button("均衡", primary: false, onClick: () => ApplyPreset(60, 16), height: 32);
        var presetFast = UIKit.Button("高速", primary: false, onClick: () => ApplyPreset(96, 24), height: 32);
        var presetMax = UIKit.Button("极限", primary: false, onClick: () => ApplyPreset(128, 32), height: 32);

        _verifySw.IsChecked = cfg.VerifyAfterDownload;
        _verifySw.VerticalAlignment = VerticalAlignment.Center;

        _rateSlider.Minimum = 0; _rateSlider.Maximum = 50; _rateSlider.TickFrequency = 1;
        _rateSlider.IsSnapToTickEnabled = true;
        _rateSlider.Width = 260;
        _rateSlider.VerticalAlignment = VerticalAlignment.Center;
        _rateSlider.Value = Math.Clamp(cfg.DownloadRateLimitMbps, 0, 50);
        _rateValue.Text = cfg.DownloadRateLimitMbps > 0 ? $"{cfg.DownloadRateLimitMbps:0} MB/s" : "不限速";
        _rateValue.Width = 80; _rateValue.TextAlignment = TextAlignment.Right;
        _rateSlider.ValueChanged += (_, _) =>
            _rateValue.Text = _rateSlider.Value > 0 ? $"{_rateSlider.Value:0} MB/s" : "不限速";

        var saveBtn = Lg(UIKit.Button("保存下载设置", primary: true, onClick: () =>
        {
            cfg.DownloadThreads = (int)_threadsSlider.Value;
            cfg.DownloadShardCount = (int)_shardSlider.Value;
            cfg.VerifyAfterDownload = _verifySw.IsChecked == true;
            cfg.DownloadRateLimitMbps = _rateSlider.Value;
            _config.Save();
            Shell()?.SetStatus("下载设置已保存");
            DialogKit.Info($"下载设置已保存\n并发线程:{cfg.DownloadThreads}(下次启动生效)\n大文件分片:{cfg.DownloadShardCount} 片\n完整性校验:{(cfg.VerifyAfterDownload ? "开启" : "关闭")}\n限速:{(cfg.DownloadRateLimitMbps > 0 ? cfg.DownloadRateLimitMbps + " MB/s" : "不限速")}",
                owner: Window.GetWindow(this));
        }), 200);
        saveBtn.HorizontalAlignment = HorizontalAlignment.Right;
        var saveWrap = new Border { Padding = new Thickness(0, 16, 0, 2), Child = saveBtn };

        _downloadSection.Children.Add(UIKit.PageHeader("下载设置", "预设档、并发线程、大文件分片与限速;线程/分片下次启动生效"));
        _downloadSection.Children.Add(UIKit.Card(UIKit.V(
            UIKit.SettingRow("预设档", "按网络环境一键套用线程与分片组合:均衡最稳、高速更快、极限压榨带宽", UIKit.H(presetBalanced, presetFast, presetMax)),
            UIKit.SettingRow("并发线程数", "各类别任务独立并发,线程越多下载越快(硬性下限 60,网络拥堵时可适度调低)", UIKit.H(_threadsSlider, _threadsValue)),
            UIKit.SettingRow("大文件分片数", "≥4MB 的文件自动分片并发下载,分片越多超大文件越快(每片不小于 4MB)", UIKit.H(_shardSlider, _shardValue)),
            UIKit.SettingRow("完整性校验", "下载完成后 SHA1 比对,失败自动重下一次", _verifySw),
            UIKit.SettingRow("全局限速", "边下边玩时可限制下载占用的带宽", UIKit.H(_rateSlider, _rateValue)),
            saveWrap)));
    }

    // ==================== 高级设置 ====================

    private void BuildAdvancedSection()
    {
        var cfg = _config.Config;
        string ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "1.9.8.6";   // 2026-09-25:ToString(3) 会截成 1.9.8,与状态栏 v1.9.8.6 不一致

        var checkBtn = Lg(UIKit.Button("检查更新", primary: false, onClick: CheckUpdate), 180);
        _updateText.Text = $"当前版本 {ver}";
        _updateText.TextWrapping = TextWrapping.Wrap;
        _updateText.FontSize = 12.5;

        var envBtn = Lg(UIKit.Button("运行环境自检", primary: false, onClick: RunEnvCheck), 180);   // 2026-09-25:按钮等宽 180
        var dataDirBtn = Lg(UIKit.Button("打开数据目录", primary: false, onClick: () => OpenDir(AppPaths.Root)), 180);
        var logDirBtn = Lg(UIKit.Button("打开日志目录", primary: false, onClick: () => OpenDir(AppPaths.Logs)), 180);

        _verboseSw.IsChecked = cfg.VerboseLog;
        _verboseSw.Checked += (_, _) => { _config.Config.VerboseLog = true; App.SetVerboseLog(true); _config.Save(); };
        _verboseSw.Unchecked += (_, _) => { _config.Config.VerboseLog = false; App.SetVerboseLog(false); _config.Save(); };

        _logCleanSw.IsChecked = cfg.LogAutoCleanEnabled;
        _logKeepBox.Text = cfg.LogKeepCount.ToString();
        _logKeepBox.Width = 110;
        _logKeepBox.VerticalAlignment = VerticalAlignment.Center;
        var logSave = Lg(UIKit.Button("保存", primary: false, onClick: () =>
        {
            _config.Config.LogAutoCleanEnabled = _logCleanSw.IsChecked == true;
            if (int.TryParse(_logKeepBox.Text.Trim(), out int keep) && keep >= 3) _config.Config.LogKeepCount = keep;
            _config.Save();
            Shell()?.SetStatus("日志设置已保存");
        }), 180);   // 2026-09-25:按钮等宽 180

        _advancedSection.Children.Add(UIKit.PageHeader("高级设置", "程序更新、运行环境自检与日志配置"));
        _advancedSection.Children.Add(UIKit.Card(UIKit.V(
            UIKit.SettingRow("程序更新", "检查并应用启动器新版本,更新前自动备份", checkBtn),
            new Border { Padding = new Thickness(2, 12, 2, 6), Child = _updateText },
            UIKit.SettingRow("自动检查更新", "启动时静默检查,有新版才提示", CreateAutoUpdateChk()),
            UIKit.SettingRow("环境自检", ".NET 运行时 / 架构 / 磁盘 / 网络 / Java 全面体检", envBtn),
            new Border { Padding = new Thickness(2, 12, 2, 6), Child = _envResultList },
            UIKit.SettingRow("日志配置", "自动清理旧日志,保留最近 N 份", UIKit.H(_logCleanSw, UIKit.Sub("保留", 12), _logKeepBox, UIKit.Sub("份", 12), logSave)),
            UIKit.SettingRow("分级日志", "开启后 Debug 级细节写入 app.log", _verboseSw),
            new Border { Padding = new Thickness(0, 16, 0, 2), Child = UIKit.SettingRowNoDivider("目录入口", "游戏数据与启动器日志所在位置", UIKit.H(dataDirBtn, logDirBtn)) })));
    }

    private CheckBox CreateAutoUpdateChk()
    {
        var sw = new UIKit.ToggleSwitch("启动时自动检查", _config.Config.AutoCheckUpdate);
        sw.Checked += (_, _) => { _config.Config.AutoCheckUpdate = true; _config.Save(); };
        sw.Unchecked += (_, _) => { _config.Config.AutoCheckUpdate = false; _config.Save(); };
        return sw;
    }

    private async void CheckUpdate()
    {
        _updateText.Text = "正在检查更新…";
        try
        {
            var result = await _updater.CheckForUpdateAsync();
            if (!result.Ok)
            {
                _updateText.Text = $"检查失败:{result.Error}(可稍后重试)";
                return;
            }
            if (!result.HasUpdate)
            {
                _updateText.Text = $"当前已是最新版本 {result.LocalVersion}";
                DialogKit.Success("当前已是最新版本", "检查更新", Window.GetWindow(this));
                return;
            }
            var manifest = result.Manifest!;
            _updateText.Text = $"发现新版本 {manifest.Version}";
            if (!DialogKit.Confirm($"发现新版本 {manifest.Version},是否立即下载并应用?\n更新前会自动备份当前版本。", "发现新版本", Window.GetWindow(this)))
                return;
            _updateText.Text = "正在应用更新…";
            var (ok, msg) = await _updater.ApplyUpdateAsync(manifest);
            _updateText.Text = ok ? "更新完成,重启后生效" : "更新失败:" + msg;
            if (ok) DialogKit.Success(msg, "更新完成", Window.GetWindow(this));
            else DialogKit.Error(msg, "更新失败", Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            _updateText.Text = "检查更新异常";
            DialogKit.Error("更新检查异常:" + ex.Message, owner: Window.GetWindow(this));
        }
    }

    private async void RunEnvCheck()
    {
        Shell()?.SetStatus("正在运行环境自检…");
        _envResultList.Children.Clear();
        try
        {
            await _envCheck.RunEnvironmentCheckAsync();
            foreach (var item in _envCheck.LastResult.Items)
            {
                var msg = UIKit.Sub(string.IsNullOrEmpty(item.Message) ? "" : $" — {item.Message}", 12);
                msg.TextTrimming = TextTrimming.CharacterEllipsis;   // 路径类长消息省略显示,悬停看完整值
                msg.MaxWidth = 480;
                msg.ToolTip = item.Message;
                var nameTb = UIKit.Text(item.Name, 13);
                nameTb.TextTrimming = TextTrimming.CharacterEllipsis;
                nameTb.ToolTip = item.Name;
                var row = UIKit.H(
                    UIKit.Text(item.Ok ? "✓" : "✗", 13, FontWeights.Bold, item.Ok ? "T.Success" : (item.Severity == SeverityLevel.Critical ? "T.Danger" : "T.Warning")),
                    nameTb,
                    msg);
                row.Margin = new Thickness(0, 5, 0, 5);
                _envResultList.Children.Add(row);
            }
            Shell()?.SetStatus(_envCheck.LastResult.AllOk ? "环境自检全部通过" : "环境自检发现问题", !_envCheck.LastResult.AllOk);
        }
        catch (Exception ex) { DialogKit.Error("自检异常:" + ex.Message, owner: Window.GetWindow(this)); }
    }

    private void OpenDir(string dir)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", dir)?.Dispose(); }
        catch (Exception ex) { DialogKit.Error("打开目录失败:" + ex.Message, owner: Window.GetWindow(this)); }
    }

    /// <summary>设置页按钮统一加大:加高 + MinWidth 拉长(字体/配色/点击逻辑不动)</summary>
    private static Button Lg(Button b, double minWidth = 180)
    {
        b.Height = 46;
        b.MinWidth = minWidth;
        return b;
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
