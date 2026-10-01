// Copyright © FufuLauncher
//
// 版本管理页:
// 1. 本地版本列表:搜索筛选 / 启动 / 重命名 / 复制 / 打开目录 / 导出 / 删除(存档硬性保留)
//    / 导入已有 .minecraft 目录,版本卡带草方块图标;
// 2. 新装版本(三步向导):
//    第一步 选版本 —— 官方清单一次性全量列出(类型筛选 + 搜索 + 下载源切换),点击行进入下一步;
//    第二步 选加载器 —— 卡片式选择 原版/Fabric/Forge/NeoForge/Quilt/OptiFine,自动标注兼容性;
//    第三步 确认安装 —— 版本命名 + 加载器版本 + Java 需求提示,安装进度实时播报;
//    (client/libraries/assets/natives 全量安装,DownloadService 事件驱动进度);
// 3. 整合包导入:Modrinth/CF/Prism/自研格式自动识别(ModPackImportService)。
// UI 文案规范:统一使用「游戏版本」,不出现「实例」字眼。

using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class InstancesDeck : UserControl
{
    private readonly InstanceService _instances;
    private readonly GameInstallService _installer;
    private readonly GameLaunchService _launch;
    private readonly VersionManifestService _manifest;
    private readonly LoaderVersionProvider _loaderProvider;
    private readonly ModLoaderInstallService _loaderInstall;
    private readonly DownloadService _downloads;
    private readonly ModPackImportService _packs;
    private readonly JavaRuntimeService _javaRuntimes;
    private readonly ConfigService _config;
    private readonly InstanceExtrasService _extras;      // 分组/快照/磁盘缓存(LauncherX-4)
    private readonly InstanceCloneService _clone;        // Axolotl-5 实例快速克隆
    private readonly ModPackExportService _export;       // LauncherX-6 完整整合包导出
    private readonly ModPackLinkService _link;           // Celestial-6 整合包链接导入
    private readonly DependencyPreloadService _preload;  // Axolotl-6 后台预加载依赖

    // ---- 本地列表 ----
    private readonly StackPanel _localList = new();
    private readonly Dictionary<string, FrameworkElement> _cardById = new();   // 2026-09-26 批3:实例 Id → 版本卡(定位滚动/高亮用)
    private string _locateId = "";   // 2026-09-26 批3:安装/改加载器完成后待定位的实例 Id(渲染一次后消费)
    private readonly Border _localEmpty;
    private readonly StackPanel _localSection = new();
    // ---- 版本设置分区(与模组管理同款页内分区)----
    private readonly VersionSettingsPanel _settingsPanel = new();
    private string _pendingSettingsId = "";   // 版本卡「设置」带入的预选版本
    // ---- 版本工具分区(配置快照 / 日志崩溃 / 启动记录 / 脚本磁盘 / 环境校验 五页签)----
    private readonly InstanceToolsPanel _toolsPanel = new();
    private int _pendingToolsTab;             // 版本卡或侧边面板带入的目标页签
    private string _pendingToolsId = "";      // 带入的预选版本
    private readonly TextBox _localFilter = UIKit.TextBox(placeholder: "搜索版本(名称 / 版本号)");
    private readonly TextBlock _localCount = UIKit.Sub("", 12);
    // ---- 本地列表刷新节流(消除版本管理页偶发卡顿)----
    private readonly System.Windows.Threading.DispatcherTimer _filterDebounce;   // 搜索输入防抖:停顿后再重排,免每敲一键就全量重建
    private bool _scanning;         // 后台扫盘进行中:期间的重复刷新请求合并,避免并发重扫与列表竞态
    private bool _rescanQueued;     // 扫盘期间又收到刷新请求:完成后补扫一次,保证数据新鲜
    // ---- 侧边预览 + 拖拽归组 ----
    private readonly InstancePreviewPanel _preview = new();   // LauncherX-1 版本侧边预览(默认收起)
    private string _dragInstanceId = "";                       // LauncherX-4 正在拖拽归组的源版本 Id
    private Point _dragStart;

    // ---- 新装向导 ----
    private readonly StackPanel _createSection = new();
    private readonly StackPanel _step1 = new();
    private readonly StackPanel _step2 = new();
    private readonly StackPanel _step3 = new();
    private readonly TextBlock[] _stepLabels = new TextBlock[3];
    private readonly Border[] _stepChips = new Border[3];   // 步骤胶囊(当前步主色底发光)

    // 第一步:版本列表
    private readonly TextBox _searchBox = UIKit.TextBox(placeholder: "搜索版本号,如 1.20");
    private readonly ComboBox _sourceBox = UIKit.ComboBox();
    private readonly StackPanel _versionList = new();
    private readonly TextBlock _versionCount = UIKit.Sub("", 12);
    private FrameworkElement[] _typeChips = Array.Empty<FrameworkElement>();
    private int _typeFilter;   // 0=release 1=snapshot 2=远古 3=愚人节 4=all

    // 第二步:加载器选择
    private readonly TextBlock _selVersionText = UIKit.SubOneLine("", 12, 420);
    private readonly StackPanel _loaderCards = new();

    // 第三步:确认安装
    private readonly TextBlock _summaryText = UIKit.Sub("", 12);
    private readonly TextBox _nameBox = UIKit.TextBox(placeholder: "如:我的生存存档");
    private readonly ComboBox _loaderVersionBox = UIKit.ComboBox();
    private bool _loaderVersionExpanding;   // 二次下拉展开防递归
    private readonly TextBlock _javaHint = UIKit.Sub("", 12);
    private readonly ProgressBar _installProgress = UIKit.ProgressBar();
    private readonly TextBlock _installStatus = UIKit.Sub("就绪", 12);
    private readonly Button _installBtn;

    private List<MojangVersion> _versions = new();
    private List<LoaderVersionEntry> _loaderVersions = new();
    private MojangVersion? _selVersion;
    private string _selLoaderKind = "";
    private bool _installing;
    private bool _manifestFetching;
    private int _loaderFetchGen;   // 加载器版本拉取代次,防快速切换时旧请求覆盖新结果(第三步下拉兜底路径)
    private int _loaderCardGen;    // 第二步加载器卡片代次:切换游戏版本后作废在途的异步回填,防陈旧列表写回新卡片
    private string? _selLoaderVersion;   // 第二步选定的具体加载器版本(原版/未选为 null)
    private readonly Dictionary<string, List<LoaderVersionEntry>> _loaderListCache = new();   // 第二步已拉取的加载器版本列表(第三步复用免二次请求)
    private readonly List<LoaderCardRef> _loaderCardRefs = new();
    private bool _nameAuto = true;       // 版本名是否仍自动跟随选择变化(用户手改后停止跟随)
    private bool _applyingAutoName;      // 程序写入名称的标记(不触发"用户手改"判定)

    private static readonly string[] TypeFilters = { "正式版", "快照", "远古版", "愚人节版", "全部" };
    private static readonly (string Label, string Key, string Desc)[] Loaders =
    {
        ("原版", "", "不安装任何加载器,纯净 Minecraft 体验"),
        ("Fabric", "fabric", "轻量现代加载器,模组生态活跃,启动快"),
        ("Forge", "forge", "老牌加载器,大型模组整合包兼容性好"),
        ("NeoForge", "neoforge", "Forge 社区分支,1.20.2+ 新选择"),
        ("Quilt", "quilt", "Fabric 衍生,兼容大部分 Fabric 模组"),
        ("OptiFine", "optifine", "高清修复与光影支持,可独立使用")
    };

    public InstancesDeck()
    {
        _instances = App.Services.GetRequiredService<InstanceService>();
        _installer = App.Services.GetRequiredService<GameInstallService>();
        _launch = App.Services.GetRequiredService<GameLaunchService>();
        _manifest = App.Services.GetRequiredService<VersionManifestService>();
        _loaderProvider = App.Services.GetRequiredService<LoaderVersionProvider>();
        _loaderInstall = App.Services.GetRequiredService<ModLoaderInstallService>();
        _downloads = App.Services.GetRequiredService<DownloadService>();
        _packs = App.Services.GetRequiredService<ModPackImportService>();
        _javaRuntimes = App.Services.GetRequiredService<JavaRuntimeService>();
        _config = App.Services.GetRequiredService<ConfigService>();
        _extras = App.Services.GetRequiredService<InstanceExtrasService>();
        _clone = App.Services.GetRequiredService<InstanceCloneService>();
        _export = App.Services.GetRequiredService<ModPackExportService>();
        _link = App.Services.GetRequiredService<ModPackLinkService>();
        _preload = App.Services.GetRequiredService<DependencyPreloadService>();

        // 侧边预览面板把「克隆/导出」交回本页跑(需要弹框与进度),分组变化则重排列表
        _preview.CloneRequested += CloneFlow;
        _preview.ExportRequested += ExportFlow;
        _preview.GroupChanged += () => Dispatcher.BeginInvoke(RenderLocalList);
        // 2026-09-26 批3 收口:版本设置里改加载器生成派生版本后,回本列表重扫并定位高亮该版本
        _settingsPanel.Derived = id =>
        {
            _locateId = id;
            Dispatcher.BeginInvoke(RenderLocalList);
        };

        _localEmpty = UIKit.EmptyHint("还没有本地版本,点击左侧「新装版本」开始安装");
        _installBtn = UIKit.Button("开始安装", primary: true, onClick: OnInstall, height: 42);
        _installBtn.MinWidth = 160;

        // 搜索防抖计时器:连续敲键只在停顿 220ms 后重排一次列表(每敲一键就全量重建版本卡是卡顿主因之一)
        _filterDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _filterDebounce.Tick += (_, _) => { _filterDebounce.Stop(); RenderLocalList(); };

        BuildLocalSection();
        BuildCreateSection();

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Grid { Children = { _localSection, _createSection, _settingsPanel, _toolsPanel } }
        };
        ShowSection(0);   // 进入默认分区(本地版本),其 default 分支已触发一次 RefreshLocal,无需再重复扫盘

        // 安装进度:全局下载事件;游戏退出:刷新列表复位「运行中」按钮
        // 订阅跟随页面可见性(Loaded/Unloaded),避免切页后丢失事件导致状态不再同步
        Loaded += (_, _) =>
        {
            _downloads.OverallProgressChanged += OnOverallProgress;
            _launch.GameExited += OnGameExited;
        };
        Unloaded += (_, _) =>
        {
            _downloads.OverallProgressChanged -= OnOverallProgress;
            _launch.GameExited -= OnGameExited;
            // 防抖表必须停:DispatcherTimer 强引用页面对象,不停会泄漏且切页后仍在后台
            // 触发 RenderLocalList(白重建列表,内存与 CPU 双浪费)
            _filterDebounce.Stop();
        };
        // 排版:安装向导状态/总结/Java 提示限宽,长文本换行不撑破卡片(Sub 已默认 Wrap)
        _installStatus.MaxWidth = 620;
        _summaryText.MaxWidth = 520;
        _javaHint.MaxWidth = 460;
    }

    /// <summary>游戏进程退出(含用户手动关闭):刷新本地列表,运行中按钮复位为启动(后台线程触发,需切 UI 线程)</summary>
    private void OnGameExited() => Dispatcher.BeginInvoke(() =>
    {
        Shell()?.SetStatus("游戏已退出");
        RenderLocalList();
    });

    /// <summary>二级菜单入口:0=本地版本 1=新装版本 2=版本设置(叠放交叉过渡,与模组管理一致)</summary>
    public void ShowSection(int idx)
    {
        MotionKit.SwapOverlay(new UIElement[] { _localSection, _createSection, _settingsPanel, _toolsPanel }, idx);
        switch (idx)
        {
            case 1:
                SetStep(1);
                _ = EnsureManifestAsync();   // 进入新装页自动备妥清单(有缓存时零网络)
                break;
            case 2:
                _settingsPanel.Enter(_pendingSettingsId);   // 版本卡带入的预选版本;空则恢复上次选中
                _pendingSettingsId = "";
                break;
            case 3:
                _toolsPanel.Enter(_pendingToolsTab, _pendingToolsId);   // 页签与预选版本由调用方带入
                _pendingToolsId = "";
                break;

            default:
                RefreshLocal();
                break;
        }
    }

    /// <summary>打开「版本工具」分区并定位到指定页签与版本(供版本卡、侧边预览面板、ShellWindow.OpenInstanceTools 调用)</summary>
    public void OpenTools(int tab, string instanceId)
    {
        _pendingToolsTab = tab;
        _pendingToolsId = instanceId ?? "";
        ShowSection(3);
    }


    /// <summary>紧凑按钮辅助:收紧内边距适配小尺寸</summary>
    private static Button CompactBtn(Button b, double minWidth = 76)
    {
        b.MinWidth = minWidth;
        if (b.Content is Grid g && g.Children.Count > 0 && g.Children[0] is Border bl)
            bl.Padding = new Thickness(8, 0, 8, 0);
        if (b.Content is Grid g2 && g2.Children.Count > 1 && g2.Children[1] is Border bl2)
            bl2.Padding = new Thickness(8, 0, 8, 0);
        b.Padding = new Thickness(8, 0, 8, 0);
        return b;
    }

    /// <summary>判断命中点是否落在某个按钮内(拖拽/预览点击要避开按钮,免得和按钮点击打架)</summary>
    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Button) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    // ==================== 本地版本 ====================

    private void BuildLocalSection()
    {
        // 右上角操作按钮组(仅视觉统一,功能不增不减):四个按钮全部走 UIKit.Button 全局规格
        // (高度 36 / 圆角与内边距由高度推导 / hover 淡入 + 按下 0.97 内缩反馈),并统一为
        // 等宽固定尺寸(取能容纳最长文案「导入已有游戏目录」的宽度),避免宽窄参差显得
        // “大小不一样”;组内间距显式 10px 均分、整组垂直居中,作为单个容器交给 PageHeader 右对齐。
        var importBtn = UIKit.Button("导入整合包", primary: false, onClick: OnImportPack, height: 36);
        var importDirBtn = UIKit.Button("导入已有游戏目录", primary: false, onClick: OnImportExistingDir, height: 36);
        var importLazyBtn = UIKit.Button("导入懒人整合包", primary: false, onClick: OnImportLazyPack, height: 36);
        var refreshBtn = UIKit.Button("刷新", primary: false, onClick: RefreshLocal, height: 36);
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var headerBtns = new[] { importBtn, importDirBtn, importLazyBtn, refreshBtn };
        for (int i = 0; i < headerBtns.Length; i++)
        {
            headerBtns[i].Width = 150;                                  // 等宽:四按钮尺寸完全一致
            headerBtns[i].VerticalAlignment = VerticalAlignment.Center;
            if (i > 0) headerBtns[i].Margin = new Thickness(10, 0, 0, 0);   // 组内等间距
            headerActions.Children.Add(headerBtns[i]);
        }
        _localSection.Children.Add(UIKit.PageHeader("版本管理", "本地游戏版本的启动、复制、维护与导出", headerActions));

        // 筛选工具栏卡
        _localFilter.Width = 280;
        _localFilter.VerticalAlignment = VerticalAlignment.Center;
        // 输入防抖:停顿后再重排,避免每敲一个字符就全量重建列表(大批量版本时逐键重建会明显卡顿)
        _localFilter.TextChanged += (_, _) => { _filterDebounce.Stop(); _filterDebounce.Start(); };
        _localCount.VerticalAlignment = VerticalAlignment.Center;
        var filterLabel = UIKit.Sub("筛选", 12);
        filterLabel.VerticalAlignment = VerticalAlignment.Center;
        filterLabel.Margin = new Thickness(0, 0, 10, 0);
        _localFilter.Margin = new Thickness(0, 0, 12, 0);
        _localSection.Children.Add(UIKit.Card(
            UIKit.H(filterLabel, _localFilter, _localCount), pad: 12, topGap: 8));

        // 左列版本列表 + 右列侧边预览(LauncherX-1):预览默认收起不占宽度,列表布局与原来一致
        var listCol = UIKit.V(_localEmpty, _localList);
        _localSection.Children.Add(new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { listCol.Col(0), _preview.Col(1) }
        });
    }

    /// <summary>刷新本地版本:磁盘扫描(含旧结构迁移)在后台线程跑,不阻塞 UI;完成后回 UI 线程重排列表。
    /// 扫盘进行中的重复请求合并为一次补扫,避免频繁切页/点击造成并发重扫与卡顿。</summary>
    private async void RefreshLocal()
    {
        if (_scanning) { _rescanQueued = true; return; }
        _scanning = true;
        Shell()?.SetStatus("正在扫描本地版本…");
        try { await _instances.RefreshInstancesAsync(); }
        catch (Exception ex) { App.WriteAppLog($"[版本] 后台扫描异常:{ex.Message}"); }
        finally { _scanning = false; }
        RenderLocalList();
        Shell()?.SetStatus("就绪");
        if (_rescanQueued) { _rescanQueued = false; RefreshLocal(); }
    }

    /// <summary>按筛选关键词渲染本地版本列表;无关键词时按用户自建分组归类(LauncherX-4)</summary>
    private void RenderLocalList()
    {
        _localList.Children.Clear();
        _cardById.Clear();
        string kw = _localFilter.Text.Trim();
        var visible = string.IsNullOrEmpty(kw)
            ? _instances.Instances
            : _instances.Instances.Where(i =>
                i.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                i.VersionId.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();

        _localEmpty.FadeToggle(_instances.Instances.Count == 0);
        _localCount.Text = _instances.Instances.Count == 0 ? "" :
            (visible.Count == _instances.Instances.Count
                ? $"共 {_instances.Instances.Count} 个版本"
                : $"匹配 {visible.Count} / 共 {_instances.Instances.Count} 个版本");

        // 搜索态:跨分组平铺命中结果,不折叠,方便一眼扫全
        if (!string.IsNullOrEmpty(kw))
        {
            foreach (var inst in visible)
                _localList.Children.Add(MakeVersionCard(inst));
            LocatePendingCard();
            return;
        }

        // 常态:按分组归类渲染。没建过任何分组时不显示分组头,与原来平铺完全一致
        var groups = _extras.GroupsSnapshot();
        var validIds = new HashSet<string>(groups.Select(g => g.Id));
        var groupOf = visible.ToDictionary(i => i.Id, i =>
        {
            string gid = _extras.Get(i.Id).GroupId ?? "";
            return validIds.Contains(gid) ? gid : "";
        });
        foreach (var g in groups)
        {
            var members = visible.Where(i => groupOf[i.Id] == g.Id).ToList();
            _localList.Children.Add(MakeGroupHeader(g, members.Count));
            if (g.Collapsed) continue;
            foreach (var m in members)
                _localList.Children.Add(MakeVersionCard(m));
        }
        var loose = visible.Where(i => groupOf[i.Id] == "").ToList();
        if (groups.Count > 0 && loose.Count > 0)
            _localList.Children.Add(MakeUngroupedHeader(loose.Count));
        foreach (var i in loose)
            _localList.Children.Add(MakeVersionCard(i));
        LocatePendingCard();
    }

    /// <summary>定位待高亮版本卡(2026-09-26 批3):新装完成 / 改加载器派生后,
    /// 渲染完毕把目标卡滚到可见并短暂主色描边提示,只消费一次</summary>
    private void LocatePendingCard()
    {
        if (string.IsNullOrEmpty(_locateId)) return;
        string id = _locateId;
        _locateId = "";
        if (!_cardById.TryGetValue(id, out var card) || card is not Border b) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                card.BringIntoView();
                var oldBrush = b.BorderBrush;
                var oldThickness = b.BorderThickness;
                b.SetResourceReference(Border.BorderBrushProperty, "T.Primary");
                b.BorderThickness = new Thickness(2);
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    b.BorderBrush = oldBrush;
                    b.BorderThickness = oldThickness;
                };
                timer.Start();
            }
            catch (Exception ex) { App.WriteAppLog("[版本] 定位高亮异常:" + ex.Message); }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>分组标题行:点击折叠/展开,可把版本卡拖到这里归入本组(LauncherX-4)</summary>
    private FrameworkElement MakeGroupHeader(InstanceGroup g, int count)
    {
        var arrow = UIKit.Text(g.Collapsed ? "▸" : "▾", 13, FontWeights.SemiBold);
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "T.Primary");
        arrow.VerticalAlignment = VerticalAlignment.Center;
        var title = UIKit.Text(g.Name, 13.5, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(8, 0, 0, 0);
        var cnt = UIKit.Sub($"{count} 个版本", 11.5);
        cnt.VerticalAlignment = VerticalAlignment.Center;
        cnt.Margin = new Thickness(8, 0, 0, 0);

        var head = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 10, 0, 0),
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            Cursor = Cursors.Hand,
            AllowDrop = true,
            Child = UIKit.H(arrow, title, cnt)
        };
        head.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");
        head.MouseLeftButtonUp += (_, _) => { _extras.SetGroupCollapsed(g.Id, !g.Collapsed); RenderLocalList(); };
        head.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent("FufuInstanceId") ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        head.Drop += (_, e) =>
        {
            if (e.Data.GetData("FufuInstanceId") is string id && !string.IsNullOrEmpty(id))
                AssignGroup(id, g.Id, g.Name);
            e.Handled = true;
        };
        return head;
    }

    /// <summary>「未分组」标题行:仅当已建过分组且存在未归组版本时出现,拖到这里可移出分组</summary>
    private FrameworkElement MakeUngroupedHeader(int count)
    {
        var title = UIKit.Text("未分组", 13, FontWeights.SemiBold);
        title.SetResourceReference(TextBlock.ForegroundProperty, "T.ForegroundDim");
        title.VerticalAlignment = VerticalAlignment.Center;
        var cnt = UIKit.Sub($"{count} 个版本", 11.5);
        cnt.VerticalAlignment = VerticalAlignment.Center;
        cnt.Margin = new Thickness(8, 0, 0, 0);

        var head = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 10, 0, 0),
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            AllowDrop = true,
            Child = UIKit.H(title, cnt)
        };
        head.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");
        head.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent("FufuInstanceId") ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        head.Drop += (_, e) =>
        {
            if (e.Data.GetData("FufuInstanceId") is string id && !string.IsNullOrEmpty(id))
                AssignGroup(id, "", "未分组");
            e.Handled = true;
        };
        return head;
    }

    /// <summary>把版本归入/移出分组并重排列表(groupId 传空 = 移出)</summary>
    private void AssignGroup(string instanceId, string groupId, string groupName)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        _extras.SetInstanceGroup(instanceId, groupId);
        RenderLocalList();
        Shell()?.SetStatus(string.IsNullOrEmpty(groupId)
            ? $"「{inst?.Name}」已移出分组"
            : $"「{inst?.Name}」已归入「{groupName}」");
    }

    private FrameworkElement MakeVersionCard(GameInstance inst)
    {
        // 本地版本卡图标:按清单类型区分方块(未缓存清单时默认草方块);卡内图标 36px 醒目档位
        var mv = _manifest.CachedManifest?.Versions.FirstOrDefault(x => x.Id == inst.VersionId);
        var icon = IconKit.McBlock(36, mv == null ? "grass" : IconKindOf(mv));
        icon.Margin = new Thickness(0, 0, 10, 0);

        // 第一行:版本名称(14px,卡内主标题)
        var nameText = UIKit.Text(inst.Name, 14, FontWeights.SemiBold);
        nameText.VerticalAlignment = VerticalAlignment.Center;
        nameText.TextTrimming = TextTrimming.CharacterEllipsis;   // 长实例名省略显示,悬停看完整
        nameText.ToolTip = inst.Name;

        // 第二行:三徽章独占一行(全局统一规格放大后不再挤压)
        var versionBadge = UIKit.Badge($"MC {inst.VersionId}", "T.ForegroundDim");
        var loaderBadge = string.IsNullOrEmpty(inst.ModLoader)
            ? IsShellInstance(inst)
                ? ShellLoaderBadge(inst)
                : UIKit.Badge("原版", "T.ForegroundDim")
            : LoaderBadgeWithLogo(inst.ModLoader, inst.ModLoaderVersion);
        var javaBadge = UIKit.Badge($"Java {inst.JavaMajorVersion}", "T.ForegroundDim");
        // 安装未完成红标:失败保留的实例在卡片上醒目提示,避免用户误当已完成版本启动
        var incompleteBadge = !inst.InstallComplete
            ? UIKit.Badge("安装未完成", "T.Danger")
            : null;
        // 2026-09-25:三徽章行改用 WrapPanel 换行面板——长加载器徽章(如 Forge 10.13.4.1614)
        // 不再把右侧 Java 徽章挤出裁剪区(曾出现 Java 徽章被截成 "Ja" 的显示 bug)
        var infoRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 5, 0, 0)
        };
        foreach (var b in new[] { versionBadge, loaderBadge, javaBadge })
        {
            b.Margin = new Thickness(0, 2, 7, 2);
            infoRow.Children.Add(b);
        }
        if (incompleteBadge != null)
        {
            incompleteBadge.Margin = new Thickness(0, 2, 7, 2);
            infoRow.Children.Add(incompleteBadge);
        }

        // 第三行:上次游玩时间 + 游玩时长独占一行(11.5px,完整可见不被挤压)
        string lastPlayed = inst.LastPlayedAt.Year > 2000
            ? inst.LastPlayedAt.ToString("yyyy-MM-dd HH:mm")
            : "从未启动";
        string playTime = inst.TotalPlayTimeSeconds > 0 ? $" · 游玩 {FmtDuration(inst.TotalPlayTimeSeconds)}" : "";
        var playInfo = UIKit.Sub("上次启动 " + lastPlayed + playTime, 11.5);
        playInfo.VerticalAlignment = VerticalAlignment.Center;
        playInfo.Margin = new Thickness(0, 5, 0, 0);

        bool running = _launch.IsInstanceRunning(inst.Id);
        // 行内按钮全局统一 32px 档位 + MinWidth 76,与账号卡/模组卡行内按钮同规格,禁止私自改小
        var launchBtn = UIKit.Button(running ? "运行中" : "启动",
            primary: true, onClick: () => QuickLaunch(inst), height: 32);
        CompactBtn(launchBtn);
        launchBtn.IsEnabled = !running;
        var renameBtn = UIKit.Button("重命名", primary: false, onClick: () => Rename(inst), height: 32);
        var setBtn = UIKit.Button("设置", primary: false, onClick: () =>
        {
            _pendingSettingsId = inst.Id;
            ShowSection(2);
        }, height: 32);
        var copyBtn = UIKit.Button("复制", primary: false, onClick: () => CloneFlow(inst.Id), height: 32);
        var dirBtn = UIKit.Button("目录", primary: false, onClick: () => OpenDir(inst), height: 32);
        var exportBtn = UIKit.Button("导出", primary: false, onClick: () => ExportFlow(inst.Id), height: 32);
        var delBtn = UIKit.Button("删除", primary: false, onClick: () => Delete(inst), height: 32);
        foreach (var b in new[] { renameBtn, setBtn, copyBtn, dirBtn, exportBtn, delBtn }) CompactBtn(b);

        // 操作区用换行面板:窄窗口下按钮自动换行,不挤压左侧信息区(版本名/徽章/游玩信息始终完整)
        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        foreach (var b in new[] { launchBtn, setBtn, renameBtn, copyBtn, dirBtn, exportBtn, delBtn })
        {
            b.Margin = new Thickness(0, 2, 8, 2);
            actions.Children.Add(b);
        }

        // 2026-09-26 修复:详情面板展开后列表变窄,原「图标|信息|按钮」单行 Grid 中
        // 按钮 WrapPanel 在 Auto 列测量无限宽→不换行占满行宽→信息 Star 列被压扁→每字换行→卡片高度爆炸。
        // 改两行:行1 图标+信息(名称/徽章/游玩);行2 操作按钮 WrapPanel 占满卡片宽自然换行(至多2行)。
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            Children = { icon, UIKit.V(nameText, infoRow, playInfo).Col(1) }
        };
        actions.Margin = new Thickness(0, 8, 0, 0);   // 从右侧移到按钮行:间距统一
        var cardContent = UIKit.V(grid, actions);

        // 卡片加高:pad 14 + topGap 10,三行信息舒展不挤压
        var card = UIKit.Card(cardContent, pad: 14, topGap: 10);
        card.RenderTransform = new TranslateTransform();
        card.MouseEnter += (_, _) => card.Lift(-1);
        card.MouseLeave += (_, _) => card.Lift(0);
        // LauncherX-1:点卡片空白处在右侧展开该版本详情(点按钮时事件被按钮吃掉,不误触)
        card.MouseLeftButtonUp += (_, _) => _preview.Show(inst);
        // LauncherX-4:按住卡片拖到分组标题上即可归组
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject)) { _dragInstanceId = ""; return; }
            _dragInstanceId = inst.Id;
            _dragStart = e.GetPosition(this);
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || string.IsNullOrEmpty(_dragInstanceId)) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            string id = _dragInstanceId;
            _dragInstanceId = "";
            try { DragDrop.DoDragDrop(card, new DataObject("FufuInstanceId", id), DragDropEffects.Move); }
            catch (Exception ex) { App.WriteAppLog($"[版本] 拖拽归组失败:{ex.Message}"); }
        };
        _cardById[inst.Id] = card;   // 2026-09-26 批3:登记供安装/改加载器后定位高亮
        return card;
    }

    // ==================== 空壳实例警示(暗病修复) ====================

    /// <summary>空壳实例判定:名字含加载器关键字但元数据未写入加载器信息
    /// (加载器安装失败残留)。卡面给出警示,启动时还会弹窗让用户补装或按原版。</summary>
    private static bool IsShellInstance(GameInstance inst)
    {
        if (!string.IsNullOrEmpty(inst.ModLoader)) return false;
        if (string.IsNullOrWhiteSpace(inst.Name)) return false;
        string low = inst.Name.ToLowerInvariant();
        return low.Contains("neoforge") || low.Contains("fabric") || low.Contains("quilt")
            || low.Contains("forge") || low.Contains("optifine");
    }

    /// <summary>空壳警示徽章:琥珀色「⚠ 加载器未装」+ ToolTip 说明</summary>
    private static Border ShellLoaderBadge(GameInstance inst)
    {
        return UIKit.WarningBadge("⚠ 加载器未装",
            $"版本名包含加载器关键字,但未检测到已安装的加载器本体(可能是安装中断残留)。\n" +
            $"启动该版本时会弹出补装选项;直接启动只会进原版,模组不会被加载。");
    }

    private async void QuickLaunch(GameInstance inst)
    {
        try
        {
            if (!inst.InstallComplete)
            {
                bool reinstall = DialogKit.Confirm(
                    $"版本「{inst.Name}」安装未完成(可能因网络中断)。\n\n" +
                    $"已下载的游戏文件已保留,重新安装会秒装(幂等复用)。\n" +
                    $"是否现在前往「新装版本」重新安装该版本?",
                    "版本安装未完成", Window.GetWindow(this));
                if (reinstall)
                {
                    _installing = false;
                    _installBtn.IsEnabled = true;
                    ShowSection(0);   // 回到「选择游戏版本」向导,用户重选同版本即复用文件
                }
                return;
            }
            Shell()?.SetStatus($"正在启动 {inst.Name}…");
            var result = await _launch.LaunchAsync(inst.Id);
            if (result.Success)
            {
                // 2026-09-25 补全:启动成功即证明文件完整,自动纠正残留的「安装未完成」标记
                // (下载中心重试补下文件成功后,实例仍可能带着失败时留下的 false)
                if (!inst.InstallComplete) { inst.InstallComplete = true; _instances.SaveInstance(inst); }
                Shell()?.SetStatus($"游戏运行中:{inst.Name}");
                RefreshLocal();
            }
            else
            {
                Shell()?.SetStatus("启动失败", warning: true);
                DialogKit.Error(result.ErrorMessage, "启动失败", Window.GetWindow(this));
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本] 操作异常:" + ex.Message);
            Shell()?.SetStatus("操作失败,详见日志", warning: true);
        }
    }

    private void Rename(GameInstance inst)
    {
        string? name = DialogKit.Input("新的版本名称:", "重命名", inst.Name, Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name)) return;
        _instances.RenameInstance(inst.Id, name.Trim());
        RefreshLocal();
        Shell()?.SetStatus("已重命名");
    }

    /// <summary>Axolotl-5 实例快速克隆:模组/各项设置/JVM 参数整套继承给新版本,只需改个名</summary>
    private void CloneFlow(string sourceInstanceId)
    {
        var src = _instances.Instances.FirstOrDefault(i => i.Id == sourceInstanceId);
        if (src == null) { DialogKit.Warn("找不到要克隆的版本", owner: Window.GetWindow(this)); return; }
        if (_clone.IsBusy) { DialogKit.Info("已有一个克隆任务在进行中,请稍候再试", owner: Window.GetWindow(this)); return; }

        string suggest = InstanceCloneService.SuggestName(src.Name, _instances.Instances);
        string? name = DialogKit.Input("新版本的名称:", "快速克隆版本", suggest, Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name)) return;
        bool copySaves = DialogKit.Confirm(
            $"要连存档(saves)一起克隆吗?\n\n「是」= 连存档一起复制(体积大、含玩家数据)\n「否」= 只克隆模组、配置与 JVM 参数(推荐)",
            "克隆范围", Window.GetWindow(this));
        var options = new CloneOptions { CopySaves = copySaves };
        App.WriteAppLog($"[版本] 开始克隆:{src.Name} → {name.Trim()}(存档={(copySaves ? "含" : "不含")})");
        try
        {
            var result = DialogKit.RunWithProgress("正在克隆版本",
                report => _clone.CloneAsync(src.Id, name.Trim(), options, new Progress<string>(report)),
                Window.GetWindow(this));
            if (result.Ok)
            {
                RefreshLocal();
                if (result.Instance != null) _preview.Show(result.Instance);
                Shell()?.SetStatus($"已克隆为「{result.Instance?.Name}」");
                DialogKit.Success($"克隆完成:\n{result.Detail}", "克隆成功", Window.GetWindow(this));
                App.WriteAppLog($"[版本] 克隆成功:{src.Name} → {result.Instance?.Name} / {result.Detail}");
            }
            else
            {
                Shell()?.SetStatus("克隆失败", warning: true);
                DialogKit.Error("克隆失败:" + result.Message, owner: Window.GetWindow(this));
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本] 克隆异常:{ex}");
            DialogKit.Error("克隆异常:" + ex.Message, owner: Window.GetWindow(this));
        }
    }

    private void OpenDir(GameInstance inst)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", _instances.GetInstanceDir(inst.Id))?.Dispose(); }
        catch (Exception ex) { DialogKit.Error("打开目录失败:" + ex.Message, owner: Window.GetWindow(this)); }
    }

    /// <summary>LauncherX-6 完整整合包导出:两种模式(仅用户内容 / 含游戏本体),可选是否带存档</summary>
    private void ExportFlow(string instanceId)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null) { DialogKit.Warn("找不到要导出的版本", owner: Window.GetWindow(this)); return; }

        int mode = DialogKit.Choice("选择导出模式:",
            new[]
            {
                "仅用户内容(模组 / 配置 / 资源,体积小,换机需联网补游戏本体)",
                "含游戏本体(库文件 / 资源全带上,离线可用,体积大)"
            },
            "导出整合包", Window.GetWindow(this));
        if (mode < 0) return;
        bool withCore = mode == 1;
        bool includeSaves = DialogKit.Confirm(
            "要连存档(saves)一起打包吗?\n\n「是」= 连存档一起导出\n「否」= 不含存档(推荐,存档属于玩家数据)",
            "导出范围", Window.GetWindow(this));
        var options = new ModPackExportOptions
        {
            Mode = withCore ? ModPackExportMode.WithGameCore : ModPackExportMode.UserContentOnly,
            IncludeSaves = includeSaves
        };

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出版本为整合包",
            Filter = "ZIP 压缩包|*.zip",
            FileName = inst.Name + (withCore ? "_完整版.zip" : "_精简版.zip")
        };
        if (dlg.ShowDialog() != true) return;
        App.WriteAppLog($"[版本] 开始导出整合包:{inst.Name} → {dlg.FileName}({options.ModeDisplay} / 存档={(includeSaves ? "含" : "不含")})");
        try
        {
            var result = DialogKit.RunWithProgress("正在导出整合包",
                report => _export.ExportAsync(inst.Id, dlg.FileName, options, new Progress<string>(report)),
                Window.GetWindow(this));
            if (result.Ok)
            {
                Shell()?.SetStatus($"已导出到 {System.IO.Path.GetFileName(result.ZipPath)}");
                DialogKit.Success($"导出完成({options.ModeDisplay}):\n{result.ZipPath}\n\n{result.Detail}", "导出成功", Window.GetWindow(this));
                App.WriteAppLog($"[版本] 导出成功:{result.ZipPath} / {result.Detail}");
            }
            else
            {
                Shell()?.SetStatus("导出失败", warning: true);
                DialogKit.Error("导出失败:" + result.Message, owner: Window.GetWindow(this));
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本] 导出异常:{ex}");
            DialogKit.Error("导出异常:" + ex.Message, owner: Window.GetWindow(this));
        }
    }

    private async void Delete(GameInstance inst)
    {
        try
        {
            if (!DialogKit.Confirm(
                    $"确定删除版本「{inst.Name}」吗?\n将移除其配置与模组目录;存档完整保留,共享的游戏本体在无其它版本使用时一并清理。",
                    "删除版本", Window.GetWindow(this)))
                return;
            var (ok, error) = await _instances.UninstallInstanceAsync(inst.Id);
            if (ok) { RefreshLocal(); Shell()?.SetStatus("已删除"); }
            else DialogKit.Error("删除失败:" + error, owner: Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本] 操作异常:" + ex.Message);
            Shell()?.SetStatus("操作失败,详见日志", warning: true);
        }
    }

    /// <summary>导入已有 .minecraft 目录为本地版本</summary>
    private void OnImportExistingDir()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择已有的 .minecraft 游戏目录" };
        if (dlg.ShowDialog() != true) return;
        string? name = DialogKit.Input("给导入的版本起个名字:", "导入游戏目录",
            System.IO.Path.GetFileName(dlg.FolderName), Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name)) return;
        App.WriteAppLog($"[版本] 开始导入已有游戏目录:{dlg.FolderName} → {name.Trim()}");
        ImportDirectoryFlow(dlg.FolderName, name.Trim(), "导入游戏目录");
    }

    /// <summary>懒人整合包入口:压缩包(zip)或已解压文件夹均支持</summary>
    private void OnImportLazyPack()
    {
        int kind = DialogKit.Choice("懒人包是压缩包还是已解压的文件夹?",
            new[] { "压缩包(.zip)", "文件夹(已解压)" }, "导入懒人整合包", Window.GetWindow(this));
        if (kind < 0) return;

        if (kind == 0)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择懒人包压缩包",
                Filter = "压缩包|*.zip|所有文件|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            App.WriteAppLog($"[版本] 开始导入懒人包压缩包:{dlg.FileName}");
            RunImport("导入懒人包", report => _packs.ImportAsync(dlg.FileName, null, report));
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择懒人包文件夹(游戏根目录)" };
            if (dlg.ShowDialog() != true) return;
            string? name = DialogKit.Input("给导入的版本起个名字:", "导入懒人包",
                System.IO.Path.GetFileName(dlg.FolderName), Window.GetWindow(this));
            if (string.IsNullOrWhiteSpace(name)) return;
            App.WriteAppLog($"[版本] 开始导入懒人包文件夹:{dlg.FolderName} → {name.Trim()}");
            ImportDirectoryFlow(dlg.FolderName, name.Trim(), "导入懒人包");
        }
    }

    /// <summary>目录导入统一流程:进度窗 + 版本缺失时手动指定版本兜底</summary>
    private void ImportDirectoryFlow(string dir, string name, string title)
    {
        try
        {
            var probe = DialogKit.RunWithProgress(title, report => _packs.ImportDirAsync(dir, name, report), Window.GetWindow(this));
            if (probe.NeedManualVersion)
            {
                // 版本未识别/缺失:手动指定版本后继续导入
                DialogKit.Info("目录中未检测到游戏版本本体。\n请从列表中选择该目录对应的游戏版本,将自动下载对应版本核心。",
                    "需要指定游戏版本", Window.GetWindow(this));
                var versions = DialogKit.RunWithProgress("获取版本列表", async _ => await _packs.GetKnownVersionsAsync(), Window.GetWindow(this));
                if (versions.Count == 0)
                {
                    DialogKit.Error("获取版本列表失败,请检查网络后重试。", owner: Window.GetWindow(this));
                    return;
                }
                string? picked = DialogKit.PickFromList($"「{name}」对应的游戏版本是?", versions, "手动指定游戏版本", Window.GetWindow(this));
                if (string.IsNullOrEmpty(picked)) return;
                App.WriteAppLog($"[版本] 手动指定版本:{picked}");
                var r2 = DialogKit.RunWithProgress(title,
                    report => _packs.ImportDirWithVersionAsync(dir, name, picked, report), Window.GetWindow(this));
                ShowImportResult(r2);
                return;
            }
            ShowImportResult(probe);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本] 目录导入异常:{ex}");
            DialogKit.Error("导入异常:" + ex.Message, owner: Window.GetWindow(this));
        }
    }

    /// <summary>整合包导入统一走进度窗,结果统一中文提示</summary>
    private void RunImport(string title, Func<Action<string>, System.Threading.Tasks.Task<ModPackImportResult>> work)
    {
        try
        {
            var result = DialogKit.RunWithProgress(title, work, Window.GetWindow(this));
            ShowImportResult(result);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本] {title}异常:{ex}");
            DialogKit.Error($"{title}异常:" + ex.Message, owner: Window.GetWindow(this));
        }
    }

    private void ShowImportResult(ModPackImportResult result)
    {
        RefreshLocal();
        if (result.Ok)
        {
            Shell()?.SetStatus($"已导入「{result.Instance?.Name}」");
            DialogKit.Success(result.Message, "导入完成", Window.GetWindow(this));
        }
        else
        {
            Shell()?.SetStatus("导入失败", warning: true);
            DialogKit.Error("导入失败:" + result.Message, owner: Window.GetWindow(this));
        }
    }

    // ==================== 新装向导(三步)====================

    private void BuildCreateSection()
    {
        var packImportBtn = UIKit.Button("导入整合包", primary: false, onClick: OnImportPack, height: 36);   // 2026-09-26 支持主流第三方启动器格式,去格式后缀
        _createSection.Children.Add(UIKit.PageHeader("新装版本", "选择游戏版本 → 选择加载器 → 确认安装,三步完成", packImportBtn));
        _createSection.Children.Add(BuildStepBar());

        BuildStep1();
        BuildStep2();
        BuildStep3();
        _createSection.Children.Add(_step1);
        _createSection.Children.Add(_step2);
        _createSection.Children.Add(_step3);
        SetStep(1);
    }

    /// <summary>顶部步骤条:① 选版本 → ② 选加载器 → ③ 确认安装(当前步高亮)</summary>
    private UIElement BuildStepBar()
    {
        var names = new[] { "选择游戏版本", "选择模组加载器", "确认并安装" };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                var sep = UIKit.Sub("→", 13);
                sep.VerticalAlignment = VerticalAlignment.Center;
                sep.Margin = new Thickness(8, 0, 8, 0);
                row.Children.Add(sep);
            }
            var label = UIKit.Text($"{i + 1}  {names[i]}", 13, FontWeights.Medium);
            label.VerticalAlignment = VerticalAlignment.Center;
            _stepLabels[i] = label;
            var chip = UIKit.Panel(label, pad: 8);
            chip.WithRef("T.SurfaceAlt", "T.Border");
            chip.BorderThickness = new Thickness(1);
            chip.Tag = i;
            _stepChips[i] = chip;
            row.Children.Add(chip);
        }
        var wrap = new Border { Padding = new Thickness(0, 0, 0, 14), Child = row };
        return wrap;
    }

    private int _curStep = 1;   // 当前向导步(方向感动画依据)

    private void SetStep(int step)
    {
        // 方向感过渡:前进自下浮现/后退自上浮现(步骤面板是堆叠容器,旧步立即折叠避免重叠)
        int dir = step >= _curStep ? 1 : -1;
        _curStep = step;
        MotionKit.SwapStacked(new UIElement[] { _step1, _step2, _step3 }, step - 1, dir);

        for (int i = 0; i < 3; i++)
        {
            bool active = i + 1 == step;
            _stepLabels[i].SetResourceReference(TextBlock.ForegroundProperty,
                active ? "T.Surface" : "T.ForegroundDim");
            _stepLabels[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
            var chip = _stepChips[i];
            if (chip != null)
            {
                if (active) chip.SetResourceReference(Border.BackgroundProperty, "T.Primary");
                else chip.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");
            }
        }
    }

    // ---- 第一步:选版本 ----

    private void BuildStep1()
    {
        // 类型筛选 chips(正式版/快照/远古版/愚人节版/全部,自绘双层结构:选中主色底白字/未选灰底)
        _typeChips = new FrameworkElement[TypeFilters.Length];
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < TypeFilters.Length; i++)
        {
            int idx = i;
            var label = UIKit.Text(TypeFilters[i], 13, FontWeights.Medium);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            var bd = new Border
            {
                CornerRadius = new CornerRadius(UIKit.R.Chip),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 10, 0),
                Child = label
            };
            var chip = UIKit.GhostButton(bd, () =>
            {
                _typeFilter = idx;
                StyleTypeChips();
                RenderVersionList();
            }, height: double.NaN);
            chip.Height = double.NaN;   // 内容自适应高度
            chip.Cursor = Cursors.Hand;
            _typeChips[i] = bd;
            chipRow.Children.Add(chip);
        }
        StyleTypeChips();

        _searchBox.Width = 220;
        _searchBox.VerticalAlignment = VerticalAlignment.Center;
        _searchBox.TextChanged += (_, _) => RenderVersionList();

        // 下载源切换(写全局配置,下载引擎实时读取)
        // 2026-09-30:补第 4 项「自有镜像」——地址在设置页填,这里只做切换;
        // 少加这一项的话 SelectedIndex=3 会被下面的 switch 静默回落成 Auto
        _sourceBox.Items.Add("智能自动(测速选最快源)");
        _sourceBox.Items.Add("BMCLAPI(国内加速)");
        _sourceBox.Items.Add("Mojang(官方源)");
        _sourceBox.Items.Add("自有镜像(需在设置页填地址)");
        _sourceBox.SelectedIndex = _config.Config.DownloadSource switch
        {
            "Mojang" => 2,
            "BMCLAPI" => 1,
            "Custom" => 3,
            _ => 0
        };
        _sourceBox.VerticalAlignment = VerticalAlignment.Center;
        _sourceBox.Margin = new Thickness(16, 0, 0, 0);
        _sourceBox.SelectionChanged += (_, _) =>
        {
            _config.Config.DownloadSource = _sourceBox.SelectedIndex switch
            {
                1 => "BMCLAPI",
                2 => "Mojang",
                3 => "Custom",
                _ => "Auto"
            };
            _config.Save();
            _downloads?.NotifySourceChanged();
            _manifest?.ClearCache();     // 清单缓存是按源拉的,切源后必须重拉,否则看到的还是旧源结果
            Shell()?.SetStatus(_sourceBox.SelectedIndex == 3 &&
                                string.IsNullOrWhiteSpace(_config.Config.CustomDownloadBaseUrl)
                ? "下载源已切到自有镜像,但地址还没填:设置页 → 网络设置 → 自有镜像地址。在填上之前按官方源下载"
                : $"下载源已切换:{_config.Config.DownloadSource}");
        };

        var refreshBtn = UIKit.Button("刷新清单", primary: false, onClick: () => _ = EnsureManifestAsync(forceRefresh: true), height: 36);
        refreshBtn.VerticalAlignment = VerticalAlignment.Center;
        refreshBtn.Margin = new Thickness(16, 0, 0, 0);

        _versionCount.VerticalAlignment = VerticalAlignment.Center;

        // chips 一行、搜索/下载源/刷新一行(5 类 chips 单行会溢出)
        var toolRow = UIKit.H(_searchBox, _sourceBox, refreshBtn);
        toolRow.Margin = new Thickness(0, 12, 0, 0);
        _step1.Children.Add(UIKit.Card(
            UIKit.V(chipRow, toolRow), pad: 12, topGap: 8));

        var hint = UIKit.Sub("点击任意版本进入下一步(选择加载器)", 12);
        hint.Margin = new Thickness(4, 2, 0, 8);
        _step1.Children.Add(UIKit.H(hint, _versionCount));
        _versionCount.Margin = new Thickness(14, 0, 0, 0);
        _step1.Children.Add(_versionList);
    }

    private void StyleTypeChips()
    {
        for (int i = 0; i < _typeChips.Length; i++)
        {
            if (_typeChips[i] is not Border bd || bd.Child is not TextBlock tb) continue;
            bool sel = i == _typeFilter;
            bd.SetResourceReference(Border.BackgroundProperty, sel ? "T.Primary" : "T.HoverFill");
            bd.SetResourceReference(Border.BorderBrushProperty, sel ? "T.Primary" : "T.Border");
            tb.SetResourceReference(TextBlock.ForegroundProperty, sel ? "T.Surface" : "T.Foreground");
        }
    }

    /// <summary>版本列表渲染:搜索态平铺命中结果;常规态按分组折叠卡呈现——
    /// 「最新版本」置顶(最新正式版+最新快照)+ 正式版/快照/远古版/愚人节版分组;
    /// chips 筛选决定显示哪些分组;大分组行懒渲染,首次展开才创建(快照近千条不卡首屏)</summary>
    private void RenderVersionList()
    {
        _versionList.Children.Clear();
        string kw = _searchBox.Text.Trim();
        var list = _versions.Where(v =>
                TypeMatch(v) &&
                (string.IsNullOrEmpty(kw) || v.Id.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        _versionCount.Text = _versions.Count == 0 ? "" :
            (list.Count < _versions.Count ? $"匹配 {list.Count} 个(共 {_versions.Count} 个)" : $"共 {_versions.Count} 个版本");
        if (_versions.Count == 0) return;

        // 搜索模式:平铺命中结果,快速定位不分组(与主流启动器搜索行为一致)
        if (kw.Length > 0)
        {
            foreach (var v in list)
                _versionList.Children.Add(MakeManifestRow(v));
            return;
        }

        // 常规模式:分组折叠卡(组内保持官方发布时间倒序)
        var release = _versions.Where(v => v.Type == "release").ToList();
        var snapshot = _versions.Where(v => v.Type == "snapshot" && !v.IsAprilFools).ToList();
        var ancient = _versions.Where(v => v.IsAncient).ToList();
        var april = _versions.Where(v => v.IsAprilFools).ToList();

        // 「最新版本」置顶卡:最新正式版 + 最新快照(仅「全部」筛选下展示)
        if (_typeFilter == 4)
        {
            var latest = new List<MojangVersion>();
            if (release.Count > 0) latest.Add(release[0]);
            if (snapshot.Count > 0) latest.Add(snapshot[0]);
            if (latest.Count > 0)
                _versionList.Children.Add(MakeVersionGroup("最新版本", latest, expanded: true, lazy: false));
        }
        if (_typeFilter is 0 or 4 && release.Count > 0)
            _versionList.Children.Add(MakeVersionGroup("正式版", release, expanded: _typeFilter == 0, lazy: true));
        if (_typeFilter is 1 or 4 && snapshot.Count > 0)
            _versionList.Children.Add(MakeVersionGroup("快照(预览版)", snapshot, expanded: false, lazy: true));
        if (_typeFilter is 2 or 4 && ancient.Count > 0)
            _versionList.Children.Add(MakeVersionGroup("远古版", ancient, expanded: false, lazy: true));
        if (_typeFilter is 3 or 4 && april.Count > 0)
            _versionList.Children.Add(MakeVersionGroup("愚人节版", april, expanded: false, lazy: true));
    }

    /// <summary>版本分组折叠卡:头部 组名+数量+箭头,点击展开/收起;
    /// lazy=true 且初始收起时,版本行延迟到首次展开才创建(大列表性能保护)</summary>
    private FrameworkElement MakeVersionGroup(string title, List<MojangVersion> items, bool expanded, bool lazy)
    {
        var rows = new StackPanel();
        bool filled = false;
        void FillRows()
        {
            if (filled) return;
            foreach (var v in items) rows.Children.Add(MakeManifestRow(v));
            filled = true;
        }
        if (!lazy || expanded) FillRows();

        var body = new Border
        {
            Padding = new Thickness(2, 8, 2, 2),
            Child = rows,
            Visibility = expanded ? Visibility.Visible : Visibility.Collapsed
        };

        var arrow = UIKit.Text(expanded ? "▾" : "▸", 13, FontWeights.Medium);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "T.ForegroundDim");
        var t = UIKit.Text(title, 13.5, FontWeights.SemiBold);
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(8, 0, 0, 0);
        var cnt = UIKit.Sub($"{items.Count} 个", 11.5);
        cnt.VerticalAlignment = VerticalAlignment.Center;
        cnt.Margin = new Thickness(10, 0, 0, 0);

        var card = UIKit.Panel(UIKit.V(UIKit.H(arrow, t, cnt), body), pad: 10);
        card.WithRef("T.SurfaceAlt", "T.Border");
        card.BorderThickness = new Thickness(1);
        card.Margin = new Thickness(0, 0, 0, 6);
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonDown += (_, _) =>
        {
            bool show = body.Visibility == Visibility.Collapsed;
            if (show) FillRows();
            body.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = show ? "▾" : "▸";
        };
        return card;
    }

    /// <summary>按当前 chips 选中项判定版本是否显示(0 正式/1 快照/2 远古/3 愚人节/4 全部)</summary>
    private bool TypeMatch(MojangVersion v) => _typeFilter switch
    {
        0 => v.Type == "release",
        1 => v.Type == "snapshot" && !v.IsAprilFools,
        2 => v.IsAncient,
        3 => v.IsAprilFools,
        _ => true
    };

    /// <summary>版本类型 → 方块图标类别(按类型区分:草方块/命令方块/圆石/TNT)</summary>
    private static string IconKindOf(MojangVersion v) => v.IsAprilFools ? "tnt" : v.Type switch
    {
        "snapshot" => "command",
        "old_beta" or "old_alpha" => "cobble",
        _ => "grass"
    };

    /// <summary>累计游玩时长格式化(秒/分钟/小时进位,玩家可读)</summary>
    private static string FmtDuration(long secs)
    {
        var ts = TimeSpan.FromSeconds(secs);
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes} 分 {ts.Seconds} 秒";
        return $"{ts.Seconds} 秒";
    }

    /// <summary>加载器徽章(图标 + 文字):实例卡片上用官方 Logo 代替纯文字提示,未知加载器回退纯文字</summary>
    private FrameworkElement LoaderBadgeWithLogo(string loader, string? loaderVersion)
    {
        var logo = IconKit.LoaderLogo(loader, 17);
        logo.Margin = new Thickness(1, 0, 7, 0);
        var label = UIKit.Text(
            string.IsNullOrEmpty(loaderVersion) ? loader : $"{loader} {loaderVersion}",
            12, FontWeights.Medium, "T.Surface");
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 1, 0, 0);
        var badge = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { logo, label } }
        };
        badge.SetResourceReference(Border.BackgroundProperty, "T.Primary");
        return badge;
    }

    private FrameworkElement MakeManifestRow(MojangVersion v)
    {
        var icon = IconKit.McBlock(36, IconKindOf(v));
        icon.Margin = new Thickness(0, 0, 12, 0);

        var id = UIKit.Text(v.Id, 16, FontWeights.SemiBold);
        id.VerticalAlignment = VerticalAlignment.Center;
        id.TextTrimming = TextTrimming.CharacterEllipsis;
        // 徽章配色:正式版绿 / 快照主色 / 远古灰 / 愚人节橙(醒目区分)
        string badgeTone = v.IsAprilFools ? "T.Warning"
            : v.Type == "release" ? "T.Success"
            : v.Type == "snapshot" ? "T.Primary"
            : "T.ForegroundDim";
        var typeBadge = UIKit.Badge(v.TypeDisplay, badgeTone);
        typeBadge.VerticalAlignment = VerticalAlignment.Center;
        typeBadge.Margin = new Thickness(14, 0, 0, 0);
        var date = UIKit.Sub(v.ReleaseTimeDisplay, 12);
        date.VerticalAlignment = VerticalAlignment.Center;
        date.Margin = new Thickness(14, 0, 0, 0);
        date.TextTrimming = TextTrimming.CharacterEllipsis;

        // 选择按钮:主色胶囊(与全局按钮同规格,悬停加深 + 按下内缩),替代原先的纯文字「选择 →」
        var pickBtn = UIKit.Button("选择", primary: true, onClick: () => OnPickVersion(v), height: 30);
        pickBtn.MinWidth = 72;
        pickBtn.VerticalAlignment = VerticalAlignment.Center;
        pickBtn.ToolTip = $"选择 Minecraft {v.Id} 并进入下一步(选择加载器)";

        // 2026-09-25:内容(版本号+徽章+日期)左紧凑排、日期紧贴徽章清晰可见,
        // 中间 Star 列撑满、选择/安装按钮固定在最后面贴右(用户要求"安装要在最后面")
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { icon, id.Col(1), typeBadge.Col(2), date.Col(3), pickBtn.Col(5) }
        };

        var card = UIKit.Panel(grid, pad: 12);
        card.WithRef("T.SurfaceAlt", "T.Border");
        card.BorderThickness = new Thickness(1);
        card.Margin = new Thickness(0, 0, 0, 8);
        card.Cursor = Cursors.Hand;
        card.RenderTransform = new TranslateTransform();
        card.MouseEnter += (_, _) =>
        {
            card.Lift(-1);
            card.SetResourceReference(Border.BorderBrushProperty, "T.Primary");
        };
        card.MouseLeave += (_, _) =>
        {
            card.Lift(0);
            card.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        };
        card.MouseLeftButtonDown += (_, _) => OnPickVersion(v);
        return card;
    }

    private void OnPickVersion(MojangVersion v)
    {
        _selVersion = v;
        // 切换游戏版本:重置加载器选择与已拉取列表(不同版本可用加载器不同),作废在途异步回填
        _selLoaderKind = "";
        _selLoaderVersion = null;
        _loaderListCache.Clear();
        _loaderCardGen++;
        _selVersionText.Text = $"已选版本:{v.Id}({v.TypeDisplay} · {v.ReleaseTimeDisplay})";
        BuildLoaderCards();
        SetStep(2);
    }

    // ---- 第二步:选加载器 ----

    private void BuildStep2()
    {
        var backBtn = UIKit.Button("← 返回选版本", primary: false, onClick: () => SetStep(1), height: 36);
        backBtn.MinWidth = 120;
        var head = UIKit.V(
            UIKit.Text("要不要安装模组加载器?", 15, FontWeights.SemiBold),
            UIKit.H(_selVersionText).MarginTop(5));

        _step2.Children.Add(UIKit.Card(UIKit.V(
            UIKit.H(backBtn),
            new Border { Padding = new Thickness(0, 10, 0, 0), Child = head },
            new Border { Padding = new Thickness(0, 10, 0, 0), Child = _loaderCards }), pad: 14, topGap: 8));
    }

    /// <summary>加载器折叠卡列表:未选显示「可以添加」灰字,展开后内部版本列表,
    /// 选中后头部显示版本号 + ✕ 清除;不兼容的加载器显示原因且不可展开;
    /// 保持原有单选语义:选一个自动清除其它(引擎一次只装一个加载器)</summary>
    private void BuildLoaderCards()
    {
        _loaderCards.Children.Clear();
        _loaderCardRefs.Clear();
        if (_selVersion == null) return;
        foreach (var (label, key, desc) in Loaders)
        {
            string reason = "";   // 预置空串:原版卡短路不进入兼容性检查,编译器无法确认 out 参数已赋值
            bool supported = string.IsNullOrEmpty(key)
                || LoaderVersionProvider.IsLoaderSupportedFor(key, _selVersion.Id, out reason);

            var glyph = IconKit.LoaderLogo(key, 26);
            glyph.Margin = new Thickness(0, 0, 10, 0);
            var name = UIKit.Text(label, 13.5, FontWeights.SemiBold);
            name.VerticalAlignment = VerticalAlignment.Center;
            var status = UIKit.Sub(key == "" ? desc : "可以添加", 12);
            status.VerticalAlignment = VerticalAlignment.Center;
            status.Margin = new Thickness(10, 0, 0, 0);

            var clearX = UIKit.Text("✕", 13);
            clearX.SetResourceReference(TextBlock.ForegroundProperty, "T.ForegroundDim");
            var clearBtn = UIKit.GhostButton(clearX, () =>
            {
                _selLoaderKind = "";
                _selLoaderVersion = null;
                UpdateLoaderCardHeaders();
            }, height: 26);
            clearBtn.ToolTip = "清除选择";
            clearBtn.Margin = new Thickness(6, 0, 4, 0);
            clearBtn.Visibility = Visibility.Collapsed;

            var arrow = UIKit.Text("▸", 13, FontWeights.Medium);
            arrow.VerticalAlignment = VerticalAlignment.Center;
            arrow.Margin = new Thickness(6, 0, 0, 0);
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "T.ForegroundDim");

            var headGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Children = { glyph, UIKit.H(name, status).Col(1), clearBtn.Col(2), arrow.Col(3) }
            };

            var body = new StackPanel { Margin = new Thickness(34, 8, 0, 0), Visibility = Visibility.Collapsed };
            // 2026-09-26 批3:列表宿主独立于卡片体,搜索行固定在其上方(重绘不重建输入框)
            // 2026-09-27 修复:状态行(加载中/失败/暂无适配)另用一个宿主,重绘只清状态行。
            // 此前每次拉取都 Body.Children.Clear(),会把 listHost 一并摘出可视树,
            // 成功路径把版本列表写进游离面板 → 卡片一片空白、任何加载器版本都点不到。
            var statusHost = new StackPanel();
            var listHost = new StackPanel();
            body.Children.Add(statusHost);
            body.Children.Add(listHost);

            var card = UIKit.Panel(UIKit.V(headGrid, body), pad: 10);
            card.WithRef("T.SurfaceAlt", "T.Border");
            card.BorderThickness = new Thickness(1);
            card.Margin = new Thickness(0, 0, 0, 6);
            card.RenderTransform = new TranslateTransform();
            card.MouseEnter += (_, _) =>
            {
                card.Lift(-1);
                card.SetResourceReference(Border.BorderBrushProperty, "T.Primary");
            };
            card.MouseLeave += (_, _) =>
            {
                card.Lift(0);
                card.SetResourceReference(Border.BorderBrushProperty, "T.Border");
            };

            var rf = new LoaderCardRef
            {
                Key = key,
                Supported = supported,
                Status = status,
                Clear = clearBtn,
                Body = body,
                StatusHost = statusHost,
                ListHost = listHost,
                Arrow = arrow
            };
            _loaderCardRefs.Add(rf);

            if (supported)
            {
                card.Cursor = Cursors.Hand;
                card.MouseLeftButtonDown += (_, _) => OnToggleLoaderCard(rf);
            }
            else
            {
                // 不兼容:头部直接显示原因(不可展开卡),同格提示降级为警告色(不覆盖原因文案)
                card.Opacity = 0.7;
                status.Text = reason;
                status.SetResourceReference(TextBlock.ForegroundProperty, "T.Warning");
            }
            _loaderCards.Children.Add(card);
        }
    }

    /// <summary>加载器卡展开/收起:原版卡点击直接进入第三步;其余卡手风琴式互斥(同时只展开一个),
    /// 首次展开时懒加载版本列表</summary>
    private void OnToggleLoaderCard(LoaderCardRef rf)
    {
        if (rf.Key == "")
        {
            // 原版:无版本列表,直接确认安装(保持原有一步到位行为)
            _selLoaderKind = "";
            _selLoaderVersion = null;
            UpdateLoaderCardHeaders();
            GoToStep3();
            return;
        }
        bool show = rf.Body.Visibility == Visibility.Collapsed;
        // 手风琴互斥:展开新卡前收起其它卡(一次只看一个列表)
        foreach (var other in _loaderCardRefs)
        {
            if (other == rf || other.Key == "") continue;
            other.Body.Visibility = Visibility.Collapsed;
            other.Arrow.Text = "▸";
        }
        rf.Body.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        rf.Arrow.Text = show ? "▾" : "▸";
        if (show) EnsureLoaderList(rf);
    }

    /// <summary>懒拉取加载器版本列表:缓存命中直接填充;失败可原地重试;切版本后陈旧结果丢弃</summary>
    private async void EnsureLoaderList(LoaderCardRef rf)
    {
        try
        {
            int gen = _loaderCardGen;
            string mc = _selVersion?.Id ?? "";
            if (string.IsNullOrEmpty(mc)) return;
            if (_loaderListCache.TryGetValue(rf.Key, out var cached))
            {
                FillLoaderBody(rf, cached);
                return;
            }
            rf.StatusHost.Children.Clear();
            rf.StatusHost.Children.Add(UIKit.Sub("正在获取版本列表…", 12));
            var res = await _loaderProvider.GetVersionsAsync(rf.Key, mc);
            if (gen != _loaderCardGen || _selVersion?.Id != mc) return;   // 已切换游戏版本,丢弃陈旧结果
            // 只换状态行:listHost 常驻 body,清 Body 会把它摘出可视树导致版本列表不可见
            rf.StatusHost.Children.Clear();
            if (!res.Success)
            {
                var hint = UIKit.Sub("获取失败:" + (res.Error ?? "网络异常"), 12);
                hint.SetResourceReference(TextBlock.ForegroundProperty, "T.Warning");
                var retry = UIKit.Button("重试", primary: false, onClick: () => EnsureLoaderList(rf), height: 28);
                retry.Margin = new Thickness(12, 0, 0, 0);
                rf.StatusHost.Children.Add(UIKit.H(hint, retry));
                return;
            }
            if (res.Versions.Count == 0)
            {
                rf.StatusHost.Children.Add(UIKit.Sub($"该游戏版本暂无适配的 {Loaders.First(l => l.Key == rf.Key).Label} 版本", 12));
                return;
            }
            var list = res.Versions.Take(120).ToList();
            _loaderListCache[rf.Key] = list;
            FillLoaderBody(rf, list);
        }
        catch (Exception ex)
        {
            App.WriteAppLog("[版本] 加载器列表拉取异常:" + ex.Message);
        }
    }

    /// <summary>把版本列表渲染进卡片体:正式版纯文本,测试版加「测试」徽章,点击即选定。
    /// 2026-09-25 二次展开:full=false 只出前 6 个(推荐分区)+「查看全部」入口,点击后追加全部</summary>
    private void FillLoaderBody(LoaderCardRef rf, List<LoaderVersionEntry> list, bool full = false)
    {
        if (rf.ListHost == null) return;
        rf.List = list;
        rf.Full = full;
        rf.ListHost.Children.Clear();
        // 2026-09-25 推荐分区:稳定版在前、测试版统一置后
        var stable = list.Where(x => x.IsStable).ToList();
        var beta = list.Where(x => !x.IsStable).ToList();
        var all = stable.Concat(beta).ToList();
        int total = all.Count;
        var matched = string.IsNullOrEmpty(rf.Query)
            ? all
            : all.Where(x => x.Version.Contains(rf.Query, StringComparison.OrdinalIgnoreCase)).ToList();
        // 2026-09-26 回退:搜索行只在点了「查看全部」展开后出现。
        // 默认视图保持「直接列出推荐 6 个稳定版」的旧样式 —— 此前无条件把搜索框顶在列表上方,
        // 用户会以为必须先搜索才能选版本。
        if (total > 6 && full)
        {
            EnsureLoaderSearchRow(rf);
            if (rf.HitText != null)
                rf.HitText.Text = string.IsNullOrEmpty(rf.Query) ? $"共 {total} 个版本" : $"命中 {matched.Count} / {total}";
        }
        if (matched.Count == 0)
        {
            rf.ListHost.Children.Add(UIKit.Sub("没有匹配的版本,换个关键词试试", 12));
            return;
        }
        int show = full ? matched.Count : Math.Min(6, matched.Count);
        for (int i = 0; i < show; i++)
        {
            var lv = matched[i];
            var ver = UIKit.Text(lv.Version, 12.5);
            ver.VerticalAlignment = VerticalAlignment.Center;
            var row = UIKit.H(ver);
            // 2026-09-26 批3:推荐徽章显式化——列表已按 CompareVersionDesc 降序,首个稳定版即「最新稳定」
            if (lv.IsStable && ReferenceEquals(lv, stable.FirstOrDefault()))
            {
                var badge = UIKit.Badge("推荐 · 最新稳定", "T.Primary");
                badge.VerticalAlignment = VerticalAlignment.Center;
                badge.Margin = new Thickness(8, 0, 0, 0);
                row.Children.Add(badge);
            }
            else if (!lv.IsStable)
            {
                var badge = UIKit.Badge("测试", "T.Warning");
                badge.VerticalAlignment = VerticalAlignment.Center;
                badge.Margin = new Thickness(8, 0, 0, 0);
                row.Children.Add(badge);
            }
            var item = new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(UIKit.R.Chip),  // 圆角长方形,非椭圆胶囊(2026-09-27 Trae 改坏修复)
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Child = row
            };
            item.MouseEnter += (_, _) => item.SetResourceReference(Border.BackgroundProperty, "T.HoverFill");
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            string picked = lv.Version;
            string kind = rf.Key;
            item.MouseLeftButtonDown += (_, e) => { e.Handled = true; OnPickLoaderVersion(kind, picked); };
            rf.ListHost.Children.Add(item);
        }
        // 2026-09-25 二次展开入口:超过 6 个时追加「查看全部」,点击后重绘全量
        if (!full && matched.Count > show)
        {
            var more = UIKit.Text($"▼ 查看全部 {matched.Count} 个版本", 12);
            more.SetResourceReference(TextBlock.ForegroundProperty, "T.Primary");
            more.VerticalAlignment = VerticalAlignment.Center;
            var moreItem = new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(UIKit.R.Chip),  // 圆角长方形,非椭圆胶囊(2026-09-27 Trae 改坏修复)
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Child = more
            };
            moreItem.MouseEnter += (_, _) => moreItem.SetResourceReference(Border.BackgroundProperty, "T.HoverFill");
            moreItem.MouseLeave += (_, _) => moreItem.Background = Brushes.Transparent;
            var keep = list;
            moreItem.MouseLeftButtonDown += (_, e) => { e.Handled = true; FillLoaderBody(rf, keep, full: true); };
            rf.ListHost.Children.Add(moreItem);
        }
    }

    /// <summary>加载器版本搜索行(2026-09-26 批3):仅版本数超过 6 时创建一次,
    /// 输入实时过滤(大小写不敏感)并刷新命中数;控件常驻,重绘列表不丢输入焦点</summary>
    private void EnsureLoaderSearchRow(LoaderCardRef rf)
    {
        if (rf.SearchRow != null) return;
        var box = UIKit.TextBox("", "搜索版本号…");
        box.Width = 170;
        box.FontSize = 12;
        box.Padding = new Thickness(10, 6, 10, 6);
        box.MaxWidth = 170;
        box.VerticalAlignment = VerticalAlignment.Center;
        box.TextChanged += (_, _) =>
        {
            rf.Query = box.Text.Trim();
            FillLoaderBody(rf, rf.List, rf.Full);
        };
        var hit = UIKit.Sub("", 11.5);
        hit.VerticalAlignment = VerticalAlignment.Center;
        hit.Margin = new Thickness(10, 0, 0, 0);
        var row = UIKit.H(box, hit);
        row.Margin = new Thickness(0, 2, 0, 6);
        rf.SearchBox = box;
        rf.HitText = hit;
        rf.SearchRow = row;
        rf.Body.Children.Insert(0, row);   // 搜索行固定在版本列表上方
    }

    /// <summary>选定具体加载器版本(单选:自动清除其它加载器),收起卡片并进入第三步</summary>
    private void OnPickLoaderVersion(string kind, string version)
    {
        _selLoaderKind = kind;
        _selLoaderVersion = version;
        foreach (var r in _loaderCardRefs)
        {
            if (r.Key == "") continue;
            r.Body.Visibility = Visibility.Collapsed;
            r.Arrow.Text = "▸";
        }
        UpdateLoaderCardHeaders();
        GoToStep3();
    }

    /// <summary>刷新全部加载器卡头部状态行:选中卡显示版本号+✕,未选显示「可以添加」</summary>
    private void UpdateLoaderCardHeaders()
    {
        foreach (var rf in _loaderCardRefs)
        {
            if (rf.Key == "" || !rf.Supported) continue;   // 原版卡/不兼容卡状态行不参与选中刷新
            if (rf.Key == _selLoaderKind && !string.IsNullOrEmpty(_selLoaderVersion))
            {
                rf.Status.Text = _selLoaderVersion;
                rf.Status.SetResourceReference(TextBlock.ForegroundProperty, "T.Primary");
                rf.Clear.Visibility = Visibility.Visible;
            }
            else
            {
                rf.Status.Text = "可以添加";
                rf.Status.SetResourceReference(TextBlock.ForegroundProperty, "T.ForegroundDim");
                rf.Clear.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>进入第三步:同步摘要/Java 提示/自动命名/预选加载器版本</summary>
    private void GoToStep3()
    {
        string loaderName = string.IsNullOrEmpty(_selLoaderKind)
            ? "原版"
            : Loaders.First(l => l.Key == _selLoaderKind).Label;
        _summaryText.Text = $"游戏版本:{_selVersion?.Id}    加载器:{loaderName}" +
            (string.IsNullOrEmpty(_selLoaderVersion) ? "" : $" {_selLoaderVersion}");
        UpdateJavaHint();
        ApplyAutoName();
        FillLoaderVersionBox();
        SetStep(3);
    }

    /// <summary>自动命名:版本名-加载器_版本号(如 1.21.1-Forge_52.1.16、1.21.1-Fabric 0.16.14);
    /// 用户手改过名称后不再跟随</summary>
    private void ApplyAutoName()
    {
        if (!_nameAuto || _selVersion == null) return;
        string name = _selVersion.Id;
        if (!string.IsNullOrEmpty(_selLoaderKind) && !string.IsNullOrEmpty(_selLoaderVersion))
            name += _selLoaderKind switch
            {
                "fabric" => $"-Fabric {_selLoaderVersion}",
                "forge" => $"-Forge_{_selLoaderVersion}",
                "neoforge" => $"-NeoForge_{_selLoaderVersion}",
                "quilt" => $"-Quilt_{_selLoaderVersion}",
                "optifine" => $"-OptiFine_{_selLoaderVersion}",
                _ => ""
            };
        _applyingAutoName = true;
        _nameBox.Text = name;
        _applyingAutoName = false;
    }

    // ---- 第三步:确认安装 ----

    private void BuildStep3()
    {
        var backBtn = UIKit.Button("← 返回选加载器", primary: false, onClick: () => SetStep(2), height: 36);
        backBtn.MinWidth = 120;

        _summaryText.VerticalAlignment = VerticalAlignment.Center;
        _nameBox.Width = 320;
        // 用户手改名称后停止自动跟随(程序写入不触发判定)
        _nameBox.TextChanged += (_, _) => { if (!_applyingAutoName) _nameAuto = false; };
        _loaderVersionBox.VerticalAlignment = VerticalAlignment.Center;
        _loaderVersionBox.SelectionChanged += OnLoaderVersionBoxSelectionChanged;   // 2026-09-25 二次下拉展开
        _loaderVersionBox.MaxDropDownHeight = 230;   // 物理兜底:下拉窗口最高约 8 项,超长自动滚动(配合二次下拉逻辑)
        _javaHint.TextWrapping = TextWrapping.Wrap;
        _javaHint.MaxWidth = 420;

        _installProgress.Height = 10;   // 2026-09-26 加高更明显
        _installProgress.Margin = new Thickness(0, 14, 0, 6);
        _installStatus.TextWrapping = TextWrapping.Wrap;
        _installStatus.Margin = new Thickness(0, 2, 0, 0);
        _installBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _installBtn.Margin = new Thickness(0, 4, 0, 0);

        _step3.Children.Add(UIKit.Card(UIKit.V(
            UIKit.H(backBtn),
            new Border { Padding = new Thickness(0, 14, 0, 0), Child = UIKit.H(UIKit.Sub("当前选择", 12), _summaryText) },
            new Border { Padding = new Thickness(0, 14, 0, 0), Child = UIKit.SettingRow("版本名称", "给这个版本起个名字,方便区分", _nameBox) },
            new Border { Child = UIKit.SettingRow("加载器版本", "默认最新稳定版,可按需选择旧版", _loaderVersionBox) },
            new Border { Child = UIKit.SettingRow("Java 需求", "根据所选版本自动推荐 Java 主版本", _javaHint) },
            new Border { Padding = new Thickness(0, 10, 0, 2), Child = UIKit.V(_installBtn, _installProgress, _installStatus) })));
    }

    private void UpdateJavaHint()
    {
        if (_selVersion == null) { _javaHint.Text = ""; return; }
        int major = JavaRuntimeService.RecommendJavaMajor(_selVersion.Id);
        var ready = _javaRuntimes.FindReadyRuntime(major);
        _javaHint.Text = $"推荐 Java {major}" + (ready != null ? "(已就绪)" : "(未安装,安装游戏时会尝试自动下载)");
    }

    // ==================== 清单与加载器数据 ====================

    /// <summary>确保版本清单就绪:有缓存零网络秒开,无缓存走网络;forceRefresh=强制刷新</summary>
    private async Task EnsureManifestAsync(bool forceRefresh = false)
    {
        if (_manifestFetching) return;
        if (!forceRefresh && _versions.Count > 0) { RenderVersionList(); return; }
        _manifestFetching = true;
        _installStatus.Text = "正在准备版本清单…";
        _versionCount.Text = "正在拉取版本清单…";
        try
        {
            var manifest = await _manifest.FetchManifestAsync(forceRefresh);
            if (manifest == null || manifest.Versions.Count == 0)
            {
                // 2026-09-25 后手:网络抖动常见,失败后 2 秒自动重试一次,仍失败才提示手动刷新
                _versionCount.Text = "版本清单拉取失败,2 秒后自动重试…";
                _installStatus.Text = "版本清单拉取失败,正在自动重试…";
                await Task.Delay(2000);
                manifest = await _manifest.FetchManifestAsync(forceRefresh: true);
            }
            if (manifest == null || manifest.Versions.Count == 0)
            {
                _versionCount.Text = "版本清单拉取失败,请检查网络后点击「刷新清单」重试";
                _installStatus.Text = "版本清单拉取失败:" + (string.IsNullOrEmpty(_manifest.LastError) ? "请检查网络后重试" : _manifest.LastError.Replace("\n", " "));
                return;
            }
            _versions = manifest.Versions;
            RenderVersionList();
            _installStatus.Text = $"已获取 {manifest.Versions.Count} 个版本";
        }
        catch (Exception ex)
        {
            _versionCount.Text = "清单拉取失败:" + ex.Message;
            _installStatus.Text = "清单拉取失败:" + ex.Message;
            App.WriteAppLog($"[版本] 清单拉取异常:{ex.Message}");
        }
        finally
        {
            _manifestFetching = false;
        }
    }

    /// <summary>填充第三步加载器版本下拉:优先复用第二步卡片已拉取的列表(免二次请求),
    /// 并预选第二步已选定的具体版本;无缓存时才走网络兜底</summary>
    /// <summary>统一填充加载器版本下拉:full=false 只出前 6 个推荐 + 「查看全部」占位(二次下拉),
    /// full=true 展开全量。稳定版已在 _loaderVersions 按最新在前排序</summary>
    private void PopulateLoaderVersionBox(bool full)
    {
        _loaderVersionBox.Items.Clear();
        if (full || _loaderVersions.Count <= 6)
        {
            foreach (var lv in _loaderVersions) _loaderVersionBox.Items.Add(lv.Version);
        }
        else
        {
            for (int i = 0; i < 6; i++) _loaderVersionBox.Items.Add(_loaderVersions[i].Version);
            _loaderVersionBox.Items.Add($"▼ 查看全部 {_loaderVersions.Count} 个版本");
        }
        _loaderVersionBox.IsEnabled = _loaderVersions.Count > 0;
    }

    /// <summary>二次下拉:选中「查看全部」占位项时展开全量版本并回到推荐首位</summary>
    private void OnLoaderVersionBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaderVersionExpanding) return;
        var item = _loaderVersionBox.SelectedItem as string;
        if (item != null && item.StartsWith("▼ 查看全部", StringComparison.Ordinal))
        {
            _loaderVersionExpanding = true;
            PopulateLoaderVersionBox(full: true);
            _loaderVersionBox.SelectedIndex = 0;   // 展开后默认推荐最新稳定版
            _loaderVersionExpanding = false;
        }
    }

    private void FillLoaderVersionBox()
    {
        _loaderVersionBox.Items.Clear();
        _loaderVersions.Clear();
        if (string.IsNullOrEmpty(_selLoaderKind))
        {
            _loaderVersionBox.IsEnabled = false;
            return;   // 原版无需加载器版本
        }
        if (_loaderListCache.TryGetValue(_selLoaderKind, out var list))
        {
            _loaderVersions = list.ToList();
            PopulateLoaderVersionBox(full: false);
            int idx = _loaderVersions.FindIndex(x => x.Version == _selLoaderVersion);
            if (_loaderVersions.Count > 0)
            {
                // 已选版本不在前 6 个推荐位:直接全量展开并选中它,避免 SelectedIndex 越界(Items 只有 7 项)
                if (idx >= 6) PopulateLoaderVersionBox(full: true);
                _loaderVersionBox.SelectedIndex = idx >= 0 ? idx : 0;
            }
            _installStatus.Text = $"共 {_loaderVersions.Count} 个加载器版本可选";
            return;
        }
        LoadLoaderVersions();   // 兜底:无第二步缓存(理论上不会发生,防御性保留)
    }

    private async void LoadLoaderVersions()
    {
        try
        {
            int gen = ++_loaderFetchGen;
            _loaderVersionBox.Items.Clear();
            _loaderVersions.Clear();
            if (_selVersion == null) return;
            if (string.IsNullOrEmpty(_selLoaderKind))
            {
                _loaderVersionBox.IsEnabled = false;
                return;   // 原版无需加载器版本
            }

            _installStatus.Text = "正在获取加载器版本列表…";
            var res = await _loaderProvider.GetVersionsAsync(_selLoaderKind, _selVersion.Id);
            if (gen != _loaderFetchGen) return;   // 已有更新的请求,丢弃陈旧结果
            if (!res.Success)
            {
                _installStatus.Text = "加载器版本获取失败:" + res.Error;
                return;
            }
            _loaderVersions = res.Versions.Where(x => x.IsStable).Take(60).ToList();
            PopulateLoaderVersionBox(full: false);
            // 预选第二步已选定的版本(若不在稳定版列表则退回首项)
            int idx = _loaderVersions.FindIndex(x => x.Version == _selLoaderVersion);
            if (_loaderVersions.Count > 0)
            {
                if (idx >= 6) PopulateLoaderVersionBox(full: true);   // 已选版本不在推荐位:全量展开并选中
                _loaderVersionBox.SelectedIndex = idx >= 0 ? idx : 0;
            }
            _installStatus.Text = _loaderVersions.Count > 0
                ? $"共 {_loaderVersions.Count} 个稳定版可选({res.SourceLabel})"
                : $"该版本暂无适配的 {_selLoaderKind} 稳定版";
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本] 操作异常:" + ex.Message);
            Shell()?.SetStatus("操作失败,详见日志", warning: true);
        }
    }

    // ==================== 安装流程 ====================

    private void OnOverallProgress(DownloadProgressInfo info)
    {
        if (!_installing) return;   // 非安装期的其它下载(如 JDK)不污染本页进度条
        Dispatcher.BeginInvoke(() =>
        {
            _installProgress.SmoothSet(info.Progress * 100);
            _installStatus.Text = $"下载中 {UIKit.FmtBytes(info.DownloadedBytes)} / {UIKit.FmtBytes(info.TotalBytes)}" +
                                  $"  ·  {UIKit.FmtBytes((long)info.SpeedBytesPerSec)}/s";
        });
    }

    private async void OnInstall()
    {
        try
        {
            if (_installing)
            {
                DialogKit.Info("已有版本正在安装,请等待当前安装完成后再安装下一个版本。",
                    owner: Window.GetWindow(this));
                return;
            }

            string name = _nameBox.Text.Trim();
            var mv = _selVersion;
            if (string.IsNullOrEmpty(name)) { DialogKit.Info("请先填写版本名称", owner: Window.GetWindow(this)); return; }
            if (mv == null) { DialogKit.Info("请先选择游戏版本", owner: Window.GetWindow(this)); return; }

            string kind = _selLoaderKind;
            string? loaderVersion = _loaderVersionBox.SelectedIndex >= 0 && _loaderVersions.Count > 0
                ? _loaderVersions[_loaderVersionBox.SelectedIndex].Version : null;
            if (!string.IsNullOrEmpty(kind) && string.IsNullOrEmpty(loaderVersion))
            {
                DialogKit.Info("请选择加载器版本号", owner: Window.GetWindow(this));
                return;
            }

            _installing = true;
            _installBtn.IsEnabled = false;
            _installProgress.ResetProgress();
            GameInstance? inst = null;
            _installer.ProgressChanged += OnInstallProgress;   // 阶段进度实时反馈(解析/下载/校验/Java)
            try
            {
                int javaMajor = JavaRuntimeService.RecommendJavaMajor(mv.Id);
                _installStatus.Text = "正在创建版本配置…";
                inst = _instances.CreateInstance(name, mv.Id, javaMajor);

                _installStatus.Text = "正在安装游戏本体(libraries / assets / natives)…";
                Shell()?.SetStatus($"正在安装 {mv.Id}…");
                _downloads.ResetOverallProgress();
                bool ok = await _installer.InstallVersionAsync(inst.Id, mv);
                if (!ok)
                {
                    string detail = _installer.LastError;
                    // 2026-09-25 修复:安装失败不再删实例壳——保留版本配置与已下载文件,
                    // 版本管理仍可见该实例(卡片标「安装未完成」);到下载中心重试成功文件后,
                    // 直接重新安装即幂等复用秒装;文件齐时甚至可直接启动。
                    if (inst != null)
                    {
                        inst.InstallComplete = false;
                        _instances.SaveInstance(inst);
                    }
                    throw new InvalidOperationException(
                        string.IsNullOrEmpty(detail) ? "游戏文件安装失败(详见日志,多为网络中断),版本配置已保留,可重新安装" : detail);
                }

                if (!string.IsNullOrEmpty(kind))
                {
                    _installStatus.Text = $"正在安装加载器 {kind} {loaderVersion}…";
                    var lr = await _loaderInstall.InstallLoaderAsync(inst.Id, mv.Id, kind, loaderVersion);
                    if (!lr.Success)
                    {
                        // 2026-09-25 修复:加载器失败同样保留实例壳并标记未完成——
                        // 删除后版本管理找不到、已下游戏文件也失去入口;保留可重装复用,
                        // 卡片红标提示加载器未装成,避免误当原版启动。
                        if (inst != null)
                        {
                            inst.InstallComplete = false;
                            _instances.SaveInstance(inst);
                        }
                        throw new InvalidOperationException(
                            $"加载器 {kind} 安装失败,已保留版本配置(游戏文件已保留,可直接重试):\n{lr.ErrorMessage}");
                    }
                }

                // Axolotl-6:新装版本后台静默补齐依赖,界面不阻塞(开关在「版本工具 → 环境校验」)
                if (_config.Config.PreloadDependencies && inst != null)
                {
                    _preload.Enabled = true;
                    if (_preload.EnqueueVersionDeps(inst.Id, "新装版本后台补齐依赖"))
                        App.WriteAppLog($"[版本] ⇢ 已入队后台补齐依赖:{inst.Name}");
                }

                if (!string.IsNullOrEmpty(_installer.JavaAutoDownloadHint))
                    DialogKit.Warn(_installer.JavaAutoDownloadHint, "Java 提示", Window.GetWindow(this));

                // 2026-09-25 修复:成功路径显式标记安装完成——失败保留的实例重装成功后,
                // 不置回 true 会一直红标「安装未完成」并阻挠启动
                if (inst != null) { inst.InstallComplete = true; _instances.SaveInstance(inst); }
                _installProgress.SmoothSet(100);
                _installStatus.Text = "安装完成";
                _locateId = inst?.Id ?? "";   // 2026-09-26 批3:回到本列表后定位并高亮新装版本
                Shell()?.SetStatus($"版本 {name} 安装完成");
                App.WriteAppLog($"[版本] 安装完成:{name}({mv.Id} / {kind})");
                DialogKit.Success($"版本「{name}」安装完成,可在本地列表中启动。", "安装成功", Window.GetWindow(this));
                _applyingAutoName = true;
                _nameBox.Text = "";
                _applyingAutoName = false;
                _nameAuto = true;   // 下次安装重新启用自动命名跟随
                ShowSection(0);
            }
            catch (Exception ex)
            {
                _installStatus.Text = "安装失败";
                Shell()?.SetStatus("安装失败", warning: true);
                App.WriteAppLog($"[版本] 安装异常:{ex}");
                DialogKit.Error(ex.Message, "安装失败", Window.GetWindow(this));
            }
            finally
            {
                _installing = false;
                _installBtn.IsEnabled = true;
                _installer.ProgressChanged -= OnInstallProgress;
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本] 操作异常:" + ex.Message);
            Shell()?.SetStatus("操作失败,详见日志", warning: true);
        }
    }

    // ==================== 整合包导入 ====================

    private void OnImportPack()
    {
        int src = DialogKit.Choice("整合包从哪里导入?",
            new[] { "本地文件(.zip / .mrpack / 第三方启动器包)", "粘贴网页链接(Modrinth / CurseForge)" },
            "导入整合包", Window.GetWindow(this));
        if (src < 0) return;
        if (src == 1) { OnImportPackFromLink(); return; }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择整合包文件",
            Filter = "整合包|*.zip;*.mrpack|所有文件|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        App.WriteAppLog($"[版本] 开始导入整合包:{dlg.FileName}");
        Shell()?.SetStatus("正在导入整合包…");
        RunImport("导入整合包", report => _packs.ImportAsync(dlg.FileName, null, report));
    }

    /// <summary>Celestial-6 整合包链接导入:粘贴 Modrinth / CurseForge 网页链接,自动拉元数据建版本下资源</summary>
    private void OnImportPackFromLink()
    {
        string? url = DialogKit.Input("粘贴整合包网页链接(Modrinth / CurseForge):", "链接导入整合包",
            "https://modrinth.com/modpack/", Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();

        var src = ModPackLinkService.DetectSource(url);
        if (src == ModPackLinkSource.Unknown)
        {
            DialogKit.Error("无法识别这个链接。\n\n目前支持 Modrinth 与 CurseForge 的整合包(modpack)网页地址,\n请确认复制的是整合包页面链接。",
                "链接无法识别", Window.GetWindow(this));
            return;
        }
        string? custom = DialogKit.Input($"已识别为 {src} 整合包。\n给新建的版本起个名字(留空则用整合包原名):",
            "链接导入整合包", "", Window.GetWindow(this));
        if (custom == null) return;   // 取消
        string? customName = string.IsNullOrWhiteSpace(custom) ? null : custom.Trim();

        App.WriteAppLog($"[版本] 开始链接导入整合包:{url}({src}) → {(customName ?? "(原名)")}");
        Shell()?.SetStatus("正在从链接导入整合包…");
        RunImport("链接导入整合包", report => _link.ImportFromLinkAsync(url, customName, null, report));
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;

    /// <summary>第二步加载器折叠卡控件引用(状态行/✕清除/版本列表容器/展开箭头)</summary>
    private sealed class LoaderCardRef
    {
        public string Key = "";
        public bool Supported = true;
        public TextBlock Status = null!;
        public Button Clear = null!;
        public StackPanel Body = null!;
        public TextBlock Arrow = null!;
        // 2026-09-26 批3:搜索行与列表宿主分离,重绘只换列表内容,输入焦点不丢
        // 2026-09-27:状态行(加载中/失败/暂无适配)独立宿主,拉取重绘不允许再动 Body
        public StackPanel StatusHost = null!;
        public StackPanel ListHost = null!;
        public TextBox? SearchBox;
        public TextBlock? HitText;
        public FrameworkElement? SearchRow;
        public string Query = "";
        public List<LoaderVersionEntry> List = new();
        public bool Full;
    }

    /// <summary>版本安装阶段进度(解析/下载/校验/Java)实时反馈,驱动状态文案与进度条</summary>
    private void OnInstallProgress(InstallProgress p)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnInstallProgress(p)); return; }
        if (_installStatus == null) return;
        string file = string.IsNullOrEmpty(p.CurrentFile) ? "" : " · " + Path.GetFileName(p.CurrentFile);
        _installStatus.Text = $"{p.Stage} {p.Current}/{p.Total}{file}";
        _installProgress.Maximum = Math.Max(1, p.Total);
        _installProgress.Value = p.Current;
    }
}
