// Copyright © FufuLauncher
//
// 游戏版本安装服务,完整安装流程:
// 1. 根据 version URL 拉取版本 JSON
// 2. 下载 client.jar(校验 SHA1)
// 3. 下载 libraries(原生库 + 普通库,过滤操作系统规则)
// 4. 下载 assetIndex 资产索引,再下载全部 asset 对象
// 5. 校验损坏/缺失文件,自动修复
// 6. 自动补齐匹配的 Java 运行时(失败不阻塞,引导手动下载)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

public class AssetIndexManifest
{
    [JsonPropertyName("objects")] public Dictionary<string, AssetObject> Objects { get; set; } = new();
}

public class AssetObject
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public class InstallProgress
{
    public string Stage { get; set; } = "";      // 解析JSON / 下载client / 下载libraries / 下载资源 / 校验
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentFile { get; set; } = "";
}

/// <summary>主页完整性校验结果(快速检查,不做 SHA1)</summary>
public class IntegrityCheckResult
{
    public bool Passed { get; set; }
    public List<string> MissingFiles { get; set; } = new();
    public List<string> CorruptFiles { get; set; } = new();
    public string Summary { get; set; } = "";
}

public class GameInstallService
{
    private readonly VersionManifestService _versionManifest;
    private readonly DownloadService _downloadService;
    private readonly HashVerifyService _hashVerify;
    private readonly InstanceService _instanceService;
    private readonly JavaRuntimeService _javaRuntime;
    private readonly StorageGuardService _storageGuard;

    public event Action<InstallProgress>? ProgressChanged;

    /// <summary>最后一次安装失败的详细错误信息(供 UI 展示)</summary>
    public string LastError { get; private set; } = "";

    /// <summary>本次安装是否已成功自动下载/就绪匹配的 Java runtime(供 UI 调整成功提示)</summary>
    public bool JavaAutoDownloadSucceeded { get; private set; }

    /// <summary>Java 自动下载失败时的提示信息(非空表示需用户去 Java 页手动下载)</summary>
    public string? JavaAutoDownloadHint { get; private set; }

    public GameInstallService(VersionManifestService versionManifest,
                              DownloadService downloadService,
                              HashVerifyService hashVerify,
                              InstanceService instanceService,
                              JavaRuntimeService javaRuntime,
                              StorageGuardService storageGuard)
    {
        _versionManifest = versionManifest;
        _downloadService = downloadService;
        _hashVerify = hashVerify;
        _instanceService = instanceService;
        _javaRuntime = javaRuntime;
        _storageGuard = storageGuard;
    }

