// Copyright © FufuLauncher
//
// 内存配置智能推荐服务(BlockHelm-4):
// 读取本机物理内存 → 自动算出 Xms / Xmx 推荐值 → 一键套用到指定游戏版本,
// 同时完整保留用户手动输入修改内存的能力(套用只是把推荐值写进输入框,不是锁死)。
//
// 实现要点:
// 1. 天花板计算全部复用 MemoryMonitorService(已有的档位表 + 系统预留红线 + 实时可用内存硬约束),
//    不另造一套内存策略,避免和启动时的实时复验互相打架;
// 2. Xms 取 Xmx 的一半(512MB 起、4GB 封顶):太低会让 JVM 运行中反复扩容产生停顿,
//    太高则在游戏还没吃满内存时白占物理内存,一半是主流启动器的通行折中;
// 3. 模组数量与整合包自带建议内存会向上微调 Xmx,但绝不突破实时可用内存天花板;
// 4. 32 位 java.exe 堆上限约 1.5GB,识别到就把 Xmx 压到 1280MB 并给出换 64 位 Java 的提示;
// 5. 推荐值全部 256MB 对齐,减少 GC 碎片。

using System.IO;

namespace FufuLauncher.Services;

/// <summary>内存推荐结果</summary>
public sealed class MemoryRecommendation
{
    /// <summary>推荐的初始堆(MB)</summary>
    public int Xms { get; set; }
    /// <summary>推荐的最大堆(MB)</summary>
    public int Xmx { get; set; }
    /// <summary>本机物理内存总量(MB)</summary>
    public long TotalMb { get; set; }
    /// <summary>当前实时可用内存(MB)</summary>
    public long AvailableMb { get; set; }
    /// <summary>系统预留红线(MB)</summary>
    public int ReserveMb { get; set; }
    /// <summary>整机档位描述</summary>
    public string Tier { get; set; } = "";
    /// <summary>推荐依据说明(UI 直接展示,让用户知道这个数怎么来的)</summary>
    public string Reason { get; set; } = "";
    /// <summary>非空即需要提醒用户(可用内存吃紧 / 32 位 Java 等)</summary>
    public string Warning { get; set; } = "";
    /// <summary>推荐值是否被实时可用内存压低过</summary>
    public bool Capped { get; set; }
    /// <summary>等效 JVM 参数写法</summary>
    public string Display => $"-Xms{Xms}m -Xmx{Xmx}m";
    public string TotalDisplay => TotalMb >= 1024 ? $"{TotalMb / 1024.0:F1} GB" : $"{TotalMb} MB";
}

public sealed class MemoryRecommendService
{
    /// <summary>Xms 下限(MB)</summary>
    public const int MinXms = 512;
    /// <summary>Xms 上限(MB):初始堆超过 4GB 没有收益,只会白占物理内存</summary>
    public const int MaxXms = 4096;
    /// <summary>32 位 JVM 的安全堆上限(MB)</summary>
    public const int X86HeapCeiling = 1280;

    private readonly MemoryMonitorService _memory;
    private readonly InstanceService _instances;
    private readonly ConfigService _config;

    public MemoryRecommendService(MemoryMonitorService memory, InstanceService instances, ConfigService config)
    {
        _memory = memory;
        _instances = instances;
        _config = config;
    }

    /// <summary>本机物理内存总量(MB)</summary>
    public long TotalPhysicalMb => _memory.GetCurrent().TotalBytes / (1024L * 1024);

    /// <summary>整机档位中文名</summary>
    public static string TierOf(long totalMb)
    {
        // 硬件保留普遍少报约 3%,按校正后的值分档,免得 8GB 机型被说成"4GB 档"
        long tier = totalMb + totalMb * 3 / 100;
        return tier < 4096 ? "入门(不足 4GB)"
             : tier < 8192 ? "主流(8GB)"
             : tier < 17000 ? "充裕(16GB)"   // 2026-09-26 档位线上调:15.8GB+3% 校正≈16.3GB,原来压过 16GB 线被误标 32GB 档
             : tier < 32768 ? "高端(32GB)"
             : "发烧(64GB 及以上)";
    }

