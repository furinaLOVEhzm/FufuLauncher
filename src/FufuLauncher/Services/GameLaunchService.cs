// GameLaunchService.cs — 游戏启动服务(完整重写)
// FufuLauncher
//
// 启动流程:
// 1. 解析 version JSON(含 inheritsFrom 继承链)获取 mainClass、libraries、arguments
// 2. 构建 classpath(从 version JSON libraries 列表解析路径,非暴力遍历)
// 3. 解压 natives DLL 到 natives 目录
// 4. 从 version JSON 构建 JVM + 游戏参数(支持新旧两种格式 + rules 过滤)
// 5. 占位符替换(${auth_player_name} 等)
// 6. 启动 Java 进程 + 进程优化(内存/GC/CPU 亲和性)
// 7. 监控进程退出,更新游玩时间

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FufuLauncher.Next.UI; // 统一弹窗体系(DialogKit),启动期确认框与主界面风格一致

namespace FufuLauncher.Services;

public class LaunchResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = "";
    public Process? Process { get; set; }
}

public class GameLaunchService
{
    private readonly InstanceService _instanceService;
    private readonly AccountService _accountService;
    private readonly GameLogService _gameLog;
    private readonly ConfigService _configService;
    private readonly JavaRuntimeService _javaRuntimeService;
    private readonly JavaScanService _javaScanService;
    private readonly MemoryMonitorService _memoryMonitor;
    private bool _autoDegraded;   // 本次启动是否触发过内存自动降级(供启动后状态提示)
    private readonly ProcessGuardService _processGuard;
    private readonly GameMemoryWatchService _memoryWatch;
    private readonly ModLoaderInstallService _loaderInstall;
    private readonly AiEngineService _aiEngine;   // 游戏启动后静默卸载:不占显卡性能与内存(仅 YesAI 有模型时生效)
    private readonly LaunchTuneService _autoTune;   // 启动自动调参:经典方案不依赖 AI,只在打开游戏时介入一次
    private readonly InstanceExtrasService _extras;      // 启动会话记录落地(BlockHelm-6)
    private readonly IntegrityRepairService _integrity;  // 启动前环境完整性校验与自动修复(BlockHelm-3)
    private readonly GameLogFixService _logFix;          // 游戏日志自愈:OOM 自动加内存等(会话级)
    private readonly LaunchScriptService _scripts;       // 实例前后置脚本(Celestial-7)
    private readonly CrashReportService _crash;          // 崩溃原因摘要(BlockHelm-6 记录用)

    private Process? _currentProcess;

    public GameLaunchService(InstanceService instanceService,
                                VersionManifestService versionManifest,
                                HashVerifyService hashVerify,
                                AccountService accountService,
                                GameLogService gameLog,
                                ConfigService configService,
                                JavaRuntimeService javaRuntimeService,
                                JavaScanService javaScanService,
                                MemoryMonitorService memoryMonitor,
                                ProcessGuardService processGuard,
                                GameMemoryWatchService memoryWatch,
                                ModLoaderInstallService loaderInstall,
                                AiEngineService aiEngine,
                                LaunchTuneService autoTune,
                                InstanceExtrasService extras,
                                IntegrityRepairService integrity,
                                GameLogFixService logFix,
                                LaunchScriptService scripts,
                                CrashReportService crash)
    {
        _instanceService = instanceService;
        _accountService = accountService;
        _gameLog = gameLog;
        _configService = configService;
        _javaRuntimeService = javaRuntimeService;
        _javaScanService = javaScanService;
        _memoryMonitor = memoryMonitor;
        _processGuard = processGuard;
        _memoryWatch = memoryWatch;
        _loaderInstall = loaderInstall;
        _aiEngine = aiEngine;
        _autoTune = autoTune;
        _extras = extras;
        _integrity = integrity;
        _logFix = logFix;
        _scripts = scripts;
        _crash = crash;
    }

    public bool IsGameRunning
    {
        get
        {
            var p = _currentProcess;
            if (p == null) return false;
            try { return !p.HasExited; }
            catch { return false; }   // 进程对象已释放(Detach/Dispose 后)视为未运行
        }
    }

    /// <summary>游戏进程退出事件(后台线程触发,订阅者需自行切 UI 线程)</summary>
    public event Action? GameExited;
    /// <summary>游戏进程堆外内存持续暴涨预警(供 UI 状态栏展示;仅在游戏运行期触发)</summary>
    public event Action<string>? GameMemoryWarning;
    /// <summary>游戏进程实时内存快照(堆+堆外,UI 线程触发,供主页运行状态展示)</summary>
    public event Action<GameMemorySnapshot>? GameMemoryUpdated;

    /// <summary>当前正在运行的游戏实例 Id(无游戏运行时为 null)。供卸载拦截使用:禁止卸载运行中的版本</summary>
    public string? RunningInstanceId { get; private set; }

    /// <summary>指定实例是否正在运行</summary>
    public bool IsInstanceRunning(string instanceId) =>
        IsGameRunning && RunningInstanceId == instanceId;

    // ==================== 启动入口 ====================

    /// <summary>
    /// 启动入口外层:包一层启动会话记录(BlockHelm-6)。
    /// 不管里面哪个环节失败都能留下一条记录,不用在每个 return 分支里重复写。
    /// </summary>
    public async Task<LaunchResult> LaunchAsync(string instanceId, bool forceLaunch = false)
    {
        var session = BeginLaunchSession(instanceId);
        LaunchResult result;
        try
        {
            result = await LaunchCoreAsync(instanceId, forceLaunch, session);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[启动] 未预期异常:{ex}");
            result = new LaunchResult { Success = false, ErrorMessage = ex.Message };
        }

        // 进程真正拉起来后记录已经在内层写了;走到这里还是失败说明没起来,补一条失败记录
        if (!result.Success)
        {
            session.EndTime = DateTime.Now;
            session.Success = false;
            session.ExitCode = -1;
            if (string.IsNullOrEmpty(session.Reason)) session.Reason = TruncReason(result.ErrorMessage ?? "");
            SafeAddLaunchRecord(instanceId, session);
        }
        return result;
    }

    private async Task<LaunchResult> LaunchCoreAsync(string instanceId, bool forceLaunch, LaunchSessionRecord session)
    {
        var inst = _instanceService.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null)
            return new LaunchResult { Success = false, ErrorMessage = "游戏版本不存在" };

        // 防重复启动:同一时刻只允许一个游戏进程在运行,避免重复点击拉起第二个 Java 进程
        if (IsGameRunning)
            return new LaunchResult { Success = false, ErrorMessage = "游戏正在运行中,请先结束当前游戏后再启动" };

        // ---- 0. 启动自动调参(经典方案不依赖 AI,学习主流启动器做法;只做内存审计记录,毫秒级完成,异常不阻塞启动;
        //      JVM 参数填充推迟到第 5 步实际 Java 版本确定后执行,见 FillJvmArgsIfNeeded)----
        _autoTune.TuneBeforeLaunch(inst);

        if (forceLaunch)
            App.WriteAppLog($"[启动] ⚡ 强制启动模式(跳过前置校验)");

        // ---- 1. 校验账号(强制模式跳过)----
        if (!forceLaunch)
        {
            bool tokenOk = await _accountService.EnsureValidTokenAsync();
            if (!tokenOk)
                return new LaunchResult { Success = false, ErrorMessage = "账号令牌已过期且无法刷新,请重新登录" };
        }

        // ---- 1.5 自动修复:加载器已标记但本体缺失时,启动前补装 ----
        // 覆盖三种"空壳版本"(名字带加载器却按原版启动、模组不加载的暗病根源):
        //  A) ModLoader 非空但 LoaderVersionId 空——旧版安装中断,元数据只写了加载器类型
        //  B) LoaderVersionId 非空但 versions 里对应 JSON 缺失——版本文件未落盘/被清理
        //  C) 元数据全空但版本名含加载器关键字——加载器安装失败残留,此前启动器静默按原版启动,
        //     用户完全无感知"装了加载器却不生效";现在弹窗让用户决策(补装/按原版/取消)
        if (!forceLaunch)
        {
            // 0) 懒人包/第三方启动器导出的整合包自带完整版本(如 "26.1.2-NeoForge_26.1.2.78"):
            //    加载器与游戏本体已合并在同一份版本 JSON 里,但实例的 LoaderVersionId 是空的。
            //    先认领这个版本作为加载器本体,否则下面的空壳判定会把已装好的加载器当成缺失,
            //    启动前反复下载安装器 —— 网络源不稳时必然失败,用户看到的就是「导入完了还要补全」。
            string? baked = DetectBakedLoaderVersionId(inst, out string? bakedKey);
            if (baked != null && inst.LoaderVersionId != baked)
            {
                inst.LoaderVersionId = baked;
                // 元数据全空的案例 C:认领后把加载器类型回填,否则版本卡仍显示「⚠ 加载器未装」
                if (string.IsNullOrEmpty(inst.ModLoader) && bakedKey != null)
                    inst.ModLoader = LoaderLabel(bakedKey);
                _instanceService.SaveInstance(inst);
                App.WriteAppLog($"[启动] 版本 [{inst.Name}] 的加载器本体已合并于 {baked},无需补装");
            }

            bool loaderMarked = !string.IsNullOrEmpty(inst.ModLoader);
            bool loaderJsonMissing = !string.IsNullOrEmpty(inst.LoaderVersionId)
                && !File.Exists(Path.Combine(AppPaths.Versions, inst.LoaderVersionId!, $"{inst.LoaderVersionId}.json"));
            bool nameSuggestsLoader = false;
            string? nameLoader = null, nameLoaderVer = null;
            if (string.IsNullOrEmpty(inst.ModLoader))
                nameSuggestsLoader = TryParseLoaderFromName(inst.Name, out nameLoader, out nameLoaderVer);

            if (loaderMarked && string.IsNullOrEmpty(inst.LoaderVersionId))
            {
                // A:补装已标记但缺 LoaderVersionId 的加载器(旧版安装中断残留)
                string? repairErr = await RepairLoaderAsync(inst);
                if (repairErr != null)
                    return new LaunchResult { Success = false, ErrorMessage = repairErr };
                // RepairLoaderAsync 内部 await 期间,UI 线程可能触发 RefreshInstances() 整个列表 Clear+AddRange,
                // 持有的旧 inst 引用 LoaderVersionId 仍是空,必须重新从列表拿最新对象,否则后续会回退原版版本
                inst = _instanceService.Instances.FirstOrDefault(i => i.Id == instanceId) ?? inst;
                App.WriteAppLog($"[启动] 补装后刷新实例引用:LoaderVersionId={inst.LoaderVersionId ?? "(空)"},ModLoader={inst.ModLoader ?? "(空)"}");
            }
            else if (loaderJsonMissing)
            {
                // B:加载器版本 JSON 丢失——先清掉失效引用再补装,避免启动直接报"版本文件缺失"
                App.WriteAppLog($"[启动] 加载器版本 JSON 缺失({inst.LoaderVersionId}),重置引用后补装 {inst.ModLoader}");
                inst.LoaderVersionId = null;
                _instanceService.SaveInstance(inst);
                string? repairErr = await RepairLoaderAsync(inst);
                if (repairErr != null)
                    return new LaunchResult { Success = false, ErrorMessage = repairErr };
                inst = _instanceService.Instances.FirstOrDefault(i => i.Id == instanceId) ?? inst;
            }
            else if (nameSuggestsLoader)
            {
                // C:名字带加载器关键字但元数据全空——不静默进原版,让用户决策
                string loaderDisplay = nameLoader ?? "加载器";
                string verDisplay = string.IsNullOrEmpty(nameLoaderVer) ? "" : $" {nameLoaderVer}";
                int choice = DialogKit.Choice(
                    $"版本「{inst.Name}」名字包含『{loaderDisplay}』,但未检测到已安装的加载器本体。\n\n" +
                    "这通常是之前加载器安装中断/失败留下的空壳版本:\n" +
                    "· 「补装」会按名字重新安装该加载器(需联网,较慢)\n" +
                    "· 「按原版启动」只进原版游戏,该版本的模组不会被加载\n\n" +
                    "如何处理?",
                    new[] { $"补装 {loaderDisplay}{verDisplay}", "按原版启动", "取消启动" },
                    "检测到空壳加载器版本", System.Windows.Application.Current?.MainWindow);
                if (choice == 0)
                {
                    string? repairErr = await RepairLoaderByNameAsync(inst);
                    if (repairErr != null)
                        return new LaunchResult { Success = false, ErrorMessage = repairErr };
                    inst = _instanceService.Instances.FirstOrDefault(i => i.Id == instanceId) ?? inst;
                }
                else if (choice == 2)
                    return new LaunchResult { Success = false, ErrorMessage = $"用户取消启动(空壳加载器版本 {inst.Name})" };
                else
                    App.WriteAppLog($"[启动] 用户选择按原版启动空壳版本 {inst.Name}(模组不会加载)");
            }
        }
        