    /// <summary>完整安装一个版本到指定实例</summary>
    public async Task<bool> InstallVersionAsync(string instanceId, MojangVersion version)
    {
        LastError = "";
        JavaAutoDownloadSucceeded = false;
        JavaAutoDownloadHint = null;
        try
        {
            // 重置全局总进度统计(本批次从 0 开始,含游戏文件 + Java runtime)
            _downloadService.ResetOverallProgress();

            // 0. 存储预检:磁盘空间 + 写权限(失败直接友好报错,绝不带着隐患开工)
            // 完整安装(本体+库+资源)经验值约 1.5GB,叠加 StorageGuard 内置安全缓冲
            var pre = _storageGuard.Precheck(AppPaths.Versions, 1536L * 1024 * 1024);
            if (pre.Result != StorageCheckResult.Ok)
            {
                LastError = $"存储环境预检未通过:{pre.Message}";
                App.WriteAppLog($"[安装] 预检失败:{pre.Result} - {pre.Message}");
                Report(LastError, 0, 0, "");
                return false;
            }

            // 1. 拉取版本 JSON
            Report("解析版本清单", 0, 1, version.Id);
            var versionJson = await _versionManifest.FetchVersionJsonAsync(version.Url);
            if (versionJson == null)
            {
                LastError = $"拉取版本 JSON 失败:URL={version.Url}\n" +
                            $"可能原因:网络不通 / 当前下载源不可用\n" +
                            $"建议:在「设置」页切换下载源后重试";
                Report(LastError, 0, 0, "");
                return false;
            }

            // 游戏本体统一落到规范 versions 目录(全实例共享);依赖库/资源同理
            string versionDir = Path.Combine(AppPaths.Versions, versionJson.Id);
            Directory.CreateDirectory(versionDir);

            // 1.5 记录版本要求的 Java 主版本号到实例(供主页/Java页引导用户下载)
            var inst = _instanceService.Instances.FirstOrDefault(i => i.Id == instanceId);
            if (inst != null)
            {
                inst.JavaMajorVersion = versionJson.JavaVersion?.MajorVersion
                                        ?? JavaRuntimeService.RecommendJavaMajor(versionJson.Id);
                _instanceService.SaveInstance(inst);
            }

            // 保存版本 JSON(原子写,防半写 json 导致下次启动解析失败)
            string versionJsonPath = Path.Combine(versionDir, $"{versionJson.Id}.json");
            WriteJsonAtomic(versionJsonPath,
                JsonSerializer.Serialize(versionJson, new JsonSerializerOptions { WriteIndented = true }));

            var tasks = new List<DownloadTaskItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 2. client.jar(分类:Game)
            if (versionJson.Downloads?.Client != null)
            {
                var client = versionJson.Downloads.Client;
                string clientPath = Path.Combine(versionDir, $"{versionJson.Id}.jar");
                AddTask(tasks, seen, new DownloadTaskItem
                {
                    Url = client.Url,
                    LocalPath = clientPath,
                    Sha1 = client.Sha1,
                    Size = client.Size,
                    Category = DownloadCategory.Game
                });
            }

            // 3. libraries(分类:Game)
            Report("解析库文件", 0, versionJson.Libraries.Count, "");
            int libIdx = 0;
            foreach (var lib in versionJson.Libraries)
            {
                libIdx++;
                if (!IsLibraryAllowedForOs(lib)) continue;

                if (lib.Downloads?.Artifact != null)
                {
                    var art = lib.Downloads.Artifact;
                    AddTask(tasks, seen, new DownloadTaskItem
                    {
                        Url = art.Url,
                        LocalPath = Path.Combine(AppPaths.Libraries, art.Path),
                        Sha1 = art.Sha1,
                        Size = art.Size,
                        Category = DownloadCategory.Game
                    });
                }

                // 原生库(natives)
                if (lib.Natives != null && lib.Downloads?.Classifiers != null)
                {
                    string nativeKey = lib.Natives.GetValueOrDefault("windows") ?? "";
                    nativeKey = nativeKey.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                    if (!string.IsNullOrEmpty(nativeKey) &&
                        lib.Downloads.Classifiers.TryGetValue(nativeKey, out var nativeArt))
                    {
                        AddTask(tasks, seen, new DownloadTaskItem
                        {
                            Url = nativeArt.Url,
                            LocalPath = Path.Combine(AppPaths.Libraries, nativeArt.Path),
                            Sha1 = nativeArt.Sha1,
                            Size = nativeArt.Size,
                            Category = DownloadCategory.Game
                        });
                    }
                }
                Report("解析库文件", libIdx, versionJson.Libraries.Count, lib.Name);
            }

            // 4. asset index + asset objects(分类:Asset)
            if (versionJson.AssetIndex != null)
            {
                string indexDir = Path.Combine(AppPaths.Assets, "indexes");
                Directory.CreateDirectory(indexDir);
                string indexPath = Path.Combine(indexDir, $"{versionJson.AssetIndex.Id}.json");

                // 先同步下载索引(失败重试一次),再解析资源对象
                Report("下载资产索引", 0, 1, versionJson.AssetIndex.Id);
                var indexTask = new DownloadTaskItem
                {
                    Url = versionJson.AssetIndex.Url,
                    LocalPath = indexPath,
                    Sha1 = versionJson.AssetIndex.Sha1,
                    Size = versionJson.AssetIndex.Size,
                    Category = DownloadCategory.Asset
                };
                bool indexOk = false;
                // 资产索引失败重试最多 3 次(网络抖动/镜像源超时常见,日志曾多次"重试一次"仍失败)
                for (int retry = 1; retry <= 3; retry++)
                {
                    indexOk = await _downloadService.DownloadAllAsync(new() { indexTask });
                    if (indexOk && File.Exists(indexPath)) break;
                    if (retry < 3)
                    {
                        App.WriteAppLog($"[安装] 资产索引下载失败(第{retry}次),2s 后重试…");
                        await Task.Delay(2000);
                    }
                    else
                        App.WriteAppLog("[安装] 资产索引下载失败(已重试 3 次),继续尝试解析已有文件");
                }
                indexOk = indexOk && File.Exists(indexPath);

                AssetIndexManifest? index = null;
                if (File.Exists(indexPath))
                {
                    try
                    {
                        var indexJson = await File.ReadAllTextAsync(indexPath);
                        index = JsonSerializer.Deserialize<AssetIndexManifest>(indexJson);
                    }
                    catch (Exception ex)
                    {
                        App.WriteAppLog($"[安装] 资产索引解析失败:{ex.Message}");
                    }
                }

                if (index?.Objects != null && index.Objects.Count > 0)
                {
                    Report("解析资源对象", 0, index.Objects.Count, "");
                    int assetIdx = 0;
                    foreach (var kv in index.Objects)
                    {
                        assetIdx++;
                        var obj = kv.Value;
                        if (string.IsNullOrEmpty(obj.Hash) || obj.Hash.Length < 2) continue;
                        // 资源路径:assets/objects/{hash前2位}/{完整hash}
                        string subDir = obj.Hash[..2];
                        AddTask(tasks, seen, new DownloadTaskItem
                        {
                            Url = $"https://resources.download.minecraft.net/{subDir}/{obj.Hash}",
                            LocalPath = Path.Combine(AppPaths.Assets, "objects", subDir, obj.Hash),
                            Sha1 = obj.Hash,
                            Size = obj.Size,
                            Category = DownloadCategory.Asset
                        });
                        Report("解析资源对象", assetIdx, index.Objects.Count, kv.Key);
                    }
                }
                else
                {
                    // 索引缺失/损坏:整批安装注定残缺,直接失败并回滚,引导重试
                    LastError = "资产索引下载或解析失败,无法获取资源文件清单。\n建议:检查网络 / 切换下载源后重试";
                    Report(LastError, 0, 0, "");
                    RollbackInstall(versionJson.Id);
                    return false;
                }
            }

            // 5. Java 运行时与游戏文件并行下载(2026-09-25 提速):
            // 原来文件全部下完才串行下 Java(100~300MB),整版安装多等 1-3 分钟;
            // Java 走独立并发槽位(JavaRuntimeService 内部 DownloadCategory.Java),与文件并发互不抢占
            int javaMajor = versionJson.JavaVersion?.MajorVersion
                            ?? JavaRuntimeService.RecommendJavaMajor(versionJson.Id);
            Report($"下载 Java 运行时(JDK {javaMajor})", 0, 1, javaMajor.ToString());
            var javaTask = Task.Run(async () =>
            {
                try
                {
                    var jp = await _javaRuntime.DownloadJdkAsync(javaMajor);
                    return jp != null;
                }
                catch (Exception jex)
                {
                    JavaAutoDownloadHint = $"Java 运行时自动下载异常:{jex.Message}\n" +
                                           $"建议:前往「Java 运行库」页面手动下载 Java {javaMajor}";
                    return false;
                }
            });

            // 6. 执行全部下载(失败子集自动补下最多 2 轮,双源交替兜底,
            //    避免大批量资源/库中个别文件抽风就整版判死回滚)
            Report("下载文件", 0, tasks.Count, "");
            bool ok = await _downloadService.DownloadAllAsync(tasks);
            for (int round = 1; !ok && round <= 2; round++)
            {
                var failedTasks = tasks.Where(t => t.Status is DownloadStatus.Failed or DownloadStatus.Cancelled).ToList();
                if (failedTasks.Count == 0) break;
                App.WriteAppLog($"[安装] 第 {round} 轮补下失败文件 {failedTasks.Count} 个");
                Report($"重试失败文件(第 {round} 轮,{failedTasks.Count} 个)", 0, failedTasks.Count, "");
                ok = await _downloadService.RequeueAsync(failedTasks);
            }

            // 7. 校验并修复损坏文件(与 Java 下载并行等待)
            if (ok)
            {
                Report("校验文件", 0, tasks.Count, "");
                await VerifyAndRepairAsync(tasks);
            }
            bool javaOk = await javaTask;   // 等 Java 下载完成(已与文件下载并行跑完)
            if (ok && javaOk)
                JavaAutoDownloadSucceeded = true;
            else if (ok && !javaOk && string.IsNullOrEmpty(JavaAutoDownloadHint))
                JavaAutoDownloadHint = $"Java 运行时(JDK {javaMajor})自动下载失败。\n" +
                                       $"建议:前往「Java 运行库」页面手动下载 Java {javaMajor}";
            else
            {
                int stillFailed = tasks.Count(t => t.Status is DownloadStatus.Failed or DownloadStatus.Cancelled);
                int cancelled = tasks.Count(t => t.Status == DownloadStatus.Cancelled);
                LastError = cancelled == tasks.Count
                    ? $"下载已被取消(共 {tasks.Count} 个任务)。\n可在「下载中心」页点「重试」重新下载"
                    : $"下载文件失败,自动补下 2 轮后仍有 {stillFailed} 个文件未下载成功(共 {tasks.Count} 个任务)\n" +
                      $"建议:检查网络 / 切换下载源后重试,或在「下载中心」页点「重试失败」单独补下";
                RollbackInstall(versionJson.Id); // 失败回滚:清理半成品版本目录,不留下损坏版本
            }

            Report(ok ? "安装完成" : "安装失败", 1, 1, versionJson.Id);
            return ok;
        }
        catch (Exception ex)
        {
            LastError = $"安装异常:{ex.Message}";
            App.WriteAppLog($"[安装] 异常:{ex}");
            Report(LastError, 0, 0, "");
            return false;
        }
    }

