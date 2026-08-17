// App.xaml.cs — 应用入口
// 可爱的芙芙 - Minecraft启动器
//
// 职责:
// 1. 初始化 DI 容器,注册全部服务
// 2. 启动环境自检(运行时、OS 架构、Java、磁盘、网络)
// 3. 加载主题与背景
// 4. 捕获全局未处理异常,提示用户

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using FufuLauncher.Services;
using FufuLauncher.ViewModels;
using FufuLauncher.Views;

namespace FufuLauncher;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>统一数据目录:全部用户数据存放于此(即 AppPaths.Root = {exe目录}\APP\mcGAME)。
    /// 不再向 C 盘用户目录/AppData 写入任何业务数据。</summary>
    public static string AppDataDir { get; private set; } = string.Empty;

    // 应用日志批量写入:ConcurrentQueue 缓冲 + 200ms Timer 落地,避免高频同步 IO 阻塞调用线程
    private static readonly ConcurrentQueue<string> _appLogQueue = new();
    private static Timer? _appLogTimer;
    private static readonly object _appLogFileLock = new();
    /// <summary>日志文件最大大小(5MB),超过后自动轮转</summary>
    private const long MaxLogFileSize = 5L * 1024 * 1024;
    /// <summary>保留的历史日志文件数</summary>
    private const int MaxLogBackups = 3;
    /// <summary>当前日志文件对应的日期(按日滚动,防止单文件无限膨胀)</summary>
    private static DateTime _logFileDate = DateTime.MinValue;

    /// <summary>写入应用日志(入队,由后台 Timer 批量写入文件)</summary>
    public static void WriteAppLog(string message)
    {
        _appLogQueue.Enqueue($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
    }

    /// <summary>把队列中待写的日志刷到文件(由 Timer 周期触发或 ReadAppLog 调用前强制触发)</summary>
    private static void FlushAppLogQueue()
    {
        if (string.IsNullOrEmpty(AppDataDir) || _appLogQueue.IsEmpty) return;
        try
        {
            var lines = new List<string>();
            while (_appLogQueue.TryDequeue(out var line)) lines.Add(line);
            if (lines.Count == 0) return;
            string logFile = ResolveAppLogFileByDate();
            lock (_appLogFileLock)
            {
                // 日志轮转:超过 MaxLogFileSize 时重命名为 .1/.2/.3
                RotateLogFile(logFile);
                File.AppendAllLines(logFile, lines);
            }
        }
        catch { /* 忽略日志写入失败 */ }
    }

    /// <summary>日志轮转:超过大小限制时 app.log → app.log.1 → app.log.2 → 删除</summary>
    private static void RotateLogFile(string logFile)
    {
        try
        {
            if (!File.Exists(logFile)) return;
            var fi = new FileInfo(logFile);
            if (fi.Length < MaxLogFileSize) return;

            // 删除最旧的备份
            string oldest = logFile + "." + MaxLogBackups;
            if (File.Exists(oldest)) File.Delete(oldest);

            // 依次重命名 .2→.3, .1→.2, 当前→.1
            for (int i = MaxLogBackups - 1; i >= 1; i--)
            {
                string src = i == 1 ? logFile : logFile + "." + (i - 1);
                string dst = logFile + "." + i;
                if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            }
        }
        catch { /* 轮转失败不影响正常日志写入 */ }
    }

    /// <summary>按日期解析应用日志文件:当天写 app.log,跨天时旧文件滚动为 app-yyyyMMdd.log</summary>
    private static string ResolveAppLogFileByDate()
    {
        string current = FufuLauncher.Services.AppPaths.AppLogFile;
        try
        {
            var today = DateTime.Now.Date;
            if (_logFileDate != DateTime.MinValue && _logFileDate != today && File.Exists(current))
            {
                string archive = Path.Combine(
                    FufuLauncher.Services.AppPaths.Logs, $"app-{_logFileDate:yyyyMMdd}.log");
                try { File.Move(current, archive, overwrite: true); } catch { }
            }
            _logFileDate = today;
        }
        catch { }
        return current;
    }

    /// <summary>读取应用日志文件全文(供日志查看器调用)。读取前先强制刷新缓冲,确保最新日志落地</summary>
    public static string ReadAppLog()
    {
        try
        {
            FlushAppLogQueue();
            string logFile = FufuLauncher.Services.AppPaths.AppLogFile;
            if (File.Exists(logFile)) return File.ReadAllText(logFile);
            return "(暂无应用日志文件)";
        }
        catch (Exception ex) { return $"读取日志失败:{ex.Message}"; }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常捕获
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 初始化应用数据目录:统一固化到 {exe目录}\APP\mcGAME(含旧 appmcGAME 数据自动迁移)
        // 空间不足/无权限时弹窗友好提示,绝不回退 C 盘、绝不静默崩溃
        if (!FufuLauncher.Services.AppPaths.Initialize(out string pathError))
        {
            MessageBox.Show(pathError, "可爱的芙芙 - 数据目录初始化失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }
        AppDataDir = FufuLauncher.Services.AppPaths.Root;
        Directory.CreateDirectory(AppDataDir);
        // 启动日志定时器(AppDataDir 已就绪,Timer 可以安全写文件)
        _appLogTimer = new Timer(_ => FlushAppLogQueue(), null, 200, 200);
        foreach (var note in FufuLauncher.Services.AppPaths.InitNotes)
            WriteAppLog($"[数据目录] {note}");
        WriteAppLog($"[数据目录] 数据根目录={AppDataDir}");

        // 配置 DI 容器
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // 加载配置
        var config = Services.GetRequiredService<ConfigService>();
        config.Load();

        WriteAppLog($"=== 启动器启动 === 版本 {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} 下载源={config.Config.DownloadSource}");

        // 应用日志自动清理:登记启动计数,每 N 次启动触发一次后台清理(不阻塞 UI)
        try
        {
            var logCleanup = Services.GetRequiredService<LogCleanupService>();
            if (logCleanup.TickAndShouldClean())
            {
                WriteAppLog("[日志清理] 已达自动清理节拍,后台执行清理");
                _ = logCleanup.RunCleanupAsync();
            }
        }
        catch (Exception ex) { WriteAppLog($"[日志清理] 执行异常:{ex.Message}"); }

        // 段5:启动内存监控定时刷新(供设置页/主页可视化展示)
        var memMonitor = Services.GetRequiredService<MemoryMonitorService>();
        memMonitor.Start();

        // 手动从 DI 创建并显示主窗口(不能用 StartupUri,因为 MainWindow 需要依赖注入)
        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        WriteAppLog("=== 启动器退出 ===");
        // 停止定时器,释放资源
        try { Services.GetRequiredService<MemoryMonitorService>().Stop(); } catch { /* ignore */ }
        try { Services.GetRequiredService<GameMemoryWatchService>().Stop(); } catch { /* ignore */ }
        try { Services.GetRequiredService<ThemeService>().Shutdown(); } catch { /* ignore */ }
        // 子进程托管回收:强制终止全部残留子进程(游戏/探测/外部工具),杜绝僵尸进程
        try { Services.GetRequiredService<FufuLauncher.Services.ProcessGuardService>().KillAll(); } catch { /* ignore */ }
        // 最后一次刷新日志缓冲
        _appLogTimer?.Dispose();
        _appLogTimer = null;
        FlushAppLogQueue();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // 基础服务
        services.AddSingleton<ConfigService>();
        services.AddSingleton<EnvironmentCheckService>();
        services.AddSingleton<JavaScanService>();
        services.AddSingleton<NetworkService>();
        services.AddSingleton<DownloadService>();
        services.AddSingleton<VersionManifestService>();
        services.AddSingleton<InstanceService>();
        services.AddSingleton<ModLoaderInstallService>();
                services.AddSingleton<LoaderVersionProvider>();
        services.AddSingleton<HashVerifyService>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<ModManagerService>();
        services.AddSingleton<ResourcePackService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<GameLaunchService>();
        services.AddSingleton<GameLogService>();
        services.AddSingleton<NativeInteropService>();
        services.AddSingleton<GameInstallService>();
        services.AddSingleton<JavaRuntimeService>();
        services.AddSingleton<MemoryMonitorService>();
        services.AddSingleton<ModrinthService>();
        services.AddSingleton<UpdateService>();      // 程序自身更新检测与应用框架
        services.AddSingleton<ProcessGuardService>(); // 子进程托管中心(退出时强制回收)
        services.AddSingleton<GameMemoryWatchService>(); // 游戏进程堆+堆外内存监控(暴涨预警)
        services.AddSingleton<LogCleanupService>();   // 应用日志自动清理

        // 视图模型
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<NavigationViewModel>(); // 侧边栏导航

        // 页面视图(Transient:每次导航新建,由 DI 自动注入依赖)
        services.AddTransient<HomePage>();
        services.AddTransient<DownloadPage>();
        services.AddTransient<JavaRuntimePage>();
        services.AddTransient<ModsPage>();
        services.AddTransient<MarketPage>();
        services.AddTransient<ManageVersionsPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<AboutPage>();

        // 主窗口(单例,由 DI 创建)
        services.AddSingleton<MainWindow>();
    }

    /// <summary>崩溃前把完整异常写入 Logs\crash-yyyyMMdd-HHmmss.log,便于事后排查</summary>
    private static void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            WriteAppLog($"[崩溃-{source}] {ex}");
            if (string.IsNullOrEmpty(AppDataDir)) return;
            string crashDir = FufuLauncher.Services.AppPaths.Logs;
            Directory.CreateDirectory(crashDir);
            string file = Path.Combine(crashDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(file,
                $"=== 可爱的芙芙 崩溃报告 ({source}) ===\n时间:{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"版本:{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}\n\n{ex}");
        }
        catch { /* 崩溃日志写入失败不再报错 */ }
    }

    /// <summary>UI线程未处理异常:隐藏底层堆栈,仅显示中文友好提示</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("UI", e.Exception);
        string friendlyMsg = GetFriendlyCrashMessage(e.Exception);
        MessageBox.Show(friendlyMsg, "可爱的芙芙 - 出错了",
                          MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;  // 标记已处理,不闪退
    }

    /// <summary>后台线程致命异常:隐藏底层堆栈,仅显示中文友好提示</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            WriteCrashLog("域", ex);
            string friendlyMsg = GetFriendlyCrashMessage(ex);
            MessageBox.Show(friendlyMsg, "可爱的芙芙 - 严重错误",
                              MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>将崩溃异常转换为中文友好提示(隐藏技术堆栈,仅保留用户可理解的信息)</summary>
    private static string GetFriendlyCrashMessage(Exception ex)
    {
        string baseMsg = "启动器遇到一个未预期的错误,请尝试以下操作：\n\n";

        // 根据异常类型给出针对性建议
        if (ex is OutOfMemoryException)
            baseMsg += "内存不足。请关闭其他程序后重试,或增大虚拟内存。";
        else if (ex is IOException && ex.Message.Contains("空间"))
            baseMsg += "磁盘空间不足,无法写入文件。请清理磁盘后重试。";
        else if (ex is UnauthorizedAccessException)
            baseMsg += "文件访问被拒绝。请检查杀毒软件是否拦截,或以管理员身份运行。";
        else if (ex is System.Net.Http.HttpRequestException)
            baseMsg += "网络连接异常。请检查网络后重试,或尝试切换下载源。";
        else if (ex is TaskCanceledException || ex is TimeoutException)
            baseMsg += "操作超时。请检查网络状况后重试。";
        else if (ex is InvalidOperationException)
            baseMsg += "程序状态异常。请尝试重启启动器。";
        else if (ex is NullReferenceException || ex is ArgumentNullException)
            baseMsg += "程序数据不完整。请尝试重启启动器。";
        else if (ex is System.Text.Json.JsonException)
            baseMsg += "配置文件损坏。请尝试删除 APP\\mcGAME\\config.json 后重启。";
        else
            baseMsg += "请尝试重启启动器。如问题持续,请查看 APP\\mcGAME\\日志 目录下的崩溃日志。";

        baseMsg += "\n\n详细错误信息已保存到 APP\\mcGAME\\日志 目录的崩溃日志中。";
        return baseMsg;
    }

    /// <summary>处理 Task 中未观察的异常(async void / 未 await 的 Task 抛出且未被捕获)</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteAppLog($"[异常-Task] {e.Exception}");
        // 后台任务异常只记录日志+提示,不打断用户当前操作(防止弹窗风暴)
        e.SetObserved();  // 标记已观察,阻止进程终止
    }
}
