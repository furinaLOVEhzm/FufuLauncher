// FufuLauncher - 模组管理 Deck
// Copyright © FufuLauncher
//
// 二级双区结构:
// A. 管理模组:按所选游戏版本的 mods 目录加载(ModManagerService),
//    启用/禁用(.jar.disabled 重命名,不删文件)、删除、定位文件、详情弹窗、
//    兼容体检(冲突 + 缺失依赖)、本地筛选、多选导入 / 拖拽导入;
// B. 下载模组:Modrinth 在线搜索(自动带入所选版本的 MC 版本与加载器过滤),
//    热门模组板块(全站下载排行卡片流,封面图展示,可换一批),
//    一键安装含依赖解析(InstallModWithDependenciesAsync),装完自动回流管理区。

using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FufuLauncher.Services;
using FufuLauncher.Theme;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class ModsDeck : UserControl
{
    private readonly ModManagerService _mods;
    private readonly ModrinthService _modrinth;
    private readonly InstanceService _instances;
    private readonly ConfigService _config;
    private readonly ModSearchService _search;       // LauncherX-2 搜索增强(名称/modid/作者 + 输入即搜防抖)
    private static readonly HttpClient _zhHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, string> _zhCache = new();
    private static readonly SemaphoreSlim _zhSem = new(4, 4);   // 翻译并发限制
    private readonly ModBatchService _batch;         // BlockHelm-2 本地模组批量管理
    private readonly ModDiagnosticsService _diag;    // LauncherX-7 更新聚合

    // 双区容器
    private readonly StackPanel _manageSection = new();
    private readonly StackPanel _downloadSection = new();

    // 管理区
    private readonly ComboBox _instanceBox = UIKit.ComboBox();
    private readonly TextBlock _ctxBadgeHost = UIKit.Sub("", 12);
    private readonly StackPanel _modList = new();
    private readonly Border _emptyHint;
    private readonly TextBlock _modCount = UIKit.Sub("", 12);
    private readonly TextBox _filterBox = UIKit.TextBox(placeholder: "筛选本地模组(名称 / modid / 作者)");
    private readonly TextBlock _compatText = UIKit.Sub("", 12);
    private readonly UIElement _compatCard;

    // 侧边预览 / 批量管理 / 更新聚合 / 悬浮卡(LauncherX-1 / BlockHelm-2 / LauncherX-7 / Axolotl-7)
    private readonly ModPreviewPanel _modPreview = new();
    private readonly UIKit.ToggleSwitch _onlyDisabledChk = new("只看禁用", false);
    private readonly StackPanel _batchBarHost = new();
    private readonly TextBlock _selCount = UIKit.Sub("", 12);
    private readonly StackPanel _updateHost = new();
    private readonly TextBlock _updateStatus = UIKit.Sub("", 12);
    private readonly FrameworkElement _updateCard;
    private readonly Popup _hoverPop = new()
    {
        Placement = PlacementMode.Relative,
        AllowsTransparency = true,
        StaysOpen = true,
        Focusable = false
    };
    // 悬浮卡防抖(修闪烁):鼠标划过不闪。延迟开(180ms)、延迟关(220ms),
    // 且鼠标可从卡片移进弹窗本体不立即关;开关完全由两个定时器驱动,不再依赖 StaysOpen。
    private readonly DispatcherTimer _hoverOpenTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _hoverCloseTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(220) };
    // 本地筛选防抖(250ms):RenderModList 会全量 Clear+重建列表,击键无防抖会输入卡顿/闪烁
    private readonly DispatcherTimer _filterDebounce = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private UIElement? _hoverAnchor;
    private ModInfo? _hoverMod;
    // 模组行图标(Modrinth 在线拉取,内存缓存;按 modid/文件名做键,查过没图的不再重查)
    private readonly Dictionary<string, ImageSource?> _modIconCache = new();
    private readonly HashSet<string> _modIconTried = new();
    private readonly SemaphoreSlim _iconSem = new(12, 12);  // 12 路并发拉图标,Modrinth 限流内可接受

    // 下载区
    private readonly ComboBox _dlInstanceBox = UIKit.ComboBox();
    private readonly TextBlock _dlBadgeHost = UIKit.Sub("", 12);
    private readonly TextBox _searchBox = UIKit.TextBox(placeholder: "搜索模组名称,如 Sodium");
    private readonly StackPanel _searchResults = new();
    private readonly TextBlock _searchStatus = UIKit.Sub("", 12);
    private readonly ComboBox _sortBox = UIKit.ComboBox();        // 搜索排序方式
    private readonly UniformGrid _hotGrid = new() { Columns = 3 };  // 热门模组卡片列表(3 / 4 列可切,等宽铺满)
    private readonly TextBlock _hotStatus = UIKit.Sub("", 12);
    private readonly List<(Border Pill, TextBlock Label, int Cols)> _colSegs = new();   // 列数分段切换器
    private int _cardColumns = 3;      // 当前卡片列数(持久化到 AppConfig.ModsCardColumns)

    private List<ModInfo> _loaded = new();
    private int _searchGen;      // 搜索代次:快速连续搜索时旧结果不得覆盖新结果
    private bool _searching;
    private bool _requeryPending;   // 实时搜索进行中又来了新关键词,待当前查询结束后补跑一次
    private bool _batchMode;        // 批量管理模式(BlockHelm-2)
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _checkingUpdates;  // 检查更新进行中(LauncherX-7)
    private List<ModUpdateInfo> _updateResults = new();
    private int _hotOffset;      // 热门翻页偏移(换一批)
    private int _hotGen;
    private bool _hotLoading;
    private bool _hotInited;     // 热门板块首次进入懒加载一次

    public ModsDeck()
    {
        _mods = App.Services.GetRequiredService<ModManagerService>();
        _modrinth = App.Services.GetRequiredService<ModrinthService>();
        _instances = App.Services.GetRequiredService<InstanceService>();
        _config = App.Services.GetRequiredService<ConfigService>();
        _search = App.Services.GetRequiredService<ModSearchService>();
        _batch = App.Services.GetRequiredService<ModBatchService>();
        _diag = App.Services.GetRequiredService<ModDiagnosticsService>();
        _cardColumns = _config.Config.ModsCardColumns == 4 ? 4 : 3;   // 只认 3 / 4 两档,脏值回退 3
        _hotGrid.Columns = _cardColumns;
        // 悬浮卡防抖接线(修闪烁):到点才开弹窗;鼠标移进弹窗本体时取消关闭,移开再延迟关
        _hoverOpenTimer.Tick += (_, _) =>
        {
            _hoverOpenTimer.Stop();
            _hoverCloseTimer.Stop();
            if (_hoverAnchor == null || _hoverMod == null) return;
            var card = ModPreviewPanel.BuildMetaCard(_hoverMod);
            card.MouseEnter += (_, _) => _hoverCloseTimer.Stop();
            card.MouseLeave += (_, _) => _hoverCloseTimer.Start();
            _hoverPop.Child = card;
            // 悬浮卡钉死在主窗口右下角:不再跟随卡片位置/翻转,避免跟随弹出来回跳闪。
            // 水平贴右边距 24;垂直在 Loaded 后按弹窗实际高度贴底,内容多少都坐右下角。
            var shell = Window.GetWindow(this);
            _hoverPop.PlacementTarget = shell;
            if (shell != null)
            {
                const double margin = 24;
                _hoverPop.HorizontalOffset = shell.ActualWidth - 340 - margin;
                card.Loaded += (_, _) =>
                    _hoverPop.VerticalOffset = shell.ActualHeight - card.ActualHeight - margin;
            }
            _hoverPop.IsOpen = true;
        };
        _hoverCloseTimer.Tick += (_, _) => { _hoverCloseTimer.Stop(); _hoverPop.IsOpen = false; };
        _filterDebounce.Tick += (_, _) => { _filterDebounce.Stop(); RenderModList(); };

        _emptyHint = UIKit.EmptyHint("该版本还没有安装任何模组,可切到「下载模组」在线搜索安装");
        _compatCard = UIKit.Card(_compatText, pad: 16, topGap: 10);
        _compatText.TextWrapping = TextWrapping.Wrap;
        _compatCard.Visibility = Visibility.Collapsed;

        // 侧边预览面板接线(LauncherX-1):诊断/换版本装完后回流刷新本地列表
        _modPreview.RefreshRequested += () => Dispatcher.BeginInvoke(RefreshMods);
        // 只看禁用筛选(Celestial-8)
        _onlyDisabledChk.VerticalAlignment = VerticalAlignment.Center;
        _onlyDisabledChk.ToolTip = "只显示被禁用(灰色标记)的模组,方便集中排查";
        _onlyDisabledChk.Checked += (_, _) => RenderModList();
        _onlyDisabledChk.Unchecked += (_, _) => RenderModList();
        // 更新聚合面板(LauncherX-7):默认收起,检查更新后填充显示
        _updateStatus.TextWrapping = TextWrapping.Wrap;
        _updateCard = UIKit.Card(UIKit.V(_updateStatus, _updateHost), pad: 16, topGap: 10);
        _updateCard.Visibility = Visibility.Collapsed;

        BuildManageSection();
        BuildDownloadSection();

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Grid { Children = { _manageSection, _downloadSection } }
        };
        ShowSection(0);
        RefreshInstances();

        // 热门模组首次进入懒加载(异步不阻塞页面构建)
        Loaded += (_, _) => { if (!_hotInited) { _hotInited = true; LoadHotMods(false); } };

        // 拖拽导入:把 .jar/.zip 直接拖进页面即可导入到管理区所选版本的 mods 目录
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;

        // 其它页新增/删除版本后,两区版本下拉即时联动
        _instances.InstancesChanged += OnInstancesChanged;
        Unloaded += (_, _) =>
        {
            _instances.InstancesChanged -= OnInstancesChanged;
            // 三个定时器全停 + 关悬浮窗:DispatcherTimer 强引用页面不 GC,且 hover 弹窗
            // 在切页后仍可能弹出(用户切走页面突然冒出悬浮窗 = 交互 bug)
            _hoverOpenTimer.Stop();
            _hoverCloseTimer.Stop();
            _filterDebounce.Stop();
            _hoverPop.IsOpen = false;
        };
    }

    /// <summary>二级菜单入口:0=管理模组 1=下载模组(叠放交叉过渡)</summary>
    public void ShowSection(int idx)
    {
        MotionKit.SwapOverlay(new UIElement[] { _manageSection, _downloadSection }, idx);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop)
            && ((string[])e.Data.GetData(DataFormats.FileDrop))
                .Any(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                       || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = ((string[])e.Data.GetData(DataFormats.FileDrop))
            .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0)
        {
            DialogKit.Info("仅支持拖入 .jar / .zip 格式的模组文件", "拖拽导入", Window.GetWindow(this));
            return;
        }
        ImportFiles(files);
    }

    private void OnInstancesChanged() => Dispatcher.BeginInvoke(RefreshInstances);

    /// <summary>页内按钮统一放大(字体/配色/点击逻辑不动)</summary>
    private static Button Big(Button b, double minWidth = 150)
    {
        b.Height = 42;
        b.MinWidth = minWidth;
        return b;
    }

    // ==================== 管理区 ====================

    private void BuildManageSection()
    {
        var importBtn = Big(UIKit.Button("导入模组文件", primary: true, onClick: OnImportFile), 160);
        var openBtn = Big(UIKit.Button("打开模组目录", primary: false, onClick: () => _mods.OpenModsFolder()), 160);
        var refreshBtn = Big(UIKit.Button("刷新", primary: false, onClick: RefreshMods), 110);
        // 检查更新(LauncherX-7) + 批量管理(BlockHelm-2)入口
        var updateBtn = Big(UIKit.Button("检查更新", primary: false, onClick: () => _ = CheckUpdatesAsync()), 130);
        updateBtn.ToolTip = "联网检查当前版本所有模组是否有新版本,结果汇总到下方「模组更新」面板";
        var batchBtn = Big(UIKit.Button("批量管理", primary: false, onClick: ToggleBatchMode), 130);
        batchBtn.ToolTip = "进入多选模式:批量启用 / 禁用 / 导出模组包 / 迁移到别的版本";
        _manageSection.Children.Add(UIKit.PageHeader("管理模组", "本地模组的启用 / 禁用 / 删除与兼容体检", importBtn, openBtn, refreshBtn, updateBtn, batchBtn));

        // 上下文工具栏卡:作用版本 + 本地筛选
        _instanceBox.SelectionChanged += (_, _) => OnInstanceSelected();
        _ctxBadgeHost.VerticalAlignment = VerticalAlignment.Center;
        var ctxRow = UIKit.H(UIKit.Sub("作用于版本", 12), _instanceBox, _ctxBadgeHost);
        _instanceBox.Margin = new Thickness(10, 0, 12, 0);

        _filterBox.Width = 300;
        _filterBox.VerticalAlignment = VerticalAlignment.Center;
        _filterBox.TextChanged += (_, _) => { _filterDebounce.Stop(); _filterDebounce.Start(); };
        // 排版:搜索/热门状态行限宽,长文本换行不撑破卡片(Sub 已默认 Wrap)
        _searchStatus.MaxWidth = 560;
        _hotStatus.MaxWidth = 560;
        _dlBadgeHost.MaxWidth = 420;
        _modCount.VerticalAlignment = VerticalAlignment.Center;
        var filterLabel = UIKit.Sub("筛选", 12);
        filterLabel.Margin = new Thickness(0, 0, 10, 0);
        filterLabel.VerticalAlignment = VerticalAlignment.Center;
        _onlyDisabledChk.Margin = new Thickness(2, 0, 12, 0);
        var filterRow = UIKit.H(filterLabel, _filterBox, _onlyDisabledChk, _modCount);
        _filterBox.Margin = new Thickness(0, 0, 12, 0);

        _manageSection.Children.Add(UIKit.Card(
            UIKit.V(ctxRow, new Border { Padding = new Thickness(0, 12, 0, 0), Child = filterRow }), pad: 18, topGap: 12));
        ctxRow.Margin = new Thickness(0, 2, 0, 0);

        _manageSection.Children.Add(_batchBarHost);   // 批量操作条(进入批量模式后填充)
        _manageSection.Children.Add(_updateCard);     // 更新聚合面板(检查更新后显示)

        // 列表区改两列:左=兼容卡/空态/模组列表(原样),右=侧边预览(LauncherX-1,不弹全屏窗)
        var listCol = UIKit.V(_compatCard, _emptyHint, _modList);
        var listArea = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { listCol, _modPreview.Col(1) }
        };
        _manageSection.Children.Add(listArea);
    }

    // ==================== 下载区 ====================

    private void BuildDownloadSection()
    {
        var searchBtn = Big(UIKit.Button("搜索", primary: true, onClick: OnSearch), 120);
        var toManageBtn = Big(UIKit.Button("前往管理模组", primary: false, onClick: () => ShowSection(0)), 150);
        _downloadSection.Children.Add(UIKit.PageHeader("下载模组", "Modrinth 热门推荐与在线搜索,按所选版本自动过滤并含依赖解析", toManageBtn));

        // 目标版本卡:安装到哪
        _dlInstanceBox.SelectionChanged += (_, _) => OnDlInstanceSelected();
        _dlBadgeHost.VerticalAlignment = VerticalAlignment.Center;
        var dlCtxRow = UIKit.H(UIKit.Sub("安装到版本", 12), _dlInstanceBox, _dlBadgeHost);
        _dlInstanceBox.Margin = new Thickness(10, 0, 12, 0);
        _downloadSection.Children.Add(UIKit.Card(dlCtxRow, pad: 18, topGap: 12));
        dlCtxRow.Margin = new Thickness(0, 2, 0, 0);

        // 热门模组卡:Modrinth 全站下载排行卡片列表,封面图展示,可换一批 + 可切 3/4 列
        var moreBtn = UIKit.Button("换一批", primary: false, onClick: () => LoadHotMods(true), height: 32);
        moreBtn.MinWidth = 84;
        moreBtn.VerticalAlignment = VerticalAlignment.Center;
        var headActions = UIKit.H(BuildColumnSwitcher(), moreBtn);
        headActions.VerticalAlignment = VerticalAlignment.Center;
        var hotHead = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children =
            {
                UIKit.V(UIKit.Text("热门模组", 14, FontWeights.Medium),
                    UIKit.Sub("Modrinth 全站下载排行 · 安装到上方所选版本 · 卡片列数可自定义", 11).MarginTop(3)),
                headActions.Col(1)
            }
        };
        _hotStatus.TextWrapping = TextWrapping.Wrap;
        _downloadSection.Children.Add(UIKit.Card(UIKit.V(hotHead,
            new Border { Padding = new Thickness(0, 12, 0, 0), Child = _hotGrid },
            new Border { Padding = new Thickness(0, 8, 0, 0), Child = _hotStatus }), pad: 18, topGap: 10));

        // 搜索卡:关键词 + 排序方式,结果行带封面图
        foreach (var s in new[] { "综合排序", "下载最多", "最近更新", "最新发布" }) _sortBox.Items.Add(s);
        _sortBox.SelectedIndex = 0;
        _sortBox.MinWidth = 122;
        _sortBox.VerticalAlignment = VerticalAlignment.Center;
        var searchRow = UIKit.H(_searchBox, _sortBox, searchBtn);
        searchBtn.VerticalAlignment = VerticalAlignment.Center;
        _searchBox.Width = 320;
        _searchBox.VerticalAlignment = VerticalAlignment.Center;
        _searchBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) OnSearch(); };
        // 输入即搜(LauncherX-2):停顿 350ms 后自动联网查询(名称/modid/作者),不必点「搜索」按钮
        _searchBox.TextChanged += (_, _) => _search.DebouncedSearch(_searchBox.Text, _ => OnSearch());
        _searchStatus.TextWrapping = TextWrapping.Wrap;

        var searchHead = UIKit.V(
            UIKit.Text("在线搜索(Modrinth)", 14, FontWeights.Medium),
            UIKit.Sub("自动按所选版本的 MC 版本与加载器过滤,安装时含依赖解析", 12).MarginTop(4));

        _downloadSection.Children.Add(UIKit.Card(UIKit.V(searchHead,
            new Border { Padding = new Thickness(0, 12, 0, 0), Child = searchRow },
            new Border { Padding = new Thickness(0, 10, 0, 8), Child = _searchStatus },
            _searchResults), pad: 18, topGap: 10));
    }

    // ==================== 卡片列数自定义(纯 UI 布局偏好)====================

    /// <summary>列数分段切换器(3 列 / 4 列):选中项主色柔底 + 主色文字,未选项灰底灰字</summary>
    private UIElement BuildColumnSwitcher()
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (int cols in new[] { 3, 4 })
        {
            var label = UIKit.Text($"{cols} 列", 12.5, FontWeights.Medium);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(UIKit.R.Chip),
                Padding = new Thickness(13, 5, 13, 5),
                Child = label
            };
            int c = cols;   // 闭包捕获独立副本
            var btn = UIKit.GhostButton(pill, () => ApplyCardColumns(c), height: 30);
            btn.Cursor = Cursors.Hand;
            btn.Margin = new Thickness(0, 0, 6, 0);
            btn.ToolTip = $"卡片列表按 {c} 列显示";
            _colSegs.Add((pill, label, c));
            host.Children.Add(btn);
        }
        host.Margin = new Thickness(0, 0, 4, 0);
        PaintColumnSwitcher();
        return host;
    }

    /// <summary>应用列数:重排 UniformGrid 列数 + 刷新分段器选中态;persist=true 时写回配置(下次启动沿用)</summary>
    private void ApplyCardColumns(int cols, bool persist = true)
    {
        cols = cols == 4 ? 4 : 3;
        if (cols == _cardColumns && !persist) { PaintColumnSwitcher(); return; }
        _cardColumns = cols;
        _hotGrid.Columns = cols;
        // 重排后给一次极轻的淡入反馈:只动单个元素的 Opacity,不额外触发布局成本,
        // 且 Anim 不指定 From(从当前值续接),快速连点 3/4 列也不会叠加跳变
        _hotGrid.Opacity = 0.55;
        MotionKit.Anim(_hotGrid, UIElement.OpacityProperty, 1, MotionKit.Fast);
        PaintColumnSwitcher();
        if (!persist) return;
        _config.Config.ModsCardColumns = cols;
        _config.Save();
        Shell()?.SetStatus($"模组卡片已切换为 {cols} 列显示");
    }

    /// <summary>分段器选中态上色(令牌驱动,随主题即时刷新)</summary>
    private void PaintColumnSwitcher()
    {
        foreach (var (pill, label, cols) in _colSegs)
        {
            bool on = cols == _cardColumns;
            pill.SetResourceReference(Border.BackgroundProperty, on ? "T.PrimarySoft" : "T.HoverFill");
            label.SetResourceReference(TextBlock.ForegroundProperty, on ? "T.Primary" : "T.ForegroundDim");
        }
    }

    // ==================== 版本下拉(双区联动)====================

    private void RefreshInstances()
    {
        string prevManage = (_instanceBox.SelectedItem as GameInstance)?.Id ?? "";
        string prevDl = (_dlInstanceBox.SelectedItem as GameInstance)?.Id ?? "";
        _instanceBox.Items.Clear();
        _dlInstanceBox.Items.Clear();

        if (_instances.Instances.Count == 0)
        {
            foreach (var box in new[] { _instanceBox, _dlInstanceBox })
            {
                box.Items.Add("(暂无本地版本)");
                box.SelectedIndex = 0;
                box.IsEnabled = false;
            }
            return;
        }
        foreach (var box in new[] { _instanceBox, _dlInstanceBox })
        {
            box.IsEnabled = true;
            foreach (var inst in _instances.Instances) box.Items.Add(inst);
        }
        // 优先保持当前选中,避免刷新时跳回第一个
        int idxM = _instances.Instances.FindIndex(i => i.Id == prevManage);
        _instanceBox.SelectedIndex = idxM >= 0 ? idxM : 0;
        int idxD = _instances.Instances.FindIndex(i => i.Id == prevDl);
        _dlInstanceBox.SelectedIndex = idxD >= 0 ? idxD : (idxM >= 0 ? idxM : 0);
        OnInstanceSelected();
        OnDlInstanceSelected();
    }

    private void OnInstanceSelected()
    {
        if (_instanceBox.SelectedItem is not GameInstance inst)
        {
            _ctxBadgeHost.Text = "";
            return;
        }
        _mods.SetCurrentInstance(inst.Id);
        // 切换版本:清空批量选择、收起上一个版本的更新面板与侧边预览(避免张冠李戴)
        _selectedPaths.Clear();
        _updateCard.Visibility = Visibility.Collapsed;
        _updateResults = new List<ModUpdateInfo>();
        _modPreview.Close();
        string mc = _mods.GetCurrentMcVersion() ?? inst.VersionId;
        string? loader = _mods.GetCurrentModLoader();
        _ctxBadgeHost.Text = string.IsNullOrEmpty(loader) ? $"MC {mc} · 原版" : $"MC {mc} · {loader}";
        RefreshMods();
    }

    private void OnDlInstanceSelected()
    {
        if (_dlInstanceBox.SelectedItem is not GameInstance inst)
        {
            _dlBadgeHost.Text = "";
            return;
        }
        _mods.SetCurrentInstance(inst.Id);
        string mc = _mods.GetCurrentMcVersion() ?? inst.VersionId;
        string? loader = _mods.GetCurrentModLoader();
        _dlBadgeHost.Text = string.IsNullOrEmpty(loader) ? $"MC {mc} · 原版(无加载器通常无法装模组)" : $"MC {mc} · {loader}";
    }

    // ==================== 本地模组 ====================

    private void RefreshMods()
    {
        if (_instanceBox.SelectedItem is not GameInstance) return;
        try { _loaded = _mods.LoadMods(); }
        catch (Exception ex)
        {
            App.WriteAppLog($"[模组] 加载模组列表失败:{ex.Message}");
            _loaded = new List<ModInfo>();
        }
        RenderModList();
        RefreshCompat();
    }

    /// <summary>渲染本地模组列表:搜索增强(LauncherX-2)按名称/modid/作者/文件名/简介匹配 + 只看禁用(Celestial-8)</summary>
    private void RenderModList()
    {
        _modList.Children.Clear();
        var filter = ModSearchService.Filter(_loaded, _filterBox.Text,
            _onlyDisabledChk.IsChecked == true ? true : (bool?)null);
        var visible = filter.Mods;

        _emptyHint.FadeToggle(_loaded.Count == 0);
        string kw = _filterBox.Text.Trim();
        if (_loaded.Count == 0) _modCount.Text = "";
        else if (kw.Length > 0) _modCount.Text = $"匹配 {visible.Count} / 共 {_loaded.Count} 个模组";
        else if (_onlyDisabledChk.IsChecked == true) _modCount.Text = $"已禁用 {visible.Count} / 共 {_loaded.Count} 个模组";
        else _modCount.Text = $"共 {_loaded.Count} 个模组(启用 {filter.EnabledCount} · 禁用 {filter.DisabledCount})";

        foreach (var mod in visible)
            _modList.Children.Add(MakeModRow(mod));
        if (_batchMode) UpdateBatchBar();
    }

    // 模组行左侧图标:后台从 Modrinth 拉模组封面,拉到就显示;点图标仍可切换启用/禁用。
    private Border BuildModIcon(ModInfo mod)
    {
        var img = new Image { Width = 40, Height = 40, Stretch = Stretch.UniformToFill };
        var border = new Border
        {
            Width = 44, Height = 44,
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            Child = img
        };
        // 2026-09-26 批1:去掉硬编码兜底色,统一走主题令牌(切换预设不再露出固定深灰)
        border.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");
        border.Cursor = Cursors.Hand;
        border.ToolTip = mod.Enabled ? "点击禁用此模组" : "点击启用此模组";
        border.MouseLeftButtonUp += (_, _) => Toggle(mod, !mod.Enabled);
        if (!mod.Enabled) border.Opacity = 0.55;
        LoadModIconAsync(mod, img);
        return border;
    }

    private async void LoadModIconAsync(ModInfo mod, Image img)
    {
        string key = string.IsNullOrWhiteSpace(mod.ModIdDisplay) ? mod.FileName : mod.ModIdDisplay;
        lock (_modIconCache)
        {
            if (_modIconCache.TryGetValue(key, out var cached)) { if (cached != null) img.Source = cached; return; }
            if (_modIconTried.Contains(key)) return;
        }
        try
        {
            await _iconSem.WaitAsync();
            try
            {
                var res = await _modrinth.SearchAsync(key, limit: 1);
                var proj = res?.Hits?.FirstOrDefault();
                if (proj != null && !string.IsNullOrEmpty(proj.IconUrl))
                {
                    var bytes = await ModrinthService.DownloadIconBytesAsync(proj.IconUrl!);
                    if (bytes != null && bytes.Length > 0)
                    {
                        var bmp = ImageAssets.DecodeBytes(bytes);
                        if (bmp != null)
                        {
                            _ = Dispatcher.BeginInvoke(new Action(() => img.Source = bmp));
                            lock (_modIconCache) _modIconCache[key] = bmp;
                            return;
                        }
                    }
                }
            }
            finally { _iconSem.Release(); }
        }
        catch { /* 拉不到图就静默,行里留占位 */ }
        lock (_modIconTried) _modIconTried.Add(key);
    }
    private Border MakeModRow(ModInfo mod)
    {
        // 批量多选框(BlockHelm-2):仅批量模式显示;选中集合按 FilePath 记,刷新后不丢
        CheckBox? selectChk = null;
        if (_batchMode)
        {
            selectChk = UIKit.CheckBox("", _selectedPaths.Contains(mod.FilePath));
            selectChk.VerticalAlignment = VerticalAlignment.Center;
            selectChk.Margin = new Thickness(0, 0, 12, 0);
            selectChk.ToolTip = "勾选以纳入批量操作";
            string path = mod.FilePath;
            selectChk.Checked += (_, _) => { _selectedPaths.Add(path); UpdateBatchBar(); };
            selectChk.Unchecked += (_, _) => { _selectedPaths.Remove(path); UpdateBatchBar(); };
        }

        var modIcon = BuildModIcon(mod);
        modIcon.VerticalAlignment = VerticalAlignment.Center;

        var name = UIKit.Text(mod.DisplayName, 13, FontWeights.Medium);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        if (!mod.Enabled) name.Opacity = 0.55;
        var sub = UIKit.Sub($"{mod.LoaderDisplay}  ·  v{mod.VersionDisplay}  ·  {mod.FileName}", 11);
        // 修复模组项内部文字截断:小字不再用省略号横向切掉,改为自动换行完整显示;
        // 行高随内容自然撑开,配合下方加大的底部内边距,保证条目内文字全部露出不被遮挡。
        sub.TextWrapping = TextWrapping.Wrap;
        sub.ToolTip = mod.FileName;
        sub.Margin = new Thickness(0, 3, 0, 0);
        if (!mod.Enabled) sub.Opacity = 0.55;

        var statusBadge = UIKit.Badge(mod.Enabled ? "已启用" : "已禁用", mod.Enabled ? "T.Success" : "T.ForegroundDim");
        statusBadge.VerticalAlignment = VerticalAlignment.Center;
        statusBadge.Margin = new Thickness(0, 0, 12, 0);

        // 行内按钮统一高度 32(全局按钮风格一致)
        var detailBtn = UIKit.Button("详情", primary: false, onClick: () => ShowModDetail(mod), height: 32);
        var revealBtn = UIKit.Button("定位", primary: false, onClick: () => _mods.RevealModInExplorer(mod.FilePath), height: 32);
        var delBtn = UIKit.Button("删除", primary: false, onClick: () => Delete(mod), height: 32);
        detailBtn.MinWidth = 70; revealBtn.MinWidth = 70; delBtn.MinWidth = 70;
        var actions = UIKit.H(statusBadge, detailBtn, revealBtn, delBtn);
        actions.VerticalAlignment = VerticalAlignment.Center;

        // 首列留给批量多选框(非批量模式无子元,Auto 列自动收成 0 宽)
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        if (selectChk != null) grid.Children.Add(selectChk.Col(0));
        grid.Children.Add(modIcon.Col(1));
        grid.Children.Add(UIKit.V(name, sub).Col(2));
        grid.Children.Add(actions.Col(3));
        modIcon.Margin = new Thickness(0, 0, 14, 0);

        var card = UIKit.Panel(grid, pad: 14);
        card.WithRef("T.SurfaceAlt", "T.Border");
        card.BorderThickness = new Thickness(1);
        // 加大条目底部内边距(上/侧 12–14、底 18)与条目间距(8→12),
        // 给条目下方留出足够空间,避免底部小字/操作行被切割遮挡。
        card.Padding = new Thickness(14, 12, 14, 18);
        card.Margin = new Thickness(0, 0, 0, 12);
        // 点行空白处 → 侧边预览(LauncherX-1);按钮/勾选框自身会 Handled,不会误触
        card.MouseLeftButtonUp += (_, _) => _modPreview.Show(mod, CurrentInstanceId());
        // 悬浮 → 元数据卡片(Axolotl-7),不点开详情弹窗就能看作者/版本/依赖/简介
        card.MouseEnter += (_, _) => ShowHoverCard(card, mod);
        card.MouseLeave += (_, _) => HideHoverCard();
        card.HoverLift(-1);   // 悬停轻微上浮(-1px 不干扰悬浮卡定位),与悬浮预览共同表达"可交互"
        return card;
    }

    private void Toggle(ModInfo mod, bool enable)
    {
        bool ok = _mods.ToggleMod(mod.FilePath, enable);
        if (!ok)
        {
            DialogKit.Error($"切换失败:{mod.FileName}", owner: Window.GetWindow(this));
            RefreshMods();
            return;
        }
        App.WriteAppLog($"[模组] {(enable ? "启用" : "禁用")}:{mod.FileName}");
        RefreshMods();
        Shell()?.SetStatus($"已{(enable ? "启用" : "禁用")} {mod.DisplayName}");
    }

    private void Delete(ModInfo mod)
    {
        if (!DialogKit.Confirm($"确定删除模组「{mod.DisplayName}」吗?\n文件将被移除,不可恢复。", "删除模组", Window.GetWindow(this)))
            return;
        bool ok = _mods.DeleteMod(mod.FilePath);
        if (ok) { RefreshMods(); Shell()?.SetStatus("模组已删除"); }
        else DialogKit.Error("删除失败(文件可能被占用)", owner: Window.GetWindow(this));
    }

    private void RefreshCompat()
    {
        var problems = new List<string>();
        try
        {
            foreach (var (a, b, reason) in _mods.DetectConflicts(_loaded))
                problems.Add($"冲突:{a.DisplayName} × {b.DisplayName}({reason})");
            foreach (var (mod, dep) in _mods.DetectMissingRequirements(_loaded))
                problems.Add($"缺失依赖:{mod.DisplayName} 需要 {dep.ModId}");
        }
        catch (Exception ex) { App.WriteAppLog($"[模组] 兼容体检异常:{ex.Message}"); }

        if (problems.Count == 0)
        {
            _compatCard.Visibility = _loaded.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _compatText.Text = "兼容体检通过:未发现冲突与缺失依赖";
            return;
        }
        _compatCard.Visibility = Visibility.Visible;
        string more = problems.Count > 8 ? $"\n…另有 {problems.Count - 8} 个问题" : "";
        _compatText.Text = "⚠ 发现 " + problems.Count + " 个问题:\n" + string.Join("\n", problems.Take(8)) + more;
    }

    /// <summary>模组详情弹窗:展示解析自 fabric.mod.json / mods.toml 的元信息</summary>
    private void ShowModDetail(ModInfo mod)
    {
        var lines = new List<string>
        {
            $"名称:{mod.DisplayName}",
            $"模组 ID:{mod.ModIdDisplay}",
            $"版本:{mod.VersionDisplay}",
            $"加载器:{mod.LoaderDisplay}",
            $"适配游戏版本:{mod.McVersionDisplay}",
            $"作者:{(string.IsNullOrWhiteSpace(mod.Author) ? "-" : mod.Author)}",
            $"文件大小:{mod.SizeDisplay}",
            $"状态:{mod.StatusDisplay}",
            $"必需依赖:{mod.RequiredDepsDisplay}",
            $"文件:{mod.FileName}"
        };
        if (mod.Dependencies.Count > 0)
            lines.Add("依赖明细:" + string.Join(", ", mod.Dependencies.Select(d => $"{d.ModId}({d.DependencyType})")));
        if (mod.Conflicts.Count > 0)
            lines.Add("声明冲突:" + string.Join(", ", mod.Conflicts));
        if (!string.IsNullOrWhiteSpace(mod.Description))
            lines.Add("\n简介:\n" + mod.Description);
        DialogKit.Info(string.Join("\n", lines), "模组详情", Window.GetWindow(this));
    }

    private void OnImportFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择模组文件",
            Filter = "模组|*.jar;*.zip|所有文件|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;
        ImportFiles(dlg.FileNames);
    }

    /// <summary>统一导入入口(文件选择与拖拽共用),逐文件导入并汇总失败明细</summary>
    private void ImportFiles(string[] files)
    {
        if (_instanceBox.SelectedItem is not GameInstance sel)
        {
            App.WriteAppLog($"[模组] 导入中止:未选择游戏版本({files.Length} 个文件)");
            DialogKit.Info("请先安装并选择一个游戏版本,再导入模组", "导入模组", Window.GetWindow(this));
            return;
        }

        App.WriteAppLog($"[模组] 开始导入模组文件 {files.Length} 个,目标版本:{sel.Name}({sel.Id})");
        int ok = 0;
        var failures = new List<string>();
        foreach (var f in files)
        {
            var (success, error) = _mods.ImportModFile(f);
            if (success) ok++;
            else failures.Add($"{System.IO.Path.GetFileName(f)}:{error ?? "未知原因"}");
        }

        if (failures.Count == 0)
        {
            DialogKit.Success($"导入完成:{ok} / {files.Length} 个文件", "导入模组", Window.GetWindow(this));
        }
        else
        {
            string detail = string.Join("\n", failures.Take(5));
            if (failures.Count > 5) detail += $"\n…另有 {failures.Count - 5} 个失败";
            DialogKit.Error($"成功 {ok} 个,失败 {failures.Count} 个:\n{detail}", "导入模组", Window.GetWindow(this));
        }
        App.WriteAppLog($"[模组] 导入完成:成功 {ok} / 失败 {failures.Count}");
        RefreshMods();
    }

    // ==================== 侧边预览 / 悬浮卡(LauncherX-1 / Axolotl-7)====================

    /// <summary>当前管理区选中的实例 Id(优先下拉选中项,回落 ModManager 记录的当前实例)</summary>
    private string CurrentInstanceId() =>
        (_instanceBox.SelectedItem as GameInstance)?.Id ?? _mods.CurrentInstanceId ?? "";

    /// <summary>悬浮元数据卡片(Axolotl-7):鼠标移入模组行弹出,移出即收</summary>
    private void ShowHoverCard(Border anchor, ModInfo mod)
    {
        // 防抖:不立即开,先记锚点与模组,等 _hoverOpenTimer 到点再开,避免划过即弹+死循环
        _hoverAnchor = anchor;
        _hoverMod = mod;
        _hoverCloseTimer.Stop();
        _hoverOpenTimer.Stop();
        _hoverOpenTimer.Start();
    }

    private void HideHoverCard()
    {
        // 防抖:取消待开,延迟关;鼠标移进弹窗时由弹窗自身 MouseEnter 取消本关闭计时
        _hoverOpenTimer.Stop();
        _hoverCloseTimer.Stop();
        _hoverCloseTimer.Start();
    }

    // ==================== BlockHelm-2 批量管理 ====================

    private List<ModInfo> VisibleMods() =>
        ModSearchService.Filter(_loaded, _filterBox.Text, _onlyDisabledChk.IsChecked == true ? true : (bool?)null).Mods;

    private List<ModInfo> SelectedMods() =>
        _loaded.Where(m => _selectedPaths.Contains(m.FilePath)).ToList();

    private void ToggleBatchMode()
    {
        _batchMode = !_batchMode;
        if (!_batchMode) _selectedPaths.Clear();
        BuildBatchBar();
        RenderModList();
        Shell()?.SetStatus(_batchMode
            ? "已进入批量管理:勾选模组复选框后可批量启用 / 禁用 / 导出 / 迁移"
            : "已退出批量管理");
    }

    private void BuildBatchBar()
    {
        _batchBarHost.Children.Clear();
        if (!_batchMode) return;
        var selectAllBtn = PanelKit.Btn("全选", false, () => { foreach (var m in VisibleMods()) _selectedPaths.Add(m.FilePath); RenderModList(); }, 72);
        var invertBtn = PanelKit.Btn("反选", false, () => { foreach (var m in VisibleMods()) { if (!_selectedPaths.Remove(m.FilePath)) _selectedPaths.Add(m.FilePath); } RenderModList(); }, 72);
        var clearSelBtn = PanelKit.Btn("清空选择", false, () => { _selectedPaths.Clear(); RenderModList(); }, 92);
        var enableBtn = PanelKit.Btn("批量启用", true, () => BatchSetEnabled(true), 92);
        var disableBtn = PanelKit.Btn("批量禁用", false, () => BatchSetEnabled(false), 92);
        var exportBtn = PanelKit.Btn("批量导出", false, () => _ = BatchExportAsync(), 92);
        var moveBtn = PanelKit.Btn("批量迁移", false, () => _ = BatchMoveAsync(), 92);
        var exitBtn = PanelKit.Btn("退出批量", false, ToggleBatchMode, 92);
        _selCount.VerticalAlignment = VerticalAlignment.Center;
        _selCount.Margin = new Thickness(6, 0, 0, 0);
        var actionsRow = UIKit.H(selectAllBtn, invertBtn, clearSelBtn, enableBtn, disableBtn, exportBtn, moveBtn, exitBtn, _selCount);
        var hint = UIKit.Sub("批量管理:勾选每行左侧复选框选中模组,再点按钮统一处理。禁用不删文件;迁移默认「移动」,也可选「复制」。", 11.5);
        hint.TextWrapping = TextWrapping.Wrap;
        _batchBarHost.Children.Add(UIKit.Card(UIKit.V(hint,
            new Border { Padding = new Thickness(0, 10, 0, 0), Child = actionsRow }), pad: 16, topGap: 10));
        UpdateBatchBar();
    }

    private void UpdateBatchBar()
    {
        int n = SelectedMods().Count;
        _selCount.Text = n == 0 ? "未选中模组" : $"已选中 {n} 个模组";
        _selCount.SetResourceReference(TextBlock.ForegroundProperty, n == 0 ? "T.ForegroundDim" : "T.Primary");
    }

    private void BatchSetEnabled(bool enable)
    {
        var sel = SelectedMods();
        if (sel.Count == 0) { DialogKit.Info("请先勾选要处理的模组", "批量操作", Window.GetWindow(this)); return; }
        var res = _batch.SetEnabled(sel, enable);
        string title = "批量" + (enable ? "启用" : "禁用");
        _selectedPaths.Clear();
        RefreshMods();
        if (res.Ok) { DialogKit.Success(res.Message, title, Window.GetWindow(this)); Shell()?.SetStatus(title + "完成"); }
        else { DialogKit.Warn(res.Message, title, Window.GetWindow(this)); Shell()?.SetStatus(title + "部分失败", warning: true); }
    }

    private async Task BatchExportAsync()
    {
        var sel = SelectedMods();
        if (sel.Count == 0) { DialogKit.Info("请先勾选要导出的模组", "批量导出", Window.GetWindow(this)); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出模组包",
            Filter = "压缩包|*.zip",
            FileName = $"mods-export_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
        };
        if (dlg.ShowDialog() != true) return;
        Shell()?.SetStatus($"正在导出 {sel.Count} 个模组…");
        var progress = new Progress<string>(s => Shell()?.SetStatus(s));
        var res = await _batch.ExportAsync(sel, dlg.FileName, progress);
        if (res.Ok) { DialogKit.Success(res.Message, "批量导出", Window.GetWindow(this)); Shell()?.SetStatus("模组包导出完成"); }
        else { DialogKit.Warn(res.Message, "批量导出", Window.GetWindow(this)); Shell()?.SetStatus("模组包导出未完成", warning: true); }
    }

    private async Task BatchMoveAsync()
    {
        var sel = SelectedMods();
        if (sel.Count == 0) { DialogKit.Info("请先勾选要迁移的模组", "批量迁移", Window.GetWindow(this)); return; }
        string curId = CurrentInstanceId();
        var targets = _instances.Instances.Where(i => i.Id != curId).ToList();
        if (targets.Count == 0) { DialogKit.Info("没有其它游戏版本可以迁移,请先再安装一个版本", "批量迁移", Window.GetWindow(this)); return; }

        int mode = DialogKit.Choice($"把选中的 {sel.Count} 个模组迁移到别的版本,选择方式:",
            new[] { "移动(源版本不再保留)", "复制(源版本保留一份)" }, "批量迁移", Window.GetWindow(this));
        if (mode < 0) return;
        bool move = mode == 0;

        var names = targets.Select(i => i.Name).ToList();
        string? picked = DialogKit.PickFromList($"迁移到哪个游戏版本?({(move ? "移动" : "复制")} {sel.Count} 个模组)",
            names, "选择目标版本", Window.GetWindow(this));
        if (picked == null) return;
        var target = targets.FirstOrDefault(i => i.Name == picked);
        if (target == null) return;

        Shell()?.SetStatus($"正在{(move ? "移动" : "复制")} {sel.Count} 个模组到 {target.Name}…");
        var progress = new Progress<string>(s => Shell()?.SetStatus(s));
        var res = await _batch.MoveToAsync(sel, target.Id, move, progress);
        _selectedPaths.Clear();
        RefreshMods();
        if (res.Ok) { DialogKit.Success(res.Message, "批量迁移", Window.GetWindow(this)); Shell()?.SetStatus("模组迁移完成"); }
        else { DialogKit.Warn(res.Message, "批量迁移", Window.GetWindow(this)); Shell()?.SetStatus("模组迁移部分失败", warning: true); }
    }

    // ==================== LauncherX-7 更新聚合 ====================

    private async Task CheckUpdatesAsync()
    {
        if (_checkingUpdates) return;
        string instId = CurrentInstanceId();
        if (string.IsNullOrEmpty(instId) || _loaded.Count == 0)
        {
            DialogKit.Info("当前版本还没有模组,无需检查更新", "检查更新", Window.GetWindow(this));
            return;
        }
        _checkingUpdates = true;
        _updateCard.Visibility = Visibility.Visible;
        _updateHost.Children.Clear();
        _updateStatus.ClearValue(TextBlock.ForegroundProperty);
        _updateStatus.Text = "正在联网检查所有模组的新版本…";
        Shell()?.SetStatus("正在检查模组更新…");
        try
        {
            var progress = new Progress<string>(s => { _updateStatus.Text = s; Shell()?.SetStatus(s); });
            var list = await _diag.CheckUpdatesAsync(instId, progress);
            _updateResults = list;
            RenderUpdates(list);
        }
        catch (Exception ex)
        {
            _updateStatus.Text = "检查更新失败:" + ex.Message;
            App.WriteAppLog($"[模组更新] 检查异常:{ex}");
        }
        finally { _checkingUpdates = false; }
    }

    private void RenderUpdates(List<ModUpdateInfo> all)
    {
        _updateHost.Children.Clear();
        var updatable = all.Where(x => x.HasUpdate).ToList();
        int err = all.Count(x => !string.IsNullOrEmpty(x.Error));
        string errTail = err > 0 ? $"(其中 {err} 个查询失败)" : "";

        var closeBtn = PanelKit.Btn("收起面板", false, () => _updateCard.Visibility = Visibility.Collapsed, 92);
        if (updatable.Count == 0)
        {
            _updateStatus.Text = $"检查完成:全部 {all.Count} 个模组都是最新版本{errTail}";
            _updateStatus.SetResourceReference(TextBlock.ForegroundProperty, "T.Success");
            _updateHost.Children.Add(new Border { Padding = new Thickness(0, 8, 0, 0), Child = UIKit.H(closeBtn) });
            Shell()?.SetStatus("模组已是最新版本");
            return;
        }

        _updateStatus.Text = $"发现 {updatable.Count} 个模组有新版本(共检查 {all.Count} 个){errTail}";
        _updateStatus.SetResourceReference(TextBlock.ForegroundProperty, "T.Primary");
        var updateAllBtn = PanelKit.Btn($"一键更新全部({updatable.Count})", true, () => _ = UpdateAllAsync(updatable), 150);
        _updateHost.Children.Add(new Border { Padding = new Thickness(0, 8, 0, 10), Child = UIKit.H(updateAllBtn, closeBtn) });
        foreach (var u in updatable)
            _updateHost.Children.Add(MakeUpdateRow(u));
        Shell()?.SetStatus($"发现 {updatable.Count} 个可更新模组", warning: true);
    }

    private Border MakeUpdateRow(ModUpdateInfo u)
    {
        var title = UIKit.Text(u.Mod.DisplayName, 12.5, FontWeights.Medium);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var ver = UIKit.Sub($"{u.CurrentVersion} → {u.LatestVersion}  ·  {u.LatestType}", 11);
        ver.SetResourceReference(TextBlock.ForegroundProperty, "T.Primary");
        var btn = PanelKit.Btn("更新", true, null, 76);
        btn.VerticalAlignment = VerticalAlignment.Center;
        btn.Click += async (_, _) => await UpdateOneAsync(u, btn);
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { UIKit.V(title, ver), btn.Col(1) }
        };
        return PanelKit.Row(grid, pad: 12);
    }

    private async Task UpdateOneAsync(ModUpdateInfo u, Button btn)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == CurrentInstanceId());
        if (inst == null) return;
        btn.IsEnabled = false;
        UIKit.SetButtonText(btn, "更新中");
        try
        {
            var (ok, msg) = await _diag.UpdateModAsync(u, _instances.GetModsDir(inst.Id));
            if (ok)
            {
                u.HasUpdate = false;
                UIKit.SetButtonText(btn, "已更新");
                Shell()?.SetStatus(msg);
                RefreshMods();
            }
            else
            {
                UIKit.SetButtonText(btn, "更新");
                btn.IsEnabled = true;
                Shell()?.SetStatus("更新失败:" + msg, warning: true);
            }
        }
        catch (Exception ex)
        {
            UIKit.SetButtonText(btn, "更新");
            btn.IsEnabled = true;
            Shell()?.SetStatus("更新异常:" + ex.Message, warning: true);
        }
    }

    private async Task UpdateAllAsync(List<ModUpdateInfo> updatable)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == CurrentInstanceId());
        if (inst == null || updatable.Count == 0) return;
        if (!DialogKit.Confirm($"一键更新全部 {updatable.Count} 个模组?\n旧文件会改名为 .old 备份保留,确认没问题后可自行删除。",
                "批量更新", Window.GetWindow(this))) return;
        string modsDir = _instances.GetModsDir(inst.Id);
        int ok = 0;
        var fails = new List<string>();
        foreach (var u in updatable)
        {
            _updateStatus.Text = $"正在更新 {u.Mod.DisplayName}…";
            Shell()?.SetStatus($"正在更新 {u.Mod.DisplayName}…");
            try
            {
                var (success, msg) = await _diag.UpdateModAsync(u, modsDir);
                if (success) { ok++; u.HasUpdate = false; }
                else fails.Add($"{u.Mod.DisplayName}:{msg}");
            }
            catch (Exception ex) { fails.Add($"{u.Mod.DisplayName}:{ex.Message}"); }
        }
        RefreshMods();
        string detail = fails.Count == 0
            ? $"全部 {ok} 个模组已更新到最新版本。"
            : $"成功 {ok} 个,失败 {fails.Count} 个:\n" + string.Join("\n", fails.Take(8)) + (fails.Count > 8 ? $"\n…另有 {fails.Count - 8} 个失败" : "");
        if (fails.Count == 0) { DialogKit.Success(detail, "批量更新", Window.GetWindow(this)); Shell()?.SetStatus($"已更新 {ok} 个模组"); }
        else { DialogKit.Warn(detail, "批量更新", Window.GetWindow(this)); Shell()?.SetStatus($"批量更新:成功 {ok},失败 {fails.Count}", warning: true); }
        RenderUpdates(_updateResults);
    }

    // ==================== 在线搜索(下载区)====================

    private async void OnSearch()
    {
        // 输入即搜时可能上一次查询还没回来:记下“还要再查一次”,等当前结束后用最新关键词补跑
        if (_searching) { _requeryPending = true; return; }
        string query = _searchBox.Text.Trim();
        int gen = ++_searchGen;
        _searching = true;
        _searchStatus.Text = "正在搜索…";
        _searchResults.Children.Clear();
        try
        {
            string? mc = _mods.GetCurrentMcVersion();
            string? loader = _mods.GetCurrentModLoader()?.ToLowerInvariant();
            string sort = _sortBox.SelectedIndex switch { 1 => "downloads", 2 => "updated", 3 => "newest", _ => "relevance" };
            var result = await _modrinth.SearchAsync(query, limit: 20,
                gameVersion: mc, loader: loader == "optifine" ? null : loader, sort: sort);
            if (gen != _searchGen) return;   // 已有更新的搜索,丢弃过期结果
            _searchStatus.Text = $"共 {result.TotalHits} 个结果" +
                (string.IsNullOrEmpty(mc) ? "" : $"(已按 MC {mc}{(string.IsNullOrEmpty(loader) ? "" : " / " + loader)} 过滤)");
            foreach (var hit in result.Hits)
                _searchResults.Children.Add(MakeHitRow(hit));
            if (result.Hits.Count == 0)
                _searchResults.Children.Add(UIKit.Sub("没有匹配的模组,试试更短的关键词或更换版本", 12));
        }
        catch (Exception ex)
        {
            if (gen == _searchGen)
                _searchStatus.Text = "搜索失败:" + ex.Message;
            App.WriteAppLog($"[模组] Modrinth 搜索异常:{ex.Message}");
        }
        finally
        {
            _searching = false;
            if (_requeryPending) { _requeryPending = false; OnSearch(); }   // 补跑最新关键词(输入即搜)
        }
    }

    private Border MakeHitRow(ModrinthProject hit)
    {
        var iconHost = MakeIconHost(hit.IconUrl, hit.Title, 38, 6);
        iconHost.VerticalAlignment = VerticalAlignment.Center;
        iconHost.Margin = new Thickness(0, 0, 12, 0);

        var name = UIKit.Text(hit.Title, 13, FontWeights.Medium);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        _ = TranslateToZhAsync(hit.Title, name);
        var desc = UIKit.Sub(hit.Description, 11.5);
        desc.TextTrimming = TextTrimming.CharacterEllipsis;
        desc.ToolTip = hit.Description;
        desc.Margin = new Thickness(0, 3, 0, 0);
        var meta = UIKit.Sub($"下载 {FmtCount(hit.Downloads)}  ·  作者 {hit.Author}", 10.5);
        meta.Margin = new Thickness(0, 3, 0, 0);
        _ = TranslateToZhAsync(hit.Description, desc, 8);

        var installBtn = UIKit.Button("安装", primary: true, height: 32);
        installBtn.MinWidth = 90;
        installBtn.Click += (_, _) => InstallHit(hit, installBtn);
        installBtn.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { iconHost, UIKit.V(name, desc, meta).Col(1), installBtn.Col(2) }
        };

        var card = UIKit.Panel(grid, pad: 14);
        card.WithRef("T.SurfaceAlt", "T.Border");
        card.BorderThickness = new Thickness(1);
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    // ==================== 热门模组板块 ====================

    /// <summary>加载热门模组:Modrinth 下载量排行;next=true 翻页(换一批)</summary>
    private async void LoadHotMods(bool next)
    {
        if (_hotLoading) return;
        _hotLoading = true;
        if (next) _hotOffset = (_hotOffset + 12) % 120;   // Top 132 内循环翻页
        int gen = ++_hotGen;
        _hotStatus.Text = next ? "正在换一批…" : "正在加载热门模组…";
        try
        {
            var result = await _modrinth.SearchAsync("", offset: _hotOffset, limit: 12, sort: "downloads");
            if (gen != _hotGen) return;   // 已有更新的加载,丢弃过期结果
            _hotGrid.Children.Clear();
            foreach (var hit in result.Hits)
                _hotGrid.Children.Add(MakeHotCard(hit));
            _hotStatus.Text = result.Hits.Count == 0 ? "暂无热门数据(可能网络异常),可点「换一批」重试" : "";
        }
        catch (Exception ex)
        {
            if (gen == _hotGen) _hotStatus.Text = "热门模组加载失败:" + ex.Message;
            App.WriteAppLog($"[模组] 热门模组加载异常:{ex.Message}");
        }
        finally { _hotLoading = false; }
    }

    /// <summary>热门模组卡:封面图 + 名称 + 简介 + 下载量 + 安装按钮。
    /// 卡片不再定宽,改为随 UniformGrid 列宽自适应铺满(3 列宽卡 / 4 列窄卡都不留白)</summary>
    private FrameworkElement MakeHotCard(ModrinthProject hit)
    {
        var iconHost = MakeIconHost(hit.IconUrl, hit.Title, 44);
        iconHost.Margin = new Thickness(0, 0, 10, 0);

        var title = UIKit.Text(hit.Title, 13, FontWeights.Medium);
        title.TextTrimming = TextTrimming.CharacterEllipsis;   // 列宽自适应:超出列宽才截断,不再写死 MaxWidth
        _ = TranslateToZhAsync(hit.Title, title);
        var meta = UIKit.Sub($"下载 {FmtCount(hit.Downloads)}", 10.5);
        meta.Margin = new Thickness(0, 2, 0, 0);
        var textCol = UIKit.V(title, meta);
        textCol.VerticalAlignment = VerticalAlignment.Center;
        // 头部用 Grid(Auto + Star)而不是水平 StackPanel:StackPanel 给子元素无限测量宽度,
        // TextTrimming 永远不会触发,长标题会把卡片撑破(这正是原先写死 MaxWidth 的原因)
        var headRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            Children = { iconHost, textCol.Col(1) }
        };

        var desc = UIKit.Sub(hit.Description, 11);
        desc.TextWrapping = TextWrapping.Wrap;
        desc.MaxHeight = 32;   // 最多两行,超出截断
        desc.Margin = new Thickness(0, 8, 0, 0);
        _ = TranslateToZhAsync(hit.Description, desc, 8);

        // 行内按钮全局统一 32px 档位(与搜索结果卡/版本卡/账号卡同规格)
        var installBtn = UIKit.Button("安装", primary: true, height: 32);
        installBtn.MinWidth = 72;
        installBtn.HorizontalAlignment = HorizontalAlignment.Right;
        installBtn.Margin = new Thickness(0, 10, 0, 0);
        installBtn.Click += (_, _) => InstallHit(hit, installBtn);

        var card = UIKit.Panel(UIKit.V(headRow, desc, installBtn), pad: 12);
        card.WithRef("T.SurfaceAlt", "T.Border");
        card.BorderThickness = new Thickness(1);
        card.HorizontalAlignment = HorizontalAlignment.Stretch;   // 铺满所在列
        card.Margin = new Thickness(5, 0, 5, 10);   // 左右对称:列间距 10px,且末列不再比首列多出一条空白
        return card;
    }

    /// <summary>模组封面容器:加载中/无封面显示首字母占位,封面下载成功后替换(圆角裁切)</summary>
    private static Grid MakeIconHost(string? iconUrl, string fallbackTitle, double size, double radius = 8)
    {
        string letter = string.IsNullOrWhiteSpace(fallbackTitle) ? "?"
            : fallbackTitle.Trim().Substring(0, 1).ToUpperInvariant();
        var letterText = UIKit.Text(letter, size * 0.42, FontWeights.SemiBold, "T.ForegroundDim");
        letterText.HorizontalAlignment = HorizontalAlignment.Center;
        letterText.VerticalAlignment = VerticalAlignment.Center;
        var placeholder = new Border { CornerRadius = new CornerRadius(radius), Child = letterText };
        placeholder.SetResourceReference(Border.BackgroundProperty, "T.Surface");

        var img = new Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.UniformToFill,
            Visibility = Visibility.Collapsed,
            Tag = iconUrl ?? "",    // 记录目标 URL,防异步回来串图
            Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius)
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Fant);

        var host = new Grid { Width = size, Height = size, Children = { placeholder, img } };
        LoadIconAsync(img, placeholder, iconUrl);
        return host;
    }

    /// <summary>异步拉取封面图(ModrinthService 内存+磁盘双缓存),成功后替换占位符</summary>
    private static async void LoadIconAsync(Image img, Border placeholder, string? iconUrl)
    {
        if (string.IsNullOrEmpty(iconUrl)) return;
        try
        {
            var bytes = await ModrinthService.DownloadIconBytesAsync(iconUrl);
            if (bytes == null || (string)img.Tag != iconUrl) return;
            var bmp = ImageAssets.DecodeBytes(bytes);
            if (bmp == null) return;   // 解码失败(含 WebP 降级失败)保留首字母占位
            img.Source = bmp;
            img.Visibility = Visibility.Visible;
            placeholder.Visibility = Visibility.Collapsed;
        }
        catch { /* 网络失败保留首字母占位 */ }
    }

    /// <summary>下载量友好格式(万/亿进位)</summary>
    private static string FmtCount(long n) =>
        n >= 100_000_000 ? $"{n / 100000000.0:F1} 亿"
        : n >= 10_000 ? $"{n / 10000.0:F1} 万"
        : n.ToString("N0");

    private async void InstallHit(ModrinthProject hit, Button btn)
    {
        if (_dlInstanceBox.SelectedItem is not GameInstance inst)
        {
            DialogKit.Info("请先安装一个游戏版本", owner: Window.GetWindow(this));
            return;
        }
        btn.IsEnabled = false;
        _searchStatus.Text = $"正在获取 {hit.Title} 的版本信息…";
        try
        {
            string? mc = _mods.GetCurrentMcVersion();
            string? loader = _mods.GetCurrentModLoader()?.ToLowerInvariant();
            if (loader == "optifine") loader = null;

            var versions = await _modrinth.GetProjectVersionsAsync(hit.ProjectId,
                string.IsNullOrEmpty(mc) ? null : mc,
                string.IsNullOrEmpty(loader) ? null : loader);
            var version = versions.FirstOrDefault();
            if (version == null)
            {
                _searchStatus.Text = $"{hit.Title}:没有适配当前版本({mc})的文件";
                return;
            }

            _searchStatus.Text = $"正在安装 {hit.Title}(含依赖解析)…";
            Shell()?.SetStatus($"正在安装模组 {hit.Title}…");
            string modsDir = _instances.GetModsDir(inst.Id);
            var (ok, files, failedDeps) = await _modrinth.InstallModWithDependenciesAsync(version, modsDir, mc, loader);
            if (ok)
            {
                _searchStatus.Text = failedDeps.Count > 0
                    ? $"{hit.Title} 已安装({files.Count} 个文件),但有 {failedDeps.Count} 个前置下载失败:{string.Join("、", failedDeps)}。模组可能无法正常运行,请重试或检查网络"
                    : $"{hit.Title} 安装成功({files.Count} 个文件,含自动解析的前置),可前往「管理模组」查看";
                Shell()?.SetStatus(failedDeps.Count > 0
                    ? $"模组 {hit.Title} 已装但 {failedDeps.Count} 个前置失败"
                    : $"模组 {hit.Title} 安装完成");
                App.WriteAppLog($"[模组] 在线安装:{hit.Title} 共 {files.Count} 文件");
                // 管理区若同为该版本,即时刷新列表
                if ((_instanceBox.SelectedItem as GameInstance)?.Id == inst.Id) RefreshMods();
            }
            else
            {
                _searchStatus.Text = $"{hit.Title} 安装失败";
                DialogKit.Error("安装失败,请查看日志", "模组安装", Window.GetWindow(this));
            }
        }
        catch (Exception ex)
        {
            _searchStatus.Text = "安装异常:" + ex.Message;
            App.WriteAppLog($"[模组] 在线安装异常:{ex}");
        }
        finally { btn.IsEnabled = true; }
    }


    /// <summary>英文→中文翻译(MyMemory 免费接口):命中缓存直接回填,否则异步请求后回填。
    /// 标题/简介共用:minLen 控制最短翻译长度(标题 3,简介 8)。</summary>
    private async Task TranslateToZhAsync(string en, TextBlock target, int minLen = 3)
    {
        if (string.IsNullOrWhiteSpace(en) || en.Length < minLen) return;   // 太短(缩写/专名)不翻
        if (System.Text.RegularExpressions.Regex.IsMatch(en, "[\u4e00-\u9fff]")) return;   // 已含中文不翻
        // 本地词典优先:钠/锂/机械动力等社区共识译名离线秒翻,不受网络与翻译限额影响
        if (ZhDict.TryLookup(en, out var dictZh))
        {
        _ =     Dispatcher.BeginInvoke(new Action(() => target.Text = dictZh));
            return;
        }
        try
        {
            if (_zhCache.TryGetValue(en, out var cached))
            {
        _ =         Dispatcher.BeginInvoke(new Action(() => target.Text = cached));
                return;
            }
            await _zhSem.WaitAsync();
            try
            {
                if (_zhCache.TryGetValue(en, out cached))
                {
        _ =             Dispatcher.BeginInvoke(new Action(() => target.Text = cached));
                    return;
                }
                string zh = "";
                try
                {
                    var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(en) + "&langpair=en|zh-CN";
                    var json = await _zhHttp.GetStringAsync(url);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var zh0 = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
                    zh = System.Text.RegularExpressions.Regex.Replace(zh0, "<[^>]+>", "").Replace("★", "").Trim();   // 剥离 MyMemory 的 HTML 标签与 ★ 标记
                }
                catch
                {
                    // MyMemory 免费接口易限流,失败换 Google 免费端点兜底(不抛错保留英文)
                }
                if (zh.Length == 0 || zh.Equals(en, StringComparison.OrdinalIgnoreCase))
                    zh = await ZhDict.TranslateViaGoogleAsync(_zhHttp, en) ?? "";
                if (zh.Length > 0 && !zh.Equals(en, StringComparison.OrdinalIgnoreCase))
                {
                    // 2026-09-25 缓存上限:翻译词条超 2000 清空重建,防长期使用内存只涨不降
            if (_zhCache.Count > 2000) _zhCache.Clear();
            _zhCache[en] = zh;
    _ =                 Dispatcher.BeginInvoke(new Action(() => target.Text = zh));
                }
            }
            finally { _zhSem.Release(); }
        }
        catch { /* 翻译失败保留英文 */ }
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
