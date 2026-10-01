// Copyright © FufuLauncher
//
// 版本工具面板(版本管理页第四分区):把新增的版本级维护功能收在一处,
// 五个页签叠放切换(与「版本设置」内的标签页同款交互,不新增导航层级):
//   ① 配置快照(Axolotl-1) ② 日志与崩溃(Axolotl-4 / Celestial-4)
//   ③ 启动记录(BlockHelm-6) ④ 脚本与磁盘(Celestial-7 / Celestial-5)
//   ⑤ 环境校验(BlockHelm-3 / Axolotl-6)
// 页内所有控件沿用 UIKit 既有工厂与 T.* 主题令牌,视觉与原有页面完全一致。

using System.Windows;
using System.Windows.Controls;
using FufuLauncher.Services;

namespace FufuLauncher.Next.UI;

public sealed class InstanceToolsPanel : UserControl
{
    private static readonly string[] TabNames = { "配置快照", "日志与崩溃", "启动记录", "脚本与磁盘", "环境校验", "资源与存档" };

    private readonly SnapshotPanel _snapshot = new();
    private readonly LogFilterPanel _log = new();
    private readonly LaunchHistoryPanel _history = new();
    private readonly ScriptDiskPanel _scriptDisk = new();
    private readonly IntegrityPanel _integrity = new();
    private readonly InstanceAssetsPanel _assets = new();

    private readonly Border[] _chips = new Border[TabNames.Length];
    private UIElement[] _pages = Array.Empty<UIElement>();
    private int _curTab;

    public InstanceToolsPanel()
    {
        var header = UIKit.PageHeader("版本工具",
            "配置快照、日志筛选、启动记录、前后置脚本、磁盘占用、环境校验与资源存档,都按版本单独设置");

        var tabBar = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < TabNames.Length; i++)
        {
            int idx = i;   // 闭包捕获独立副本
            var (chip, _) = PanelKit.TabChip(TabNames[i]);
            chip.MouseLeftButtonUp += (_, _) => SelectTab(idx);
            _chips[i] = chip;
            tabBar.Children.Add(chip);
        }
        tabBar.Margin = new Thickness(0, 14, 0, 0);

        _pages = new UIElement[] { _snapshot, _log, _history, _scriptDisk, _integrity, _assets };
        var host = new Grid();
        foreach (var p in _pages) host.Children.Add(p);

        Content = UIKit.V(header, tabBar, host);
        SelectTab(0);
    }

    /// <summary>进入本分区:切到指定页签并把版本预选带过去</summary>
    public void Enter(int tab, string? instanceId)
    {
        SelectTab(Math.Clamp(tab, 0, _pages.Length - 1), instanceId);
    }

    private void SelectTab(int idx, string? instanceId = null)
    {
        _curTab = idx;
        MotionKit.SwapOverlay(_pages, idx);
        for (int i = 0; i < _chips.Length; i++) PanelKit.PaintChip(_chips[i], i == idx);

        // 每个页签自己负责重读版本列表;没带版本时保持各页签上次选中
        switch (idx)
        {
            case 0: _snapshot.Enter(instanceId); break;
            case 1: _log.Enter(instanceId); break;
            case 2: _history.Enter(instanceId); break;
            case 3: _scriptDisk.Enter(instanceId); break;
            case 4: _integrity.Enter(instanceId); break;
            default: _assets.Enter(instanceId); break;
        }
    }
}
