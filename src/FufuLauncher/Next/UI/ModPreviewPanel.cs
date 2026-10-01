// Copyright © FufuLauncher
//
// 模组侧边预览面板(LauncherX-1):选中模组行后在页面右侧原地展开详情,
// 不弹全屏窗,看详情的同时还能继续操作左侧模组列表。
// 承载:模组冲突快速诊断(Axolotl-2)、模组版本智能匹配(Axolotl-3)、
// 模组依赖树可视化(Celestial-3);另导出悬浮元数据卡片构造器供列表悬停使用(Axolotl-7)。
// 全部控件走 UIKit 既有工厂,配色一律 T.* 主题令牌。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class ModPreviewPanel : UserControl
{
    private readonly InstanceService _instances;
    private readonly ModManagerService _mods;
    private readonly ModDiagnosticsService _diag;

    private readonly StackPanel _body = new();
    private readonly Border _shell;
    private readonly StackPanel _reportHost = new();

    private ModInfo? _cur;
    private string _instanceId = "";
    private bool _busy;
    private int _treeGen;

    /// <summary>用户点「收起」</summary>
    public event Action? CloseRequested;
    /// <summary>诊断/依赖树/换版本装完后,宿主页需要重刷列表</summary>
    public event Action? RefreshRequested;

    public ModPreviewPanel()
    {
        _instances = App.Services.GetRequiredService<InstanceService>();
        _mods = App.Services.GetRequiredService<ModManagerService>();
        _diag = App.Services.GetRequiredService<ModDiagnosticsService>();

        _shell = PanelKit.SideShell(_body, 360);
        Content = _shell;
        Visibility = Visibility.Collapsed;
        Render();
    }

    /// <summary>展示指定模组的详情</summary>
    public void Show(ModInfo mod, string instanceId)
    {
        _cur = mod;
        _instanceId = instanceId;
        _treeGen++;   // 作废上一份在途的依赖树异步回填
        Visibility = Visibility.Visible;
        Render();
    }

    public void Close()
    {
        _cur = null;
        Visibility = Visibility.Collapsed;
        Render();
    }

    private void Render()
    {
        _body.Children.Clear();
        _body.Children.Add(PanelKit.SideHead("模组详情", () => { Close(); CloseRequested?.Invoke(); }));

        var mod = _cur;
        if (mod == null)
        {
            var hint = UIKit.Sub("点一下左侧任意模组行,这里就会显示它的作者、版本、依赖与冲突诊断结果", 12);
            hint.TextWrapping = TextWrapping.Wrap;
            _body.Children.Add(hint);
            return;
        }

        var badges = UIKit.H(
            UIKit.Badge(mod.Enabled ? "已启用" : "已禁用", mod.Enabled ? "T.Success" : "T.ForegroundDim"),
            UIKit.Badge(mod.LoaderDisplay, "T.Primary"));
        badges.Margin = new Thickness(0, 6, 0, 0);
        var title = UIKit.Text(mod.DisplayName, 15, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.ToolTip = mod.DisplayName;
        _body.Children.Add(title);
        _body.Children.Add(badges);
        _body.Children.Add(PanelKit.Divider(12, 8));

        _body.Children.Add(PanelKit.KvRow("模组 ID", mod.ModIdDisplay));
        _body.Children.Add(PanelKit.KvRow("版本", mod.VersionDisplay));
        _body.Children.Add(PanelKit.KvRow("作者", string.IsNullOrWhiteSpace(mod.Author) ? "-" : mod.Author));
        _body.Children.Add(PanelKit.KvRow("适配游戏", mod.McVersionDisplay));
        _body.Children.Add(PanelKit.KvRow("文件大小", mod.SizeDisplay));
        _body.Children.Add(PanelKit.KvRow("文件名", mod.FileName));
        _body.Children.Add(PanelKit.KvRow("必需前置", mod.RequiredDepsDisplay));

        var inst = _instances.Instances.FirstOrDefault(i => i.Id == _instanceId);
        string mc = _mods.GetCurrentMcVersion() ?? inst?.VersionId ?? "";
        string loader = _mods.GetCurrentModLoader() ?? inst?.ModLoader ?? "";
        _body.Children.Add(PanelKit.KvRow("当前环境", string.IsNullOrEmpty(mc) ? "-" : $"MC {mc} · {(string.IsNullOrEmpty(loader) ? "原版" : loader)}"));

        if (!string.IsNullOrWhiteSpace(mod.Description))
        {
            var desc = UIKit.Sub(Trim(mod.Description, 240), 11.5);
            desc.TextWrapping = TextWrapping.Wrap;
            desc.Margin = new Thickness(0, 8, 0, 0);
            _body.Children.Add(desc);
        }

        // ---- 操作按钮 ----
        _body.Children.Add(PanelKit.Divider());
        var scanBtn = PanelKit.Btn("冲突诊断", true, RunDiagnose, 104);
        var treeBtn = PanelKit.Btn("依赖树", false, () => _ = RunDependencyTreeAsync(), 96);
        var matchBtn = PanelKit.Btn("版本匹配", false, () => _ = RunVersionMatchAsync(mc, loader), 104);
        matchBtn.ToolTip = "按当前游戏版本与加载器找可用的适配版本,可一键下载替换";
        _body.Children.Add(UIKit.H(scanBtn, treeBtn));
        _body.Children.Add(new Border { Padding = new Thickness(0, 8, 0, 0), Child = UIKit.H(matchBtn) });

        _body.Children.Add(PanelKit.Divider());
        _body.Children.Add(_reportHost);
        RenderReport();
    }

    private void RenderReport()
    {
        // 报告区内容由各操作按需填充,这里只在初始化时给个空态
        if (_reportHost.Children.Count == 0)
        {
            var idle = UIKit.Sub("点上面的按钮开始诊断:冲突来源、缺失前置、版本是否匹配都会显示在这里", 11.5);
            idle.TextWrapping = TextWrapping.Wrap;
            _reportHost.Children.Add(idle);
        }
    }

    private void SetReportTitle(string title, string? tone = null)
    {
        _reportHost.Children.Clear();
        var t = UIKit.Text(title, 13, FontWeights.SemiBold);
        t.TextWrapping = TextWrapping.Wrap;
        if (!string.IsNullOrEmpty(tone)) t.SetResourceReference(TextBlock.ForegroundProperty, tone);
        _reportHost.Children.Add(t);
    }

    private void AddReport(UIElement el) => _reportHost.Children.Add(el);

    // ==================== Axolotl-2 冲突快速诊断 ====================

    private void RunDiagnose()
    {
        var mod = _cur;
        if (mod == null) return;
        SetReportTitle("正在扫描冲突…");
        ModDiagnosisReport report;
        try { report = _diag.Diagnose(_instanceId, mod); }
        catch (Exception ex)
        {
            SetReportTitle("诊断失败:" + ex.Message, "T.Danger");
            App.WriteAppLog($"[模组预览] 冲突诊断异常:{ex}");
            return;
        }

        SetReportTitle(report.HasIssues
            ? $"发现 {report.Issues.Count} 个问题(严重 {report.SevereCount} 个)"
            : "诊断通过:没有 ID 冲突,前置依赖齐全",
            report.HasIssues ? (report.SevereCount > 0 ? "T.Danger" : "T.Warning") : "T.Success");

        var scope = UIKit.Sub($"扫描范围:{report.ScannedMods} 个模组 · MC {report.McVersion} · {(string.IsNullOrEmpty(report.Loader) ? "原版" : report.Loader)}", 11);
        scope.TextWrapping = TextWrapping.Wrap;
        scope.Margin = new Thickness(0, 4, 0, 8);
        AddReport(scope);

        foreach (var issue in report.Issues.OrderByDescending(i => i.Severe).Take(40))
            AddReport(MakeIssueRow(issue));
        if (report.Issues.Count > 40)
            AddReport(UIKit.Sub($"…另有 {report.Issues.Count - 40} 条未列出", 11));
        if (!report.HasIssues)
        {
            var ok = UIKit.Sub(report.Summary, 11.5);
            ok.TextWrapping = TextWrapping.Wrap;
            AddReport(ok);
        }
    }

    /// <summary>单条问题行:类型徽章 + 涉及对象 + 冲突来源 + 一句话修复提示(严重项描红)</summary>
    private static Border MakeIssueRow(ModIssue issue)
    {
        var kind = UIKit.Badge(issue.KindDisplay, issue.Severe ? "T.Danger" : "T.Warning");
        kind.Margin = new Thickness(0, 0, 0, 4);
        var target = UIKit.Text(issue.Target, 12, FontWeights.Medium);
        target.TextWrapping = TextWrapping.Wrap;
        var source = UIKit.Sub("来源:" + issue.Source, 11);
        source.TextWrapping = TextWrapping.Wrap;
        source.Margin = new Thickness(0, 2, 0, 0);
        var fix = UIKit.Sub("修复:" + issue.Fix, 11);
        fix.TextWrapping = TextWrapping.Wrap;
        fix.SetResourceReference(TextBlock.ForegroundProperty, issue.Severe ? "T.Danger" : "T.Primary");
        fix.Margin = new Thickness(0, 2, 0, 0);

        var row = PanelKit.Row(UIKit.V(kind, target, source, fix), pad: 10);
        PanelKit.Tint(row, issue.Severe ? "T.Danger" : "T.Warning");
        return row;
    }

    // ==================== Celestial-3 依赖树 ====================

    private async Task RunDependencyTreeAsync()
    {
        var mod = _cur;
        if (mod == null || _busy) return;
        int gen = ++_treeGen;
        _busy = true;
        SetReportTitle("正在构建依赖树…");
        try
        {
            var root = _diag.BuildDependencyTree(_instanceId, mod);
            var inst = _instances.Instances.FirstOrDefault(i => i.Id == _instanceId);
            string mc = _mods.GetCurrentMcVersion() ?? inst?.VersionId ?? "";
            string loader = _mods.GetCurrentModLoader() ?? inst?.ModLoader ?? "";
            await _diag.EnrichDependencyTreeAsync(root, mc, loader);
            if (gen != _treeGen) return;

            int missing = CountMissing(root);
            SetReportTitle(missing > 0 ? $"依赖树:缺 {missing} 个必需前置" : "依赖树:必需前置全部就位",
                missing > 0 ? "T.Danger" : "T.Success");
            var legend = UIKit.Sub("■ 必需前置    □ 可选前置    红色 = 缺失,可直接搜索下载", 11);
            legend.Margin = new Thickness(0, 4, 0, 8);
            AddReport(legend);
            AddReport(RenderNode(root, 0, gen));
        }
        catch (Exception ex)
        {
            if (gen == _treeGen) SetReportTitle("依赖树构建失败:" + ex.Message, "T.Danger");
            App.WriteAppLog($"[模组预览] 依赖树异常:{ex}");
        }
        finally { _busy = false; }
    }

    private static int CountMissing(DependencyNode node)
    {
        int n = node.Missing ? 1 : 0;
        foreach (var c in node.Children) n += CountMissing(c);
        return n;
    }

    /// <summary>递归渲染树节点:缩进体现层级,必需/可选前缀区分,缺失标红并提供一键搜索下载</summary>
    private UIElement RenderNode(DependencyNode node, int depth, int gen)
    {
        var host = new StackPanel();
        host.Children.Add(MakeNodeRow(node, depth, gen));
        if (depth < 4)
            foreach (var c in node.Children)
                host.Children.Add(RenderNode(c, depth + 1, gen));
        return host;
    }

    private UIElement MakeNodeRow(DependencyNode node, int depth, int gen)
    {
        bool required = node.DependencyType == "required";
        string prefix = required ? "■ " : "□ ";
        string label = prefix + node.DisplayName + (string.IsNullOrEmpty(node.Version) ? "" : $" {node.Version}");
        var name = UIKit.Text(label, 12, required ? FontWeights.Medium : FontWeights.Normal);
        name.TextWrapping = TextWrapping.Wrap;
        if (node.Missing) name.SetResourceReference(TextBlock.ForegroundProperty, "T.Danger");
        else if (node.InstalledButDisabled) name.SetResourceReference(TextBlock.ForegroundProperty, "T.Warning");

        string state = node.Missing ? "缺失"
            : node.InstalledButDisabled ? "已安装但被禁用"
            : node.Installed ? "已安装" : "未安装(可选)";
        var sub = UIKit.Sub($"{node.TypeDisplay} · {state}" + (string.IsNullOrEmpty(node.VersionRange) ? "" : $" · 要求 {node.VersionRange}"), 10.5);
        sub.TextWrapping = TextWrapping.Wrap;
        sub.Margin = new Thickness(0, 2, 0, 0);
        if (node.Missing) sub.SetResourceReference(TextBlock.ForegroundProperty, "T.Danger");

        var col = UIKit.V(name, sub);
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { col }
        };

        // 缺失且平台可下载的依赖:行尾给一键搜索下载按钮
        if (node.Missing && node.Downloadable)
        {
            var btn = PanelKit.Btn("下载", true, () => _ = InstallNodeAsync(node, gen), 68);
            btn.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(btn.Col(1));
        }

        var panel = PanelKit.Row(row, pad: 10);
        panel.Margin = new Thickness(depth * 16, 0, 0, 6);
        if (node.Missing) PanelKit.Tint(panel, "T.Danger");
        return panel;
    }

    private async Task InstallNodeAsync(DependencyNode node, int gen)
    {
        if (_busy || gen != _treeGen) return;
        var mod = _cur;
        if (mod == null) return;
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == _instanceId);
        if (inst == null) return;
        string mc = _mods.GetCurrentMcVersion() ?? inst.VersionId;
        string loader = _mods.GetCurrentModLoader() ?? inst.ModLoader ?? "";

        _busy = true;
        SetReportTitle($"正在下载缺失前置 {node.DisplayName}…");
        try
        {
            var candidates = await _diag.FindCompatibleAsync(
                new ModInfo { Name = node.DisplayName, ModId = node.ModId }, mc, loader, limit: 5);
            if (gen != _treeGen) return;
            if (candidates.Count == 0)
            {
                SetReportTitle($"没找到适配 MC {mc} 的 {node.DisplayName}", "T.Warning");
                return;
            }
            var pick = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(node.ModId)
                        && c.ProjectId.Contains(node.ModId, StringComparison.OrdinalIgnoreCase))
                    ?? candidates[0];
            var (ok, msg) = await _diag.InstallCompatibleAsync(pick, _instances.GetModsDir(inst.Id), mc, loader);
            if (gen != _treeGen) return;
            SetReportTitle(ok ? $"已下载 {pick.ProjectTitle} {pick.VersionNumber}" : "下载失败:" + msg, ok ? "T.Success" : "T.Danger");
            if (ok) RefreshRequested?.Invoke();
        }
        catch (Exception ex)
        {
            if (gen == _treeGen) SetReportTitle("下载异常:" + ex.Message, "T.Danger");
            App.WriteAppLog($"[模组预览] 缺失前置下载异常:{ex}");
        }
        finally { _busy = false; }
    }

    // ==================== Axolotl-3 版本智能匹配 ====================

    private async Task RunVersionMatchAsync(string mc, string loader)
    {
        var mod = _cur;
        if (mod == null || _busy) return;
        int gen = ++_treeGen;
        _busy = true;
        SetReportTitle($"正在为 {mod.DisplayName} 匹配 MC {mc} 的适配版本…");
        try
        {
            bool verOk = string.IsNullOrEmpty(mod.McVersion) || ModDiagnosticsService.McVersionMatches(mod.McVersion, mc);
            bool loaderOk = ModDiagnosticsService.LoaderMatches(mod.ModLoader, loader);
            var verdict = UIKit.Sub(verOk && loaderOk
                ? "当前文件与这个版本匹配,不需要换"
                : $"不匹配:{(!verOk ? $"声明适配 {mod.McVersionDisplay},当前是 MC {mc}" : "")}{(!verOk && !loaderOk ? "; " : "")}{(!loaderOk ? $"加载器 {mod.LoaderDisplay},当前是 {(string.IsNullOrEmpty(loader) ? "原版" : loader)}" : "")}",
                11.5);
            verdict.TextWrapping = TextWrapping.Wrap;
            verdict.SetResourceReference(TextBlock.ForegroundProperty, verOk && loaderOk ? "T.Success" : "T.Danger");

            var candidates = await _diag.FindCompatibleAsync(mod, mc, loader, limit: 8);
            if (gen != _treeGen) return;

            SetReportTitle(candidates.Count > 0
                ? $"找到 {candidates.Count} 个适配 MC {mc} 的版本"
                : $"平台上没有适配 MC {mc} 的版本",
                candidates.Count > 0 ? "T.Success" : "T.Warning");
            AddReport(verdict);
            var gap = new Border { Height = 8 };
            AddReport(gap);

            foreach (var c in candidates)
            {
                var title = UIKit.Text(c.ProjectTitle + " " + c.VersionNumber, 12, FontWeights.Medium);
                title.TextWrapping = TextWrapping.Wrap;
                var meta = UIKit.Sub($"{c.GameVersions} · {c.Loaders} · {StorageGuardService.FmtSize(c.Size)}", 10.5);
                meta.TextWrapping = TextWrapping.Wrap;
                meta.Margin = new Thickness(0, 2, 0, 0);
                var btn = PanelKit.Btn("下载安装", true, () => _ = InstallCandidateAsync(c, mc, loader, gen), 92);
                btn.VerticalAlignment = VerticalAlignment.Center;
                var grid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    Children = { UIKit.V(title, meta), btn.Col(1) }
                };
                AddReport(PanelKit.Row(grid, pad: 10));
            }
        }
        catch (Exception ex)
        {
            if (gen == _treeGen) SetReportTitle("版本匹配失败:" + ex.Message, "T.Danger");
            App.WriteAppLog($"[模组预览] 版本匹配异常:{ex}");
        }
        finally { _busy = false; }
    }

    private async Task InstallCandidateAsync(ModMatchCandidate c, string mc, string loader, int gen)
    {
        if (_busy || gen != _treeGen) return;
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == _instanceId);
        if (inst == null) return;
        if (!DialogKit.Confirm($"下载 {c.ProjectTitle} {c.VersionNumber} 到「{inst.Name}」的模组目录?\n原来的文件会保留,可手动删除。",
                "下载适配版本", Window.GetWindow(this))) return;
        _busy = true;
        SetReportTitle($"正在下载 {c.ProjectTitle} {c.VersionNumber}…");
        try
        {
            var (ok, msg) = await _diag.InstallCompatibleAsync(c, _instances.GetModsDir(inst.Id), mc, loader);
            if (gen != _treeGen) return;
            SetReportTitle(ok ? $"已下载:{c.FileName}" : "下载失败:" + msg, ok ? "T.Success" : "T.Danger");
            if (ok) RefreshRequested?.Invoke();
        }
        catch (Exception ex)
        {
            if (gen == _treeGen) SetReportTitle("下载异常:" + ex.Message, "T.Danger");
            App.WriteAppLog($"[模组预览] 适配版本下载异常:{ex}");
        }
        finally { _busy = false; }
    }

    // ==================== Axolotl-7 悬浮元数据卡片 ====================

    /// <summary>模组悬浮卡内容:作者、版本、依赖、简介,不点开详情弹窗就能看全</summary>
    public static Border BuildMetaCard(ModInfo mod)
    {
        var head = UIKit.Text(mod.DisplayName, 13, FontWeights.SemiBold);
        head.TextWrapping = TextWrapping.Wrap;
        var badges = UIKit.H(
            UIKit.Badge(mod.Enabled ? "已启用" : "已禁用", mod.Enabled ? "T.Success" : "T.ForegroundDim"),
            UIKit.Badge($"v{mod.VersionDisplay}", "T.Primary"));
        badges.Margin = new Thickness(0, 6, 0, 0);

        var rows = UIKit.V(
            PanelKit.KvRow("作者", string.IsNullOrWhiteSpace(mod.Author) ? "-" : Trim(mod.Author, 60)),
            PanelKit.KvRow("模组 ID", mod.ModIdDisplay),
            PanelKit.KvRow("加载器", mod.LoaderDisplay),
            PanelKit.KvRow("适配游戏", mod.McVersionDisplay),
            PanelKit.KvRow("必需前置", mod.RequiredDepsDisplay),
            PanelKit.KvRow("大小", mod.SizeDisplay));

        var desc = UIKit.Sub(string.IsNullOrWhiteSpace(mod.Description) ? "这个模组没有写简介" : Trim(mod.Description, 160), 11);
        desc.TextWrapping = TextWrapping.Wrap;
        desc.MaxWidth = 300;
        desc.Margin = new Thickness(0, 6, 0, 0);

        var card = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Panel),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Width = 340,
            Child = UIKit.V(head, badges, PanelKit.Divider(10, 6), rows, desc)
        };
        card.WithRef("T.Surface", "T.Border");
        card.Effect = UIKit.SoftShadow(0.4);
        return card;
    }

    private static string Trim(string s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
