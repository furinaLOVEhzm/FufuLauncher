// FufuLauncher - 统一日志门面(全量重构版)
// Copyright © FufuLauncher
//
// 全项目日志统一出口:转发到 App 的缓冲写入体系(app.log 双缓冲落盘)。
// ERROR 级额外同步直写 error-trace.log,进程被强杀时也能留下现场。

using System.IO;

namespace FufuLauncher.Next.Foundation;

public static class NextLog
{
    private const string Tag = "[V2] ";

    /// <summary>Info 级(始终落盘)</summary>
    public static void Info(string message) => App.WriteAppLog(Tag + message);

    /// <summary>Warn 级</summary>
    public static void Warn(string message) => App.WriteLog(App.AppLogLevel.Warn, Tag + message);

    /// <summary>Debug 级(受「高级设置 → 详细日志」开关控制)</summary>
    public static void Debug(string message) => App.WriteLog(App.AppLogLevel.Debug, Tag + message);

    /// <summary>Error 级:进缓冲队列 + 同步直写错误追踪文件,崩溃/强杀现场必留痕</summary>
    public static void Error(string message, Exception? ex = null)
    {
        string full = ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}";
        App.WriteLog(App.AppLogLevel.Error, Tag + full);
        try
        {
            NextPaths.EnsureDir(NextPaths.Logs);
            File.AppendAllText(Path.Combine(NextPaths.Logs, "error-trace.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {full}{Environment.NewLine}");
        }
        catch { /* 直写失败不影响主流程,缓冲队列日志仍在 */ }
    }
}
