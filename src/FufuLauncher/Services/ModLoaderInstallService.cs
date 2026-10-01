// Copyright © FufuLauncher
//
// 模组加载器安装服务:Forge / Fabric / Quilt / NeoForge / OptiFine 一键安装(安装用户选定的具体版本)。
// 安装 = 生成加载器版本 JSON(写入 versions\{id}\{id}.json,通过 inheritsFrom 继承原版)
//        + 下载加载器全部依赖库到 libraries\,使启动链路可直接拉起加载器环境。
//   Fabric/Quilt:官方 meta 直接提供 profile JSON,下载即用(与主流启动器同源方案);
//   Forge/NeoForge:新版运行官方安装器 --installClient;老牌 Forge(1.12.2 及以下)解包安装器
//                   直接提取 version JSON 与 universal jar,不依赖运行安装器,老环境更稳;
//                   过渡期「中版」(无 install、有 json 字段)按通行方式解包 json+maven 目录;
//   OptiFine:提取安装器内 launchwrapper-of 与本体 jar,合成独立版本 JSON。
// 下载源:国内镜像优先(BMCLAPI),失败自动回退官方源。
// 捕获全部异常,输出普通玩家看得懂的中文提示,不抛出底层英文堆栈。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

/// <summary>加载器安装结果,包含成功标志和中文错误提示</summary>
public class LoaderInstallResult
{
    public bool Success { get; set; }
    /// <summary>中文错误提示(仅失败时有值)</summary>
    public string? ErrorMessage { get; set; }
    /// <summary>安装的加载器版本</summary>
    public string? InstalledVersion { get; set; }
    /// <summary>生成的加载器版本 JSON 的 id(写入实例 LoaderVersionId,启动时使用)</summary>
    public string? LoaderVersionId { get; set; }

    public static LoaderInstallResult Ok(string? version = null, string? loaderVersionId = null)
        => new() { Success = true, InstalledVersion = version, LoaderVersionId = loaderVersionId };
    public static LoaderInstallResult Fail(string msg) => new() { Success = false, ErrorMessage = msg };
}

public class ModLoaderInstallService
{
    private readonly DownloadService _downloadService;
    private readonly InstanceService _instanceService;
    private readonly JavaRuntimeService _javaRuntimeService;
    private readonly HttpClient _http = new();

    private const string BmclapiMaven = "https://bmclapi2.bangbang93.com/maven/";

