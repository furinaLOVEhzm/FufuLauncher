// Copyright © FufuLauncher
//
// 日志智能筛选服务(Axolotl-4):
// 把游戏运行日志分成「崩溃 / 警告 / 信息」三类,支持关键词过滤、垃圾日志一键隐藏、
// 筛选条件收藏,以及崩溃时自动提取关键报错片段。
//
// 实现要点:
// 1. 纯字符串分类,零外部依赖,YesAI / NoAI 双版本行为完全一致;
// 2. 分类优先级 Crash > Warn > Info,一行只归一类,避免重复计数;
// 3. 垃圾日志用「模块级噪声清单 + 正则」双通道过滤,清单可关;
// 4. 崩溃片段提取按"锚点回溯":定位到异常首行后向前抓 6 行上下文、向后抓 24 行堆栈,
//    并额外把 Caused by 链条单独列出,方便直接贴给别人排错;
// 5. 收藏条件走 InstanceExtrasService 持久化(全局共享,不分实例)。

using System.Text;
using System.Text.RegularExpressions;

namespace FufuLauncher.Services;

/// <summary>日志行分类</summary>
public enum GameLogLevel
{
    /// <summary>崩溃 / 致命错误</summary>
    Crash,
    /// <summary>警告</summary>
    Warn,
    /// <summary>普通信息</summary>
    Info
}

/// <summary>分类后的一行日志</summary>
public sealed class LogLine
{
    /// <summary>在原始日志中的行号(1 起)</summary>
    public int Index { get; set; }
    /// <summary>原始整行(含 [HH:mm:ss] 时间戳前缀)</summary>
    public string Raw { get; set; } = "";
    /// <summary>剥掉时间戳后的正文(搜索匹配用它,避免关键词误命中时间)</summary>
    public string Body { get; set; } = "";
    public GameLogLevel Level { get; set; }
    /// <summary>命中的分类依据(供 UI 标注"为什么这条被判成崩溃")</summary>
    public string Tag { get; set; } = "";
    /// <summary>是否属于垃圾日志(被"一键隐藏"过滤掉)</summary>
    public bool Junk { get; set; }
    /// <summary>是否包含关键词高亮命中</summary>
    public bool KeywordHit { get; set; }
}

/// <summary>筛选条件(可收藏)</summary>
public sealed class LogFilterQuery
{
    public string Keyword { get; set; } = "";
    public bool ShowCrash { get; set; } = true;
    public bool ShowWarn { get; set; } = true;
    public bool ShowInfo { get; set; } = true;
    /// <summary>隐藏垃圾日志</summary>
    public bool HideJunk { get; set; } = true;
    /// <summary>只看命中的模组名(空 = 不限)</summary>
    public string OnlyModName { get; set; } = "";

    public LogFilterQuery Clone() => new()
    {
        Keyword = Keyword,
        ShowCrash = ShowCrash,
        ShowWarn = ShowWarn,
        ShowInfo = ShowInfo,
        HideJunk = HideJunk,
        OnlyModName = OnlyModName
    };
}

/// <summary>筛选结果</summary>
public sealed class LogFilterResult
{
    public List<LogLine> Lines { get; set; } = new();
    public int TotalLines { get; set; }
    public int CrashCount { get; set; }
    public int WarnCount { get; set; }
    public int InfoCount { get; set; }
    /// <summary>被"隐藏垃圾日志"过滤掉的行数</summary>
    public int JunkHidden { get; set; }
    public string Summary =>
        $"共 {TotalLines} 行 · 崩溃 {CrashCount} · 警告 {WarnCount} · 信息 {InfoCount}" +
        (JunkHidden > 0 ? $" · 已隐藏 {JunkHidden} 行噪声" : "") +
        $" · 当前显示 {Lines.Count} 行";
}

/// <summary>崩溃关键片段</summary>
public sealed class CrashExcerpt
{
    public bool Found { get; set; }
    /// <summary>一句话结论(弹窗标题用)</summary>
    public string Headline { get; set; } = "";
    /// <summary>提取出的关键片段(可直接复制)</summary>
    public string Text { get; set; } = "";
    /// <summary>异常链条(Caused by)</summary>
    public List<string> Causes { get; set; } = new();
    /// <summary>疑似出问题的模组(从堆栈里扫出的 modid)</summary>
    public List<string> SuspectMods { get; set; } = new();
    /// <summary>片段起止行号</summary>
    public int StartIndex { get; set; }
    public int EndIndex { get; set; }
}

public sealed class GameLogFilterService
{
    // ---------- 分类锚点 ----------

