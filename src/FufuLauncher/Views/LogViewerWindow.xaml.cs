// LogViewerWindow.xaml.cs — 日志控制台(全新重写)
// 可爱的芙芙
//
// 日志统一路径:APP\mcGAME\日志(app.log / game.log 文件分离)
// 应用日志:1s 轮询文件刷新;游戏日志:订阅 GameLogService 批量事件实时追加。
// 完整显示不截断(TextWrapping=Wrap),单例窗口防堆叠。

using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FufuLauncher.Services;

namespace FufuLauncher.Views;

public partial class LogViewerWindow : Window
{
    private static LogViewerWindow? _instance;

    private readonly GameLogService _gameLog;
    private readonly DispatcherTimer _appLogTimer;
    private DateTime _lastAppLogWrite = DateTime.MinValue;
    private bool _gameAutoScroll = true;

    /// <summary>单例入口:已打开则激活,否则新建并显示</summary>
    public static void ShowSingle(GameLogService gameLog, Window owner)
    {
        if (_instance != null)
        {
            try
            {
                if (_instance.IsLoaded)
                {
                    _instance.WindowState = WindowState.Normal;
                    _instance.Activate();
                    return;
                }
            }
            catch { _instance = null; }
        }
        _instance = new LogViewerWindow(gameLog) { Owner = owner };
        _instance.Show();
    }

    public LogViewerWindow(GameLogService gameLog)
    {
        InitializeComponent();
        _gameLog = gameLog;
        TxtLogPath.Text = AppPaths.Logs;

        // 应用日志:1s 轮询(仅当文件有变化时刷新,降低 IO)
        _appLogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _appLogTimer.Tick += (_, _) => RefreshAppLog();

        // 游戏日志:订阅批量追加事件
        _gameLog.LogsAppended += OnGameLogsAppended;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshAppLog();
        // 游戏日志初始全量载入
        _gameLog.FlushNow();
        _gameLog.ReloadFromFile();
        GameLogBox.Text = _gameLog.FullLog;
        ScrollGameLogToEnd();
        _appLogTimer.Start();
    }

    private void OnClosed(object sender, EventArgs e)
    {
        _appLogTimer.Stop();
        try { _gameLog.LogsAppended -= OnGameLogsAppended; } catch { /* ignore */ }
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    /// <summary>刷新应用日志(文件时间戳变化才读取,全文显示不截断)</summary>
    private void RefreshAppLog()
    {
        try
        {
            string logFile = AppPaths.AppLogFile;
            if (!File.Exists(logFile)) return;
            var writeTime = File.GetLastWriteTime(logFile);
            if (writeTime == _lastAppLogWrite) return;
            _lastAppLogWrite = writeTime;
            AppLogBox.Text = App.ReadAppLog();
            AppLogBox.ScrollToEnd();
        }
        catch { /* 文件占用等异常忽略,下个周期重试 */ }
    }

    private void OnGameLogsAppended(string[] lines)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded) return;
                GameLogBox.AppendText(string.Concat(lines));
                if (_gameAutoScroll) ScrollGameLogToEnd();
            });
        }
        catch { /* 窗口已关闭 */ }
    }

    private void ScrollGameLogToEnd()
    {
        try { GameLogBox.ScrollToEnd(); } catch { /* ignore */ }
    }

    #region 交互

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            Process.Start(new ProcessStartInfo { FileName = AppPaths.Logs, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[日志控制台] 打开日志目录失败:{ex.Message}");
        }
    }

    private void CopyApp_Click(object sender, RoutedEventArgs e) => CopyToClipboard(AppLogBox.Text);

    private void CopyGame_Click(object sender, RoutedEventArgs e) => CopyToClipboard(GameLogBox.Text);

    private void CopyToClipboard(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return;
            Clipboard.SetText(text);
        }
        catch { /* 剪贴板占用时静默失败 */ }
    }

    #endregion
}
