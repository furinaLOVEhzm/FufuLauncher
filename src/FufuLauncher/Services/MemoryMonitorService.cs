// MemoryMonitorService.cs — 系统内存监控与智能内存分配服务
// FufuLauncher - 阶段5 模块 / 批5 重构
//
// 职责:
// 1. 读取整机物理内存总量、当前实时可用空闲内存
// 2. 智能自动分配游戏 JVM 内存:硬性约束 = min(档位预设内存, 当前可用内存 − 1.5GB),
//    可用内存紧张(≤1.5GB)时自动下调分配并提示 UI 弹窗警告,并做上下限安全阈值限制
// 3. 提供定时刷新事件,供 UI 可视化展示
//
// 批5修复:
// - 改用 DispatcherTimer(DispatcherPriority.Background),Tick 在 UI 线程触发,
//   订阅者无需 Dispatcher.Invoke 跨线程切换,避免 Invoke 队列堆积导致静置卡死
// - 降频 1500ms → 3000ms,减少无谓刷新
// - Background 优先级低于 Input,按钮点击不会被定时回调抢占

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FufuLauncher.Services;

public class MemoryMonitorService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;       // 已用百分比 0~100
        public ulong ullTotalPhys;      // 物理内存总量(字节)
        public ulong ullAvailPhys;      // 可用物理内存(字节)
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private readonly ConfigService _configService;
    // UI 线程定时器:Tick 在 UI 线程,订阅者无需 Invoke 切换
    private DispatcherTimer? _timer;
    // 兜底:若 UI Dispatcher 不可用(不应发生),回退到线程池定时器
    private Timer? _fallbackTimer;
    private bool _running;

    /// <summary>内存信息刷新事件(Tick 在 UI 线程触发,订阅者可直接更新绑定属性)</summary>
    public event Action<MemoryInfo>? Updated;

    public MemoryMonitorService(ConfigService configService)
    {
        _configService = configService;
    }

    /// <summary>启动定时刷新(应用启动后调用)。默认 3000ms,UI 线程 Background 优先级。</summary>
    public void Start(int intervalMs = 3000)
    {
        if (_running) return;
        _running = true;

        var dispatcher = Application.Current?.Dispatcher;
        // 启动器入口在 UI 线程,正常情况 Dispatcher 一定可用
        if (dispatcher == null)
        {
            StartFallbackTimer(intervalMs);
            return;
        }
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => Start(intervalMs)));
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(intervalMs)
        };
        _timer.Tick += (s, e) => RaiseUpdatedSafe();
        _timer.Start();
        // 立即触发一次,避免 UI 初始空白
        RaiseUpdatedSafe();
    }

    /// <summary>停止定时刷新(应用退出 / 长期不需要时调用)</summary>
    public void Stop()
    {
        _running = false;
        var t = _timer;
        if (t != null)
        {
            t.Stop();
            _timer = null;
        }
        var ft = _fallbackTimer;
        if (ft != null)
        {
            ft.Dispose();
            _fallbackTimer = null;
        }
    }

    /// <summary>暂停定时器(设置页离开时调用,避免无监听者时每 3s P/Invoke 浪费)</summary>
    public void Pause()
    {
        _timer?.Stop();
    }

    /// <summary>恢复定时器(设置页进入时调用)</summary>
    public void Resume()
    {
        if (_running)
        {
            _timer?.Start();
            RaiseUpdatedSafe();
        }
    }

    /// <summary>UI Dispatcher 不可用时的兜底定时器(线程池触发,订阅者需自行切回 UI 线程)</summary>
    private void StartFallbackTimer(int intervalMs)
    {
        _fallbackTimer = new Timer(_ => RaiseUpdatedSafe(), null, 0, intervalMs);
    }

    private void RaiseUpdatedSafe()
    {
        try
        {
            var info = GetCurrent();
            Updated?.Invoke(info);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[内存监控] 读取异常:{ex.Message}");
        }
    }

    /// <summary>读取当前内存状态</summary>
    public MemoryInfo GetCurrent()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        return new MemoryInfo
        {
            TotalBytes = (long)mem.ullTotalPhys,
            AvailableBytes = (long)mem.ullAvailPhys,
            UsedBytes = (long)(mem.ullTotalPhys - mem.ullAvailPhys),
            LoadPercent = mem.dwMemoryLoad
        };
    }

    /// <summary>读取 CPU 物理核心数量(供 JVM 多核优化预设)</summary>
    public static int GetPhysicalCoreCount()
    {
        try
        {
            return Environment.ProcessorCount;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[内存监控] 读取 CPU 核心数失败,回退到 4:{ex.Message}");
            return 4;
        }
    }

    /// <summary>内存档位推荐基准值(MB):纯净 / 中型模组 / 大型光影(借鉴主流启动器推荐值)</summary>
    public static int TierBaseMb(string tier) => tier switch
    {
        "Vanilla" => 2048,   // 纯净版:2GB 起步
        "Shader" => 8192,    // 大型光影/整合:8GB 起步
        _ => 4096             // 中型模组:4GB 起步
    };

    public static string TierDisplayName(string tier) => tier switch
    {
        "Vanilla" => "纯净版",
        "Shader" => "大型光影",
        _ => "中型模组"
    };

    /// <summary>
    /// 总内存推荐档位表(MB):学习主流启动器无 AI 年代的经典自动分配方案,
    /// 纯确定性硬件计算,不依赖 AI,YesAI / NoAI 双版本行为完全一致。
    /// 按整机总物理内存分档:&lt;4GB→1024,4~8GB→2048,8~16GB→4096,16~32GB→6144,≥32GB→8192;
    /// 另受「总内存 − 系统预留」硬顶约束,绝不超过整机承载。
    /// 只看总内存不看「当前可用」瞬时高值,避免过度分配。
    /// </summary>
    public int RecommendByTotalMb()
    {
        long totalMb = GetCurrent().TotalBytes / (1024L * 1024);
        // 物理内存上报容差:8GB/16GB 机型因硬件保留普遍少报约 3%(8GB 实测约 8000MB),
        // 若按上报值直接比档会全部落进低一档;分档前按 3% 校正,硬顶仍用真实上报值,二者互不影响
        long tierMb = totalMb + totalMb * 3 / 100;
        int tier = tierMb < 4096 ? 1024
                 : tierMb < 8192 ? 2048
                 : tierMb < 16384 ? 4096
                 : tierMb < 32768 ? 6144
                 : 8192;
        long capByTotal = Math.Max(512, totalMb - ReserveMb());   // 系统预留红线不可破,低于 512 时兜底 512 由后续安全校验再降
        return (int)Math.Min(tier, capByTotal);
    }

    /// <summary>
    /// 智能自动分配游戏 JVM 最大内存(MB)。负载感知,启动与 UI 推荐单一来源:
    /// 1. 基础档位 = 总内存经典曲线(<4G→1G / 4~8→2G / 8~16→4G / 16~32→6G / ≥32→8G);
    /// 2. 负载升档:模组每 50 个 +1GB(上限 +4GB);整合包作者建议内存高于基础档则采用;
    ///    光影/重负载 +1GB;
    /// 3. 硬性约束三取最小:整机承载(总内存−预留)、实时可用(当前可用−预留)、用户上限;
    /// 4. 下限兼容:候选低于下限时抬到下限,但仍绝不突破实时天花板;
    /// 5. 256MB 对齐减少 GC 碎片;天花板不足时如实返回小值,由调用方决策。
    /// </summary>
    public int CalculateSmartXmx(int modCount = -1, int packSuggestedMb = 0, bool heavyGraphics = false)
    {
        var cfg = _configService.Config;
        var info = GetCurrent();
        long totalMb = info.TotalBytes / (1024L * 1024);
        long availMb = info.AvailableBytes / (1024L * 1024);
        int min = Math.Max(512, cfg.AutoMemoryMinMb);
        int max = Math.Max(min, cfg.AutoMemoryMaxMb);

        // 1) 基础档位:总内存经典曲线(不看瞬时可用,避免过度分配)
        long candidate = RecommendByTotalMb();

        // 2) 负载升档
        if (modCount > 0)
            candidate += Math.Min(4096, (modCount / 50) * 1024);   // 每 50 模组 +1GB,上限 +4GB
        if (packSuggestedMb > 0 && packSuggestedMb > candidate)
            candidate = packSuggestedMb;                            // 整合包作者实测建议优先
        if (heavyGraphics)
            candidate += 1024;                                      // 光影/重负载 +1GB

        // 3) 硬约束:整机承载 / 实时可用 / 用户上限 三取最小
        long hardCeiling = Math.Max(0, availMb - ReserveMb());      // 实时可用 − 预留
        long totalCap = Math.Max(512, totalMb - ReserveMb());       // 整机承载 − 预留
        candidate = Math.Min(candidate, hardCeiling);
        candidate = Math.Min(candidate, totalCap);
        candidate = Math.Min(candidate, max);

        // 4) 下限兼容:候选低于下限时抬到下限,但仍绝不突破实时天花板(绝不超分)
        if (candidate < min) candidate = Math.Min(min, Math.Max(512, hardCeiling));

        // 5) 向下取整到 256MB 对齐;天花板不足时如实返回小值,由调用方降级处理
        candidate = (candidate / 256) * 256;
        if (candidate <= 0) return (int)Math.Max(0, hardCeiling);
        return (int)Math.Min(candidate, hardCeiling);
    }

    /// <summary>系统预留内存(MB):
    /// 用户手动设置过(≠默认哨兵 1536)则尊重(至少留 384MB);
    /// 否则按总内存动态分档——原实现固定 1.5GB 不可降,4GB 小内存机器 Windows 开机
    /// 就占 2GB+,可用内存常年 ≤1.5GB → 「什么都没做就弹内存不足」、游戏推荐被压到 0。
    /// 改为:≤4GB 留 768MB / 4~8GB 留 1GB / 8~16GB 留 1.5GB / ≥16GB 留 2GB。</summary>
    public int ReserveMb()
    {
        int cfg = _configService.Config.MemoryReserveMb;
        if (cfg != 1536) return Math.Max(384, cfg);
        long totalMb = GetCurrent().TotalBytes / (1024L * 1024);
        return totalMb <= 4096 ? 768
             : totalMb < 8192 ? 1024
             : totalMb < 16384 ? 1536
             : 2048;
    }

    /// <summary>
    /// 当前实时可安全分配给游戏的最大内存(MB) = 可用内存 − 1.5GB 预留(负值按 0 处理)。
    /// 手动设置内存时的安全校验基准。
    /// </summary>
    public int GetSafeAllocMb()
    {
        long availMb = GetCurrent().AvailableBytes / (1024L * 1024);
        return (int)Math.Max(0, availMb - ReserveMb());
    }

    /// <summary>内存紧张判定:当前实时可用内存 ≤ 系统预留(1.5GB)</summary>
    public bool IsMemoryTight()
    {
        long availMb = GetCurrent().AvailableBytes / (1024L * 1024);
        return availMb <= ReserveMb();
    }

    /// <summary>
    /// 通过 PE 头判定 java.exe 是否 64 位(纯文件读取,不执行 java,毫秒级):
    /// 32 位 JVM 堆上限约 1.5~2GB,过度分配会导致启动即崩(主流启动器同款防护)。
    /// </summary>
    public static bool IsJava64Bit(string javaExePath)
    {
        try
        {
            using var fs = new System.IO.FileStream(javaExePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            using var br = new System.IO.BinaryReader(fs);
            // DOS 头 e_lfanew 偏移(0x3C)→ PE 签名 → COFF 头 Machine 字段
            fs.Seek(0x3C, System.IO.SeekOrigin.Begin);
            int peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > fs.Length) return true;
            fs.Seek(peOffset, System.IO.SeekOrigin.Begin);
            uint sig = br.ReadUInt32();
            if (sig != 0x00004550) return true; // 非标准 PE,保守按 64 位处理
            ushort machine = br.ReadUInt16();
            // 0x014C = i386(32 位);0x8664 = AMD64(64 位);0xAA64 = ARM64
            return machine != 0x014C;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[内存监控] PE 头读取失败,按 64 位处理:{ex.Message}");
            return true;
        }
    }

    /// <summary>生成多核 GC 优化 JVM 参数(兼容旧调用,默认按 Java 17 预览)</summary>
    public static string BuildMultiCoreGcArgs(int coreCount) => BuildMultiCoreGcArgs(coreCount, 17);

    /// <summary>
    /// 生成 GC + 编译线程优化参数(按 Java 版本与 CPU 核心数动态生成):
    /// - Java 21+ :ZGC 分代模式(-XX:+UseZGC -XX:+ZGenerational),低停顿适合大型光影
    /// - Java 17~20:ZGC(-XX:+UseZGC)
    /// - Java 16 及以下:G1GC(-XX:+UseG1GC)
    /// 线程数按核心数动态计算:ParallelGCThreads≈5/8 核、ConcGCThreads≈1/4、CICompilerCount≈1/4。
    /// </summary>
    public static string BuildMultiCoreGcArgs(int coreCount, int javaMajor)
    {
        // GC 选型:Java 17+ 自动 ZGC,低版本 G1GC;
        // G1(Java 8~16):默认堆空闲超 70% 才缩,MC 玩久堆只涨不降 → 缩到 50%/20%,
        // GC 后把空闲堆主动还给操作系统
        // ZGC 默认只在分配压力大时收集,MC 长时间挂机堆只涨不降 → 加 ZCollectionInterval 周期
        // 并发收集(秒),让空闲堆定期还给 OS,缓解「玩久了内存越占越多」的通病
        string gc = javaMajor >= 21 ? "-XX:+UseZGC -XX:+ZGenerational -XX:ZCollectionInterval=10"
                  : javaMajor >= 17 ? "-XX:+UseZGC -XX:ZCollectionInterval=10"
                  : "-XX:+UseG1GC -XX:G1HeapRegionSize=16m -XX:MaxHeapFreeRatio=50 -XX:MinHeapFreeRatio=20";

        // ParallelGCThreads:并行 GC 线程数,约逻辑核心的 5/8,至少 1
        int parallel = Math.Max(1, (int)Math.Ceiling(coreCount * 5.0 / 8.0));
        // ConcGCThreads:并发 GC 线程数,约 Parallel 的 1/4,至少 1
        int conc = Math.Max(1, parallel / 4);
        // CICompilerCount:JIT 编译线程数,约核心的 1/4,至少 2
        int ci = Math.Max(2, coreCount / 4);
        // 周期 GC(仅 G1 分支,Java 12~16):每 10 秒触发一次并发 GC,回收废弃 DirectByteBuffer,
        // 缓解 Iris/Sodium 堆外只涨不释放。Java 8/11 不识别该参数,强加会导致 JVM 启动失败,必须版本守护;
        // ZGC 分支的周期回收由调用方用 -XX:ZCollectionInterval 等价实现
        string periodic = (javaMajor >= 12 && javaMajor < 17)
            ? " -XX:G1PeriodicGCInterval=10000 -XX:+G1PeriodicGCInvokesConcurrent"
            : "";
        return $"{gc} -XX:ParallelGCThreads={parallel} -XX:ConcGCThreads={conc} -XX:CICompilerCount={ci}{periodic}";
    }
}

/// <summary>内存快照信息</summary>
public class MemoryInfo
{
    public long TotalBytes { get; set; }
    public long AvailableBytes { get; set; }
    public long UsedBytes { get; set; }
    public uint LoadPercent { get; set; }
    public double TotalGb => TotalBytes / 1024.0 / 1024 / 1024;
    public double AvailableGb => AvailableBytes / 1024.0 / 1024 / 1024;
    public double UsedGb => UsedBytes / 1024.0 / 1024 / 1024;
}
