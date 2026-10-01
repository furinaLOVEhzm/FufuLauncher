// Copyright © FufuLauncher
//
// 版本侧边预览面板(LauncherX-1):选中左侧版本卡后,页面右侧原地展开这块详情,
// 不弹全屏窗,看详情的同时还能继续操作列表。
// 顺带承载:分组归类(LauncherX-4)、外置资源快捷跳转(BlockHelm-5)、
// 磁盘占用速览(Celestial-5)、内存智能推荐(BlockHelm-4)。
// 全部控件走 UIKit 既有工厂,配色一律 T.* 主题令牌,不引入任何外来布局样式。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class InstancePreviewPanel : UserControl
{
    /// <summary>空壳实例判定:名字含加载器关键字但元数据未写入(加载器安装失败残留)</summary>
    private static bool IsShellInstance(GameInstance inst)
    {
        if (!string.IsNullOrEmpty(inst.ModLoader)) return false;
        if (string.IsNullOrWhiteSpace(inst.Name)) return false;
        string low = inst.Name.ToLowerInvariant();
        return low.Contains("neoforge") || low.Contains("fabric") || low.Contains("quilt")
            || low.Contains("forge") || low.Contains("optifine");
    }

    /// <summary>空壳警示徽章</summary>
    private static Border ShellLoaderBadge(GameInstance inst)
    {
        return UIKit.WarningBadge("⚠ 加载器未装",
            "版本名包含加载器关键字,但未检测到已安装的加载器本体(可能是安装中断残留)。\n" +
            "启动该版本时会弹出补装选项;直接启动只会进原版,模组不会被加载。");
    }

    private readonly InstanceService _instances;
    private readonly InstanceExtrasService _extras;
    private readonly DiskUsageService _disk;
    private readonly MemoryRecommendService _memRec;

    private readonly StackPanel _body = new();
    private readonly Border _shell;
    private GameInstance? _cur;
    private bool _measuring;

    /// <summary>用户点「收起」:由宿主页负责把面板 Visibility 置 Collapsed</summary>
    public event Action? CloseRequested;
    /// <summary>点「快速克隆」:交给宿主页跑克隆流程(需要弹输入框与进度)</summary>
    public event Action<string>? CloneRequested;
    /// <summary>点「导出整合包」:交给宿主页跑导出流程</summary>
    public event Action<string>? ExportRequested;
    /// <summary>分组归属发生变化:宿主页据此重排列表</summary>
    public event Action? GroupChanged;

    public InstancePreviewPanel()
    {
        _instances = App.Services.GetRequiredService<InstanceService>();
        _extras = App.Services.GetRequiredService<InstanceExtrasService>();
        _disk = App.Services.GetRequiredService<DiskUsageService>();
        _memRec = App.Services.GetRequiredService<MemoryRecommendService>();

        _shell = PanelKit.SideShell(_body, 348);
        Content = _shell;
        Visibility = Visibility.Collapsed;   // 默认不占位,列表宽度与原来完全一致
        Render();
    }

    /// <summary>展示指定版本的详情</summary>
    public void Show(GameInstance inst)
    {
        _cur = inst;
        Visibility = Visibility.Visible;
        Render();
    }

    /// <summary>清空并收起</summary>
    public void Close()
    {
        _cur = null;
        Visibility = Visibility.Collapsed;
        Render();
    }

    /// <summary>当前预览的版本 Id(无则空串)</summary>
    public string CurrentId => _cur?.Id ?? "";

    private void Render()
    {
        _body.Children.Clear();
        _body.Children.Add(PanelKit.SideHead("版本详情", () => { Close(); CloseRequested?.Invoke(); }));

        var inst = _cur;
        if (inst == null)
        {
            var hint = UIKit.Sub("点一下左侧任意版本卡,这里就会显示它的详情、分组、内存推荐与目录入口", 12);
            hint.TextWrapping = TextWrapping.Wrap;
            _body.Children.Add(hint);
            return;
        }

        // ---- 基本信息 ----
        var badges = UIKit.H(
            UIKit.Badge($"MC {inst.VersionId}", "T.ForegroundDim"),
            IsShellInstance(inst)
                ? ShellLoaderBadge(inst)
                : string.IsNullOrEmpty(inst.ModLoader)
                    ? UIKit.Badge("原版", "T.ForegroundDim")
                    : UIKit.Badge($"{inst.ModLoader} {inst.ModLoaderVersion}", "T.Primary"));
        badges.Margin = new Thickness(0, 6, 0, 0);
        var title = UIKit.Text(inst.Name, 15, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.ToolTip = inst.Name;
        _body.Children.Add(title);
        _body.Children.Add(badges);
        _body.Children.Add(PanelKit.Divider(12, 8));

        _body.Children.Add(PanelKit.KvRow("运行配置", inst.LoaderVersionId ?? inst.VersionId));
        // 2026-09-26 修复:Java 版本号与路径无分隔硬拼(录屏实锤「21D:\vs 2026...」粘连溢出);
        // 加分隔符「 · 」(自动选择也统一为中文括号),路径过长 KvRow 尾部省略 + ToolTip 显示完整路径
        string javaVal = inst.JavaMajorVersion
            + (string.IsNullOrEmpty(inst.JavaPath) ? "（自动选择）" : " · " + inst.JavaPath);
        _body.Children.Add(PanelKit.KvRow("Java", javaVal));
        _body.Children.Add(PanelKit.KvRow("创建时间",
            inst.CreatedAt.Year > 2000 ? inst.CreatedAt.ToString("yyyy-MM-dd HH:mm") : "未知"));
        _body.Children.Add(PanelKit.KvRow("上次启动", inst.LastPlayedAt.Year > 2000 ? inst.LastPlayedAt.ToString("yyyy-MM-dd HH:mm") : "从未启动"));
        _body.Children.Add(PanelKit.KvRow("游玩时长", inst.TotalPlayTimeSeconds > 0 ? FmtDuration(inst.TotalPlayTimeSeconds) : "-"));

        var (modCount, disabledCount) = SafeModCount(inst.Id);
        _body.Children.Add(PanelKit.KvRow("模组数量", disabledCount > 0 ? $"{modCount} 个(其中禁用 {disabledCount} 个)" : $"{modCount} 个"));
        _body.Children.Add(PanelKit.KvRow("配置快照", $"{_extras.SnapshotsOf(inst.Id).Count} 套"));
        _body.Children.Add(PanelKit.KvRow("启动记录", $"{_extras.LaunchHistoryOf(inst.Id).Count} 条"));

        // ---- 分组归类(LauncherX-4)----
        _body.Children.Add(PanelKit.Divider());
        _body.Children.Add(PanelKit.SectionHead("分组归类", "把版本收进自建分组文件夹,列表里可折叠展开"));
        _body.Children.Add(BuildGroupRow(inst));

        // ---- 内存智能推荐(BlockHelm-4)----
        _body.Children.Add(PanelKit.Divider());
        _body.Children.Add(PanelKit.SectionHead("内存推荐", "按本机物理内存 + 模组数量算出建议的 Xms / Xmx,可一键套用"));
        _body.Children.Add(BuildMemoryRow(inst, modCount));

        // ---- 磁盘占用(Celestial-5)----
        _body.Children.Add(PanelKit.Divider());
        _body.Children.Add(PanelKit.SectionHead("磁盘占用", "只统计这个版本自己占的空间,共享的游戏本体不计入"));
        _body.Children.Add(BuildDiskRow(inst));

        // ---- 快捷跳转(BlockHelm-5)----
        _body.Children.Add(PanelKit.Divider());
        _body.Children.Add(PanelKit.SectionHead("目录直达", "不用自己去磁盘翻路径,点一下就打开对应文件夹"));
        _body.Children.Add(BuildDirRows(inst));

        // ---- 快捷操作 ----
        _body.Children.Add(PanelKit.Divider());
        var cloneBtn = PanelKit.Btn("快速克隆", true, () => CloneRequested?.Invoke(inst.Id), 104);
        var exportBtn = PanelKit.Btn("导出整合包", false, () => ExportRequested?.Invoke(inst.Id), 104);
        var actRow = UIKit.H(cloneBtn, exportBtn);
        actRow.Margin = new Thickness(0, 6, 0, 0);
        _body.Children.Add(actRow);
        var tip = UIKit.Sub("克隆会把模组、各项设置与 JVM 参数整套继承给新版本;导出可选带游戏本体或只带用户内容", 11);
        tip.TextWrapping = TextWrapping.Wrap;
        tip.Margin = new Thickness(0, 6, 0, 0);
        _body.Children.Add(tip);
    }

    // ==================== 分组 ====================

    private UIElement BuildGroupRow(GameInstance inst)
    {
        var box = UIKit.ComboBox();
        box.MinWidth = 170;
        box.VerticalAlignment = VerticalAlignment.Center;
        var ids = new List<string> { "" };
        box.Items.Add("未分组");
        foreach (var g in _extras.GroupsSnapshot())
        {
            ids.Add(g.Id);
            box.Items.Add(g.Name);
        }
        string curGroup = _extras.Get(inst.Id).GroupId;
        int sel = ids.IndexOf(curGroup);
        box.SelectedIndex = sel >= 0 ? sel : 0;

        bool applying = false;
        box.SelectionChanged += (_, _) =>
        {
            if (applying) return;
            int i = box.SelectedIndex;
            if (i < 0 || i >= ids.Count) return;
            _extras.SetInstanceGroup(inst.Id, ids[i]);
            GroupChanged?.Invoke();
            Shell()?.SetStatus(i == 0 ? $"「{inst.Name}」已移出分组" : $"「{inst.Name}」已归入分组");
        };

        var addBtn = PanelKit.Btn("新建分组", false, () =>
        {
            string? name = DialogKit.Input("分组名称:", "新建分组", "", Window.GetWindow(this));
            if (string.IsNullOrWhiteSpace(name)) return;
            var g = _extras.AddGroup(name.Trim());
            if (g == null) { DialogKit.Warn("分组名称重复或为空", owner: Window.GetWindow(this)); return; }
            _extras.SetInstanceGroup(inst.Id, g.Id);
            GroupChanged?.Invoke();
            applying = true;
            Render();
            applying = false;
        }, 96);

        var delBtn = PanelKit.Btn("删除分组", false, () =>
        {
            string gid = _extras.Get(inst.Id).GroupId;
            if (string.IsNullOrEmpty(gid)) { DialogKit.Info("这个版本还没归入任何分组", owner: Window.GetWindow(this)); return; }
            var grp = _extras.GroupsSnapshot().FirstOrDefault(x => x.Id == gid);
            if (grp == null) return;
            if (!DialogKit.Confirm($"删除分组「{grp.Name}」?\n分组里的版本不会被删除,只是变回未分组。",
                    "删除分组", Window.GetWindow(this))) return;
            _extras.DeleteGroup(gid);
            GroupChanged?.Invoke();
            Render();
        }, 96);

        var row = UIKit.V(UIKit.H(box, addBtn), new Border { Padding = new Thickness(0, 8, 0, 0), Child = UIKit.H(delBtn) });
        row.Margin = new Thickness(0, 8, 0, 0);
        return row;
    }

    // ==================== 内存推荐 ====================

    private UIElement BuildMemoryRow(GameInstance inst, int modCount)
    {
        var info = UIKit.Sub("正在计算推荐值…", 11.5);
        info.TextWrapping = TextWrapping.Wrap;
        info.Margin = new Thickness(0, 8, 0, 0);

        MemoryRecommendation? rec = null;
        try { rec = _memRec.Recommend(inst.Id, modCount); }
        catch (Exception ex) { App.WriteAppLog($"[版本预览] 内存推荐失败:{ex.Message}"); }

        if (rec != null)
        {
            string cur = inst.UseCustomMemory ? $"当前手动指定 -Xms{inst.Xms}m -Xmx{inst.Xmx}m" : "当前为自动分配";
            info.Text = $"{cur}\n本机物理内存 {rec.TotalDisplay}({rec.Tier})\n推荐 {rec.Display}\n{rec.Reason}"
                        + (string.IsNullOrEmpty(rec.Warning) ? "" : $"\n⚠ {rec.Warning}");
            info.SetResourceReference(TextBlock.ForegroundProperty, string.IsNullOrEmpty(rec.Warning) ? "T.ForegroundDim" : "T.Warning");
        }
        else info.Text = "推荐值计算失败,可在「版本设置」里手动填写内存";

        var applyBtn = PanelKit.Btn("一键套用推荐", true, () =>
        {
            var r = rec ?? _memRec.Recommend(inst.Id, SafeModCount(inst.Id).Total);
            var (ok, msg) = _memRec.Apply(inst.Id, r);
            if (ok)
            {
                DialogKit.Success($"已套用推荐内存:{r.Display}\n{msg}\n\n之后仍可在「版本设置」里手动改。", owner: Window.GetWindow(this));
                Shell()?.SetStatus($"「{inst.Name}」内存已设为 {r.Display}");
            }
            else DialogKit.Error(msg, "套用失败", Window.GetWindow(this));
            Render();
        }, 132);
        var gotoBtn = PanelKit.Btn("手动调整", false, () => GroupChanged?.Invoke(), 96);
        gotoBtn.ToolTip = "刷新列表后可在「版本设置 → 基础设置」里手动填内存";

        var row = UIKit.V(info, new Border { Padding = new Thickness(0, 10, 0, 0), Child = UIKit.H(applyBtn, gotoBtn) });
        return row;
    }

    // ==================== 磁盘占用 ====================

    private UIElement BuildDiskRow(GameInstance inst)
    {
        var (bytes, at, oversize) = _extras.DiskUsageOf(inst.Id);
        var text = bytes > 0
            ? $"{StorageGuardService.FmtSize(bytes)}{(at.Year > 2000 ? $"(测量于 {at:MM-dd HH:mm})" : "")}"
            : "还没测过,点下面的按钮实测一次";
        var line = UIKit.Text(text, 12, oversize ? FontWeights.SemiBold : FontWeights.Normal);
        line.TextWrapping = TextWrapping.Wrap;
        line.SetResourceReference(TextBlock.ForegroundProperty, oversize ? "T.Warning" : "T.Foreground");
        line.Margin = new Thickness(0, 8, 0, 0);

        var measureBtn = PanelKit.Btn("实测占用", false, () => _ = MeasureAsync(inst.Id), 96);
        measureBtn.IsEnabled = !_measuring;
        var openBtn = PanelKit.Btn("全部版本统计", false, () => Shell()?.OpenInstanceTools(3, inst.Id), 116);
        openBtn.ToolTip = "跳到「版本工具 → 脚本与磁盘」看全部版本的占用汇总";

        var row = UIKit.V(line, new Border { Padding = new Thickness(0, 10, 0, 0), Child = UIKit.H(measureBtn, openBtn) });
        return row;
    }

    private async Task MeasureAsync(string instanceId)
    {
        if (_measuring) return;
        _measuring = true;
        try
        {
            var usage = await _disk.MeasureInstanceAsync(instanceId, force: true);
            Shell()?.SetStatus($"「{usage.InstanceName}」占用 {usage.TotalDisplay}");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本预览] 磁盘统计失败:{ex.Message}");
            Shell()?.SetStatus("磁盘统计失败:" + ex.Message, warning: true);
        }
        finally
        {
            _measuring = false;
            if (_cur?.Id == instanceId) Render();
        }
    }

    // ==================== 目录直达 ====================

    private UIElement BuildDirRows(GameInstance inst)
    {
        var r1 = UIKit.H(
            DirBtn("版本目录", () => _instances.GetInstanceDir(inst.Id)),
            DirBtn("模组目录", () => _instances.GetModsDir(inst.Id)));
        var r2 = UIKit.H(
            DirBtn("资源包", () => _instances.GetResourcePacksDir(inst.Id)),
            DirBtn("光影包", () => _instances.GetShaderPacksDir(inst.Id)));
        var r3 = UIKit.H(
            DirBtn("存档目录", () => _instances.GetSavesDir(inst.Id)),
            DirBtn("崩溃报告", () => System.IO.Path.Combine(_instances.GetInstanceDir(inst.Id), "crash-reports")));
        r1.Margin = new Thickness(0, 8, 0, 0);
        r2.Margin = new Thickness(0, 8, 0, 0);
        r3.Margin = new Thickness(0, 8, 0, 0);
        return UIKit.V(r1, r2, r3);
    }

    private Button DirBtn(string label, Func<string> dirOf)
    {
        var b = PanelKit.Btn(label, false, () =>
        {
            string dir = dirOf();
            if (!PanelKit.OpenFolder(dir))
                DialogKit.Error("打开目录失败:" + dir, owner: Window.GetWindow(this));
            else Shell()?.SetStatus("已打开 " + label);
        }, 96);
        b.ToolTip = dirOf();
        return b;
    }

    // ==================== 内部工具 ====================

    /// <summary>统计该版本的模组总数与禁用数:直接按扩展名枚举 mods 目录即可
    /// (.jar=启用、.disabled=禁用),与 ModManagerService.LoadMods 的计数口径完全一致;
    /// 关键是不再逐个打开 jar 解析元数据——大整合包数百个 mod 时,点开版本卡不会因此卡住 UI 主线程。</summary>
    private (int Total, int Disabled) SafeModCount(string instanceId)
    {
        try
        {
            string modsDir = _instances.GetModsDir(instanceId);
            if (!System.IO.Directory.Exists(modsDir)) return (0, 0);
            int total = 0, disabled = 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(modsDir))
            {
                string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".jar") total++;
                else if (ext == ".disabled") { total++; disabled++; }
            }
            return (total, disabled);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本预览] 统计模组数量失败:{ex.Message}");
            return (0, 0);
        }
    }

    private static string FmtDuration(long secs)
    {
        if (secs < 60) return $"{secs} 秒";
        if (secs < 3600) return $"{secs / 60} 分钟";
        long h = secs / 3600, m = (secs % 3600) / 60;
        return m > 0 ? $"{h} 小时 {m} 分" : $"{h} 小时";
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
