// ModrinthService.cs — Modrinth API 集成服务
// 可爱的芙芙 - 参考 PrismLauncher / HMCL 的模组搜索与下载
//
// 功能:
// 1. 搜索 Modrinth 上的模组(支持关键词、MC版本、加载器过滤)
// 2. 获取模组的版本列表(按MC版本和加载器筛选)
// 3. 获取版本下载链接(复用 DownloadService 多线程引擎)
// 4. 搜索整合包(Modpack)并一键安装
//
// API 文档: https://docs.modrinth.com (Labrinth v2)
// 无需认证即可搜索和下载,但需携带 User-Agent 头

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace FufuLauncher.Services;

/// <summary>Modrinth 搜索结果中的项目(模组/整合包/资源包等)</summary>
public class ModrinthProject
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
    [JsonPropertyName("client_side")] public string ClientSide { get; set; } = "";
    [JsonPropertyName("server_side")] public string ServerSide { get; set; } = "";
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("follows")] public long Follows { get; set; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("date_modified")] public string DateModified { get; set; } = "";
    [JsonPropertyName("latest_version_ids")] public List<string> LatestVersionIds { get; set; } = new();
    [JsonPropertyName("display_categories")] public List<string> DisplayCategories { get; set; } = new();
    /// <summary>项目类型: mod / modpack / resourcepack / shader</summary>
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "mod";
    /// <summary>支持的加载器(从 facets 解析)</summary>
    public List<string> Loaders { get; set; } = new();
    /// <summary>支持的MC版本(从 facets 解析)</summary>
    public List<string> GameVersions { get; set; } = new();
}

/// <summary>Modrinth 版本(对应一个具体的文件下载)</summary>
public class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("changelog")] public string? Changelog { get; set; }
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = new();
    [JsonPropertyName("version_type")] public string VersionType { get; set; } = "release"; // release/beta/alpha
    [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();
    [JsonPropertyName("files")] public List<ModrinthFile> Files { get; set; } = new();
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = new();

    /// <summary>获取主文件(优先 primary=true,否则取第一个)</summary>
    public ModrinthFile? GetPrimaryFile() =>
        Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();
}

public class ModrinthFile
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("hashes")] public ModrinthHashes Hashes { get; set; } = new();
}

public class ModrinthHashes
{
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("sha512")] public string Sha512 { get; set; } = "";
}

public class ModrinthDependency
{
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = ""; // required/optional/incompatible/embedded
}

/// <summary>Modrinth 搜索结果</summary>
public class ModrinthSearchResult
{
    [JsonPropertyName("hits")] public List<ModrinthProject> Hits { get; set; } = new();
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
}

public class ModrinthService
{
    // 接入点策略:只使用 Modrinth 官方 API(实测国内可直连,数据最完整最及时)。
    // 不依赖任何第三方镜像(第三方镜像数据滞后/随时失效,可靠性不如官方源)。
    // 健壮性靠"快速失败 + 自动重试"实现:首次 12s 快速探测,失败立即重试(25s 放宽超时)。
    private static readonly string[] FallbackBases =
    {
        "https://api.modrinth.com/v2"
    };
    private static string _currentBase = FallbackBases[0];
    private static readonly object _baseLock = new();
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>最近一次网络请求的错误信息(成功时清空),供 UI 弹出中文网络异常提示</summary>
    public string? LastError { get; private set; }

