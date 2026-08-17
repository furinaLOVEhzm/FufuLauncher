// VersionManifestService.cs — Mojang 版本清单服务
// 可爱的芙芙 - 阶段2 模块
//
// 拉取 version_manifest_v2.json,分类 Release / Snapshot / old_beta / old_alpha
// 提供版本列表查询

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

public class MojangVersionManifest
{
    [JsonPropertyName("latest")] public LatestVersion Latest { get; set; } = new();
    [JsonPropertyName("versions")] public List<MojangVersion> Versions { get; set; } = new();
}

public class LatestVersion
{
    [JsonPropertyName("release")] public string Release { get; set; } = "";
    [JsonPropertyName("snapshot")] public string Snapshot { get; set; } = "";
}

public class MojangVersion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";       // release / snapshot / old_beta / old_alpha
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("releaseTime")] public string ReleaseTime { get; set; } = "";

    /// <summary>解析 ReleaseTime 为本地时间(失败返回 null)</summary>
    public DateTime? ReleaseTimeUtc
    {
        get
        {
            if (DateTime.TryParse(ReleaseTime, null,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var dt))
                return dt;
            return null;
        }
    }

    /// <summary>可读的发布时间字符串(YYYY-MM-DD),解析失败返回原字符串</summary>
    public string ReleaseTimeDisplay
    {
        get
        {
            var dt = ReleaseTimeUtc;
            return dt.HasValue ? dt.Value.ToString("yyyy-MM-dd") : ReleaseTime;
        }
    }

    /// <summary>类型显示名(中文友好,用于左侧色块标签文字)</summary>
    public string TypeDisplay => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "Beta",
        "old_alpha" => "远古版",
        _ => Type
    };

    /// <summary>类型对应的标签色(ARGB 十六进制字符串):正式蓝/Beta黄/快照紫/远古灰</summary>
    public string TypeBadgeColor => Type switch
    {
        "release" => "#FF2196F3",      // 正式版:蓝色
        "old_beta" => "#FFEAB308",     // Beta 版:黄色
        "snapshot" => "#FFA855F7",     // 测试版(快照):紫色
        "old_alpha" => "#FF6B7280",    // 远古旧版本:灰色
        _ => "#FF9E9E9E"
    };
}

public class MojangVersionJson
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("mainClass")] public string MainClass { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("releaseTime")] public string ReleaseTime { get; set; } = "";
    [JsonPropertyName("minecraftArguments")] public string MinecraftArguments { get; set; } = "";
    [JsonPropertyName("arguments")] public VersionArguments? Arguments { get; set; }
    [JsonPropertyName("libraries")] public List<MojangLibrary> Libraries { get; set; } = new();
    [JsonPropertyName("assetIndex")] public AssetIndexRef? AssetIndex { get; set; }
    [JsonPropertyName("assets")] public string Assets { get; set; } = "";
    [JsonPropertyName("downloads")] public VersionDownloads? Downloads { get; set; }
    [JsonPropertyName("logging")] public JsonElement Logging { get; set; }
    [JsonPropertyName("javaVersion")] public JavaVersionRef? JavaVersion { get; set; }
    [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
    [JsonPropertyName("jar")] public string? Jar { get; set; }
}

public class VersionArguments
{
    [JsonPropertyName("game")] public List<JsonElement> Game { get; set; } = new();
    [JsonPropertyName("jvm")] public List<JsonElement> Jvm { get; set; } = new();
}

public class MojangLibrary
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("rules")] public List<LibraryRule>? Rules { get; set; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }
    [JsonPropertyName("extract")] public LibraryExtract? Extract { get; set; }
}

public class LibraryDownloads
{
    [JsonPropertyName("artifact")] public LibraryArtifact? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, LibraryArtifact>? Classifiers { get; set; }
}

public class LibraryArtifact
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public class LibraryRule
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("os")] public OsRule? Os { get; set; }
}

public class OsRule
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public class LibraryExtract
{
    [JsonPropertyName("exclude")] public List<string> Exclude { get; set; } = new();
}

public class AssetIndexRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public class VersionDownloads
{
    [JsonPropertyName("client")] public VersionDownload? Client { get; set; }
    [JsonPropertyName("server")] public VersionDownload? Server { get; set; }
}

