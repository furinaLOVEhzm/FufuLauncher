// ProcessGuardService.cs — 子进程托管中心
// 可爱的芙芙 - 多语言调用加固
//
// 职责:
// 1. 统一登记启动器派生的全部子进程(Java 探测、外部工具、游戏进程、Python/C++ 衍生进程)
// 2. 关闭启动器时强制回收全部子进程(Kill 整棵进程树),杜绝后台残留僵尸进程
//    占用端口、占用文件锁
// 3. 登记/回收过程全部写入应用日志

using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace FufuLauncher.Services;

public class ProcessGuardService
{
    /// <summary>key=进程 Id,便于按 Id 移除</summary>
    private readonly ConcurrentDictionary<int, Process> _processes = new();

    /// <summary>当前托管中的子进程数量</summary>
    public int ActiveCount => _processes.Count;

    /// <summary>登记一个由启动器派生的子进程;进程自行退出时自动移除登记</summary>
    public void Register(Process? process, string purpose)
    {
        if (process == null) return;
        try
        {
            int id = process.Id;
            _processes[id] = process;
            App.WriteAppLog($"[进程托管] 登记子进程 {purpose} (PID={id}, Name={SafeName(process)})");
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _processes.TryRemove(id, out _);
        }
        catch (Exception ex)
        {
            // 进程可能已退出或拒绝访问,不影响主流程
            App.WriteAppLog($"[进程托管] 登记失败 {purpose}:{ex.Message}");
        }
    }

    /// <summary>关闭启动器时调用:强制终止全部托管子进程(整棵进程树),
    /// 杜绝 Python/C++/Java 衍生进程残留占用端口与文件锁</summary>
    public void KillAll()
    {
        foreach (var kv in _processes)
        {
            var p = kv.Value;
            try
            {
                if (!p.HasExited)
                {
                    App.WriteAppLog($"[进程托管] 回收残留子进程 PID={kv.Key} Name={SafeName(p)}");
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[进程托管] 终止进程 PID={kv.Key} 失败:{ex.Message}");
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }
        _processes.Clear();
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return "?"; }
    }
}
