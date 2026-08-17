// DownloadPage.xaml.cs — 下载中心(全新重写)
// 可爱的芙芙
//
// 只含两类内容:游戏版本下载、Java 运行时下载。
// 镜像源:游戏本体走 BMCLAPI(失败自动回退 Mojang),Java 走华为云国内镜像。
// 下载进度条实时显示,失败可一键重试,可随时取消。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FufuLauncher.Interaction;
using FufuLauncher.Services;

namespace FufuLauncher.Views;

/// <summary>游戏版本列表展示项</summary>
public class VersionListItem
{
    public MojangVersion Version { get; set; } = null!;
    public Visibility InstalledVis { get; set; } = Visibility.Collapsed;

    // ===== 版本类型色块标签(Badge):标签方块在最左,版本文字颜色保持原样 =====
    // 正式版=蓝 / Beta=黄 / 快照(测试版)=紫 / 远古版=灰
    private static readonly Brush ReleaseBadge = Freeze(new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3))); // 蓝
    private static readonly Brush BetaBadge = Freeze(new SolidColorBrush(Color.FromRgb(0xEA, 0xB3, 0x08)));     // 黄
    private static readonly Brush SnapshotBadge = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7))); // 紫
    private static readonly Brush AlphaBadge = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));    // 灰
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>左侧色块标签背景色(标签内白字)</summary>
    public Brush BadgeBrush => Version.Type switch
    {
        "release" => ReleaseBadge,
        "old_beta" => BetaBadge,
        "snapshot" => SnapshotBadge,
        "old_alpha" => AlphaBadge,
        _ => ReleaseBadge
    };
}

/// <summary>Java JDK 列表展示项(合并去重,一个主版本只出现一次)</summary>
public class JdkListItem
{
    public int MajorVersion { get; set; }
    public string DisplayName { get; set; } = "";
    public Visibility LtsVis { get; set; } = Visibility.Collapsed;
    public string StatusText { get; set; } = "";
    public string ButtonText { get; set; } = "⬇ 下载";
    public bool CanDownload { get; set; } = true;
}