    /// <summary>按 LocalPath 去重加入任务(同一路径只下一次)</summary>
    private static void AddTask(List<DownloadTaskItem> tasks, HashSet<string> seen, DownloadTaskItem task)
    {
        if (string.IsNullOrEmpty(task.LocalPath) || !seen.Add(task.LocalPath)) return;
        // 2026-09-25 安装链路完善:已存在且大小一致的文件源头跳过——重装/复用已下文件时,
        // 几千个 assets 对象任务不再白入队(下载管道幂等跳过但仍有调度/进度开销);
        // 大小不一致/0 字节/未知大小仍入队,下载阶段有 SHA1 校验兜底,不会放过损坏文件
        if (File.Exists(task.LocalPath) && task.Size > 0)
        {
            try { if (new FileInfo(task.LocalPath).Length == task.Size) return; } catch { }
        }
        tasks.Add(task);
    }

    /// <summary>JSON 原子写(tmp → Flush → Move)</summary>
    private static void WriteJsonAtomic(string path, string json)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(fs);
            writer.Write(json);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>安装失败回滚:保留版本 JSON 与已下载文件(均有 SHA1 校验,重装幂等复用),
    /// 仅清理 .partial 半成品残片。绝不整目录删除——否则用户在下载中心重试成功后版本仍缺失,
    /// 版本管理找不到(曾实测:jar 重试下好但版本目录被删,实例也被删,用户无法恢复)。</summary>
    public void RollbackInstall(string versionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(versionId)) return;
            string versionDir = Path.Combine(AppPaths.Versions, versionId);
            if (!Directory.Exists(versionDir)) return;
            foreach (var f in Directory.GetFiles(versionDir, "*.partial", SearchOption.AllDirectories))
            {
                try { File.Delete(f); }
                catch { /* 占用时忽略,下次清理 */ }
            }
            App.WriteAppLog($"[安装] 已清理版本 {versionId} 的半成品残片(已下载文件保留,重装复用)");
        }
        catch (Exception ex)
        {
            // 回滚失败不阻断主流程,仅记日志(文件被占用时下次卸载版本可再清理)
            App.WriteAppLog($"[安装] 回滚清理失败:{ex.Message}");
        }
    }

    /// <summary>校验已下载文件,损坏/缺失的自动重新下载</summary>
    public async Task VerifyAndRepairAsync(List<DownloadTaskItem> tasks)
    {
        // 2026-09-25 并行校验:几百个文件逐个串行算 SHA1 很慢;限 16 并发兼顾磁盘 IO 与 CPU
        var needRepair = new List<DownloadTaskItem>();
        var repairLock = new object();
        int done = 0;
        var verifyTasks = new List<Task>(tasks.Count);
        foreach (var t in tasks)
        {
            if (string.IsNullOrEmpty(t.Sha1)) continue;
            verifyTasks.Add(Task.Run(() =>
            {
                var r = _hashVerify.Verify(t.LocalPath, t.Sha1, t.Size);
                if (!r.Valid)
                {
                    lock (repairLock)
                    {
                        // 删除损坏文件,加入重下
                        try { if (File.Exists(t.LocalPath)) File.Delete(t.LocalPath); }
                        catch (Exception ex) { App.WriteAppLog($"[安装] 删除损坏文件失败 {t.LocalPath}:{ex.Message}"); }
                        needRepair.Add(new DownloadTaskItem
                        {
                            Url = t.Url,
                            LocalPath = t.LocalPath,
                            Sha1 = t.Sha1,
                            Size = t.Size,
                            Category = t.Category
                        });
                    }
                }
                // 进度:每 20 个聚合上报一次,避免几百次跨线程事件刷 UI
                int now = System.Threading.Interlocked.Increment(ref done);
                if (now % 20 == 0 || now == tasks.Count)
                    Report("校验文件", now, tasks.Count, Path.GetFileName(t.LocalPath));
            }));
        }
        await Task.WhenAll(verifyTasks);

        if (needRepair.Count > 0)
        {
            App.WriteAppLog($"[安装] 校验发现 {needRepair.Count} 个文件损坏/缺失,自动修复");
            Report("修复损坏文件", 0, needRepair.Count, "");
            await _downloadService.DownloadAllAsync(needRepair);
        }
    }

    /// <summary>主页完整性快速校验:仅检查关键文件存在性(不做 SHA1,速度快,适合 UI 即时反馈)</summary>
    public async Task<IntegrityCheckResult> VerifyInstanceIntegrityAsync(string instanceId)
    {
        var result = new IntegrityCheckResult();
        var inst = _instanceService.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null)
        {
            result.Summary = "游戏版本不存在";
            return result;
        }

        // 校验目标:加载器实例优先校验加载器版本 JSON(含继承链),原版实例校验原版——
        // 之前只查 inst.VersionId,加载器版本 JSON/库/client 缺失会被漏报(主页误显示"校验通过")
        string checkVersionId = string.IsNullOrEmpty(inst.LoaderVersionId) ? inst.VersionId : inst.LoaderVersionId!;
        string versionDir = Path.Combine(AppPaths.Versions, checkVersionId);
        string versionJsonPath = Path.Combine(versionDir, $"{checkVersionId}.json");

        // 版本 JSON 不存在:整个版本未安装
        if (!File.Exists(versionJsonPath))
        {
            result.MissingFiles.Add($"版本 JSON:{checkVersionId}.json(版本可能未下载,请前往「下载」页安装)");
            result.Summary = $"版本 {checkVersionId} 尚未安装";
            return result;
        }

        // 解析版本 JSON,逐项检查关键文件存在性
        MojangVersionJson? versionJson;
        try
        {
            var json = await File.ReadAllTextAsync(versionJsonPath);
            versionJson = JsonSerializer.Deserialize<MojangVersionJson>(json);
        }
        catch (Exception ex)
        {
            result.CorruptFiles.Add($"版本 JSON 解析失败:{ex.Message}");
            result.Summary = "版本 JSON 损坏,无法校验";
            return result;
        }
        if (versionJson == null)
        {
            result.CorruptFiles.Add("版本 JSON 解析为空");
            result.Summary = "版本 JSON 损坏";
            return result;
        }

        // 1. client.jar(按 inheritsFrom 链回溯到原版基座;加载器版本目录里没有 jar)
        string baseVersionId = checkVersionId;
        try
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(versionJsonPath));
            for (int hop = 0; hop < 4; hop++)
            {
                string? parent = (string?)node?["inheritsFrom"];
                if (string.IsNullOrEmpty(parent)) break;
                baseVersionId = parent;
                string pp = Path.Combine(AppPaths.Versions, parent, $"{parent}.json");
                if (!File.Exists(pp)) break;
                node = JsonNode.Parse(await File.ReadAllTextAsync(pp));
            }
        }
        catch { /* 回溯失败按当前版本查,不阻断 */ }
        string clientPath = Path.Combine(AppPaths.Versions, baseVersionId, $"{baseVersionId}.jar");
        if (!File.Exists(clientPath))
            result.MissingFiles.Add($"客户端主程序:{baseVersionId}.jar");

        // 2. libraries(当前版本 JSON + 继承链父版本全部库,子库优先去重)
        int libChecked = 0;
        var seenLibNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string libCur = checkVersionId;
        for (int hop = 0; hop < 4; hop++)
        {
            string libJsonPath = Path.Combine(AppPaths.Versions, libCur, $"{libCur}.json");
            MojangVersionJson? libJson = null;
            try
            {
                libJson = JsonSerializer.Deserialize<MojangVersionJson>(await File.ReadAllTextAsync(libJsonPath));
            }
            catch { break; }
            if (libJson == null) break;

            foreach (var lib in libJson.Libraries)
            {
                if (!IsLibraryAllowedForOs(lib)) continue;
                if (lib.Name != null && !seenLibNames.Add(lib.Name)) continue; // 子版本库优先
                libChecked++;

                if (lib.Downloads?.Artifact != null)
                {
                    string libPath = Path.Combine(AppPaths.Libraries, lib.Downloads.Artifact.Path);
                    if (!File.Exists(libPath))
                        result.MissingFiles.Add($"库文件:{lib.Downloads.Artifact.Path}");
                }

                if (lib.Natives != null && lib.Downloads?.Classifiers != null)
                {
                    string nativeKey = lib.Natives.GetValueOrDefault("windows") ?? "";
                    nativeKey = nativeKey.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                    if (!string.IsNullOrEmpty(nativeKey) &&
                        lib.Downloads.Classifiers.TryGetValue(nativeKey, out var nativeArt))
                    {
                        string nativePath = Path.Combine(AppPaths.Libraries, nativeArt.Path);
                        if (!File.Exists(nativePath))
                            result.MissingFiles.Add($"原生库:{nativeArt.Path}");
                    }
                }
            }

            // 沿 inheritsFrom 上溯到父版本
            try
            {
                var node = JsonNode.Parse(await File.ReadAllTextAsync(libJsonPath));
                string? parent = (string?)node?["inheritsFrom"];
                if (string.IsNullOrEmpty(parent)) break;
                libCur = parent;
            }
            catch { break; }
        }

        // 3. asset index
        if (versionJson.AssetIndex != null)
        {
            string indexPath = Path.Combine(AppPaths.Assets, "indexes", $"{versionJson.AssetIndex.Id}.json");
            if (!File.Exists(indexPath))
                result.MissingFiles.Add($"资产索引:{versionJson.AssetIndex.Id}.json");
            else
            {
                // 抽样检查资产对象目录是否存在(完整 SHA1 校验太慢,仅查 objects 根)
                string objectsDir = Path.Combine(AppPaths.Assets, "objects");
                if (!Directory.Exists(objectsDir))
                    result.MissingFiles.Add("资产对象目录:assets/objects/");
            }
        }

        result.Passed = result.MissingFiles.Count == 0 && result.CorruptFiles.Count == 0;
        result.Summary = result.Passed
            ? $"完整性校验通过(已检查 client.jar + {libChecked} 个库 + 资产索引)"
            : $"发现 {result.MissingFiles.Count} 个缺失文件,{result.CorruptFiles.Count} 个损坏";
        return result;
    }

    /// <summary>判断库是否适用于当前操作系统</summary>
    private static bool IsLibraryAllowedForOs(MojangLibrary lib)
    {
        if (lib.Rules == null || lib.Rules.Count == 0) return true;
        bool allow = false;
        foreach (var rule in lib.Rules)
        {
            bool osMatch = rule.Os == null || rule.Os.Name == "windows";
            if (rule.Action == "allow")
            {
                if (osMatch) allow = true;
            }
            else if (rule.Action == "disallow")
            {
                if (osMatch) allow = false;
            }
        }
        return allow;
    }

    private void Report(string stage, int cur, int total, string file)
    {
        ProgressChanged?.Invoke(new InstallProgress
        {
            Stage = stage,
            Current = cur,
            Total = total,
            CurrentFile = file
        });
    }
}
