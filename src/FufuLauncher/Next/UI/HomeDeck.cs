// FufuLauncher - 主页 Deck
// Copyright © FufuLauncher
//
// 职责:启动大卡(真实 GameLaunchService 启动编排) + 快捷入口 + 状态图标区。
// 启动链路:账号令牌校验 → Java 运行时解析 → 版本文件校验 → 智能内存分配 → 进程拉起,
// 全部由 GameLaunchService 完成;本页只负责选版本、点按钮、播报状态。
// 状态图标区:Java 就绪 / 网络连通 / 下载任务 / 磁盘余量四枚图标,悬停气泡显示详情。
// 状态联动:账号变更 / 版本增删 / 游戏退出 / 下载任务变化 均实时刷新界面。

using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class HomeDeck : UserControl
{
    private readonly InstanceService _instances;
    private readonly GameLaunchService _launch;
    private readonly AccountService _accounts;
    private readonly ConfigService _config;
    private readonly JavaRuntimeService _javaRuntimes;
    private readonly JavaScanService _javaScan;
    private readonly DownloadService _downloads;

    private readonly ComboBox _versionBox = UIKit.ComboBox();
    private readonly TextBlock _accountText = UIKit.Text("未登录", 13, FontWeights.Medium);
    private readonly TextBlock _memHint = UIKit.Sub("", 12);
    private readonly TextBlock _launchStatus = UIKit.Sub("", 12);
    private readonly Button _launchBtn;
    // 状态图标区(悬停气泡显示详情)
    private Border? _statusJava = null, _statusNet = null, _statusDown = null, _statusDisk = null;   // 2026-09-25:主页状态小球已移除,字段保留判空逻辑,显式 null 消 CS0649
    private TextBlock? _statusJavaVal = null, _statusNetVal = null, _statusDownVal = null, _statusDiskVal = null;
    private DateTime _netCheckedAt = DateTime.MinValue;
    private bool _launching;

    public HomeDeck()
    {
        // 主页构建分段计时(2026-08-29 启动提速排查):定位首屏构建耗时构成,稳定后可删
        var hw = System.Diagnostics.Stopwatch.StartNew();
        long hwLast = 0;
        void HStage(string s)
        {
            long now = hw.ElapsedMilliseconds;
            App.WriteAppLog($"[主页计时] {s} +{now - hwLast}ms 累计{now}ms");
            hwLast = now;
        }
        _instances = App.Services.GetRequiredService<InstanceService>();
        _launch = App.Services.GetRequiredService<GameLaunchService>();
        _accounts = App.Services.GetRequiredService<AccountService>();
        _config = App.Services.GetRequiredService<ConfigService>();
        _javaRuntimes = App.Services.GetRequiredService<JavaRuntimeService>();
        _javaScan = App.Services.GetRequiredService<JavaScanService>();
        _downloads = App.Services.GetRequiredService<DownloadService>();
        HStage("DI 服务解析");

        _launchBtn = UIKit.Button("启动游戏", primary: true, onClick: OnLaunch, height: 52);
        _launchBtn.MinWidth = 190;

        BuildUi();
        HStage("UI 构建");
        RefreshVersions();
        RefreshAccount();
        RefreshMemHint();
        SyncRunningState();
        HStage("版本/账号/内存刷新");
        RefreshStatusIcons();
        HStage("状态图标刷新");
        CheckNetworkAsync();

        // 订阅/退订跟随页面可见性:Deck 实例常驻但 Loaded/Unloaded 反复触发,
        // 只在构造函数订阅会在切页(Unloaded)后永久丢失游戏退出事件,导致状态不再同步
        Loaded += (_, _) => SubscribeEvents();
        Unloaded += (_, _) => UnsubscribeEvents();
    }

    private void SubscribeEvents()
    {
        _accounts.AccountsChanged += OnAccountsChanged;
        _launch.GameExited += OnGameExited;
        _launch.GameMemoryWarning += OnGameMemoryWarning;
        _launch.GameMemoryUpdated += OnGameMemoryUpdated;
        _instances.InstancesChanged += OnInstancesChanged;
        _downloads.TaskStatusChanged += OnDownloadTaskChanged;
        _downloads.TaskCompleted += OnDownloadTaskChanged;
        _downloads.TaskFailed += OnDownloadTaskChanged;
        SyncRunningState();   // 回页时复验运行态,避免错过运行中的游戏
    }

    private void UnsubscribeEvents()
    {
        _accounts.AccountsChanged -= OnAccountsChanged;
        _launch.GameExited -= OnGameExited;
        _launch.GameMemoryWarning -= OnGameMemoryWarning;
        _launch.GameMemoryUpdated -= OnGameMemoryUpdated;
        _instances.InstancesChanged -= OnInstancesChanged;
        _downloads.TaskStatusChanged -= OnDownloadTaskChanged;
        _downloads.TaskCompleted -= OnDownloadTaskChanged;
        _downloads.TaskFailed -= OnDownloadTaskChanged;
    }

    // ==================== 布局 ====================

    private void BuildUi()
    {
        // ---- 极简主页:上面全空透背景,只留底部一条通栏 ----
        // 左:版本下拉(带账号/内存小字);中:状态 chips;右:启动按钮(左下角大按钮)
        var subLine = UIKit.H(_accountText, _memHint, _launchStatus);
        _accountText.Margin = new Thickness(0, 0, 14, 0);
        subLine.Margin = new Thickness(0, 4, 0, 0);
        var versionBlock = UIKit.V(_versionBox, subLine);
        versionBlock.MinWidth = 280;

        _launchBtn.VerticalAlignment = VerticalAlignment.Center;
        _launchBtn.HorizontalAlignment = HorizontalAlignment.Right;

        // 底部:左边弹性空白,右边版本下拉(带账号/内存)+ 启动按钮 紧贴
        var bottomGrid = new Grid
        {
            Margin = new Thickness(4, 0, 4, 8),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children =
            {
                versionBlock.Col(1),
                _launchBtn.Col(2)
            }
        };
        versionBlock.Margin = new Thickness(0, 0, 16, 0);

        // 顶部留白:标题小字 + 弹性空白把底栏顶到最下面
        Content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            },
            Children =
            {
                UIKit.PageHeader("主页", "选择版本与账号,一键启动 Minecraft").Row(0),
                bottomGrid.Row(2)
            }
        };
    }

    /// <summary>紧凑状态 chip:无大卡片,仅 glyph + 标题 + 值,横向排一行</summary>
    private static Border MakeStatusChip(string glyph, string title, out TextBlock value)
    {
        value = UIKit.Text("—", 12, FontWeights.SemiBold);
        var icon = UIKit.Text(glyph, 13, FontWeights.Medium, "T.Primary");
        var titleTb = UIKit.Sub(title, 11);
        titleTb.Margin = new Thickness(8, 0, 6, 0);
        var row = UIKit.H(icon, titleTb, value);
        row.Margin = new Thickness(0, 0, 24, 0);
        row.VerticalAlignment = VerticalAlignment.Center;
        var card = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            Padding = new Thickness(10, 6, 12, 6),
            BorderThickness = new Thickness(1),
            Child = row
        };
        card.WithRef("T.SurfaceAlt", "T.Border");
        return card;
    }

    /// <summary>状态图标:左侧符号 + 标题/数值,悬停气泡(ToolTip)展示详情</summary>
    private static Border MakeStatusIcon(string glyph, string title, out TextBlock value)
    {
        value = UIKit.Text("—", 13, FontWeights.SemiBold);
        var icon = UIKit.Text(glyph, 16, FontWeights.Medium, "T.Primary");
        var col = UIKit.V(UIKit.Sub(title, 11), value);
        col.Margin = new Thickness(10, 2, 0, 0);
        var row = UIKit.H(icon, col);
        row.VerticalAlignment = VerticalAlignment.Center;
        var card = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Panel),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(6),
            BorderThickness = new Thickness(1),
            Child = row
        };
        card.WithRef("T.SurfaceAlt", "T.Border");
        // 悬停反馈:轻微上浮 + 描边主色化(纯视觉,不改布局)
        card.MouseEnter += (_, _) =>
        {
            card.Lift(-2);
            card.SetResourceReference(Border.BorderBrushProperty, "T.Primary");
        };
        card.MouseLeave += (_, _) =>
        {
            card.Lift(0);
            card.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        };
        return card;
    }

    /// <summary>悬停气泡:多行明细文本</summary>
    private static void SetStatusTip(Border card, string detail)
    {
        var tip = new TextBlock
        {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Padding = new Thickness(4, 2, 4, 2)
        };
        card.ToolTip = tip;
    }

    private static void SetStatusColor(TextBlock value, string colorKey)
        => value.SetResourceReference(TextBlock.ForegroundProperty, colorKey);

    // ==================== 数据刷新 ====================

    private void RefreshVersions()
    {
        string prevId = (_versionBox.SelectedItem as GameInstance)?.Id ?? _config.Config.LastInstanceId;
        _versionBox.Items.Clear();
        foreach (var inst in _instances.Instances)
            _versionBox.Items.Add(inst);   // GameInstance.ToString() = 名称
        if (_instances.Instances.Count == 0)
        {
            _versionBox.Items.Add("(暂无本地版本,前往版本管理安装)");
            _versionBox.SelectedIndex = 0;
            _versionBox.IsEnabled = false;
            return;
        }
        _versionBox.IsEnabled = true;
        // 优先保持当前选中,其次恢复上次启动的版本
        int idx = _instances.Instances.FindIndex(i => i.Id == prevId);
        if (idx < 0) idx = _instances.Instances.FindIndex(i => i.Id == _config.Config.LastInstanceId);
        _versionBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void RefreshAccount()
    {
        var acc = _accounts.CurrentAccount;
        _accountText.Text = acc == null
            ? "未登录(点击侧栏账号卡登录)"
            : $"{acc.Username}  ·  {(acc.Type == AccountType.Microsoft ? "微软账号" : "离线账号")}";
    }

    private void RefreshMemHint()
    {
        var cfg = _config.Config;
        _memHint.Text = cfg.AutoMemoryMode
            ? "智能分配(启动时按可用内存实时计算)"
            : $"手动:{cfg.Xms} MB ~ {cfg.Xmx} MB";
    }

    /// <summary>进入主页时若游戏已在运行,按钮与状态直接呈现运行态</summary>
    private void SyncRunningState()
    {
        if (!_launch.IsGameRunning) return;
        var running = _instances.Instances.Find(i => i.Id == _launch.RunningInstanceId);
        _launchStatus.Text = $"游戏运行中:{running?.Name ?? "Minecraft"}";
        UIKit.SetButtonText(_launchBtn, "结束游戏");
    }

    private void OnAccountsChanged() => Dispatcher.BeginInvoke(RefreshAccount);

    /// <summary>其它页新增/删除/重命名版本后,主页下拉即时联动</summary>
    private void OnInstancesChanged() => Dispatcher.BeginInvoke(RefreshVersions);

    // ==================== 状态图标区 ====================

    private void RefreshStatusIcons()
    {
        RefreshJavaStatus();
        RefreshDownloadStatus();
        RefreshDiskStatus();
    }

    private void RefreshJavaStatus()
    {
        if (_statusJava == null || _statusJavaVal == null) return;
        List<InstalledJavaEntry> runtimes = new();
        try { runtimes = _javaRuntimes.ListInstalledRuntimes(); }
        catch (Exception ex) { App.WriteAppLog($"[主页] 统计 Java 状态失败:{ex.Message}"); }
        var ready = runtimes.Where(r => r.Status == "已就绪").ToList();
        if (ready.Count > 0)
        {
            _statusJavaVal.Text = $"{ready.Count} 个就绪";
            SetStatusColor(_statusJavaVal, "T.Success");
            SetStatusTip(_statusJava, "就绪的 Java(启动器内置):\n" + string.Join("\n",
                ready.Select(r => $"· Java {r.MajorVersion.TrimStart("Java ".ToCharArray())}  {r.JavaExe}")));
            return;
        }

        // 内置 runtimes 池无就绪 Java(纯净包首次启动常见):后台扫描系统已装 Java,
        // 扫到即视为可用(启动链路同样兜底系统 Java),避免误报「未就绪」
        _statusJavaVal.Text = "扫描系统 Java…";
        SetStatusColor(_statusJavaVal, "T.ForegroundDim");
        SetStatusTip(_statusJava, "启动器内置 Java 未安装,正在扫描系统已安装的 Java…");
        ScanSystemJavaAsync();
    }

    /// <summary>后台扫描系统 Java(JAVA_HOME/注册表/PATH/常见目录),结果刷新状态卡</summary>
    private async void ScanSystemJavaAsync()
    {
        try
        {
            await _javaScan.ScanAsync();
            if (!IsLoaded || _statusJava == null || _statusJavaVal == null) return;
            var sysJavas = _javaScan.FoundJavas
                .Where(j => j.MajorVersion > 0 && !string.IsNullOrEmpty(j.Path))
                .OrderByDescending(j => j.MajorVersion).ToList();
            if (sysJavas.Count > 0)
            {
                _statusJavaVal.Text = $"系统 {sysJavas.Count} 个";
                SetStatusColor(_statusJavaVal, "T.Success");
                SetStatusTip(_statusJava, "系统已安装的 Java(启动时自动匹配):\n" + string.Join("\n",
                    sysJavas.Select(j => $"· Java {j.MajorVersion}  {j.Path}")) +
                    "\n\n如需更稳定的运行环境,可在「设置 → Java 运行时」下载内置 Java");
            }
            else
            {
                _statusJavaVal.Text = "未就绪";
                SetStatusColor(_statusJavaVal, "T.Warning");
                SetStatusTip(_statusJava, "未找到任何 Java(内置与系统均无)。\n请前往「设置 → Java 运行时」下载安装。");
            }
        }
        catch (Exception ex) { App.WriteAppLog($"[主页] 系统 Java 扫描失败:{ex.Message}"); }
    }

    /// <summary>网络连通探测(60s 内不重复探测,避免频繁请求;
    /// 当前源失败自动兜底探测另一源 —— 单源抖动/区域屏蔽不应误报「不可达」)</summary>
    private async void CheckNetworkAsync()
    {
        try
        {
            if (_statusNet == null || _statusNetVal == null) return;
            if ((DateTime.Now - _netCheckedAt).TotalSeconds < 60) return;
            _netCheckedAt = DateTime.Now;
            _statusNetVal.Text = "探测中…";
            SetStatusColor(_statusNetVal, "T.ForegroundDim");

            string source = _config.Config.DownloadSource;
            string primaryName = source switch { "Mojang" => "Mojang", "Auto" => "智能自动", _ => "BMCLAPI" };
            string primaryUrl = source == "Mojang"
                ? "https://piston-meta.mojang.com/mc/game/version_manifest.json"
                : "https://bmclapi2.bangbang93.com/mc/game/version_manifest.json";
            string fallbackName = primaryName == "Mojang" ? "BMCLAPI" : "Mojang";
            string fallbackUrl = primaryName == "Mojang"
                ? "https://bmclapi2.bangbang93.com/mc/game/version_manifest.json"
                : "https://piston-meta.mojang.com/mc/game/version_manifest.json";

            var first = await ProbeSourceAsync(primaryUrl);
            var hit = first;
            string hitName = primaryName;
            if (!first.Reached && !first.NonSuccess)
            {
                var second = await ProbeSourceAsync(fallbackUrl);
                if (second.Reached || second.NonSuccess) { hit = second; hitName = fallbackName; }
            }
            if (!IsLoaded) return;

            if (hit.Reached)
            {
                _statusNetVal.Text = $"{hit.LatencyMs}ms";
                SetStatusColor(_statusNetVal, "T.Success");
                SetStatusTip(_statusNet, hitName == primaryName
                    ? $"下载源:{source}\n探测目标:{primaryUrl}\n结果:可达,延迟 {hit.LatencyMs}ms"
                    : $"当前源 {source} 暂不可达,已兜底探测到 {hitName}\n延迟 {hit.LatencyMs}ms(建议在设置 → 网络设置切换下载源)");
            }
            else if (hit.NonSuccess)
            {
                _statusNetVal.Text = "异常";
                SetStatusColor(_statusNetVal, "T.Warning");
                SetStatusTip(_statusNet, $"探测源:{hitName}\n结果:HTTP {hit.StatusCode},建议切换其它下载源");
            }
            else
            {
                _statusNetVal.Text = "不可达";
                SetStatusColor(_statusNetVal, "T.Danger");
                SetStatusTip(_statusNet, $"Mojang 与 BMCLAPI 均无法连接\n最后错误:{hit.Error}\n请检查网络或代理设置(设置 → 网络设置)");
                App.WriteAppLog($"[主页] 下载源探测失败(双源):{hit.Error}");
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[主页] 操作异常:" + ex.Message);
            Shell()?.SetStatus("操作失败,详见日志", warning: true);
        }
    }

    /// <summary>单源探测结果(Reached=可达 / NonSuccess=收到非成功状态码 / 两者皆否=连接失败)</summary>
    private record struct ProbeResult(bool Reached, bool NonSuccess, long LatencyMs, int StatusCode, string Error);

    /// <summary>探测用 HttpClient:进程级复用,避免频繁新建造成 socket 堆积(2026-09 修复)</summary>
    private static readonly HttpClient ProbeHttp = CreateProbeHttp();

    private static HttpClient CreateProbeHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/1.9.8.6 (Windows)"); } catch { }
        return c;
    }

    /// <summary>单源探测:15s 超时(冷启动首包含 DNS/TLS 握手,8s 偏紧易误判不可达)</summary>
    private static async Task<ProbeResult> ProbeSourceAsync(string url)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await ProbeHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            sw.Stop();
            return resp.IsSuccessStatusCode
                ? new ProbeResult(true, false, sw.ElapsedMilliseconds, (int)resp.StatusCode, "")
                : new ProbeResult(false, true, sw.ElapsedMilliseconds, (int)resp.StatusCode, "");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult(false, false, sw.ElapsedMilliseconds, 0, ex.Message);
        }
    }

    private void OnDownloadTaskChanged(DownloadTaskItem _) => Dispatcher.BeginInvoke(RefreshDownloadStatus);

    private void RefreshDownloadStatus()
    {
        if (_statusDown == null || _statusDownVal == null) return;
        try
        {
            var batch = _downloads.CurrentBatchSnapshot();
            int active = batch.Count(t => t.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Verifying);
            int paused = batch.Count(t => t.Status == DownloadStatus.Paused);
            int failed = batch.Count(t => t.Status == DownloadStatus.Failed);
            int done = batch.Count(t => t.Status == DownloadStatus.Completed);
            if (active > 0)
            {
                _statusDownVal.Text = $"{active} 进行中";
                SetStatusColor(_statusDownVal, "T.Primary");
            }
            else if (paused > 0)
            {
                _statusDownVal.Text = $"{paused} 已暂停";
                SetStatusColor(_statusDownVal, "T.Warning");
            }
            else if (failed > 0)
            {
                _statusDownVal.Text = $"{failed} 失败";
                SetStatusColor(_statusDownVal, "T.Danger");
            }
            else if (done > 0)
            {
                _statusDownVal.Text = $"{done} 完成";
                SetStatusColor(_statusDownVal, "T.Success");
            }
            else
            {
                _statusDownVal.Text = "空闲";
                SetStatusColor(_statusDownVal, "T.ForegroundDim");
            }
            SetStatusTip(_statusDown, batch.Count == 0
                ? "当前没有下载任务。\n点击下载可直达下载中心。"
                : $"本批任务:{batch.Count} 个\n进行中 {active} · 已完成 {done} · 已暂停 {paused} · 失败 {failed}\n点击下载可直达下载中心");
        }
        catch (Exception ex) { App.WriteAppLog($"[主页] 统计下载任务失败:{ex.Message}"); }
    }

    private void RefreshDiskStatus()
    {
        if (_statusDisk == null || _statusDiskVal == null) return;
        try
        {
            string? rootPath = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(AppPaths.Root));
            if (string.IsNullOrEmpty(rootPath)) throw new InvalidOperationException("无法定位数据目录盘符");
            var drive = new System.IO.DriveInfo(rootPath);
            double freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
            double totalGb = drive.TotalSize / 1024.0 / 1024 / 1024;
            bool low = freeGb < 5;
            _statusDiskVal.Text = $"{freeGb:F1} GB";
            SetStatusColor(_statusDiskVal, low ? "T.Warning" : "T.Success");
            SetStatusTip(_statusDisk, $"数据目录所在磁盘 {drive.Name.TrimEnd('\\')}\n总容量 {totalGb:F1} GB,剩余 {freeGb:F1} GB" +
                (low ? "\n剩余空间偏少,安装游戏版本可能失败,建议清理磁盘。" : "\n剩余空间充足。"));
        }
        catch (Exception ex)
        {
            _statusDiskVal.Text = "未知";
            SetStatusColor(_statusDiskVal, "T.ForegroundDim");
            SetStatusTip(_statusDisk, "磁盘余量检查失败:" + ex.Message);
        }
    }

    private void OnGameMemoryUpdated(GameMemorySnapshot snap)
    {
        try
        {
            // 游戏运行中实时刷新:总占用(堆外 X)让用户直观看到堆外内存涨落
            var running = _instances.Instances.Find(i => i.Id == _launch.RunningInstanceId);
            string name = running?.Name ?? "Minecraft";
            _launchStatus.Text = $"{name} 运行中 · 内存 {snap.TotalMb / 1024.0:F1}G" +
                                 (snap.OffHeapMb > 0 ? $"(堆外 {snap.OffHeapMb / 1024.0:F1}G)" : "");
        }
        catch { /* 状态刷新异常不影响 */ }
    }

    private void OnGameMemoryWarning(string message)
    {
        try { Shell()?.SetStatus(message, warning: true); } catch { /* 状态栏异常不影响 */ }
    }

    private void OnGameExited()
    {
        Dispatcher.BeginInvoke(() =>
        {
            SetLaunchButtonIdle();
            _launchStatus.Text = "游戏已退出";
            Shell()?.SetStatus("游戏已退出");
            App.WriteAppLog("[主页] 游戏进程退出,启动按钮恢复");
        });
    }

    // ==================== 启动 ====================

    private async void OnLaunch()
    {
        try
        {
            // 游戏运行中:按钮变为"结束游戏"
            if (_launch.IsGameRunning)
            {
                if (DialogKit.Confirm("确定要强制结束游戏进程吗?", "结束游戏", Window.GetWindow(this)))
                {
                    _launch.KillGame();
                    Shell()?.SetStatus("已发送结束游戏指令", warning: true);
                }
                return;
            }
            if (_launching) return;

            if (_versionBox.SelectedItem is not GameInstance inst)
            {
                DialogKit.Info("还没有本地游戏版本,请先前往「版本管理 → 新装版本」安装。", "无可用版本", Window.GetWindow(this));
                return;
            }
            if (_accounts.CurrentAccount == null)
            {
                DialogKit.Info("请先登录账号(微软或离线),否则无法启动游戏。", "未登录", Window.GetWindow(this));
                Shell()?.Switch(ShellWindow.DeckKey.Accounts);
                return;
            }

            _launching = true;
            _launchBtn.IsEnabled = false;
            _launchStatus.Text = "正在启动:校验账号与 Java 运行时…";
            Shell()?.SetStatus("正在启动游戏…");
            App.WriteAppLog($"[主页] 启动请求:版本={inst.Name}({inst.Id}) 账号={_accounts.CurrentAccount.Username}");

            try
            {
                var result = await _launch.LaunchAsync(inst.Id);
                if (!result.Success)
                {
                    _launchStatus.Text = "启动失败";
                    Shell()?.SetStatus("启动失败", warning: true);
                    DialogKit.Error(result.ErrorMessage, "启动失败", Window.GetWindow(this));
                    SetLaunchButtonIdle();
                    return;
                }

                // 记住本次启动的版本
                _config.Config.LastInstanceId = inst.Id;
                _config.Save();

                _launchStatus.Text = $"游戏运行中:{inst.Name}";
                Shell()?.SetStatus($"游戏运行中:{inst.Name}");
                _launchBtn.IsEnabled = true;
                UIKit.SetButtonText(_launchBtn, "结束游戏");
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[主页] 启动异常:{ex}");
                _launchStatus.Text = "启动异常";
                DialogKit.Error("启动过程发生异常:" + ex.Message, "启动失败", Window.GetWindow(this));
                SetLaunchButtonIdle();
            }
            finally { _launching = false; }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[主页] 操作异常:" + ex.Message);
            Shell()?.SetStatus("操作失败,详见日志", warning: true);
        }
    }

    private void SetLaunchButtonIdle()
    {
        _launchBtn.IsEnabled = true;
        UIKit.SetButtonText(_launchBtn, "启动游戏");
    }

    // ==================== 快捷入口 ====================

    private void GoNewVersion() { Shell()?.Switch(ShellWindow.DeckKey.Instances); Shell()?.SelectSub(ShellWindow.DeckKey.Instances, 1); }
    private void GoMods() => Shell()?.Switch(ShellWindow.DeckKey.Mods);
    private void GoAccounts() => Shell()?.Switch(ShellWindow.DeckKey.Accounts);

    private void OpenDataDir()
    {
        try { System.Diagnostics.Process.Start("explorer.exe", AppPaths.Root)?.Dispose(); }
        catch (Exception ex) { DialogKit.Error("打开数据目录失败:" + ex.Message, owner: Window.GetWindow(this)); }
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
