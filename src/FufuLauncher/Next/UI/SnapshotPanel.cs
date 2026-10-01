// Copyright © FufuLauncher
//
// 配置快照面板(Axolotl-1):同一个游戏版本可以存多套配置快照。
// 每套快照里存:模组启用状态(哪些被禁用)、JVM 附加参数、内存(Xms/Xmx 与是否手动指定)、
// Java 路径与主版本、游戏窗口宽高与全屏开关。
// 一键套用即可整套切换,不用手动改参数或反复增删模组。
// 控件全部走 UIKit 既有工厂,配色 T.* 令牌,与原有页面同一套观感。

using System.Windows;
using System.Windows.Controls;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class SnapshotPanel : UserControl
{
    private readonly InstanceService _instances;
    private readonly ConfigSnapshotService _snapshots;
    private readonly InstanceExtrasService _extras;

    private readonly ComboBox _instBox = UIKit.ComboBox();
    private readonly TextBlock _badge = UIKit.Sub("", 11.5);
    private readonly StackPanel _list = new();
    private readonly Border _empty;
    private readonly TextBlock _count = UIKit.Sub("", 12);

    public SnapshotPanel()
    {
        _instances = App.Services.GetRequiredService<InstanceService>();
        _snapshots = App.Services.GetRequiredService<ConfigSnapshotService>();
        _extras = App.Services.GetRequiredService<InstanceExtrasService>();
        _empty = UIKit.EmptyHint("这个版本还没有配置快照,点「保存当前配置」先存一套");

        _instBox.MinWidth = 260;
        _instBox.VerticalAlignment = VerticalAlignment.Center;
        _instBox.SelectionChanged += (_, _) => Render();
        _badge.VerticalAlignment = VerticalAlignment.Center;
        _badge.Margin = new Thickness(12, 0, 0, 0);
        _count.VerticalAlignment = VerticalAlignment.Center;

        var captureBtn = PanelKit.Btn("保存当前配置", true, Capture, 132);
        var refreshBtn = PanelKit.Btn("刷新", false, () => Enter(Current?.Id), 88);
        var head = UIKit.H(captureBtn, refreshBtn);
        head.VerticalAlignment = VerticalAlignment.Center;

        Content = UIKit.V(
            UIKit.Card(UIKit.V(
                UIKit.SettingRow("游戏版本", "要给哪个版本存快照、切快照,就在这里选", UIKit.H(_instBox, _badge)),
                UIKit.SettingRowNoDivider("快照操作", "保存 = 把当前这套配置拍照存档;套用 = 整套换回去(含模组启用状态)", head)),
                pad: 16, topGap: 12),
            new Border { Padding = new Thickness(2, 14, 0, 4), Child = UIKit.H(UIKit.Text("已保存的快照", 14, FontWeights.Medium), _count) },
            _empty,
            _list);
    }

    private GameInstance? Current => _instBox.SelectedItem as GameInstance;

    /// <summary>进入面板:重读版本列表并预选指定版本</summary>
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
            _count.Text = "";
            return;
        }
        _badge.Text = $"MC {inst.VersionId} · {(string.IsNullOrEmpty(inst.ModLoader) ? "原版" : $"{inst.ModLoader} {inst.ModLoaderVersion}")}";

        var list = _snapshots.List(inst.Id);
        _empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _count.Text = list.Count == 0 ? "" : $"共 {list.Count} 套(上限 {InstanceExtrasService.MaxSnapshots} 套,超出时最旧的自动淘汰)";
        foreach (var snap in list.OrderByDescending(s => s.CreatedAt))
            _list.Children.Add(MakeRow(inst, snap));
    }

    private UIElement MakeRow(GameInstance inst, InstanceConfigSnapshot snap)
    {
        var name = UIKit.Text(snap.Name, 13.5, FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var when = UIKit.Sub(snap.CreatedAt.ToString("yyyy-MM-dd HH:mm"), 11);
        when.VerticalAlignment = VerticalAlignment.Center;
        when.Margin = new Thickness(10, 0, 0, 0);
        var head = UIKit.H(name, when);

        var summary = UIKit.Sub(snap.Summary, 11.5);
        summary.TextWrapping = TextWrapping.Wrap;
        summary.Margin = new Thickness(0, 5, 0, 0);

        var applyBtn = PanelKit.Btn("一键套用", true, () => Apply(inst, snap), 96);
        var updateBtn = PanelKit.Btn("覆盖更新", false, () => Update(inst, snap), 96);
        updateBtn.ToolTip = "用这个版本现在的配置覆盖掉这套快照";
        var renameBtn = PanelKit.Btn("重命名", false, () => Rename(inst, snap), 88);
        var noteBtn = PanelKit.Btn("改备注", false, () => EditNote(inst, snap), 88);
        var delBtn = PanelKit.Btn("删除", false, () => Delete(inst, snap), 80);
        var actions = UIKit.H(applyBtn, updateBtn, renameBtn, noteBtn, delBtn);
        actions.Margin = new Thickness(0, 10, 0, 0);

        var col = UIKit.V(head, summary);
        if (!string.IsNullOrWhiteSpace(snap.Note))
        {
            var note = UIKit.Sub("备注:" + snap.Note, 11);
            note.TextWrapping = TextWrapping.Wrap;
            note.SetResourceReference(TextBlock.ForegroundProperty, "T.Primary");
            note.Margin = new Thickness(0, 4, 0, 0);
            col.Children.Add(note);
        }
        col.Children.Add(actions);

        return PanelKit.Row(col, pad: 14);
    }

    // ==================== 操作 ====================

    private void Capture()
    {
        var inst = Current;
        if (inst == null) { DialogKit.Info("请先安装并选择一个游戏版本", owner: Window.GetWindow(this)); return; }
        string? name = DialogKit.Input("给这套快照起个名字:", "保存配置快照",
            $"{inst.Name} {DateTime.Now:MM-dd HHmm}", Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name)) return;
        string note = DialogKit.Input("备注(可留空,方便以后认出来这是哪套配置):", "快照备注", "", Window.GetWindow(this)) ?? "";

        var snap = _snapshots.Capture(inst.Id, name.Trim(), note.Trim());
        if (snap == null)
        {
            DialogKit.Error("快照保存失败,请查看日志", "保存配置快照", Window.GetWindow(this));
            return;
        }
        App.WriteAppLog($"[快照] 保存:{inst.Name} → {snap.Name}({snap.Summary})");
        Shell()?.SetStatus($"已保存快照「{snap.Name}」");
        Render();
    }

    private void Apply(GameInstance inst, InstanceConfigSnapshot snap)
    {
        if (!DialogKit.Confirm(
                $"把「{inst.Name}」的配置整套换成快照「{snap.Name}」?\n\n{snap.Summary}\n\n模组启用状态、JVM 参数、内存与窗口设置都会被覆盖。",
                "套用快照", "套用", danger: true, owner: Window.GetWindow(this))) return;

        SnapshotApplyResult res;
        try { res = _snapshots.Apply(inst.Id, snap.Id); }
        catch (Exception ex)
        {
            App.WriteAppLog($"[快照] 套用异常:{ex}");
            DialogKit.Error("套用异常:" + ex.Message, "套用快照", Window.GetWindow(this));
            return;
        }

        if (!res.Ok)
        {
            DialogKit.Error(res.Message, "套用快照", Window.GetWindow(this));
            return;
        }
        string fail = res.Failures.Count == 0 ? "" :
            $"\n\n有 {res.Failures.Count} 个模组没能切换状态(文件可能被占用):\n{string.Join("\n", res.Failures.Take(6))}";
        App.WriteAppLog($"[快照] 套用:{inst.Name} ← {snap.Name},切换 {res.ToggledMods} 个模组");
        DialogKit.Success($"{res.Message}{fail}", "套用快照", Window.GetWindow(this));
        Shell()?.SetStatus($"已套用快照「{snap.Name}」");
        Render();
    }

    private void Update(GameInstance inst, InstanceConfigSnapshot snap)
    {
        if (!DialogKit.Confirm($"用「{inst.Name}」现在的配置覆盖快照「{snap.Name}」?\n原来的内容会被替换,不可恢复。",
                "覆盖更新快照", "覆盖", danger: true, owner: Window.GetWindow(this))) return;
        var updated = _snapshots.Update(inst.Id, snap.Id);
        if (updated == null) { DialogKit.Error("更新失败,请查看日志", "覆盖更新快照", Window.GetWindow(this)); return; }
        App.WriteAppLog($"[快照] 覆盖更新:{inst.Name} → {updated.Name}");
        Shell()?.SetStatus($"快照「{updated.Name}」已更新为当前配置");
        Render();
    }

    private void Rename(GameInstance inst, InstanceConfigSnapshot snap)
    {
        string? name = DialogKit.Input("新的快照名称:", "重命名快照", snap.Name, Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!_snapshots.Rename(inst.Id, snap.Id, name.Trim()))
        {
            DialogKit.Error("重命名失败", "重命名快照", Window.GetWindow(this));
            return;
        }
        Render();
    }

    private void EditNote(GameInstance inst, InstanceConfigSnapshot snap)
    {
        string? note = DialogKit.Input("快照备注:", "修改备注", snap.Note, Window.GetWindow(this));
        if (note == null) return;
        snap.Note = note.Trim();
        _extras.NotifyChanged();
        _extras.Save();
        Render();
    }

    private void Delete(GameInstance inst, InstanceConfigSnapshot snap)
    {
        if (!DialogKit.Confirm($"删除快照「{snap.Name}」?\n只删快照记录,版本当前的配置不受影响。",
                "删除快照", Window.GetWindow(this))) return;
        _snapshots.Delete(inst.Id, snap.Id);
        Shell()?.SetStatus("快照已删除");
        Render();
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
