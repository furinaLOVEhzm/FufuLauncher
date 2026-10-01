// GameLogFixService.cs — 游戏日志自愈服务
//
// 挂 GameLogService.LogAppended 实时行事件,按规则表识别卡顿/报错:
//   · 简单配置类(Java 堆 OOM)→ 自动上调该实例 Xmx 并落盘,提示重启生效(每会话只修一次);
//   · 复杂类(光影编译失败 / 模组冲突 / 显存不足)→ 只记日志给中文处置建议,不动文件不删模组。
// 会话上下文由 GameLaunchService 在启动/退出时 BeginSession / EndSession 注入。

using System.Text.RegularExpressions;

namespace FufuLauncher.Services;

public class GameLogFixService
{
    private readonly InstanceService _instances;
    private readonly MemoryMonitorService _memory;
    private readonly object _gate = new();

    private string _sessionId = "";
    private readonly HashSet<string> _fired = new();

    private static readonly Regex OomRx = new("out of memory|java heap space|OutOfMemoryError", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ShaderRx = new("shader compilation failed|iris.*error|failed to create shader", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DupModRx = new("duplicate mod|mod resolution exception|mod conflict", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VramRx = new("out of vram|cuda error|out of memory.*vulkan|vkAllocateMemory", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GameLogFixService(GameLogService gameLog, InstanceService instances, MemoryMonitorService memory)
    {
        _instances = instances;
        _memory = memory;
        gameLog.LogAppended += OnLogLine;
    }

    /// <summary>游戏启动时注入会话实例 Id(日志行自愈需要知道改哪个实例的配置)</summary>
    public void BeginSession(string instanceId)
    {
        lock (_gate) { _sessionId = instanceId; _fired.Clear(); }
    }

    public void EndSession()
    {
        lock (_gate) { _sessionId = ""; }
    }

    private void OnLogLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        string session;
        lock (_gate) { session = _sessionId; }
        if (string.IsNullOrEmpty(session)) return;

        if (OomRx.IsMatch(line)) { FireOnce(session, "oom", () => FixOom(session)); return; }
        if (ShaderRx.IsMatch(line)) { FireOnce(session, "shader", () => App.WriteAppLog("[日志自愈] 检测到光影/ Iris 编译报错:建议在版本设置里暂时关闭光影包或更换光影,未自动改动文件")); return; }
        if (DupModRx.IsMatch(line)) { FireOnce(session, "dupmod", () => App.WriteAppLog("[日志自愈] 检测到模组重复/冲突:请到「管理模组」移除重复 jar,未自动删除任何文件")); return; }
        if (VramRx.IsMatch(line)) { FireOnce(session, "vram", () => App.WriteAppLog("[日志自愈] 检测到显存不足:建议在设置-泡芙助理中关闭 GPU 加速回落 CPU,或降低光影/渲染距离"));
        }
    }

    private void FireOnce(string session, string rule, Action fix)
    {
        lock (_gate)
        {
            if (!_fired.Add(rule)) return;
        }
        try { fix(); }
        catch (Exception ex) { App.WriteAppLog($"[日志自愈] 规则 {rule} 处置异常:{ex.Message}"); }
    }

    /// <summary>OOM 自动修正:该实例 Xmx +2G(不超过物理内存一半),开自定义内存开关并落盘</summary>
    private void FixOom(string instanceId)
    {
        var inst = _instances.Instances.Find(i => i.Id == instanceId);
        if (inst == null) return;
        long physHalfMb = _memory.GetCurrent().TotalBytes / 1024 / 1024 / 2;
        int target = (int)Math.Min(inst.Xmx + 2048L, Math.Max(physHalfMb, 2048));
        if (target <= inst.Xmx)
        {
            App.WriteAppLog($"[日志自愈] 检测到 Java 堆 OOM,但 Xmx={inst.Xmx}MB 已达物理内存一半上限,建议关闭光影或减少实体数量");
            return;
        }
        inst.Xmx = target;
        inst.UseCustomMemory = true;
        _instances.SaveInstance(inst);
        App.WriteAppLog($"[日志自愈] 检测到 Java 堆 OOM,已自动把该实例最大内存上调到 {target}MB,重启游戏生效");
    }
}
