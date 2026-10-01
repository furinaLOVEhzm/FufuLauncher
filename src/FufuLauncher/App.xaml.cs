// App.xaml.cs — 应用入口
// FufuLauncher - Minecraft启动器
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
using FufuLauncher.Next.UI;

namespace FufuLauncher;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>统一数据目录:全部用户数据存放于此(即 AppPaths.Root = {exe目录}\APP\mcGAME)。
    /// 不再向 C 盘用户目录/AppData 写入任何业务数据。</summary>
    public static string AppDataDir { get; private set; } = string.Empty;

    /// <summary>单实例互斥体(2026-08-28 新增):同目录只允许一个启动器窗口,
    /// 多开时第二个实例静默自行退出——不弹任何提示、不影响已在运行的实例。
    /// 字段持有防止 GC 提前释放;进程退出时系统自动回收。</summary>
    private static Mutex? _singleInstanceMutex;

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

    /// <summary>分级日志等级(重构要求 ⑦-2:玩家友好提示 + 开发者详细调试日志)</summary>
    public enum AppLogLevel { Debug, Info, Warn, Error }

    /// <summary>开发者调试日志开关:关闭时 Debug 级不落盘(由设置页切换并持久化)</summary>
    public static bool VerboseLogEnabled { get; private set; }

    /// <summary>运行时切换调试日志等级(设置页保存时调用)</summary>
    public static void SetVerboseLog(bool enabled)
    {
        VerboseLogEnabled = enabled;
        WriteLog(AppLogLevel.Info, $"[日志] 开发者调试日志已{(enabled ? "开启" : "关闭")}");
    }

    /// <summary>写入应用日志(默认 INFO 级,入队由后台 Timer 批量写入文件)</summary>
    public static void WriteAppLog(string message) => WriteLog(AppLogLevel.Info, message);

    /// <summary>分级写日志:Debug 受开关控制;Info/Warn/Error 始终落盘,
    /// 便于普通玩家排障时日志简洁、开发者开启开关后可见完整调试链路</summary>
    public static void WriteLog(AppLogLevel level, string message)
    {
        if (level == AppLogLevel.Debug && !VerboseLogEnabled) return;
        string tag = level switch
        {
            AppLogLevel.Debug => "DEBUG",
            AppLogLevel.Warn => "WARN ",
            AppLogLevel.Error => "ERROR",
            _ => "INFO "
        };
        // 2026-09-25 防日志风暴:异常死循环/重试风暴刷日志时队列不无限涨(5000 条上限,超限丢弃)
        if (_appLogQueue.Count < 5000)
            _appLogQueue.Enqueue($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}][{tag}] {message}");
    }

    /// <summary>把队列中待写的日志刷到文件(由 Timer 周期触发或 ReadAppLog 调用前强制触发)。
    /// 其中 [AI]/[泡芙助理] 前缀的行额外同步写入 日志\AI\ai.log 专夹,便于单独排查 AI 问题</summary>
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

                // AI 专夹日志:日志\AI\ai.log(同样带轮转,只收 AI 相关行)
                try
                {
                    var aiLines = lines.Where(l => l.Contains("[AI]") || l.Contains("[泡芙助理]")).ToList();
                    if (aiLines.Count > 0)
                    {
                        string aiDir = Path.Combine(FufuLauncher.Services.AppPaths.Logs, "AI");
                        Directory.CreateDirectory(aiDir);
                        string aiFile = Path.Combine(aiDir, "ai.log");
                        RotateLogFile(aiFile);
                        File.AppendAllLines(aiFile, aiLines);
                    }
                }
                catch { /* AI 专夹写入失败不影响主日志 */ }
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
        // 启动分段计时(2026-08-29 新增):老机器启动异常时凭日志直接定位卡在哪一段,
        // 不再靠猜。各段耗时(毫秒)随普通日志一并落盘。
        var bootWatch = System.Diagnostics.Stopwatch.StartNew();
        long lastMark = 0;
        bool preludeLogged = false;
        void Mark(string stage)
        {
            long now = bootWatch.ElapsedMilliseconds;
            if (!preludeLogged)
            {
                preludeLogged = true;
                // 进程启动→OnStartup 首段:单文件宿主自解压/校验 + CLR 启动的纯前置开销,
                // 应用代码无法优化,单列出来避免与业务段混淆(杀软扫描也体现在这里)
                try
                {
                    var procStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;
                    long prelude = (long)(DateTime.Now - procStart).TotalMilliseconds - now;
                    WriteAppLog($"[启动计时] 前奏(进程启动→OnStartup,含自解压/CLR/杀软) {Math.Max(0, prelude)}ms");
                }
                catch { /* 忽略 */ }
            }
            WriteAppLog($"[启动计时] {stage} +{now - lastMark}ms 累计{now}ms");
            lastMark = now;
        }

        // 单实例门禁(最早执行,先于异常钩子/数据目录/窗口创建):
        // 互斥名按部署根哈希区分——数据目录随部署根走(便携式设计),
        // 同部署根双开会争抢 config/账号/实例等文件必须拦截;不同部署根的多份部署互不影响。
        // 【历史】8.17 曾为入口壳加过 FUFU_ENTRY=stub 握手接管分支,壳方案废弃后已删除(见 8.18)。
        // Local\ 前缀限定当前会话+当前用户,无需管理员权限。
        try
        {
            string deployRoot = FufuLauncher.Services.AppPaths.ResolveDeployRoot().ToLowerInvariant();
            string hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(deployRoot)))[..16];
            string mutexName = $@"Local\FufuLauncher_{hash}";

            _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);
            if (!createdNew)
            {
                // 已有实例在运行:静默退出,不弹窗、不打扰已开的窗口(用户要求:自动关掉多开的窗口)
                Shutdown();
                return;
            }
        }
        catch
        {
            // 互斥体创建失败(极罕见,如名称被占用)不拦截启动,降级放行,避免误杀正常启动
        }

        // 全局异常捕获
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 初始化应用数据目录:统一固化到 {exe目录}\APP\mcGAME(含旧 appmcGAME 数据自动迁移)
        // 空间不足/无权限时弹窗友好提示,绝不回退 C 盘、绝不静默崩溃
        if (!FufuLauncher.Services.AppPaths.Initialize(out string pathError))
        {
            MessageBox.Show(pathError, "FufuLauncher - 数据目录初始化失败",
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
        Mark("数据目录就绪");

        // 单文件自解压残留清理(2026-08-29 松散部署配套):现已改为松散部署不再产生自解压缓存,
        // 历史版本残留的 %TEMP%\.net\FufuLauncher 目录(排查时曾积到 810MB)后台一次性清光;
        // 当前实例已不经此路径运行,全部可删;删除失败(占用)即跳过,不影响任何功能。
        _ = Task.Run(() =>
        {
            try
            {
                string bundleRoot = Path.Combine(Path.GetTempPath(), ".net", "FufuLauncher");
                if (!Directory.Exists(bundleRoot)) return;
                int removed = 0;
                foreach (var d in new DirectoryInfo(bundleRoot).GetDirectories())
                {
                    try { d.Delete(recursive: true); removed++; }
                    catch { /* 可能被占用,跳过 */ }
                }
                if (removed > 0)
                {
                    WriteAppLog($"[启动提速] 已清理 {removed} 个历史自解压缓存残留(%TEMP%\\.net\\FufuLauncher)");
                }
            }
            catch { /* 缓存清理失败不影响任何功能 */ }
        });

        // 配置 DI 容器
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
        Mark("DI 容器构建完成");

        // 加载配置
        var config = Services.GetRequiredService<ConfigService>();
        config.Load();
        // 分级日志开关随配置生效(Debug 级是否落盘)
        VerboseLogEnabled = config.Config.VerboseLog;

        // 2026-09-30:自有镜像启用时把实际填的地址一起写进启动日志——
        // 装机失败排查时第一件要确认的就是「当时到底在从哪儿下」
        WriteAppLog($"=== 启动器启动 === 版本 {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} 下载源={config.Config.DownloadSource}" +
                    (config.Config.DownloadSource == "Custom"
                        ? $" 地址={config.Config.CustomDownloadBaseUrl}"
                        : ""));

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

        // 段5:内存监控延迟到主窗口 Loaded 后启动(见 MainWindow_OnLoaded),避免与首屏渲染争抢启动时间

        // 主题系统初始化:预设 + 扫描 tupian\jm 自定义主题 + 恢复用户设置
        // (必须在令牌字典合并进 App.Resources 之前完成,窗口创建时令牌已就绪)
        try { FufuLauncher.Theme.ThemeManager.Initialize(); }
        catch (Exception ex) { WriteAppLog($"[主题] 初始化异常(降级默认令牌):{ex.Message}"); }
        Resources.MergedDictionaries.Add(FufuLauncher.Theme.ThemeManager.TokenDict);
        Mark("主题系统初始化完成");

        // 账号与本地版本加载:AccountService 构造时从 accounts 目录加载并恢复上次账号;
        // InstanceService 加载本地游戏版本列表(供主页/版本页直接使用)
        try
        {
            Services.GetRequiredService<AccountService>();
            Services.GetRequiredService<InstanceService>().LoadInstances();
            // 实例级扩展配置(快照/分组/启动记录/脚本/JVM模板/日志筛选收藏)开机一次性读入
            Services.GetRequiredService<InstanceExtrasService>().Load();
        }
        catch (Exception ex) { WriteAppLog($"[启动] 账号/版本加载异常:{ex.Message}"); }
        Mark("账号/实例加载完成");
        var shell = new ShellWindow();
        MainWindow = shell;
        Mark("主窗口构造完成");
        shell.Show();
        Mark("主窗口已显示");

        // 下载队列恢复(2026-09-27 后台/重启续传):主窗口显示后再拉起,
        // 仅在有上次未完成任务时才真正入队下载,不抢首屏启动时间
        _ = Task.Run(() =>
        {
            try { Services.GetRequiredService<DownloadService>().RestoreQueue(); }
            catch (Exception ex) { WriteAppLog($"[下载] 队列恢复异常(忽略):{ex.Message}"); }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        WriteAppLog("=== 启动器退出 ===");
        // 释放单实例互斥体(进程退出时系统也会自动回收,此处显式释放更干净)
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* 未持有时忽略 */ }
        try { _singleInstanceMutex?.Dispose(); } catch { /* ignore */ }
        _singleInstanceMutex = null;
        // 停止定时器,释放资源
        try { Services.GetRequiredService<MemoryMonitorService>().Stop(); } catch { /* ignore */ }
        try { Services.GetRequiredService<GameMemoryWatchService>().Stop(); } catch { /* ignore */ }
        try { Services.GetRequiredService<ThemeService>().Shutdown(); } catch { /* ignore */ }
        // 子进程托管回收:强制终止全部残留子进程(游戏/探测/外部工具),杜绝僵尸进程
        try { Services.GetRequiredService<FufuLauncher.Services.ProcessGuardService>().KillAll(); } catch { /* ignore */ }
        // 后台预加载线程收尾(没被用到过时 Created 为 null,不会为了退出反而新建一个线程)
        try { FufuLauncher.Services.DependencyPreloadService.Created?.Dispose(); } catch { /* ignore */ }
        // 实例级扩展配置刷盘:未写出的快照/分组/启动记录等改动一并落盘
        try
        {
            var extras = Services.GetRequiredService<FufuLauncher.Services.InstanceExtrasService>();
            if (extras.HasPendingChanges) extras.Save();
        }
        catch { /* ignore */ }
        // 下载队列断点刷盘(2026-09-27 后台/重启续传):退出前把未完成任务与
        // 分片段位表写下,下次启动自动接着下;队列已空则删除断点文件
        try { Services.GetRequiredService<FufuLauncher.Services.DownloadService>().SaveQueue(); }
        catch { /* ignore */ }
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
        services.AddSingleton<VersionPatchService>();
        services.AddSingleton<LoaderVersionProvider>();
        services.AddSingleton<HashVerifyService>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<ModManagerService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<GameLaunchService>();
        services.AddSingleton<GameLogService>();
        services.AddSingleton<NativeInteropService>();
        services.AddSingleton<StorageGuardService>(); // 磁盘空间/文件锁/权限预检 + IO异常友好分类
        services.AddSingleton<GameInstallService>();
        services.AddSingleton<JavaRuntimeService>();
        services.AddSingleton<MemoryMonitorService>();
        services.AddSingleton<ModrinthService>();
        services.AddSingleton<ModPackImportService>(); // 多格式整合包解析与导入(Modrinth/CF/Prism/懒人包)
        services.AddSingleton<UpdateService>();      // 程序自身更新检测与应用框架
        services.AddSingleton<ProcessGuardService>(); // 子进程托管中心(退出时强制回收)
        services.AddSingleton<GameMemoryWatchService>(); // 游戏进程堆+堆外内存监控(暴涨预警)
        services.AddSingleton<LogCleanupService>();   // 应用日志自动清理
        services.AddSingleton<AiModelLibraryService>(); // 泡芙助理本地模型库:用户拖入的 GGUF 模型登记/切换/删除(程序不内置、不下载模型)
        services.AddSingleton<AiEngineService>();     // 泡芙助理本地推理引擎(加载模型库活跃项,部署了 CUDA 则优先显存全量利用,否则 CPU)
        services.AddSingleton<AiAssistantService>();  // 泡芙助理:NL意图解析 + 一句话生成模组整合包
        services.AddSingleton<GameLogFixService>();   // 游戏日志自愈:OOM 自动加内存,复杂问题只记建议
        services.AddSingleton<LaunchTuneService>();   // 启动自动调参:经典方案(总内存档位表+安全上限),不依赖 AI

        // ===== 实例级扩展存储与四大参考启动器功能服务 =====
        services.AddSingleton<InstanceExtrasService>();   // 根级扩展配置(快照/分组/启动记录/脚本/磁盘缓存/JVM模板/日志筛选收藏)
        services.AddSingleton<ConfigSnapshotService>();   // Axolotl-1 实例多配置快照
        services.AddSingleton<ModDiagnosticsService>();   // Axolotl-2/3 冲突诊断与版本匹配 + Celestial-3 依赖树 + LauncherX-7 更新聚合
        services.AddSingleton<GameLogFilterService>();    // Axolotl-4 日志智能筛选面板
        services.AddSingleton<InstanceCloneService>();    // Axolotl-5 实例快速克隆
        services.AddSingleton<DependencyPreloadService>(); // Axolotl-6 后台预加载依赖(独立低优先级线程)
        services.AddSingleton<IntegrityRepairService>();  // BlockHelm-3 环境完整性校验与自动修复
        services.AddSingleton<MemoryRecommendService>();  // BlockHelm-4 内存配置智能推荐
        services.AddSingleton<ModBatchService>();         // BlockHelm-2 本地模组批量管理
        services.AddSingleton<ModSearchService>();        // LauncherX-2 模组搜索增强(名称/modid/作者)
        services.AddSingleton<ZipDropService>();          // LauncherX-3 拖拽 zip 自动识别
        services.AddSingleton<ModPackExportService>();    // LauncherX-6 完整整合包导出(两种模式)
        services.AddSingleton<OfflineSkinService>();      // Celestial-1 离线账号与本地皮肤
        services.AddSingleton<JvmTemplateService>();      // Celestial-2 JVM 参数预设模板库
        services.AddSingleton<CrashReportService>();      // Celestial-4 崩溃报告一键复制
        services.AddSingleton<DiskUsageService>();        // Celestial-5 磁盘占用统计
        services.AddSingleton<ModPackLinkService>();      // Celestial-6 整合包链接导入
        services.AddSingleton<LaunchScriptService>();     // Celestial-7 实例前后置脚本
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
                $"=== FufuLauncher 崩溃报告 ({source}) ===\n时间:{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"版本:{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}\n\n{ex}");
        }
        catch { /* 崩溃日志写入失败不再报错 */ }
    }

    /// <summary>UI线程未处理异常:隐藏底层堆栈,仅显示中文友好提示</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("UI", e.Exception);
        string friendlyMsg = GetFriendlyCrashMessage(e.Exception);
        MessageBox.Show(friendlyMsg, "FufuLauncher - 出错了",
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
            MessageBox.Show(friendlyMsg, "FufuLauncher - 严重错误",
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
