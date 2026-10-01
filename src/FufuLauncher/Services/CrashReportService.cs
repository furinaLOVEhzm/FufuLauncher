// Copyright © FufuLauncher
//
// 崩溃报告服务(Celestial-4 一键复制 + Axolotl-4 关键报错提取 + BlockHelm-6 崩溃原因摘要):
// 游戏崩了以后,把 crash-reports 目录里的报告与 logs\latest.log 的关键片段整理成
// 一段可以直接粘给别人的纯文本,并给出「一句话崩溃原因」用于启动记录列表展示。
//
// 实现要点:
// 1. 复制内容是纯文本,不带任何富文本格式,贴到 QQ / 论坛 / issue 都不会花;
// 2. 头部自动带上实例名、游戏版本、加载器、Java 路径与内存设置 —— 排错时这些信息比堆栈还关键,
//    省掉对方反问一轮;
// 3. Clipboard 只能在 UI 线程访问,服务层只做文本组装,真正写剪贴板前判一次 Dispatcher;
// 4. 单个报告读取失败不影响其它报告列出,坏文件如实标注"读不出来"。

using System.IO;
using System.Text;
using System.Windows;

namespace FufuLauncher.Services;

/// <summary>一份崩溃报告</summary>
public sealed class CrashReportInfo
{
    public string FilePath { get; set; } = "";
    public string FileName => Path.GetFileName(FilePath);
    public DateTime CrashTime { get; set; }
    /// <summary>报告第一行的标题(通常含崩溃描述)</summary>
    public string Title { get; set; } = "";
    public long Size { get; set; }
    public string SizeDisplay => StorageGuardService.FmtSize(Size);
    public string TimeDisplay => CrashTime == default ? "-" : CrashTime.ToString("yyyy-MM-dd HH:mm:ss");
    /// <summary>报告全文(懒读,列表里不加载)</summary>
    public string FullText { get; set; } = "";
    /// <summary>Caused by 链条</summary>
    public List<string> Causes { get; set; } = new();
    /// <summary>从堆栈里扫出来的可疑模组 id</summary>
    public List<string> SuspectMods { get; set; } = new();
    /// <summary>一句话崩溃原因(BlockHelm-6 启动记录用)</summary>
    public string Reason { get; set; } = "";
    public string Display => $"{TimeDisplay} · {FileName} · {(string.IsNullOrEmpty(Title) ? "无标题" : Title)}";
}

public sealed class CrashReportService
{
    /// <summary>复制文本的最大长度(超过就截断,避免几 MB 的日志把剪贴板撑爆)</summary>
    public const int MaxCopyChars = 400_000;

    private readonly InstanceService _instances;
    private readonly GameLogFilterService _logFilter;

    public CrashReportService(InstanceService instances, GameLogFilterService logFilter)
    {
        _instances = instances;
        _logFilter = logFilter;
    }

    /// <summary>实例的 crash-reports 目录</summary>
    public string CrashDir(string instanceId) => Path.Combine(_instances.GetInstanceDir(instanceId), "crash-reports");

    /// <summary>实例的 logs 目录</summary>
    public string LogsDir(string instanceId) => Path.Combine(_instances.GetInstanceDir(instanceId), "logs");

    /// <summary>latest.log 路径</summary>
    public string LatestLogPath(string instanceId) => Path.Combine(LogsDir(instanceId), "latest.log");

    // ==================== 列表 ====================