        // ---- 2. 解析 Java 路径(优先内置 runtimes,无则兜底系统已装 Java)----
        string javaPath = await ResolveJavaPathAsync(inst);
        if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
        {
            return new LaunchResult
            {
                Success = false,
                ErrorMessage = $"Java 运行时缺失,请前往【☕ Java 运行时】页面下载 Java {inst.JavaMajorVersion}"
            };
        }

        // Java 完整性校验(强制模式跳过)
        if (!forceLaunch && !JavaRuntimeService.VerifyJavaIntegrity(javaPath))
        {
            App.WriteAppLog($"[启动] Java 完整性校验失败:{javaPath}");
            bool cancel = !DialogKit.Confirm(
                $"Java 文件存在但无法执行(java -version 失败):\n{javaPath}\n\n是否取消启动?(选「取消」则仍尝试启动)",
                "Java 校验未通过", "取消启动", danger: true,
                owner: System.Windows.Application.Current?.MainWindow);
            if (cancel)
                return new LaunchResult { Success = false, ErrorMessage = "Java 完整性校验未通过" };
        }

        // ---- 3. 校验游戏文件(游戏本体在全局共享 versions 目录)----
        string mcDir = _instanceService.GetMinecraftDir(instanceId); // 游戏工作目录
        // 装了加载器的实例优先用加载器版本 JSON(inheritsFrom 原版),否则用原版
        string launchVersionId = string.IsNullOrEmpty(inst.LoaderVersionId) ? inst.VersionId : inst.LoaderVersionId!;
        string versionJsonPath = Path.Combine(AppPaths.Versions, launchVersionId, $"{launchVersionId}.json");
        if (!File.Exists(versionJsonPath))
            return new LaunchResult { Success = false, ErrorMessage = $"版本文件缺失:{versionJsonPath}" };

        // 客户端 jar 按 inheritsFrom 链回溯到原版版本(加载器版本目录里没有 jar);
        // 独立式老 Forge JSON(1.7.10 等,无 inheritsFrom)再按 minecraftVersion 字段回退到原版
        string baseVersionId = ResolveBaseVersionId(launchVersionId);
        string clientJar = Path.Combine(AppPaths.Versions, baseVersionId, $"{baseVersionId}.jar");
        if (!File.Exists(clientJar))
        {
            string mcVer = ResolveMinecraftVersion(launchVersionId);
            if (!string.IsNullOrEmpty(mcVer) && mcVer != baseVersionId)
            {
                string fallbackJar = Path.Combine(AppPaths.Versions, mcVer, $"{mcVer}.jar");
                if (File.Exists(fallbackJar))
                {
                    App.WriteAppLog($"[启动] 独立式加载器 JSON,客户端 jar 回退到原版 {mcVer}");
                    baseVersionId = mcVer;
                    clientJar = fallbackJar;
                }
            }
        }
        if (!forceLaunch && !File.Exists(clientJar))
            return new LaunchResult { Success = false, ErrorMessage = $"客户端 jar 缺失:{baseVersionId}.jar" };

        // ---- 4. 解析 version JSON(含继承链)----
        VersionMeta versionMeta;
        try
        {
            versionMeta = LoadVersionMeta(AppPaths.Versions, launchVersionId);
        }
        catch (Exception ex)
        {
            return new LaunchResult { Success = false, ErrorMessage = $"解析版本文件失败:{ex.Message}" };
        }

        // ---- 5. 内存分配(智能模式实时计算;手动模式用设置页保存值;启动时实时复验,严禁超分)----
        int javaMajor = DetectJavaMajorVersion(javaPath, inst.JavaMajorVersion);
        // JVM 参数自动填充必须在此处(实际 Java 版本已确定):按真实版本生成参数,
        // 避免按实例推荐值猜版本填出实际 Java 不认识的参数导致 JVM 拒启且被永久落盘(异常不阻塞启动)
        _autoTune.FillJvmArgsIfNeeded(inst, javaMajor);
        bool java64 = MemoryMonitorService.IsJava64Bit(javaPath);
        int xmx, xms;
        if (_configService.Config.AutoMemoryMode)
        {
            // 2026-09-25 负载感知:启动时按实例真实负载算(模组数量/整合包建议/光影),
            // 与 UI 推荐单一来源,模组多的大实例不再和纯净实例拿一样的内存
            int smartXmx = _memoryMonitor.CalculateSmartXmx(
                CountInstanceMods(inst.Id), inst.RecommendedMemoryMb, HasInstanceShaderPacks(inst.Id));
            if (smartXmx >= 512)
            {
                xmx = smartXmx;
                App.WriteAppLog($"[启动] 智能内存:Xmx={xmx}MB Xms=Xmx(游戏版本原值 Xmx={inst.Xmx}MB,总内存档位表推荐 {_memoryMonitor.RecommendByTotalMb()}MB)");
            }
            else
            {
                // 可用内存紧张到不足 512MB:如实降级到实际可分配上限,绝不回退旧值超分
                xmx = Math.Max(256, smartXmx);
                App.WriteAppLog($"[启动] ⚠ 内存极度紧张:安全分配上限仅 {smartXmx}MB,已降级为 {xmx}MB 启动(低于推荐下限,可能卡顿)");
            }
            xms = xmx; // 固定堆:Xms=Xmx,避免运行时动态扩容的 GC 开销
            if (_memoryMonitor.IsMemoryTight())
                App.WriteAppLog($"[启动] 内存紧张警告:当前可用内存 ≤ {_memoryMonitor.ReserveMb()}MB 预留线,已自动下调游戏内存上限至 {xmx}MB");
        }
        else
        {
            // 手动模式:采用用户在设置页保存的全局 Xms/Xmx
            // (修复:之前用实例默认值 1024/4096,导致设置页保存的内存从不生效)
            xmx = Math.Max(256, _configService.Config.Xmx);
            xms = Math.Max(256, _configService.Config.Xms);
            // 2026-09-25 手动模式总内存硬顶:默认 Xmx=4096 会让 4GB 小内存机器每次启动都弹
            // 「内存风险警告」。手动值尊重用户,但超过本机总内存承载档位时收缩并提示。
            int hardCap = HardCapByTotalMb();
            if (xmx > hardCap)
            {
                App.WriteAppLog($"[启动] 手动内存 Xmx={xmx}MB 超过本机总内存硬顶 {hardCap}MB,已收缩为 {hardCap}MB");
                xmx = hardCap;
                xms = Math.Min(xms, xmx);
            }
            App.WriteAppLog($"[启动] 手动内存:Xms={xms}MB Xmx={xmx}MB(来自设置页)");
        }

        // ---- 4.0 单版本自定义内存(版本设置分区设置):用户明确指定优先于全局智能/手动策略,
        // 后续 32 位限制与启动时实时复验仍作最终兜底,绝不超分 ----
        if (inst.UseCustomMemory && inst.Xmx >= 256)
        {
            xmx = inst.Xmx;
            xms = Math.Min(Math.Max(inst.Xms, 256), xmx);
            // 2026-09-25 同手动模式:自定义值超过总内存承载档位也收缩,小内存机器开开关没改数
            // (默认 4096) 时不再被弹窗轰炸
            int customCap = HardCapByTotalMb();
            if (xmx > customCap)
            {
                App.WriteAppLog($"[启动] 单版本自定义内存 Xmx={xmx}MB 超过本机总内存硬顶 {customCap}MB,已收缩为 {customCap}MB");
                xmx = customCap;
                xms = Math.Min(xms, xmx);
            }
            App.WriteAppLog($"[启动] 单版本自定义内存:{inst.Name} Xmx={xmx}MB Xms={xms}MB(来自版本设置,覆盖全局策略)");
        }

