// GameLogService.cs — 游戏日志捕获服务
// 可爱的芙芙 - 阶段6 模块 / 批5 重构
//
// 实时捕获游戏 stdout/stderr,同时写入独立 game.log 文件
// 与应用日志(app.log)完全分离,二者文件独立互不混杂
//
// 批5修复:
// - 原逐行 File.AppendAllText(每行开关文件句柄)→ 改为内存缓冲队列 + 200ms 批量刷新
// - 原 _logBuffer StringBuilder 无上限 → 改为 5000 行上限(超出丢弃最早行)
// - 原逐行 LogAppended 事件 → 改为批量 LogsAppended 事件(200ms 合并通知)
// - 新增 FlushNow() 供日志查看器打开时强制刷新待写缓冲

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace FufuLauncher.Services;

public class GameLogService : IDisposable
{
    private Process? _process;
    // 内存缓冲:限制最大行数,避免长时间运行内存膨胀
    private readonly StringBuilder _logBuffer = new();
    private const int MaxBufferLines = 5000;
    private int _currentLineCount;
    private bool _disposed;

    // 待写入文件的待办队列(线程安全)
    private readonly ConcurrentQueue<string> _pendingFileWrites = new();
    // 批量刷新定时器:200ms 合并写文件,避免逐行 IO
    private Timer? _flushTimer;
    private readonly object _fileLock = new();

    /// <summary>游戏日志文件路径:APP\MCGAME\Logs\game.log(与应用日志同目录但文件完全隔离)</summary>
    public static string LogFilePath => AppPaths.GameLogFile;

    /// <summary>新日志批量追加事件(参数:本批次新增的行数组,已含时间戳)</summary>
    public event Action<string[]>? LogsAppended;

    /// <summary>兼容旧订阅者:单行追加事件(在 LogsAppended 之前逐行触发)</summary>
    public event Action<string>? LogAppended;

    public string FullLog => _logBuffer.ToString();

    public GameLogService()
    {
        // 200ms 批量刷新到文件
        _flushTimer = new Timer(_ => FlushPendingToFile(), null, 200, 200);
    }

    public void AttachToProcess(Process process)
    {
        // 若上一次监控的进程未清理,先取消事件订阅,避免重复回调累积导致内存泄漏
        DetachFromCurrentProcess();

        _process = process;
        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived += OnError;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        AppendLine($"[可爱的芙芙] 开始监控游戏进程 PID={process.Id}");
    }

    /// <summary>取消当前进程的事件订阅并清空引用(不 Dispose 进程,由 GameLaunchService 负责回收)</summary>
    private void DetachFromCurrentProcess()
    {
        if (_process == null) return;
        try
        {
            _process.OutputDataReceived -= OnOutput;
            _process.ErrorDataReceived -= OnError;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[游戏日志] 取消旧进程事件订阅失败:{ex.Message}");
        }
        _process = null;
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            AppendLine(e.Data);
        }
    }

    private void OnError(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            AppendLine("[stderr] " + e.Data);
        }
    }

    public void AppendLine(string line)
    {
        string formatted = $"[{DateTime.Now:HH:mm:ss}] {line}";

        // 写入内存缓冲(线程安全:lock 防止 StringBuilder 并发损坏)
        lock (_logBuffer)
        {
            _logBuffer.AppendLine(formatted);
            _currentLineCount++;
            // 超出上限:丢弃最早行(粗粒度裁剪,避免每行都裁剪的开销)
            if (_currentLineCount > MaxBufferLines)
            {
                TrimBuffer(MaxBufferLines - MaxBufferLines / 10);
            }
        }

        // 加入待写文件队列(线程安全,无锁)
        _pendingFileWrites.Enqueue(formatted + "\n");

        // 通知订阅者(单行,向后兼容)
        try { LogAppended?.Invoke(line); } catch { /* 订阅者异常不影响主流程 */ }
    }

    /// <summary>裁剪缓冲区到指定行数(删除最早的 excess 行)</summary>
    private void TrimBuffer(int targetLines)
    {
        // _logBuffer 调用方已持有 lock
        int excess = _currentLineCount - targetLines;
        if (excess <= 0) return;
        var content = _logBuffer.ToString();
        var lines = content.Split('\n', excess + 1, StringSplitOptions.None);
        if (lines.Length > excess)
        {
            _logBuffer.Clear();
            // lines[excess] 起为保留内容(Split 最多返回 excess+1 段,最后一段含剩余全部)
            _logBuffer.Append(lines[excess]);
            _currentLineCount = targetLines;
        }
    }

    /// <summary>批量把待写队列刷到文件(由定时器或 FlushNow 触发)</summary>
    private void FlushPendingToFile()
    {
        if (_pendingFileWrites.IsEmpty) return;
        // 取出全部待写项
        var batch = new System.Collections.Generic.List<string>(64);
        while (_pendingFileWrites.TryDequeue(out var item))
        {
            batch.Add(item);
        }
        if (batch.Count == 0) return;

        // 批量追加写文件(单次打开句柄)
        lock (_fileLock)
        {
            try
            {
                File.AppendAllText(LogFilePath, string.Concat(batch));
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[游戏日志] 批量写入文件失败:{ex.Message}");
            }
        }

        // 批量通知订阅者(新批量事件)
        try { LogsAppended?.Invoke(batch.ToArray()); } catch { /* ignore */ }
    }

    /// <summary>强制立即刷新待写缓冲(供日志查看器打开时调用,确保读到最新)</summary>
    public void FlushNow()
    {
        FlushPendingToFile();
    }

    public void Clear()
    {
        lock (_logBuffer)
        {
            _logBuffer.Clear();
            _currentLineCount = 0;
        }
        while (_pendingFileWrites.TryDequeue(out _)) { }
    }

    /// <summary>从文件重新加载游戏日志(用于跨页面切换时恢复显示)</summary>
    public void ReloadFromFile()
    {
        lock (_logBuffer)
        {
            _logBuffer.Clear();
            _currentLineCount = 0;
            try
            {
                if (File.Exists(LogFilePath))
                {
                    var content = File.ReadAllText(LogFilePath);
                    _logBuffer.Append(content);
                    // 重新计数行数(按 \n 计)
                    _currentLineCount = content.Count(c => c == '\n');
                    // 若超出上限,裁剪
                    if (_currentLineCount > MaxBufferLines)
                    {
                        TrimBuffer(MaxBufferLines);
                    }
                }
            }
            catch (Exception ex) { App.WriteAppLog($"[游戏日志] 重新加载日志文件失败:{ex.Message}"); }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            DetachFromCurrentProcess();
            // 最后一次刷新,确保缓冲落地
            FlushPendingToFile();
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
    }
}
