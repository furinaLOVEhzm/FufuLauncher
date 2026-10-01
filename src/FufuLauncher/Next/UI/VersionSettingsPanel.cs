// Copyright © FufuLauncher
//
// 版本设置分区(版本管理页第三分区,页内分区 + 叠放交叉过渡):
// 由版本卡「设置」按钮或侧边栏「版本管理 → 版本设置」进入;
// 不触发主页面路由,侧边高亮保持锁定在版本管理;
// 读写只针对所选游戏版本(顶部下拉可切换,版本卡进入时预选对应版本)。
// 双标签页:
//   ① 基础设置 —— 内存分配(拦截负数/0/不合理超大值)、Java 运行时、JVM 附加参数、游戏窗口大小;
//   ② JSON 补丁 · 修改加载器 —— 简易模式生成派生版本;高级模式对象级深度合并;
//      两种模式均只写新派生版本,原始 version.json 保持只读。
// UI 统一规格:全部按钮 32px 档位、下拉框 320×38 全局规格、卡片 pad 16/topGap 12。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace FufuLauncher.Next.UI;

public sealed class VersionSettingsPanel : UserControl
{
    private const int MemMin = 128;
    private const int MemMax = 1048576;   // 1TB,超过即视为不合理超大数值
    private const int BtnHeight = 32;     // 面板内按钮全局统一档位

    private readonly InstanceService _instances;
    private readonly JavaRuntimeService _javaRuntimes;
    private readonly LoaderVersionProvider _loaderProvider;
    private readonly VersionPatchService _patchService;
    private readonly MemoryMonitorService _memoryMonitor;   // 本机硬件探测:自动推荐内存预设

    // ---- 版本选择 ----
    private readonly ComboBox _instBox = UIKit.ComboBox();
    private readonly TextBlock _instBadge = UIKit.Sub("", 11.5);

    // ---- 标签页容器(叠放交叉过渡)----
    private readonly StackPanel _basicPanel = new();
    private readonly StackPanel _patchPanel = new();
    private (Border c1, TextBlock l1, Border c2, TextBlock l2) _tabs;

    // ---- Tab1 基础设置控件 ----
    private readonly CheckBox _memChk;
    private readonly TextBox _xmsBox = UIKit.TextBox();
    private readonly TextBox _xmxBox = UIKit.TextBox();
    private readonly ComboBox _javaBox = UIKit.ComboBox();
    private readonly TextBox _jvmArgsBox = UIKit.TextBox(placeholder: "留空就用全局默认");
    private readonly TextBox _widthBox = UIKit.TextBox();
    private readonly TextBox _heightBox = UIKit.TextBox();
    private readonly CheckBox _fullChk = new UIKit.ToggleSwitch("全屏启动");
    private readonly TextBlock _memHint = UIKit.Sub("", 11.5);   // 硬件自动推荐提示
    private List<InstalledJavaEntry> _javaOptions = new();
    private int _recXms, _recXmx;