    /// <summary>崩溃级锚点(命中即判 Crash)</summary>
    private static readonly string[] CrashTokens =
    {
        "fatal", "crash", "crashed", "crash report", "exception", "error executing",
        "at net.minecraft", "at java.", "caused by:", "java.lang.", "nullpointerexception",
        "outofmemoryerror", "stackoverflowerror", "classnotfoundexception", "nosuchmethoderror",
        "nosuchfielderror", "noclassdeffounderror", "illegalstateexception", "illegalargumentexception",
        "unsupportedclassversionerror", "modloadingexception", "transformationexception",
        "---- minecraft crash report ----", "exit code", "exited with code",
        "failed to start", "could not launch", "launch failed", "jvm crash",
        "hs_err", "sigsegv", "sigabrt", "internal exception", "unrecoverable"
    };

    /// <summary>警告级锚点</summary>
    private static readonly string[] WarnTokens =
    {
        "warn", "warning", "[w]", "deprecated", "unsupported", "not compatible", "incompatible",
        "conflict", "duplicate", "missing", "failed to load", "couldn't load", "unable to load",
        "skipping", "fallback", "retry", "timed out", "timeout", "slow", "leak",
        "not found", "does not exist", "invalid", "malformed", "mismatch", "downgrade"
    };

    /// <summary>垃圾日志噪声清单(一键隐藏):刷屏但排错时几乎没用的行</summary>
    private static readonly string[] JunkTokens =
    {
        "[render thread/info]: sound engine started",
        "backend library: lwjgl",
        "narrator library for x64 successfully loaded",
        "openal initialized",
        "reloading resource packs",
        "applying holder lookups",
        "loaded 7 recipes",
        "loaded 1",
        "advancement loading",
        "registering dimension",
        "preparing start region",
        "chunk status",
        "region file",
        "mixinscript",
        "[main/info]: environment: authhost",
        "natives loaded",
        "setting user",
        "lwjgl version",
        "opengl version",
        "gl version",
        "glsl version",
        "shaderpack loaded",
        "successfully loaded",
        "texture stitched",
        "created: 1024x512",
        "created: 512x512",
        "created: 256x256",
        "found unifont",
        "using default channel",
        "realms: availability check"
    };

    /// <summary>时间戳前缀:[HH:mm:ss] 或 [HH:mm:ss] [线程/级别]</summary>
    private static readonly Regex TsRegex = new(@"^\s*\[\d{1,2}:\d{2}:\d{2}\]\s*(\[[^\]]{1,60}\]\s*)?", RegexOptions.Compiled);

    /// <summary>堆栈帧:at 开头 或 Caused by</summary>
    private static readonly Regex StackFrameRegex = new(@"^\s*(at\s+[\w$.<>]+\(|caused by:|\.\.\.\s+\d+\s+(more|common frames omitted))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>从堆栈里扫 modid 的候选(小写字母数字下划线,3~40 位)</summary>
    private static readonly Regex ModIdRegex = new(@"\b(?:net\.)?([a-z][a-z0-9_]{2,39})\.", RegexOptions.Compiled);

    /// <summary>已知 Minecraft / JVM 包前缀(扫嫌疑模组时排除,避免把整条堆栈都判成"模组")</summary>
    private static readonly HashSet<string> NoisePackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "java", "javax", "jdk", "sun", "com.google", "org.apache", "org.lwjgl", "net.minecraft",
        "net.minecraftforge", "net.fabricmc", "org.spongepowered", "it.unimi", "oshi", "com.mojang",
        "io.netty", "com.ibm", "org.slf4j", "net.neoforged", "org.quiltmc", "cpw.mods", "com.electronwill"
    };

    private readonly InstanceExtrasService _extras;

    public GameLogFilterService(InstanceExtrasService extras)
    {
        _extras = extras;
    }

    /// <summary>垃圾日志清单(供 UI 展示"隐藏了哪些类型")</summary>
    public static IReadOnlyList<string> JunkTokenList => JunkTokens;

    // ==================== 分类 ====================

    /// <summary>
    /// 把整份日志逐行分类。传入的每一行都会产出一个 LogLine(不在这一步做过滤,
    /// 过滤留给 Filter,方便复用同一份分类结果切换筛选条件)。
    /// </summary>
    public List<LogLine> Classify(IEnumerable<string> lines)
    {
        var result = new List<LogLine>();
        int idx = 0;
        foreach (string? line in lines)
        {
            idx++;
            string raw = line ?? "";
            if (raw.Length == 0) continue;
            var item = new LogLine { Index = idx, Raw = raw, Body = TsRegex.Replace(raw, "", 1).Trim() };

            string lower = item.Body.ToLowerInvariant();
            if (IsJunk(lower)) item.Junk = true;

            if (TryClassifyCrash(lower, out string crashTag))
            {
                item.Level = GameLogLevel.Crash;
                item.Tag = crashTag;
            }
            else if (TryClassifyWarn(lower, out string warnTag))
            {
                item.Level = GameLogLevel.Warn;
                item.Tag = warnTag;
            }
            else
            {
                item.Level = GameLogLevel.Info;
                item.Tag = "";
            }
            result.Add(item);
        }
        return result;
    }