/// <summary>页内加载器选择列表项(单选高亮 + 版本下拉,支持属性通知)</summary>
public class LoaderOptionVM : INotifyPropertyChanged
{
    /// <summary>加载器标识("" = 不安装加载器,纯原版)</summary>
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>是否需要版本下拉(「不安装加载器」为 false)</summary>
    public bool NeedsVersion => Key != "";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Notify(nameof(IsSelected)); Notify(nameof(CheckVis)); } }
    }

    /// <summary>None=未拉取 Loading=拉取中 Ready=成功 Error=失败 Empty=该版本暂无适配</summary>
    public enum LoaderState { None, Loading, Ready, Error, Empty }
    private LoaderState _state = LoaderState.None;
    public LoaderState State
    {
        get => _state;
        set { if (_state != value) { _state = value; Notify(nameof(State)); NotifyStateVis(); } }
    }

    public ObservableCollection<LoaderComboItem> ComboItems { get; } = new();

    private LoaderComboItem? _selectedEntry;
    public LoaderComboItem? SelectedEntry
    {
        get => _selectedEntry;
        set { if (_selectedEntry != value) { _selectedEntry = value; Notify(nameof(SelectedEntry)); } }
    }

    private string _errorText = "获取版本失败";
    public string ErrorText { get => _errorText; set { _errorText = value; Notify(nameof(ErrorText)); } }

    /// <summary>实际生效的版本数据源(如「BMCLAPI 国内镜像」「本地缓存」)</summary>
    public string SourceLabel { get; set; } = "";

    public Visibility CheckVis => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>下拉区域:就绪可用 / 失败、无适配时置灰禁用(不允许直接确定)</summary>
    public Visibility ComboVis => NeedsVersion && (State == LoaderState.Ready || State == LoaderState.Error || State == LoaderState.Empty)
        ? Visibility.Visible : Visibility.Collapsed;
    public bool ComboEnabled => State == LoaderState.Ready;
    /// <summary>下拉框上的覆盖提示(失败/无适配时显示)</summary>
    public Visibility PlaceholderVis => NeedsVersion && (State == LoaderState.Error || State == LoaderState.Empty)
        ? Visibility.Visible : Visibility.Collapsed;
    public string PlaceholderText => State == LoaderState.Error ? "获取版本失败,点击重试" : "该版本暂无适配";
    public Visibility LoadingVis => NeedsVersion && State == LoaderState.Loading ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>失败时的「重试」按钮可见性</summary>
    public Visibility ErrorVis => NeedsVersion && State == LoaderState.Error ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVis => NeedsVersion && State == LoaderState.Empty ? Visibility.Visible : Visibility.Collapsed;

    private void NotifyStateVis()
    {
        Notify(nameof(ComboVis)); Notify(nameof(ComboEnabled)); Notify(nameof(PlaceholderVis));
        Notify(nameof(PlaceholderText)); Notify(nameof(LoadingVis));
        Notify(nameof(ErrorVis)); Notify(nameof(EmptyVis));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>加载器版本下拉条目(含分组标题:正式版 / 测试版)</summary>
public class LoaderComboItem
{
    public bool IsHeader { get; set; }
    public string HeaderText { get; set; } = "";
    /// <summary>真实版本号(标题项为空)</summary>
    public string Version { get; set; } = "";
    public string Display { get; set; } = "";
    public Visibility HeaderVis => IsHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ItemVis => IsHeader ? Visibility.Collapsed : Visibility.Visible;
}

public partial class DownloadPage : Page
{
    private readonly VersionManifestService _versionManifest;
    private readonly DownloadService _downloadService;
    private readonly ConfigService _configService;
    private readonly GameInstallService _gameInstall;
    private readonly InstanceService _instanceService;
    private readonly JavaRuntimeService _javaRuntime;
    private readonly ModLoaderInstallService _loaderInstall;
    private readonly LoaderVersionProvider _loaderVersions;

    private bool _busy;
    private string _currentFilter = "";
    private MojangVersion? _lastGameVersion;   // 供失败重试
    private JdkListItem? _lastJdk;             // 供失败重试
    private bool _jdkTabLoaded;
    private MojangVersion? _pendingVersion;      // 加载器选择步骤待确认的版本
    private string _pendingInstanceName = "";     // 加载器选择步骤待确认的实例名
    private int _loaderSession;                   // 加载器选择会话号(防止异步拉取返回后页面已切换)
    private readonly List<LoaderOptionVM> _loaderOptions = new();
    // 搜索防抖:停止输入 250ms 后才刷新列表,避免每敲一个字符就全盘重建(借鉴主流启动器搜索体验)
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public DownloadPage(VersionManifestService versionManifest, DownloadService downloadService,
                        ConfigService configService, GameInstallService gameInstall,
                        InstanceService instanceService, JavaRuntimeService javaRuntime,
                        ModLoaderInstallService loaderInstall, LoaderVersionProvider loaderVersions)
    {
        _versionManifest = versionManifest;
        _downloadService = downloadService;
        _configService = configService;
        _gameInstall = gameInstall;
        _instanceService = instanceService;
        _javaRuntime = javaRuntime;
        _loaderInstall = loaderInstall;
        _loaderVersions = loaderVersions;
        InitializeComponent();

        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilter(); };

        // ===== 进度订阅(构造时订阅一次,页面被缓存不会重复) =====

        // 安装阶段进度(游戏文件下载/资源校验等)
        _gameInstall.ProgressChanged += p =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                TxtStage.Text = $"{p.Stage} {p.Current}/{p.Total}";
                TxtSubStage.Text = p.CurrentFile;
            });
        };

        // 全局总进度(字节级)
        _downloadService.OverallProgressChanged += info =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (info.TotalBytes > 0)
                {
                    OverallBar.IsIndeterminate = false;
                    OverallBar.Value = info.Progress * 100;
                    TxtSpeed.Text = $"{FormatSize(info.DownloadedBytes)} / {FormatSize(info.TotalBytes)}" +
                                      $" · {FormatSize((long)info.SpeedBytesPerSec)}/s";
                }
                else
                {
                    TxtSpeed.Text = $"已下载 {FormatSize(info.DownloadedBytes)} · {FormatSize((long)info.SpeedBytesPerSec)}/s";
                }
            });
        };

        // 单文件进度
        _downloadService.ProgressChanged += info =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (info.TotalBytes > 0)
                    TxtSubStage.Text = $"当前文件 {info.Progress * 100:F0}% · {FormatSize(info.DownloadedBytes)}/{FormatSize(info.TotalBytes)}";
            });
        };

        // Java 下载状态文本
        _javaRuntime.ProgressChanged += s =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                TxtJavaStatus.Text = s;
                TxtStage.Text = s;
            });
        };

        // 单文件失败提示
        _downloadService.TaskFailed += t =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
                TxtSubStage.Text = $"失败:{Path.GetFileName(t.LocalPath)} - {t.Error}");
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }

    // ==================== 页面加载 ====================

    private async void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        TxtSourceLabel.Text = $"游戏版本与 Java 运行时下载 · 当前源:{_configService.Config.DownloadSource}(失败自动回退)";
        // 实例列表只在进入页面时扫一次磁盘(含迁移检查),后续筛选/搜索不再重复扫盘
        _instanceService.LoadInstances();
        await LoadVersionsAsync(forceRefresh: false);
    }

    // ==================== 游戏版本列表 ====================

    private async Task LoadVersionsAsync(bool forceRefresh)
    {
        TxtSearch.IsEnabled = false;
        var manifest = await _versionManifest.FetchManifestAsync(forceRefresh);
        TxtSearch.IsEnabled = true;

        if (manifest == null)
        {
            string err = _versionManifest.LastError;
            var owner = Window.GetWindow(this);
            FufuMessage.Error(owner, "版本清单加载失败",
                (string.IsNullOrEmpty(err) ? "无法获取版本清单。" : err) +
                $"\n\n当前下载源:{_configService.Config.DownloadSource}" +
                "\n已自动尝试回退源。可点击「刷新清单」重试,或在设置页切换下载源。");
            return;
        }

        string cachedHint = _versionManifest.CachedManifestUtc.HasValue
            ? $"(缓存于 {_versionManifest.CachedManifestUtc.Value.ToLocalTime():MM-dd HH:mm})"
            : "";
        TxtSourceLabel.Text = $"共 {manifest.Versions.Count} 个版本 · 当前源:{_configService.Config.DownloadSource} {cachedHint}";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        // 不再每次调 LoadInstances(高频全盘 IO 是卡顿根因),实例变化点(进页面/安装后)单独刷新
        string keyword = TxtSearch.Text.Trim();
        var items = new List<VersionListItem>();
        foreach (var v in _versionManifest.SearchAll(keyword, sortByReleaseDesc: true))
        {
            if (!string.IsNullOrEmpty(_currentFilter) && v.Type != _currentFilter) continue;
            items.Add(new VersionListItem
            {
                Version = v,
                InstalledVis = _versionManifest.IsVersionInstalled(v.Id, _instanceService)
                    ? Visibility.Visible : Visibility.Collapsed
            });
        }
        VersionList.ItemsSource = items;
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 防抖:连续输入时重置计时器,停止输入 250ms 后才刷新
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void FilterRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        _currentFilter = rb.Content?.ToString() switch
        {
            "正式版" => "release",
            "快照" => "snapshot",
            "Beta" => "old_beta",
            "远古版" => "old_alpha",
            _ => ""
        };
        if (VersionList != null) ApplyFilter();
    }

    private async void RefreshManifest_Click(object sender, RoutedEventArgs e)
    {
        await LoadVersionsAsync(forceRefresh: true);
    }

    // ==================== 游戏版本安装 ====================

    private async void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VersionListItem item) return;
        await StartInstallAsync(item.Version);
    }
    
    /// <summary>安装入口:输入实例名后,进入页内加载器选择步骤(不再弹独立窗口)</summary>
    private async Task StartInstallAsync(MojangVersion version)
    {
        if (_busy)
        {
            FufuMessage.Info(Window.GetWindow(this), "提示", "已有下载任务在进行中,请等待完成。");
            return;
        }
        var owner = Window.GetWindow(this);
    
        // 1. 输入实例名称
        var dlg = new InputDialogWindow("安装游戏版本",
            $"为 Minecraft {version.Id} 创建一个游戏版本:",
            version.Id, isPassword: false,
            validate: s => !string.IsNullOrWhiteSpace(s),
            watermark: "版本名称") { Owner = owner };
        if (dlg.ShowDialog() != true) return;
        string instanceName = (dlg.ResultText ?? version.Id).Trim();
    
        // 2. 进入页内加载器选择步骤
        _pendingVersion = version;
        _pendingInstanceName = instanceName;
        ShowLoaderSelect(version.Id);
    }
    
    // ==================== 页内加载器选择(整行点选 + 版本下拉,参照 PCL2 交互) ====================

    private static List<LoaderOptionVM> BuildLoaderOptions() => new()
    {
        new LoaderOptionVM { Key = "", DisplayName = "不安装加载器", Description = "安装纯原版 Minecraft,不添加任何模组加载器" },
        new LoaderOptionVM { Key = "Fabric", DisplayName = "Fabric", Description = "轻量主流加载器,模组更新快" },
        new LoaderOptionVM { Key = "NeoForge", DisplayName = "NeoForge", Description = "Forge 社区续作,1.20.1 及以上推荐" },
        new LoaderOptionVM { Key = "Forge", DisplayName = "Forge", Description = "经典加载器,模组生态丰富" },
        new LoaderOptionVM { Key = "Quilt", DisplayName = "Quilt", Description = "兼容大部分 Fabric 模组" },
        new LoaderOptionVM { Key = "OptiFine", DisplayName = "OptiFine", Description = "高清画质 + 性能优化 + 光影支持" },
    };

    private void ShowLoaderSelect(string versionId)
    {
        _loaderSession++;
        _loaderOptions.Clear();
        _loaderOptions.AddRange(BuildLoaderOptions());
        _loaderOptions[0].IsSelected = true;   // 默认选中「不安装加载器」,下拉隐藏
        TxtLoaderVersion.Text = $"Minecraft {versionId}";
        TxtLoaderHint.Text = "";
        LoaderList.ItemsSource = _loaderOptions;
        UpdateLoaderConfirmState();
        PageHeader.Visibility = Visibility.Collapsed;
        TabMain.Visibility = Visibility.Collapsed;
        LoaderSelectPanel.Visibility = Visibility.Visible;
    }

    private void HideLoaderSelect()
    {
        _loaderSession++;                     // 作废尚未返回的版本拉取任务
        LoaderSelectPanel.Visibility = Visibility.Collapsed;
        TabMain.Visibility = Visibility.Visible;
        PageHeader.Visibility = Visibility.Visible;
        LoaderList.ItemsSource = null;
        _loaderOptions.Clear();
        _pendingVersion = null;
        _pendingInstanceName = "";
    }

    private void LoaderCancel_Click(object sender, RoutedEventArgs e) => HideLoaderSelect();

    /// <summary>点击整行条目选中该加载器(忽略下拉框/按钮自身区域内的点击)</summary>
    private void LoaderRow_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement row || row.DataContext is not LoaderOptionVM vm) return;
        // 点在版本下拉框或重试按钮内部时,交由控件自身处理,不切换选中行
        var p = e.OriginalSource as DependencyObject;
        while (p != null && p != row)
        {
            if (p is ComboBox || p is Button) return;
            p = VisualTreeHelper.GetParent(p);
        }
        SelectLoader(vm);
    }

    private void SelectLoader(LoaderOptionVM vm)
    {
        if (vm.IsSelected) return;
        foreach (var o in _loaderOptions) o.IsSelected = o == vm;
        // 首次选中某个加载器时动态拉取版本列表(失败可在行内重试)
        if (vm.NeedsVersion && vm.State == LoaderOptionVM.LoaderState.None)
            _ = LoadLoaderVersionsAsync(vm);
        UpdateLoaderConfirmState();
    }

    /// <summary>从镜像源拉取该加载器的版本列表,区分正式版/测试版填入下拉</summary>
    private async Task LoadLoaderVersionsAsync(LoaderOptionVM vm)
    {
        if (_pendingVersion == null) return;
        int session = _loaderSession;
        vm.State = LoaderOptionVM.LoaderState.Loading;
        UpdateLoaderConfirmState();

        var result = await _loaderVersions.GetVersionsAsync(vm.Key, _pendingVersion.Id);
        if (session != _loaderSession) return;   // 页面已切换/关闭,丢弃结果

        if (!result.Success)
        {
            vm.ErrorText = result.Error ?? "获取版本失败";
            vm.State = LoaderOptionVM.LoaderState.Error;
            App.WriteAppLog($"[加载器] {vm.Key} 版本列表获取失败:{result.Error}");
        }
        else if (result.Versions.Count == 0)
        {
            vm.State = LoaderOptionVM.LoaderState.Empty;
        }
        else
        {
            vm.SourceLabel = result.SourceLabel;
            vm.ComboItems.Clear();
            var stable = result.Versions.Where(v => v.IsStable).ToList();
            var beta = result.Versions.Where(v => !v.IsStable).ToList();
            LoaderComboItem? firstStable = null;
            if (stable.Count > 0)
            {
                vm.ComboItems.Add(new LoaderComboItem { IsHeader = true, HeaderText = $"正式版({stable.Count})" });
                foreach (var v in stable)
                {
                    var item = new LoaderComboItem { Version = v.Version, Display = v.Version };
                    vm.ComboItems.Add(item);
                    firstStable ??= item;
                }
            }
            if (beta.Count > 0)
            {
                vm.ComboItems.Add(new LoaderComboItem { IsHeader = true, HeaderText = $"测试版({beta.Count})" });
                foreach (var v in beta)
                    vm.ComboItems.Add(new LoaderComboItem { Version = v.Version, Display = v.Version });
            }
            vm.SelectedEntry = firstStable ?? vm.ComboItems.FirstOrDefault(i => !i.IsHeader);
            vm.State = LoaderOptionVM.LoaderState.Ready;
        }
        UpdateLoaderConfirmState();
    }

    /// <summary>行内「重试」:重新拉取该加载器的版本列表</summary>
    private void LoaderRetry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not LoaderOptionVM vm) return;
        _ = LoadLoaderVersionsAsync(vm);
    }

    /// <summary>版本下拉选择变化:标题项不可选(自动回退);同步确定按钮可用性</summary>
    private void LoaderVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not LoaderOptionVM vm) return;
        if (combo.SelectedItem is LoaderComboItem ci && ci.IsHeader)
        {
            // 分组标题不可选,回退到上一个有效选择
            combo.SelectedItem = vm.SelectedEntry is LoaderComboItem prev && !prev.IsHeader
                ? prev
                : vm.ComboItems.FirstOrDefault(i => !i.IsHeader);
            return;
        }
        vm.SelectedEntry = combo.SelectedItem as LoaderComboItem;
        UpdateLoaderConfirmState();
    }

    /// <summary>确定按钮可用性校验:选了加载器必须已选定具体版本</summary>
    private void UpdateLoaderConfirmState()
    {
        if (BtnLoaderConfirm == null) return;
        var sel = _loaderOptions.FirstOrDefault(o => o.IsSelected);
        bool canConfirm = true;
        string hint = "";
        if (sel != null && sel.NeedsVersion)
        {
            switch (sel.State)
            {
                case LoaderOptionVM.LoaderState.Loading:
                    canConfirm = false; hint = "正在获取加载器版本列表…"; break;
                case LoaderOptionVM.LoaderState.Error:
                    canConfirm = false; hint = "获取版本失败,请点击行内「重试」"; break;
                case LoaderOptionVM.LoaderState.Empty:
                    canConfirm = false; hint = "该游戏版本暂无可用的加载器版本"; break;
                case LoaderOptionVM.LoaderState.None:
                    canConfirm = false; hint = "请先选择加载器版本"; break;
                default:
                    if (sel.SelectedEntry == null || sel.SelectedEntry.IsHeader)
                    {
                        canConfirm = false; hint = "请先在右侧下拉框选择具体的加载器版本";
                    }
                    break;
            }
        }
        BtnLoaderConfirm.IsEnabled = canConfirm;
        TxtLoaderHint.Text = hint;
    }

    private async void LoaderConfirm_Click(object sender, RoutedEventArgs e)
    {
        var version = _pendingVersion;
        string instanceName = _pendingInstanceName;
        var sel = _loaderOptions.FirstOrDefault(o => o.IsSelected);
        if (version == null || sel == null) { HideLoaderSelect(); return; }

        // 二次校验(与按钮可用性一致)
        if (sel.NeedsVersion && (sel.State != LoaderOptionVM.LoaderState.Ready
            || sel.SelectedEntry == null || sel.SelectedEntry.IsHeader))
        {
            UpdateLoaderConfirmState();
            return;
        }

        string loaderKey = sel.Key;
        string? loaderVersion = sel.NeedsVersion ? sel.SelectedEntry!.Version : null;
        HideLoaderSelect();
        await ProceedInstallAsync(version, instanceName, loaderKey, loaderVersion);
    }

    /// <summary>实际执行游戏本体 + 加载器的下载安装</summary>
    private async Task ProceedInstallAsync(MojangVersion version, string instanceName, string loader, string? loaderVersion)
    {
        var owner = Window.GetWindow(this);
        _lastGameVersion = version;
        SetBusy(true, $"开始安装 Minecraft {version.Id}…");
        try
        {
            var inst = _instanceService.CreateInstance(instanceName, version.Id, _configService.Config.JavaVersion);
            bool ok = await _gameInstall.InstallVersionAsync(inst.Id, version);
            if (!ok)
            {
                throw new Exception(string.IsNullOrEmpty(_gameInstall.LastError)
                    ? "安装失败(未知原因)" : _gameInstall.LastError);
            }

            // 加载器安装(本体完成后,安装用户选定的具体版本)
            string loaderMsg = "";
            if (!string.IsNullOrEmpty(loader))
            {
                TxtStage.Text = $"安装加载器 {loader} {loaderVersion}…";
                var result = await _loaderInstall.InstallLoaderAsync(inst.Id, version.Id, loader, loaderVersion);
                loaderMsg = result.Success
                    ? $"\n\n✅ {loader} {result.InstalledVersion} 安装完成。"
                    : $"\n\n⚠ {loader} 安装失败:{result.ErrorMessage}";
            }
    
            string javaMsg = _gameInstall.JavaAutoDownloadSucceeded
                ? "匹配的 Java 运行时已自动下载就绪。"
                : (string.IsNullOrEmpty(_gameInstall.JavaAutoDownloadHint)
                    ? "未检测到匹配的 Java 运行时,请到「Java 管理」下载。"
                    : _gameInstall.JavaAutoDownloadHint);
    
            SetBusy(false);
            FufuMessage.Success(owner, "安装完成",
                $"Minecraft {version.Id} 安装完成!\n\n{javaMsg}{loaderMsg}\n已回到主页即可选择启动。");
            _instanceService.LoadInstances();  // 安装后实例集变化,刷新一次再重绘列表
            ApplyFilter();
        }
        catch (Exception ex)
        {
            SetBusy(false);
            TxtStage.Text = "安装失败";
            BtnRetry.Visibility = Visibility.Visible;
            App.WriteAppLog($"[下载] 版本 {version.Id} 安装失败:{ex.Message}");
            FufuMessage.Error(owner, "安装失败",
                $"Minecraft {version.Id} 安装失败:\n{ex.Message}" +
                $"\n\n当前下载源:{_configService.Config.DownloadSource}" +
                "\n可点击进度卡片上的「重试」,或在设置页切换下载源后再试。");
        }
    }

    // ==================== Java 运行时下载 ====================

    private async void RefreshJdk_Click(object sender, RoutedEventArgs e) => await LoadJdkListAsync();

    private async Task EnsureJdkListAsync()
    {
        if (_jdkTabLoaded) return;
        _jdkTabLoaded = true;
        TxtJavaMirror.Text = $"镜像源:{_javaRuntime.GetCurrentMirrorLabel()}";
        await LoadJdkListAsync();
    }

    private async Task LoadJdkListAsync()
    {
        TxtJavaStatus.Text = "正在获取可下载的 Java 版本列表…";
        try
        {
            var available = await _javaRuntime.FetchAvailableJdkVersionsAsync();
            var installed = _javaRuntime.ListInstalledRuntimes();

            // 合并去重:同一主版本只展示一条(修复旧版 Java 双重下载项问题)
            var items = new List<JdkListItem>();
            foreach (var v in available.OrderByDescending(x => x.MajorVersion))
            {
                var installedEntry = installed.FirstOrDefault(r =>
                    r.MajorVersion == $"Java {v.MajorVersion}" && r.Status == "已就绪");
                bool ready = installedEntry != null;
                items.Add(new JdkListItem
                {
                    MajorVersion = v.MajorVersion,
                    DisplayName = $"Java {v.MajorVersion}" + (string.IsNullOrEmpty(v.DisplayName) ? "" : $" · {v.DisplayName}"),
                    LtsVis = v.IsLts ? Visibility.Visible : Visibility.Collapsed,
                    StatusText = ready
                        ? $"✓ 已安装就绪({installedEntry!.Name})"
                        : (v.SupportedByCurrentMirror ? "当前镜像源可下载" : "当前镜像源暂不支持,可切换镜像源"),
                    ButtonText = ready ? "✓ 已安装" : "⬇ 下载",
                    CanDownload = !ready && v.SupportedByCurrentMirror
                });
            }
            JdkList.ItemsSource = items;
            TxtJavaStatus.Text = items.Count == 0
                ? "未获取到可下载列表,请检查网络后点击「刷新列表」重试。"
                : $"共 {items.Count} 个可下载版本 · 下载目录:{JavaRuntimeService.RuntimesDir}";
        }
        catch (Exception ex)
        {
            TxtJavaStatus.Text = "获取 Java 列表失败:" + ex.Message;
            App.WriteAppLog($"[下载] Java 列表获取失败:{ex}");
        }
    }

    private async void DownloadJdk_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not JdkListItem item) return;
        if (_busy)
        {
            FufuMessage.Info(Window.GetWindow(this), "提示", "已有下载任务在进行中,请等待完成。");
            return;
        }

        _lastJdk = item;
        SetBusy(true, $"开始下载 Java {item.MajorVersion}…");
        JavaProgress.Visibility = Visibility.Visible;
        JavaProgress.IsIndeterminate = true;
        try
        {
            string? javaExe = await _javaRuntime.DownloadJdkAsync(item.MajorVersion);
            JavaProgress.IsIndeterminate = false;
            JavaProgress.Value = 100;

            if (!string.IsNullOrEmpty(javaExe) && JavaRuntimeService.VerifyJavaIntegrity(javaExe))
            {
                SetBusy(false);
                FufuMessage.Success(Window.GetWindow(this), "下载完成",
                    $"Java {item.MajorVersion} 已下载就绪:\n{javaExe}\n\n可在主页或「Java 管理」中选用。");
            }
            else
            {
                throw new Exception("下载完成但完整性校验未通过,文件可能损坏,请重试。");
            }
            await LoadJdkListAsync();
        }
        catch (Exception ex)
        {
            JavaProgress.IsIndeterminate = false;
            SetBusy(false);
            BtnRetry.Visibility = Visibility.Visible;
            App.WriteAppLog($"[下载] Java {item.MajorVersion} 下载失败:{ex.Message}");
            FufuMessage.Error(Window.GetWindow(this), "Java 下载失败",
                $"Java {item.MajorVersion} 下载失败:\n{ex.Message}" +
                $"\n\n当前镜像源:{_javaRuntime.GetCurrentMirrorLabel()}" +
                "\n可点击「重试」,或在设置页切换 Java 镜像源后再试。");
        }
    }

    // ==================== 重试 / 取消 ====================

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        BtnRetry.Visibility = Visibility.Collapsed;
        if (_lastGameVersion != null)
        {
            var v = _lastGameVersion;
            _lastGameVersion = null;
            await StartInstallAsync(v);
        }
        else if (_lastJdk != null)
        {
            var j = _lastJdk;
            _lastJdk = null;
            DownloadJdk_Click(new Button { Tag = j }, e);
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        if (!FufuMessage.Confirm(Window.GetWindow(this), "取消下载",
                "确定取消当前下载任务吗?已下载的部分文件将被保留,可稍后重试。",
                okText: "取消下载", danger: true))
            return;
        _downloadService.Cancel();
        SetBusy(false);
        TxtStage.Text = "已取消";
    }

    private void SetBusy(bool busy, string? stage = null)
    {
        _busy = busy;
        ProgressCard.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy)
        {
            OverallBar.IsIndeterminate = false;
            BtnRetry.Visibility = Visibility.Collapsed;
        }
        else
        {
            OverallBar.Value = 0;
        }
        if (stage != null) TxtStage.Text = stage;
    }

    /// <summary>Tab 切换:进入 Java 标签时懒加载列表</summary>
    private void Tab_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is TabControl && TabJava != null && TabJava.IsSelected)
            _ = EnsureJdkListAsync();
    }
}