        // ---- 4.1 大型整合包建议内存上调(GTNH 等,导入时写入 RecommendedMemoryMb)----
        // 智能模式算出的值低于整合包建议值时上调至建议值;
        // 后续 32 位限制与启动时实时复验仍作最终兜底,绝不超分(用户已自定义内存时尊重用户选择,不再上调)
        if (!inst.UseCustomMemory && inst.RecommendedMemoryMb > 0 && xmx < inst.RecommendedMemoryMb)
        {
            int raised = Math.Min(inst.RecommendedMemoryMb, _memoryMonitor.GetSafeAllocMb());
            raised = (raised / 256) * 256;
            if (raised > xmx)
            {
                App.WriteAppLog($"[启动] 大型整合包建议内存:Xmx 由 {xmx}MB 上调至 {raised}MB(整合包建议 {inst.RecommendedMemoryMb}MB)");
                xmx = raised;
                xms = xmx; // GTNH 官方要求 Xms=Xmx,避免内存调度增大 CPU 负荷
            }
            else
            {
                // 安全上限够不到建议值:大型整合包低于 4GB 几乎必崩(模组加载即 OOM),
                // 强制地板 4GB 尝试启动,后续 5.2 总预估校验会如实弹窗让用户知情决策,
                // 绝不静默压到几百 MB 必崩启动(日志保留真实可用量便于排查)
                int floor = Math.Min(inst.RecommendedMemoryMb, 4096);
                if (floor > xmx)
                {
                    App.WriteAppLog($"[启动] 大型整合包内存地板:当前可安全分配仅 {raised}MB,低于整合包生存下限,强制按 {floor}MB 启动(建议 {inst.RecommendedMemoryMb}MB,请确保系统已腾出足够内存)");
                    xmx = floor;
                    xms = floor;
                }
                else
                {
                    App.WriteAppLog($"[启动] 大型整合包建议内存 {inst.RecommendedMemoryMb}MB 超出当前可安全分配上限,维持 {xmx}MB");
                }
            }
        }

        // 32 位 Java 限制:32 位 JVM 堆上限约 1.5~2GB,超配直接启动即崩(主流启动器同款防护)
        if (!java64 && xmx > 1024)
        {
            App.WriteAppLog($"[启动] ⚠ 检测到 32 位 Java,内存由 {xmx}MB 强制下调至 1024MB(32 位 JVM 无法寻址更大堆)");
            xmx = 1024;
        }

        // 启动时实时复验:从设置保存到点击启动期间内存状况可能变化,超限自动下调,绝不超分
        int safeMb = _memoryMonitor.GetSafeAllocMb();
        if (safeMb > 0 && xmx > safeMb)
        {
            App.WriteAppLog($"[启动] ⚠ 当前可用内存已变化:Xmx {xmx}MB 超出实时安全上限 {safeMb}MB,自动下调");
            xmx = Math.Max(256, (safeMb / 256) * 256);
        }
        if (xms > xmx) xms = xmx; // Xms 不得大于 Xmx,否则 JVM 直接拒绝启动

        // ---- 5.1 堆外直接内存硬锁(MaxDirectMemorySize)----
        // 背景:Iris/Sodium 等模组的 DirectBuffer/Native 缓冲不受 -Xmx 约束,
        // 曾出现 Xmx=2560MB 而进程实际占用 5.3GB 的失控泄漏。
        // 默认自动取 Xmx 的 0.25 倍(夹在 256~1024):0.75 过于激进,预估总量虚高,
        // 会把本可启动的场景误判为内存不足;真·泄漏由周期 GC + 运行时监控兜底。
        // 2026-09-25 收紧:上限 2048→1024,大 Xmx 不再给 2G 堆外额度(Iris 上传缓冲 1G 极宽裕)。
        // 可在设置页手动覆盖(>0 生效)。
        int cfgDirectMb = _configService.Config.MaxDirectMemoryMb;
        int directMb = cfgDirectMb > 0 ? Math.Max(64, cfgDirectMb)
                                       : Math.Clamp((int)(xmx * 0.25), 256, 1024);
        App.WriteAppLog($"[启动] 堆外锁死:MaxDirectMemorySize={directMb}MB({(cfgDirectMb > 0 ? "手动设置" : "自动 0.25×Xmx,夹在 256~1024")})");

        // ---- 5.2 总预估内存安全校验:Xmx + MaxDirectMemorySize 不得超当前可用物理内存 ----
        long availPhysMb = _memoryMonitor.GetCurrent().AvailableBytes / (1024L * 1024);
        // 先自动收缩堆外锁保 Xmx 本体:堆外只是上限锁,实际用量通常远低于锁值,
        // 宁可缩锁也不打断启动;只有 Xmx 本体都放不下时才弹窗询问。
        if (cfgDirectMb <= 0 && (long)xmx + directMb > availPhysMb)
        {
            int fitDirect = (int)Math.Clamp(availPhysMb - xmx, 128, directMb);
            if (fitDirect < directMb)
            {
                App.WriteAppLog($"[启动] 堆外锁自动收缩:{directMb}MB → {fitDirect}MB(当前可用 {availPhysMb}MB)");
                directMb = fitDirect;
            }
        }
        long estimateMb = (long)xmx + directMb;
        // 2026-09-25 自动降级优先:堆外锁缩完仍放不下时,先自动收缩 Xmx 本体到
        // 「当前可用 − 系统预留」,避免开发者机器(后台多、内存 60% 占用)启动游戏被弹窗打断;
        // 降级后仍放不下(可用内存极低)才弹窗作最后手段。
        if (estimateMb > availPhysMb)
        {
            int autoFit = _memoryMonitor.GetSafeAllocMb();   // 可用 − 预留
            autoFit = Math.Max(256, (autoFit / 256) * 256);
            if (autoFit < xmx)
            {
                App.WriteAppLog($"[启动] ⚠ 内存自动降级:Xmx {xmx}MB → {autoFit}MB" +
                                $"(当前可用 {availPhysMb}MB,已避免弹窗;手动值仍保留在设置里)");
                xmx = autoFit;
                xms = Math.Min(xms, xmx);
                int fitDirect2 = (int)Math.Clamp(availPhysMb - xmx, 128, directMb);
                if (fitDirect2 < directMb)
                {
                    App.WriteAppLog($"[启动] 堆外锁二次收缩:{directMb}MB → {fitDirect2}MB");
                    directMb = fitDirect2;
                }
                estimateMb = (long)xmx + directMb;
                _autoDegraded = true;
            }
        }
        if (estimateMb > availPhysMb)
        {
            App.WriteAppLog($"[启动] ⚠ 内存风险:预估总量 {estimateMb}MB(Xmx {xmx} + 直接内存 {directMb})超过当前可用物理内存 {availPhysMb}MB");
            bool go = DialogKit.Confirm(
                $"游戏预估总内存 = Xmx {xmx}MB + 堆外直接内存 {directMb}MB = {estimateMb}MB,\n" +
                $"已超过当前可用物理内存 {availPhysMb}MB。\n\n" +
                "继续启动可能导致系统内存不足、卡顿甚至崩溃。\n建议:关闭占用内存的程序,或在设置中调低内存后重试。\n\n是否仍要继续启动?",
                "内存风险警告", "继续启动", danger: true,
                owner: System.Windows.Application.Current?.MainWindow);
            if (!go)
                return new LaunchResult { Success = false, ErrorMessage = $"用户取消启动(预估内存 {estimateMb}MB 超出可用物理内存 {availPhysMb}MB)" };
        }

        // ---- 6. 构建 classpath(依赖库在全局共享 libraries 目录)----
        var classpathEntries = BuildClasspathFromJson(AppPaths.Libraries, versionMeta);
        classpathEntries.Add(clientJar); // 客户端 jar 追加到末尾
        string classpath = string.Join(";", classpathEntries);

        // ---- 7. 解压 natives(临时产物落 cache,不污染 versions)----
        string nativesDir = Path.Combine(AppPaths.Cache, "natives", inst.VersionId);
        ExtractNatives(AppPaths.Libraries, versionMeta, nativesDir);

        // ---- 8. 构建启动参数 ----
        var account = _accountService.CurrentAccount;
        string username = account?.Username ?? "Player";
        string uuid = account?.Uuid ?? "";
        string accessToken = account?.AccessToken ?? "";
        string userType = account?.Type == AccountType.Microsoft ? "msa" : "mojang";
        string assetIndexId = versionMeta.AssetIndex?.Id ?? inst.VersionId;
        string assetsDir = AppPaths.Assets;
        string versionType = "FufuLauncher";

