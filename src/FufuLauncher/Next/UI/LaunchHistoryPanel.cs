// Copyright © FufuLauncher
//
// 启动会话记录面板(BlockHelm-6):
// 每一次启动游戏都留一条记录 —— 启动时间、是否成功、玩了多久、退出码、崩溃简单原因、
// 启动前自动修复了几个文件;在这里按版本翻看历史,失败的一眼看出是哪次、为什么。
// 记录由 GameLaunchService 在启动流程里落地(进程拉起后先写一条,退出时回填结果),
// 上限 InstanceExtrasService.MaxLaunchHistory 条,超出自动淘汰最旧的。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class LaunchHistoryPanel : UserControl
{
    private readonly InstanceService _instances;
    private readonly InstanceExtrasService _extras;

    private readonly ComboBox _instBox = UIKit.ComboBox();
    private readonly TextBlock _badge = UIKit.Sub("", 11.5);
    private readonly TextBlock _summary = UIKit.Sub("", 12);
    private readonly StackPanel _list = new();
    private readonly Border _empty;

    public LaunchHistoryPanel()
    {
        _instances = App.Services.GetRequiredService<InstanceService>();
        _extras = App.Services.GetRequiredService<InstanceExtrasService>();
        _empty = UIKit.EmptyHint("这个版本还没有启动记录,启动一次游戏之后就会出现在这里");

        _instBox.MinWidth = 260;
        _instBox.VerticalAlignment = VerticalAlignment.Center;
        _instBox.SelectionChanged += (_, _) => Render();
        _badge.VerticalAlignment = VerticalAlignment.Center;
        _badge.Margin = new Thickness(12, 0, 0, 0);
        _summary.TextWrapping = TextWrapping.Wrap;

        var refreshBtn = PanelKit.Btn("刷新", false, () => Enter(Current?.Id), 88);
        var clearBtn = PanelKit.Btn("清空记录", false, ClearAll, 104);

        Content = UIKit.V(
            UIKit.Card(UIKit.V(
                UIKit.SettingRow("游戏版本", "看哪个版本的启动历史,就在这里选", UIKit.H(_instBox, _badge)),
                UIKit.SettingRowNoDivider("记录操作", "记录只存在本地,不会上传;清空只删记录,不影响版本本身",
                    UIKit.H(refreshBtn, clearBtn))),
                pad: 16, topGap: 12),
            new Border { Padding = new Thickness(2, 12, 0, 4), Child = _summary },
            _empty,
            _list);
    }

    private GameInstance? Current => _instBox.SelectedItem as GameInstance;

    public void Enter(string? instanceId)
    {
        RefreshInstBox(instanceId);
        Render();
    }

    private void RefreshInstBox(string? wantId)
    {
        _instances.RefreshInstances();
        string prev = Current?.Id ?? "";
        _instBox.Items.Clear();
        foreach (var inst in _instances.Instances) _instBox.Items.Add(inst);
        if (_instances.Instances.Count == 0)
        {
            _instBox.Items.Add("(暂无本地版本)");
            _instBox.SelectedIndex = 0;
            _instBox.IsEnabled = false;
            _badge.Text = "";
            return;
        }
        _instBox.IsEnabled = true;
        string target = string.IsNullOrEmpty(wantId) ? prev : wantId;
        int idx = _instances.Instances.FindIndex(i => i.Id == target);
        _instBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void Render()
    {
        _list.Children.Clear();
        var inst = Current;
        if (inst == null)
        {
            _empty.Visibility = Visibility.Visible;
            _badge.Text = "";
            _summary.Text = "";
            return;
        }
        _badge.Text = $"MC {inst.VersionId} · {(string.IsNullOrEmpty(inst.ModLoader) ? "原版" : $"{inst.ModLoader} {inst.ModLoaderVersion}")}";

        var records = _extras.LaunchHistoryOf(inst.Id);
        _empty.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (records.Count == 0) { _summary.Text = ""; return; }

        int ok = records.Count(r => r.Success);
        int fail = records.Count - ok;
        long totalSecs = records.Sum(r => (long)r.DurationSeconds);
        var last = records.OrderByDescending(r => r.StartTime).First();
        _summary.Text = $"共 {records.Count} 条 · 成功 {ok} 次 · 失败 {fail} 次 · 累计游玩 {FmtDuration(totalSecs)}"
                        + $"\n最近一次:{last.StartTime:yyyy-MM-dd HH:mm} · {(last.Success ? "启动成功" : "启动失败")}";
        _summary.SetResourceReference(TextBlock.ForegroundProperty, fail > 0 ? "T.Warning" : "T.ForegroundDim");

        foreach (var r in records.OrderByDescending(x => x.StartTime))
            _list.Children.Add(MakeRow(r));
    }

    private UIElement MakeRow(LaunchSessionRecord r)
    {
        var stateBadge = UIKit.Badge(r.Success ? "成功" : "失败", r.Success ? "T.Success" : "T.Danger");
        stateBadge.Margin = new Thickness(0, 0, 10, 0);
        var when = UIKit.Text(r.StartTime.ToString("yyyy-MM-dd HH:mm:ss"), 12.5, FontWeights.Medium);
        when.VerticalAlignment = VerticalAlignment.Center;
        var head = UIKit.H(stateBadge, when);

        string meta = $"环境 {r.VersionId}"
                      + (string.IsNullOrEmpty(r.Loader) ? "" : $" · {r.Loader}")
                      + (r.DurationSeconds > 0 ? $" · 时长 {FmtDuration(r.DurationSeconds)}" : "")
                      + (r.Success ? "" : $" · 退出码 {r.ExitCode}")
                      + (r.RepairedFiles > 0 ? $" · 启动前自动修复 {r.RepairedFiles} 个文件" : "");
        var metaLine = UIKit.Sub(meta, 11);
        metaLine.TextWrapping = TextWrapping.Wrap;
        metaLine.Margin = new Thickness(0, 4, 0, 0);

        var col = UIKit.V(head, metaLine);
        if (!string.IsNullOrWhiteSpace(r.Reason))
        {
            var reason = UIKit.Sub("原因:" + r.Reason, 11.5);
            reason.TextWrapping = TextWrapping.Wrap;
            reason.SetResourceReference(TextBlock.ForegroundProperty, "T.Danger");
            reason.Margin = new Thickness(0, 4, 0, 0);
            col.Children.Add(reason);
        }

        var row = PanelKit.Row(col, pad: 12);
        if (!r.Success) PanelKit.Tint(row, "T.Danger");
        return row;
    }

    private void ClearAll()
    {
        var inst = Current;
        if (inst == null) return;
        if (_extras.LaunchHistoryOf(inst.Id).Count == 0)
        {
            DialogKit.Info("这个版本还没有启动记录", owner: Window.GetWindow(this));
            return;
        }
        if (!DialogKit.Confirm($"清空「{inst.Name}」的全部启动记录?\n记录清空后无法找回。",
                "清空启动记录", "清空", danger: true, owner: Window.GetWindow(this))) return;
        _extras.ClearLaunchHistory(inst.Id);
        Shell()?.SetStatus("启动记录已清空");
        Render();
    }

    private static string FmtDuration(long secs)
    {
        if (secs <= 0) return "0 秒";
        if (secs < 60) return $"{secs} 秒";
        if (secs < 3600) return $"{secs / 60} 分钟";
        long h = secs / 3600, m = (secs % 3600) / 60;
        return m > 0 ? $"{h} 小时 {m} 分" : $"{h} 小时";
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