    /// <summary>当前生效的接入点(供 UI 展示)</summary>
    public static string CurrentApiBase { get { lock (_baseLock) return _currentBase; } }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // Modrinth 要求 User-Agent 包含项目标识(否则可能被限流)
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/1.0.5.1 (https://github.com/FufuLauncher)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>
    /// 带快速重试的 GET 请求:首次 12s 快速探测,失败立即重试一次(25s 放宽超时),
    /// 仍失败则依次尝试其它候选接入点。4xx 业务错误直接抛出不重试(重试也是同样的错)。
    /// 返回响应 JSON;全部失败抛 HttpRequestException。
    /// </summary>
    private static async Task<string> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        string startBase;
        lock (_baseLock) startBase = _currentBase;

        // 尝试顺序:当前粘滞端点优先,其余候选端点兜底
        var order = new List<string> { startBase };
        order.AddRange(FallbackBases.Where(b => b != startBase));

        foreach (var b in order)
        {
            // 每个端点最多两次尝试:首次快速失败(12s),重试放宽到 25s
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attemptCts.CancelAfter(TimeSpan.FromSeconds(attempt == 1 ? 12 : 25));
                    using var resp = await Http.GetAsync(b + relativeUrl, attemptCts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        lock (_baseLock) _currentBase = b;
                        return await resp.Content.ReadAsStringAsync(ct);
                    }
                    // 4xx 业务错误直接抛出(参数/不存在类错误,重试无意义)
                    if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500)
                        throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (HttpRequestException hre) when (hre.StatusCode.HasValue && (int)hre.StatusCode < 500)
                {
                    throw; // 4xx 不重试不切换端点
                }
                catch (Exception ex)
                {
                    App.WriteAppLog($"[Modrinth] 接入点访问失败 {b} 第{attempt}次:{ex.Message}");
                }
            }
        }
        throw new HttpRequestException("Modrinth 接口暂时无法访问(已自动重试),请检查网络后重试");
    }

    private readonly DownloadService _downloadService;

    public ModrinthService(DownloadService downloadService)
    {
        _downloadService = downloadService;
    }

    /// <summary>
    /// 搜索 Modrinth 项目(模组/整合包等)。
    /// 参考 PrismLauncher:支持关键词、MC版本、加载器、项目类型过滤。
    /// </summary>
    public async Task<ModrinthSearchResult> SearchAsync(
        string query,
        int offset = 0,
        int limit = 20,
        string? gameVersion = null,
        string? loader = null,
        string projectType = "mod",
        string sort = "relevance",
        CancellationToken ct = default)
    {
        // 构建 facets(参考 PrismLauncher 的搜索过滤方式)
        var facetGroups = new List<List<string>>();

        // 项目类型
        facetGroups.Add(new List<string> { $"\"project_type:{projectType}\"" });

        // MC 版本过滤
        if (!string.IsNullOrEmpty(gameVersion))
            facetGroups.Add(new List<string> { $"\"versions:{gameVersion}\"" });

        // 加载器过滤
        if (!string.IsNullOrEmpty(loader))
            facetGroups.Add(new List<string> { $"\"categories:{loader.ToLowerInvariant()}\"" });

        string facets = "[" + string.Join(",", facetGroups.Select(g => "[" + string.Join(",", g) + "]")) + "]";

        string relativeUrl = "/search" +
            $"?query={Uri.EscapeDataString(query ?? "")}" +
            $"&offset={offset}" +
            $"&limit={limit}" +
            $"&facets={Uri.EscapeDataString(facets)}" +
            $"&index={sort}";

        LastError = null;
        try
        {
            var json = await GetJsonAsync(relativeUrl, ct);

            var result = JsonSerializer.Deserialize<ModrinthSearchResult>(json);
            if (result == null) return new ModrinthSearchResult();

            // 从 facets 解析出 loaders 和 game_versions(供 UI 显示)
            foreach (var hit in result.Hits)
            {
                hit.Loaders = hit.Categories.Where(c =>
                    c is "fabric" or "forge" or "quilt" or "neoforge" or "liteloader").ToList();
            }

            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Modrinth] 搜索异常:{ex.Message}");
            LastError = "网络异常,无法访问 Modrinth 接口,请检查网络连接";
            return new ModrinthSearchResult();
        }
    }

    /// <summary>获取指定项目的所有版本列表</summary>
    public async Task<List<ModrinthVersion>> GetProjectVersionsAsync(
        string projectIdOrSlug,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        string relativeUrl = $"/project/{projectIdOrSlug}/version";
        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(gameVersion))
            queryParams.Add($"game_versions={Uri.EscapeDataString(gameVersion)}");
        if (!string.IsNullOrEmpty(loader))
            queryParams.Add($"loaders=[\"{Uri.EscapeDataString(loader.ToLowerInvariant())}\"]");
        if (queryParams.Count > 0)
            relativeUrl += "?" + string.Join("&", queryParams);

        LastError = null;
        try
        {
            var json = await GetJsonAsync(relativeUrl, ct);
            return JsonSerializer.Deserialize<List<ModrinthVersion>>(json) ?? new List<ModrinthVersion>();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Modrinth] 获取版本异常:{ex.Message}");
            LastError = "网络异常,无法访问 Modrinth 接口,请检查网络连接";
            return new List<ModrinthVersion>();
        }
    }

    /// <summary>获取单个版本的详细信息</summary>
    public async Task<ModrinthVersion?> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        LastError = null;
        try
        {
            var json = await GetJsonAsync($"/version/{versionId}", ct);
            return JsonSerializer.Deserialize<ModrinthVersion>(json);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Modrinth] 获取版本详情异常:{ex.Message}");
            LastError = "网络异常,无法访问 Modrinth 接口,请检查网络连接";
            return null;
        }
    }

    /// <summary>
    /// 从 Modrinth 下载模组文件到指定目录(复用 DownloadService 多线程引擎)。
    /// 参考 PrismLauncher:自动下载主文件,SHA1 校验。
    /// </summary>
    public async Task<bool> DownloadModVersionAsync(ModrinthVersion version, string destDir, CancellationToken ct = default)
    {
        var file = version.GetPrimaryFile();
        if (file == null)
        {
            App.WriteAppLog($"[Modrinth] 版本 {version.VersionNumber} 没有可下载的文件");
            return false;
        }

        Directory.CreateDirectory(destDir);
        string destPath = Path.Combine(destDir, file.Filename);

        var task = new DownloadTaskItem
        {
            Url = file.Url,
            LocalPath = destPath,
            Sha1 = file.Hashes.Sha1,
            Size = file.Size,
            Category = DownloadCategory.Mod
        };

        bool ok = await _downloadService.DownloadAllAsync(new List<DownloadTaskItem> { task });
        if (!ok)
        {
            App.WriteAppLog($"[Modrinth] 下载失败:{file.Filename} - {task.Error}");
            return false;
        }

        App.WriteAppLog($"[Modrinth] ✓ 下载完成:{file.Filename} ({file.Size / 1024.0 / 1024.0:F2} MB)");
        return true;
    }

    /// <summary>
    /// 获取项目的依赖列表(仅 required 类型)。
    /// 参考 PrismLauncher:自动解析并下载依赖模组。
    /// </summary>
    public async Task<List<ModrinthVersion>> ResolveDependenciesAsync(
        ModrinthVersion version,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        var deps = new List<ModrinthVersion>();
        var required = version.Dependencies
            .Where(d => d.DependencyType == "required" && !string.IsNullOrEmpty(d.ProjectId))
            .ToList();

        foreach (var dep in required)
        {
            if (string.IsNullOrEmpty(dep.ProjectId)) continue;

            // 如果指定了 version_id,直接获取该版本
            if (!string.IsNullOrEmpty(dep.VersionId))
            {
                var depVer = await GetVersionAsync(dep.VersionId!, ct);
                if (depVer != null) { deps.Add(depVer); continue; }
            }

            // 否则获取该项目的最新版本(匹配 gameVersion + loader)
            var versions = await GetProjectVersionsAsync(dep.ProjectId, gameVersion, loader, ct);
            var best = versions.FirstOrDefault();
            if (best != null) deps.Add(best);
        }

        return deps;
    }

    /// <summary>
    /// 一键安装模组:下载主文件 + 自动解析并下载依赖。
    /// 参考 HMCL/PrismLauncher 的一键安装体验。
    /// </summary>
    public async Task<(bool Success, List<string> InstalledFiles)> InstallModWithDependenciesAsync(
        ModrinthVersion version,
        string modsDir,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        var installed = new List<string>();

        // 1. 下载主文件
        bool mainOk = await DownloadModVersionAsync(version, modsDir, ct);
        if (!mainOk) return (false, installed);
        var mainFile = version.GetPrimaryFile();
        if (mainFile != null) installed.Add(mainFile.Filename);

        // 2. 解析并下载依赖
        var deps = await ResolveDependenciesAsync(version, gameVersion, loader, ct);
        foreach (var dep in deps)
        {
            ct.ThrowIfCancellationRequested();
            bool depOk = await DownloadModVersionAsync(dep, modsDir, ct);
            if (depOk)
            {
                var depFile = dep.GetPrimaryFile();
                if (depFile != null) installed.Add(depFile.Filename);
                App.WriteAppLog($"[Modrinth] ✓ 依赖已安装:{dep.Name} {dep.VersionNumber}");
            }
            else
            {
                App.WriteAppLog($"[Modrinth] ⚠ 依赖安装失败:{dep.Name} {dep.VersionNumber}");
            }
        }

        return (true, installed);
    }

    /// <summary>搜索整合包(Modpack)</summary>
    public Task<ModrinthSearchResult> SearchModpacksAsync(
        string query, int offset = 0, int limit = 20,
        string? gameVersion = null, string? loader = null,
        CancellationToken ct = default)
    {
        return SearchAsync(query, offset, limit, gameVersion, loader, "modpack", "relevance", ct);
    }

    /// <summary>获取项目基础信息(轻量化:仅用于前置依赖名称提示)</summary>
    public async Task<ModrinthProject?> GetProjectAsync(string projectIdOrSlug, CancellationToken ct = default)
    {
        try
        {
            var json = await GetJsonAsync($"/project/{projectIdOrSlug}", ct);
            return JsonSerializer.Deserialize<ModrinthProject>(json);
        }
        catch { return null; }
    }

    // ===== 模组图标下载(修复图标不显示:直接拉取图标字节,供 UI 解码渲染) =====

    private static readonly ConcurrentDictionary<string, byte[]> IconCache = new();

    /// <summary>下载模组图标原始字节(带内存缓存,失败返回 null)</summary>
    public static async Task<byte[]?> DownloadIconBytesAsync(string iconUrl)
    {
        if (string.IsNullOrEmpty(iconUrl)) return null;
        if (IconCache.TryGetValue(iconUrl, out var cached)) return cached;
        try
        {
            var bytes = await Http.GetByteArrayAsync(iconUrl);
            if (bytes.Length == 0) return null;
            // 限制缓存条目数量,避免内存无限增长
            if (IconCache.Count < 500) IconCache.TryAdd(iconUrl, bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }
}
