// Copyright © FufuLauncher
//
// 环境完整性校验面板(BlockHelm-3):
// 启动游戏之前自动校验核心 jar、库文件、资源文件是否完好;发现损坏或缺失就自动补下载修复,
// 并弹窗告诉用户哪些项目被修复了。这一段已经挂在 GameLaunchService 的启动流程里(步骤 8.5),
// 本面板提供:开关配置(要不要校验 / 深度校验 / 自动修复)、手动立即校验、校验结果明细。
// 顺带承载后台预加载依赖开关(Axolotl-6)。控件全部走 UIKit 既有工厂,配色 T.* 令牌。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class IntegrityPanel : UserControl
{
    private readonly InstanceService _instances;
    private readonly IntegrityRepairService _integrity;
    private readonly ConfigService _config;
    private readonly DependencyPreloadService _preload;

    private readonly ComboBox _instBox = UIKit.ComboBox();
    private readonly CheckBox _verifySw;
    private readonly CheckBox _deepSw;
    private readonly CheckBox _repairSw;
    private readonly CheckBox _preloadSw;
    private readonly TextBlock _status = UIKit.Sub("", 12);
    private readonly ProgressBar _bar = UIKit.ProgressBar();
    private readonly StackPanel _list = new();
    private readonly Button _runBtn;
    private bool _busy;

    public IntegrityPanel()
    {
        _instances = App.Services.GetRequiredService<InstanceService>();
        _integrity = App.Services.GetRequiredService<IntegrityRepairService>();
        _config = App.Services.GetRequiredService<ConfigService>();
        _preload = App.Services.GetRequiredService<DependencyPreloadService>();

        var cfg = _config.Config;
        _verifySw = new UIKit.ToggleSwitch("启动前自动校验", cfg.VerifyBeforeLaunch);
        _deepSw = new UIKit.ToggleSwitch("深度校验(逐个算 SHA1)", cfg.DeepVerifyBeforeLaunch);
        _repairSw = new UIKit.ToggleSwitch("发现问题自动补下载修复", cfg.AutoRepairBeforeLaunch);
        _preloadSw = new UIKit.ToggleSwitch("后台静默预加载依赖", cfg.PreloadDependencies);
        foreach (var sw in new[] { _verifySw, _deepSw, _repairSw, _preloadSw })
            sw.VerticalAlignment = VerticalAlignment.Center;
        _verifySw.Checked += (_, _) => SaveCfg();
        _verifySw.Unchecked += (_, _) => SaveCfg();
        _deepSw.Checked += (_, _) => SaveCfg();
        _deepSw.Unchecked += (_, _) => SaveCfg();
        _repairSw.Checked += (_, _) => SaveCfg();
        _repairSw.Unchecked += (_, _) => SaveCfg();
        _preloadSw.Checked += (_, _) =>
        {
            SaveCfg();
            _preload.Enabled = true;
            Shell()?.SetStatus("后台预加载已开启");
        };
        _preloadSw.Unchecked += (_, _) =>
        {
            SaveCfg();
            _preload.Enabled = false;
            Shell()?.SetStatus("后台预加载已关闭", warning: true);
        };

        _instBox.MinWidth = 260;
        _instBox.VerticalAlignment = VerticalAlignment.Center;
        _runBtn = PanelKit.Btn("立即校验并修复", true, () => _ = RunAsync(), 148);
        _status.TextWrapping = TextWrapping.Wrap;
        _bar.Margin = new Thickness(0, 10, 0, 4);

        Content = UIKit.V(
            UIKit.Card(UIKit.V(
                UIKit.SettingRow("启动前校验", "每次启动游戏前先查一遍文件在不在、有没有坏,坏了自动补", _verifySw),
                UIKit.SettingRow("深度校验", "逐个文件算 SHA1 比对,更准但明显更慢;关掉只查文件是否存在", _deepSw),
                UIKit.SettingRow("自动修复", "查到损坏或缺失时直接补下载,修好了弹窗告诉你修了哪些", _repairSw),
                UIKit.SettingRowNoDivider("后台预加载", "新建版本或安装 Fabric / Forge 时,依赖在后台静默下载,界面不卡不阻塞", _preloadSw)),
                pad: 16, topGap: 12),

            UIKit.Card(UIKit.V(
                UIKit.Text("手动校验", 14, FontWeights.Medium),
                UIKit.Sub("不想等启动时才校验,可以在这里立刻查一遍并修复", 11.5).MarginTop(4),
                UIKit.SettingRow("游戏版本", "要校验哪个版本,就在这里选", _instBox),
                UIKit.SettingRowNoDivider("执行", "校验过程会走下载引擎,可在「下载中心」看到任务", UIKit.H(_runBtn)),
                _bar,
                new Border { Padding = new Thickness(2, 4, 2, 8), Child = _status },
                _list),
                pad: 16, topGap: 10));
    }

    public void Enter(string? instanceId)
    {
        RefreshInstBox(instanceId);
        _preload.Enabled = _config.Config.PreloadDependencies;
    }

    private void RefreshInstBox(string? wantId)
    {
        _instances.RefreshInstances();
        string prev = (_instBox.SelectedItem as GameInstance)?.Id ?? "";
        _instBox.Items.Clear();
        foreach (var inst in _instances.Instances) _instBox.Items.Add(inst);
        if (_instances.Instances.Count == 0)
        {
            _instBox.Items.Add("(暂无本地版本)");
            _instBox.SelectedIndex = 0;
            _instBox.IsEnabled = false;
            _runBtn.IsEnabled = false;
            return;
        }
        _instBox.IsEnabled = true;
        _runBtn.IsEnabled = !_busy;
        string target = string.IsNullOrEmpty(wantId) ? prev : wantId;
        int idx = _instances.Instances.FindIndex(i => i.Id == target);
        _instBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void SaveCfg()
    {
        var cfg = _config.Config;
        cfg.VerifyBeforeLaunch = _verifySw.IsChecked == true;
        cfg.DeepVerifyBeforeLaunch = _deepSw.IsChecked == true;
        cfg.AutoRepairBeforeLaunch = _repairSw.IsChecked == true;
        cfg.PreloadDependencies = _preloadSw.IsChecked == true;
        _config.Save();
    }

    private async Task RunAsync()
    {
        if (_busy) return;
        if (_instBox.SelectedItem is not GameInstance inst)
        {
            DialogKit.Info("请先安装并选择一个游戏版本", owner: Window.GetWindow(this));
            return;
        }
        bool autoRepair = _repairSw.IsChecked == true;
        if (autoRepair && !DialogKit.Confirm(
                $"要校验「{inst.Name}」并自动修复吗?\n损坏或缺失的文件会重新下载,已下载的部分会走断点续传。",
                "环境校验", "开始校验", danger: false, owner: Window.GetWindow(this))) return;

        _busy = true;
        _runBtn.IsEnabled = false;
        _bar.ResetProgress(10);
        _list.Children.Clear();
        _status.Text = autoRepair ? "正在校验并自动修复…" : "正在校验(只查不修)…";
        try
        {
            var mode = _deepSw.IsChecked == true ? IntegrityCheckMode.Deep : IntegrityCheckMode.Quick;
            _bar.SmoothSet(35);
            var report = await _integrity.CheckAndRepairAsync(inst.Id, mode, autoRepair);
            _bar.SmoothSet(100);
            Render(report, autoRepair);
        }
        catch (Exception ex)
        {
            _status.Text = "校验失败:" + ex.Message;
            _status.SetResourceReference(TextBlock.ForegroundProperty, "T.Danger");
            App.WriteAppLog($"[校验] 异常:{ex}");
        }
        finally
        {
            _busy = false;
            _runBtn.IsEnabled = true;
        }
    }

    private void Render(IntegrityReport report, bool autoRepair)
    {
        string tone = report.Ok ? "T.Success" : (report.NeedFullInstall || report.Failed.Count > 0 ? "T.Danger" : "T.Warning");
        _status.Text = report.Headline + $"\n检查了 {report.CheckedCount} 个文件 · 耗时 {report.Elapsed.TotalSeconds:0.0} 秒"
                       + (autoRepair ? "" : "\n(当前是只查不修模式,发现的文件没有被修复)");
        _status.SetResourceReference(TextBlock.ForegroundProperty, tone);
        Shell()?.SetStatus(report.Ok ? $"「{report.InstanceName}」环境完好" : $"「{report.InstanceName}」发现 {report.Problems.Count} 个问题", !report.Ok);

        _list.Children.Clear();
        if (report.Ok)
        {
            var okHint = UIKit.Sub("核心 jar、库文件与资源文件都在,没有发现损坏或缺失,可以放心启动", 11.5);
            okHint.TextWrapping = TextWrapping.Wrap;
            _list.Children.Add(PanelKit.Row(okHint, pad: 12));
            return;
        }
        if (report.NeedFullInstall)
        {
            var warn = UIKit.Text("游戏本体缺得太厉害,建议直接重装这个版本", 12.5, FontWeights.SemiBold);
            warn.TextWrapping = TextWrapping.Wrap;
            warn.SetResourceReference(TextBlock.ForegroundProperty, "T.Danger");
            _list.Children.Add(PanelKit.Row(warn, pad: 12));
        }

        foreach (var item in report.Repaired.Take(60))
            _list.Children.Add(MakeItemRow(item, "已修复", "T.Success"));
        foreach (var item in report.Failed.Take(60))
            _list.Children.Add(MakeItemRow(item, "修复失败", "T.Danger"));
        foreach (var item in report.Problems.Where(p => !p.Fixed).Take(60))
            _list.Children.Add(MakeItemRow(item, autoRepair ? "未修复" : "待修复", "T.Warning"));

        int total = report.Repaired.Count + report.Failed.Count + report.Problems.Count(p => !p.Fixed);
        if (total > 180)
            _list.Children.Add(UIKit.Sub($"…另有 {total - 180} 条未列出,完整清单见启动器日志", 11));
    }

    private static UIElement MakeItemRow(RepairItem item, string state, string tone)
    {
        var badge = UIKit.Badge(state, tone);
        badge.Margin = new Thickness(0, 0, 10, 0);
        var kind = UIKit.Badge(item.KindDisplay, "T.ForegroundDim");
        var name = UIKit.Text(item.FileName, 12.5, FontWeights.Medium);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;
        var head = UIKit.H(badge, kind, name);

        var sub = UIKit.Sub($"{item.Reason} · {StorageGuardService.FmtSize(item.Size)} · {item.LocalPath}", 10.5);
        sub.TextWrapping = TextWrapping.Wrap;
        sub.ToolTip = item.LocalPath;
        sub.Margin = new Thickness(0, 4, 0, 0);

        var row = PanelKit.Row(UIKit.V(head, sub), pad: 10);
        PanelKit.Tint(row, tone);
        return row;
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