        // 占位符字典
        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["${auth_player_name}"] = username,
            ["${auth_uuid}"] = uuid,
            ["${auth_access_token}"] = accessToken,
            ["${auth_session}"] = $"token:{accessToken}:{uuid}",  // 1.7.10 等老版本 LaunchWrapper 期望 token 格式
            ["${user_type}"] = userType,
            ["${version_name}"] = launchVersionId,
            ["${game_directory}"] = mcDir,
            ["${assets_root}"] = assetsDir,
            ["${assets_index_name}"] = assetIndexId,
            ["${game_assets}"] = assetsDir,
            ["${version_type}"] = versionType,
            ["${user_properties}"] = "{}",
            ["${clientid}"] = AuthService.ClientId,
            ["${auth_xuid}"] = "",
            ["${launcher_name}"] = "FufuLauncher",
            ["${launcher_version}"] = "1.9.8.6",
            ["${classpath}"] = classpath,
            ["${natives_directory}"] = nativesDir,
            ["${library_directory}"] = AppPaths.Libraries,
            ["${libraries_directory}"] = AppPaths.Libraries,
            ["${classpath_separator}"] = ";",
            ["${resolution_width}"] = inst.Width.ToString(),
            ["${resolution_height}"] = inst.Height.ToString(),
            ["${primary_jar}"] = clientJar,
        };

        // 构建参数列表
        var allArgs = new List<string>();

        // JVM 参数
        allArgs.Add($"-Xms{xms}m");
        allArgs.Add($"-Xmx{xmx}m");

        // 内存预提交(AlwaysPreTouch):启动时一次性提交全部堆页,避免游戏中途缺页抖动。
        // 两类冲突必须跳过(主流启动器默认根本不开此项):
        // 1. ZGC 模式:ZGC 对堆做多重映射并自行管理提交,叠加 PreTouch 会加倍提交压力,
        //    提交额度(物理内存+页面文件)不足时 JVM 启动即崩——这是之前"内存分配崩溃"的主要根因;
        // 2. 大堆(≥6GB):启动时强提交全部页面易压垮提交额度,改为按需提交更稳。
        // GC 选择器冲突防护:实例额外参数(如 GTNH 专属参数)已显式选定收集器时,
        // 启动器不再叠加自己的 GC 参数,避免 -XX:+UseZGC 与 -XX:+UseG1GC 同现导致 JVM 拒启
        bool userGcOverride = GtnhTuner.ContainsGcSelector(inst.ExtraJvmArgs);
        bool useZgc = !userGcOverride && _configService.Config.MultiCoreGcOptimize && javaMajor >= 17;
        // 命令行里实际生效的 GC:实例额外参数(含自动填充落盘的参数)显式选了 ZGC 时也算,
        // 否则下方 PreTouch 防护会失效——ZGC + AlwaysPreTouch 叠加是内存分配崩溃的主要根因
        bool zgcActive = useZgc || (inst.ExtraJvmArgs?.Contains("-XX:+UseZGC") ?? false);
        if (_configService.Config.MemoryPreCommit)
        {
            if (zgcActive)
                App.WriteAppLog("[启动] ZGC 模式:跳过 AlwaysPreTouch(ZGC 自行管理内存提交,强制预提交易超提交额度导致崩溃)");
            else if (xmx >= 6144)
                App.WriteAppLog($"[启动] 大堆模式(Xmx={xmx}MB ≥ 6GB):跳过 AlwaysPreTouch,降低启动提交压力");
            else
                allArgs.Add("-XX:+AlwaysPreTouch");
        }

        // version JSON 提供的 JVM 参数(含 rules 过滤)
        var jvmArgs = versionMeta.GetJvmArgs();
        // 老格式版本 JSON(1.13 前,含独立式老 Forge)没有 arguments.jvm 节点,
        // JVM 拿不到 classpath 会直接「找不到主类」——按官方启动器行为补固定三件套:
        // natives 目录 + classpath + 工作目录(占位符已提前注册,这里直接替换)
        if (jvmArgs.Count == 0)
        {
            jvmArgs.AddRange(new[]
            {
                $"-Djava.library.path={nativesDir}",
                "-cp", classpath,
                $"-Duser.dir={mcDir}"
            });
            App.WriteAppLog("[启动] 老格式版本 JSON(无 arguments.jvm):已补 -cp classpath 等固定 JVM 参数");
        }
        foreach (var arg in jvmArgs)
        {
            string resolved = ReplacePlaceholders(arg, placeholders);
            allArgs.Add(resolved);
        }

        // 堆外直接内存硬锁:放在版本 JSON 参数之后,确保本启动器的锁死值最终生效
        allArgs.Add($"-XX:MaxDirectMemorySize={directMb}m");

        // GC 多核优化(用户配置):按实际 Java 版本选型(Java17+ ZGC,低版本 G1GC)+ 核心数动态线程
        // 周期 GC:每 10 秒触发一次并发 GC,回收废弃 DirectByteBuffer,缓解堆外只涨不释放(Fabric/Sodium/Iris 环境尤为必要)
        if (userGcOverride)
        {
            App.WriteAppLog("[启动] 实例额外参数已含 GC 收集器选择,跳过启动器自带 GC 优化参数(以实例参数为准)");
        }
        else if (_configService.Config.MultiCoreGcOptimize)
        {
            int cores = MemoryMonitorService.GetPhysicalCoreCount();
            string gcArgs = MemoryMonitorService.BuildMultiCoreGcArgs(cores, javaMajor);
            foreach (var a in gcArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                allArgs.Add(a);
            App.WriteAppLog($"[启动] GC 优化:核心数={cores} Java={javaMajor} 参数={gcArgs}");
        }
        else if (javaMajor >= 12)
        {
            // 未开多核优化时也显式启用周期 GC(Java 8/11 不识别该参数,不加强加以免启动失败)
            allArgs.Add("-XX:+UseG1GC");
            allArgs.Add("-XX:G1PeriodicGCInterval=10000");
            allArgs.Add("-XX:+G1PeriodicGCInvokesConcurrent");
            App.WriteAppLog("[启动] 周期 GC 已启用:G1 每 10 秒触发一次并发 GC,回收堆外直接内存");
        }
        // ZGC 的周期 GC 等价参数(ZCollectionInterval 单位秒):定期触发回收,避免堆外资源长期驻留
        if (useZgc)
            allArgs.Add("-XX:ZCollectionInterval=10");

        // 2026-09-25 堆外收紧:Metaspace 设 1GB 上限——模组类加载器泄漏时元空间默认无限、
        // 只涨不降,是「堆外越来越大」的主因之一;1GB 覆盖绝大多数整合包(GTNH 级 ~500MB 足够),
        // 超限抛 OOM 由游戏侧处理,不会无限占下去
        allArgs.Add("-XX:MaxMetaspaceSize=1g");
        // JIT 代码缓存上限 192MB:防编译缓存涨满默认 240MB;MC+常见模组用量 <160MB,无感
        allArgs.Add("-XX:ReservedCodeCacheSize=192m");

        // 实例额外 JVM 参数
        if (!string.IsNullOrEmpty(inst.ExtraJvmArgs))
        {
            foreach (var a in inst.ExtraJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                allArgs.Add(a);
        }

        // 主类
        allArgs.Add(versionMeta.MainClass);

        // 游戏参数(从 version JSON,含 rules 过滤)
        var gameArgs = versionMeta.GetGameArgs();
        foreach (var arg in gameArgs)
        {
            string resolved = ReplacePlaceholders(arg, placeholders);
            allArgs.Add(resolved);
        }

        // 分辨率(如果 version JSON 的 game args 里没有 --width/--height/--fullscreen,则追加)
        if (!gameArgs.Any(a => a.Contains("fullscreen")) && !gameArgs.Any(a => a.Contains("width")))
        {
            if (inst.Fullscreen)
            {
                allArgs.Add("--fullscreen");
            }
            else
            {
                allArgs.Add("--width");
                allArgs.Add(inst.Width.ToString());
                allArgs.Add("--height");
                allArgs.Add(inst.Height.ToString());
            }
        }

        // ---- 8.5 启动前置:环境完整性校验(BlockHelm-3) + 前置脚本(Celestial-7) ----
        if (!forceLaunch && _configService.Config.VerifyBeforeLaunch)
        {
            try
            {
                var mode = _configService.Config.DeepVerifyBeforeLaunch
                    ? IntegrityCheckMode.Deep : IntegrityCheckMode.Quick;
                var report = await _integrity.CheckAndRepairAsync(instanceId, mode,
                    _configService.Config.AutoRepairBeforeLaunch);
                if (report != null)
                {
                    session.RepairedFiles = report.Repaired.Count;
                    // 2026-09-26:只有「整个版本没装」才硬拦(启动必崩,无可挽回);
                    // 普通文件修复失败不再硬拦——弹窗让用户选「仍然启动 / 取消」,由用户决定。
                    if (report.NeedFullInstall)
                    {
                        session.Reason = TruncReason(report.Headline);
                        return new LaunchResult { Success = false, ErrorMessage = report.Detail };
                    }
                    if (report.Failed.Count > 0)
                    {
                        bool cont = DialogKit.Confirm(
                            $"启动前自动补全有 {report.Failed.Count} 个文件仍未修复(网络等原因)," +
                            $"可能影响游戏正常运行。\n\n{report.Detail}\n\n是否仍然尝试启动?",
                            "仍有文件未修复", System.Windows.Application.Current?.MainWindow);
                        if (!cont)
                        {
                            session.Reason = TruncReason(report.Headline);
                            return new LaunchResult { Success = false, ErrorMessage = report.Detail };
                        }
                    }
                    if (report.Repaired.Count > 0)
                    {
                        // 弹窗告知哪些项目被修复了(BlockHelm-3 硬要求)
                        DialogKit.Info($"启动前校验发现 {report.Problems.Count} 个问题,已自动修复 {report.Repaired.Count} 个文件:\n\n{report.Detail}",
                                       "环境校验已自动修复",
                                       System.Windows.Application.Current?.MainWindow);
                    }
                }
            }
            catch (Exception ex)
            {
                // 校验本身出错不能把游戏拦在门外,只记日志
                App.WriteAppLog($"[启动] 前置完整性校验异常(不阻塞启动):{ex.Message}");
            }
        }

        // 前置脚本(总开关关掉时服务内部直接跳过,不报错)
        try
        {
            var pre = await _scripts.RunPreLaunchAsync(instanceId);
            if (pre.Executed)
            {
                _gameLog.AppendLine($"[FufuLauncher] 前置脚本:{pre.Display} | {pre.Message}");
                if (!pre.Success)
                    App.WriteAppLog($"[启动] 前置脚本执行失败(继续启动):{pre.Message}");
            }
            else if (!string.IsNullOrEmpty(pre.SkippedReason))
            {
                App.WriteAppLog($"[启动] 前置脚本已跳过:{pre.SkippedReason}");
            }
        }
        catch (Exception ex) { App.WriteAppLog($"[启动] 前置脚本异常(不阻塞启动):{ex.Message}"); }

        // ---- 9. 启动进程 ----
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = mcDir
            };

            // 设置环境变量
            try
            {
                string pathEnv = psi.EnvironmentVariables["Path"] ?? "";
                var paths = new List<string>(pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries));
                string javaBinDir = Path.GetDirectoryName(javaPath) ?? "";
                if (!string.IsNullOrEmpty(javaBinDir) && !paths.Contains(javaBinDir))
                    paths.Add(javaBinDir);
                psi.EnvironmentVariables["Path"] = string.Join(";", paths.Distinct());
                psi.EnvironmentVariables["appdata"] = mcDir;
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[启动] 设置环境变量失败:{ex.Message}");
            }

            foreach (var arg in allArgs)
                psi.ArgumentList.Add(arg);

            App.WriteAppLog($"[启动] Java={javaPath}");
            App.WriteAppLog($"[启动] MainClass={versionMeta.MainClass}");
            App.WriteAppLog($"[启动] ClasspathEntries={classpathEntries.Count}");
            App.WriteAppLog($"[启动] NativesDir={nativesDir}");
            App.WriteAppLog($"[启动] Xms={xms}MB Xmx={xmx}MB");
            App.WriteAppLog($"[启动] TotalArgs={allArgs.Count}");

            // 输出完整参数列表(调试用;令牌等凭据脱敏,防止敏感信息落盘日志)
            App.WriteAppLog($"[启动] ===== 启动参数开始 =====");
            foreach (var arg in allArgs)
                App.WriteAppLog($"[启动]   {MaskSecret(arg, accessToken)}");
            App.WriteAppLog($"[启动] ===== 启动参数结束 =====");

            _currentProcess = Process.Start(psi);
            if (_currentProcess == null)
                return new LaunchResult { Success = false, ErrorMessage = "无法启动 Java 进程" };

            // 记录运行中实例 Id(供卸载拦截:运行中的版本禁止卸载)
            RunningInstanceId = inst.Id;

            // 启动会话记录(BlockHelm-6):进程拉起成功先落一条,退出码与崩溃原因由下方监控线程回填
            session.Success = true;
            SafeAddLaunchRecord(instanceId, session);

            // 进程托管登记:关闭启动器时强制回收游戏进程树,杜绝残留占用文件锁
            _processGuard.Register(_currentProcess, $"游戏进程 [{inst.Name}]");

            // 进程优化
            ApplyProcessOptimizations(_currentProcess);

            // 捕获输出到日志
            _gameLog.AttachToProcess(_currentProcess);

            // 日志自愈会话注入:本次启动的日志行按实例上下文做 OOM 自动修正等处置
            _logFix.BeginSession(inst.Id);

            // 启动堆+堆外内存监控(每 2s 采样,持续暴涨预警)
            _memoryWatch.Start(_currentProcess, xmx);
            // 2026-09-25 预警接到 UI:堆外持续暴涨时状态栏可见(原来只写日志,用户看不到)
            _memoryWatch.OffHeapGrowthWarning += OnGameMemoryWarning;
            _memoryWatch.Updated += OnGameMemoryUpdated;

            // AI 静默退场:游戏已启动,卸载模型释放显存与内存,之后不再占用显卡性能与内存;
            // 重开泡芙助理页时按需自动重载,全程无感(火忘式,不阻塞启动返回)。
            _ = _aiEngine.UnloadAsync($"游戏「{inst.Name}」已启动,泡芙助理进入静默");

            // 更新游玩时间
            inst.LastPlayedAt = DateTime.Now;
            _instanceService.SaveInstance(inst);

            var proc = _currentProcess;
            var launchedAt = inst.LastPlayedAt;
            _ = Task.Run(() =>
            {
                try
                {
                    // 带超时轮询等待:即使标准输出管道被占用导致无参 WaitForExit 阻塞,
                    // 也能在游戏进程退出(含用户手动关闭)后可靠返回,保证状态同步与计时落盘
                    while (!proc.WaitForExit(1000)) { }
                    _gameLog.AppendLine($"[FufuLauncher] 游戏进程已退出(代码 {proc.ExitCode})");
                    long secs = (long)Math.Max(0, (DateTime.Now - launchedAt).TotalSeconds);
                    inst.TotalPlayTimeSeconds += secs;
                    _instanceService.SaveInstance(inst);
                    App.WriteAppLog($"[启动] 游戏退出:本次游玩 {secs} 秒,累计 {inst.TotalPlayTimeSeconds} 秒");

                    // 回填启动会话结果(BlockHelm-6):退出码非 0 时去崩溃报告/日志里抠一句话原因
                    int exitCode = SafeExitCode(proc);
                    string reason = exitCode == 0 ? "" : SummarizeExitReason(inst.Id, exitCode);
                    SafeFinishLaunch(inst.Id, exitCode == 0, exitCode, reason);

                    // 后置脚本(Celestial-7):游戏退出后运行,失败只记日志不影响收尾
                    try
                    {
                        var post = _scripts.RunPostExitAsync(inst.Id, exitCode, exitCode == 0).GetAwaiter().GetResult();
                        if (post.Executed)
                            App.WriteAppLog($"[启动] 后置脚本:{post.Display} | {post.Message}");
                    }
                    catch (Exception ex) { App.WriteAppLog($"[启动] 后置脚本异常:{ex.Message}"); }
                }
                catch (Exception ex)
                {
                    _gameLog.AppendLine($"[FufuLauncher] 监控进程异常:{ex.Message}");
                }
                finally
                {
                    _memoryWatch.OffHeapGrowthWarning -= OnGameMemoryWarning;
                    _memoryWatch.Updated -= OnGameMemoryUpdated;
                    _memoryWatch.Stop();
                    _logFix.EndSession();
                    proc.Dispose();
                    if (ReferenceEquals(_currentProcess, proc)) _currentProcess = null;
                    RunningInstanceId = null;
                    try { GameExited?.Invoke(); } catch { /* 订阅者异常不影响清理 */ }
                }
            });

            if (_autoDegraded)
                App.WriteAppLog($"[启动] ✓ {inst.Name} 已启动(本次内存自动降级至 Xmx={xmx}MB,手动配置值未改动)");
            return new LaunchResult { Success = true, Process = _currentProcess };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    // ==================== 启动会话记录(BlockHelm-6) ====================

    /// <summary>开一条启动会话:先把起始时间与版本信息填好,结果稍后回填</summary>
    private LaunchSessionRecord BeginLaunchSession(string instanceId)
    {
        var inst = _instanceService.Instances.FirstOrDefault(i => i.Id == instanceId);
        return new LaunchSessionRecord
        {
            StartTime = DateTime.Now,
            VersionId = inst?.VersionId ?? "",
            Loader = string.IsNullOrEmpty(inst?.ModLoader)
                ? "原版"
                : $"{inst!.ModLoader}{(string.IsNullOrEmpty(inst.ModLoaderVersion) ? "" : " " + inst.ModLoaderVersion)}"
        };
    }

    private void SafeAddLaunchRecord(string instanceId, LaunchSessionRecord session)
    {
        try { _extras.AddLaunchRecord(instanceId, session); }
        catch (Exception ex) { App.WriteAppLog($"[启动记录] 写入异常:{ex.Message}"); }
    }

    private void SafeFinishLaunch(string instanceId, bool success, int exitCode, string reason)
    {
        try { _extras.FinishLastLaunch(instanceId, success, exitCode, reason); }
        catch (Exception ex) { App.WriteAppLog($"[启动记录] 回填异常:{ex.Message}"); }
    }

    private void OnGameMemoryUpdated(GameMemorySnapshot snap)
    {
        try { GameMemoryUpdated?.Invoke(snap); } catch { /* 订阅者异常不影响监控 */ }
    }

    private void OnGameMemoryWarning(long offHeapMb)
    {
        try { GameMemoryWarning?.Invoke($"游戏堆外内存已达 {offHeapMb}MB 且持续上涨,长时间游玩可能崩溃,建议重启游戏或减少光影/模组"); }
        catch { /* 订阅者异常不影响监控 */ }
    }

    /// <summary>崩溃原因只存一句话,不把整份堆栈塞进配置文件</summary>
    private static string TruncReason(string reason)
    {
        string r = (reason ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return r.Length <= 200 ? r : r[..200] + "…";
    }

    /// <summary>实例 mods 目录已装的 .jar 模组数量(毫秒级目录扫描,供负载感知)</summary>
    private int CountInstanceMods(string instanceId)
    {
        try
        {
            string dir = _instanceService.GetModsDir(instanceId);
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "*.jar", SearchOption.TopDirectoryOnly).Count();
        }
        catch { return 0; }
    }

    /// <summary>实例 shaderpacks 目录是否装有光影(非空即视为重负载)</summary>
    private bool HasInstanceShaderPacks(string instanceId)
    {
        try
        {
            string dir = _instanceService.GetShaderPacksDir(instanceId);
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch { return false; }
    }

    /// <summary>总内存档位硬顶(MB):手动/单版本自定义内存也不得超过整机承载档位,
    /// 防止小内存机器被默认 4096 坑(4GB 机器 4096 无论怎么调都超可用内存)</summary>
    private int HardCapByTotalMb()
    {
        long totalMb = _memoryMonitor.GetCurrent().TotalBytes / (1024L * 1024);
        // 4GB 档 1280:4GB 机器可用内存通常 1.5~2GB,2048+堆外锁必超可用仍会误弹
        return totalMb <= 4096 ? 1280
             : totalMb < 8192 ? 4096
             : totalMb < 16384 ? 8192
             : 16384;
    }

    private static int SafeExitCode(Process proc)
    {
        try { return proc.ExitCode; }
        catch { return -1; }
    }

    /// <summary>
    /// 根据退出码与崩溃报告拼一句话原因。
    /// 优先拿 crash-reports 里最新一份的结论,拿不到再退回 latest.log 关键片段,都没有就只给退出码。
    /// </summary>
    private string SummarizeExitReason(string instanceId, int exitCode)
    {
        string head = exitCode switch
        {
            1 => "游戏异常退出(退出码 1)",
            -1073741819 => "JVM 崩溃(内存访问异常,0xC0000005)",
            -1073740791 => "JVM 崩溃(栈溢出,0xC0000409)",
            _ => $"游戏非正常退出(退出码 {exitCode})"
        };
        try
        {
            string detail = _crash.SummarizeReason(instanceId);
            return string.IsNullOrEmpty(detail) ? head : TruncReason($"{head}:{detail}");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[启动记录] 崩溃原因提取失败:{ex.Message}");
            return head;
        }
    }

    /// <summary>启动参数日志脱敏:access token 等凭据不以明文落盘</summary>
    private static string MaskSecret(string arg, string accessToken)
    {
        if (!string.IsNullOrEmpty(accessToken) && arg.Contains(accessToken, StringComparison.Ordinal))
            return arg.Replace(accessToken, "******");
        return arg;
    }

    // ==================== Version JSON 解析 ====================

    /// <summary>沿 inheritsFrom 链回溯到最底层原版版本 id(客户端 jar 所在层)</summary>
    private static string ResolveBaseVersionId(string versionId)
    {
        string cur = versionId;
        for (int hop = 0; hop < 4; hop++)
        {
            string p = Path.Combine(AppPaths.Versions, cur, $"{cur}.json");
            if (!File.Exists(p)) break;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
                string? parent = (string?)node["inheritsFrom"];
                if (string.IsNullOrEmpty(parent)) break;
                cur = parent;
            }
            catch { break; }
        }
        return cur;
    }

    /// <summary>读取版本 JSON 的 minecraftVersion 字段(独立式老 Forge JSON 用它指向原版基座,无则返回空)</summary>
    private static string ResolveMinecraftVersion(string versionId)
    {
        try
        {
            string p = Path.Combine(AppPaths.Versions, versionId, $"{versionId}.json");
            if (!File.Exists(p)) return "";
            var node = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
            return (string?)node["minecraftVersion"] ?? "";
        }
        catch { return ""; }
    }

    /// <summary>加载 version JSON,处理 inheritsFrom 继承链(Forge/Fabric)。versionsDir=全局 versions 目录</summary>
    private VersionMeta LoadVersionMeta(string versionsDir, string versionId)
    {
        string jsonPath = Path.Combine(versionsDir, versionId, $"{versionId}.json");
        var json = File.ReadAllText(jsonPath);
        var root = JsonNode.Parse(json)!.AsObject();

        var meta = ParseVersionNode(root);

        // 处理继承链(Forge/Fabric 通过 inheritsFrom 引用原版)
        if (!string.IsNullOrEmpty(meta.InheritsFrom))
        {
            string parentPath = Path.Combine(versionsDir, meta.InheritsFrom, $"{meta.InheritsFrom}.json");
            if (File.Exists(parentPath))
            {
                var parentJson = File.ReadAllText(parentPath);
                var parentRoot = JsonNode.Parse(parentJson)!.AsObject();
                var parent = ParseVersionNode(parentRoot);
                meta.MergeParent(parent);
            }
        }

        return meta;
    }

    private static VersionMeta ParseVersionNode(JsonObject node)
    {
        var meta = new VersionMeta();
        meta.Id = (string?)node["id"] ?? "";
        meta.MainClass = (string?)node["mainClass"] ?? "";
        meta.InheritsFrom = (string?)node["inheritsFrom"];
        meta.AssetType = (string?)node["assetIndex"]?["id"];

        // assetIndex
        if (node["assetIndex"] is JsonObject ai)
            meta.AssetIndex = new AssetIndexInfo { Id = (string?)ai["id"] ?? "", Url = (string?)ai["url"] };

        // libraries
        if (node["libraries"] is JsonArray libs)
        {
            foreach (var lib in libs)
            {
                if (lib is not JsonObject libObj) continue;
                var entry = ParseLibrary(libObj);
                if (entry != null) meta.Libraries.Add(entry);
            }
        }

        // arguments (新格式 1.13+)
        if (node["arguments"] is JsonObject argsNode)
        {
            meta.GameArguments = ParseArgArray(argsNode["game"] as JsonArray);
            meta.JvmArguments = ParseArgArray(argsNode["jvm"] as JsonArray);
        }

        // minecraftArguments (旧格式 <1.13)
        if (meta.GameArguments.Count == 0 && node["minecraftArguments"] is JsonNode mcArgs)
        {
            string argsStr = mcArgs.ToString();
            meta.GameArguments = argsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => new ArgEntry { Value = s }).ToList();
            meta.LegacyGameArguments = true; // 旧格式参数自包含,合并时不得再追加父版本旧参数(会重复)
        }

        return meta;
    }

    /// <summary>解析单个 library 条目</summary>
    private static LibraryEntry? ParseLibrary(JsonObject obj)
    {
        string? name = (string?)obj["name"];
        if (string.IsNullOrEmpty(name)) return null;

        var entry = new LibraryEntry { Name = name };

        // downloads.artifact.path
        if (obj["downloads"] is JsonObject dl)
        {
            if (dl["artifact"] is JsonObject art)
                entry.Path = (string?)art["path"];

            // downloads.classifiers (natives)
            if (dl["classifiers"] is JsonObject classifiers)
            {
                foreach (var (key, val) in classifiers)
                {
                    if (val is JsonObject cls && key != null)
                    {
                        entry.ClassifierPaths[key] = (string?)cls["path"] ?? "";
                    }
                }
            }
        }

        // rules
        if (obj["rules"] is JsonArray rules)
            entry.Rules = ParseRules(rules);

        // natives map
        if (obj["natives"] is JsonObject natives)
        {
            foreach (var (os, classifier) in natives)
            {
                if (os != null && classifier != null)
                    entry.NativesMap[os] = classifier.ToString();
            }
        }

        // extract
        if (obj["extract"] is JsonObject extract)
        {
            if (extract["exclude"] is JsonArray exclude)
            {
                foreach (var ex in exclude)
                    if (ex != null) entry.ExtractExclude.Add(ex.ToString());
            }
        }

        return entry;
    }

    /// <summary>解析 rules 数组</summary>
    private static List<RuleInfo> ParseRules(JsonArray rulesArr)
    {
        var rules = new List<RuleInfo>();
        foreach (var ruleNode in rulesArr)
        {
            if (ruleNode is not JsonObject ruleObj) continue;
            var rule = new RuleInfo();
            rule.Action = (string?)ruleObj["action"] ?? "allow";
            if (ruleObj["os"] is JsonObject osObj)
            {
                rule.OsName = (string?)osObj["name"];
                rule.OsVersion = (string?)osObj["version"];
                rule.OsArch = (string?)osObj["arch"];
            }
            // 标记 features 条件(is_demo_user / has_custom_resolution 等)
            if (ruleObj.ContainsKey("features"))
                rule.HasFeatures = true;
            rules.Add(rule);
        }
        return rules;
    }

    /// <summary>解析参数数组(支持字符串和带 rules 的对象两种格式)</summary>
    private static List<ArgEntry> ParseArgArray(JsonArray? arr)
    {
        var result = new List<ArgEntry>();
        if (arr == null) return result;
        foreach (var item in arr)
        {
            if (item == null) continue;
            if (item is JsonValue jv)
            {
                // 简单字符串参数
                result.Add(new ArgEntry { Value = jv.ToString() });
            }
            else if (item is JsonObject obj)
            {
                // 带 rules 的复杂参数
                var entry = new ArgEntry();
                if (obj["rules"] is JsonArray rules)
                    entry.Rules = ParseRules(rules);

                if (obj["value"] is JsonValue vv)
                {
                    entry.Value = vv.ToString();
                }
                else if (obj["value"] is JsonArray valArr)
                {
                    // value 可以是数组(一个 rule 对应多个参数)
                    foreach (var v in valArr)
                        if (v != null) entry.MultiValues.Add(v.ToString());
                }
                result.Add(entry);
            }
        }
        return result;
    }

    // ==================== Classpath 构建 ====================

    /// <summary>从 version JSON 的 libraries 列表构建 classpath(精确解析,非暴力遍历)。libsDir=全局 libraries 目录</summary>
    private List<string> BuildClasspathFromJson(string libsDir, VersionMeta meta)
    {
        var parts = new List<string>();

        foreach (var lib in meta.Libraries)
        {
            // 跳过 natives-only 库(它们不进 classpath)
            if (lib.NativesMap.Count > 0 && string.IsNullOrEmpty(lib.Path))
                continue;

            // rules 过滤
            if (!CheckRules(lib.Rules))
                continue;

            string? libPath = lib.Path;

            // 如果没有 downloads.artifact.path,从 Maven name 推导
            if (string.IsNullOrEmpty(libPath))
                libPath = NameToMavenPath(lib.Name);

            if (string.IsNullOrEmpty(libPath))
                continue;

            string fullPath = Path.Combine(libsDir, libPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath) && !parts.Contains(fullPath))
                parts.Add(fullPath);
        }

        return parts;
    }

    /// <summary>将 Maven 坐标转换为相对路径(com.google.guava:guava:31.1 → com/google/guava/guava/31.1/guava-31.1.jar)</summary>
    private static string NameToMavenPath(string name)
    {
        // 格式: group:artifact:version[:classifier][@extension]
        string ext = "jar";
        string classifier = "";
        int atIdx = name.IndexOf('@');
        if (atIdx >= 0) { ext = name[(atIdx + 1)..]; name = name[..atIdx]; }

        string[] parts = name.Split(':');
        if (parts.Length < 3) return "";

        string group = parts[0].Replace('.', '/');
        string artifact = parts[1];
        string version = parts[2];
        if (parts.Length > 3) classifier = parts[3];

        string fileName = string.IsNullOrEmpty(classifier)
            ? $"{artifact}-{version}.{ext}"
            : $"{artifact}-{version}-{classifier}.{ext}";

        return $"{group}/{artifact}/{version}/{fileName}";
    }

    // ==================== Natives 解压 ====================

    /// <summary>从 native 库 jar 中提取 DLL 到 natives 目录。libsDir=全局 libraries 目录</summary>
    private void ExtractNatives(string libsDir, VersionMeta meta, string nativesDir)
    {
        // 2026-09-25:解压前清空旧 natives——版本更新后残留旧 DLL 与新版混合会启动崩溃;
        // 顺带治理 cache/natives 逐版本累积的缓存膨胀(失败不阻断,解压会覆盖同名文件)
        try { if (Directory.Exists(nativesDir)) Directory.Delete(nativesDir, true); } catch { }
        Directory.CreateDirectory(nativesDir);

        foreach (var lib in meta.Libraries)
        {
            if (!CheckRules(lib.Rules)) continue;
            if (lib.NativesMap.Count == 0) continue;

            // 获取当前 OS 的 classifier
            string osKey = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
                         : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "osx";
            if (!lib.NativesMap.TryGetValue(osKey, out string? classifier))
                continue;

            // 解析 native jar 路径
            string? nativePath = null;
            if (lib.ClassifierPaths.TryGetValue(classifier, out string? cp))
                nativePath = cp;
            else if (!string.IsNullOrEmpty(lib.Path))
            {
                // 从 artifact path 推导 classifier path
                string basePath = Path.ChangeExtension(lib.Path, null);
                nativePath = $"{basePath}-{classifier}.jar";
            }
            else
            {
                // 从 Maven name 推导
                string baseName = lib.Name;
                string mavenPath = NameToMavenPath(baseName);
                if (!string.IsNullOrEmpty(mavenPath))
                {
                    string dir = Path.GetDirectoryName(mavenPath) ?? "";
                    string file = Path.GetFileNameWithoutExtension(mavenPath);
                    nativePath = $"{dir}/{file}-{classifier}.jar";
                }
            }

            if (string.IsNullOrEmpty(nativePath)) continue;
            string fullJar = Path.Combine(libsDir, nativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullJar)) continue;

            // 解压 jar 中的 native 文件
            try
            {
                using var archive = ZipFile.OpenRead(fullJar);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    // 排除规则
                    bool excluded = false;
                    foreach (var ex in lib.ExtractExclude)
                    {
                        if (entry.FullName.StartsWith(ex, StringComparison.OrdinalIgnoreCase))
                        { excluded = true; break; }
                    }
                    if (excluded) continue;

                    string destPath = Path.Combine(nativesDir, entry.Name);
                    using var src = entry.Open();
                    using var dst = File.Create(destPath);
                    src.CopyTo(dst);
                }
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[启动] 解压 natives 失败 {fullJar}:{ex.Message}");
            }
        }
    }

    // ==================== Rules 评估 ====================

    /// <summary>检查 rules 列表,判断当前 OS 是否匹配(空 rules = 全部允许)</summary>
    private static bool CheckRules(List<RuleInfo>? rules)
    {
        if (rules == null || rules.Count == 0) return true;

        bool allowed = false;
        foreach (var rule in rules)
        {
            // features 条件:启动器暂不支持,跳过
            if (rule.HasFeatures) continue;

            // OS 名称匹配
            if (!string.IsNullOrEmpty(rule.OsName))
            {
                bool osMatch = rule.OsName switch
                {
                    "windows" => RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                    "linux" => RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
                    "osx" => RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
                    _ => false
                };
                if (!osMatch) continue;
            }

            // OS 架构匹配
            if (!string.IsNullOrEmpty(rule.OsArch))
            {
                string actualArch = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";
                if (rule.OsArch != actualArch) continue;
            }

            allowed = rule.Action == "allow";
        }
        return allowed;
    }

    // ==================== 占位符替换 ====================

    private static string ReplacePlaceholders(string input, Dictionary<string, string> map)
    {
        foreach (var (key, value) in map)
            input = input.Replace(key, value, StringComparison.OrdinalIgnoreCase);
        return input;
    }

    // ==================== Java 路径解析 ====================

    /// <summary>探测实际使用的 Java 主版本:runtimes 目录名约定 jdk-{major}-{arch} 优先,其次实例推荐值</summary>
    private int DetectJavaMajorVersion(string javaPath, int fallback)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(javaPath) ?? "");
            for (int hop = 0; hop < 4 && dir != null; hop++, dir = dir.Parent)
            {
                if (dir.Name.StartsWith("jdk-", StringComparison.OrdinalIgnoreCase))
                {
                    var seg = dir.Name[4..].Split('-')[0];
                    if (int.TryParse(seg, out int v) && v >= 6 && v <= 99) return v;
                }
            }
        }
        catch { /* 忽略解析异常,回退实例推荐值 */ }
        return fallback > 0 ? fallback : 17;
    }

    /// <summary>启动前自动补装加载器本体(旧版导入只写了 ModLoader 标记、未装本体);
    /// 顺带修复老版本 Java 推荐错配与 lwjgl3ify 兼容问题;失败返回中文错误(阻断启动),成功返回 null</summary>
    // ==================== 空壳版本:按名字推断加载器(暗病修复) ====================

    /// <summary>
    /// 从版本名推断加载器类型与版本号(覆盖加载器安装失败残留的空壳实例,
    /// 如名字 "1.20.1-Fabric 0.19.5" 但元数据未写入任何加载器信息的暗病)。
    /// 匹配顺序:NeoForge 必须先于 Forge(NeoForge 名字含 Forge 子串)。
    /// 版本号只取加载器关键字之后的第一个版本段(如 "Fabric 0.19.5" / "Forge10.13.4.1614")。
    /// </summary>
    private static bool TryParseLoaderFromName(string name, out string? loader, out string? version)
    {
        loader = null;
        version = null;
        if (string.IsNullOrWhiteSpace(name)) return false;

        string low = name.ToLowerInvariant();
        string? kind = null;
        if (low.Contains("neoforge")) kind = "neoforge";
        else if (low.Contains("fabric")) kind = "fabric";
        else if (low.Contains("quilt")) kind = "quilt";
        else if (low.Contains("forge")) kind = "forge";
        else if (low.Contains("optifine")) kind = "optifine";
        if (kind == null) return false;

        var m = Regex.Match(name, @"(?:neoforge|fabric|quilt|forge|optifine)\s*[-_ ]?\s*(\d+(?:\.\d+)*[\w.\-+]*)",
            RegexOptions.IgnoreCase);
        loader = kind;
        if (m.Success) version = m.Groups[1].Value;
        return true;
    }

    /// <summary>懒人包「加载器已合并进版本本体」识别:返回可直接当作加载器本体的版本 ID(无则 null)。
    /// 场景:第三方启动器导出的整合包自带完整版本目录(如 "26.1.2-NeoForge_26.1.2.78"),
    /// 加载器与游戏本体已合并在同一份版本 JSON 里,而实例的 LoaderVersionId 是空的。
    /// 不识别就会把已装好的加载器误判成空壳版本,启动前反复下载安装器。</summary>
    private static string? DetectBakedLoaderVersionId(GameInstance inst, out string? keyword)
    {
        // 加载器关键字来源:优先实例元数据;元数据全空时退回实例名(案例 C:
        // 名字带加载器关键字、ModLoader 却是空——此时同样可能只是没登记,本体其实已落盘)
        string? key = LoaderKeyword(inst.ModLoader);
        if (key == null && !string.IsNullOrEmpty(inst.Name)) key = LoaderKeyword(inst.Name);
        keyword = key;
        if (key == null) return null;

        foreach (var vid in new[] { inst.VersionId, inst.LoaderVersionId })
        {
            if (!string.IsNullOrEmpty(vid) && MatchesBakedLoader(vid!, key)) return vid;
        }
        // 兜底:versions 下扫同名加载器版本。必须与实例的主版本段一致(如都是 1.20.1),
        // 否则「名字里恰好带了 forge 字样」的实例会被挂到别的 MC 版本的加载器上。
        try
        {
            if (!Directory.Exists(AppPaths.Versions)) return null;
            string? baseVer = string.IsNullOrEmpty(inst.VersionId)
                ? null : BaseVersionOf(inst.VersionId);
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.Versions))
            {
                string name = Path.GetFileName(dir);
                if (!MatchesBakedLoader(name, key)) continue;
                if (baseVer != null && !string.IsNullOrEmpty(BaseVersionOf(name))
                    && !string.Equals(BaseVersionOf(name), baseVer, StringComparison.OrdinalIgnoreCase))
                    continue;
                return name;
            }
        }
        catch (Exception ex) { App.WriteAppLog($"[启动] 加载器本体版本探测失败(按需补装):{ex.Message}"); }
        return null;
    }

    /// <summary>版本目录名的主版本段(1.20.1-forge-47.2.0 → 1.20.1;首段非数字则空串)</summary>
    private static string BaseVersionOf(string versionId)
    {
        int dash = versionId.IndexOf('-');
        string head = dash > 0 ? versionId[..dash] : versionId;
        return head.Length > 0 && char.IsDigit(head[0]) ? head : "";
    }

    /// <summary>ModLoader 值 → 版本目录名中的加载器关键字(NeoForge 必须先于 Forge 判断)</summary>
    private static string? LoaderKeyword(string? modLoader)
    {
        string low = (modLoader ?? "").ToLowerInvariant();
        if (low.Contains("neoforge")) return "neoforge";
        if (low.Contains("fabric")) return "fabric";
        if (low.Contains("quilt")) return "quilt";
        if (low.Contains("forge")) return "forge";
        if (low.Contains("optifine")) return "optifine";
        return null;
    }

    /// <summary>加载器关键字 → 展示名(认领本体后回填实例 ModLoader,UI 徽章/诊断口径一致)</summary>
    private static string LoaderLabel(string keyword) => keyword switch
    {
        "neoforge" => "NeoForge",
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "forge" => "Forge",
        "optifine" => "OptiFine",
        _ => keyword
    };

    /// <summary>版本目录名既含该加载器关键字、又有落盘的版本 JSON 才算本体(排除纯原版;NeoForge 不兼作 Forge)</summary>
    private static bool MatchesBakedLoader(string versionId, string keyword)
    {
        string low = versionId.ToLowerInvariant();
        if (!low.Contains(keyword)) return false;
        if (keyword == "forge" && low.Contains("neoforge")) return false;
        return File.Exists(Path.Combine(AppPaths.Versions, versionId, versionId + ".json"));
    }

    /// <summary>按名字推断的加载器补装(仅用于元数据全空的空壳版本;返回 null=成功,否则错误文案)</summary>
    private async Task<string?> RepairLoaderByNameAsync(GameInstance inst)
    {
        if (!TryParseLoaderFromName(inst.Name, out var loader, out var version))
            return null; // 推断不出就不阻断(弹窗已给用户决策)
        App.WriteAppLog($"[启动] 空壳版本 [{inst.Name}] 按名字补装 {loader} {version ?? "(默认最新)"}…");
        try
        {
            var res = await _loaderInstall.InstallLoaderAsync(inst.Id, inst.VersionId, loader!, version);
            if (!res.Success)
            {
                App.WriteAppLog($"[启动] 按名字补装失败:{res.ErrorMessage}");
                return $"{loader} 补装失败({res.ErrorMessage}),可在「新装版本」向导手动安装该加载器";
            }
            _instanceService.SaveInstance(inst);
            App.WriteAppLog($"[启动] ✓ 按名字补装 {loader} 完成,继续启动流程");
            return null;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[启动] 按名字补装异常:{ex.Message}");
            return $"{loader} 补装异常:{ex.Message}";
        }
    }

    private async Task<string?> RepairLoaderAsync(GameInstance inst)
    {
        App.WriteAppLog($"[启动] 版本 [{inst.Name}] 缺少 {inst.ModLoader} {inst.ModLoaderVersion} 本体,启动前自动补装…");
        try
        {
            var res = inst.ModLoader switch
            {
                // 统一入口:ModLoaderVersion 为空时自动拉取该 MC 版本最新适配版本(不再卡死)
                "Forge" => await _loaderInstall.InstallLoaderAsync(inst.Id, inst.VersionId, "forge", inst.ModLoaderVersion),
                "Fabric" => await _loaderInstall.InstallLoaderAsync(inst.Id, inst.VersionId, "fabric", inst.ModLoaderVersion),
                "Quilt" => await _loaderInstall.InstallLoaderAsync(inst.Id, inst.VersionId, "quilt", inst.ModLoaderVersion),
                "NeoForge" => await _loaderInstall.InstallLoaderAsync(inst.Id, inst.VersionId, "neoforge", inst.ModLoaderVersion),
                "OptiFine" => await _loaderInstall.InstallOptiFineAsync(inst.Id, inst.VersionId, inst.ModLoaderVersion),
                _ => null
            };
            if (res == null || !res.Success)
            {
                string detail = res?.ErrorMessage ?? "无对应安装通道";
                App.WriteAppLog($"[启动] 加载器补装失败:{detail}");
                return $"{inst.ModLoader} 自动补装失败({detail}),可在「新装版本」向导手动安装该加载器";
            }

            // 修复 2:老版本(如 1.7.10 + Forge 需 Java 8)的 Java 推荐错配——
            // 版本级 Java 直接指到内置池匹配项,避免落到全局的高版本 Java 导致秒退
            int rec = JavaRuntimeService.RecommendJavaMajor(inst.VersionId);
            if (inst.JavaMajorVersion != rec)
            {
                inst.JavaMajorVersion = rec;
                var match = FindBestRuntimeJava(rec);
                inst.JavaPath = match?.JavaExe ?? "";
                App.WriteAppLog($"[启动] 已自动修正版本级 Java 为 Java {rec}" +
                               (match == null ? "(池中暂无匹配,将提示下载)" : $":{match.JavaExe}"));
            }

            // 修复 3:lwjgl3ify 需魔改补丁链,与标准 Forge 链不兼容(GTNH Java 17~25 包遗留)——禁用且保留文件
            DisableLwjgl3ifyMod(inst);

            _instanceService.SaveInstance(inst);
            App.WriteAppLog($"[启动] ✓ 加载器补装完成,继续启动流程");
            return null;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[启动] 加载器补装异常:{ex.Message}");
            return $"{inst.ModLoader} 自动补装异常:{ex.Message}";
        }
    }

    /// <summary>禁用 mods 目录中的 lwjgl3ify(重命名 .disabled,不删文件;可在模组管理页重新启用)</summary>
    private void DisableLwjgl3ifyMod(GameInstance inst)
    {
        try
        {
            string modsDir = Path.Combine(_instanceService.GetInstanceDir(inst.Id), "mods");
            if (!Directory.Exists(modsDir)) return;
            foreach (var f in Directory.GetFiles(modsDir, "lwjgl3ify*.jar"))
            {
                File.Move(f, f + ".disabled");
                App.WriteAppLog($"[启动] 已禁用与标准启动链不兼容的模组:{Path.GetFileName(f)}");
            }
        }
        catch (Exception ex) { App.WriteAppLog($"[启动] 禁用 lwjgl3ify 异常(不阻断):{ex.Message}"); }
    }

    private async Task<string> ResolveJavaPathAsync(GameInstance inst)
    {
        // 1) 游戏版本级 Java 优先(「版本设置」里手动指定的对该版本生效,高于全局)
        string javaPath = inst.JavaPath;
        if (!string.IsNullOrEmpty(javaPath))
        {
            if (File.Exists(javaPath)) return javaPath;
            // 实例级 Java 已失效(被删除/移动):提示并回退全局/公共池,不静默失败
            App.WriteAppLog($"[启动] ⚠ 游戏版本级 Java 已失效:{javaPath},自动回退全局/公共池 Java");
            inst.JavaPath = "";
        }

        // 2) 主页全局选择的 Java(未指定版本级时全局生效)
        string globalJava = _configService.Config.JavaPath ?? "";
        if (!string.IsNullOrEmpty(globalJava))
        {
            if (File.Exists(globalJava)) return globalJava;
            // 全局 Java 文件丢失(如被删除/移动):记日志并降级,不静默吞掉用户的选择
            App.WriteAppLog($"[启动] ⚠ 全局 Java 已失效(文件不存在):{globalJava},自动降级匹配");
        }

        // 未选/已失效:自动从 runtimes 公共池匹配最接近推荐版本的已就绪 Java
        var bestJava = FindBestRuntimeJava(inst.JavaMajorVersion);
        if (bestJava != null)
        {
            javaPath = bestJava.JavaExe;
            inst.JavaPath = javaPath;
            _instanceService.SaveInstance(inst);
            App.WriteAppLog($"[启动] 自动匹配公共池 Java:{javaPath}");
            return javaPath;
        }

        // 内置池无 Java(纯净包首次启动常见):兜底扫描系统已装 Java
        string? sysJava = await FindBestSystemJavaAsync(inst.JavaMajorVersion);
        if (!string.IsNullOrEmpty(sysJava))
        {
            inst.JavaPath = sysJava;
            _instanceService.SaveInstance(inst);
            App.WriteAppLog($"[启动] 内置池无 Java,自动匹配系统 Java:{sysJava}");
            return sysJava;
        }
        return javaPath;
    }

    /// <summary>从系统已装 Java(JAVA_HOME/注册表/PATH/常见目录扫描结果)匹配推荐主版本;无精确匹配取最高版本</summary>
    private async Task<string?> FindBestSystemJavaAsync(int requiredMajorVersion)
    {
        try
        {
            await _javaScanService.ScanAsync();
            var javas = _javaScanService.FoundJavas
                .Where(j => j.MajorVersion > 0 && !string.IsNullOrEmpty(j.Path) && File.Exists(j.Path))
                .ToList();
            if (javas.Count == 0) return null;
            if (requiredMajorVersion > 0)
            {
                var exact = javas.FirstOrDefault(j => j.MajorVersion == requiredMajorVersion);
                if (exact != null) return exact.Path;
            }
            return javas.OrderByDescending(j => j.MajorVersion).First().Path;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[启动] 系统 Java 扫描失败:{ex.Message}");
            return null;
        }
    }

    private InstalledJavaEntry? FindBestRuntimeJava(int requiredMajorVersion)
    {
        try
        {
            var installed = _javaRuntimeService.ListInstalledRuntimes();
            var ready = installed.Where(r => r.Status == "已就绪" && !string.IsNullOrEmpty(r.JavaExe)).ToList();
            if (requiredMajorVersion > 0)
            {
                // 精确匹配主版本(避免 "Java 17".Contains("7") 误命中)
                var exact = ready.FirstOrDefault(r =>
                    r.MajorVersion == $"Java {requiredMajorVersion}");
                if (exact != null) return exact;
            }
            return ready.FirstOrDefault();
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[启动] 查找 runtimes Java 失败:{ex.Message}");
            return null;
        }
    }

    // ==================== 进程优化 ====================

    private void ApplyProcessOptimizations(Process proc)
    {
        try
        {
            if (_configService.Config.HighPriorityProcess)
            {
                proc.PriorityClass = ProcessPriorityClass.AboveNormal;
                App.WriteAppLog("[启动] 进程优先级:AboveNormal");
            }
        }
        catch (Exception ex) { App.WriteAppLog($"[启动] 设置优先级失败:{ex.Message}"); }

        try
        {
            if (_configService.Config.CpuAffinityEnabled)
            {
                int cores = MemoryMonitorService.GetPhysicalCoreCount();
                int gameCores = Math.Max(1, cores - 1);
                long mask = (1L << gameCores) - 1;
                if (mask > 0)
                {
                    proc.ProcessorAffinity = (IntPtr)mask;
                    App.WriteAppLog($"[启动] CPU 亲和性:核心={gameCores}/{cores} 掩码=0x{mask:X}");
                }
            }
        }
        catch (Exception ex) { App.WriteAppLog($"[启动] 设置 CPU 亲和性失败:{ex.Message}"); }
    }

    // ==================== 其他 ====================

    public void KillGame()
    {
        try
        {
            if (_currentProcess != null && !_currentProcess.HasExited)
                _currentProcess.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // 进程可能已退出或拒绝访问,不向调用方抛异常
            App.WriteAppLog($"[启动] 结束游戏进程失败:{ex.Message}");
        }
    }

    public static string InferComponentByMajorVersion(int majorVersion)
    {
        if (majorVersion <= 8) return "jre-legacy";
        if (majorVersion <= 16) return "java-runtime-alpha";
        if (majorVersion <= 20) return "java-runtime-gamma";
        return "java-runtime-delta";
    }
}