    private static bool TryClassifyCrash(string lower, out string tag)
    {
        // 显式级别标记优先(Minecraft 日志格式:[线程/级别])
        if (lower.Contains("/fatal]") || lower.Contains("/error]") || lower.Contains("[fatal]") || lower.Contains("[error]"))
        {
            tag = lower.Contains("fatal") ? "FATAL" : "ERROR";
            return true;
        }
        foreach (string t in CrashTokens)
            if (lower.Contains(t)) { tag = t.ToUpperInvariant().Replace("AT ", "").Replace(":", ""); return true; }
        // 裸异常类名(XxxException / XxxError)
        if (lower.Contains("exception") || lower.Contains("error:")) { tag = "EXCEPTION"; return true; }
        tag = "";
        return false;
    }

    private static bool TryClassifyWarn(string lower, out string tag)
    {
        if (lower.Contains("/warn]") || lower.Contains("[warn]")) { tag = "WARN"; return true; }
        foreach (string t in WarnTokens)
            if (lower.Contains(t)) { tag = t.ToUpperInvariant(); return true; }
        tag = "";
        return false;
    }

    private static bool IsJunk(string lower)
    {
        foreach (string t in JunkTokens)
            if (lower.Contains(t)) return true;
        return false;
    }

    // ==================== 过滤 ====================

    /// <summary>按条件过滤已分类的行(纯内存操作,切条件时可反复调用)</summary>
    public LogFilterResult Filter(List<LogLine> classified, LogFilterQuery query)
    {
        var res = new LogFilterResult();
        if (classified == null) return res;
        query ??= new LogFilterQuery();

        string kw = (query.Keyword ?? "").Trim();
        string[] kwParts = kw.Length == 0 ? Array.Empty<string>()
            : kw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        string onlyMod = (query.OnlyModName ?? "").Trim();

        foreach (var line in classified)
        {
            res.TotalLines++;
            switch (line.Level)
            {
                case GameLogLevel.Crash: res.CrashCount++; break;
                case GameLogLevel.Warn: res.WarnCount++; break;
                default: res.InfoCount++; break;
            }

            if (query.HideJunk && line.Junk) { res.JunkHidden++; continue; }
            if (line.Level == GameLogLevel.Crash && !query.ShowCrash) continue;
            if (line.Level == GameLogLevel.Warn && !query.ShowWarn) continue;
            if (line.Level == GameLogLevel.Info && !query.ShowInfo) continue;

            if (onlyMod.Length > 0 && line.Body.IndexOf(onlyMod, StringComparison.OrdinalIgnoreCase) < 0) continue;

            line.KeywordHit = false;
            if (kwParts.Length > 0)
            {
                // 多关键词按"与"关系匹配(空格分隔),命中任一即高亮全部
                bool all = true;
                foreach (string part in kwParts)
                    if (line.Body.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) { all = false; break; }
                if (!all) continue;
                line.KeywordHit = true;
            }

            res.Lines.Add(line);
        }
        return res;
    }

    /// <summary>一步到位:原始日志 → 分类 → 过滤</summary>
    public LogFilterResult Run(IEnumerable<string> rawLines, LogFilterQuery query)
        => Filter(Classify(rawLines), query);

    // ==================== 崩溃片段提取 ====================

