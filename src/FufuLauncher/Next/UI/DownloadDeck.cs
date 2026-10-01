// FufuLauncher - 下载中心 Deck
// Copyright © FufuLauncher
//
// 职责:展示 DownloadService 的真实下载任务(游戏安装/资源/Java/模组),
// 订阅 TaskStatusChanged / ProgressChanged / OverallProgressChanged 实时刷新;
// 提供暂停 / 继续 / 取消与批量重试入口(下载源切换/测速在「设置 → 网络设置」)。
// 说明:下载引擎为批次制,暂停/继续/取消作用于整批任务(面板级控制),
// 单任务维度展示进度、速度、完成/失败状态与失败原因分类。

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class DownloadDeck : UserControl
{
    private readonly DownloadService _downloads;

    private readonly ProgressBar _overallBar = UIKit.ProgressBar();
    // 2026-09-26 改单行:总进度卡三行文字任何宽度都不换行,卡片高度确定不飘
    private readonly TextBlock _overallText = UIKit.SubOneLine("暂无下载任务", 12, 640);
    private readonly TextBlock _speedText = UIKit.SubOneLine("", 12, 180);
    private readonly TextBlock _summaryChips = UIKit.SubOneLine("", 12, 640);
    // 2026-09-26 批1:切源留痕独立成行,不再与「总速度/剩余」共用同一 TextBlock(互相冲刷)
    private readonly TextBlock _auxText = UIKit.SubOneLine("", 12, 900);
    private readonly List<string> _sourceLog = new();   // 最近 3 条切源记录
    // 2026-09-26 空态文案(空态卡形状不变)
    private readonly TextBlock _emptyTitle;
    private readonly TextBlock _emptySub;
    private Button _goInstBtn = null!;
    private Button _goModsBtn = null!;
    private readonly StackPanel _taskList = new();
    private readonly Border _emptyHint;
    private readonly Action<ShellWindow.DeckKey, int?>? _navigate;   // 2026-09-26 空态快捷入口跳转(可带二级分区)
    private FrameworkElement? _overallCard;   // 2026-09-26 空状态隐藏总进度卡,消除重复空提示
    private readonly Button _pauseBtn;
    private readonly Button _retryBtn;

    // 拖拽调整下载优先级(BlockHelm-1):按下记录起点与目标任务,移动超阈值即发起拖放
    private DownloadTaskItem? _dragTask;
    private Point _dragStart;

    private readonly Dictionary<DownloadTaskItem, (TextBlock Status, TextBlock Pct, TextBlock Speed, TextBlock Eta, ProgressBar Bar, Border Card, Button Retry, Button PauseResume, Button Dir)> _rows = new();
    private DateTime _lastProgressUiAt = DateTime.MinValue;   // 进度 UI 节流时间戳(100ms)
    private bool _notifiedDone;   // 整批完成弹窗一次性标记(新任务加入时重置)

    public DownloadDeck(Action<ShellWindow.DeckKey, int?>? navigate = null)
    {
        _navigate = navigate;
        _downloads = App.Services.GetRequiredService<DownloadService>();
        // 2026-09-26 空态重做:图标+主标题+副标题,居中显示视觉饱满
        var glyph = UIKit.Text("\u21D3", 42);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "T.ForegroundDim");
        _emptyTitle = UIKit.Text("暂无下载任务", 16, FontWeights.Medium);
        _emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyTitle.Margin = new Thickness(0, 14, 0, 0);
        _emptySub = UIKit.Sub("安装版本或下载模组时会自动出现在这里", 12);
        _emptySub.HorizontalAlignment = HorizontalAlignment.Center;
        _emptySub.Margin = new Thickness(0, 8, 0, 0);
        // 2026-09-26 空态快捷入口:两个主按钮跳版本管理/模组管理,页面视觉饱满不空
        _goInstBtn = UIKit.Button("去版本管理下载", primary: true, onClick: () => _navigate?.Invoke(ShellWindow.DeckKey.Instances, 1), height: 36);   // 2026-09-26 批2:深链落到「新装版本」分区
        _goModsBtn = UIKit.Button("去模组管理浏览", primary: false, onClick: () => _navigate?.Invoke(ShellWindow.DeckKey.Mods, 1), height: 36);   // 2026-09-26 批2:深链落到「下载模组」分区
        _goModsBtn.Margin = new Thickness(10, 0, 0, 0);
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 0)
        };
        btnRow.Children.Add(_goInstBtn);
        btnRow.Children.Add(_goModsBtn);
        // 2026-09-26 空态卡 Stretch 撑满剩余空间:大面板覆盖像素背景,内容居中,视觉统一不裸露
        var emptyInner = UIKit.V(glyph, _emptyTitle, _emptySub, btnRow);
        emptyInner.VerticalAlignment = VerticalAlignment.Center;
        _emptyHint = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Panel),
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(28, 36, 28, 36),
            Margin = new Thickness(0, 8, 0, 4),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = emptyInner
        };
        _emptyHint.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        _emptyHint.SetResourceReference(Border.BackgroundProperty, "T.Overlay");
        _pauseBtn = Big(UIKit.Button("暂停", primary: false, onClick: TogglePause), 100);
        _retryBtn = Big(UIKit.Button("重试失败", primary: true, onClick: RetryFailed), 100);
        _retryBtn.IsEnabled = false;
        _retryBtn.Visibility = Visibility.Collapsed;   // 无失败任务时不显示,避免空页面突个蓝按钮
        _retryBtn.ToolTip = "把当前批次中失败/已取消的任务重新下载(断点残片会自动清理)";

        BuildUi();
        RebuildTaskList();

        _downloads.TaskStatusChanged += OnTaskChanged;
        _downloads.TaskCompleted += OnTaskDone;
        _downloads.TaskFailed += OnTaskDone;
        _downloads.ProgressChanged += OnSingleProgress;
        _downloads.OverallProgressChanged += OnOverall;
        _downloads.SourceSwitched += OnSourceSwitched;
        Unloaded += (_, _) =>
        {
            _downloads.TaskStatusChanged -= OnTaskChanged;
            _downloads.TaskCompleted -= OnTaskDone;
            _downloads.TaskFailed -= OnTaskDone;
            _downloads.ProgressChanged -= OnSingleProgress;
            _downloads.OverallProgressChanged -= OnOverall;
            _downloads.SourceSwitched -= OnSourceSwitched;
        };
    }

    /// <summary>页内按钮统一放大(字体/配色/点击逻辑不动)</summary>
    private static Button Big(Button b, double minWidth = 100)   // 2026-09-26 按钮缩小:高 42→34,宽 140→100
    {
        b.Height = 34;
        b.MinWidth = minWidth;
        return b;
    }

    private void BuildUi()
    {
        var cancelBtn = Big(UIKit.Button("取消全部", primary: false, onClick: () =>
        {
            if (DialogKit.Confirm("确定取消当前全部下载任务吗?", "取消下载", Window.GetWindow(this)))
            {
                _downloads.Cancel();
                Shell()?.SetStatus("已请求取消下载", warning: true);
            }
        }), 130);
        var dirBtn = Big(UIKit.Button("下载目录", primary: false, onClick: OpenRootDir), 100);

        // 清空队列(BlockHelm-1):移除当前批次全部任务(含已完成),二次确认后重建列表
        var clearBtn = Big(UIKit.Button("清空队列", primary: false, onClick: ClearQueue), 100);
        clearBtn.ToolTip = "移除当前下载队列里的全部任务(已完成的一并清除),不会删除已下载到磁盘的文件";

        // ---- 总进度卡:进度条置顶(前),总量/速度/任务统计在进度条后面(下) ----
        // 2026-09-26 重做:进度条撑满卡宽 + 加高 14(截图里进度条几乎看不见)
        _overallBar.Height = 14;
        _overallBar.HorizontalAlignment = HorizontalAlignment.Stretch;
        _overallBar.Margin = new Thickness(0, 6, 0, 8);
        _overallText.VerticalAlignment = VerticalAlignment.Center;
        _speedText.VerticalAlignment = VerticalAlignment.Center;
        _speedText.Margin = new Thickness(16, 0, 0, 0);
        _summaryChips.VerticalAlignment = VerticalAlignment.Center;
        _auxText.VerticalAlignment = VerticalAlignment.Center;
        _auxText.Visibility = Visibility.Collapsed;   // 2026-09-26 批1:无内容时不占位,卡片高度不变
        var summaryRow = UIKit.H(_overallText, _speedText);
        var chipsRow = UIKit.H(_summaryChips);
        var auxRow = UIKit.H(_auxText);

        // 2026-09-26 布局重构:Grid 双行 —— 行0 头部(页眉+进度卡)固定不滚动;
        // 行1 剩余空间:空状态空态提示垂直居中(上下均衡不再露大片背景),有任务时任务列表独立滚动
        var head = UIKit.V(
            UIKit.PageHeader("下载中心", "下载队列、总速度与失败重试;大文件断点续传,关闭窗口会缩到托盘继续下载(切源 / 测速在「设置 → 网络设置」)", _pauseBtn, _retryBtn, cancelBtn, clearBtn, dirBtn),   // 2026-09-26 精简:回到「进度卡 + 任务列表」旧样式
            // 2026-09-25 布局:进度条置顶(前),总量/速度/统计文字移到进度条后面(下)——进度可视化优先
            _overallCard = UIKit.Card(UIKit.V(_overallBar, summaryRow, chipsRow, auxRow), pad: 16, topGap: 10));   // 2026-09-26 收紧

        var taskScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _taskList
        };

        Content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            },
            Children =
            {
                head.Row(0),
                taskScroller.Row(1),
                _emptyHint.Row(1)
            }
        };
        chipsRow.Margin = new Thickness(0, 2, 0, 0);
    }

    // ==================== 任务列表 ====================

    private void RebuildTaskList()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RebuildTaskList); return; }
        _taskList.Children.Clear();
        _rows.Clear();
        var tasks = _downloads.CurrentBatchSnapshot().ToList();
        _emptyHint.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 2026-09-26 空状态隐藏总进度卡:无任务时进度卡里的「暂无下载任务」与 emptyHint 重复
        if (_overallCard != null)
            _overallCard.Visibility = tasks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var t in tasks)
            AddTaskRow(t);
        RefreshChips();
    }

    private void AddTaskRow(DownloadTaskItem t)
    {
        var name = UIKit.Text(System.IO.Path.GetFileName(t.LocalPath), 12.5, FontWeights.Medium);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.MaxWidth = 240;   // 2026-09-26:无宽度约束时 TextTrimming 不生效,长文件名撑爆整行
        name.ToolTip = t.LocalPath;

        var status = UIKit.Sub(StatusText(t), 11.5);
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.MaxWidth = 280;
        status.Margin = new Thickness(12, 0, 0, 0);
        // 2026-09-26 补全:失败/取消任务的错误详情悬停可见(不截断的关键信息)
        status.ToolTip = string.IsNullOrEmpty(t.Error) ? t.LocalPath : $"{System.IO.Path.GetFileName(t.LocalPath)}\n{t.Error}";
        // 2026-09-26 修正:Sub 默认开换行→超宽就把「MB」折到第二行(截图实锤);
        // 改 SubOneLine(不换行+省略号),百分比列加宽容纳「100% · 39.6 MB/39.6 MB」
        var pct = UIKit.SubOneLine("", 11.5);
        pct.Width = 180; pct.TextAlignment = TextAlignment.Right;
        pct.VerticalAlignment = VerticalAlignment.Center;
        pct.Margin = new Thickness(8, 0, 0, 0);

        // 2026-09-25 下载中心改进:单任务实时速度(仅下载中显示,其余状态留空)
        var speed = UIKit.SubOneLine("", 11.5);
        speed.Width = 92; speed.TextAlignment = TextAlignment.Right;
        speed.VerticalAlignment = VerticalAlignment.Center;
        speed.Margin = new Thickness(8, 0, 0, 0);

        // 2026-09-26 批1:单任务剩余时间(仅下载中显示,补齐文档 T16「进度条+速度+剩余时间」要求)
        var eta = UIKit.SubOneLine("", 11.5);
        eta.Width = 84; eta.TextAlignment = TextAlignment.Right;
        eta.VerticalAlignment = VerticalAlignment.Center;
        eta.Margin = new Thickness(8, 0, 0, 0);

        var bar = UIKit.ProgressBar(t.Size > 0 ? Math.Clamp((double)t.Downloaded / t.Size, 0, 1) * 100 : 0);
        bar.Height = 6;
        bar.Margin = new Thickness(0, 8, 0, 0);

        // 2026-09-26 布局重做:任务行改 Grid 单行——名称列弹性收缩(截断),其余列固定不换行,
        // 根治「100%·29.7 MB/29.7 MB」换行后 MB 孤行、按钮错位的乱排版
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.Children.Add(UIKit.Badge(CategoryText(t.Category), "T.Primary"));
        name.Margin = new Thickness(10, 0, 0, 0);
        topRow.Children.Add(name);
        Grid.SetColumn(name, 1);
        topRow.Children.Add(status);
        Grid.SetColumn(status, 2);
        topRow.Children.Add(speed);
        Grid.SetColumn(speed, 3);
        topRow.Children.Add(eta);
        Grid.SetColumn(eta, 4);
        topRow.Children.Add(pct);
        Grid.SetColumn(pct, 5);

        // 失败/已取消的任务行尾提供单任务重试入口
        var retryBtn = UIKit.Button("重试", primary: false, onClick: () => RetryOne(t));
        retryBtn.Height = 26;
        retryBtn.MinWidth = 50;
        retryBtn.Margin = new Thickness(10, 0, 0, 0);
        retryBtn.Visibility = IsRetryable(t) ? Visibility.Visible : Visibility.Collapsed;

        // 2026-09-26 补全:单任务暂停/继续(下载中=暂停,暂停=继续)——只停这一条,其余照跑
        var pauseResumeBtn = UIKit.Button(
            t.Status == DownloadStatus.Paused ? "继续" : "暂停",
            primary: false, onClick: () => PauseResumeOne(t));
        pauseResumeBtn.Height = 26;
        pauseResumeBtn.MinWidth = 50;
        pauseResumeBtn.Margin = new Thickness(8, 0, 0, 0);
        pauseResumeBtn.Visibility =
            (t.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Paused)
                ? Visibility.Visible : Visibility.Collapsed;
        pauseResumeBtn.ToolTip = "仅暂停/继续这一个下载任务,其余任务不受影响";

        // 2026-09-26 补全:打开文件所在目录(下载完/失败都能定位)
        var dirBtn = UIKit.Button("目录", primary: false, onClick: () => OpenTaskDir(t));
        dirBtn.Height = 26;
        dirBtn.MinWidth = 50;
        dirBtn.Margin = new Thickness(8, 0, 0, 0);
        dirBtn.ToolTip = System.IO.Path.GetDirectoryName(t.LocalPath);

        // 单任务移除(BlockHelm-1):下载中的任务会先撤单并清理残片,再从队列移除
        var removeBtn = UIKit.Button("移除", primary: false, onClick: () => RemoveOne(t));
        removeBtn.Height = 26;
        removeBtn.MinWidth = 50;
        removeBtn.Margin = new Thickness(8, 0, 0, 0);
        removeBtn.ToolTip = "从下载队列移除该任务(下载中的会先取消);已写入磁盘的文件不会被删除";

        // 按钮组固定右列(不换行);名称列弹性收缩兜底
        var btnGroup = UIKit.H(retryBtn, pauseResumeBtn, dirBtn, removeBtn);
        Grid.SetColumn(btnGroup, 6);
        topRow.Children.Add(btnGroup);

        var card = UIKit.Panel(UIKit.V(topRow, bar), pad: 14);
        card.WithRef("T.SurfaceAlt", "T.Border");
        card.BorderThickness = new Thickness(1);
        card.Margin = new Thickness(0, 0, 0, 8);
        ApplyStatusStyle(t, status, card);
        WireRowDrag(card, t);   // 拖拽调整下载优先级(BlockHelm-1)
        card.HoverLift(-1);    // 悬停轻微上浮:与入场动效共用同一 TranslateTransform,不冲突

        _rows[t] = (status, pct, speed, eta, bar, card, retryBtn, pauseResumeBtn, dirBtn);
        _taskList.Children.Add(card);
    }

    /// <summary>失败/已取消的任务可重试</summary>
    private static bool IsRetryable(DownloadTaskItem t)
        => t.Status is DownloadStatus.Failed or DownloadStatus.Cancelled;

    /// <summary>按任务状态刷新行内按钮(重试可见性 + 暂停/继续文案与可见性),状态变化即时生效(2026-09-26 批1)</summary>
    private static void RefreshRowActions(DownloadTaskItem t,
        (TextBlock Status, TextBlock Pct, TextBlock Speed, TextBlock Eta, ProgressBar Bar, Border Card, Button Retry, Button PauseResume, Button Dir) row)
    {
        row.Retry.Visibility = IsRetryable(t) ? Visibility.Visible : Visibility.Collapsed;
        UIKit.SetButtonText(row.PauseResume, t.Status == DownloadStatus.Paused ? "继续" : "暂停");
        row.PauseResume.Visibility =
            (t.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Paused)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>按任务状态着色:失败/取消转警示红,完成转成功绿,其它保持默认</summary>
    private static void ApplyStatusStyle(DownloadTaskItem t, TextBlock status, Border card)
    {
        if (t.Status is DownloadStatus.Failed or DownloadStatus.Cancelled)
        {
            status.SetResourceReference(TextBlock.ForegroundProperty, "T.Danger");
            card.SetResourceReference(Border.BorderBrushProperty, "T.Danger");
        }
        else if (t.Status == DownloadStatus.Completed)
        {
            status.SetResourceReference(TextBlock.ForegroundProperty, "T.Success");
        }
        else
        {
            status.ClearValue(TextBlock.ForegroundProperty);
            card.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        }
    }

    private void OnTaskChanged(DownloadTaskItem t)
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool added = false;
            if (!_rows.ContainsKey(t))
            {
                _notifiedDone = false;   // 新任务加入,完成弹窗重新武装
                _emptyHint.Visibility = Visibility.Collapsed;
                AddTaskRow(t);
                added = true;
            }
            if (_rows.TryGetValue(t, out var row))
            {
                row.Status.Text = StatusText(t);
                // 2026-09-26 批1(E3 修复):状态变化即时刷新行内按钮,暂停后按钮不再停留旧文案
                RefreshRowActions(t, row);
                // 2026-09-26 批1:非下载中状态清除单任务剩余时间,避免暂停/完成时残留旧 ETA
                if (t.Status != DownloadStatus.Downloading) row.Eta.Text = "";
                ApplyStatusStyle(t, row.Status, row.Card);
                // 2026-09-26 批1:新增单条任务入场动效(不整体重建列表,避免整页闪烁)
                if (added) MotionKit.Enter(row.Card, fromY: 8);
            }
            RefreshChips();
            UpdateSummary();
        });
    }

    /// <summary>单任务进度事件(携带任务引用):按 task 定位对应行刷新,多任务并发时每行进度都实时更新</summary>
    private void OnSingleProgress(DownloadTaskItem task, DownloadProgressInfo info)
    {
        // 2026-09-25 节流:下载事件洪泛(多任务并发每秒数十次)会占满 UI 线程。100ms 合并足够平滑,完成态由 OnTaskDone 兜底刷 100%
        if ((DateTime.Now - _lastProgressUiAt).TotalMilliseconds < 100) return;
        _lastProgressUiAt = DateTime.Now;
        Dispatcher.BeginInvoke(() =>
        {
            if (_rows.TryGetValue(task, out var row) && info.TotalBytes > 0)
            {
                // 2026-09-26 修复:已完成任务(OnTaskDone 已置 100%)后,后续进度事件不得再覆盖
                // —— 图1「已完成 + 63%/81%」就是完成后最后一条进度事件把 100% 盖回去
                if (task.Status == DownloadStatus.Completed)
                {
                    row.Bar.SmoothSet(100);
                    row.Pct.Text = "100%";
                    row.Speed.Text = "";
                    row.Eta.Text = "";
                    return;
                }
                row.Bar.SmoothSet(info.Progress * 100);
                // 2026-09-26 补全:百分比旁显示已下/总量字节
                row.Pct.Text = $"{info.Progress * 100:0}% · {UIKit.FmtBytes((long)info.DownloadedBytes)}/{UIKit.FmtBytes((long)info.TotalBytes)}";
                // 单任务速度:仅下载中显示,避免完成态残留旧速度
                row.Speed.Text = info.SpeedBytesPerSec > 0
                    ? $"{UIKit.FmtBytes((long)info.SpeedBytesPerSec)}/s"
                    : "";
                // 2026-09-26 批1:单任务剩余时间(仅下载中且有有效速度时显示)
                row.Eta.Text = info.SpeedBytesPerSec > 0 && info.EstimatedRemaining > TimeSpan.Zero
                    ? $"剩 {info.EstimatedRemaining:mm\\:ss}"
                    : "";
            }
        });
    }

    private void OnTaskDone(DownloadTaskItem t)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_rows.TryGetValue(t, out var row))
            {
                row.Status.Text = StatusText(t);
                if (t.Status == DownloadStatus.Completed) row.Bar.SmoothSet(100);   // 未完成保持当前进度,不回退
                row.Speed.Text = "";   // 任务结束清掉速度显示
                row.Eta.Text = "";     // 2026-09-26 批1:任务结束清掉剩余时间
                if (t.Status == DownloadStatus.Completed && t.Size > 0)
                    row.Pct.Text = $"100% · {UIKit.FmtBytes(t.Size)}/{UIKit.FmtBytes(t.Size)}";
                ApplyStatusStyle(t, row.Status, row.Card);
                // 2026-09-26 批1:统一走 RefreshRowActions,重试与暂停/继续按钮按状态同步
                RefreshRowActions(t, row);
            }
            RefreshChips();
            UpdateSummary();
            NotifyAllDoneIfNeeded();
        });
    }

    private void OnOverall(DownloadProgressInfo info)
    {
        // 与单任务共享 100ms 节流窗口,避免单帧内多次排队
        if ((DateTime.Now - _lastProgressUiAt).TotalMilliseconds < 100) return;
        _lastProgressUiAt = DateTime.Now;
        Dispatcher.BeginInvoke(() =>
        {
            _overallBar.SmoothSet(info.Progress * 100);
            _overallText.Text = $"总进度 {info.Progress * 100:0.0}%  ·  {UIKit.FmtBytes(info.DownloadedBytes)} / {UIKit.FmtBytes(info.TotalBytes)}";
            // 2026-09-26 修复:下完时显示「下载完成」,不再出现"100% + 剩余 48:05"矛盾
            _speedText.Text = info.Progress >= 0.999
                ? "下载完成"
                : (info.SpeedBytesPerSec > 0
                    ? $"速度 {UIKit.FmtBytes((long)info.SpeedBytesPerSec)}/s  ·  剩余 {info.EstimatedRemaining:mm\\:ss}"
                    : "");
        });
    }

    /// <summary>下载源切换提示:状态栏可见 + 卡片辅助行留痕最近 3 条,用户能追溯镜像自救过程(2026-09-26 批1)</summary>
    private void OnSourceSwitched(string file, string msg)
    {
        Shell()?.SetStatus($"下载 {file}:{msg}");
        Dispatcher.BeginInvoke(() =>
        {
            // 2026-09-26 批1:切源留痕(最近 3 条),解决「切源一闪而过、事后无处可查」
            _sourceLog.Add($"{DateTime.Now:HH:mm:ss} {System.IO.Path.GetFileName(file)} → {msg}");
            if (_sourceLog.Count > 3) _sourceLog.RemoveAt(0);
            SetAuxText("切源记录:" + string.Join("  |  ", _sourceLog));
        });
    }

    /// <summary>辅助行文本(测速结果 / 切源留痕):空字符串自动折叠不占位(2026-09-26 批1)</summary>
    private void SetAuxText(string text)
    {
        _auxText.Text = text;
        _auxText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>整批全部完成时弹一次性提示(2026-09-25 下载中心改进):
    /// 用户切到其它页/后台下载时也能第一时间知道,弹窗只出现一次,新任务加入后重新武装</summary>
    private void NotifyAllDoneIfNeeded()
    {
        var snap = _downloads.CurrentBatchSnapshot();
        if (snap.Count == 0) return;
        if (snap.All(x => x.Status == DownloadStatus.Completed))
        {
            if (_notifiedDone) return;
            _notifiedDone = true;
            // 2026-09-25:完成提示改右下角状态胶囊,不再弹窗——进度驻留右下角,避免每次下载完弹窗跳来跳去
            // 2026-09-26:附会话累计下载统计
            Shell()?.SetStatus($"全部 {snap.Count} 个下载任务已完成!本次会话共下载 {_downloads.SessionFileCount} 个文件 / " +
                               $"{UIKit.FmtBytes(_downloads.SessionDownloadedBytes)}。可前往「版本管理」启动游戏。", sticky: true);   // 2026-09-26 批2:完成播报驻留,切页不被冲刷
        }
        else if (snap.Any(x => x.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Verifying))
        {
            _notifiedDone = false;   // 还有在途任务,等全部完成再弹
        }
    }

    /// <summary>任务统计 chips:总数 / 下载中 / 已完成 / 失败 / 在途类别(实时刷新)</summary>
    private void RefreshChips()
    {
        var tasks = _downloads.CurrentBatchSnapshot();
        if (tasks.Count == 0) { _summaryChips.Text = ""; _retryBtn.IsEnabled = false; _retryBtn.Visibility = Visibility.Collapsed; return; }
        int downloading = tasks.Count(t => t.Status == DownloadStatus.Downloading);
        int done = tasks.Count(t => t.Status == DownloadStatus.Completed);
        int failed = tasks.Count(t => t.Status is DownloadStatus.Failed or DownloadStatus.Cancelled);
        // 2026-09-26 批1:在途任务的类别并发细分(游戏/资源/Java/模组各在跑多少),便于辨识并发分配
        var inFlight = tasks
            .Where(t => t.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Verifying)
            .GroupBy(t => t.Category)
            .Select(g => $"{CategoryText(g.Key)} {g.Count()}")
            .ToList();
        // 2026-09-27:断点续传可见化 —— 有多少任务复用了上次的断点(不再从头下)
        int resumed = tasks.Count(t => t.ShardTotal > 0 && t.DoneShards.Count > 0
                                       && t.Status != DownloadStatus.Completed);
        // 2026-09-26 补全:显示当前生效下载源,用户能看出正在用 BMCLAPI 还是官方
        _summaryChips.Text = $"当前源:{_downloads.CurrentSourceName}  ·  任务总数 {tasks.Count}  ·  下载中 {downloading}  ·  已完成 {done}" +
                             (inFlight.Count > 0 ? "  ·  在途 " + string.Join(" / ", inFlight) : "") +
                             (resumed > 0 ? $"  ·  断点续传 {resumed} 个(已复用已下分段)" : "") +
                             (failed > 0 ? $"  ·  失败 {failed}(点「重试失败」或任务行的「重试」补下)" : "");
        _summaryChips.SetResourceReference(TextBlock.ForegroundProperty, failed > 0 ? "T.Warning" : "T.ForegroundDim");
        _retryBtn.IsEnabled = failed > 0;
        _retryBtn.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSummary()
    {
        var tasks = _downloads.CurrentBatchSnapshot();
        if (tasks.Count == 0) return;
        int done = tasks.Count(t => t.Status == DownloadStatus.Completed);
        int failed = tasks.Count(t => t.Status is DownloadStatus.Failed or DownloadStatus.Cancelled);
        Shell()?.SetStatus($"下载任务 {done}/{tasks.Count}" + (failed > 0 ? $"(失败 {failed})" : ""), failed > 0);
    }

    // ==================== 操作 ====================

    /// <summary>整批重试:把全部失败/已取消任务重新入队</summary>
    private async void RetryFailed()
    {
        int failed = _downloads.CurrentBatchSnapshot().Count(t => t.Status is DownloadStatus.Failed or DownloadStatus.Cancelled);
        if (failed == 0) return;
        _retryBtn.IsEnabled = false;
        Shell()?.SetStatus($"正在重试 {failed} 个失败任务…");
        try
        {
            bool ok = await _downloads.RetryFailedAsync();
            Shell()?.SetStatus(ok ? "失败任务已全部补下成功" : "仍有任务失败,可继续重试或切换下载源", !ok);
        }
        catch (Exception ex)
        {
            Shell()?.SetStatus("重试异常:" + ex.Message, warning: true);
        }
        finally { RefreshChips(); }
    }

    /// <summary>单任务重试:仅重下该失败文件</summary>
    private async void RetryOne(DownloadTaskItem t)
    {
        if (!IsRetryable(t)) return;
        Shell()?.SetStatus($"正在重试 {System.IO.Path.GetFileName(t.LocalPath)}…");
        try
        {
            bool ok = await _downloads.RetryTaskAsync(t);
            Shell()?.SetStatus(ok
                ? $"{System.IO.Path.GetFileName(t.LocalPath)} 补下成功"
                : $"{System.IO.Path.GetFileName(t.LocalPath)} 仍失败:{t.Error}", !ok);
        }
        catch (Exception ex)
        {
            Shell()?.SetStatus("重试异常:" + ex.Message, warning: true);
        }
    }

    /// <summary>单任务暂停/继续切换(2026-09-26 补全)</summary>
    private void PauseResumeOne(DownloadTaskItem t)
    {
        try
        {
            if (t.Status == DownloadStatus.Paused)
            {
                _downloads.ResumeTask(t);
                Shell()?.SetStatus($"已继续下载 {System.IO.Path.GetFileName(t.LocalPath)}");
            }
            else if (t.Status is DownloadStatus.Downloading or DownloadStatus.Pending)
            {
                _downloads.PauseTask(t);
                Shell()?.SetStatus($"已暂停 {System.IO.Path.GetFileName(t.LocalPath)}(断点已保留)", warning: true);
            }
        }
        catch (Exception ex)
        {
            Shell()?.SetStatus("操作异常:" + ex.Message, warning: true);
        }
    }

    /// <summary>打开任务文件所在目录(2026-09-26 补全)</summary>
    private void OpenTaskDir(DownloadTaskItem t)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(t.LocalPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                Shell()?.SetStatus("文件目录不存在", warning: true);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Shell()?.SetStatus("打开目录失败:" + ex.Message, warning: true);
        }
    }

    /// <summary>单任务移除:下载中的先撤单清残片,再从队列移除并重建列表(BlockHelm-1)</summary>
    private void RemoveOne(DownloadTaskItem t)
    {
        // 2026-09-26 批1:在途任务移除属破坏性操作(会先撤单并清理残片),二次确认防误点
        if (t.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Verifying)
        {
            string name = System.IO.Path.GetFileName(t.LocalPath);
            if (!DialogKit.Confirm($"「{name}」正在下载,移除会先取消该任务并清理未完成的残片。\n已写入磁盘的文件不会被删除。",
                    "移除下载任务", "移除", danger: true, Window.GetWindow(this)))
                return;
        }
        if (_downloads.RemoveTask(t))
        {
            RebuildTaskList();
            Shell()?.SetStatus($"已从队列移除 {System.IO.Path.GetFileName(t.LocalPath)}", warning: true);
        }
        else
            Shell()?.SetStatus("移除失败:任务可能已结束", warning: true);
    }

    /// <summary>清空队列:移除当前批次全部任务(含已完成),二次确认(BlockHelm-1)</summary>
    private void ClearQueue()
    {
        var tasks = _downloads.CurrentBatchSnapshot();
        if (tasks.Count == 0) { Shell()?.SetStatus("队列为空,无需清理"); return; }
        if (!DialogKit.Confirm($"确定清空当前下载队列的全部 {tasks.Count} 个任务吗?\n已下载到磁盘的文件不会被删除。", "清空队列", Window.GetWindow(this)))
            return;
        int n = _downloads.ClearQueue();
        RebuildTaskList();
        Shell()?.SetStatus($"已清空队列({n} 个任务)", warning: true);
    }

    // ---- 拖拽调整下载优先级(BlockHelm-1) ----

    /// <summary>给任务卡挂上拖拽手势:按下记起点,移动超阈值发起拖放,放到别的卡上即重排</summary>
    private void WireRowDrag(Border card, DownloadTaskItem t)
    {
        card.AllowDrop = true;
        card.PreviewMouseLeftButtonDown += (_, e) => { _dragTask = t; _dragStart = e.GetPosition(null); };
        card.PreviewMouseMove += (_, e) =>
        {
            if (_dragTask == null || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            DragDrop.DoDragDrop(card, new DataObject(typeof(DownloadTaskItem), _dragTask), DragDropEffects.Move);
        };
        card.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(DownloadTaskItem)) is DownloadTaskItem src && !ReferenceEquals(src, t))
                Reorder(src, t);
            _dragTask = null;
            e.Handled = true;
        };
        card.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(typeof(DownloadTaskItem)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
    }

    /// <summary>把 src 移到 target 所在位置,重排后刷新列表并让下载引擎按新序调度(BlockHelm-1)</summary>
    private void Reorder(DownloadTaskItem src, DownloadTaskItem target)
    {
        var order = _downloads.CurrentBatchSnapshot();
        int from = order.IndexOf(src);
        int to = order.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        order.RemoveAt(from);
        order.Insert(to, src);
        _downloads.ApplyOrder(order);
        RebuildTaskList();
        _ = _downloads.ReprioritizeAsync();
        Shell()?.SetStatus($"已调整优先级:{System.IO.Path.GetFileName(src.LocalPath)} 移到 {System.IO.Path.GetFileName(target.LocalPath)} 处");
    }

    private void TogglePause()
    {
        if (_downloads.IsPaused)
        {
            _downloads.Resume();
            UIKit.SetButtonText(_pauseBtn, "暂停");
            Shell()?.SetStatus("下载已继续");
        }
        else
        {
            _downloads.Pause();
            UIKit.SetButtonText(_pauseBtn, "继续");
            Shell()?.SetStatus("下载已暂停", warning: true);
        }
    }

    /// <summary>打开数据根目录(2026-09-26:一键定位下载产物所在文件夹)</summary>
    private void OpenRootDir()
    {
        try
        {
            var dir = AppPaths.Root;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Shell()?.SetStatus("打开下载目录失败:" + ex.Message, warning: true);
        }
    }

    /// <summary>状态文案(2026-09-26 批1:失败行追加「重试 N/MaxRetry」,用户可知已自动重试几次)</summary>
    private string StatusText(DownloadTaskItem t) => t.Status switch
    {
        DownloadStatus.Pending => "等待中",
        DownloadStatus.Downloading => "下载中" + ResumeTag(t),
        DownloadStatus.Paused => "已暂停" + ResumeTag(t),
        DownloadStatus.Verifying => "校验中",
        DownloadStatus.Completed => t.Duration is { } d ? $"已完成 · 耗时 {d:mm\\:ss}" : "已完成",
        DownloadStatus.Failed => "失败" +
            (t.RetryCount > 0 ? $" · 重试 {t.RetryCount}/{_downloads.MaxRetry}" : "") +
            (string.IsNullOrEmpty(t.Error) ? "" : ":" + (t.Error.Length > 36 ? t.Error[..36] + "…" : t.Error)),
        DownloadStatus.Cancelled => "已取消",
        _ => t.Status.ToString()
    };

    /// <summary>断点续传标记(2026-09-27):分片任务已有完成段位时显示,让「接着下、不重来」看得见</summary>
    private static string ResumeTag(DownloadTaskItem t)
        => t.ShardTotal > 0 && t.DoneShards.Count > 0
            ? $" · 续传 {t.DoneShards.Count}/{t.ShardTotal} 段"
            : "";

    private static string CategoryText(DownloadCategory c) => c switch
    {
        DownloadCategory.Game => "游戏",
        DownloadCategory.Asset => "资源",
        DownloadCategory.Java => "Java",
        DownloadCategory.Mod => "模组",
        _ => "其他"
    };

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