public class VersionDownload
{
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public class JavaVersionRef
{
    [JsonPropertyName("component")] public string Component { get; set; } = "";
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
}

public class VersionManifestService
{
    private readonly NetworkService _networkService;
    private readonly ConfigService _configService;
    // 超时 8s:国内直连 Mojang 必超时,快速失败回退另一源,避免用户卡死 2 分钟
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>最后一次拉取清单失败的异常信息(供 UI 展示)</summary>
    public string LastError { get; private set; } = "";

    public MojangVersionManifest? CachedManifest { get; private set; }

    public VersionManifestService(NetworkService networkService, ConfigService configService)
    {
        _networkService = networkService;
        _configService = configService;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/1.0.5.1 (Windows)");
    }

    /// <summary>清单缓存时间戳(供 UI 显示"最后更新于")</summary>
    public DateTime? CachedManifestUtc { get; private set; }

    // ===== 磁盘缓存:重启启动器/断网时免网络秒开(借鉴主流启动器本地缓存策略)=====
    private static string ManifestCacheFile => Path.Combine(AppPaths.Cache, "version-manifest.json");
    private static readonly TimeSpan DiskCacheTtl = TimeSpan.FromHours(24);

    /// <summary>清空缓存(切换下载源后调用,确保下次拉取走新源)</summary>
    public void ClearCache()
    {
        CachedManifest = null;
        CachedManifestUtc = null;
    }

    /// <summary>尝试从磁盘缓存读清单(未过期才用),成功同时回填内存缓存</summary>
    private bool TryLoadDiskCache()
    {
        try
        {
            string file = ManifestCacheFile;
            if (!File.Exists(file)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > DiskCacheTtl) return false;
            var cached = JsonSerializer.Deserialize<MojangVersionManifest>(File.ReadAllText(file));
            if (cached == null || cached.Versions.Count == 0) return false;
            CachedManifest = cached;
            CachedManifestUtc = File.GetLastWriteTimeUtc(file);
            App.WriteAppLog($"[清单] 命中磁盘缓存(缓存于 {CachedManifestUtc.Value.ToLocalTime():MM-dd HH:mm}),免网络");
            return true;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[清单] 磁盘缓存读取失败,走网络:{ex.Message}");
            return false;
        }
    }

