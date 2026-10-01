// Copyright © FufuLauncher
//
// 资源与存档面板(版本工具第 6 页签):集中管理当前版本的 资源包 / 光影 / 存档。
// 资源包与光影支持把 zip 拖进启动器自动安装(ZipDropService 已有落盘路径);
// 本面板负责查看、打开目录、删除,存档来自游戏内创建。控件全走 UIKit 工厂与 T.* 令牌。

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class InstanceAssetsPanel : UserControl
{
    private readonly InstanceService _instances;

    private readonly ComboBox _instBox = UIKit.ComboBox();
    private readonly TextBlock _badge = UIKit.SubOneLine("", 11.5, 400);   // 单行+省略号:长路径不换行撑高行
    private readonly TextBlock _rpCount = UIKit.Sub("", 12);
    private readonly StackPanel _rpList = new();
    private readonly Border _rpEmpty;
    private readonly TextBlock _shCount = UIKit.Sub("", 12);
    private readonly StackPanel _shList = new();
    private readonly Border _shEmpty;
    private readonly TextBlock _svCount = UIKit.Sub("", 12);
    private readonly StackPanel _svList = new();
    private readonly Border _svEmpty;

    public InstanceAssetsPanel()
    {
        _instances = App.Services.GetRequiredService<InstanceService>();
        _rpEmpty = UIKit.EmptyHint("该版本还没有资源包,可把 .zip 资源包拖进启动器窗口自动安装");
        _shEmpty = UIKit.EmptyHint("该版本还没有光影,可把光影压缩包放入 shaderpacks 目录");
        _svEmpty = UIKit.EmptyHint("该版本还没有存档,进游戏创建世界后这里会列出");

        _instBox.MinWidth = 260;
        _instBox.VerticalAlignment = VerticalAlignment.Center;
        _instBox.SelectionChanged += (_, _) => Render();
        _badge.VerticalAlignment = VerticalAlignment.Center;
        _badge.Margin = new Thickness(12, 0, 0, 0);

        var refreshBtn = PanelKit.Btn("刷新", false, () => Enter(Current?.Id), 88);
        refreshBtn.VerticalAlignment = VerticalAlignment.Center;

        Content = UIKit.V(
            UIKit.Card(UIKit.V(
                UIKit.SettingRow("游戏版本", "要给哪个版本管理资源包 / 光影 / 存档,就在这里选", UIKit.H(_instBox, _badge)),
                UIKit.SettingRowNoDivider("操作", "资源包与光影可把 zip 拖进启动器自动安装;存档来自游戏内创建的世界", refreshBtn)),
                pad: 16, topGap: 12),
            SectionHeader("资源包", _rpCount),
            _rpEmpty, _rpList,
            SectionHeader("光影", _shCount),
            _shEmpty, _shList,
            SectionHeader("存档", _svCount),
            _svEmpty, _svList);
    }

    private GameInstance? Current => _instBox.SelectedItem as GameInstance;

    private static FrameworkElement SectionHeader(string title, TextBlock count)
    {
        var h = UIKit.H(UIKit.Text(title, 14, FontWeights.Medium), count);
        h.Margin = new Thickness(2, 14, 0, 4);
        return h;
    }

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
        string? pick = wantId ?? prev;
        for (int i = 0; i < _instBox.Items.Count; i++)
            if ((_instBox.Items[i] as GameInstance)?.Id == pick) { _instBox.SelectedIndex = i; break; }
        if (_instBox.SelectedIndex < 0 && _instBox.Items.Count > 0) _instBox.SelectedIndex = 0;
        _badge.Text = Current == null ? "" : $"MC {Current.VersionId} · 目录 {_instances.GetMinecraftDir(Current.Id)}";
    }

    private void Render()
    {
        var inst = Current;
        if (inst == null) return;
        RenderDir(_instances.GetResourcePacksDir(inst.Id), _rpList, _rpEmpty, _rpCount, "个资源包");
        RenderDir(_instances.GetShaderPacksDir(inst.Id), _shList, _shEmpty, _shCount, "个光影");
        RenderDir(_instances.GetSavesDir(inst.Id), _svList, _svEmpty, _svCount, "个存档");
    }

    private void RenderDir(string dir, StackPanel list, Border empty, TextBlock count, string unit)
    {
        list.Children.Clear();
        if (!Directory.Exists(dir))
        {
            empty.FadeToggle(true);
            count.Text = "";
            return;
        }
        // 资源包/光影:文件(zip 等)与子目录都算;存档:只算子目录
        var items = Directory.GetFileSystemEntries(dir)
            .Where(e => !dir.EndsWith("saves", StringComparison.OrdinalIgnoreCase) || Directory.Exists(e))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        empty.FadeToggle(items.Count == 0);
        count.Text = items.Count == 0 ? "" : $"{items.Count} {unit}";
        foreach (var name in items)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string full = Path.Combine(dir, name);
            list.Children.Add(MakeRow(name, full));
        }
    }

    private Border MakeRow(string name, string fullPath)
    {
        var nameTb = UIKit.Text(name, 12.5, FontWeights.Medium);
        nameTb.TextTrimming = TextTrimming.CharacterEllipsis;
        nameTb.VerticalAlignment = VerticalAlignment.Center;
        nameTb.ToolTip = fullPath;

        var openBtn = UIKit.Button("打开", primary: false, onClick: () => OpenDir(fullPath), height: 26);
        openBtn.MinWidth = 56;
        var delBtn = UIKit.Button("删除", primary: false, onClick: () => DeleteItem(name, fullPath), height: 26);
        delBtn.MinWidth = 56;
        delBtn.Margin = new Thickness(8, 0, 0, 0);

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { nameTb.Col(0), openBtn.Col(1), delBtn.Col(2) }
        };

        var row = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid
        };
        row.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");
        return row;
    }

    private void OpenDir(string fullPath)
    {
        try
        {
            if (Directory.Exists(fullPath)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{fullPath}\"") { UseShellExecute = true });
            else if (File.Exists(fullPath)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[资源与存档] 打开目录失败:{ex.Message}");
            DialogKit.Warn("打开目录失败,请手动到游戏目录查看。", "提示", Window.GetWindow(this));
        }
    }

    private void DeleteItem(string name, string fullPath)
    {
        bool isDir = Directory.Exists(fullPath);
        if (!DialogKit.Confirm($"确定要删除「{name}」吗?\n{(isDir ? "整个文件夹" : "文件")}会被永久删除,此操作不可恢复。", "删除确认", "删除", danger: true, owner: Window.GetWindow(this))) return;
        try
        {
            if (isDir) Directory.Delete(fullPath, true);
            else if (File.Exists(fullPath)) File.Delete(fullPath);
            Shell()?.SetStatus($"已删除 {name}");
            Render();
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[资源与存档] 删除失败:{ex.Message}");
            DialogKit.Warn("删除失败,文件可能正被游戏占用,请先关闭游戏。", "删除失败", Window.GetWindow(this));
        }
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
