// Copyright © FufuLauncher
//
// Mojang 版本清单服务:拉取 version_manifest_v2.json(BMCLAPI/Mojang 双源回退),
// 内存 + 磁盘双层缓存(断网/重启秒开),分类 Release/Snapshot/old_beta/old_alpha 查询。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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

    /// <summary>解析 ReleaseTime 为 UTC 时间(失败返回 null)</summary>
    public DateTime? ReleaseTimeUtc
    {
        get
        {
            if (DateTime.TryParse(ReleaseTime, null,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
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

    /// <summary>愚人节/整蛊版本 Id 集合(官方清单中它们的 type 是 snapshot,但实际为节日特别版,
    // 与主流启动器的归类一致:单独成类展示,不混入普通快照)</summary>
    public static readonly HashSet<string> AprilFoolsIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "15w14a",                       // 2015 爱与拥抱
        "1.RV-Pre1",                    // 2016 趋势更新
        "3D Shareware v1.34",           // 2019 3D 共享软件
        "20w14infinite",                // 2020 无限维度(20w14∞)
        "22w13oneblockatatime",         // 2022 一次一个方块
        "23w13a_or_b",                  // 2023 投票更新
        "24w14potato",                  // 2024 毒马铃薯更新
        "25w14craftmine"                // 2025 合成矿车(CraftMine)
    };

    /// <summary>是否愚人节/整蛊特别版</summary>
    public bool IsAprilFools => AprilFoolsIds.Contains(Id);

    /// <summary>是否远古版(Beta/Alpha/Pre-classic,官方清单 old_beta + old_alpha 全量收录)</summary>
    public bool IsAncient => Type == "old_beta" || Type == "old_alpha";

    /// <summary>类型显示名(中文友好,用于标签文字)</summary>
    public string TypeDisplay => IsAprilFools ? "愚人节" : Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type
    };

    /// <summary>类型对应的标签色(ARGB 十六进制字符串):正式蓝/Beta黄/快照紫/远古灰</summary>
    public string TypeBadgeColor => Type switch
    {
        "release" => "#FF2196F3",      // 正式版:蓝色
        "old_beta" => "#FFEAB308",     // Beta 版:黄色
        "snapshot" => "#FFA855F7",     // 快照:紫色
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
    // 超时 20s:国内直连 Mojang 首包常需 5~15s,8s 过紧导致误判失败(2026-09 实测日志多次超时);
    // 配合下方单源失败重试,既保证体验又不让用户干等
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    // 并发拉取去重:多页面同时初始化时只打一次网络
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    /// <summary>最后一次拉取清单失败的异常信息(供 UI 展示)</summary>
    public string LastError { get; private set; } = "";

    public MojangVersionManifest? CachedManifest { get; private set; }

    /// <summary>清单缓存时间戳(供 UI 显示"最后更新于")</summary>
    public DateTime? CachedManifestUtc { get; private set; }

    // ===== 磁盘缓存:重启启动器/断网时免网络秒开 =====
    private static string ManifestCacheFile => Path.Combine(AppPaths.Cache, "version-manifest.json");
    private static readonly TimeSpan DiskCacheTtl = TimeSpan.FromHours(24);

    public VersionManifestService(NetworkService networkService, ConfigService configService)
    {
        _networkService = networkService;
        _configService = configService;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/3.0 (Windows)");
    }

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
                // tmp → Move 原子写:两次拉取并发写盘或中途断电时,读端永远只见完整文件(2026-08-28 全局审计修复)
                string tmp = ManifestCacheFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, ManifestCacheFile, overwrite: true);
            }
            catch { /* 写缓存失败不影响本次结果 */ }
        });
    }

    /// <summary>
    /// 拉取版本清单:内存缓存 → 磁盘缓存 → 首选源 → 回退源 → 过期缓存兜底。
    /// <param name="forceRefresh">true=强制网络拉取并刷新缓存;false=有缓存直接返回(零网络)</param>
    /// </summary>
    public async Task<MojangVersionManifest?> FetchManifestAsync(bool forceRefresh = false)
    {
        // 非强制刷新且有内存缓存:直接返回,杜绝每次切页都打网络
        if (!forceRefresh && CachedManifest != null) return CachedManifest;
        // 非强制刷新且无内存缓存(如重启后首次进入):优先用磁盘缓存,未过期则零网络
        if (!forceRefresh && TryLoadDiskCache()) return CachedManifest;

        await _fetchLock.WaitAsync();
        try
        {
            // 等锁期间可能已有其他调用完成拉取,二次检查避免重复打网络
            if (!forceRefresh && CachedManifest != null) return CachedManifest;

            LastError = "";
            bool preferBmcl = _configService.Config.DownloadSource != "Mojang";   // Auto 默认国内优先
            var sources = new List<(string Label, string Url)>();
            // 2026-09-30:自有镜像排最前(与 BMCLAPI 同路径布局,清单在 /mc/game/ 下),
            // BMCLAPI 与官方紧随其后——自建源还没同步到这个文件时不至于拉不到清单
            string? custom = MirrorUrlMap.NormalizeBase(_configService.Config.CustomDownloadBaseUrl);
            if (_configService.Config.DownloadSource == "Custom" && custom != null)
                sources.Add(("自有镜像", custom + "/mc/game/version_manifest_v2.json"));
            sources.Add(preferBmcl
                ? ("BMCLAPI", NetworkService.BmclapiMetaUrl)
                : ("Mojang", NetworkService.MojangMetaUrl));
            sources.Add(preferBmcl
                ? ("Mojang", NetworkService.MojangMetaUrl)
                : ("BMCLAPI", NetworkService.BmclapiMetaUrl));

            foreach (var (label, url) in sources)
            {
                // 单源最多两次尝试:首次失败(网络抖动/超时)立即重试一次再切源
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        using var resp = await _http.GetAsync(url);
                        resp.EnsureSuccessStatusCode();
                        var json = await resp.Content.ReadAsStringAsync();
                        var fresh = JsonSerializer.Deserialize<MojangVersionManifest>(json);
                        if (fresh != null && fresh.Versions.Count > 0)
                        {
                            CachedManifest = fresh;
                            CachedManifestUtc = DateTime.UtcNow;
                            WriteDiskCache(json);
                            App.WriteAppLog($"[清单] 拉取成功({label}),共 {fresh.Versions.Count} 个版本");
                            return CachedManifest;
                        }
                        LastError += $"\n{label} 返回空清单";
                    }
                    catch (Exception ex)
                    {
                        LastError += $"\n{label} 源失败:{ex.Message}";
                        App.WriteAppLog($"[清单] {label} 源拉取失败(第{attempt}次):{ex.Message}");
                    }
                    if (attempt < 2)
                        App.WriteAppLog($"[清单] {label} 源重试中(1/1)…");
                }
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
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>当前启用的自有镜像根地址:下载源不是「自有镜像」或地址无效时返回 null</summary>
    private string? CustomBase()
        => _configService.Config.DownloadSource == "Custom"
            ? MirrorUrlMap.NormalizeBase(_configService.Config.CustomDownloadBaseUrl)
            : null;

    /// <summary>拉取单个版本的 version json:当前源 → BMCLAPI → 官方依次兜底,全失败记日志返回 null</summary>
    public async Task<MojangVersionJson?> FetchVersionJsonAsync(string versionUrl)
    {
        // 2026-09-30:改成多源依次尝试。原来只按当前源拼一个 URL 打一次,
        // 自有镜像 / BMCLAPI 尚未同步到该版本(冷门版、刚发布的快照)时直接判失败,
        // 而官方源其实是有的——白丢一次装机。
        var candidates = new List<string>();
        void Add(string u) { if (!candidates.Contains(u)) candidates.Add(u); }
        string src = _configService.Config.DownloadSource;
        string? custom = CustomBase();
        if (custom != null) Add(MirrorUrlMap.ToCustom(versionUrl, custom));
        if (src != "Mojang") Add(MirrorUrlMap.ToBmclapi(versionUrl));
        Add(versionUrl);

        foreach (string url in candidates)
        {
            try
            {
                using var resp = await _http.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync();
                var parsed = JsonSerializer.Deserialize<MojangVersionJson>(json);
                if (parsed != null)
                {
                    if (url != candidates[0])
                        App.WriteAppLog($"[清单] 版本 json 已回退到 {url}");
                    return parsed;
                }
                LastError += "\n版本 json 解析为空";
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[清单] 版本 json 拉取失败 {url}:{ex.Message}");
            }
        }
        return null;
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
            q = q.Where(v => v.Id.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }
        q = sortByReleaseDesc
            ? q.OrderByDescending(v => v.ReleaseTimeUtc ?? DateTime.MinValue)
            : q.OrderByDescending(v => v.Id, StringComparer.OrdinalIgnoreCase);
        return q.ToList();
    }

    /// <summary>跨类型关键词搜索(用于"全部"标签页)</summary>
    public List<MojangVersion> SearchAll(string? keyword, bool sortByReleaseDesc = true) =>
        Search(null, keyword, sortByReleaseDesc);

    /// <summary>判断指定版本号是否已安装(供 UI 显示"已安装"徽章)。
    /// 双判据:任一游戏版本实例引用该版本,或 versions\{id} 目录内已有版本 json/jar 本体。</summary>
    public bool IsVersionInstalled(string versionId, InstanceService instanceService)
    {
        foreach (var inst in instanceService.Instances)
        {
            if (string.Equals(inst.VersionId, versionId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // 第二判据:版本本体目录(共享目录,可能无实例引用但本体已就绪)
        try
        {
            string verDir = Path.Combine(AppPaths.Versions, versionId);
            if (Directory.Exists(verDir) &&
                (File.Exists(Path.Combine(verDir, versionId + ".json")) ||
                 File.Exists(Path.Combine(verDir, versionId + ".jar"))))
                return true;
        }
        catch { /* 路径异常不影响主判定 */ }
        return false;
    }
}