    /// <summary>网络拉取成功后把原始 JSON 写入磁盘缓存(后台写盘不阻塞主流程)</summary>
    private static void WriteDiskCache(string json)
    {
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Cache);
                File.WriteAllText(ManifestCacheFile, json);
            }
            catch { /* 写缓存失败不影响本次结果 */ }
        });
    }

    /// <summary>
    /// 根据当前下载源设置选取首选清单 URL,失败时回退到另一源。
    /// <param name="forceRefresh">true=强制网络拉取并刷新缓存;false=有缓存直接返回(零网络)</param>
    /// </summary>
    public async Task<MojangVersionManifest?> FetchManifestAsync(bool forceRefresh = false)
    {
        // 非强制刷新且有内存缓存:直接返回,杜绝每次切页都打网络
        if (!forceRefresh && CachedManifest != null) return CachedManifest;
        // 非强制刷新且无内存缓存(如重启后首次进入):优先用磁盘缓存,未过期则零网络
        if (!forceRefresh && TryLoadDiskCache()) return CachedManifest;

        LastError = "";
        bool preferBmcl = _configService.Config.DownloadSource == "BMCLAPI";
        string firstUrl = preferBmcl ? NetworkService.BmclapiMetaUrl : NetworkService.MojangMetaUrl;
        string fallbackUrl = preferBmcl ? NetworkService.MojangMetaUrl : NetworkService.BmclapiMetaUrl;

        // 首选源
        try
        {
            using var resp = await _http.GetAsync(firstUrl);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            var fresh = JsonSerializer.Deserialize<MojangVersionManifest>(json);
            if (fresh != null)
            {
                CachedManifest = fresh;
                CachedManifestUtc = DateTime.UtcNow;
                WriteDiskCache(json);
                return CachedManifest;
            }
        }
        catch (Exception ex)
        {
            LastError = $"首选源({(preferBmcl ? "BMCLAPI" : "Mojang")})失败:{ex.Message}";
        }

        // 回退到另一源
        try
        {
            using var resp = await _http.GetAsync(fallbackUrl);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            var fresh = JsonSerializer.Deserialize<MojangVersionManifest>(json);
            if (fresh != null)
            {
                CachedManifest = fresh;
                CachedManifestUtc = DateTime.UtcNow;
                WriteDiskCache(json);
                return CachedManifest;
            }
        }
        catch (Exception ex)
        {
            LastError += $"\n回退源({(preferBmcl ? "Mojang" : "BMCLAPI")})也失败:{ex.Message}";
        }

        // 网络全失败:旧缓存/过期磁盘缓存都优于空白(失败不阻塞 UI,使用旧数据 + 警告)
        if (CachedManifest != null) return CachedManifest;
        try
        {
            string file = ManifestCacheFile;
            if (File.Exists(file))
            {
                var stale = JsonSerializer.Deserialize<MojangVersionManifest>(File.ReadAllText(file));
                if (stale != null && stale.Versions.Count > 0)
                {
                    CachedManifest = stale;
                    CachedManifestUtc = File.GetLastWriteTimeUtc(file);
                    LastError += "\n已回退使用过期的本地缓存清单";
                    return CachedManifest;
                }
            }
        }
        catch { /* 过期缓存也读不出来才返回 null */ }
        return null;
    }

    public async Task<MojangVersionJson?> FetchVersionJsonAsync(string versionUrl)
    {
        // 通过 DownloadService.GetSourceUrl 同款规则重写 URL(随下载源切换)
        string url = RewriteUrl(versionUrl);
        try
        {
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<MojangVersionJson>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>同 DownloadService.GetSourceUrl 的 URL 重写规则,保持各模块一致</summary>
    private string RewriteUrl(string originalUrl)
    {
        if (_configService.Config.DownloadSource != "BMCLAPI") return originalUrl;
        if (originalUrl.Contains("piston-meta.mojang.com") ||
            originalUrl.Contains("launchermeta.mojang.com") ||
            originalUrl.Contains("piston-data.mojang.com") ||
            originalUrl.Contains("libraries.minecraft.net") ||
            originalUrl.Contains("resources.download.minecraft.net"))
        {
            return originalUrl
                .Replace("piston-meta.mojang.com", "bmclapi2.bangbang93.com")
                .Replace("launchermeta.mojang.com", "bmclapi2.bangbang93.com")
                .Replace("piston-data.mojang.com", "bmclapi2.bangbang93.com")
                .Replace("libraries.minecraft.net", "bmclapi2.bangbang93.com/maven")
                .Replace("resources.download.minecraft.net", "bmclapi2.bangbang93.com");
        }
        return originalUrl;
    }

    public List<MojangVersion> FilterByType(string type) =>
        CachedManifest?.Versions.FindAll(v => v.Type == type) ?? new();

    /// <summary>关键词搜索:在指定类型范围内按 Id 模糊匹配(大小写不敏感)</summary>
    /// <param name="type">类型过滤,传 null/空 表示全部类型</param>
    /// <param name="keyword">关键词,空则返回该类型全部</param>
    /// <param name="sortByReleaseDesc">true=按发布时间倒序(最新在前),false=按 Id 倒序</param>
    public List<MojangVersion> Search(string? type, string? keyword, bool sortByReleaseDesc = true)
    {
        if (CachedManifest == null) return new();
        IEnumerable<MojangVersion> q = CachedManifest.Versions;
        if (!string.IsNullOrEmpty(type))
            q = q.Where(v => v.Type == type);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            string kw = keyword.Trim();
            q = q.Where(v => v.Id.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        if (sortByReleaseDesc)
        {
            q = q.OrderByDescending(v => v.ReleaseTimeUtc ?? DateTime.MinValue);
        }
        else
        {
            q = q.OrderByDescending(v => v.Id, StringComparer.OrdinalIgnoreCase);
        }
        return q.ToList();
    }

    /// <summary>跨类型关键词搜索(用于"全部"标签页)</summary>
    public List<MojangVersion> SearchAll(string? keyword, bool sortByReleaseDesc = true) =>
        Search(null, keyword, sortByReleaseDesc);

    /// <summary>判断指定版本号是否已安装在任一实例中(供 UI 显示"已安装"徽章)</summary>
    public bool IsVersionInstalled(string versionId, InstanceService instanceService)
    {
        foreach (var inst in instanceService.Instances)
        {
            if (string.Equals(inst.VersionId, versionId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