// ==================== Version JSON 数据模型 ====================

/// <summary>解析后的版本元数据(支持 inheritsFrom 合并)</summary>
internal class VersionMeta
{
    public string Id { get; set; } = "";
    public string MainClass { get; set; } = "";
    public string? InheritsFrom { get; set; }
    public string? AssetType { get; set; }
    public AssetIndexInfo? AssetIndex { get; set; }
    public List<LibraryEntry> Libraries { get; } = new();
    public List<ArgEntry> GameArguments { get; set; } = new();
    public List<ArgEntry> JvmArguments { get; set; } = new();
    /// <summary>游戏参数来自旧格式 minecraftArguments(自包含完整集,非增量覆盖语义)</summary>
    public bool LegacyGameArguments { get; set; }

    /// <summary>合并父版本(子版本覆盖 mainClass,libraries 追加在前面,arguments 追加在前面)</summary>
    public void MergeParent(VersionMeta parent)
    {
        if (string.IsNullOrEmpty(MainClass)) MainClass = parent.MainClass;
        if (AssetIndex == null) AssetIndex = parent.AssetIndex;

        // 子 libraries 优先(去重)
        var childNames = new HashSet<string>(Libraries.Select(l => l.Name));
        foreach (var lib in parent.Libraries)
            if (!childNames.Contains(lib.Name)) Libraries.Add(lib);

        // 子 arguments 在前,父在后(Minecraft 要求子覆盖父);
        // 但旧格式 minecraftArguments 是自包含完整集(如独立式老 Forge 1.7.10 自带全套参数),
        // 再追加父版本旧参数会让 --gameDir 等重复,joptsimple 报 MultipleArguments 拒启 → 只留子的
        if (GameArguments.Count == 0) GameArguments = parent.GameArguments;
        else if (!(LegacyGameArguments && parent.LegacyGameArguments)) GameArguments.AddRange(parent.GameArguments);

        if (JvmArguments.Count == 0) JvmArguments = parent.JvmArguments;
        else JvmArguments.AddRange(parent.JvmArguments);
    }

