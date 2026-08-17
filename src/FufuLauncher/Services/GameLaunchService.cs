// GameLaunchService.cs — 游戏启动服务(完整重写)
// 可爱的芙芙
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
using System.Threading.Tasks;
using FufuLauncher.Interaction;

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
    private readonly MemoryMonitorService _memoryMonitor;
    private readonly ProcessGuardService _processGuard;
    private readonly GameMemoryWatchService _memoryWatch;

    private Process? _currentProcess;

    public GameLaunchService(InstanceService instanceService,
                                VersionManifestService versionManifest,
                                HashVerifyService hashVerify,
                                AccountService accountService,
                                GameLogService gameLog,
                                ConfigService configService,
                                JavaRuntimeService javaRuntimeService,
                                MemoryMonitorService memoryMonitor,
                                ProcessGuardService processGuard,
                                GameMemoryWatchService memoryWatch)
    {
        _instanceService = instanceService;
        _accountService = accountService;
        _gameLog = gameLog;
        _configService = configService;
        _javaRuntimeService = javaRuntimeService;
        _memoryMonitor = memoryMonitor;
        _processGuard = processGuard;
        _memoryWatch = memoryWatch;
    }

    public bool IsGameRunning => _currentProcess != null && !_currentProcess.HasExited;

    /// <summary>游戏进程退出事件(后台线程触发,订阅者需自行切 UI 线程)</summary>
    public event Action? GameExited;

    /// <summary>当前正在运行的游戏实例 Id(无游戏运行时为 null)。供卸载拦截使用:禁止卸载运行中的版本</summary>
    public string? RunningInstanceId { get; private set; }

    /// <summary>指定实例是否正在运行</summary>
    public bool IsInstanceRunning(string instanceId) =>
        IsGameRunning && RunningInstanceId == instanceId;

    // ==================== 启动入口 ====================

    public async Task<LaunchResult> LaunchAsync(string instanceId, bool forceLaunch = false)
    {
        var inst = _instanceService.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null)
            return new LaunchResult { Success = false, ErrorMessage = "游戏版本不存在" };

        if (forceLaunch)
            App.WriteAppLog($"[启动] ⚡ 强制启动模式(跳过前置校验)");

        // ---- 1. 校验账号(强制模式跳过)----
        if (!forceLaunch)
        {
            bool tokenOk = await _accountService.EnsureValidTokenAsync();
            if (!tokenOk)
                return new LaunchResult { Success = false, ErrorMessage = "账号令牌已过期且无法刷新,请重新登录" };
        }

        // ---- 2. 解析 Java 路径(只用 runtimes)----
        string javaPath = ResolveJavaPath(inst);
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
            bool cancel = !FufuMessage.Confirm(System.Windows.Application.Current?.MainWindow,
                "Java 校验未通过",
                $"Java 文件存在但无法执行(java -version 失败):\n{javaPath}\n\n是否取消启动?(选「取消」则仍尝试启动)",
                okText: "取消启动", danger: true);
            if (cancel)
                return new LaunchResult { Success = false, ErrorMessage = "Java 完整性校验未通过" };
        }

        // ---- 3. 校验游戏文件(游戏本体在全局共享 versions 目录)----
        string mcDir = _instanceService.GetMinecraftDir(instanceId); // 游戏工作目录
        string versionJsonPath = Path.Combine(AppPaths.Versions, inst.VersionId, $"{inst.VersionId}.json");
        if (!File.Exists(versionJsonPath))
            return new LaunchResult { Success = false, ErrorMessage = $"版本文件缺失:{versionJsonPath}" };

        string clientJar = Path.Combine(AppPaths.Versions, inst.VersionId, $"{inst.VersionId}.jar");
        if (!forceLaunch && !File.Exists(clientJar))
            return new LaunchResult { Success = false, ErrorMessage = $"客户端 jar 缺失:{inst.VersionId}.jar" };

        // ---- 4. 解析 version JSON(含继承链)----
        VersionMeta versionMeta;
        try
        {
            versionMeta = LoadVersionMeta(AppPaths.Versions, inst.VersionId);
        }
        catch (Exception ex)
        {
            return new LaunchResult { Success = false, ErrorMessage = $"解析版本文件失败:{ex.Message}" };
        }

        // ---- 5. 内存分配(借鉴 PCL2/HMCL:智能模式实时计算;手动模式用设置页保存值;启动时实时复验,严禁超分)----
        int javaMajor = DetectJavaMajorVersion(javaPath, inst.JavaMajorVersion);
        bool java64 = MemoryMonitorService.IsJava64Bit(javaPath);
        int xmx, xms;
        if (_configService.Config.AutoMemoryMode)
        {
            int smartXmx = _memoryMonitor.CalculateSmartXmx();
            if (smartXmx >= 512)
            {
                xmx = smartXmx;
                App.WriteAppLog($"[启动] 智能内存:Xmx={xmx}MB Xms=Xmx(实例原值 Xmx={inst.Xmx}MB,总内存曲线推荐 {_memoryMonitor.RecommendByTotalMb()}MB)");
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
            App.WriteAppLog($"[启动] 手动内存:Xms={xms}MB Xmx={xmx}MB(来自设置页)");
        }

        // 32 位 Java 限制:32 位 JVM 堆上限约 1.5~2GB,超配直接启动即崩(PCL2 同款防护)
        if (!java64 && xmx > 1024)
        {
            App.WriteAppLog($"[启动] ⚠ 检测到 32 位 Java,内存由 {xmx}MB 强制下调至 1024MB(32 位 JVM 无法寻址更大堆)");
            xmx = 1024;
        }

        // 启动时实时复验(借鉴 PCL2):从设置保存到点击启动期间内存状况可能变化,超限自动下调,绝不超分
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
        // 默认自动取 Xmx 的 0.75 倍,可在设置页手动覆盖(>0 生效)。
        int cfgDirectMb = _configService.Config.MaxDirectMemoryMb;
        int directMb = cfgDirectMb > 0 ? Math.Max(64, cfgDirectMb)
                                       : Math.Max(128, (int)(xmx * 0.75));
        App.WriteAppLog($"[启动] 堆外锁死:MaxDirectMemorySize={directMb}MB({(cfgDirectMb > 0 ? "手动设置" : "自动 0.75×Xmx")})");

        // ---- 5.2 总预估内存安全校验:Xmx + MaxDirectMemorySize 不得超当前可用物理内存 ----
        long availPhysMb = _memoryMonitor.GetCurrent().AvailableBytes / (1024L * 1024);
        long estimateMb = (long)xmx + directMb;
        if (estimateMb > availPhysMb)
        {
            App.WriteAppLog($"[启动] ⚠ 内存风险:预估总量 {estimateMb}MB(Xmx {xmx} + 直接内存 {directMb})超过当前可用物理内存 {availPhysMb}MB");
            bool go = FufuMessage.Confirm(System.Windows.Application.Current?.MainWindow,
                "内存风险警告",
                $"游戏预估总内存 = Xmx {xmx}MB + 堆外直接内存 {directMb}MB = {estimateMb}MB,\n" +
                $"已超过当前可用物理内存 {availPhysMb}MB。\n\n" +
                "继续启动可能导致系统内存不足、卡顿甚至崩溃。\n建议:关闭占用内存的程序,或在设置中调低内存后重试。\n\n是否仍要继续启动?",
                okText: "继续启动", danger: true);
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
        string versionType = "可爱的芙芙";

        // 占位符字典
        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["${auth_player_name}"] = username,
            ["${auth_uuid}"] = uuid,
            ["${auth_access_token}"] = accessToken,
            ["${auth_session}"] = accessToken,
            ["${user_type}"] = userType,
            ["${version_name}"] = inst.VersionId,
            ["${game_directory}"] = mcDir,
            ["${assets_root}"] = assetsDir,
            ["${assets_index_name}"] = assetIndexId,
            ["${game_assets}"] = assetsDir,
            ["${version_type}"] = versionType,
            ["${user_properties}"] = "{}",
            ["${clientid}"] = AuthService.ClientId,
            ["${auth_xuid}"] = "",
            ["${launcher_name}"] = "可爱的芙芙",
            ["${launcher_version}"] = "1.0",
            ["${classpath}"] = classpath,
            ["${natives_directory}"] = nativesDir,
            ["${library_directory}"] = AppPaths.Libraries,
            ["${libraries_directory}"] = AppPaths.Libraries,
            ["${classpath_separator}"] = ";",
            ["${resolution_width}"] = inst.Width.ToString(),
            ["${resolution_height}"] = inst.Height.ToString(),
            ["${primary_jar}"] = Path.Combine(AppPaths.Versions, inst.VersionId, $"{inst.VersionId}.jar"),
        };

        // 构建参数列表
        var allArgs = new List<string>();

        // JVM 参数
        allArgs.Add($"-Xms{xms}m");
        allArgs.Add($"-Xmx{xmx}m");

        // 内存预提交(AlwaysPreTouch):启动时一次性提交全部堆页,避免游戏中途缺页抖动。
        // 两类冲突必须跳过(PCL2/HMCL 默认根本不开此项):
        // 1. ZGC 模式:ZGC 对堆做多重映射并自行管理提交,叠加 PreTouch 会加倍提交压力,
        //    提交额度(物理内存+页面文件)不足时 JVM 启动即崩——这是之前"内存分配崩溃"的主要根因;
        // 2. 大堆(≥6GB):启动时强提交全部页面易压垮提交额度,改为按需提交更稳。
        bool useZgc = _configService.Config.MultiCoreGcOptimize && javaMajor >= 17;
        if (_configService.Config.MemoryPreCommit)
        {
            if (useZgc)
                App.WriteAppLog("[启动] ZGC 模式:跳过 AlwaysPreTouch(ZGC 自行管理内存提交,强制预提交易超提交额度导致崩溃)");
            else if (xmx >= 6144)
                App.WriteAppLog($"[启动] 大堆模式(Xmx={xmx}MB ≥ 6GB):跳过 AlwaysPreTouch,降低启动提交压力");
            else
                allArgs.Add("-XX:+AlwaysPreTouch");
        }

        // version JSON 提供的 JVM 参数(含 rules 过滤)
        var jvmArgs = versionMeta.GetJvmArgs();
        foreach (var arg in jvmArgs)
        {
            string resolved = ReplacePlaceholders(arg, placeholders);
            allArgs.Add(resolved);
        }

        // 堆外直接内存硬锁:放在版本 JSON 参数之后,确保本启动器的锁死值最终生效
        allArgs.Add($"-XX:MaxDirectMemorySize={directMb}m");

        // GC 多核优化(用户配置):按实际 Java 版本选型(Java17+ ZGC,低版本 G1GC)+ 核心数动态线程
        // 周期 GC:每 10 秒触发一次并发 GC,回收废弃 DirectByteBuffer,缓解堆外只涨不释放(Fabric/Sodium/Iris 环境尤为必要)
        if (_configService.Config.MultiCoreGcOptimize)
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

            // 设置环境变量(参考 PCL2)
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

            // 输出完整参数列表(调试用)
            App.WriteAppLog($"[启动] ===== 启动参数开始 =====");
            foreach (var arg in allArgs)
                App.WriteAppLog($"[启动]   {arg}");
            App.WriteAppLog($"[启动] ===== 启动参数结束 =====");

            _currentProcess = Process.Start(psi);
            if (_currentProcess == null)
                return new LaunchResult { Success = false, ErrorMessage = "无法启动 Java 进程" };

            // 记录运行中实例 Id(供卸载拦截:运行中的版本禁止卸载)
            RunningInstanceId = inst.Id;

            // 进程托管登记:关闭启动器时强制回收游戏进程树,杜绝残留占用文件锁
            _processGuard.Register(_currentProcess, $"游戏进程 [{inst.Name}]");

            // 进程优化
            ApplyProcessOptimizations(_currentProcess);

            // 捕获输出到日志
            _gameLog.AttachToProcess(_currentProcess);

            // 启动堆+堆外内存监控(每 2s 采样,持续暴涨预警)
            _memoryWatch.Start(_currentProcess, xmx);

            // 更新游玩时间
            inst.LastPlayedAt = DateTime.Now;
            _instanceService.SaveInstance(inst);

            var proc = _currentProcess;
            var launchedAt = inst.LastPlayedAt;
            _ = Task.Run(() =>
            {
                try
                {
                    proc.WaitForExit();
                    _gameLog.AppendLine($"[可爱的芙芙] 游戏进程已退出(代码 {proc.ExitCode})");
                    inst.TotalPlayTimeSeconds += (long)(DateTime.Now - launchedAt).TotalSeconds;
                    _instanceService.SaveInstance(inst);
                }
                catch (Exception ex)
                {
                    _gameLog.AppendLine($"[可爱的芙芙] 监控进程异常:{ex.Message}");
                }
                finally
                {
                    _memoryWatch.Stop();
                    proc.Dispose();
                    if (ReferenceEquals(_currentProcess, proc)) _currentProcess = null;
                    RunningInstanceId = null;
                    try { GameExited?.Invoke(); } catch { /* 订阅者异常不影响清理 */ }
                }
            });

            return new LaunchResult { Success = true, Process = _currentProcess };
        }
        catch (Exception ex)
        {
            return new LaunchResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    // ==================== Version JSON 解析 ====================

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

    private string ResolveJavaPath(GameInstance inst)
    {
        // 1) 主页全局选择的 Java 优先(全局生效,启动直接读取)
        string globalJava = _configService.Config.JavaPath ?? "";
        if (!string.IsNullOrEmpty(globalJava))
        {
            if (File.Exists(globalJava)) return globalJava;
            // 全局 Java 文件丢失(如被删除/移动):记日志并降级,不静默吞掉用户的选择
            App.WriteAppLog($"[启动] ⚠ 全局 Java 已失效(文件不存在):{globalJava},自动降级匹配");
        }

        // 2) 实例自身绑定的 Java(兼容旧数据)
        string javaPath = inst.JavaPath;
        if (!string.IsNullOrEmpty(javaPath) && File.Exists(javaPath))
            return javaPath;

        // 未选/已失效:自动从 runtimes 公共池匹配最接近推荐版本的已就绪 Java
        var bestJava = FindBestRuntimeJava(inst.JavaMajorVersion);
        if (bestJava != null)
        {
            javaPath = bestJava.JavaExe;
            inst.JavaPath = javaPath;
            _instanceService.SaveInstance(inst);
            App.WriteAppLog($"[启动] 自动匹配公共池 Java:{javaPath}");
        }
        return javaPath;
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
        if (_currentProcess != null && !_currentProcess.HasExited)
            _currentProcess.Kill(entireProcessTree: true);
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

    /// <summary>合并父版本(子版本覆盖 mainClass,libraries 追加在前面,arguments 追加在前面)</summary>
    public void MergeParent(VersionMeta parent)
    {
        if (string.IsNullOrEmpty(MainClass)) MainClass = parent.MainClass;
        if (AssetIndex == null) AssetIndex = parent.AssetIndex;

        // 子 libraries 优先(去重)
        var childNames = new HashSet<string>(Libraries.Select(l => l.Name));
        foreach (var lib in parent.Libraries)
            if (!childNames.Contains(lib.Name)) Libraries.Add(lib);

        // 子 arguments 在前,父在后(Minecraft 要求子覆盖父)
        if (GameArguments.Count == 0) GameArguments = parent.GameArguments;
        else GameArguments.AddRange(parent.GameArguments);

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
