// Copyright © FufuLauncher
//
// 脚本与磁盘面板:
//   · 实例前后置脚本(Celestial-7)—— 游戏启动前跑一条命令、游戏退出后跑一条命令,
//     附总开关,整套脚本功能可以一键关掉;关掉后启动流程直接跳过,不报错也不拖慢启动。
//   · 磁盘占用统计(Celestial-5)—— 每个版本各占多少、全部版本合计多少、
//     共享的游戏本体/库/资源/Java 各占多少,占用特别大的版本高亮提醒。
// 控件全部走 UIKit 既有工厂,配色 T.* 令牌。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class ScriptDiskPanel : UserControl
{
    private readonly InstanceService _instances;
    private readonly LaunchScriptService _scripts;
    private readonly DiskUsageService _disk;

    // ---- 脚本 ----
    private readonly ComboBox _instBox = UIKit.ComboBox();
    private readonly CheckBox _scriptSw;
    private readonly TextBox _preBox = UIKit.TextBox(placeholder: "留空 = 不执行;例:cmd /c echo 启动前清理");
    private readonly TextBox _postBox = UIKit.TextBox(placeholder: "留空 = 不执行;例:powershell -File D:\\backup.ps1");
    private readonly TextBox _timeoutBox = UIKit.TextBox();
    private readonly TextBlock _scriptHint = UIKit.Sub("", 11.5);

    // ---- 磁盘 ----
    private readonly TextBlock _diskSummary = UIKit.Sub("", 12);
    private readonly StackPanel _diskList = new();
    private readonly ProgressBar _diskBar = UIKit.ProgressBar();
    private readonly Button _measureBtn;
    private bool _measuring;

    public ScriptDiskPanel()
    {
        _instances = App.Services.GetRequiredService<InstanceService>();
        _scripts = App.Services.GetRequiredService<LaunchScriptService>();
        _disk = App.Services.GetRequiredService<DiskUsageService>();

        _scriptSw = new UIKit.ToggleSwitch("启用脚本(总开关)", false);
        _scriptSw.VerticalAlignment = VerticalAlignment.Center;
        _scriptSw.ToolTip = "关掉之后启动前 / 退出后都不会再跑脚本,启动流程完全不受影响";

        _instBox.MinWidth = 260;
        _instBox.VerticalAlignment = VerticalAlignment.Center;
        _instBox.SelectionChanged += (_, _) => LoadScripts();

        _preBox.MinWidth = 380;
        _postBox.MinWidth = 380;
        _timeoutBox.MinWidth = 90; _timeoutBox.MaxWidth = 130;
        _timeoutBox.PreviewTextInput += DigitsOnly;
        _scriptHint.TextWrapping = TextWrapping.Wrap;

        var saveBtn = PanelKit.Btn("保存脚本设置", true, SaveScripts, 118);   // 2026-09-25:操作按钮等宽
        var testPreBtn = PanelKit.Btn("试跑启动前", false, () => TestScript(true), 118);
        var testPostBtn = PanelKit.Btn("试跑退出后", false, () => TestScript(false), 118);
        var envBtn = PanelKit.Btn("可用变量", false, ShowEnvDocs, 118);

        _measureBtn = PanelKit.Btn("重新统计全部", true, () => _ = MeasureAllAsync(), 128);   // 2026-09-25:等宽
        var cacheBtn = PanelKit.Btn("看上次结果", false, ShowCache, 128);
        cacheBtn.ToolTip = $"统计结果会缓存 {DiskUsageService.CacheHours} 小时,不重复扫盘";
        _diskSummary.TextWrapping = TextWrapping.Wrap;
        _diskBar.Margin = new Thickness(0, 10, 0, 4);

        Content = UIKit.V(
            UIKit.Card(UIKit.V(
                UIKit.SettingRow("游戏版本", "脚本是按版本单独设的,先选版本", _instBox),
                UIKit.SettingRow("脚本总开关", "关掉后这个版本不再跑任何脚本", _scriptSw),
                UIKit.SettingRow("启动前运行", "游戏进程拉起之前执行一次,超时或失败只记日志,不拦着启动", _preBox),
                UIKit.SettingRow("退出后运行", "游戏进程退出之后执行一次,可拿到退出码", _postBox),
                UIKit.SettingRow("超时时间", "单条脚本最多跑多少秒,超时强制结束(默认 60)",
                    UIKit.H(_timeoutBox, UIKit.Sub("秒", 12))),
                UIKit.SettingRowNoDivider("脚本操作", "改完记得点保存;试跑只看输出,不会影响游戏",
                    UIKit.H(saveBtn, testPreBtn, testPostBtn, envBtn)),
                new Border { Padding = new Thickness(2, 10, 2, 2), Child = _scriptHint }),
                pad: 16, topGap: 12),

            UIKit.Card(UIKit.V(
                UIKit.Text("磁盘占用统计", 14, FontWeights.Medium),
                UIKit.Sub("逐版本实测占用;共享的游戏本体只算一次,不重复计入各版本", 11.5).MarginTop(4),
                new Border { Padding = new Thickness(0, 12, 0, 0), Child = UIKit.H(_measureBtn, cacheBtn) },
                _diskBar,
                new Border { Padding = new Thickness(0, 4, 0, 8), Child = _diskSummary },
                _diskList),
                pad: 16, topGap: 10));

        _scriptSw.Checked += (_, _) => { if (Current != null) _scriptHint.Text = "总开关已打开,记得点「保存脚本设置」才生效"; };
        _scriptSw.Unchecked += (_, _) => { if (Current != null) _scriptHint.Text = "总开关已关闭,这个版本不会再跑脚本"; };
    }

    private GameInstance? Current => _instBox.SelectedItem as GameInstance;

    public void Enter(string? instanceId)
    {
        RefreshInstBox(instanceId);
        LoadScripts();
        ShowCache();
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
            return;
        }
        _instBox.IsEnabled = true;
        string target = string.IsNullOrEmpty(wantId) ? prev : wantId;
        int idx = _instances.Instances.FindIndex(i => i.Id == target);
        _instBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    // ==================== 前后置脚本(Celestial-7)====================

    private void LoadScripts()
    {
        var inst = Current;
        if (inst == null)
        {
            _scriptHint.Text = "";
            return;
        }
        var (enabled, pre, post, timeout) = _scripts.GetConfig(inst.Id);
        _scriptSw.IsChecked = enabled;
        _preBox.Text = pre;
        _postBox.Text = post;
        _timeoutBox.Text = timeout.ToString();
        _scriptHint.Text = enabled
            ? "脚本已启用:启动游戏时会在拉起进程前 / 进程退出后各跑一次"
            : "脚本总开关处于关闭状态,填了命令也不会执行";
        _scriptHint.SetResourceReference(TextBlock.ForegroundProperty, enabled ? "T.ForegroundDim" : "T.Warning");
    }

    private void SaveScripts()
    {
        var inst = Current;
        if (inst == null) { DialogKit.Info("请先安装并选择一个游戏版本", owner: Window.GetWindow(this)); return; }
        if (!int.TryParse(_timeoutBox.Text.Trim(), out int timeout) || timeout < 1 || timeout > 3600)
        {
            DialogKit.Warn("超时时间要填 1 ~ 3600 之间的整数(秒)", owner: Window.GetWindow(this));
            return;
        }
        string pre = _preBox.Text.Trim();
        string post = _postBox.Text.Trim();
        if (!string.IsNullOrEmpty(pre))
        {
            var (ok, msg) = _scripts.Validate(pre);
            if (!ok) { DialogKit.Warn("启动前脚本有问题:" + msg, "脚本校验", Window.GetWindow(this)); return; }
        }
        if (!string.IsNullOrEmpty(post))
        {
            var (ok, msg) = _scripts.Validate(post);
            if (!ok) { DialogKit.Warn("退出后脚本有问题:" + msg, "脚本校验", Window.GetWindow(this)); return; }
        }

        bool enabled = _scriptSw.IsChecked == true;
        _scripts.SaveConfig(inst.Id, enabled, pre, post, timeout);
        App.WriteAppLog($"[脚本] 保存:{inst.Name} 启用={enabled} 前置={(string.IsNullOrEmpty(pre) ? "无" : pre)} 后置={(string.IsNullOrEmpty(post) ? "无" : post)} 超时={timeout}s");
        Shell()?.SetStatus($"「{inst.Name}」的脚本设置已保存");
        LoadScripts();
        DialogKit.Success(enabled
            ? $"已保存「{inst.Name}」的脚本设置。\n下次启动游戏时自动执行。"
            : $"已保存并关闭「{inst.Name}」的脚本功能。\n启动流程会直接跳过脚本。", owner: Window.GetWindow(this));
    }

    private async void TestScript(bool pre)
    {
        var inst = Current;
        if (inst == null) return;
        string cmd = (pre ? _preBox.Text : _postBox.Text).Trim();
        if (string.IsNullOrEmpty(cmd))
        {
            DialogKit.Info(pre ? "「启动前运行」还没填命令" : "「退出后运行」还没填命令", owner: Window.GetWindow(this));
            return;
        }
        int timeout = int.TryParse(_timeoutBox.Text.Trim(), out int t) ? t : 60;
        _scriptHint.Text = "正在试跑…";
        try
        {
            var ctx = new ScriptContext
            {
                InstanceId = inst.Id,
                InstanceName = inst.Name,
                InstanceDir = _instances.GetInstanceDir(inst.Id),
                ModsDir = _instances.GetModsDir(inst.Id),
                SavesDir = _instances.GetSavesDir(inst.Id),
            };
            var res = await _scripts.RunAsync(cmd, ctx.InstanceDir, timeout, ctx);
            string body = $"命令:{cmd}\n结果:{res.Message}\n耗时:{res.Elapsed.TotalSeconds:0.0} 秒"
                          + (res.TimedOut ? "(超时被强制结束)" : "")
                          + (string.IsNullOrWhiteSpace(res.Output) ? "" : "\n\n输出:\n" + Trim(res.Output, 1600));
            if (res.Success) DialogKit.Success(body, "脚本试跑", Window.GetWindow(this));
            else DialogKit.Error(body, "脚本试跑", Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            DialogKit.Error("试跑异常:" + ex.Message, "脚本试跑", Window.GetWindow(this));
        }
        finally { LoadScripts(); }
    }

    private void ShowEnvDocs()
    {
        var lines = LaunchScriptService.EnvVarDocs.Select(d => $"{d.Name} —— {d.Desc}");
        DialogKit.Info("脚本里可以用这些环境变量:\n\n" + string.Join("\n", lines),
            "可用环境变量", Window.GetWindow(this));
    }

    // ==================== 磁盘占用(Celestial-5)====================

    private void ShowCache()
    {
        List<InstanceDiskUsage> cache;
        try { cache = _disk.ReadCache(); }
        catch (Exception ex)
        {
            App.WriteAppLog($"[磁盘] 读取缓存失败:{ex.Message}");
            cache = new List<InstanceDiskUsage>();
        }
        if (cache.Count == 0)
        {
            _diskSummary.Text = "还没有统计过,点「重新统计全部」实测一次(第一次会慢一些)";
            _diskList.Children.Clear();
            return;
        }
        long total = cache.Sum(c => c.TotalBytes);
        RenderList(cache, total, "上次统计结果");
    }

    private async Task MeasureAllAsync()
    {
        if (_measuring) return;
        _measuring = true;
        _measureBtn.IsEnabled = false;
        _diskBar.ResetProgress();
        _diskSummary.Text = "正在逐个版本统计磁盘占用…";
        try
        {
            int done = 0;
            var report = await _disk.MeasureAllAsync(new Progress<string>(msg => Dispatcher.BeginInvoke(() =>
            {
                done++;
                _diskBar.SmoothSet(Math.Min(100, done * 100.0 / Math.Max(1, _instances.Instances.Count + 4)));
                _diskSummary.Text = msg;
            })));
            RenderReport(report);
            Shell()?.SetStatus("磁盘占用统计完成");
        }
        catch (Exception ex)
        {
            _diskSummary.Text = "统计失败:" + ex.Message;
            App.WriteAppLog($"[磁盘] 统计异常:{ex}");
        }
        finally
        {
            _measuring = false;
            _measureBtn.IsEnabled = true;
            _diskBar.SmoothSet(100);
        }
    }

    private void RenderReport(DiskUsageReport report)
    {
        var (free, totalDrive) = DiskUsageService.GetDriveSpace(AppPaths.Root);
        string head = $"全部版本合计 {StorageGuardService.FmtSize(report.InstancesTotalBytes)} · "
                      + $"共享本体 {StorageGuardService.FmtSize(report.SharedBytes)} · "
                      + $"启动器数据总计 {StorageGuardService.FmtSize(report.GrandTotalBytes)}\n"
                      + $"共 {report.TotalFileCount:N0} 个文件 · 所在磁盘剩余 {StorageGuardService.FmtSize(free)} / {StorageGuardService.FmtSize(totalDrive)}"
                      + (report.OversizeCount > 0 ? $"\n⚠ 有 {report.OversizeCount} 个版本占用特别大,已在下面高亮" : "");
        _diskSummary.Text = head;
        _diskSummary.SetResourceReference(TextBlock.ForegroundProperty, report.OversizeCount > 0 ? "T.Warning" : "T.ForegroundDim");

        _diskList.Children.Clear();
        foreach (var u in report.Instances.OrderByDescending(x => x.TotalBytes))
            _diskList.Children.Add(MakeDiskRow(u.InstanceName, u.TotalDisplay, u.Detail, u.Oversize));

        _diskList.Children.Add(MakeDiskRow("共享 · 游戏本体 versions", StorageGuardService.FmtSize(report.VersionsBytes), "所有版本共用,删版本时按引用计数清理", false));
        _diskList.Children.Add(MakeDiskRow("共享 · 库文件 libraries", StorageGuardService.FmtSize(report.LibrariesBytes), "Forge / Fabric 等依赖库", false));
        _diskList.Children.Add(MakeDiskRow("共享 · 资源文件 assets", StorageGuardService.FmtSize(report.AssetsBytes), "音效、语言、贴图索引", false));
        _diskList.Children.Add(MakeDiskRow("共享 · Java 运行时", StorageGuardService.FmtSize(report.RuntimesBytes), "启动器下载管理的 JDK", false));
    }

    private void RenderList(List<InstanceDiskUsage> list, long total, string title)
    {
        var latest = list.Count == 0 ? default : list.Max(x => x.MeasuredAt);
        _diskSummary.Text = latest.Year <= 2000
            ? $"{title}:全部版本合计 {StorageGuardService.FmtSize(total)} · 尚未统计过(点上方按钮开始统计)"
            : $"{title}:全部版本合计 {StorageGuardService.FmtSize(total)} · 测量于 {latest:yyyy-MM-dd HH:mm}";
        _diskList.Children.Clear();
        foreach (var u in list.OrderByDescending(x => x.TotalBytes))
            _diskList.Children.Add(MakeDiskRow(u.InstanceName, u.TotalDisplay, u.Detail, u.Oversize));
    }

    private static UIElement MakeDiskRow(string name, string size, string detail, bool oversize)
    {
        var n = UIKit.Text(name, 12.5, oversize ? FontWeights.SemiBold : FontWeights.Medium);
        n.TextTrimming = TextTrimming.CharacterEllipsis;
        n.VerticalAlignment = VerticalAlignment.Center;
        if (oversize) n.SetResourceReference(TextBlock.ForegroundProperty, "T.Warning");
        var s = UIKit.Text(size, 12.5, FontWeights.SemiBold);
        s.VerticalAlignment = VerticalAlignment.Center;
        s.HorizontalAlignment = HorizontalAlignment.Right;
        if (oversize) s.SetResourceReference(TextBlock.ForegroundProperty, "T.Warning");
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { n, s.Col(1) }
        };
        var d = UIKit.Sub(detail, 10.5);
        d.TextWrapping = TextWrapping.Wrap;
        d.Margin = new Thickness(0, 3, 0, 0);
        var row = PanelKit.Row(UIKit.V(grid, d), pad: 10);
        if (oversize) PanelKit.Tint(row, "T.Warning");
        return row;
    }

    private static void DigitsOnly(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(c => !char.IsDigit(c));

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "\n…";

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
