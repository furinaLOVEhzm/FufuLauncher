// ModrinthService.cs — Modrinth API 集成服务
// FufuLauncher - 参考主流启动器的模组搜索与下载
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

    /// <summary>全局并发闸门:Modrinth 对并发请求敏感(实测并发几十个会被 429 限流),
    /// 所有 API 请求在此排队,同时最多 2 个在途,从源头杜绝并发风暴(2026-09 修复)</summary>
    private static readonly SemaphoreSlim ApiGate = new(2, 2);

    /// <summary>全局 429 冷却截止(UTC ticks):任一请求收到 429 后全局暂停,让限流窗口过去</summary>
    private static long _global429CooldownUntilTicks;

    /// <summary>429 全局冷却时长(秒):收到 429 后全局暂停,避免几十个请求同时退避互相放大</summary>
    private const int Global429CooldownSeconds = 3;

    private static async Task WaitFor429CooldownAsync(CancellationToken ct)
    {
        while (true)
        {
            long until;
            lock (_baseLock) until = Volatile.Read(ref _global429CooldownUntilTicks);
            if (until == 0) return;
            long remainMs = (until - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond;
            if (remainMs <= 0) return;
            try { await Task.Delay((int)Math.Min(remainMs, 3000), ct); }
            catch (OperationCanceledException) { throw; }
        }
    }

    private static void Arm429Cooldown()
    {
        lock (_baseLock)
        {
            long until = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(Global429CooldownSeconds).Ticks;
            Interlocked.Exchange(ref _global429CooldownUntilTicks, until);
        }
    }

    /// <summary>最近一次网络请求的错误信息(成功时清空),供 UI 弹出中文网络异常提示</summary>
    public string? LastError { get; private set; }

    /// <summary>当前生效的接入点(供 UI 展示)</summary>
    public static string CurrentApiBase { get { lock (_baseLock) return _currentBase; } }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // Modrinth 要求 User-Agent 包含项目标识(否则可能被限流)
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/3.0 (Windows)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>
    /// 带快速重试的 GET 请求:首次 12s 快速探测,失败立即重试一次(25s 放宽超时),
    /// 仍失败则依次尝试其它候选接入点。4xx 业务错误直接抛出不重试(重试也是同样的错)。
    /// 返回响应 JSON;全部失败抛 HttpRequestException。
    /// </summary>
    /// <summary>429 限流退避间隔(秒):第一次 1.5s,第二次 3s,第三次 6s</summary>
    private static readonly int[] RateLimitBackoff = { 0, 1500, 3000, 6000 };

    /// <summary>
    /// 带快速重试的 GET 请求:首次 12s 快速探测,失败立即重试一次(25s 放宽超时),
    /// 仍失败则依次尝试其它候选接入点。
    /// 4xx 业务错误直接抛出不重试(重试也是同样的错);HTTP 429 限流例外——
    /// 按 Retry-After 或递增间隔退避重试,最多追加 3 次,缓解"多页面并发搜索被打限流"。
    /// 返回响应 JSON;全部失败抛 HttpRequestException。
    /// </summary>
    private static async Task<string> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        await ApiGate.WaitAsync(ct);
        try
        {
            await WaitFor429CooldownAsync(ct);
            return await GetJsonAsyncCore(relativeUrl, ct);
        }
        finally { ApiGate.Release(); }
    }

    /// <summary>实际请求逻辑(已在 ApiGate 闸门内串行)</summary>
    private static async Task<string> GetJsonAsyncCore(string relativeUrl, CancellationToken ct)
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
                    // 429 限流:退避重试(最多 3 次),其它 4xx 直接抛出
                    if ((int)resp.StatusCode == 429)
                    {
                        Arm429Cooldown();
                        for (int rl = 1; rl <= 3; rl++)
                        {
                            ct.ThrowIfCancellationRequested();
                            // 优先遵循服务端 Retry-After(秒),否则用递增间隔
                            int delayMs = RateLimitBackoff[Math.Min(rl, RateLimitBackoff.Length - 1)];
                            var retryAfter = resp.Headers.RetryAfter?.Delta;
                            if (retryAfter.HasValue)
                                delayMs = Math.Max(delayMs, (int)Math.Min(retryAfter.Value.TotalMilliseconds, 10_000));
                            App.WriteAppLog($"[Modrinth] HTTP 429 限流,{delayMs / 1000.0:F1}s 后重试({rl}/3)");
                            await Task.Delay(delayMs, ct);
                            using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            retryCts.CancelAfter(TimeSpan.FromSeconds(25));
                            using var r2 = await Http.GetAsync(b + relativeUrl, retryCts.Token);
                            if (r2.IsSuccessStatusCode)
                            {
                                lock (_baseLock) _currentBase = b;
                                return await r2.Content.ReadAsStringAsync(ct);
                            }
                            if ((int)r2.StatusCode != 429) // 变成别的错误就交给外层处理
                                throw new HttpRequestException($"HTTP {(int)r2.StatusCode}", null, r2.StatusCode);
                        }
                        // 3 次退避仍 429:抛出,由调用方展示网络提示
                        throw new HttpRequestException($"HTTP 429(限流)", null, resp.StatusCode);
                    }
                    // 其余 4xx 业务错误直接抛出(参数/不存在类错误,重试无意义)
                    if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500)
                        throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (HttpRequestException hre) when (hre.StatusCode.HasValue && (int)hre.StatusCode < 500)
                {
                    throw; // 4xx(含退避后仍 429)不重试不切换端点
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
        string? category = null,
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

        // 主题分类过滤(如 technology/magic/adventure,供泡芙助理按类推荐)
        if (!string.IsNullOrEmpty(category))
            facetGroups.Add(new List<string> { $"\"categories:{category.ToLowerInvariant()}\"" });

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

    /// <summary>获取指定项目的所有版本列表。
    /// game_versions / loaders 参数均按官方要求做 URL 编码(loaders 为 JSON 数组格式)。</summary>
    public async Task<List<ModrinthVersion>> GetProjectVersionsAsync(
        string projectIdOrSlug,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        string relativeUrl = $"/project/{Uri.EscapeDataString(projectIdOrSlug)}/version";
        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(gameVersion))
            queryParams.Add($"game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}");
        if (!string.IsNullOrEmpty(loader))
            queryParams.Add($"loaders={Uri.EscapeDataString($"[\"{loader.ToLowerInvariant()}\"]")}");
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
            var json = await GetJsonAsync($"/version/{Uri.EscapeDataString(versionId)}", ct);
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
        // 2026-09-25 安全加固:filename 来自远端 API JSON,恶意/被篡改响应可带 "../" 或绝对路径,
        // 直接 Path.Combine 会越出模组目录任意写。只取文件名部分,杜绝路径穿越。
        string remoteName = Path.GetFileName(file.Filename);
        if (string.IsNullOrWhiteSpace(remoteName))
        {
            App.WriteAppLog($"[Modrinth] 版本 {version.VersionNumber} 的文件名无效,已跳过");
            return false;
        }
        string destPath = Path.Combine(destDir, remoteName);

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
        // 2026-09-25 改进:递归解析 + visited 去重防环。
        // 旧实现只解析一层直接依赖——嵌套依赖(依赖的依赖)会漏,装上主模组却缺深层前置,
        // 游戏内模组不生效/崩溃且用户无从排查。现在 BFS 递归整条依赖链,
        // visited 按 ProjectId 去重:同一前置被多个模组共享只装一次,环(A→B→A)自动截断。
        var deps = new List<ModrinthVersion>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ModrinthVersion>();
        queue.Enqueue(version);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var cur = queue.Dequeue();
            foreach (var dep in cur.Dependencies)
            {
                if (dep.DependencyType != "required" || string.IsNullOrEmpty(dep.ProjectId)) continue;
                if (!visited.Add(dep.ProjectId)) continue;   // 已解析过(去重/防环)

                ModrinthVersion? depVer = null;
                // 如果指定了 version_id,直接获取该版本
                if (!string.IsNullOrEmpty(dep.VersionId))
                {
                    depVer = await GetVersionAsync(dep.VersionId!, ct);
                }
                // 否则获取该项目的最新版本(匹配 gameVersion + loader)
                if (depVer == null)
                {
                    var versions = await GetProjectVersionsAsync(dep.ProjectId, gameVersion, loader, ct);
                    depVer = versions.FirstOrDefault();
                }
                if (depVer != null)
                {
                    deps.Add(depVer);
                    queue.Enqueue(depVer);   // 递归:依赖的依赖继续解析
                }
            }
        }

        return deps;
    }

    /// <summary>
    /// 一键安装模组:下载主文件 + 自动解析并下载依赖。
    /// 参考主流启动器的一键安装体验。
    /// </summary>
    public async Task<(bool Success, List<string> InstalledFiles, List<string> FailedDeps)> InstallModWithDependenciesAsync(
        ModrinthVersion version,
        string modsDir,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        var installed = new List<string>();
        var failedDeps = new List<string>();

        // 1. 下载主文件
        bool mainOk = await DownloadModVersionAsync(version, modsDir, ct);
        if (!mainOk) return (false, installed, failedDeps);
        var mainFile = version.GetPrimaryFile();
        if (mainFile != null) installed.Add(mainFile.Filename);

        // 2. 解析并下载依赖(递归整链,见 ResolveDependenciesAsync)
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
                // 2026-09-25 改进:依赖失败不再静默——收集返回,调用方明确提示用户缺了哪个前置
                failedDeps.Add($"{dep.Name} {dep.VersionNumber}");
                App.WriteAppLog($"[Modrinth] ⚠ 依赖安装失败:{dep.Name} {dep.VersionNumber}");
            }
        }

        return (true, installed, failedDeps);
    }

    /// <summary>搜索整合包(Modpack)</summary>
    public Task<ModrinthSearchResult> SearchModpacksAsync(
        string query, int offset = 0, int limit = 20,
        string? gameVersion = null, string? loader = null,
        CancellationToken ct = default)
    {
        return SearchAsync(query, offset, limit, gameVersion, loader, "modpack", "relevance", null, ct);
    }

    /// <summary>获取项目基础信息(轻量化:仅用于前置依赖名称提示)</summary>
    public async Task<ModrinthProject?> GetProjectAsync(string projectIdOrSlug, CancellationToken ct = default)
    {
        try
        {
            var json = await GetJsonAsync($"/project/{Uri.EscapeDataString(projectIdOrSlug)}", ct);
            return JsonSerializer.Deserialize<ModrinthProject>(json);
        }
        catch { return null; }
    }

    // ===== 模组图标下载(内存缓存 + 磁盘缓存 + 并发限制) =====

    private static readonly ConcurrentDictionary<string, byte[]> IconCache = new();
    /// <summary>图标下载并发闸门:最多 4 路并发,避免列表初始化瞬间打爆连接/线程</summary>
    private static readonly SemaphoreSlim IconGate = new(4, 4);
    /// <summary>图标磁盘缓存目录(规范 cache 目录内,重启后免重下)</summary>
    private static string IconCacheDir => Path.Combine(AppPaths.Cache, "mod-icons");

    /// <summary>下载模组图标原始字节(内存缓存 → 磁盘缓存 → 网络,限并发,失败返回 null)</summary>
    public static async Task<byte[]?> DownloadIconBytesAsync(string iconUrl)
    {
        if (string.IsNullOrEmpty(iconUrl)) return null;
        if (IconCache.TryGetValue(iconUrl, out var cached)) return cached;

        // 磁盘缓存命中:直接读盘,免网络
        string diskFile = IconDiskFile(iconUrl);
        try
        {
            if (File.Exists(diskFile))
            {
                var disk = await File.ReadAllBytesAsync(diskFile);
                if (disk.Length > 0)
                {
                    if (IconCache.Count < 500) IconCache.TryAdd(iconUrl, disk);
                    return disk;
                }
            }
        }
        catch { /* 缓存损坏走网络 */ }

        await IconGate.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var bytes = await Http.GetByteArrayAsync(iconUrl, cts.Token);
            if (bytes.Length == 0) return null;
            // WebP 图标归一化为 PNG 再入缓存:WIC 无 WebP 解码器,归一化后重复启动直接走原生快路径,免每次 ImageSharp 解码(非 WebP 原样不动)
            bytes = FufuLauncher.Theme.ImageAssets.NormalizeToRenderableBytes(bytes);
            if (IconCache.Count < 500) IconCache.TryAdd(iconUrl, bytes);
            _ = Task.Run(() =>   // 写盘不阻塞 UI
            {
                try
                {
                    Directory.CreateDirectory(IconCacheDir);
                    File.WriteAllBytes(diskFile, bytes);
                }
                catch { /* 写缓存失败不影响本次展示 */ }
            });
            return bytes;
        }
        catch
        {
            return null;
        }
        finally
        {
            IconGate.Release();
        }
    }

    /// <summary>图标磁盘缓存文件名:URL 的 SHA1 十六进制(避免 URL 特殊字符)</summary>
    private static string IconDiskFile(string iconUrl)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(iconUrl));
        return Path.Combine(IconCacheDir, Convert.ToHexString(hash) + ".img");
    }
}