    /// <summary>列出该实例的全部崩溃报告(按时间倒序)</summary>
    public List<CrashReportInfo> ListReports(string instanceId)
    {
        var list = new List<CrashReportInfo>();
        string dir = CrashDir(instanceId);
        if (!Directory.Exists(dir)) return list;
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "crash-*.txt"))
            {
                var info = new CrashReportInfo { FilePath = f };
                try
                {
                    var fi = new FileInfo(f);
                    info.Size = fi.Length;
                    info.CrashTime = fi.LastWriteTime;
                    // 只读前 4KB 取标题与原因,列表页不整份加载
                    info.Title = ReadHead(f, 4096, out string head);
                    info.Reason = SummarizeText(head);
                    // 只跑一次提取,同时拿到 Caused by 链与可疑模组
                    var excerpt = _logFilter.ExtractCrash(head.Split('\n'));
                    info.Causes = excerpt.Causes;
                    info.SuspectMods = excerpt.SuspectMods;
                    if (string.IsNullOrEmpty(info.Reason) && excerpt.Found)
                        info.Reason = TrimTo(excerpt.Causes.FirstOrDefault() ?? excerpt.Headline, 180);
                }
                catch (Exception ex)
                {
                    info.Title = "读取失败:" + ex.Message;
                    App.WriteAppLog($"[崩溃报告] 读取失败 {f}:{ex.Message}");
                }
                list.Add(info);
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[崩溃报告] 目录读取失败 {dir}:{ex.Message}");
        }
        return list.OrderByDescending(r => r.CrashTime).ToList();
    }

    /// <summary>最近一份崩溃报告(没有则返回 null)</summary>
    public CrashReportInfo? LatestReport(string instanceId) => ListReports(instanceId).FirstOrDefault();

    /// <summary>读取指定报告全文</summary>
    public string ReadFull(CrashReportInfo? report)
    {
        if (report == null || !File.Exists(report.FilePath)) return "";
        if (!string.IsNullOrEmpty(report.FullText)) return report.FullText;
        try
        {
            report.FullText = File.ReadAllText(report.FilePath);
            return report.FullText;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[崩溃报告] 全文读取失败 {report.FilePath}:{ex.Message}");
            return "";
        }
    }

    // ==================== 日志关键片段(Axolotl-4) ====================

    /// <summary>从 latest.log 里自动提取关键报错片段(游戏崩溃时单独展示用)</summary>
    public CrashExcerpt ExtractFromLatestLog(string instanceId)
    {
        string path = LatestLogPath(instanceId);
        if (!File.Exists(path)) return new CrashExcerpt { Found = false };
        try
        {
            var lines = ReadTailLines(path, 20000);
            return _logFilter.ExtractCrash(lines);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[崩溃报告] latest.log 解析失败 {path}:{ex.Message}");
            return new CrashExcerpt { Found = false };
        }
    }

    /// <summary>
    /// 一句话崩溃原因(BlockHelm-6 启动记录列表用)。
    /// 优先取 crash-reports 最新一份,没有再退回 latest.log。
    /// </summary>
    public string SummarizeReason(string instanceId)
    {
        var latest = LatestReport(instanceId);
        if (latest != null && !string.IsNullOrEmpty(latest.Reason)) return latest.Reason;
        var excerpt = ExtractFromLatestLog(instanceId);
        if (excerpt.Found)
        {
            string r = excerpt.Causes.FirstOrDefault() ?? excerpt.Headline;
            return TrimTo(r, 180);
        }
        return "";
    }

    // ==================== 一键复制(Celestial-4) ====================

    /// <summary>
    /// 组装用于复制的完整纯文本:实例信息头 + 崩溃报告全文(没有报告就附 latest.log 关键片段)。
    /// </summary>
    public string BuildCopyText(string instanceId, CrashReportInfo? report = null, bool includeLatestLog = true)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        var sb = new StringBuilder();
        sb.AppendLine("===== FufuLauncher 崩溃信息 =====");
        sb.AppendLine($"实例名称:{inst?.Name ?? instanceId}");
        sb.AppendLine($"实例 Id:{instanceId}");
        sb.AppendLine($"游戏版本:{inst?.VersionId ?? "未知"}");
        if (!string.IsNullOrEmpty(inst?.ModLoader))
            sb.AppendLine($"加载器:{inst!.ModLoader} {inst.ModLoaderVersion}");
        if (!string.IsNullOrEmpty(inst?.LoaderVersionId))
            sb.AppendLine($"加载器版本 Id:{inst.LoaderVersionId}");
        sb.AppendLine($"Java:{(inst?.JavaMajorVersion ?? 0)} ({(string.IsNullOrEmpty(inst?.JavaPath) ? "未指定" : inst!.JavaPath)})");
        sb.AppendLine($"内存:-Xms{inst?.Xms ?? 0}m -Xmx{inst?.Xmx ?? 0}m{(inst?.UseCustomMemory == true ? "(单版本自定义)" : "(全局策略)")}");
        if (!string.IsNullOrEmpty(inst?.ExtraJvmArgs))
            sb.AppendLine($"额外 JVM 参数:{inst!.ExtraJvmArgs}");
        sb.AppendLine($"导出时间:{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        string body = ReadFull(report ?? LatestReport(instanceId));
        if (!string.IsNullOrEmpty(body))
        {
            sb.AppendLine("===== 崩溃报告(crash-reports) =====");
            sb.AppendLine(body.TrimEnd());
            sb.AppendLine();
        }

        if (includeLatestLog)
        {
            var excerpt = ExtractFromLatestLog(instanceId);
            if (excerpt.Found)
            {
                sb.AppendLine("===== 游戏日志关键报错片段(logs/latest.log) =====");
                sb.AppendLine(excerpt.Text.TrimEnd());
                if (excerpt.SuspectMods.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("可疑模组(从堆栈里扫出):" + string.Join("、", excerpt.SuspectMods));
                }
                sb.AppendLine();
            }
            else if (string.IsNullOrEmpty(body))
            {
                sb.AppendLine("(没找到崩溃报告,latest.log 里也没扫到明确报错。可以手动把 logs 文件夹一起发给对方。)");
            }
        }

        string text = sb.ToString();
        if (text.Length > MaxCopyChars)
            text = text[..MaxCopyChars] + $"\n\n…(内容过长已截断,完整文件在 {CrashDir(instanceId)})";
        return text;
    }

    /// <summary>
    /// 复制崩溃信息到剪贴板。必须在 UI 线程调用;不在 UI 线程时返回失败提示,由调用方切线程。
    /// </summary>
    public (bool Ok, string Message) CopyToClipboard(string instanceId, CrashReportInfo? report = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            return (false, "复制必须在界面线程执行,请重试。");
        try
        {
            string text = BuildCopyText(instanceId, report);
            if (string.IsNullOrWhiteSpace(text))
                return (false, "没有可复制的崩溃信息:这个实例还没产生过崩溃报告。");
            Clipboard.SetText(text);
            App.WriteAppLog($"[崩溃报告] ✓ 已复制崩溃信息到剪贴板({instanceId},{text.Length} 字符)");
            return (true, $"已复制完整崩溃信息({text.Length} 字符),直接粘贴发给对方就行。");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[崩溃报告] ✗ 复制到剪贴板失败:{ex.Message}");
            return (false, "复制失败:" + ex.Message + "\n可以点「打开崩溃报告文件夹」手动把文件发过去。");
        }
    }

    /// <summary>把崩溃信息存成 txt 文件(剪贴板被别的程序占着时的退路)</summary>
    public (bool Ok, string Message, string Path) SaveCopyText(string instanceId, string outputPath)
    {
        try
        {
            string text = BuildCopyText(instanceId);
            string dir = System.IO.Path.GetDirectoryName(outputPath) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, text, new UTF8Encoding(false));
            return (true, $"已保存到 {outputPath}", outputPath);
        }
        catch (Exception ex)
        {
            return (false, "保存失败:" + StorageGuardService.ClassifyIoError(ex, "写崩溃信息文件"), "");
        }
    }

    /// <summary>用资源管理器打开崩溃报告目录</summary>
    public bool OpenCrashFolder(string instanceId)
    {
        string dir = CrashDir(instanceId);
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[崩溃报告] 打开目录失败 {dir}:{ex.Message}");
            return false;
        }
    }

    // ==================== 内部工具 ====================

    /// <summary>读文件开头若干字节,返回第一行作为标题,head 输出读到的全部内容</summary>
    private static string ReadHead(string path, int maxBytes, out string head)
    {
        head = "";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int len = (int)Math.Min(maxBytes, fs.Length);
        var buf = new byte[len];
        int read = fs.Read(buf, 0, len);
        head = Encoding.UTF8.GetString(buf, 0, read);
        foreach (string line in head.Split('\n'))
        {
            string t = line.Trim();
            // 跳过 Minecraft 报告的固定抬头空行,取第一行有内容的
            if (t.Length > 0 && t != "---- Minecraft Crash Report ----") return TrimTo(t, 160);
        }
        return "";
    }

    /// <summary>从报告文本里抠一句话原因</summary>
    private static string SummarizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            // 报告格式:Description: xxx / 后面跟着 Exception 全名
            if (line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                return TrimTo(line["Description:".Length..].Trim(), 180);
            if (line.StartsWith("Exception:", StringComparison.OrdinalIgnoreCase))
                return TrimTo(line, 180);
            if (line.Contains("Caused by:", StringComparison.OrdinalIgnoreCase))
                return TrimTo(line, 180);
        }
        return "";
    }

    /// <summary>读文件末尾 N 行(日志可能几百 MB,绝不整份读进内存)</summary>
    private static List<string> ReadTailLines(string path, int maxLines)
    {
        var lines = new List<string>();
        try
        {
            long len = new FileInfo(path).Length;
            // 平均每行按 160 字节估,最多读 8MB 尾巴
            long take = Math.Min(len, Math.Min(8L * 1024 * 1024, maxLines * 160L));
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(Math.Max(0, len - take), SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                lines.Add(line);
                if (lines.Count > maxLines) lines.RemoveAt(0);
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[崩溃报告] 日志尾部读取失败 {path}:{ex.Message}");
        }
        return lines;
    }

    private static string TrimTo(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
