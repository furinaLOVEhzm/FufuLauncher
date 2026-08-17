// GameInstallService.cs — 游戏版本安装服务
// 可爱的芙芙 - 阶段2 模块
//
// 完整安装流程:
// 1. 根据 version URL 拉取版本 JSON
// 2. 下载 client.jar(校验 SHA1)
// 3. 下载 libraries(原生库 + 普通库,过滤操作系统规则)
// 4. 下载 assetIndex 资产索引,再下载全部 asset 对象
// 5. 校验损坏/缺失文件,自动修复
// 6. 安装到指定实例目录

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
                                 JavaRuntimeService javaRuntime)
    {
        _versionManifest = versionManifest;
        _downloadService = downloadService;
        _hashVerify = hashVerify;
        _instanceService = instanceService;
        _javaRuntime = javaRuntime;
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
            // Java runtime 的自动下载在游戏文件就绪后执行(见步骤 7),失败不阻塞安装。
            var inst = _instanceService.Instances.FirstOrDefault(i => i.Id == instanceId);
            if (inst != null)
            {
                inst.JavaMajorVersion = versionJson.JavaVersion?.MajorVersion ?? 17;
                _instanceService.SaveInstance(inst);
            }

            // 保存版本 JSON
            string versionJsonPath = Path.Combine(versionDir, $"{versionJson.Id}.json");
            await File.WriteAllTextAsync(versionJsonPath,
                JsonSerializer.Serialize(versionJson, new JsonSerializerOptions { WriteIndented = true }));

            var tasks = new List<DownloadTaskItem>();

            // 2. client.jar(分类:Game)
            if (versionJson.Downloads?.Client != null)
            {
                var client = versionJson.Downloads.Client;
                string clientPath = Path.Combine(versionDir, $"{versionJson.Id}.jar");
                tasks.Add(new DownloadTaskItem
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
                    string libPath = Path.Combine(AppPaths.Libraries, art.Path);
                    tasks.Add(new DownloadTaskItem
                    {
                        Url = art.Url,
                        LocalPath = libPath,
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
                        string nativePath = Path.Combine(AppPaths.Libraries, nativeArt.Path);
                        tasks.Add(new DownloadTaskItem
                        {
                            Url = nativeArt.Url,
                            LocalPath = nativePath,
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
                tasks.Add(new DownloadTaskItem
                {
                    Url = versionJson.AssetIndex.Url,
                    LocalPath = indexPath,
                    Sha1 = versionJson.AssetIndex.Sha1,
                    Size = versionJson.AssetIndex.Size,
                    Category = DownloadCategory.Asset
                });

                // 先同步下载索引,再解析资源对象
                Report("下载资产索引", 0, 1, versionJson.AssetIndex.Id);
                var indexTask = tasks.Last();
                tasks.RemoveAt(tasks.Count - 1);
                await _downloadService.DownloadAllAsync(new() { indexTask });

                // 解析索引,加入资源下载任务
                if (File.Exists(indexPath))
                {
                    var indexJson = await File.ReadAllTextAsync(indexPath);
                    var index = JsonSerializer.Deserialize<AssetIndexManifest>(indexJson);
                    if (index?.Objects != null)
                    {
                        Report("解析资源对象", 0, index.Objects.Count, "");
                        int assetIdx = 0;
                        foreach (var kv in index.Objects)
                        {
                            assetIdx++;
                            var obj = kv.Value;
                            // 资源路径:assets/objects/{hash前2位}/{完整hash}
                            string subDir = obj.Hash.Substring(0, 2);
                            string assetPath = Path.Combine(AppPaths.Assets, "objects", subDir, obj.Hash);
                            string assetUrl = $"https://resources.download.minecraft.net/{subDir}/{obj.Hash}";
                            // BMCLAPI 也会被 DownloadService 自动替换
                            tasks.Add(new DownloadTaskItem
                            {
                                Url = assetUrl,
                                LocalPath = assetPath,
                                Sha1 = obj.Hash,
                                Size = obj.Size,
                                Category = DownloadCategory.Asset
                            });
                            Report("解析资源对象", assetIdx, index.Objects.Count, kv.Key);
                        }
                    }
                }
            }

            // 5. 执行全部下载
            Report("下载文件", 0, tasks.Count, "");
            bool ok = await _downloadService.DownloadAllAsync(tasks);

            // 6. 校验并修复损坏文件
            if (ok)
            {
                Report("校验文件", 0, tasks.Count, "");
                await VerifyAndRepairAsync(tasks);

                // 7. 自动下载匹配的 Java runtime(失败不阻塞:已下载的游戏文件保留,
                //    仅提示用户前往「Java 运行库」页面手动下载)
                int major = versionJson.JavaVersion?.MajorVersion ?? 17;
                Report($"下载 Java 运行时(JDK {major})", 0, 1, major.ToString());
                try
                {
                    var javaPath = await _javaRuntime.DownloadJdkAsync(major);
                    if (javaPath != null)
                    {
                        JavaAutoDownloadSucceeded = true;
                    }
                    else
                    {
                        JavaAutoDownloadHint = $"Java 运行时(JDK {major})自动下载失败。\n" +
                                               $"建议:前往「Java 运行库」页面手动下载 Java {major}";
                    }
                }
                catch (Exception jex)
                {
                    JavaAutoDownloadHint = $"Java 运行时自动下载异常:{jex.Message}\n" +
                                           $"建议:前往「Java 运行库」页面手动下载 Java {major}";
                }
            }
            else
            {
                LastError = $"下载文件失败,共 {tasks.Count} 个任务未能全部完成\n" +
                            $"建议:检查网络 / 切换下载源后重试";
            }

            Report(ok ? "安装完成" : "安装失败", 1, 1, versionJson.Id);
            return ok;
        }
        catch (Exception ex)
        {
            LastError = $"安装异常:{ex.Message}\n{ex.StackTrace}";
            Report(LastError, 0, 0, "");
            return false;
        }
    }

    /// <summary>校验已下载文件,损坏/缺失的自动重新下载</summary>
    public async Task VerifyAndRepairAsync(List<DownloadTaskItem> tasks)
    {
        var needRepair = new List<DownloadTaskItem>();
        int idx = 0;
        foreach (var t in tasks)
        {
            idx++;
            if (string.IsNullOrEmpty(t.Sha1))
            {
                Report("校验文件", idx, tasks.Count, Path.GetFileName(t.LocalPath));
                continue;
            }
            var r = _hashVerify.Verify(t.LocalPath, t.Sha1, t.Size);
            if (!r.Valid)
            {
                // 删除损坏文件,加入重下
                if (File.Exists(t.LocalPath)) File.Delete(t.LocalPath);
                needRepair.Add(new DownloadTaskItem
                {
                    Url = t.Url,
                    LocalPath = t.LocalPath,
                    Sha1 = t.Sha1,
                    Size = t.Size,
                    Category = t.Category
                });
            }
            Report("校验文件", idx, tasks.Count, Path.GetFileName(t.LocalPath));
        }

        if (needRepair.Count > 0)
        {
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

        string versionDir = Path.Combine(AppPaths.Versions, inst.VersionId);
        string versionJsonPath = Path.Combine(versionDir, $"{inst.VersionId}.json");

        // 版本 JSON 不存在:整个版本未安装
        if (!File.Exists(versionJsonPath))
        {
            result.MissingFiles.Add($"版本 JSON:{inst.VersionId}.json(版本可能未下载,请前往「下载」页安装)");
            result.Summary = $"版本 {inst.VersionId} 尚未安装";
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

        // 1. client.jar
        if (versionJson.Downloads?.Client != null)
        {
            string clientPath = Path.Combine(versionDir, $"{inst.VersionId}.jar");
            if (!File.Exists(clientPath))
                result.MissingFiles.Add($"客户端主程序:{inst.VersionId}.jar");
        }

        // 2. libraries(artifact + natives)
        int libChecked = 0;
        foreach (var lib in versionJson.Libraries)
        {
            if (!IsLibraryAllowedForOs(lib)) continue;
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