    /// <summary>
    /// 从日志里自动提取关键报错片段:定位第一处崩溃锚点,向前抓上下文、向后吞掉整段堆栈,
    /// 并单独列出 Caused by 链条与疑似出问题的模组。
    /// </summary>
    public CrashExcerpt ExtractCrash(IEnumerable<string> rawLines)
    {
        var ex = new CrashExcerpt();
        var lines = Classify(rawLines);
        if (lines.Count == 0) return ex;

        // 锚点选择:优先 Minecraft 崩溃报告头 / FATAL / OutOfMemory,其次任意 Crash 行
        int anchor = FindAnchor(lines);
        if (anchor < 0) return ex;

        int start = Math.Max(0, anchor - 6);
        int end = anchor;
        // 向后吞堆栈:连续的堆栈帧、Caused by、或仍是崩溃级的行都算片段的一部分
        for (int i = anchor; i < lines.Count && i < anchor + 60; i++)
        {
            end = i;
            bool isFrame = StackFrameRegex.IsMatch(lines[i].Body);
            bool isCrash = lines[i].Level == GameLogLevel.Crash;
            if (!isFrame && !isCrash && i > anchor + 2) break;
        }

        var sb = new StringBuilder();
        for (int i = start; i <= end && i < lines.Count; i++)
            sb.AppendLine(lines[i].Raw);

        ex.Found = true;
        ex.StartIndex = lines[start].Index;
        ex.EndIndex = lines[Math.Min(end, lines.Count - 1)].Index;
        ex.Text = sb.ToString().TrimEnd();
        ex.Headline = BuildHeadline(lines[anchor]);

        foreach (var l in lines.Skip(start).Take(end - start + 1))
        {
            string body = l.Body.Trim();
            if (body.StartsWith("caused by", StringComparison.OrdinalIgnoreCase))
                ex.Causes.Add(TrimTo(body, 220));
            foreach (string m in ScanModIds(body))
                if (!ex.SuspectMods.Contains(m)) ex.SuspectMods.Add(m);
        }
        // 锚点本身也扫一遍 modid
        foreach (string m in ScanModIds(lines[anchor].Body))
            if (!ex.SuspectMods.Contains(m)) ex.SuspectMods.Add(m);

        App.WriteAppLog($"[日志筛选] 提取崩溃片段:{ex.Headline}(第 {ex.StartIndex}~{ex.EndIndex} 行,Caused by {ex.Causes.Count} 段,疑似模组 {ex.SuspectMods.Count} 个)");
        return ex;
    }

    /// <summary>崩溃锚点定位:高价值标记优先,兜底取最后一条崩溃行(最接近真实退出点)</summary>
    private static int FindAnchor(List<LogLine> lines)
    {
        string[] priorities =
        {
            "---- minecraft crash report ----",
            "outofmemoryerror",
            "modloadingexception",
            "unsupportedclassversionerror",
            "hs_err",
            "fatal",
            "crash report"
        };
        foreach (string p in priorities)
        {
            int hit = lines.FindIndex(l => l.Body.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
            if (hit >= 0) return hit;
        }
        for (int i = lines.Count - 1; i >= 0; i--)
            if (lines[i].Level == GameLogLevel.Crash) return i;
        return -1;
    }

    private static string BuildHeadline(LogLine anchor)
    {
        string body = anchor.Body.Trim();
        if (body.Length == 0) body = anchor.Raw.Trim();
        return TrimTo(body, 160);
    }

    /// <summary>从一行堆栈里扫出疑似 modid(排除 java/minecraft/forge 等已知包)</summary>
    private static List<string> ScanModIds(string body)
    {
        var found = new List<string>();
        if (body.Length == 0) return found;
        foreach (Match m in ModIdRegex.Matches(body))
        {
            string id = m.Groups[1].Value;
            if (id.Length < 3) continue;
            // 整行以已知包开头则跳过(避免把 net.minecraft.xxx 拆成一堆假 modid)
            bool noisy = false;
            foreach (string np in NoisePackages)
                if (body.IndexOf(np, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    body.IndexOf(np, StringComparison.OrdinalIgnoreCase) < m.Index)
                { noisy = true; break; }
            if (noisy) continue;
            if (found.Contains(id)) continue;
            found.Add(id);
            if (found.Count >= 6) break;
        }
        return found;
    }

    private static string TrimTo(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    // ==================== 筛选条件收藏 ====================

    /// <summary>已收藏的筛选条件</summary>
    public List<LogFilterPreset> Presets() => _extras.LogFilterPresets();

    /// <summary>把当前筛选条件存成收藏。返回 null = 名字为空或重名</summary>
    public (LogFilterPreset? Saved, string Message) SavePreset(string name, LogFilterQuery query)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return (null, "请先给这个筛选条件起个名字。");
        if (query == null) return (null, "当前没有可保存的筛选条件。");

        var saved = _extras.AddLogFilterPreset(new LogFilterPreset
        {
            Name = name,
            Keyword = query.Keyword ?? "",
            ShowCrash = query.ShowCrash,
            ShowWarn = query.ShowWarn,
            ShowInfo = query.ShowInfo,
            HideJunk = query.HideJunk
        });
        if (saved == null) return (null, $"已经有一个叫「{name}」的收藏了,换个名字吧。");
        App.WriteAppLog($"[日志筛选] 已收藏筛选条件「{name}」:关键词={saved.Keyword},隐藏噪声={saved.HideJunk}");
        return (saved, $"筛选条件「{name}」已收藏。");
    }

    public bool DeletePreset(string id) => _extras.DeleteLogFilterPreset(id);

    /// <summary>把收藏还原成筛选条件</summary>
    public static LogFilterQuery FromPreset(LogFilterPreset preset) => new()
    {
        Keyword = preset.Keyword ?? "",
        ShowCrash = preset.ShowCrash,
        ShowWarn = preset.ShowWarn,
        ShowInfo = preset.ShowInfo,
        HideJunk = preset.HideJunk
    };
}
