// ModLoaderInstallService.cs — 模组加载器安装服务(优化版)
// 可爱的芙芙 - 阶段2 模块(异常提示优化)
//
// 支持:Forge / Fabric / Quilt / NeoForge / OptiFine 一键安装(安装用户选定的具体版本)
// 下载源:国内镜像优先(BMCLAPI),失败自动回退官方源
// 捕获全部异常，输出普通玩家看得懂的中文提示，不抛出底层英文堆栈

using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    public static LoaderInstallResult Ok(string? version = null) => new() { Success = true, InstalledVersion = version };
    public static LoaderInstallResult Fail(string msg) => new() { Success = false, ErrorMessage = msg };
}

public class ModLoaderInstallService
{
    private readonly DownloadService _downloadService;
    private readonly InstanceService _instanceService;
    private readonly HttpClient _http = new();

    public ModLoaderInstallService(DownloadService downloadService,
                                       InstanceService instanceService)
    {
        _downloadService = downloadService;
        _instanceService = instanceService;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/1.0.5.1 (Windows)");
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

    private class FabricInstallerVersion
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("stable")] public bool Stable { get; set; }
    }

    // ===== Fabric 安装器版本解析(带缓存) =====
    // 注意:fabric-installer 工件的版本号(如 1.1.2)与 fabric-loader 版本号(如 0.19.3)是两套独立体系,
    // 不能用 loader 版本拼安装器下载地址(会 404)。安装器版本从 fabric-meta /v2/versions/installer 获取。
    private string? _fabricInstallerVerCached;
    private DateTime _fabricInstallerVerCachedUtc = DateTime.MinValue;

    private async Task<string?> ResolveFabricInstallerVersionAsync()
    {
        if (_fabricInstallerVerCached != null && DateTime.UtcNow - _fabricInstallerVerCachedUtc < TimeSpan.FromHours(24))
            return _fabricInstallerVerCached;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var resp = await _http.GetAsync("https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/installer", cts.Token);
            if (!resp.IsSuccessStatusCode) return _fabricInstallerVerCached;
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            var list = JsonSerializer.Deserialize<List<FabricInstallerVersion>>(json);
            if (list == null || list.Count == 0) return _fabricInstallerVerCached;
            _fabricInstallerVerCached = list.FirstOrDefault(v => v.Stable)?.Version ?? list[0].Version;
            _fabricInstallerVerCachedUtc = DateTime.UtcNow;
            return _fabricInstallerVerCached;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] Fabric 安装器版本解析失败: {ex.Message}");
            return _fabricInstallerVerCached;
        }
    }

    // ===== Fabric =====
    public async Task<LoaderInstallResult> InstallFabricAsync(string instanceId, string gameVersion,
                                                                  string? loaderVersion = null)
    {
        try
        {
            if (string.IsNullOrEmpty(loaderVersion))
                return LoaderInstallResult.Fail("未指定 Fabric 版本,请先选择版本号");

            // 安装器工件版本 ≠ loader 版本:从 fabric-meta 解析当前稳定安装器版本
            string? installerVer = await ResolveFabricInstallerVersionAsync();
            if (string.IsNullOrEmpty(installerVer))
                return LoaderInstallResult.Fail("无法获取 Fabric 安装器版本信息,请检查网络后重试");

            // 国内镜像优先,回退 Fabric 官方 maven
            var urls = new List<string>
            {
                $"https://bmclapi2.bangbang93.com/maven/net/fabricmc/fabric-installer/{installerVer}/fabric-installer-{installerVer}.jar",
                $"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{installerVer}/fabric-installer-{installerVer}.jar"
            };
            string installersDir = AppPaths.Installers;
            Directory.CreateDirectory(installersDir);
            string installerPath = Path.Combine(installersDir, $"fabric-installer-{installerVer}.jar");

            bool ok = await DownloadFromMirrorsAsync(urls, installerPath);
            if (!ok)
                return LoaderInstallResult.Fail($"Fabric 安装器({installerVer})下载失败,请检查网络后重试");

            // 记录元信息到实例
            var inst = _instanceService.Instances.Find(i => i.Id == instanceId);
            if (inst != null)
            {
                inst.ModLoader = "Fabric";
                inst.ModLoaderVersion = loaderVersion;
                _instanceService.SaveInstance(inst);
            }
            return LoaderInstallResult.Ok(loaderVersion);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] Fabric 安装异常: {ex.Message}");
            return LoaderInstallResult.Fail($"Fabric 安装失败: {GetFriendlyExceptionMessage(ex)}");
        }
    }

    // ===== Forge =====
    public async Task<LoaderInstallResult> InstallForgeAsync(string instanceId, string gameVersion,
                                                                 string forgeVersion)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(forgeVersion))
                return LoaderInstallResult.Fail("未指定 Forge 版本,请先选择版本号");

            // 国内镜像优先,回退 Forge 官方
            var urls = new List<string>
            {
                $"https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/{gameVersion}-{forgeVersion}/forge-{gameVersion}-{forgeVersion}-installer.jar",
                $"https://files.minecraftforge.net/maven/net/minecraftforge/forge/{gameVersion}-{forgeVersion}/forge-{gameVersion}-{forgeVersion}-installer.jar"
            };
            string installersDir = AppPaths.Installers;
            Directory.CreateDirectory(installersDir);
            string installerPath = Path.Combine(installersDir, $"forge-installer-{gameVersion}-{forgeVersion}.jar");

            bool ok = await DownloadFromMirrorsAsync(urls, installerPath);
            if (!ok)
                return LoaderInstallResult.Fail($"Forge 安装器({forgeVersion})下载失败,请检查 {gameVersion} 版本是否有对应的 Forge 版本");

            var inst = _instanceService.Instances.Find(i => i.Id == instanceId);
            if (inst != null)
            {
                inst.ModLoader = "Forge";
                inst.ModLoaderVersion = forgeVersion;
                _instanceService.SaveInstance(inst);
            }
            return LoaderInstallResult.Ok(forgeVersion);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] Forge 安装异常: {ex.Message}");
            return LoaderInstallResult.Fail($"Forge 安装失败: {GetFriendlyExceptionMessage(ex)}");
        }
    }

    // ===== Quilt =====
    public async Task<LoaderInstallResult> InstallQuiltAsync(string instanceId, string gameVersion,
                                                                  string? loaderVersion = null)
    {
        try
        {
            if (string.IsNullOrEmpty(loaderVersion))
            {
                try
                {
                    using var resp = await _http.GetAsync("https://meta.quiltmc.org/v3/versions/loader");
                    if (!resp.IsSuccessStatusCode)
                        return LoaderInstallResult.Fail("无法连接 Quilt 官方源获取版本列表,请检查网络后重试");
                    var json = await resp.Content.ReadAsStringAsync();
                    var versions = JsonSerializer.Deserialize<List<FabricLoaderVersion>>(json);
                    loaderVersion = versions?.Count > 0 ? versions[0].Loader.Version : "0.26.0";
                }
                catch (TaskCanceledException)
                {
                    return LoaderInstallResult.Fail("连接 Quilt 官方源超时,请检查网络后重试");
                }
            }

            // Quilt 官方 maven(国内暂无镜像)
            string installerUrl = $"https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/{loaderVersion}/quilt-installer-{loaderVersion}.jar";
            string installersDir = AppPaths.Installers;
            Directory.CreateDirectory(installersDir);
            string installerPath = Path.Combine(installersDir, $"quilt-installer-{loaderVersion}.jar");

            bool ok = await DownloadFromMirrorsAsync(new List<string> { installerUrl }, installerPath);
            if (!ok)
                return LoaderInstallResult.Fail($"Quilt 安装器({loaderVersion})下载失败,请稍后重试");

            var inst = _instanceService.Instances.Find(i => i.Id == instanceId);
            if (inst != null)
            {
                inst.ModLoader = "Quilt";
                inst.ModLoaderVersion = loaderVersion;
                _instanceService.SaveInstance(inst);
            }
            return LoaderInstallResult.Ok(loaderVersion);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] Quilt 安装异常: {ex.Message}");
            return LoaderInstallResult.Fail($"Quilt 安装失败: {GetFriendlyExceptionMessage(ex)}");
        }
    }

    // ===== NeoForge =====
    public async Task<LoaderInstallResult> InstallNeoForgeAsync(string instanceId, string gameVersion,
                                                                     string? loaderVersion = null)
    {
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
            // 国内镜像优先,回退 NeoForge 官方 maven
            var urls = new List<string>
            {
                $"https://bmclapi2.bangbang93.com/maven/{pathSeg}/{fileName}",
                $"https://maven.neoforged.net/releases/{pathSeg}/{fileName}"
            };
            string installersDir = AppPaths.Installers;
            Directory.CreateDirectory(installersDir);
            string installerPath = Path.Combine(installersDir, $"neoforge-installer-{loaderVersion}.jar");

            bool ok = await DownloadFromMirrorsAsync(urls, installerPath);
            if (!ok)
                return LoaderInstallResult.Fail($"NeoForge 安装器({loaderVersion})下载失败,请稍后重试");

            var inst = _instanceService.Instances.Find(i => i.Id == instanceId);
            if (inst != null)
            {
                inst.ModLoader = "NeoForge";
                inst.ModLoaderVersion = loaderVersion;
                _instanceService.SaveInstance(inst);
            }
            return LoaderInstallResult.Ok(loaderVersion);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] NeoForge 安装异常: {ex.Message}");
            return LoaderInstallResult.Fail($"NeoForge 安装失败: {GetFriendlyExceptionMessage(ex)}");
        }
    }

    public class OptiFineVersion
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("patch")] public string Patch { get; set; } = "";
        [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    }

    // ===== OptiFine =====
    public async Task<LoaderInstallResult> InstallOptiFineAsync(string instanceId, string gameVersion,
                                                                     string? loaderVersion = null)
    {
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
                    using var resp = await _http.GetAsync($"https://bmclapi2.bangbang93.com/optifine/{gameVersion}");
                    if (!resp.IsSuccessStatusCode)
                        return LoaderInstallResult.Fail("无法连接 OptiFine 镜像源获取版本列表,请检查网络后重试");
                    var json = await resp.Content.ReadAsStringAsync();
                    var list = JsonSerializer.Deserialize<List<OptiFineVersion>>(json);
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

            bool ok = await DownloadFromMirrorsAsync(new List<string> { installerUrl }, installerPath);
            if (!ok)
                return LoaderInstallResult.Fail($"OptiFine({verLabel})下载失败,请稍后重试");

            var inst = _instanceService.Instances.Find(i => i.Id == instanceId);
            if (inst != null)
            {
                inst.ModLoader = "OptiFine";
                inst.ModLoaderVersion = verLabel;
                _instanceService.SaveInstance(inst);
            }
            return LoaderInstallResult.Ok(verLabel);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] OptiFine 安装异常: {ex.Message}");
            return LoaderInstallResult.Fail($"OptiFine 安装失败: {GetFriendlyExceptionMessage(ex)}");
        }
    }

    // ===== 统一入口 =====
    /// <summary>按加载器类型统一调度安装,返回中文结果</summary>
    public Task<LoaderInstallResult> InstallLoaderAsync(string instanceId, string gameVersion,
                                                          string kind, string? loaderVersion = null)
    {
        return (kind ?? "").Trim().ToLowerInvariant() switch
        {
            "fabric" => InstallFabricAsync(instanceId, gameVersion, loaderVersion),
            "forge" => string.IsNullOrWhiteSpace(loaderVersion)
                ? Task.FromResult(LoaderInstallResult.Fail("请选择 Forge 版本号后再安装"))
                : InstallForgeAsync(instanceId, gameVersion, loaderVersion!),
            "quilt" => InstallQuiltAsync(instanceId, gameVersion, loaderVersion),
            "neoforge" => InstallNeoForgeAsync(instanceId, gameVersion, loaderVersion),
            "optifine" => InstallOptiFineAsync(instanceId, gameVersion, loaderVersion),
            _ => Task.FromResult(LoaderInstallResult.Fail($"不支持的加载器类型: {kind}"))
        };
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

    /// <summary>多源依次下载:国内镜像优先,逐个尝试直到成功;每次重试前清理残留文件</summary>
    private async Task<bool> DownloadFromMirrorsAsync(List<string> urls, string localPath)
    {
        string lastError = "";
        foreach (var url in urls)
        {
            try
            {
                if (File.Exists(localPath))
                {
                    // 首次尝试时,已存在非空缓存文件则直接复用(文件名含精确版本号)
                    if (lastError == "" && new FileInfo(localPath).Length > 0) return true;
                    File.Delete(localPath);
                }
                var task = new DownloadTaskItem
                {
                    Url = url,
                    LocalPath = localPath,
                    Category = DownloadCategory.Other
                };
                if (await _downloadService.DownloadAllAsync(new() { task }))
                    return true;
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
