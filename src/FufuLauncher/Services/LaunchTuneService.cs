// LaunchTuneService.cs — 启动自动调参(经典方案,不依赖 AI)
//
// 定位:版本设置「基础设置」的内存 / JVM 参数由启动器在游戏启动时自动调整。
// 学习主流启动器无 AI 年代的经典做法(总内存档位表 + 系统预留 + 安全上限,
// 纯确定性硬件计算),YesAI / NoAI 双版本行为完全一致,任何环境都不需要模型参与:
//   - 只在打开游戏那一刻介入一次,全程毫秒级,不吃显卡性能;
//   - 内存:未勾选「为该版本自定义内存」→ 启动链按本机硬件实时智能分配(档位表 + 可用内存安全上限);
//     已勾选 → 锁定用户手动值,自动调参不介入;
//   - JVM:该版本没有保存过参数时,在启动链解析出真实 Java 版本之后,按实际 Java 版本 + 本机核心数自动填充 GC/线程参数并静默落盘。

using System;

namespace FufuLauncher.Services;

public class LaunchTuneService
{
    private readonly MemoryMonitorService _memoryMonitor;
    private readonly InstanceService _instances;

    public LaunchTuneService(MemoryMonitorService memoryMonitor, InstanceService instances)
    {
        _memoryMonitor = memoryMonitor;
        _instances = instances;
    }

    /// <summary>启动前自动调参:仅在即将启动游戏时调用一次。异常吞掉只记日志,绝不阻塞启动。
    /// 只做内存审计记录,不改写任何存储值;JVM 参数填充见 FillJvmArgsIfNeeded(需在实际 Java 版本确定后调用)</summary>
    public void TuneBeforeLaunch(GameInstance inst)
    {
        try
        {
            // 内存:用户已开启「为该版本自定义内存」→ 锁定手动值,自动调参不介入;
            // 未开启 → 启动链按本机硬件实时智能分配,此处只把调参审计结果记入日志
            if (inst.UseCustomMemory)
            {
                App.WriteAppLog($"[自动调参] {inst.Name}:已开启自定义内存,尊重手动值 Xms={inst.Xms}MB Xmx={inst.Xmx}MB,不介入");
            }
            else
            {
                int rec = RecommendXmxMb(inst);
                var mi = _memoryMonitor.GetCurrent();
                App.WriteAppLog($"[自动调参] {inst.Name}:未开启自定义内存,启动时按硬件自动分配" +
                                $"(推荐 Xms=Xmx={rec}MB,本机总物理内存 {mi.TotalGb:0.0}GB / 当前可用 {mi.AvailableGb:0.0}GB)");
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[自动调参] 调参异常(不影响启动):{ex.Message}");
        }
    }

    /// <summary>
    /// JVM 参数自动填充:该版本没有保存过参数时,按「实际生效的 Java 主版本」+ 核心数生成 GC/线程参数并静默落盘。
    /// 必须在启动链 DetectJavaMajorVersion 确定真实 Java 之后调用:按实例推荐值猜版本可能填出实际 Java 不认识的参数,
    /// 导致 JVM 拒启且错误参数被永久落盘。异常吞掉只记日志,绝不阻塞启动。
    /// </summary>
    public void FillJvmArgsIfNeeded(GameInstance inst, int javaMajor)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(inst.ExtraJvmArgs)) return;
            inst.ExtraJvmArgs = MemoryMonitorService.BuildMultiCoreGcArgs(MemoryMonitorService.GetPhysicalCoreCount(), javaMajor);
            _instances.SaveInstance(inst);
            App.WriteAppLog($"[自动调参] {inst.Name}:JVM 参数为空,已按实际 Java {javaMajor} 自动填充并保存:{inst.ExtraJvmArgs}");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[自动调参] JVM 参数填充异常(不影响启动):{ex.Message}");
        }
    }

    /// <summary>推荐最大堆内存:总内存经典档位表为基准;大型整合包建议值更高且安全上限允许时上调;256MB 对齐减少 GC 碎片</summary>
    private int RecommendXmxMb(GameInstance inst)
    {
        int rec = _memoryMonitor.RecommendByTotalMb();
        if (inst.RecommendedMemoryMb > rec)
        {
            int safe = _memoryMonitor.GetSafeAllocMb();
            if (safe >= inst.RecommendedMemoryMb) rec = inst.RecommendedMemoryMb;
        }
        return Math.Max(256, (rec / 256) * 256);
    }
}