    // ---- Tab2 简易模式控件 ----
    private static readonly string[] LoaderKinds = { "Forge", "Fabric", "NeoForge", "Quilt" };
    private readonly ComboBox _kindBox = UIKit.ComboBox();
    private readonly ComboBox _verBox = UIKit.ComboBox();
    // 2026-09-26 批3:加载器类型左侧的官方 Logo chip(与新装向导加载器卡观感统一;不动下拉尺寸)
    private readonly Border _kindChip = new()
    {
        CornerRadius = new CornerRadius(UIKit.R.Chip),
        Padding = new Thickness(8, 5, 10, 5),
        Margin = new Thickness(0, 0, 10, 0),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _verStatus = UIKit.Sub("", 11.5);
    private readonly Button _genBtn;
    private List<LoaderVersionEntry> _fetched = new();
    private int _fetchGen;   // 拉取代次,防快速切换时旧请求覆盖新结果

    // ---- Tab2 高级模式控件 ----
    private readonly TextBox _patchBox;

    public VersionSettingsPanel()
    {
        _instances = App.Services.GetRequiredService<InstanceService>();
        _javaRuntimes = App.Services.GetRequiredService<JavaRuntimeService>();
        _loaderProvider = App.Services.GetRequiredService<LoaderVersionProvider>();
        _patchService = App.Services.GetRequiredService<VersionPatchService>();
        _memoryMonitor = App.Services.GetRequiredService<MemoryMonitorService>();

        _memChk = new UIKit.ToggleSwitch("手动指定内存");
        _genBtn = PanelBtn("生成新版本", primary: true, ApplySimple);
        _patchBox = UIKit.TextBox("", placeholder: "普通玩家不用填这一项。高级用户可以粘贴 JSON 补丁内容");

        BuildUi();
        SelectTab(0);
    }

    // ==================== 对外入口 ====================

    /// <summary>2026-09-26 批3 收口:派生版本生成后的回调(宿主据此重扫本地列表并定位高亮新版本)</summary>
    public Action<string>? Derived { get; set; }

    /// <summary>进入本分区:重读版本列表并预选指定版本</summary>
    public void Enter(string? instanceId)
    {
        RefreshInstBox(instanceId);
        if (_instances.Instances.Count > 0) RefreshDetail();
    }

    // ==================== UI 构建 ====================

    /// <summary>面板内按钮统一工厂:32px 档位 + MinWidth 100,杜绝尺寸不一致</summary>
    private static Button PanelBtn(string text, bool primary, Action? onClick = null)
        => UIKit.Button(text, primary, onClick, height: BtnHeight);

    /// <summary>设置行右列统一宽度容器:各行右列宽一致 → 控件起点对齐,杜绝参差。
    /// 380 为实测下界:ToggleSwitch 129(54轨道+10间距+65文字) + 数值区 240 = 369,360 必挤</summary>
    private static Border Fix(UIElement c, double w = 380)
    {
        if (c is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Width = w, Child = c };
    }

    /// <summary>内存行:开关(固定宽) + 最低/最高数值(星列右对齐)。
    /// 用 Grid 而非 H——ToggleSwitch 实际 119px,H 排 381px 溢出 360 容器,
    /// Grid 的 Star 列自动吸收余量,任何窗口宽度下都不溢出。</summary>
    private UIElement MemRow()
    {
        var memVals = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                UIKit.Sub("最低", 12),
                _xmsBox,
                UIKit.Sub("MB  最高", 12),
                _xmxBox,
                UIKit.Sub("MB", 12)
            }
        };
        var grid = new Grid
        {
            Width = 380,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            Children = { _memChk.Col(0), memVals.Col(1) }
        };
        return grid;
    }

    private void BuildUi()
    {
        var header = UIKit.PageHeader("版本设置", "给选中的游戏版本单独调设置:内存、Java、窗口大小、模组加载器,只影响这个版本");

        // ---- 版本选择行:下拉全局统一规格(320×38),只保留对齐/间距属性 ----
        _instBox.VerticalAlignment = VerticalAlignment.Center;
        _instBox.SelectionChanged += (_, _) => RefreshDetail();
        _instBadge.VerticalAlignment = VerticalAlignment.Center;
        _instBadge.Margin = new Thickness(12, 0, 0, 0);
        _instBadge.TextTrimming = TextTrimming.CharacterEllipsis;

        // ---- 标签页切换条 ----
        var tab1 = TabChip(UIKit.Text("基础设置", 13, FontWeights.SemiBold));
        var tab2 = TabChip(UIKit.Text("修改加载器", 13, FontWeights.SemiBold));
        tab1.chip.MouseLeftButtonUp += (_, _) => SelectTab(0);
        tab2.chip.MouseLeftButtonUp += (_, _) => SelectTab(1);
        var tabBar = UIKit.H(tab1.chip, tab2.chip);
        tabBar.Margin = new Thickness(0, 14, 0, 0);
        _tabs = (tab1.chip, tab1.label, tab2.chip, tab2.label);

        BuildBasicPanel();
        BuildPatchPanel();

        var sectionGrid = new Grid { Children = { _basicPanel, _patchPanel } };
        Content = UIKit.V(header, tabBar, sectionGrid);
    }

    private void SelectTab(int idx)
    {
        MotionKit.SwapOverlay(new UIElement[] { _basicPanel, _patchPanel }, idx);
        ApplyChipStyle(_tabs.c1, _tabs.l1, idx == 0);
        ApplyChipStyle(_tabs.c2, _tabs.l2, idx == 1);
    }

    // ==================== Tab1 基础设置 ====================

    private void BuildBasicPanel()
    {
        // 数字输入框:仅数字可入,宽度上限防撑爆设置行
        _xmsBox.MinWidth = 80; _xmsBox.MaxWidth = 160;
        _xmxBox.MinWidth = 80; _xmxBox.MaxWidth = 160;
        _xmsBox.PreviewTextInput += DigitsOnly;
        _xmxBox.PreviewTextInput += DigitsOnly;
        _memChk.Checked += (_, _) => SyncMemEnable();
        _memChk.Unchecked += (_, _) => SyncMemEnable();

        _widthBox.MinWidth = 90; _widthBox.MaxWidth = 150;
        _heightBox.MinWidth = 90; _heightBox.MaxWidth = 150;
        _widthBox.PreviewTextInput += DigitsOnly; _heightBox.PreviewTextInput += DigitsOnly;
        _fullChk.VerticalAlignment = VerticalAlignment.Center;
        _fullChk.Margin = new Thickness(16, 0, 0, 0);
        _memChk.VerticalAlignment = VerticalAlignment.Center;
        _memChk.Margin = new Thickness(0, 0, 14, 0);   // 与数值区拉开,避免贴脸重叠
        _xmsBox.VerticalAlignment = VerticalAlignment.Center;
        _xmxBox.VerticalAlignment = VerticalAlignment.Center;
        _jvmArgsBox.ToolTip = "启动游戏时附加的技术参数,启动器已自动填好推荐值";
        _jvmArgsBox.MaxWidth = 356;   // 2026-09-25:无界 TextBox 期望宽≈500 溢出 Fix(380) 被卡片裁切,限宽根治
        _memHint.TextWrapping = TextWrapping.Wrap;   // 长推荐文案在 360 容器内换行,不溢出

        var modsBtn = PanelBtn("去管理模组", primary: false, () => Shell()?.Switch(ShellWindow.DeckKey.Mods));
        modsBtn.MinWidth = 140;
        var autoBtn = PanelBtn("帮我推荐", primary: false, ApplyAutoRecommend);
        autoBtn.MinWidth = 110;
        var saveBtn = PanelBtn("保存设置", primary: true, SaveBasic);
        saveBtn.MinWidth = 110;

        // 底部操作行:右对齐,统一 32px,按钮间 8px 呼吸间距
        autoBtn.Margin = new Thickness(0, 0, 8, 0);
        var actionsRow = new Border
        {
            Padding = new Thickness(0, 14, 0, 2),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { autoBtn, saveBtn }
            }
        };

        // 单卡片设置行列表:标题通俗化 + 浅色小字辅助说明,新手也能看懂。
        // 右列统一 Fix(360)容器:SettingRow 右列是 Auto 宽,各行控件宽不一致会让控件起点参差(此前游戏版本行被徽章撑到 560,错位根因)
        _basicPanel.Children.Add(UIKit.V(
            UIKit.Card(UIKit.V(
                UIKit.SettingRow("游戏版本", "要调整哪个版本,就选哪个", Fix(UIKit.V(_instBox, _instBadge))),
                UIKit.SettingRow("Java 环境", "选「自动」就行,启动器会帮你配好", Fix(_javaBox)),
                UIKit.SettingRow("游戏内存", "不打开开关时,启动器会按你电脑配置自动分配,不用操心", Fix(MemRow())),
                UIKit.SettingRowNoDivider("", "", Fix(_memHint)),
                UIKit.SettingRow("启动优化参数", "已经帮你填好推荐值,不懂不用改", Fix(_jvmArgsBox)),
                UIKit.SettingRow("游戏窗口大小", "游戏画面的宽和高,单位是像素",
                    Fix(UIKit.H(_widthBox, UIKit.Sub("×", 12), _heightBox, _fullChk))),
                UIKit.SettingRowNoDivider("模组管理", "给这个版本安装、管理模组", Fix(modsBtn))),
                pad: 16, topGap: 12),
            actionsRow));
    }

    private void SyncMemEnable() => _xmsBox.IsEnabled = _xmxBox.IsEnabled = _memChk.IsChecked == true;

    /// <summary>按所选版本回填基础设置各控件</summary>
    private void RefreshDetail()
    {
        if (_instBox.SelectedItem is not GameInstance inst)
        {
            _instBadge.Text = "";
            return;
        }
        string loader = string.IsNullOrEmpty(inst.ModLoader) ? "原版(无模组加载器)" : $"{inst.ModLoader} {inst.ModLoaderVersion}";
        _instBadge.Text = $"游戏 {inst.VersionId} · {loader}";
        _instBadge.ToolTip = $"实际运行配置:{inst.LoaderVersionId ?? inst.VersionId}";

        // 硬件自动适配:按本机总物理内存 + 游戏版本实时计算推荐内存
        _recXmx = RecommendXmxMb(inst);
        _recXms = _recXmx;
        _memChk.IsChecked = inst.UseCustomMemory;
        _xmsBox.Text = (inst.UseCustomMemory && inst.Xms > 0 ? inst.Xms : _recXms).ToString();
        _xmxBox.Text = (inst.UseCustomMemory && inst.Xmx > 0 ? inst.Xmx : _recXmx).ToString();
        SyncMemEnable();
        var mi = _memoryMonitor.GetCurrent();
        _memHint.Text = $"你的电脑内存 {mi.TotalGb:0.0} GB,建议给游戏分 {_recXmx} MB"
                        + (inst.UseCustomMemory ? "(已手动指定,启动时按你填的值来)" : "(没开手动开关,启动时自动按这个来)");

        _javaBox.Items.Clear();
        _javaBox.Items.Add("自动选择(推荐)");
        try { _javaOptions = _javaRuntimes.ListInstalledRuntimes().Where(r => r.Status == "已就绪").ToList(); }
        catch (Exception ex) { App.WriteAppLog($"[版本设置] 运行时列表加载失败:{ex.Message}"); _javaOptions = new List<InstalledJavaEntry>(); }
        foreach (var r in _javaOptions) _javaBox.Items.Add($"{r.Name} · {r.Architecture}");
        int recMajor = inst.JavaMajorVersion > 0 ? inst.JavaMajorVersion : JavaRuntimeService.RecommendJavaMajor(inst.VersionId);
        int sel;
        if (string.IsNullOrEmpty(inst.JavaPath))
        {
            sel = _javaOptions.FindIndex(r => ParseJavaMajor(r.MajorVersion) == recMajor) + 1;
            if (sel <= 0) sel = 0;
        }
        else
        {
            sel = _javaOptions.FindIndex(r => r.JavaExe == inst.JavaPath) + 1;
        }
        _javaBox.SelectedIndex = sel >= 0 && sel < _javaBox.Items.Count ? sel : 0;

        // 按游戏版本 + 本机硬件自动填充推荐 JVM 参数(仅原值为空时填,不覆盖用户已有参数)
        _jvmArgsBox.Text = string.IsNullOrWhiteSpace(inst.ExtraJvmArgs)
            ? MemoryMonitorService.BuildMultiCoreGcArgs(MemoryMonitorService.GetPhysicalCoreCount(), recMajor)
            : inst.ExtraJvmArgs;
        _widthBox.Text = inst.Width.ToString();
        _heightBox.Text = inst.Height.ToString();
        _fullChk.IsChecked = inst.Fullscreen;

        FetchLoaderVersions();
    }

    private void SaveBasic()
    {
        if (_instBox.SelectedItem is not GameInstance inst)
        {
            DialogKit.Info("请先安装并选择一个游戏版本", owner: Window.GetWindow(this));
            return;
        }
        int xms = 0, xmx = 0;
        if (_memChk.IsChecked == true)
        {
            if (!TryParseMem(_xmsBox.Text, out xms, out string e1)) { DialogKit.Warn("最低内存" + e1, owner: Window.GetWindow(this)); return; }
            if (!TryParseMem(_xmxBox.Text, out xmx, out string e2)) { DialogKit.Warn("最高内存" + e2, owner: Window.GetWindow(this)); return; }
            if (xms > xmx) { DialogKit.Warn("最低内存不能大于最高内存", owner: Window.GetWindow(this)); return; }
        }
        if (!int.TryParse(_widthBox.Text.Trim(), out int w) || w < 320) { DialogKit.Warn("窗口宽度不能小于 320", owner: Window.GetWindow(this)); return; }
        if (!int.TryParse(_heightBox.Text.Trim(), out int h) || h < 240) { DialogKit.Warn("窗口高度不能小于 240", owner: Window.GetWindow(this)); return; }

        try
        {
            inst.UseCustomMemory = _memChk.IsChecked == true;
            if (inst.UseCustomMemory) { inst.Xms = xms; inst.Xmx = xmx; }
            inst.JavaPath = _javaBox.SelectedIndex > 0 && _javaBox.SelectedIndex - 1 < _javaOptions.Count
                ? _javaOptions[_javaBox.SelectedIndex - 1].JavaExe : "";
            inst.ExtraJvmArgs = _jvmArgsBox.Text.Trim();
            inst.Width = w; inst.Height = h;
            inst.Fullscreen = _fullChk.IsChecked == true;
            _instances.SaveInstance(inst);
            App.WriteAppLog($"[版本设置] 已保存:{inst.Name} 自定义内存={(inst.UseCustomMemory ? $"{inst.Xms}/{inst.Xmx}MB" : "关")} Java={(string.IsNullOrEmpty(inst.JavaPath) ? "自动" : inst.JavaPath)}");
            Shell()?.SetStatus($"版本设置已保存:{inst.Name}");
            DialogKit.Success($"已保存「{inst.Name}」的设置,下次启动游戏时生效", owner: Window.GetWindow(this));
        }
        catch (Exception ex) { DialogKit.Error("保存失败: " + ex.Message, owner: Window.GetWindow(this)); }
    }

    /// <summary>一键应用硬件推荐:内存预设、按版本匹配的已就绪 Java、按版本+硬件生成的 JVM 参数,均回填供用户确认保存</summary>
    private void ApplyAutoRecommend()
    {
        if (_instBox.SelectedItem is not GameInstance inst)
        {
            DialogKit.Info("请先安装并选择一个游戏版本", owner: Window.GetWindow(this));
            return;
        }
        _recXmx = RecommendXmxMb(inst);
        _recXms = _recXmx;
        _xmsBox.Text = _recXms.ToString();
        _xmxBox.Text = _recXmx.ToString();
        int recMajor = inst.JavaMajorVersion > 0 ? inst.JavaMajorVersion : JavaRuntimeService.RecommendJavaMajor(inst.VersionId);
        int sel = _javaOptions.FindIndex(r => ParseJavaMajor(r.MajorVersion) == recMajor) + 1;
        _javaBox.SelectedIndex = sel > 0 && sel < _javaBox.Items.Count ? sel : 0;
        _jvmArgsBox.Text = MemoryMonitorService.BuildMultiCoreGcArgs(MemoryMonitorService.GetPhysicalCoreCount(), recMajor);
        var mi = _memoryMonitor.GetCurrent();
        _memHint.Text = $"你的电脑内存 {mi.TotalGb:0.0} GB,建议给游戏分 {_recXmx} MB"
                        + (_memChk.IsChecked == true ? "(已手动指定,启动时按你填的值来)" : "(没开手动开关,启动时自动按这个来)");
        App.WriteAppLog($"[版本设置] 一键应用推荐:{inst.Name} 内存={_recXms}/{_recXmx}MB Java=Java {recMajor} JVM参数已按硬件生成");
        DialogKit.Success($"已按你的电脑配置帮你填好了推荐设置:\n内存 {_recXms}/{_recXmx} MB、Java {recMajor}、启动优化参数已生成。\n检查一下没问题,点「保存设置」就生效。", owner: Window.GetWindow(this));
    }

    /// <summary>推荐最大堆内存:总物理内存推荐曲线为基准;大型整合包建议值安全上限允许时上调;256MB 对齐减少 GC 碎片</summary>
    private int RecommendXmxMb(GameInstance inst)
    {
        int rec = _memoryMonitor.RecommendByTotalMb();
        if (inst.RecommendedMemoryMb > rec)
        {
            int safe = _memoryMonitor.GetSafeAllocMb();
            if (safe >= inst.RecommendedMemoryMb) rec = inst.RecommendedMemoryMb;
        }
        return Math.Max(256, (rec / 256) * 256);
    }

    // ==================== Tab2 JSON 补丁 · 修改加载器 ====================

    private void BuildPatchPanel()
    {
        foreach (var k in LoaderKinds) _kindBox.Items.Add(k);
        _kindBox.SelectedIndex = 0;
        _kindBox.SelectionChanged += (_, _) => { UpdateKindChip(); FetchLoaderVersions(); };
        UpdateKindChip();
        _genBtn.MinWidth = 140;

        _patchBox.AcceptsReturn = true;
        _patchBox.AcceptsTab = true;
        _patchBox.TextWrapping = TextWrapping.NoWrap;
        _patchBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _patchBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _patchBox.MinHeight = 140;
        _patchBox.FontFamily = new FontFamily("Consolas");

        var importBtn = PanelBtn("导入补丁文件", primary: false, ImportPatchFile);
        importBtn.MinWidth = 120;
        var applyBtn = PanelBtn("应用补丁", primary: true, ApplyPatch);
        applyBtn.MinWidth = 120;
        importBtn.Margin = new Thickness(0, 0, 8, 0);
        var patchBtnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { importBtn, applyBtn }
        };
        var patchBtnWrap = new Border { Padding = new Thickness(0, 12, 0, 0), Child = patchBtnRow };
        var genWrap = new Border
        {
            Padding = new Thickness(0, 14, 0, 2),
            Child = new Border { HorizontalAlignment = HorizontalAlignment.Right, Child = _genBtn }
        };

        // 简易模式(主区域,放上方):通俗标题 + 浅色小字说明用途
        _patchPanel.Children.Add(UIKit.Card(UIKit.V(
            UIKit.Text("简单方式 · 安装模组加载器", 13, FontWeights.SemiBold),
            UIKit.Sub("想玩模组需要先装加载器(比如 Forge、Fabric)。选好后点下面的按钮,会生成一个带加载器的新版本,原来的版本不动", 11.5),
            UIKit.SettingRow("加载器类型", "常用的有 Forge 和 Fabric,看你装的模组要求哪种", UIKit.H(_kindChip, _kindBox)),
            UIKit.SettingRow("加载器版本", "一般选第一个(最新稳定版)就行", UIKit.V(_verBox, _verStatus)),
            genWrap), pad: 16, topGap: 12));

        // 高级模式(视觉弱化,放下方)
        var advTitle = UIKit.Text("高级方式 · JSON 补丁(普通玩家不用管)", 12, FontWeights.Medium);
        advTitle.SetResourceReference(TextBlock.ForegroundProperty, "T.ForegroundDim");
        _patchPanel.Children.Add(UIKit.Card(UIKit.V(
            advTitle,
            _patchBox,
            patchBtnWrap), pad: 16, topGap: 12));
    }

    /// <summary>刷新加载器类型 chip:官方 Logo + 名称(未知加载器 IconKit 自动回退字母徽章)</summary>
    private void UpdateKindChip()
    {
        int idx = Math.Max(0, _kindBox.SelectedIndex);
        if (idx >= LoaderKinds.Length) idx = 0;
        var logo = IconKit.LoaderLogo(LoaderKinds[idx].ToLowerInvariant(), 18);
        logo.Margin = new Thickness(0, 0, 6, 0);
        var label = UIKit.Text(LoaderKinds[idx], 12, FontWeights.Medium);
        label.VerticalAlignment = VerticalAlignment.Center;
        _kindChip.Child = UIKit.H(logo, label);
        _kindChip.BorderThickness = new Thickness(1);
        _kindChip.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");
        _kindChip.SetResourceReference(Border.BorderBrushProperty, "T.Border");
    }

    /// <summary>按所选版本拉取加载器版本列表(镜像/缓存加速,代次守护防旧请求覆盖)</summary>
    private async void FetchLoaderVersions()
    {
        try
        {
            if (_instBox.SelectedItem is not GameInstance inst) return;
            int gen = ++_fetchGen;
            string kind = LoaderKinds[_kindBox.SelectedIndex].ToLowerInvariant();
            if (!LoaderVersionProvider.IsLoaderSupportedFor(kind, inst.VersionId, out string reason))
            {
                _fetched.Clear(); _verBox.Items.Clear();
                _verStatus.Text = reason;
                return;
            }
            _verStatus.Text = "正在查询可用的加载器版本…";
            _verBox.IsEnabled = false;
            var res = await _loaderProvider.GetVersionsAsync(kind, inst.VersionId);
            if (gen != _fetchGen) return;   // 已有更新的请求,丢弃陈旧结果
            _verBox.IsEnabled = true;
            _verBox.Items.Clear(); _fetched.Clear();
            if (!res.Success) { _verStatus.Text = "查询失败:" + res.Error; return; }
            _fetched = res.Versions;
            foreach (var v in _fetched) _verBox.Items.Add(v.Version + (v.IsStable ? "" : "(测试版)"));
            if (_verBox.Items.Count > 0) _verBox.SelectedIndex = 0;
            _verStatus.Text = _fetched.Count == 0 ? "这个加载器暂时没有适配当前游戏版本" : $"共找到 {_fetched.Count} 个版本 · 来源:{res.SourceLabel}";
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本设置] 操作异常:" + ex.Message);
            Shell()?.SetStatus("操作失败,详见日志", warning: true);
        }
    }

    private async void ApplySimple()
    {
        try
        {
            if (_instBox.SelectedItem is not GameInstance inst)
            {
                DialogKit.Info("请先安装并选择一个游戏版本", owner: Window.GetWindow(this));
                return;
            }
            if (_verBox.SelectedIndex < 0 || _verBox.SelectedIndex >= _fetched.Count)
            {
                DialogKit.Warn("请先选一个加载器版本", owner: Window.GetWindow(this));
                return;
            }
            string kind = LoaderKinds[_kindBox.SelectedIndex];
            string ver = _fetched[_verBox.SelectedIndex].Version;
            // 高危确认:生成新版本前给用户一次反悔机会(原文案通俗化)
            if (!DialogKit.Confirm(
                    $"要给「{inst.Name}」安装 {kind} {ver},会生成一个带加载器的新版本。\n原来的版本和文件都不会被改动,可以放心。\n要继续吗?",
                    "操作确认", "继续", danger: true, owner: Window.GetWindow(this))) return;

            _genBtn.IsEnabled = false;
            PatchResult result;
            try
            {
                // 2026-09-26 批3:进度文案细化——安装链无百分比回调,只更新阶段说明(不加假进度条)
                result = DialogKit.RunWithProgress($"正在安装 {kind} {ver}",
                    async s =>
                    {
                        s($"正在为「{inst.Name}」下载并写入 {kind} {ver} 加载器文件,完成后会生成一个新的派生版本…");
                        return await _patchService.ApplyLoaderPatchAsync(inst.Id, kind.ToLowerInvariant(), ver);
                    }, Window.GetWindow(this));
            }
            catch (Exception ex) { _genBtn.IsEnabled = true; DialogKit.Error("安装没成功: " + ex.Message, owner: Window.GetWindow(this)); return; }
            _genBtn.IsEnabled = true;

            if (result.Success)
            {
                AfterPatchApplied(result.NewVersionId ?? "");
                TryPreloadDeps(result.NewVersionId ?? "");   // Axolotl-6:改加载器后台静默补齐依赖
                DialogKit.Success($"装好了!已生成新版本:{result.NewVersionId}\n「{inst.Name}」已经用上这个加载器,直接启动就能玩模组", owner: Window.GetWindow(this));
            }
            else if (result.Error != "已取消")
            {
                DialogKit.Error(result.Error ?? "安装没成功", owner: Window.GetWindow(this));
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本设置] 操作异常:" + ex.Message);
            Shell()?.SetStatus("操作失败,详见日志", warning: true);
        }
    }

    /// <summary>Axolotl-6:改加载器生成新派生版本后,后台静默补齐依赖(开关在「版本工具 → 环境校验」)</summary>
    private static void TryPreloadDeps(string newVersionId)
    {
        if (string.IsNullOrEmpty(newVersionId)) return;
        try
        {
            var cfg = App.Services.GetRequiredService<ConfigService>();
            if (!cfg.Config.PreloadDependencies) return;
            var preload = App.Services.GetRequiredService<DependencyPreloadService>();
            preload.Enabled = true;
            if (preload.EnqueueVersionDeps(newVersionId, "改加载器后后台补齐依赖"))
                App.WriteAppLog($"[版本设置] ⇢ 已入队后台补齐依赖:{newVersionId}");
        }
        catch (Exception ex) { App.WriteAppLog($"[版本设置] 预加载入队失败:{ex.Message}"); }
    }

    private void ImportPatchFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择补丁文件",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try { _patchBox.Text = File.ReadAllText(dlg.FileName); }
        catch (Exception ex) { DialogKit.Error("补丁文件读取失败: " + ex.Message, owner: Window.GetWindow(this)); }
    }

    private void ApplyPatch()
    {
        if (_instBox.SelectedItem is not GameInstance inst)
        {
            DialogKit.Info("请先安装并选择一个游戏版本", owner: Window.GetWindow(this));
            return;
        }
        if (string.IsNullOrWhiteSpace(_patchBox.Text))
        {
            DialogKit.Warn("要先在框里粘贴补丁内容,或者点「导入文件」", owner: Window.GetWindow(this));
            return;
        }
        // 高危确认:补丁会改动启动关键配置,落盘前给一次反悔机会(原文案通俗化)
        if (!DialogKit.Confirm(
                $"要把补丁应用到「{inst.Name}」,会生成一个新版本。\n原来的版本不会被改动。\n要继续吗?",
                "操作确认", "继续", danger: true, owner: Window.GetWindow(this))) return;

        var result = _patchService.ApplyJsonPatch(inst.Id, _patchBox.Text);
        if (result.Success)
        {
            _patchBox.Text = "";
            AfterPatchApplied(result.NewVersionId ?? "");
            DialogKit.Success($"补丁应用成功,已生成新版本:{result.NewVersionId}\n「{inst.Name}」已经切换到新版本,直接启动即可", owner: Window.GetWindow(this));
        }
        else if (result.Error != "已取消")
        {
            DialogKit.Error(result.Error ?? "补丁没有应用成功", owner: Window.GetWindow(this));
        }
    }

    /// <summary>补丁落盘后:重扫版本列表 + 定位到新派生版本 + 回填本分区徽章与生效配置,