    /// <summary>获取最终游戏参数(过滤 rules)</summary>
    public List<string> GetGameArgs()
    {
        var result = new List<string>();
        foreach (var entry in GameArguments)
        {
            if (!GameLaunchService_CheckRules(entry.Rules)) continue;
            if (!string.IsNullOrEmpty(entry.Value)) result.Add(entry.Value);
            result.AddRange(entry.MultiValues);
        }
        return result;
    }

    /// <summary>获取最终 JVM 参数(过滤 rules;仅跳过 -Xms/-Xmx,其余原样保留)</summary>
    public List<string> GetJvmArgs()
    {
        var result = new List<string>();
        foreach (var entry in JvmArguments)
        {
            if (!GameLaunchService_CheckRules(entry.Rules)) continue;
            if (!string.IsNullOrEmpty(entry.Value))
            {
                string v = entry.Value;
                // 仅跳过内存参数(启动器自己控制 -Xms/-Xmx)
                if (v.StartsWith("-Xms") || v.StartsWith("-Xmx"))
                    continue;
                result.Add(v);
            }
            foreach (var mv in entry.MultiValues)
            {
                if (!mv.StartsWith("-Xms") && !mv.StartsWith("-Xmx"))
                    result.Add(mv);
            }
        }
        return result;
    }