    /// <summary>
    /// 为指定游戏版本算推荐内存。modCount 传实例当前模组数量可让推荐更贴合实际负载。
    /// </summary>
    public MemoryRecommendation Recommend(string instanceId, int modCount = -1)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        bool heavy = inst != null && HasShaderPacks(inst.Id);
        return RecommendCore(inst?.RecommendedMemoryMb ?? 0, modCount, inst?.JavaPath ?? "", heavy);
    }

    /// <summary>通用推荐(不绑定实例)</summary>
    public MemoryRecommendation RecommendCore(int packSuggestedMb, int modCount, string javaPath, bool heavyGraphics = false)
    {
        var info = _memory.GetCurrent();
        long totalMb = info.TotalBytes / (1024L * 1024);
        long availMb = info.AvailableBytes / (1024L * 1024);
        int reserve = _memory.ReserveMb();
        int safeAlloc = _memory.GetSafeAllocMb();

        // 基线:负载感知智能分配(基础档位 + 模组/整合包/光影升档 + 天花板,与启动时单一来源一致,
        // 2026-09-25 去重:原实现此处再叠一次 bump 会与 CalculateSmartXmx 内部升档双倍叠加)
        int xmx = _memory.CalculateSmartXmx(modCount, packSuggestedMb, heavyGraphics);
        int baseline = xmx;
        var reasons = new List<string>
        {
            $"本机物理内存 {Fmt(totalMb)},当前可用 {Fmt(availMb)},系统预留 {Fmt(reserve)}"
        };
        if (packSuggestedMb > 0) reasons.Add($"整合包建议内存 {Fmt(packSuggestedMb)},已纳入自动分配");
        if (modCount > 0) reasons.Add($"已装 {modCount} 个模组,已按负载升档");
        if (heavyGraphics) reasons.Add("检测到光影/重负载,已自动升档");

        // 实时天花板复验:推荐值可以比基线高,但绝不能突破"当前可用 − 系统预留"
        bool capped = false;
        if (xmx > safeAlloc)
        {
            xmx = Math.Max(0, safeAlloc);
            capped = xmx < baseline;
            reasons.Add($"受当前可用内存限制,压到 {Fmt(xmx)}");
        }
        // 用户设置的上限同样不可破
        int userMax = Math.Max(MinXms, _config.Config.AutoMemoryMaxMb);
        if (xmx > userMax) { xmx = userMax; capped = true; }

        xmx = Align256(xmx);
        if (xmx < MinXms) xmx = Math.Min(MinXms, Math.Max(0, safeAlloc));

        // 32 位 JVM 守护:堆给多了直接启动即崩
        string warning = "";
        if (!string.IsNullOrEmpty(javaPath) && File.Exists(javaPath) && !MemoryMonitorService.IsJava64Bit(javaPath))
        {
            if (xmx > X86HeapCeiling)
            {
                xmx = X86HeapCeiling;
                capped = true;
            }
            warning = $"当前 Java({Path.GetFileName(javaPath)})是 32 位,堆内存最多只能给到 {X86HeapCeiling}MB," +
                      "给多了会启动即崩。建议在「Java 管理」里换成 64 位 Java 后再套用推荐值。";
        }
        else if (_memory.IsMemoryTight())
        {
            warning = $"当前可用内存只剩 {Fmt(availMb)},已经贴近系统预留红线。" +
                      "建议先关掉浏览器、录屏这类吃内存的程序,再启动游戏。";
        }
        else if (capped)
        {
            warning = "推荐值已经被当前可用内存压低,关掉其他占内存的程序后重新点一次可以拿到更高的值。";
        }

        // Xms = Xmx 的一半,512MB 起、4GB 封顶,256MB 对齐。
        // xmx 可能被低可用内存压到 0,此时 Math.Clamp 的 min(512) > max(0) 会抛异常,
        // 直接取 0(后续 Apply 端有 xmx<=0 的护栏,不会真的写进启动参数)
        int xms = xmx <= 0 ? 0 : Align256(Math.Clamp(xmx / 2, MinXms, Math.Min(MaxXms, xmx)));
        if (xms > xmx) xms = xmx;

        return new MemoryRecommendation
        {
            Xms = xms,
            Xmx = xmx,
            TotalMb = totalMb,
            AvailableMb = availMb,
            ReserveMb = reserve,
            Tier = TierOf(totalMb),
            Reason = string.Join("; ", reasons) + "。",
            Warning = warning,
            Capped = capped
        };
    }

    /// <summary>
    /// 一键套用推荐值:写入实例 Xms/Xmx 并打开「单版本自定义内存」开关。
    /// 套用后用户依旧可以在内存输入框里手动改,这里不做任何锁定。
    /// </summary>
    public (bool Ok, string Message) Apply(string instanceId, MemoryRecommendation? rec = null)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null) return (false, "找不到这个游戏版本,请刷新列表后重试。");

        rec ??= Recommend(instanceId);
        if (rec.Xmx <= 0)
            return (false, "当前可用内存不足以给出有效推荐,请先关掉一些占内存的程序再试。");

        inst.Xms = rec.Xms;
        inst.Xmx = rec.Xmx;
        inst.UseCustomMemory = true;
        _instances.SaveInstance(inst);
        App.WriteAppLog($"[内存推荐] ✓ {inst.Name} 套用推荐内存 Xms={rec.Xms}MB Xmx={rec.Xmx}MB({rec.Tier})");
        return (true, $"已套用推荐内存:-Xms{rec.Xms}m -Xmx{rec.Xmx}m。" +
                      (string.IsNullOrEmpty(rec.Warning) ? "" : "\n" + rec.Warning));
    }

    /// <summary>实例 shaderpacks 目录是否装有光影(目录非空即视为重负载)</summary>
    private bool HasShaderPacks(string instanceId)
    {
        try
        {
            string dir = _instances.GetShaderPacksDir(instanceId);
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch { return false; }
    }

    /// <summary>向下取整到 256MB 对齐(低于 256 时保留原值,避免归零)</summary>
    public static int Align256(int mb)
    {
        if (mb <= 0) return 0;
        int aligned = mb / 256 * 256;
        return aligned > 0 ? aligned : mb;
    }

    private static string Fmt(long mb) => mb >= 1024 ? $"{mb / 1024.0:F1}GB" : $"{mb}MB";
}
