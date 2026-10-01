// Copyright © FufuLauncher
//
// JVM 参数预设模板库(Celestial-2):
// 内置几套常用模板(性能优化 / GC 优化 / 低内存设备 / 多核吞吐 / 原版轻量),
// 用户可把自己调好的参数保存成自定义模板,随时调用套用。
//
// 实现要点:
// 1. 内置模板不入库(每次按本机 CPU 核心数与 Java 主版本动态生成),避免"存下来后换机器就错";
// 2. GC 选型与线程数推导完全对齐 MemoryMonitorService.BuildMultiCoreGcArgs,不另起一套算法;
// 3. 参数串支持 %CORES% 占位符,套用时替换为真实核心数;
// 4. 自定义模板走 InstanceExtrasService 持久化(根级 fufu-extras.json);
// 5. 套用前用 JvmArgsValidator 校验,拒绝 -Xms/-Xmx(内存归内存设置管,避免两处互相覆盖)。

using System.Text.RegularExpressions;

namespace FufuLauncher.Services;

/// <summary>模板套用结果</summary>
public sealed class JvmTemplateApplyResult
{
    public bool Ok { get; set; }
    public string Args { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>被模板丢弃的参数(如 -Xmx 之类由启动器统一管理的项)</summary>
    public List<string> Dropped { get; set; } = new();
}

public sealed class JvmTemplateService
{
    /// <summary>%CORES% 占位符:套用时替换为本机逻辑核心数</summary>
    public const string CoresToken = "%CORES%";

