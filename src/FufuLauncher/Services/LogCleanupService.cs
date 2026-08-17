// LogCleanupService.cs — 日志自动清理服务(重做)
// 可爱的芙芙
//
// 策略(取消按天定时,改为按启动次数 + 保留份数):
// - 每启动 LogCleanEveryNLaunches 次启动器自动清理一次(由 App 启动时按计数触发,后台执行)
// - 仅保留最新 LogKeepCount 份历史日志,超出的旧日志从最旧开始删除
// - 当前活跃日志 app.log / game.log 永远不删除(只清历史文件)
// - 提供手动清理入口;全部 IO 可后台执行,不阻塞 UI

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

public class LogCleanupService
{
    private readonly ConfigService _configService;

    public LogCleanupService(ConfigService configService)
    {
        _configService = configService;
    }

    /// <summary>
    /// 启动时调用:登记一次启动计数,达到「每 N 次」节拍时执行一次清理。
    /// 返回是否触发了清理(供 UI/日志展示)。清理本身请用 RunCleanupAsync 后台执行。
    /// </summary>
    public bool TickAndShouldClean()
    {
        try
        {
            var cfg = _configService.Config;
            cfg.AppLaunchCount++;
            _configService.Save();

            if (!cfg.LogAutoCleanEnabled) return false;
            int every = Math.Max(1, cfg.LogCleanEveryNLaunches);
            return cfg.AppLaunchCount % every == 0;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[日志清理] 计数异常:{ex.Message}");
            return false;
        }
    }

    /// <summary>后台执行清理(不阻塞 UI 线程)</summary>
    public Task RunCleanupAsync() => Task.Run(RunCleanup);

    /// <summary>执行一次清理:仅保留最新 LogKeepCount 份历史日志(活跃日志永不删除)</summary>
    public void RunCleanup()
    {
        var cfg = _configService.Config;
        try
        {
            string logDir = AppPaths.Logs;
            if (!Directory.Exists(logDir)) return;

            int keepCount = Math.Max(1, cfg.LogKeepCount);

            // 当前活跃日志:绝不删除
            string activeApp = AppPaths.AppLogFile;
            string activeGame = AppPaths.GameLogFile;

            var archives = new DirectoryInfo(logDir)
                .GetFiles("*", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Equals(activeApp, StringComparison.OrdinalIgnoreCase)
                         && !f.FullName.Equals(activeGame, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)   // 最新在前
                .ToList();

            if (archives.Count <= keepCount) return;

            int deleted = 0;
            long freedBytes = 0;
            foreach (var f in archives.Skip(keepCount))   // 跳过最新的 N 份,删除其余
            {
                try
                {
                    long size = f.Length;
                    f.Delete();
                    freedBytes += size;
                    deleted++;
                }
                catch { /* 单文件删除失败跳过(可能被占用) */ }
            }

            if (deleted > 0)
                App.WriteAppLog($"[日志清理] 已删除 {deleted} 份旧日志,释放 {freedBytes / 1024.0:F1} KB" +
                                $"(保留最新 {keepCount} 份)");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[日志清理] 清理失败:{ex.Message}");
        }
    }
}
