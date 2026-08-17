// GameMemoryWatchService.cs — 游戏进程内存监控(堆 + 堆外拆分展示 + 暴涨预警)
// 可爱的芙芙
//
// 背景:
// Minecraft(Iris/Sodium 等模组)的堆外内存(DirectByteBuffer、Native 缓冲、GPU 资源)
// 不受 -Xmx 约束,曾出现 Xmx=2560MB 而 Java 进程实际占用 5.3GB 的失控泄漏。
// 只展示堆内存会"欺骗"用户,必须把真实进程占用拆开呈现:
//   ① Java 堆:Xms=Xmx 固定堆,堆已提交量即 Xmx 上限(诚实标注为上限)
//   ② 堆外(Off-Heap)= 进程实际物理占用(WorkingSet) − 堆上限(负值按 0)
//
// 职责:
// 1. 游戏运行期间每 2 秒采样一次进程 WorkingSet,事件推给 UI 线程展示
// 2. 堆外内存持续快速上涨检测:滑动窗口(60s)内涨幅 ≥ 384MB 且多数采样为正增长
//    → 触发预警(提醒长时间游玩可能崩溃),预警冷却 10 分钟防刷屏

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace FufuLauncher.Services;

/// <summary>游戏进程内存快照</summary>
public class GameMemorySnapshot
{
    /// <summary>进程实际物理内存占用(MB,等同任务管理器"内存"列)</summary>
    public long TotalMb { get; set; }
    /// <summary>Java 堆上限(MB,即 Xmx;固定堆下已全部提交)</summary>
    public long HeapCapMb { get; set; }
    /// <summary>堆外内存估算(MB)= 进程总占用 − 堆上限,含 DirectBuffer/Native/元空间/线程栈</summary>
    public long OffHeapMb { get; set; }
}

public class GameMemoryWatchService
{
    private const int SampleIntervalMs = 2000;   // 采样间隔
    private const int WindowSize = 30;           // 滑动窗口 30 次 ≈ 60 秒
    private const long GrowthThresholdMb = 384;  // 窗口内涨幅阈值
    private static readonly TimeSpan WarnCooldown = TimeSpan.FromMinutes(10);

    private Process? _process;
    private Timer? _timer;
    private int _xmxMb;
    private readonly Queue<long> _offHeapWindow = new();
    private readonly object _lock = new();
    private DateTime _lastWarnAt = DateTime.MinValue;

    /// <summary>内存快照刷新(已在 UI 线程触发)</summary>
    public event Action<GameMemorySnapshot>? Updated;
    /// <summary>堆外内存持续暴涨预警(参数:当前堆外 MB,已在 UI 线程触发)</summary>
    public event Action<long>? OffHeapGrowthWarning;

    public bool IsWatching => _timer != null;

    /// <summary>开始监控游戏进程(启动成功后调用)</summary>
    public void Start(Process process, int xmxMb)
    {
        Stop();
        _process = process;
        _xmxMb = xmxMb;
        lock (_lock) _offHeapWindow.Clear();
        _lastWarnAt = DateTime.MinValue;
        _timer = new Timer(Sample, null, 3000, SampleIntervalMs);
        App.WriteAppLog($"[内存监控] 游戏进程监控启动:PID={process.Id} 堆上限={xmxMb}MB(采样间隔 {SampleIntervalMs}ms)");
    }

    /// <summary>停止监控(游戏退出 / 启动器退出时调用)</summary>
    public void Stop()
    {
        var t = _timer;
        _timer = null;
        t?.Dispose();
        _process = null;
        lock (_lock) _offHeapWindow.Clear();
    }

    private void Sample(object? state)
    {
        var proc = _process;
        if (proc == null) { Stop(); return; }
        GameMemorySnapshot snap;
        bool warn = false;
        long offHeap = 0;
        try
        {
            if (proc.HasExited) { Stop(); return; }
            long totalMb = proc.WorkingSet64 / (1024L * 1024);
            offHeap = Math.Max(0, totalMb - _xmxMb);
            snap = new GameMemorySnapshot { TotalMb = totalMb, HeapCapMb = _xmxMb, OffHeapMb = offHeap };
            warn = DetectSustainedGrowth(offHeap);
        }
        catch
        {
            // 进程已退出或句柄失效:静默停止
            Stop();
            return;
        }

        // 切回 UI 线程通知订阅者
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                try { Updated?.Invoke(snap); } catch { /* 订阅者异常不影响监控 */ }
                if (warn)
                {
                    try { OffHeapGrowthWarning?.Invoke(offHeap); } catch { /* 同上 */ }
                }
            }));
        }
    }

    /// <summary>
    /// 堆外持续暴涨检测:滑动窗口内涨幅 ≥ 384MB 且 ≥70% 采样为正增长 → 预警(冷却 10 分钟)
    /// </summary>
    private bool DetectSustainedGrowth(long offHeapMb)
    {
        lock (_lock)
        {
            _offHeapWindow.Enqueue(offHeapMb);
            while (_offHeapWindow.Count > WindowSize) _offHeapWindow.Dequeue();
            if (_offHeapWindow.Count < WindowSize) return false;

            var arr = _offHeapWindow.ToArray();
            long rise = arr[^1] - arr[0];
            int positives = 0;
            for (int i = 1; i < arr.Length; i++)
                if (arr[i] > arr[i - 1]) positives++;

            if (rise < GrowthThresholdMb) return false;
            if (positives < (arr.Length - 1) * 0.7) return false;

            if (DateTime.Now - _lastWarnAt < WarnCooldown) return false;
            _lastWarnAt = DateTime.Now;
            App.WriteAppLog($"[内存监控] ⚠ 堆外内存持续暴涨:60 秒内 +{rise}MB,当前堆外 {offHeapMb}MB,长时间游玩有崩溃风险");
            return true;
        }
    }
}