    public ModLoaderInstallService(DownloadService downloadService,
                                   InstanceService instanceService,
                                   JavaRuntimeService javaRuntimeService)
    {
        _downloadService = downloadService;
        _instanceService = instanceService;
        _javaRuntimeService = javaRuntimeService;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/3.0 (Windows)");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public class FabricLoaderVersion
    {
        [JsonPropertyName("loader")] public LoaderInfo Loader { get; set; } = new();
        [JsonPropertyName("intermediary")] public IntermediaryInfo Intermediary { get; set; } = new();
    }
    public class LoaderInfo
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
    }
    public class IntermediaryInfo
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
    }

    // ==================== Fabric ====================

    public async Task<LoaderInstallResult> InstallFabricAsync(string instanceId, string gameVersion,
                                                              string? loaderVersion = null)
    {
        string? wroteVersionId = null;
        try
        {
            if (string.IsNullOrEmpty(loaderVersion))
                return LoaderInstallResult.Fail("未指定 Fabric 版本,请先选择版本号");

            // 官方 meta 提供现成 profile JSON(inheritsFrom 原版,含全部依赖库清单)
            string profilePath = $"v2/versions/loader/{gameVersion}/{loaderVersion}/profile/json";
            string? profileJson = await FetchTextAsync(
                $"https://bmclapi2.bangbang93.com/fabric-meta/{profilePath}",
                $"https://meta.fabricmc.net/{profilePath}");
            if (profileJson == null)
                return LoaderInstallResult.Fail($"无法获取 Fabric {loaderVersion} 的适配资料,请检查网络后重试");

            var writeRes = WriteProfileVersion(profileJson, "Fabric");
            if (!writeRes.Ok) return LoaderInstallResult.Fail(writeRes.Error);
            wroteVersionId = writeRes.VersionId;

            await DownloadProfileLibrariesAsync(writeRes.ProfileRoot);
            if (!MarkInstanceLoader(instanceId, "Fabric", loaderVersion, writeRes.VersionId))
            {
                CleanupLoaderVersion(writeRes.VersionId);
                return LoaderInstallResult.Fail("Fabric 安装完成但元数据写入失败,已回滚,请重试");
            }
            App.WriteAppLog($"[加载器] Fabric {loaderVersion}({gameVersion})安装完成 → {writeRes.VersionId}");
            return LoaderInstallResult.Ok(loaderVersion, writeRes.VersionId);
        }
        catch (Exception ex)
        {
            CleanupLoaderVersion(wroteVersionId);
            App.WriteAppLog($"[加载器] Fabric 安装异常: {ex.Message}");
            return LoaderInstallResult.Fail($"Fabric 安装失败: {GetFriendlyExceptionMessage(ex)}");
        }
    }

    // ==================== Quilt ====================

    public async Task<LoaderInstallResult> InstallQuiltAsync(string instanceId, string gameVersion,
                                                             string? loaderVersion = null)
    {
        string? wroteVersionId = null;
        try
        {
            if (string.IsNullOrEmpty(loaderVersion))
            {
                // 版本列表镜像优先(与主流启动器同源策略),官方源兜底
                string? json = await FetchTextAsync(
                    "https://bmclapi2.bangbang93.com/quilt-meta/v3/versions/loader",
                    "https://meta.quiltmc.org/v3/versions/loader");
                if (json == null)
                    return LoaderInstallResult.Fail("无法连接 Quilt 源获取版本列表,请检查网络后重试");
                var versions = JsonSerializer.Deserialize<List<FabricLoaderVersion>>(json);
                loaderVersion = versions?.Count > 0 ? versions[0].Loader.Version : "";
                if (string.IsNullOrEmpty(loaderVersion))
                    return LoaderInstallResult.Fail("Quilt 版本列表为空,请稍后重试");
            }

            string profilePath = $"v3/versions/loader/{gameVersion}/{loaderVersion}/profile/json";
            string? profileJson = await FetchTextAsync(
                $"https://bmclapi2.bangbang93.com/quilt-meta/{profilePath}",
                $"https://meta.quiltmc.org/{profilePath}");
            if (profileJson == null)
                return LoaderInstallResult.Fail($"无法获取 Quilt {loaderVersion} 的适配资料,请检查网络后重试");

            var writeRes = WriteProfileVersion(profileJson, "Quilt");
            if (!writeRes.Ok) return LoaderInstallResult.Fail(writeRes.Error);
            wroteVersionId = writeRes.VersionId;

            await DownloadProfileLibrariesAsync(writeRes.ProfileRoot);
            if (!MarkInstanceLoader(instanceId, "Quilt", loaderVersion, writeRes.VersionId))
            {
                CleanupLoaderVersion(writeRes.VersionId);
                return LoaderInstallResult.Fail("Quilt 安装完成但元数据写入失败,已回滚,请重试");
            }
            App.WriteAppLog($"[加载器] Quilt {loaderVersion}({gameVersion})安装完成 → {writeRes.VersionId}");
            return LoaderInstallResult.Ok(loaderVersion, writeRes.VersionId);
        }
        catch (Exception ex)
        {
            CleanupLoaderVersion(wroteVersionId);
            App.WriteAppLog($"[加载器] Quilt 安装异常: {ex.Message}");
            return LoaderInstallResult.Fail($"Quilt 安装失败: {GetFriendlyExceptionMessage(ex)}");
        }
    }

    /// <summary>写入 profile JSON 为加载器版本(versions\{id}\{id}.json)</summary>
    private static (bool Ok, string Error, string VersionId, JsonObject ProfileRoot) WriteProfileVersion(
        string profileJson, string loaderName)
    {
        try
        {
            var root = JsonNode.Parse(profileJson)?.AsObject();
            string id = (string?)root?["id"] ?? "";
            if (string.IsNullOrEmpty(id))
                return (false, $"{loaderName} 适配资料格式异常(缺少版本 id)", "", new JsonObject());

            string dir = Path.Combine(AppPaths.Versions, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{id}.json"), profileJson);
            return (true, "", id, root!);
        }
        catch (Exception ex)
        {
            return (false, $"{loaderName} 适配资料解析失败:{ex.Message}", "", new JsonObject());
        }
    }

    /// <summary>下载 profile JSON libraries 清单中的全部依赖库(镜像优先,已存在跳过)。
    /// 批量并行:第一批全部镜像源入队一次并行下载(DownloadService 内部信号量限流),
    /// 未落盘的个别库再逐个多源回退(官方源兜底),避免几十个库串行逐个下导致的漫长等待。</summary>
    private async Task DownloadProfileLibrariesAsync(JsonObject profileRoot)
    {
        if (profileRoot["libraries"] is not JsonArray libs) return;
        var pending = new List<(string LocalPath, List<string> Urls)>();
        foreach (var lib in libs)
        {
            if (lib is not JsonObject libObj) continue;
            string? name = (string?)libObj["name"];
            if (string.IsNullOrEmpty(name)) continue;
            string mavenPath = NameToMavenPath(name);
            if (string.IsNullOrEmpty(mavenPath)) continue;

            string localPath = Path.Combine(AppPaths.Libraries, mavenPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0) continue;

            string? urlRoot = (string?)libObj["url"];
            pending.Add((localPath, LibraryUrlCandidates(urlRoot, mavenPath)));
        }
        if (pending.Count == 0) return;

        // 第一批:镜像源(候选首位=BMCLAPI)批量并行
        var batch = pending
            .Select(p => new DownloadTaskItem { Url = p.Urls[0], LocalPath = p.LocalPath, Category = DownloadCategory.Other })
            .ToList();
        await _downloadService.DownloadAllAsync(batch);

        // 第二批:仍缺失的库逐个多源回退(官方源兜底;失败不阻断安装,启动前会再校验)
        foreach (var (localPath, urls) in pending)
        {
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0) continue;
            if (!await DownloadFromMirrorsAsync(urls, localPath))
                App.WriteAppLog($"[加载器] 依赖库下载失败(不阻断安装,启动前会再校验):{Path.GetFileName(localPath)}");
        }
    }

    // ==================== Forge ====================

    public async Task<LoaderInstallResult> InstallForgeAsync(string instanceId, string gameVersion,
                                                             string forgeVersion)
    {
        string? wroteVersionId = null;
        try
        {
            if (string.IsNullOrWhiteSpace(forgeVersion))
                return LoaderInstallResult.Fail("未指定 Forge 版本,请先选择版本号");

            // 国内镜像优先(BMCLAPI),回退 Forge 官方 maven。
            // 2026-09-25 修复:老 Forge(≤1.7.10)的 maven 工件是双版本号 {mc}-{ver}-{mc}
            // (如 1.7.10-10.13.4.1614-1.7.10),新 Forge(1.8+)是单版本号 {mc}-{ver}
            // (如 1.21.1-52.0.33)。旧实现把新 Forge 也拼成双版本号 → 必然 404。
            var mirrors = new[]
            {
                "https://bmclapi2.bangbang93.com/maven",
                "https://maven.minecraftforge.net",
                "https://files.minecraftforge.net/maven"
            };
            // 2026-09-26:移除 download.mcbbs.net —— 该域名已停止解析(实测 "不知道这样的主机"),
            // 留在列表里只会让每次安装白等 4 次重试才换源。
            // 2026-09-25 双保险:老 Forge 版本号若已带 "-{mc}" 尾缀(个别源直接给完整工件版本)不再重拼,
            // 否则双重后缀必然 404(实测官方 metadata 1.7.10 返回 10.13.4.1614-1.7.10)
            string artifactSuffix = IsLegacyForgeArtifact(gameVersion)
                ? (forgeVersion.EndsWith("-" + gameVersion, StringComparison.Ordinal) ? forgeVersion : $"{forgeVersion}-{gameVersion}")
                : forgeVersion;
            var urls = new List<string>();
            foreach (var m in mirrors)
                urls.Add($"{m}/net/minecraftforge/forge/{gameVersion}-{artifactSuffix}/forge-{gameVersion}-{artifactSuffix}-installer.jar");
            string installersDir = AppPaths.Installers;
            Directory.CreateDirectory(installersDir);
            string installerPath = Path.Combine(installersDir, $"forge-installer-{gameVersion}-{forgeVersion}.jar");

            if (!await DownloadFromMirrorsAsync(urls, installerPath))
                return LoaderInstallResult.Fail($"Forge 安装器({forgeVersion})下载失败,请检查 {gameVersion} 版本是否有对应的 Forge 版本");

            var res = await InstallFromForgeStyleInstallerAsync(installerPath, gameVersion, "Forge");
            if (!res.Success) return res;
            wroteVersionId = res.LoaderVersionId;

            if (!MarkInstanceLoader(instanceId, "Forge", forgeVersion, res.LoaderVersionId))
            {
                CleanupLoaderVersion(res.LoaderVersionId);
                return LoaderInstallResult.Fail("Forge 安装完成但元数据写入失败,已回滚,请重试");
            }
            App.WriteAppLog($"[加载器] Forge {forgeVersion}({gameVersion})安装完成 → {res.LoaderVersionId}");
            return res;
        }
        catch (Exception ex)
        {
            CleanupLoaderVersion(wroteVersionId);
            App.WriteAppLog($"[加载器] Forge 安装异常: {ex.Message}");
            return LoaderInstallResult.Fail($"Forge 安装失败: {GetFriendlyExceptionMessage(ex)}");
        }
    }

    // ==================== NeoForge ====================

    public async Task<LoaderInstallResult> InstallNeoForgeAsync(string instanceId, string gameVersion,
                                                                string? loaderVersion = null)
    {
        string? wroteVersionId = null;
        try
        {
            if (string.IsNullOrWhiteSpace(loaderVersion))
                return LoaderInstallResult.Fail("未指定 NeoForge 版本,请先选择版本号");

            // MC 1.20.1 的 NeoForge 发布在 net/neoforged/forge 工件下(版本号形如 1.20.1-47.1.x)
            string pathSeg, fileName;
            if (loaderVersion.StartsWith("1.20.1-", StringComparison.Ordinal))
            {
                pathSeg = $"net/neoforged/forge/{loaderVersion}";
                fileName = $"forge-{loaderVersion}-installer.jar";
            }
            else
            {
                pathSeg = $"net/neoforged/neoforge/{loaderVersion}";
                fileName = $"neoforge-{loaderVersion}-installer.jar";
            }
            // 国内镜像优先(BMCLAPI),回退 NeoForge 官方 maven。
            // 2026-09-26:移除已停止解析的 download.mcbbs.net(白等重试后才换源)
            var urls = new List<string>
            {
                $"https://bmclapi2.bangbang93.com/maven/{pathSeg}/{fileName}",
                $"https://maven.neoforged.net/releases/{pathSeg}/{fileName}"
            };
            string installersDir = AppPaths.Installers;
            Directory.CreateDirectory(installersDir);
            string installerPath = Path.Combine(installersDir, $"neoforge-installer-{loaderVersion}.jar");

            if (!await DownloadFromMirrorsAsync(urls, installerPath))
                return LoaderInstallResult.Fail($"NeoForge 安装器({loaderVersion})下载失败,请稍后重试");

            var res = await InstallFromForgeStyleInstallerAsync(installerPath, gameVersion, "NeoForge");
            if (!res.Success) return res;
            wroteVersionId = res.LoaderVersionId;

            if (!MarkInstanceLoader(instanceId, "NeoForge", loaderVersion, res.LoaderVersionId))
            {
                CleanupLoaderVersion(res.LoaderVersionId);
                return LoaderInstallResult.Fail("NeoForge 安装完成但元数据写入失败,已回滚,请重试");
            }
            App.WriteAppLog($"[加载器] NeoForge {loaderVersion}({gameVersion})安装完成 → {res.LoaderVersionId}");
            return res;
        }
        catch (Exception ex)
        {
            CleanupLoaderVersion(wroteVersionId);
            App.WriteAppLog($"[加载器] NeoForge 安装异常: {ex.Message}");
            return LoaderInstallResult.Fail($"NeoForge 安装失败: {GetFriendlyExceptionMessage(ex)}");
        }
    }

    /// <summary>Forge 系安装器统一处理:新版运行官方安装器 --install;老牌(无 processors)解包提取,不跑 Java 更稳</summary>
    private async Task<LoaderInstallResult> InstallFromForgeStyleInstallerAsync(
        string installerPath, string gameVersion, string loaderName)
    {
        JsonObject profile;
        try
        {
            using var zip = ZipFile.OpenRead(installerPath);
            var entry = zip.GetEntry("install_profile.json")
                ?? throw new FileNotFoundException("安装器缺少 install_profile.json");
            using var sr = new StreamReader(entry.Open());
            profile = JsonNode.Parse(sr.ReadToEnd())?.AsObject()
                ?? throw new InvalidDataException("install_profile.json 解析失败");
        }
        catch (Exception ex)
        {
            return LoaderInstallResult.Fail($"{loaderName} 安装器文件损坏:{ex.Message}");
        }

        bool hasProcessors = profile["processors"] is JsonArray procs && procs.Count > 0;
        if (!hasProcessors)
        {
            // 旧版(有 install 字段):提取 versionJson + universal jar
            if (profile["install"] is JsonObject legacyInstall)
                return await InstallLegacyForgeAsync(installerPath, legacyInstall, loaderName);
            // 中版(无 install、有 json 字段,过渡期安装器):解包版本 JSON + maven 目录(通行解包方式)
            if (profile["json"] is JsonValue jsonField && jsonField.GetValueKind() == JsonValueKind.String)
                return await InstallMidForgeAsync(installerPath, ((string?)jsonField) ?? "", loaderName);
        }
        return await RunForgeInstallerProcessAsync(installerPath, profile, loaderName, gameVersion);
    }

    /// <summary>老牌 Forge(1.12.2 及以下):解包安装器提取 version JSON + universal jar,补下载依赖库。
    /// 兼容两种形态:① install.versionJson 指向包内独立 JSON 文件(较新);
    /// ② 版本 JSON 内嵌在 install_profile.json 的 versionInfo 节点(1.7.10 等最早期安装器)</summary>
    private async Task<LoaderInstallResult> InstallLegacyForgeAsync(
        string installerPath, JsonObject installNode, string loaderName)
    {
        string versionJsonEntry = (string?)installNode["versionJson"] ?? "";
        string universalEntry = (string?)installNode["filePath"] ?? "";
        string universalName = (string?)installNode["path"] ?? "";

        string versionId;
        string versionJsonText;
        try
        {
            using var zip = ZipFile.OpenRead(installerPath);

            if (!string.IsNullOrEmpty(versionJsonEntry))
            {
                var jsonEntry = zip.GetEntry(versionJsonEntry)
                    ?? throw new FileNotFoundException($"安装器缺少 {versionJsonEntry}");
                using var sr = new StreamReader(jsonEntry.Open());
                versionJsonText = sr.ReadToEnd();
            }
            else
            {
                // 最早期安装器:版本 JSON 直接内嵌在 install_profile.json 的 versionInfo 节点
                var pe = zip.GetEntry("install_profile.json")
                    ?? throw new FileNotFoundException("安装器缺少 install_profile.json");
                JsonObject? profileRoot;
                using (var sr2 = new StreamReader(pe.Open()))
                    profileRoot = JsonNode.Parse(sr2.ReadToEnd())?.AsObject();
                var embedded = profileRoot?["versionInfo"] ?? installNode["versionInfo"];
                if (embedded is not JsonObject embeddedObj)
                    return LoaderInstallResult.Fail($"{loaderName} 安装器缺少版本文件(versionJson/versionInfo 均无),该版本可能不受支持");
                versionJsonText = embeddedObj.ToJsonString();
            }
            versionId = (string?)JsonNode.Parse(versionJsonText)?["id"] ?? "";
            if (string.IsNullOrEmpty(versionId))
                return LoaderInstallResult.Fail($"{loaderName} 版本文件格式异常(缺少 id)");
            // id 来自安装器内嵌 JSON,校验防路径逃逸(恶意安装器可构造 '../' 写任意位置)
            if (SanitizeExternalId(versionId) == null)
                return LoaderInstallResult.Fail($"{loaderName} 版本文件 id 非法(含路径字符),已拒绝:{versionId}");

            // 写入 versions\{id}\{id}.json
            string dir = Path.Combine(AppPaths.Versions, versionId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{versionId}.json"), versionJsonText);

            // universal jar:直接解包到 libraries 对应 maven 路径(省去二次下载)
            if (!string.IsNullOrEmpty(universalEntry) && !string.IsNullOrEmpty(universalName))
            {
                string universalRelPath = NameToMavenPath(universalName);
                if (!string.IsNullOrEmpty(universalRelPath))
                {
                    var uEntry = zip.GetEntry(universalEntry);
                    if (uEntry != null)
                    {
                        // maven 坐标同样来自安装器,目标路径断言在 libraries 之内,越界拒绝解包
                        string? dst = SafeCombineInside(AppPaths.Libraries, universalRelPath.Replace('/', Path.DirectorySeparatorChar));
                        if (dst == null)
                        {
                            App.WriteAppLog($"[加载器] 安装器声明的 universal 路径越出 libraries,已拦截:{universalRelPath}");
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                            uEntry.ExtractToFile(dst, overwrite: true);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return LoaderInstallResult.Fail($"{loaderName} 安装器解包失败:{ex.Message}");
        }

        // 补下载 version JSON 声明的其余依赖库(已存在的自动跳过)
        try
        {
            var root = JsonNode.Parse(versionJsonText)!.AsObject();
            await DownloadLegacyForgeLibrariesAsync(root);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] {loaderName} 依赖库补下载异常(不阻断):{ex.Message}");
        }

        return LoaderInstallResult.Ok(versionId, versionId);
    }

    /// <summary>过渡期「中版」Forge 安装器(无 install 字段、json 字段指向包内版本 JSON):
    /// 解包版本 JSON 写入 versions\,并把安装器内 maven\ 目录整体落到 libraries\(与主流启动器的老版安装方式一致),
    /// 不跑安装器进程,省去其联网下载</summary>
    private async Task<LoaderInstallResult> InstallMidForgeAsync(
        string installerPath, string jsonEntryName, string loaderName)
    {
        string versionJsonText;
        string versionId;
        try
        {
            using var zip = ZipFile.OpenRead(installerPath);

            // json 字段形如 "/version.json",去掉开头斜杠
            var jsonEntry = zip.GetEntry(jsonEntryName.TrimStart('/'))
                ?? throw new FileNotFoundException($"安装器缺少 {jsonEntryName}");
            using (var sr = new StreamReader(jsonEntry.Open()))
                versionJsonText = sr.ReadToEnd();
            versionId = (string?)JsonNode.Parse(versionJsonText)?["id"] ?? "";
            if (string.IsNullOrEmpty(versionId))
                return LoaderInstallResult.Fail($"{loaderName} 版本文件格式异常(缺少 id)");
            // id 来自安装器内嵌 JSON,校验防路径逃逸(恶意安装器可构造 '../' 写任意位置)
            if (SanitizeExternalId(versionId) == null)
                return LoaderInstallResult.Fail($"{loaderName} 版本文件 id 非法(含路径字符),已拒绝:{versionId}");

            // 写入 versions\{id}\{id}.json
            string dir = Path.Combine(AppPaths.Versions, versionId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{versionId}.json"), versionJsonText);

            // maven\ 整目录解包到 libraries\(已存在非空文件跳过,避免覆盖坏档以外的好文件)
            int extracted = 0;
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith("maven/", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(e.Name)) continue; // 目录项
                string rel = e.FullName.Substring("maven/".Length);
                // 条目名完全受控于安装包:断言目标在 libraries 之内,拦截 '../../' 逃逸条目(防 ZipSlip)
                string? dst = SafeCombineInside(AppPaths.Libraries, rel.Replace('/', Path.DirectorySeparatorChar));
                if (dst == null)
                {
                    App.WriteAppLog($"[加载器] 安装器条目越出 libraries,已拦截:{e.FullName}");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!File.Exists(dst) || new FileInfo(dst).Length == 0)
                {
                    e.ExtractToFile(dst, overwrite: true);
                    extracted++;
                }
            }
            App.WriteAppLog($"[加载器] {loaderName} 中版解包完成:版本 JSON={versionId},maven 落库 {extracted} 个文件");
        }
        catch (Exception ex)
        {
            return LoaderInstallResult.Fail($"{loaderName} 安装器解包失败:{ex.Message}");
        }

        // 补下载版本 JSON 声明的其余依赖库(已存在的自动跳过)
        try
        {
            var root = JsonNode.Parse(versionJsonText)!.AsObject();
            await DownloadLegacyForgeLibrariesAsync(root);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] {loaderName} 依赖库补下载异常(不阻断):{ex.Message}");
        }

        return LoaderInstallResult.Ok(versionId, versionId);
    }

    /// <summary>老牌 Forge version JSON 依赖库下载:镜像优先;url 缺省按 Mojang 库/Forge 官方 maven 处理</summary>
    private async Task DownloadLegacyForgeLibrariesAsync(JsonObject versionRoot)
    {
        if (versionRoot["libraries"] is not JsonArray libs) return;
        var pending = new List<(string LocalPath, List<string> Urls)>();
        foreach (var lib in libs)
        {
            if (lib is not JsonObject libObj) continue;
            string? name = (string?)libObj["name"];
            if (string.IsNullOrEmpty(name)) continue;
            string mavenPath = NameToMavenPath(name);
            if (string.IsNullOrEmpty(mavenPath)) continue;

            string localPath = Path.Combine(AppPaths.Libraries, mavenPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0) continue;

            // 老牌 json 的 url 多为 http://files.minecraftforge.net/maven/;无 url 的走 Mojang 官方库
            string? urlRoot = (string?)libObj["url"];
            if (string.IsNullOrEmpty(urlRoot))
                urlRoot = name.StartsWith("net.minecraftforge", StringComparison.OrdinalIgnoreCase)
                    ? "https://files.minecraftforge.net/maven/"
                    : "https://libraries.minecraft.net/";
            pending.Add((localPath, LibraryUrlCandidates(urlRoot, mavenPath)));
        }
        if (pending.Count == 0) return;

        // 第一批:镜像源(候选首位=BMCLAPI)批量并行
        var batch = pending
            .Select(p => new DownloadTaskItem { Url = p.Urls[0], LocalPath = p.LocalPath, Category = DownloadCategory.Other })
            .ToList();
        await _downloadService.DownloadAllAsync(batch);

        // 第二批:仍缺失的库逐个多源回退(官方源兜底;失败不阻断安装)
        foreach (var (localPath, urls) in pending)
        {
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0) continue;
            if (!await DownloadFromMirrorsAsync(urls, localPath))
                App.WriteAppLog($"[加载器] 依赖库下载失败(不阻断安装):{Path.GetFileName(localPath)}");
        }
    }

    /// <summary>新版 Forge/NeoForge:运行官方安装器(与主流启动器同参:Forge 用 --installClient,
    /// NeoForge 用 --install;安装器自行下载依赖并执行 processors)。
    /// Java 必须按游戏版本选(1.16.5 等老安装器在 Java 21 上会在参数解析/SSL 阶段直接崩)</summary>
    private async Task<LoaderInstallResult> RunForgeInstallerProcessAsync(
        string installerPath, JsonObject profile, string loaderName, string gameVersion)
    {
        string versionId = (string?)profile["version"] ?? "";
        if (string.IsNullOrEmpty(versionId))
            return LoaderInstallResult.Fail($"{loaderName} 安装器未声明目标版本 id,无法继续");

        string? javaExe = FindInstallerJava(gameVersion);
        if (string.IsNullOrEmpty(javaExe))
            return LoaderInstallResult.Fail("本机没有可用的 Java 运行时,请先前往【☕ Java 运行时】安装 Java 后重试");

        string mcRoot = AppPaths.Root;
        // Forge 安装器只认 --installClient(传 --install 会在 joptsimple 参数解析直接报错退出);
        // NeoForge 安装器则是 --install
        string installFlag = loaderName.Equals("NeoForge", StringComparison.OrdinalIgnoreCase) ? "--install" : "--installClient";
        App.WriteAppLog($"[加载器] 运行 {loaderName} 官方安装器:java={javaExe} 参数={installFlag} {mcRoot}");

        var output = new StringBuilder();
        // Forge 安装器 --installClient 强制要求目标目录存在 launcher_profiles.json,
        // 缺失即报 "There is no minecraft launcher profile" 退出(主流启动器同款方案:临时占位)
        string profilesJson = Path.Combine(mcRoot, "launcher_profiles.json");
        bool profilesCreated = false;
        if (!File.Exists(profilesJson))
        {
            try
            {
                File.WriteAllText(profilesJson,
                    "{\"profiles\":{},\"clientToken\":\"00000000-0000-0000-0000-000000000000\"}");
                profilesCreated = true;
            }
            catch (Exception pfEx)
            {
                return LoaderInstallResult.Fail($"{loaderName} 安装准备失败(无法创建 launcher_profiles.json):{pfEx.Message}");
            }
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = mcRoot
            };
            psi.ArgumentList.Add("-jar");
            psi.ArgumentList.Add(installerPath);
            psi.ArgumentList.Add(installFlag);
            psi.ArgumentList.Add(mcRoot);

            // 2026-09-25 兜底:安装器失败多为下载依赖网络抖动,最多自动重试 2 次(首次失败等 5 秒重跑)
            bool installOk = false;
            for (int attempt = 1; attempt <= 2 && !installOk; attempt++)
            {
                if (attempt > 1)
                {
                    App.WriteAppLog($"[加载器] {loaderName} 安装器首次运行失败,5 秒后自动重试…");
                    await Task.Delay(5000);
                }
                using var proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) { if (output.Length < 8000) output.AppendLine(e.Data); } };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) { if (output.Length < 8000) output.AppendLine(e.Data); } };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                try
                {
                    await proc.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                    return LoaderInstallResult.Fail($"{loaderName} 安装器运行超时(15 分钟),多为下载依赖过慢,请检查网络后重试");
                }

                string logTail;
                lock (output) logTail = output.ToString();
                App.WriteAppLog($"[加载器] {loaderName} 安装器退出码 {proc.ExitCode}");
                if (!string.IsNullOrWhiteSpace(logTail))
                    App.WriteAppLog($"[加载器] 安装器输出(尾部):\n{(logTail.Length > 3000 ? logTail[^3000..] : logTail)}");

                if (proc.ExitCode == 0)
                    installOk = true;
                else if (attempt == 2)
                    return LoaderInstallResult.Fail($"{loaderName} 安装器执行失败(退出码 {proc.ExitCode}),请检查网络后重试");
            }
            if (!installOk)
                return LoaderInstallResult.Fail($"{loaderName} 安装器执行失败,请检查网络后重试");
        }
        catch (Exception ex)
        {
            return LoaderInstallResult.Fail($"{loaderName} 安装器运行异常:{ex.Message}");
        }
        finally
        {
            // 占位文件由本次创建才清理,用户/其他启动器自带的不动
            if (profilesCreated) { try { File.Delete(profilesJson); } catch { } }
        }

        // 复核产物:版本 JSON 必须生成,否则判定失败
        string jsonPath = Path.Combine(AppPaths.Versions, versionId, $"{versionId}.json");
        if (!File.Exists(jsonPath))
            return LoaderInstallResult.Fail($"{loaderName} 安装器已运行但未生成版本文件,请检查网络后重试(安装器日志见 日志\\app.log)");

        return LoaderInstallResult.Ok(versionId, versionId);
    }

    /// <summary>挑选运行安装器的 Java:优先游戏版本对应的 Java(与主流启动器策略一致,
    /// 1.16.5 等老安装器在 Java 17+ 上会直接崩),其次就近低版本,最后回退任一已就绪 Java</summary>
    private string? FindInstallerJava(string gameVersion)
    {
        try
        {
            var list = _javaRuntimeService.ListInstalledRuntimes()
                .Where(r => r.Status == "已就绪" && !string.IsNullOrEmpty(r.JavaExe) && File.Exists(r.JavaExe))
                .ToList();
            if (list.Count == 0) return null;

            int target = JavaRuntimeService.RecommendJavaMajor(gameVersion);
            // 1) 精准命中目标主版本
            var exact = list.FirstOrDefault(r => ParseMajor(r.MajorVersion) == target);
            if (exact != null)
            {
                App.WriteAppLog($"[加载器] 安装器 Java 选用 Java {target}(与游戏版本 {gameVersion} 匹配)");
                return exact.JavaExe;
            }
            // 2) 目标版本未安装:优先选低于目标的最高版本(老安装器宁低勿高),
            //    若只有更高版本也允许尝试(新安装器兼容高版本 Java)
            var lower = list.Where(r => ParseMajor(r.MajorVersion) < target)
                            .OrderByDescending(r => ParseMajor(r.MajorVersion)).FirstOrDefault();
            if (lower != null)
            {
                App.WriteAppLog($"[加载器] Java {target} 未安装,安装器改用 Java {ParseMajor(lower.MajorVersion)}");
                return lower.JavaExe;
            }
            var fallback = list.OrderByDescending(r => ParseMajor(r.MajorVersion)).First();
            App.WriteAppLog($"[加载器] 安装器 Java 回退 Java {ParseMajor(fallback.MajorVersion)}(未找到 ≤{target} 的版本)");
            return fallback.JavaExe;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] 查找可用 Java 失败:{ex.Message}");
            return null;
        }
    }

    private static int ParseMajor(string majorVersionLabel)
    {
        var seg = (majorVersionLabel ?? "").Replace("Java", "").Trim();
        return int.TryParse(seg.Split(' ')[0], out int v) ? v : 0;
    }

    // ==================== OptiFine ====================

    public class OptiFineVersion
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("patch")] public string Patch { get; set; } = "";
        [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    }

    public async Task<LoaderInstallResult> InstallOptiFineAsync(string instanceId, string gameVersion,
                                                                string? loaderVersion = null)
    {
        string? wroteVersionId = null;
        try
        {
            // OptiFine 版本标签格式为 "type_patch"(如 HD_U_I7);未指定时取镜像列表首个
            string type, patch;
            if (!string.IsNullOrWhiteSpace(loaderVersion) && loaderVersion.Contains('_'))
            {
                int idx = loaderVersion.IndexOf('_');
                type = loaderVersion[..idx];
                patch = loaderVersion[(idx + 1)..];
            }
            else
            {
                try
                {
                    // 2026-09-30:OptiFine 官方无公开 API,版本列表只能从镜像取;
                    // 走 FetchTextAsync 是为了让自有镜像排在 BMCLAPI 前面(自建源未同步时自动回落)
                    string? ofJson = await FetchTextAsync(
                        $"https://bmclapi2.bangbang93.com/optifine/{gameVersion}");
                    if (string.IsNullOrEmpty(ofJson))
                        return LoaderInstallResult.Fail("无法连接 OptiFine 镜像源获取版本列表,请检查网络后重试");
                    var list = JsonSerializer.Deserialize<List<OptiFineVersion>>(ofJson);
                    if (list == null || list.Count == 0)
                        return LoaderInstallResult.Fail($"{gameVersion} 暂无可用的 OptiFine 版本,该游戏版本可能尚未适配");
                    type = list[0].Type;
                    patch = list[0].Patch;
                }
                catch (TaskCanceledException)
                {
                    return LoaderInstallResult.Fail("连接 OptiFine 镜像源超时,请检查网络后重试");
                }
            }

            string verLabel = $"{type}_{patch}";
            string installerUrl = $"https://bmclapi2.bangbang93.com/optifine/{gameVersion}/{type}/{patch}";
            string installersDir = AppPaths.Installers;
            Directory.CreateDirectory(installersDir);
            string installerPath = Path.Combine(installersDir, $"optifine-{gameVersion}-{verLabel}.jar");

            if (!await DownloadFromMirrorsAsync(new List<string> { installerUrl }, installerPath))
                return LoaderInstallResult.Fail($"OptiFine({verLabel})下载失败,请稍后重试");

            // 从安装器提取 launchwrapper-of;安装器本体即 OptiFine 加载 jar
            string lwRelPath;
            try
            {
                using var zip = ZipFile.OpenRead(installerPath);
                var lwEntry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.StartsWith("maven/launchwrapper-of/", StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
                if (lwEntry == null)
                    return LoaderInstallResult.Fail($"该版本 OptiFine({verLabel})暂不支持一键安装,建议改用 Forge 方式集成");

                lwRelPath = lwEntry.FullName.Replace('\\', '/');
                // 与其余解压点同款 ZipSlip 防护:条目名完全受控于安装包,但纵深防御必须一致
                string? lwDst = SafeCombineInside(AppPaths.Libraries, lwRelPath.Replace('/', Path.DirectorySeparatorChar));
                if (lwDst == null)
                    return LoaderInstallResult.Fail($"OptiFine 安装器条目越出 libraries,已拦截:{lwRelPath}");
                Directory.CreateDirectory(Path.GetDirectoryName(lwDst)!);
                lwEntry.ExtractToFile(lwDst, overwrite: true);
            }
            catch (Exception ex)
            {
                return LoaderInstallResult.Fail($"OptiFine 安装器解包失败:{ex.Message}");
            }

            // OptiFine 本体 jar 落 libraries 规范路径(供版本 JSON 引用)
            string ofRelPath = $"optifine/OptiFine/{gameVersion}_{verLabel}/OptiFine-{gameVersion}_{verLabel}.jar";
            string ofDst = Path.Combine(AppPaths.Libraries, ofRelPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(ofDst)!);
            File.Copy(installerPath, ofDst, overwrite: true);

            // 合成独立版本 JSON:inheritsFrom 原版 + launchwrapper 主类 + tweak 参数
            string newId = $"{gameVersion}-optifine-{verLabel}";
            var root = new JsonObject
            {
                ["id"] = newId,
                ["inheritsFrom"] = gameVersion,
                ["type"] = "release",
                ["mainClass"] = "net.minecraft.launchwrapper.Launch",
                ["arguments"] = new JsonObject
                {
                    ["game"] = new JsonArray("--tweakClass", "optifine.OptiFineTweaker")
                },
                ["libraries"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = $"optifine:OptiFine:{gameVersion}_{verLabel}",
                        ["downloads"] = new JsonObject { ["artifact"] = new JsonObject { ["path"] = ofRelPath } }
                    },
                    new JsonObject
                    {
                        ["name"] = $"optifine:launchwrapper-of:{Path.GetFileNameWithoutExtension(Path.GetFileName(lwRelPath)).Replace("launchwrapper-of-", "")}",
                        ["downloads"] = new JsonObject { ["artifact"] = new JsonObject { ["path"] = lwRelPath } }
                    })
            };

            string dir = Path.Combine(AppPaths.Versions, newId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{newId}.json"),
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            wroteVersionId = newId;

            if (!MarkInstanceLoader(instanceId, "OptiFine", verLabel, newId))
            {
                CleanupLoaderVersion(newId);
                return LoaderInstallResult.Fail("OptiFine 安装完成但元数据写入失败,已回滚,请重试");
            }
            App.WriteAppLog($"[加载器] OptiFine {verLabel}({gameVersion})安装完成 → {newId}");
            return LoaderInstallResult.Ok(verLabel, newId);
        }
        catch (Exception ex)
        {
            CleanupLoaderVersion(wroteVersionId);
            App.WriteAppLog($"[加载器] OptiFine 安装异常: {ex.Message}");
            return LoaderInstallResult.Fail($"OptiFine 安装失败: {GetFriendlyExceptionMessage(ex)}");
        }
    }

    // ==================== 统一入口 ====================

    /// <summary>按加载器类型统一调度安装,返回中文结果。
    /// 版本号为空时自动从官方/镜像源拉取该 MC 版本的最新适配版本——
    /// 目录导入的整合包、自动补装等场景不再因"未指定版本"卡死。</summary>
    public async Task<LoaderInstallResult> InstallLoaderAsync(string instanceId, string gameVersion,
                                                              string kind, string? loaderVersion = null)
    {
        string k = (kind ?? "").Trim().ToLowerInvariant();
        // OptiFine 空版本内部有镜像列表自动逻辑,不走统一自动选取
        if (k != "optifine" && string.IsNullOrWhiteSpace(loaderVersion))
        {
            loaderVersion = await AutoPickLoaderVersionAsync(k, gameVersion);
            if (string.IsNullOrWhiteSpace(loaderVersion))
                return LoaderInstallResult.Fail($"未指定 {kind} 版本,且自动获取最新版本失败。" +
                                                 "请检查网络后在「新装版本」向导中选择版本号重试");
        }
        return k switch
        {
            "fabric" => await InstallFabricAsync(instanceId, gameVersion, loaderVersion),
            "forge" => await InstallForgeAsync(instanceId, gameVersion, loaderVersion!),
            "quilt" => await InstallQuiltAsync(instanceId, gameVersion, loaderVersion),
            "neoforge" => await InstallNeoForgeAsync(instanceId, gameVersion, loaderVersion),
            "optifine" => await InstallOptiFineAsync(instanceId, gameVersion, loaderVersion),
            _ => LoaderInstallResult.Fail($"不支持的加载器类型: {kind}")
        };
    }

    /// <summary>加载器版本号为空时,从官方源(镜像兜底)自动拉取该 MC 版本的最新适配版本。
    /// 失败返回 null(调用方给出明确提示,绝不猜测乱装);版本 id 一律过 SanitizeExternalId 防目录逃逸。</summary>
    private async Task<string?> AutoPickLoaderVersionAsync(string kind, string gameVersion)
    {
        try
        {
            switch (kind)
            {
                case "fabric":
                {
                    // meta.fabricmc.net 按日期降序,首个即最新
                    var text = await FetchTextAsync(
                        $"https://meta.fabricmc.net/v2/versions/loader/{gameVersion}",
                        $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{gameVersion}");
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                        return null;
                    string? ver = null;
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("loader", out var ld) &&
                            ld.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                        { ver = v.GetString(); break; }
                    }
                    return SanitizeExternalId(ver);
                }
                case "quilt":
                {
                    var text = await FetchTextAsync(
                        $"https://meta.quiltmc.org/v3/versions/loader/{gameVersion}",
                        $"https://bmclapi2.bangbang93.com/quilt-meta/v3/versions/loader/{gameVersion}");
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                        return null;
                    string? ver = null;
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("loader", out var ld) &&
                            ld.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                        { ver = v.GetString(); break; }
                    }
                    return SanitizeExternalId(ver);
                }
                case "forge":
                {
                    // promotions_slim.json:{ "promos": { "{mc}-latest": "47.2.0", "{mc}-recommended": "47.2.0", ... } }
                    var text = await FetchTextAsync(
                        "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json",
                        "https://bmclapi2.bangbang93.com/minecraft/version_manifest/forge/promotions_slim.json");
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    using var doc = JsonDocument.Parse(text);
                    if (!doc.RootElement.TryGetProperty("promos", out var promos) ||
                        promos.ValueKind != JsonValueKind.Object) return null;
                    string? recommended = null, latest = null;
                    foreach (var prop in promos.EnumerateObject())
                    {
                        if (prop.Name == $"{gameVersion}-recommended" && prop.Value.ValueKind == JsonValueKind.String)
                            recommended = prop.Value.GetString();
                        if (prop.Name == $"{gameVersion}-latest" && prop.Value.ValueKind == JsonValueKind.String)
                            latest = prop.Value.GetString();
                    }
                    return SanitizeExternalId(recommended ?? latest);
                }
                case "neoforge":
                {
                    // MC 1.20.1 的 NeoForge 发布在 net/neoforged/forge 工件下,其余在 neoforge
                    string seg = gameVersion == "1.20.1" ? "forge" : "neoforge";
                    var text = await FetchTextAsync(
                        $"https://maven.neoforged.net/releases/net/neoforged/{seg}/maven-metadata.xml");
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    // <version>1.20.1-47.1.0</version> 列表升序,过滤 MC 前缀取最后
                    string? picked = null;
                    foreach (var m in System.Text.RegularExpressions.Regex.Matches(
                        text, @"<version>([^<]+)</version>"))
                    {
                        var ver = ((System.Text.RegularExpressions.Match)m).Groups[1].Value;
                        if (ver.StartsWith(gameVersion + "-", StringComparison.Ordinal))
                            picked = ver;
                    }
                    return SanitizeExternalId(picked);
                }
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] 自动选取最新版本异常({kind}/{gameVersion}):{ex.Message}");
            return null;
        }
    }

    // ==================== 通用工具 ====================

    /// <summary>记录加载器元信息到实例(含启动用 LoaderVersionId);返回是否成功写入。
    /// 找不到实例时重扫磁盘一次(安装期间列表可能被刷新替换),仍失败返回 false 由调用方回滚版本文件,
    /// 杜绝"加载器版本 JSON 已落盘但实例元数据缺失"的孤儿空壳。</summary>
    private bool MarkInstanceLoader(string instanceId, string loader, string version, string? loaderVersionId)
    {
        var inst = _instanceService.Instances.Find(i => i.Id == instanceId);
        if (inst == null)
        {
            _instanceService.RefreshInstances();
            inst = _instanceService.Instances.Find(i => i.Id == instanceId);
        }
        if (inst == null)
        {
            App.WriteAppLog($"[加载器] 记录元信息失败:找不到实例 {instanceId},将回滚已写入的版本文件");
            return false;
        }
        inst.ModLoader = loader;
        inst.ModLoaderVersion = version;
        if (!string.IsNullOrEmpty(loaderVersionId))
            inst.LoaderVersionId = loaderVersionId;
        if (!_instanceService.SaveInstance(inst))
        {
            App.WriteAppLog($"[加载器] 记录元信息保存失败 {instanceId},将回滚已写入的版本文件");
            return false;
        }
        return true;
    }

    /// <summary>加载器元数据写入失败/安装异常时回滚刚生成的版本 JSON(防孤儿版本;共享 libraries 不受影响)</summary>
    private static void CleanupLoaderVersion(string? versionId)
    {
        if (string.IsNullOrEmpty(versionId)) return;
        try
        {
            string dir = Path.Combine(AppPaths.Versions, versionId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            App.WriteAppLog($"[加载器] 已回滚孤儿版本文件:{versionId}");
        }
        catch (Exception ex) { App.WriteAppLog($"[加载器] 回滚版本文件失败 {versionId}:{ex.Message}"); }
    }

    /// <summary>依赖库下载候选:BMCLAPI 镜像优先,官方源兜底</summary>
    private static List<string> LibraryUrlCandidates(string? urlRoot, string mavenPath)
    {
        var list = new List<string> { BmclapiMaven + mavenPath };
        string root = string.IsNullOrEmpty(urlRoot) ? "https://libraries.minecraft.net/" : urlRoot;
        if (!root.EndsWith("/", StringComparison.Ordinal)) root += "/";
        string official = root + mavenPath;
        if (!list.Contains(official)) list.Add(official);
        return list;
    }

    /// <summary>将 Maven 坐标转换为相对路径(group:artifact:version[:classifier][@ext] → group/artifact/…)</summary>
    private static string NameToMavenPath(string name)
    {
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

    /// <summary>校验来自安装器/网络清单的版本 id:含路径分隔符或 '..' 的拒绝(防目录逃逸写任意位置,
    /// 2026-08-28 全局审计修复)</summary>
    private static string? SanitizeExternalId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.Contains('/') || id.Contains('\\') || id.Contains("..")) return null;
        return id;
    }

    /// <summary>拼接相对路径并断言其位于根目录之内,越界返回 null(ZipSlip 防护,
    /// 2026-08-28 全局审计修复)</summary>
    private static string? SafeCombineInside(string root, string relPath)
    {
        string full = Path.GetFullPath(Path.Combine(root, relPath));
        string rootFull = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>多地址依次拉取文本(镜像优先),全部失败返回 null</summary>
    private async Task<string?> FetchTextAsync(params string[] urls)
    {
        foreach (var url in WithCustomFirst(urls))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var resp = await _http.GetAsync(url, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    App.WriteAppLog($"[加载器] 资料拉取失败({(int)resp.StatusCode}),尝试下一源:{url}");
                    continue;
                }
                var text = await resp.Content.ReadAsStringAsync(cts.Token);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[加载器] 资料拉取异常,尝试下一源:{url} -> {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>2026-09-30:启用自有镜像时,把每个 BMCLAPI 形态的 URL 复制一份换成自建源同路径排在最前,
    /// 原地址依次保留兜底——自建源还没同步到该加载器资料时不至于装不上,只是白走一跳</summary>
    private List<string> WithCustomFirst(string[] urls)
    {
        string? custom = _downloadService.CustomBaseUrl;
        var list = new List<string>(urls.Length + 1);
        foreach (var u in urls)
        {
            if (custom != null &&
                u.StartsWith(MirrorUrlMap.BmclapiRoot, StringComparison.OrdinalIgnoreCase))
            {
                string own = custom + u.Substring(MirrorUrlMap.BmclapiRoot.Length);
                if (!list.Contains(own)) list.Add(own);
            }
            if (!list.Contains(u)) list.Add(u);
        }
        return list;
    }

    /// <summary>将异常消息转换为可读中文(隐藏底层堆栈)</summary>
    private static string GetFriendlyExceptionMessage(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.Message.Contains("404") || httpEx.Message.Contains("Not Found"))
                return "下载文件不存在,可能该版本已被移除";
            if (httpEx.Message.Contains("timeout") || httpEx.Message.Contains("超时"))
                return "网络连接超时,请检查网络后重试";
            if (httpEx.Message.Contains("resolve") || httpEx.Message.Contains("DNS"))
                return "域名解析失败,请检查 DNS 或网络设置";
            return "下载失败,请检查网络连接";
        }
        if (ex is TaskCanceledException)
            return "网络请求超时,请检查网络后重试";
        if (ex is IOException ioEx)
        {
            if (ioEx.Message.Contains("disk") || ioEx.Message.Contains("space"))
                return "磁盘空间不足,请清理后重试";
            if (ioEx.Message.Contains("denied") || ioEx.Message.Contains("拒绝"))
                return "没有写入权限,请检查文件夹权限";
            return "文件写入失败,请检查磁盘空间或权限";
        }
        return "安装过程发生未知错误,请稍后重试";
    }

    /// <summary>多源依次下载:国内镜像优先,逐个尝试直到成功;成功后校验文件非空;重试前清理残留</summary>
    /// <summary>老 Forge(1.7.10 及更早)的 maven 工件用双版本号 {mc}-{ver}-{mc};
    /// 1.8 及以后的 Forge 用单版本号 {mc}-{ver}(2026-09-25 新增,修复新 Forge 404)</summary>
    private static bool IsLegacyForgeArtifact(string gameVersion)
    {
        var parts = gameVersion.Split('.');
        if (parts.Length >= 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor))
            return major < 1 || (major == 1 && minor <= 7);
        return false;
    }

    private async Task<bool> DownloadFromMirrorsAsync(List<string> urls, string localPath)
    {
        // 2026-09-25:单任务对象逐源换 URL 重试——多个镜像尝试的是同一个文件,
        // 旧实现每换一个源就 new 一个任务入下载中心队列,导致同一文件出现多行任务
        // (用户实测 forge-installer 一行变三行,进度条跳来跳去);现在合并为一个逻辑任务。
        string lastError = "";
        var task = new DownloadTaskItem
        {
            LocalPath = localPath,
            Category = DownloadCategory.Other
        };
        foreach (var url in urls)
        {
            try
            {
                if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                {
                    if (lastError == "") return true;      // 首次尝试直接复用缓存文件
                    File.Delete(localPath);                 // 换源重试时清掉坏残片
                }
                task.Url = url;
                if (await _downloadService.DownloadAllAsync(new() { task }))
                {
                    // 防 0 字节假成功(部分镜像对不存在文件返回空响应体)
                    if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                        return true;
                    lastError = "下载结果为空文件";
                    App.WriteAppLog($"[加载器] 下载得到空文件,尝试下一个:{url}");
                    continue;
                }
                lastError = GetDownloadError(task.Error);
                App.WriteAppLog($"[加载器] 下载源失败,尝试下一个:{url} -> {lastError}");
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                App.WriteAppLog($"[加载器] 下载源异常,尝试下一个:{url} -> {ex.Message}");
            }
        }
        return false;
    }

    private static string GetDownloadError(string? rawError)
    {
        if (string.IsNullOrEmpty(rawError)) return "未知错误";
        if (rawError.Contains("404") || rawError.Contains("Not Found"))
            return "文件不存在(404)";
        if (rawError.Contains("timeout") || rawError.Contains("超时"))
            return "连接超时";
        return "下载失败";
    }
}