    // 静态辅助:访问 GameLaunchService 的 rules 检查
    private static bool GameLaunchService_CheckRules(List<RuleInfo>? rules)
    {
        if (rules == null || rules.Count == 0) return true;
        bool allowed = false;
        foreach (var rule in rules)
        {
            // features 条件(is_demo_user / has_custom_resolution): 启动器暂不支持,跳过
            if (rule.HasFeatures) continue;

            // OS 名称匹配
            if (!string.IsNullOrEmpty(rule.OsName))
            {
                bool osMatch = rule.OsName switch
                {
                    "windows" => RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                    "linux" => RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
                    "osx" => RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
                    _ => false
                };
                if (!osMatch) continue;
            }

            // OS 架构匹配(x86 / x64)
            if (!string.IsNullOrEmpty(rule.OsArch))
            {
                string actualArch = Environment.Is64BitOperatingSystem ? "x86_64" : "x86";
                if (rule.OsArch != actualArch) continue;
            }

            allowed = rule.Action == "allow";
        }
        return allowed;
    }
}

internal class AssetIndexInfo
{
    public string Id { get; set; } = "";
    public string? Url { get; set; }
}

internal class LibraryEntry
{
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public List<RuleInfo>? Rules { get; set; }
    public Dictionary<string, string> NativesMap { get; } = new();
    public Dictionary<string, string> ClassifierPaths { get; } = new();
    public List<string> ExtractExclude { get; } = new();
}

internal class RuleInfo
{
    public string Action { get; set; } = "allow";
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public string? OsArch { get; set; }
    /// <summary>是否包含 features 条件(如 is_demo_user / has_custom_resolution)</summary>
    public bool HasFeatures { get; set; }
}

internal class ArgEntry
{
    public string Value { get; set; } = "";
    public List<RuleInfo>? Rules { get; set; }
    public List<string> MultiValues { get; } = new();
}