/// 并通知宿主(InstancesDeck)重扫本地列表并高亮该版本(2026-09-26 批3 收口)</summary>
    private void AfterPatchApplied(string newVersionId)
    {
        string keepId = (_instBox.SelectedItem as GameInstance)?.Id ?? "";
        RefreshInstBox(string.IsNullOrEmpty(newVersionId) ? keepId : newVersionId);
        RefreshDetail();
        App.WriteAppLog($"[版本设置] 派生版本已生效:{newVersionId}");
        if (!string.IsNullOrEmpty(newVersionId))
        {
            Shell()?.SetStatus($"已生成新版本:{newVersionId}");
            Derived?.Invoke(newVersionId);
        }
    }

    // ==================== 内部工具 ====================

    /// <summary>重读版本下拉(保留/预选指定版本);空列表时禁用并占位提示</summary>
    private void RefreshInstBox(string? wantId)
    {
        _instances.RefreshInstances();
        string prevId = (_instBox.SelectedItem as GameInstance)?.Id ?? "";
        _instBox.Items.Clear();
        foreach (var inst in _instances.Instances) _instBox.Items.Add(inst);
        if (_instances.Instances.Count == 0)
        {
            _instBox.Items.Add("(暂无本地版本)");
            _instBox.SelectedIndex = 0;
            _instBox.IsEnabled = false;
            _instBadge.Text = "";
            return;
        }
        _instBox.IsEnabled = true;
        string target = !string.IsNullOrEmpty(wantId) ? wantId! : prevId;
        int idx = _instances.Instances.FindIndex(i => i.Id == target);
        _instBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    /// <summary>内存输入校验:拦截空值、非整数、负数、0、过小值与不合理超大值(>1TB)</summary>
    private static bool TryParseMem(string text, out int mb, out string err)
    {
        mb = 0;
        string s = text.Trim();
        if (s.Length == 0) { err = "还没填数字"; return false; }
        if (!int.TryParse(s, out mb)) { err = "要填整数,不能有小数、字母或负号"; return false; }
        if (mb <= 0) { err = "要大于 0"; return false; }
        if (mb > MemMax) { err = $"太大了,不能超过 {MemMax} MB"; return false; }
        if (mb < MemMin) { err = $"太小了,至少 {MemMin} MB"; return false; }
        err = "";
        return true;
    }

    /// <summary>解析运行时条目的主版本号("21" / "17.0.9" 等 → int,失败返回 0)</summary>
    private static int ParseJavaMajor(string v)
    {
        string s = v.Trim();
        int dot = s.IndexOf('.');
        if (dot > 0) s = s.Substring(0, dot);
        return int.TryParse(s, out int m) ? m : 0;
    }

    /// <summary>输入层过滤:仅允许数字字符</summary>
    private static void DigitsOnly(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(c => !char.IsDigit(c));

    /// <summary>标签页按钮壳(下划线指示选中态)</summary>
    private static (Border chip, TextBlock label) TabChip(TextBlock label)
    {
        var underline = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(2, 4, 2, 0) };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Child = UIKit.V(label, underline)
        };
        chip.Tag = underline;
        return (chip, label);
    }

    /// <summary>标签页选中态:主色下划线 + 正常字色;未选中:透明下划线 + 暗字色</summary>
    private static void ApplyChipStyle(Border chip, TextBlock label, bool active)
    {
        if (chip.Tag is Border underline)
        {
            if (active) underline.SetResourceReference(Border.BackgroundProperty, "T.Primary");
            else underline.Background = Brushes.Transparent;
        }
        label.SetResourceReference(TextBlock.ForegroundProperty, active ? "T.Foreground" : "T.ForegroundDim");
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
