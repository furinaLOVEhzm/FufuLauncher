// Copyright © FufuLauncher
//
// 日志智能筛选面板(Axolotl-4)+ 崩溃报告一键复制(Celestial-4):
//   · 日志按 崩溃 / 警告 / 信息 三类打标签,三类各自独立开关;
//   · 关键词搜索实时过滤;
//   · 筛选条件可收藏成预设,下次一键调出;
//   · 一键隐藏垃圾日志(资源加载噪声、重复刷屏那类);
//   · 游戏崩溃时自动把关键报错片段单独提取出来展示(Caused by 链条 + 疑似模组);
//   · 一键复制完整崩溃报告纯文本,方便发给别人排错。
// 控件全部走 UIKit 既有工厂,配色 T.* 令牌。

using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class LogFilterPanel : UserControl
{
    private const int MaxTailLines = 30000;   // 只读日志尾部,绝不整份吞进内存

    private readonly InstanceService _instances;
    private readonly GameLogFilterService _filter;
    private readonly CrashReportService _crash;

    private readonly ComboBox _instBox = UIKit.ComboBox();
    private readonly TextBlock _fileBadge = UIKit.Sub("", 11.5);
    private readonly TextBox _keyword = UIKit.TextBox(placeholder: "关键词,如 Exception、Sodium;空格分隔可写多个");
    private readonly CheckBox _crashSw;
    private readonly CheckBox _warnSw;
    private readonly CheckBox _infoSw;
    private readonly CheckBox _junkSw;
    private readonly ComboBox _presetBox = UIKit.ComboBox();
    private readonly TextBlock _statLine = UIKit.Sub("", 12);
    private readonly TextBox _output;
    private readonly StackPanel _crashHost = new();
    private readonly Button _loadBtn;

    private List<string> _raw = new();
    private string _rawPath = "";
    private List<LogFilterPreset> _presets = new();

    public LogFilterPanel()
    {
        _instances = App.Services.GetRequiredService<InstanceService>();
        _filter = App.Services.GetRequiredService<GameLogFilterService>();
        _crash = App.Services.GetRequiredService<CrashReportService>();

        _crashSw = new UIKit.ToggleSwitch("崩溃", true);
        _warnSw = new UIKit.ToggleSwitch("警告", true);
        _infoSw = new UIKit.ToggleSwitch("信息", true);
        _junkSw = new UIKit.ToggleSwitch("隐藏垃圾日志", true);
        foreach (var sw in new[] { _crashSw, _warnSw, _infoSw, _junkSw })
        {
            sw.VerticalAlignment = VerticalAlignment.Center;
            sw.Checked += (_, _) => Apply();
            sw.Unchecked += (_, _) => Apply();
        }

        _keyword.MinWidth = 300;
        _keyword.VerticalAlignment = VerticalAlignment.Center;
        _keyword.TextChanged += (_, _) => Apply();
        _keyword.ToolTip = "输入即时过滤,不用点搜索按钮";

        _presetBox.MinWidth = 180;
        _presetBox.VerticalAlignment = VerticalAlignment.Center;
        _presetBox.SelectionChanged += OnPresetPicked;

        _output = PanelKit.ReadOnlyBox("", 220);
        _loadBtn = PanelKit.Btn("读取日志", true, Load, 110);

        _instBox.MinWidth = 260;
        _instBox.VerticalAlignment = VerticalAlignment.Center;
        _instBox.SelectionChanged += (_, _) => OnInstancePicked();
        _fileBadge.VerticalAlignment = VerticalAlignment.Center;
        _fileBadge.Margin = new Thickness(12, 0, 0, 0);
        _fileBadge.TextTrimming = TextTrimming.CharacterEllipsis;
        _statLine.TextWrapping = TextWrapping.Wrap;

        Content = UIKit.V(
            UIKit.Card(UIKit.V(
                UIKit.SettingRow("游戏版本", "读哪个版本的 latest.log,就在这里选", UIKit.H(_instBox, _fileBadge)),
                UIKit.SettingRowNoDivider("读取", "只读日志尾部最多三万行,超大日志也不会卡界面", UIKit.H(_loadBtn))),
                pad: 16, topGap: 12),

            UIKit.Card(UIKit.V(
                UIKit.Text("筛选条件", 14, FontWeights.Medium),
                new Border { Padding = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, Child = UIKit.H(_crashSw, _warnSw, _infoSw, _junkSw) },   // 2026-09-25:开关组右对齐,与脚本页总开关同侧
                UIKit.SettingRow("关键词", "同时命中即显示;留空表示不按关键词过滤", _keyword),
                UIKit.SettingRowNoDivider("收藏的筛选条件", "把当前这套条件存下来,下次一键调出",
                    UIKit.H(_presetBox,
                        PanelKit.Btn("保存当前条件", false, SavePreset, 110),   // 2026-09-25:等宽
                        PanelKit.Btn("删除选中", false, DeletePreset, 110)))),
                pad: 16, topGap: 10),
            new Border { Padding = new Thickness(2, 10, 0, 0), Child = _statLine },

            UIKit.Card(UIKit.V(
                UIKit.Text("崩溃关键片段(自动提取)", 14, FontWeights.Medium),
                new Border { Padding = new Thickness(0, 8, 0, 0), Child = _crashHost }), pad: 16, topGap: 10),

            UIKit.Card(UIKit.V(
                UIKit.Text("筛选结果", 14, FontWeights.Medium),
                new Border { Padding = new Thickness(0, 10, 0, 0), Child = _output }), pad: 16, topGap: 10));
    }

    private GameInstance? Current => _instBox.SelectedItem as GameInstance;

    /// <summary>进入面板:重读版本列表并预选指定版本,顺手把日志读出来</summary>
    public void Enter(string? instanceId)
    {
        RefreshInstBox(instanceId);
        RefreshPresets();
        Load();
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
            _fileBadge.Text = "";
            return;
        }
        _instBox.IsEnabled = true;
        string target = string.IsNullOrEmpty(wantId) ? prev : wantId;
        int idx = _instances.Instances.FindIndex(i => i.Id == target);
        _instBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void OnInstancePicked()
    {
        var inst = Current;
        if (inst == null) { _fileBadge.Text = ""; return; }
        string path = SafeLatestLog(inst.Id);
        _fileBadge.Text = File.Exists(path) ? Path.GetFileName(path) : "还没有日志文件(启动过一次游戏才会有)";
    }

    private string SafeLatestLog(string instanceId)
    {
        try { return _crash.LatestLogPath(instanceId); }
        catch { return ""; }
    }

    // ==================== 读取与筛选 ====================

    private void Load()
    {
        var inst = Current;
        if (inst == null)
        {
            _raw = new List<string>();
            _output.Text = "";
            _statLine.Text = "请先安装并选择一个游戏版本";
            RenderCrash(null);
            return;
        }
        string path = SafeLatestLog(inst.Id);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _raw = new List<string>();
            _rawPath = "";
            _output.Text = "";
            _statLine.Text = "这个版本还没有 latest.log,启动过一次游戏之后再来读";
            RenderCrash(null);
            return;
        }
        try
        {
            _raw = ReadTail(path, MaxTailLines);
            _rawPath = path;
            _fileBadge.Text = $"{Path.GetFileName(path)} · {StorageGuardService.FmtSize(new FileInfo(path).Length)}";
        }
        catch (Exception ex)
        {
            _raw = new List<string>();
            _output.Text = "";
            _statLine.Text = "日志读取失败:" + ex.Message;
            App.WriteAppLog($"[日志面板] 读取失败:{path} → {ex.Message}");
            return;
        }
        Apply();
        Shell()?.SetStatus($"已读取 {inst.Name} 的日志,共 {_raw.Count} 行");
    }

    private LogFilterQuery BuildQuery() => new()
    {
        Keyword = _keyword.Text.Trim(),
        ShowCrash = _crashSw.IsChecked == true,
        ShowWarn = _warnSw.IsChecked == true,
        ShowInfo = _infoSw.IsChecked == true,
        HideJunk = _junkSw.IsChecked == true
    };

    /// <summary>套用当前筛选条件并重画结果(关键词输入即时触发)</summary>
    private void Apply()
    {
        if (_raw.Count == 0)
        {
            _output.Text = "";
            RenderCrash(null);
            return;
        }
        LogFilterResult res;
        CrashExcerpt excerpt;
        try
        {
            var query = BuildQuery();
            res = _filter.Run(_raw, query);
            excerpt = _filter.ExtractCrash(_raw);
        }
        catch (Exception ex)
        {
            _statLine.Text = "筛选失败:" + ex.Message;
            App.WriteAppLog($"[日志面板] 筛选异常:{ex}");
            return;
        }

        _statLine.Text = res.Summary + (_raw.Count >= MaxTailLines ? $"(只读了尾部 {MaxTailLines} 行)" : "");
        _statLine.SetResourceReference(TextBlock.ForegroundProperty, res.CrashCount > 0 ? "T.Danger" : "T.ForegroundDim");

        var sb = new StringBuilder();
        const int cap = 3000;   // 只往界面里灌前 3000 行,再多就只统计不落屏
        int shown = 0;
        foreach (var line in res.Lines)
        {
            if (shown >= cap) break;
            sb.Append(TagOf(line.Level)).Append(' ').AppendLine(line.Raw);
            shown++;
        }
        if (res.Lines.Count > cap)
            sb.AppendLine($"…另有 {res.Lines.Count - cap} 行未显示,可用「导出筛选结果」拿到完整内容");
        _output.Text = sb.Length == 0 ? "(没有符合条件的日志行)" : sb.ToString();

        RenderCrash(excerpt);
    }

    private static string TagOf(GameLogLevel level) => level switch
    {
        GameLogLevel.Crash => "[崩溃]",
        GameLogLevel.Warn => "[警告]",
        _ => "[信息]"
    };

    // ==================== 崩溃片段(Celestial-4)====================

    private void RenderCrash(CrashExcerpt? ex)
    {
        _crashHost.Children.Clear();
        var inst = Current;

        if (ex == null || !ex.Found)
        {
            var none = UIKit.Sub(inst == null
                ? "选一个游戏版本后自动检测"
                : "这份日志里没有检测到崩溃锚点,游戏大概率是正常退出的", 11.5);
            none.TextWrapping = TextWrapping.Wrap;
            _crashHost.Children.Add(none);
            if (inst != null) _crashHost.Children.Add(BuildCrashActions(inst.Id, null));
            return;
        }

        var head = UIKit.Text(ex.Headline, 13, FontWeights.SemiBold);
        head.TextWrapping = TextWrapping.Wrap;
        head.SetResourceReference(TextBlock.ForegroundProperty, "T.Danger");
        _crashHost.Children.Add(head);

        if (ex.Causes.Count > 0)
        {
            var causes = UIKit.Sub("错误链:\n" + string.Join("\n", ex.Causes.Take(12)), 11.5);
            causes.TextWrapping = TextWrapping.Wrap;
            causes.SetResourceReference(TextBlock.ForegroundProperty, "T.Warning");
            causes.Margin = new Thickness(0, 6, 0, 0);
            _crashHost.Children.Add(causes);
        }
        if (ex.SuspectMods.Count > 0)
        {
            var suspects = UIKit.Sub("疑似出问题的模组:" + string.Join("、", ex.SuspectMods.Take(12)), 11.5);
            suspects.TextWrapping = TextWrapping.Wrap;
            suspects.Margin = new Thickness(0, 4, 0, 0);
            _crashHost.Children.Add(suspects);
        }

        var box = PanelKit.ReadOnlyBox(ex.Text, 130);
        box.Margin = new Thickness(0, 8, 0, 0);
        _crashHost.Children.Add(box);

        if (inst != null) _crashHost.Children.Add(BuildCrashActions(inst.Id, ex));
    }

    private UIElement BuildCrashActions(string instanceId, CrashExcerpt? ex)
    {
        var copyBtn = PanelKit.Btn("一键复制崩溃报告", true, () => CopyCrash(instanceId), 148);
        copyBtn.ToolTip = "把版本信息、崩溃报告全文与日志关键片段拼成纯文本复制到剪贴板";
        var saveBtn = PanelKit.Btn("导出为文件", false, () => SaveCrash(instanceId), 104);
        saveBtn.ToolTip = "剪贴板被别的程序占用时,改存成 txt 文件";
        var dirBtn = PanelKit.Btn("打开崩溃目录", false, () =>
        {
            if (!_crash.OpenCrashFolder(instanceId))
                DialogKit.Error("打开崩溃报告目录失败", owner: Window.GetWindow(this));
        }, 116);
        var exportBtn = PanelKit.Btn("导出筛选结果", false, ExportFiltered, 116);
        exportBtn.IsEnabled = ex != null || _raw.Count > 0;

        var row = UIKit.H(copyBtn, saveBtn);
        row.Margin = new Thickness(0, 10, 0, 0);
        var row2 = UIKit.H(dirBtn, exportBtn);
        row2.Margin = new Thickness(0, 8, 0, 0);
        return UIKit.V(row, row2);
    }

    private void CopyCrash(string instanceId)
    {
        var (ok, msg) = _crash.CopyToClipboard(instanceId);
        if (ok)
        {
            Shell()?.SetStatus("崩溃报告已复制到剪贴板");
            DialogKit.Success(msg, "复制崩溃报告", Window.GetWindow(this));
        }
        else DialogKit.Warn(msg + "\n\n可以改用「导出为文件」拿到同样的内容。", "复制失败", Window.GetWindow(this));
    }

    private void SaveCrash(string instanceId)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出崩溃报告",
            Filter = "文本文件|*.txt",
            FileName = $"崩溃报告_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var (ok, msg, _) = _crash.SaveCopyText(instanceId, dlg.FileName);
        if (ok) DialogKit.Success(msg, "导出崩溃报告", Window.GetWindow(this));
        else DialogKit.Error(msg, "导出崩溃报告", Window.GetWindow(this));
    }

    private void ExportFiltered()
    {
        if (_raw.Count == 0) { DialogKit.Info("先读一份日志再导出", owner: Window.GetWindow(this)); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出筛选结果",
            Filter = "文本文件|*.txt",
            FileName = $"日志筛选_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var res = _filter.Run(_raw, BuildQuery());
            var sb = new StringBuilder();
            sb.AppendLine("# FufuLauncher 日志筛选结果");
            sb.AppendLine("# 来源:" + _rawPath);
            sb.AppendLine("# 条件:" + DescribeQuery());
            sb.AppendLine("# 统计:" + res.Summary);
            sb.AppendLine();
            foreach (var line in res.Lines)
                sb.Append(TagOf(line.Level)).Append(' ').AppendLine(line.Raw);
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
            DialogKit.Success($"已导出 {res.Lines.Count} 行到:\n{dlg.FileName}", "导出筛选结果", Window.GetWindow(this));
        }
        catch (Exception ex) { DialogKit.Error("导出失败:" + ex.Message, owner: Window.GetWindow(this)); }
    }

    private string DescribeQuery()
    {
        var q = BuildQuery();
        var parts = new List<string>();
        if (q.ShowCrash) parts.Add("崩溃");
        if (q.ShowWarn) parts.Add("警告");
        if (q.ShowInfo) parts.Add("信息");
        string s = "显示 " + (parts.Count == 0 ? "无" : string.Join("/", parts));
        if (!string.IsNullOrEmpty(q.Keyword)) s += $" · 关键词「{q.Keyword}」";
        if (q.HideJunk) s += " · 隐藏垃圾日志";
        return s;
    }

    // ==================== 收藏筛选条件 ====================

    private void RefreshPresets()
    {
        try { _presets = _filter.Presets(); }
        catch (Exception ex) { App.WriteAppLog($"[日志面板] 预设读取失败:{ex.Message}"); _presets = new List<LogFilterPreset>(); }
        _presetBox.SelectionChanged -= OnPresetPicked;
        _presetBox.Items.Clear();
        _presetBox.Items.Add("(不使用收藏)");
        foreach (var p in _presets) _presetBox.Items.Add(p.Name);
        _presetBox.SelectedIndex = 0;
        _presetBox.SelectionChanged += OnPresetPicked;
    }

    private void OnPresetPicked(object sender, SelectionChangedEventArgs e)
    {
        int i = _presetBox.SelectedIndex;
        if (i <= 0 || i - 1 >= _presets.Count) return;
        var q = GameLogFilterService.FromPreset(_presets[i - 1]);
        // 回填过程中开关会反复触发 Apply,最后一次统一筛一遍就行
        _crashSw.IsChecked = q.ShowCrash;
        _warnSw.IsChecked = q.ShowWarn;
        _infoSw.IsChecked = q.ShowInfo;
        _junkSw.IsChecked = q.HideJunk;
        _keyword.Text = q.Keyword;
        Apply();
        Shell()?.SetStatus($"已调出收藏的筛选条件「{_presets[i - 1].Name}」");
    }

    private void SavePreset()
    {
        string? name = DialogKit.Input("给这套筛选条件起个名字:", "收藏筛选条件",
            $"筛选 {DateTime.Now:MM-dd HHmm}", Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name)) return;
        var (saved, msg) = _filter.SavePreset(name.Trim(), BuildQuery());
        if (saved == null) { DialogKit.Warn(msg, "收藏筛选条件", Window.GetWindow(this)); return; }
        RefreshPresets();
        Shell()?.SetStatus($"筛选条件「{saved.Name}」已收藏");
    }

    private void DeletePreset()
    {
        int i = _presetBox.SelectedIndex;
        if (i <= 0 || i - 1 >= _presets.Count)
        {
            DialogKit.Info("先在左边选中一条收藏的筛选条件", owner: Window.GetWindow(this));
            return;
        }
        var p = _presets[i - 1];
        if (!DialogKit.Confirm($"删除收藏的筛选条件「{p.Name}」?", "删除收藏", Window.GetWindow(this))) return;
        _filter.DeletePreset(p.Id);
        RefreshPresets();
        Shell()?.SetStatus("收藏已删除");
    }

    // ==================== 内部工具 ====================

    /// <summary>只取文件尾部若干行:大日志(几十 MB)也不整份读进内存</summary>
    private static List<string> ReadTail(string path, int maxLines)
    {
        var buf = new LinkedList<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (sr.ReadLine() is { } line)
        {
            buf.AddLast(line);
            if (buf.Count > maxLines) buf.RemoveFirst();
        }
        return buf.ToList();
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