    private static readonly Regex CoresTokenRegex = new(Regex.Escape(CoresToken), RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly InstanceExtrasService _extras;
    private readonly ConfigService _config;

    public JvmTemplateService(InstanceExtrasService extras, ConfigService config)
    {
        _extras = extras;
        _config = config;
    }

    // ==================== 内置模板 ====================

    /// <summary>
    /// 全部可用模板 = 内置模板(动态生成) + 用户自定义模板。
    /// 内置在前、自定义在后,便于 UI 直接铺列表。
    /// </summary>
    public List<JvmTemplate> AllTemplates(int? javaMajorOverride = null)
    {
        var list = BuiltInTemplates(javaMajorOverride ?? _config.Config.JavaVersion);
        list.AddRange(_extras.CustomJvmTemplates());
        return list;
    }

    /// <summary>
    /// 内置模板(5 套)。GC 与线程数按 javaMajor + 核心数实时算,不落盘。
    /// </summary>
    public List<JvmTemplate> BuiltInTemplates(int javaMajor)
    {
        if (javaMajor <= 0) javaMajor = 17;
        int cores = MemoryMonitorService.GetPhysicalCoreCount();
        string gc = MemoryMonitorService.BuildMultiCoreGcArgs(cores, javaMajor);

        return new List<JvmTemplate>
        {
            new()
            {
                Id = "builtin-perf",
                Name = "性能优化",
                BuiltIn = true,
                MinJavaMajor = javaMajor >= 17 ? 17 : 8,
                Description = $"按本机 {cores} 核与 Java {javaMajor} 生成:多核 GC + JIT 分层编译预热,大型整合包首选",
                Args = $"{gc} -XX:+UnlockExperimentalVMOptions -XX:+AlwaysActAsServerClassMachine " +
                       "-XX:MaxInlineLevel=15 -XX:+TieredCompilation -Dfml.ignorePatchDiscrepancies=true"
            },
            new()
            {
                Id = "builtin-gc",
                Name = "GC 优化",
                BuiltIn = true,
                MinJavaMajor = javaMajor >= 17 ? 17 : 8,
                Description = $"只调垃圾回收:Java {javaMajor} 选用 {(GcName(javaMajor))},压低停顿,缓解光影堆外内存只涨不放",
                Args = gc + PeriodicGcArgs(javaMajor)
            },
            new()
            {
                Id = "builtin-lowmem",
                Name = "低内存设备",
                BuiltIn = true,
                MinJavaMajor = 8,
                Description = "内存紧张机型:关掉预提交、缩小元空间与线程栈,尽量省内存,不适合大型光影",
                Args = "-XX:+UseSerialGC -XX:MaxMetaspaceSize=256m -XX:CompressedClassSpaceSize=64m " +
                       "-Xss512k -XX:+DisableExplicitGC -Dsun.java2d.d3d=false"
            },
            new()
            {
                Id = "builtin-multicore",
                Name = "多核吞吐",
                BuiltIn = true,
                MinJavaMajor = javaMajor >= 17 ? 17 : 8,
                Description = $"把 {cores} 个核心尽量吃满:并行 GC + 多编译线程 + 大堆区域,适合高配机器跑超大型整合包",
                Args = MultiCoreArgs(cores, javaMajor)
            },
            new()
            {
                Id = "builtin-vanilla",
                Name = "原版轻量",
                BuiltIn = true,
                MinJavaMajor = 8,
                Description = "纯净版/小型整合包:只留最稳妥的通用优化,兼容性最好,老机器也能用",
                Args = "-XX:+UseG1GC -XX:MaxGCPauseMillis=100 -XX:+DisableExplicitGC " +
                       "-Dfml.ignoreInvalidMinecraftCertificates=true -Dfml.ignorePatchDiscrepancies=true"
            }
        };
    }

    /// <summary>GC 名称(与 MemoryMonitorService 的选型保持一致)</summary>
    public static string GcName(int javaMajor) =>
        javaMajor >= 21 ? "ZGC 分代模式" : javaMajor >= 17 ? "ZGC" : "G1GC";

    /// <summary>周期 GC 参数:G1 分支(Java 12~16)用 G1PeriodicGC*,ZGC 分支用 ZCollectionInterval</summary>
    private static string PeriodicGcArgs(int javaMajor) =>
        javaMajor >= 17 ? " -XX:ZCollectionInterval=120 -XX:ZUncommitDelay=180"
      : javaMajor >= 12 ? " -XX:G1PeriodicGCInterval=10000 -XX:+G1PeriodicGCInvokesConcurrent"
      : "";

    /// <summary>多核吞吐参数:线程数推导公式与 MemoryMonitorService 完全一致</summary>
    private static string MultiCoreArgs(int cores, int javaMajor)
    {
        int parallel = Math.Max(1, (int)Math.Ceiling(cores * 5.0 / 8.0));
        int conc = Math.Max(1, parallel / 4);
        int ci = Math.Max(2, cores / 4);
        string gc = javaMajor >= 21 ? "-XX:+UseZGC -XX:+ZGenerational"
                  : javaMajor >= 17 ? "-XX:+UseZGC"
                  : "-XX:+UseG1GC -XX:G1HeapRegionSize=16m";
        return $"{gc} -XX:ParallelGCThreads={parallel} -XX:ConcGCThreads={conc} -XX:CICompilerCount={ci} " +
               "-XX:+AlwaysActAsServerClassMachine -XX:MaxInlineLevel=15" +
               (javaMajor >= 12 && javaMajor < 17 ? " -XX:+G1PeriodicGCInvokesConcurrent" : "");
    }

    // ==================== 自定义模板 ====================

    /// <summary>保存自定义模板。返回 null 表示名字为空 / 参数为空 / 重名</summary>
    public (JvmTemplate? Saved, string Message) AddCustomTemplate(string name, string description, string args, int minJavaMajor = 8)
    {
        name = (name ?? "").Trim();
        args = (args ?? "").Trim();
        if (name.Length == 0) return (null, "请先给模板起个名字。");
        if (name.Length > 32) return (null, "模板名字太长了,最多 32 个字。");
        if (args.Length == 0) return (null, "参数内容是空的,没法保存模板。");

        // 语法先过一遍,避免存进去一个根本跑不起来的模板
        var (ok, err) = JvmArgsValidator.Validate(args);
        if (!ok) return (null, "参数写法有问题:" + err);

        var saved = _extras.AddJvmTemplate(name, description ?? "", args, Math.Clamp(minJavaMajor, 8, 25));
        if (saved == null) return (null, $"已经有一个叫「{name}」的模板了,换个名字吧。");
        App.WriteAppLog($"[JVM模板] 已保存自定义模板「{name}」:{args}");
        return (saved, $"模板「{name}」已保存。");
    }

    public bool DeleteCustomTemplate(string id)
    {
        if (_extras.DeleteJvmTemplate(id))
        {
            App.WriteAppLog($"[JVM模板] 已删除自定义模板 {id}");
            return true;
        }
        return false;
    }

    // ==================== 套用 ====================

    /// <summary>
    /// 展开模板参数:替换 %CORES% 占位符、剔除 -Xms/-Xmx(内存归内存设置统一管)、
    /// 去掉与现有参数重复的项,返回可直接填进「启动优化参数」框的字符串。
    /// </summary>
    public JvmTemplateApplyResult Expand(JvmTemplate template, string? existingArgs, int javaMajor)
    {
        var res = new JvmTemplateApplyResult();
        if (template == null)
        {
            res.Message = "模板不存在。";
            return res;
        }

        int cores = MemoryMonitorService.GetPhysicalCoreCount();
        string raw = CoresTokenRegex.Replace(template.Args ?? "", cores.ToString());

        var kept = new List<string>();
        foreach (string t in SplitArgs(raw))
        {
            string lower = t.Trim('"').ToLowerInvariant();
            // 内存上下限由「游戏内存」设置项统一管理,模板里带了会互相覆盖,直接剔除并告知
            if (lower.StartsWith("-xms") || lower.StartsWith("-xmx"))
            {
                res.Dropped.Add(t.Trim('"'));
                continue;
            }
            kept.Add(t);
        }

        // 与现有参数去重(保留现有值,避免同一开关写两遍)
        if (!string.IsNullOrWhiteSpace(existingArgs))
        {
            var existing = new HashSet<string>(SplitArgs(existingArgs).Select(x => x.Trim('"')),
                                               StringComparer.OrdinalIgnoreCase);
            for (int i = kept.Count - 1; i >= 0; i--)
                if (existing.Contains(kept[i].Trim('"'))) kept.RemoveAt(i);
        }

        res.Args = string.Join(" ", kept);
        res.Ok = res.Args.Length > 0;
        res.Message = res.Ok
            ? $"已套用模板「{template.Name}」" + (res.Dropped.Count > 0 ? $",其中 {res.Dropped.Count} 项被跳过(内存参数请使用「游戏内存」设置项调整)。" : "。")
            : $"模板「{template.Name}」没有带来新参数(全被跳过了)。";

        App.WriteAppLog($"[JVM模板] 套用「{template.Name}」→ {res.Args}(跳过 {res.Dropped.Count} 项)");
        return res;
    }

    /// <summary>模板对 Java 版本的兼容性提示(不兼容返回中文提示,兼容返回 null)</summary>
    public static string? VersionWarning(JvmTemplate template, int javaMajor)
    {
        if (template == null || javaMajor <= 0) return null;
        return template.MinJavaMajor > javaMajor
            ? $"这个模板按 Java {template.MinJavaMajor}+ 设计,当前是 Java {javaMajor},部分参数可能不被识别导致启动失败。"
            : null;
    }

    /// <summary>按空白切分参数,双引号内的空白视为整体(与 JvmArgsValidator 同款规则)</summary>
    private static List<string> SplitArgs(string args)
    {
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuote = false;
        foreach (char c in args)
        {
            if (c == '"') { inQuote = !inQuote; cur.Append(c); continue; }
            if (!inQuote && char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }
}
