// Copyright © FufuLauncher
//
// 模组加载器版本列表拉取服务:按「加载器 + 游戏版本」动态拉取可选版本,区分正式版/测试版。
// 源策略:本地缓存 → 国内镜像(短超时) → 官方源兜底(长超时),全程异步不阻塞 UI。
// 数据源对齐主流启动器的通行做法:
//   Fabric   → BMCLAPI 国内镜像(fabric-meta),回退 Fabric 官方 meta
//   Forge    → BMCLAPI 国内镜像(/forge/minecraft/{mc}),回退 Forge 官方 maven-metadata 全量清单
//   NeoForge → BMCLAPI maven 镜像,回退 NeoForge 官方 maven(maven-metadata.xml)
//   Quilt    → BMCLAPI 预留路径,回退 Quilt 官方 meta(国内暂无镜像接口)
//   OptiFine → BMCLAPI 国内镜像(/optifine/{mc})(OptiFine 官方无公开 API,
//             主流启动器同样只走 BMCLAPI 爬取缓存,故无官方兜底)
// 拉取成功的原始数据写入 缓存\loader-meta 目录,切换游戏版本时秒开
// (NeoForge 元数据为全量列表,缓存后任意版本免网络)。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

/// <summary>单个加载器版本条目</summary>
public class LoaderVersionEntry
{
    /// <summary>版本号(传给安装服务的原始值)</summary>
    public string Version { get; set; } = "";
    /// <summary>是否正式版(false = 测试/预览版)</summary>
    public bool IsStable { get; set; } = true;
}

/// <summary>版本列表拉取结果</summary>
public class LoaderVersionListResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<LoaderVersionEntry> Versions { get; set; } = new();
    /// <summary>实际生效的数据源(展示给用户,如「BMCLAPI 国内镜像」「本地缓存」)</summary>
    public string SourceLabel { get; set; } = "";

    public static LoaderVersionListResult Ok(List<LoaderVersionEntry> list, string source) =>
        new() { Success = true, Versions = list, SourceLabel = source };
    public static LoaderVersionListResult Fail(string msg) => new() { Success = false, Error = msg };
}

public class LoaderVersionProvider
{
    private readonly HttpClient _http = new();
    private readonly ConfigService _configService;

    // ===== 游戏版本 ↔ 加载器 兼容性静态规则(选加载器前先过滤错误组合) =====
    // NeoForge:仅支持 MC 1.20.1 及以上;Quilt:仅支持 1.18 及以上;
    // Forge/Fabric/OptiFine 覆盖范围广,交给镜像返回结果判定(无适配时行内显示"暂无适配")。
    // 快照/远古版本号无法解析时一律放行,由拉取结果兜底。

    /// <summary>判断指定加载器是否支持该游戏版本(不支持时给出中文原因)</summary>
    public static bool IsLoaderSupportedFor(string loaderKey, string gameVersion, out string reason)
    {
        reason = "";
        var ver = ParseReleaseVersion(gameVersion);
        if (ver == null) return true;   // 快照/测试版无法静态判定,放行后由拉取结果兜底
        var (major, minor, patch) = ver.Value;
        switch ((loaderKey ?? "").Trim().ToLowerInvariant())
        {
            case "neoforge":
                // NeoForge 首个支持版本为 1.20.1
                if (major < 1 || (major == 1 && (minor < 20 || (minor == 20 && patch < 1))))
                {
                    reason = "NeoForge 仅支持 Minecraft 1.20.1 及以上版本";
                    return false;
                }
                return true;
            case "quilt":
                if (major < 1 || (major == 1 && minor < 18))
                {
                    reason = "Quilt 仅支持 Minecraft 1.18 及以上版本";
                    return false;
                }
                return true;
            default:
                return true;
        }
    }

    /// <summary>解析正式版版本号 "1.20.1" → (1,20,1);非正式版格式(快照/旧版命名)返回 null</summary>
    private static (int Major, int Minor, int Patch)? ParseReleaseVersion(string gameVersion)
    {
        var m = Regex.Match(gameVersion ?? "", @"^(\d+)\.(\d+)(?:\.(\d+))?$");
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    // ===== 缓存(内存 + 磁盘):避免切换游戏版本反复请求慢源 =====
    private static readonly Dictionary<string, (DateTime Utc, string Content)> MemCache = new();
    private static readonly object CacheLock = new();
    private static string CacheDir => Path.Combine(AppPaths.Cache, "loader-meta");

    private static readonly TimeSpan TtlLong = TimeSpan.FromHours(24);   // NeoForge 全量元数据
    private static readonly TimeSpan TtlShort = TimeSpan.FromHours(12);  // 其余按游戏版本的列表

    public LoaderVersionProvider(ConfigService configService)
    {
        _configService = configService;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/3.0 (Windows)");
        _http.Timeout = Timeout.InfiniteTimeSpan;   // 超时由每个请求的 CancellationToken 单独控制
    }

    /// <summary>当前启用的自有镜像根地址:下载源不是「自有镜像」或地址无效时返回 null</summary>
    private string? CustomBase()
        => _configService.Config.DownloadSource == "Custom"
            ? MirrorUrlMap.NormalizeBase(_configService.Config.CustomDownloadBaseUrl)
            : null;

    /// <summary>按加载器类型拉取指定游戏版本可用的加载器版本列表</summary>
    public Task<LoaderVersionListResult> GetVersionsAsync(string loaderKey, string gameVersion)
    {
        return (loaderKey ?? "").Trim().ToLowerInvariant() switch
        {
            "fabric" => GetFabricAsync(gameVersion),
            "forge" => GetForgeAsync(gameVersion),
            "neoforge" => GetNeoForgeAsync(gameVersion),
            "quilt" => GetQuiltAsync(gameVersion),
            "optifine" => GetOptiFineAsync(gameVersion),
            _ => Task.FromResult(LoaderVersionListResult.Fail($"不支持的加载器类型: {loaderKey}"))
        };
    }

    // ==================== 通用拉取:缓存 → 镜像 → 官方兜底 ====================

    /// <summary>
    /// 依次尝试各数据源(国内镜像在前,官方在后),每个源独立短超时,任一成功即返回并写缓存。
    /// 缓存命中直接返回,不再走网络。
    /// </summary>
    private async Task<(string Content, string Label, bool NotFound)?> FetchFirstAsync(
        string cacheKey, TimeSpan cacheTtl,
        (string Url, string Label, int TimeoutSec)[] sources)
    {
        // 2026-09-30:接入自有镜像。镜像按 BMCLAPI 的目录结构同步,所以每条镜像源前面插一条
        // 「同路径换根」的候选即可,不需要第二套路径映射;自建源还没同步到该文件时自动落到后面各源。
        // 缓存键也要按源分开:否则切到自有镜像后仍命中上一次 BMCLAPI 的结果,新源根本没被试过。
        string? custom = CustomBase();
        if (custom != null) cacheKey = "own-" + cacheKey;
        var candidates = new List<(string Url, string Label, int TimeoutSec)>(sources.Length + 2);
        foreach (var s in sources)
        {
            if (custom != null && s.Url.StartsWith(MirrorUrlMap.BmclapiRoot, StringComparison.OrdinalIgnoreCase))
                candidates.Add((custom + s.Url.Substring(MirrorUrlMap.BmclapiRoot.Length),
                                "自有镜像", Math.Max(s.TimeoutSec, 8)));
            candidates.Add(s);
        }
        var sourceList = custom != null ? candidates.ToArray() : sources;

        bool notFound = false;
        // 1) 内存缓存
        lock (CacheLock)
        {
            if (MemCache.TryGetValue(cacheKey, out var m) && DateTime.UtcNow - m.Utc < cacheTtl)
                return (m.Content, "本地缓存", false);
        }
        // 2) 磁盘缓存
        try
        {
            string file = CacheFile(cacheKey);
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < cacheTtl)
            {
                string cached = await File.ReadAllTextAsync(file);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    lock (CacheLock) MemCache[cacheKey] = (DateTime.UtcNow, cached);
                    return (cached, "本地缓存", false);
                }
            }
        }
        catch { /* 缓存损坏忽略,走网络 */ }

        // 3) 网络:镜像优先,官方兜底;每源独立超时,失败快速切换下一源
        // 2026-09-25:记录是否 404(版本不支持)——Fabric/Quilt/NeoForge 对过老版本返回 404,
        // 与网络故障区分开,给用户"该版本暂无支持"而不是误导性的"网络不可达"
        foreach (var (url, label, timeoutSec) in sourceList)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    App.WriteAppLog($"[加载器] 源 {label} 返回 {(int)resp.StatusCode},尝试下一源");
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) notFound = true;
                    continue;
                }
                string content = await resp.Content.ReadAsStringAsync(cts.Token);
                if (string.IsNullOrWhiteSpace(content)) continue;

                PutCache(cacheKey, content);
                return (content, label, false);
            }
            catch (OperationCanceledException)
            {
                App.WriteAppLog($"[加载器] 源 {label} 超时({timeoutSec}s),尝试下一源");
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[加载器] 源 {label} 获取失败: {ex.Message},尝试下一源");
            }
        }
        // 2026-09-25:全源失败也回传是否 404(版本不支持)——与网络故障区分
        return (null!, null!, notFound);
    }

    /// <summary>内存 + 磁盘双写缓存(磁盘后台写,失败不影响本次结果)</summary>
    private static void PutCache(string cacheKey, string content)
    {
        lock (CacheLock) MemCache[cacheKey] = (DateTime.UtcNow, content);
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string file = CacheFile(cacheKey);
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, content);
                File.Move(tmp, file, overwrite: true);
            }
            catch { /* 写缓存失败不影响本次结果 */ }
        });
    }

    private static string CacheFile(string key) => Path.Combine(CacheDir, key);

    // ==================== Fabric ====================

    private class FabricMetaItem
    {
        [JsonPropertyName("loader")] public FabricLoaderInfo Loader { get; set; } = new();
    }
    private class FabricLoaderInfo
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("stable")] public bool Stable { get; set; }
    }

    private async Task<LoaderVersionListResult> GetFabricAsync(string mc)
    {
        var fetched = await FetchFirstAsync($"fabric-{mc}.json", TtlShort, new[]
        {
            ($"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{mc}", "BMCLAPI 国内镜像", 6),
            ($"https://meta.fabricmc.net/v2/versions/loader/{mc}", "Fabric 官方源", 12)
        });
        bool fetchedNotFound = fetched != null && fetched.Value.Item3;
        if (fetched == null || fetched.Value.Item1 == null)
            return LoaderVersionListResult.Fail(fetchedNotFound
                ? "该游戏版本暂无 Fabric 支持(Fabric 一般支持 1.14 及以上的版本)"
                : "获取版本失败,镜像与官方源均不可达,请点击重试");

        try
        {
            var list = JsonSerializer.Deserialize<List<FabricMetaItem>>(fetched.Value.Item1);
            var entries = (list ?? new List<FabricMetaItem>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Loader.Version))
                .Select(x => new LoaderVersionEntry { Version = x.Loader.Version, IsStable = x.Loader.Stable })
                .ToList();
            entries.Sort((a, b) => CompareVersionDesc(a.Version, b.Version));
            return LoaderVersionListResult.Ok(entries, fetched.Value.Item2);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] Fabric 列表解析失败: {ex.Message}");
            return LoaderVersionListResult.Fail("获取版本失败,返回数据异常,请点击重试");
        }
    }

    // ==================== Forge ====================

    private class ForgeBuildItem
    {
        [JsonPropertyName("branch")] public string? Branch { get; set; }
        [JsonPropertyName("build")] public int Build { get; set; }
        [JsonPropertyName("mcversion")] public string McVersion { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
    }

    private async Task<LoaderVersionListResult> GetForgeAsync(string mc)
    {
        // 双源:BMCLAPI 按游戏版本整理好的 JSON(首选);Forge 官方 maven-metadata 全量 XML 兜底
        // (主流启动器同款兜底策略,官方源清单巨大,仅在镜像不可达时拉取)
        var fetched = await FetchFirstAsync($"forge-{mc}.json", TtlShort, new[]
        {
            ($"https://bmclapi2.bangbang93.com/forge/minecraft/{mc}", "BMCLAPI 国内镜像", 8),
            ($"https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", "Forge 官方源", 12)
        });
        if (fetched == null)
            return LoaderVersionListResult.Fail("获取版本失败,镜像与官方源均不可达,请点击重试");

        try
        {
            var entries = new List<LoaderVersionEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string content = fetched.Value.Item1.TrimStart();

            void AddEntry(string ver)
            {
                if (string.IsNullOrWhiteSpace(ver) || !seen.Add(ver)) return;
                // 2026-09-25 修复:官方 metadata 老 Forge(≤1.7.10)版本号自带尾部 "-{mc}" 后缀
                // (如 1.7.10-10.13.4.1614-1.7.10),去掉后与 BMCLAPI JSON 纯版本号格式统一,
                // 否则列表里出现两种格式、下载 URL 拼接双重后缀直接 404
                if (ver.EndsWith("-" + mc, StringComparison.Ordinal))
                    ver = ver[..^("-" + mc).Length];
                if (string.IsNullOrWhiteSpace(ver) || !seen.Add(ver)) return;
                entries.Add(new LoaderVersionEntry
                {
                    Version = ver,
                    IsStable = !Regex.IsMatch(ver, "beta|alpha|pre|rc", RegexOptions.IgnoreCase)
                });
            }

            if (content.StartsWith("<"))
            {
                // Forge 官方 maven-metadata.xml:全量版本列表,按「游戏版本-」前缀筛选并去掉前缀
                string prefix = mc + "-";
                foreach (Match m in Regex.Matches(content, @"<version>([^<]+)</version>"))
                {
                    string v = m.Groups[1].Value.Trim();
                    if (!v.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    AddEntry(v.Substring(prefix.Length));
                }
            }
            else
            {
                // BMCLAPI JSON(已按游戏版本整理)
                var list = JsonSerializer.Deserialize<List<ForgeBuildItem>>(content);
                foreach (var b in list ?? new List<ForgeBuildItem>())
                {
                    if (string.IsNullOrWhiteSpace(b.Version)) continue;
                    // 过滤掉早期以游戏版本开头的怪异条目(如 "1.6.1-xxx"),仅保留纯版本号
                    if (b.Version.StartsWith(mc, StringComparison.Ordinal)) continue;
                    AddEntry(b.Version);
                }
            }
            entries.Sort((a, b) => CompareVersionDesc(a.Version, b.Version));
            return LoaderVersionListResult.Ok(entries, fetched.Value.Item2);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] Forge 列表解析失败: {ex.Message}");
            return LoaderVersionListResult.Fail("获取版本失败,返回数据异常,请点击重试");
        }
    }

    // ==================== NeoForge ====================

    private async Task<LoaderVersionListResult> GetNeoForgeAsync(string mc)
    {
        // MC 1.20.1 的 NeoForge 发布在 net/neoforged/forge 工件下(版本号形如 1.20.1-47.1.x)
        bool is1201 = mc == "1.20.1";
        string artifact = is1201 ? "forge" : "neoforge";

        // maven-metadata.xml 是全量版本列表:缓存后切换任意游戏版本都免网络
        var fetched = await FetchFirstAsync($"neoforge-{artifact}.xml", TtlLong, new[]
        {
            ($"https://bmclapi2.bangbang93.com/maven/net/neoforged/{artifact}/maven-metadata.xml", "BMCLAPI 国内镜像", 6),
            ($"https://maven.neoforged.net/releases/net/neoforged/{artifact}/maven-metadata.xml", "NeoForge 官方源", 12)
        });
        bool fetchedNotFound = fetched != null && fetched.Value.Item3;
        if (fetched == null || fetched.Value.Item1 == null)
            return LoaderVersionListResult.Fail(fetchedNotFound
                ? "该游戏版本暂无 NeoForge 支持(NeoForge 一般支持 1.20.1 及以上的版本)"
                : "获取版本失败,镜像与官方源均不可达,请点击重试");

        try
        {
            var matches = Regex.Matches(fetched.Value.Item1, @"<version>([^<]+)</version>");
            string prefix = is1201 ? "1.20.1-" : NeoForgePrefixFor(mc);
            var entries = new List<LoaderVersionEntry>();
            foreach (Match m in matches)
            {
                string v = m.Groups[1].Value.Trim();
                if (!v.StartsWith(prefix, StringComparison.Ordinal)) continue;
                entries.Add(new LoaderVersionEntry
                {
                    Version = v,
                    IsStable = !Regex.IsMatch(v, "beta|alpha|rc", RegexOptions.IgnoreCase)
                });
            }
            entries.Sort((a, b) => CompareVersionDesc(a.Version, b.Version));
            return LoaderVersionListResult.Ok(entries, fetched.Value.Item2);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] NeoForge 列表解析失败: {ex.Message}");
            return LoaderVersionListResult.Fail("获取版本失败,返回数据异常,请点击重试");
        }
    }

    /// <summary>NeoForge 版本号与 MC 版本的对应前缀:1.21.1 → "21.1.",1.21 → "21.0."</summary>
    private static string NeoForgePrefixFor(string mc)
    {
        var parts = mc.Split('.');
        if (parts.Length < 2) return mc + ".";
        string major = parts[1];
        string minor = parts.Length >= 3 ? parts[2] : "0";
        return $"{major}.{minor}.";
    }

    // ==================== Quilt ====================

    private async Task<LoaderVersionListResult> GetQuiltAsync(string mc)
    {
        // 国内暂无 Quilt meta 镜像;前置 BMCLAPI 预留路径(404 会快速跳过),官方源兜底
        var fetched = await FetchFirstAsync($"quilt-{mc}.json", TtlShort, new[]
        {
            ($"https://bmclapi2.bangbang93.com/quilt-meta/v3/versions/loader/{mc}", "BMCLAPI 国内镜像", 4),
            ($"https://meta.quiltmc.org/v3/versions/loader/{mc}", "Quilt 官方源", 15)
        });
        bool fetchedNotFound = fetched != null && fetched.Value.Item3;
        if (fetched == null || fetched.Value.Item1 == null)
            return LoaderVersionListResult.Fail(fetchedNotFound
                ? "该游戏版本暂无 Quilt 支持(Quilt 一般支持 1.16.2 及以上的版本)"
                : "获取版本失败,无法连接 Quilt 版本源,请点击重试");

        try
        {
            var list = JsonSerializer.Deserialize<List<FabricMetaItem>>(fetched.Value.Item1);
            var entries = (list ?? new List<FabricMetaItem>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Loader.Version))
                .Select(x => new LoaderVersionEntry
                {
                    Version = x.Loader.Version,
                    IsStable = x.Loader.Stable && !Regex.IsMatch(x.Loader.Version, "beta|alpha|rc", RegexOptions.IgnoreCase)
                })
                .ToList();
            entries.Sort((a, b) => CompareVersionDesc(a.Version, b.Version));
            return LoaderVersionListResult.Ok(entries, fetched.Value.Item2);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] Quilt 列表解析失败: {ex.Message}");
            return LoaderVersionListResult.Fail("获取版本失败,返回数据异常,请点击重试");
        }
    }

    // ==================== OptiFine ====================

    private class OptiFineItem
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("patch")] public string Patch { get; set; } = "";
    }

    private async Task<LoaderVersionListResult> GetOptiFineAsync(string mc)
    {
        var fetched = await FetchFirstAsync($"optifine-{mc}.json", TtlShort, new[]
        {
            ($"https://bmclapi2.bangbang93.com/optifine/{mc}", "BMCLAPI 国内镜像", 8)
        });
        bool fetchedNotFound = fetched != null && fetched.Value.Item3;
        if (fetched == null || fetched.Value.Item1 == null)
            return LoaderVersionListResult.Fail(fetchedNotFound
                ? "该游戏版本暂无 OptiFine 适配(OptiFine 一般支持 1.8.9 及以上的版本)"
                : "获取版本失败,无法连接 OptiFine 镜像源,请点击重试");

        try
        {
            var list = JsonSerializer.Deserialize<List<OptiFineItem>>(fetched.Value.Item1);
            var entries = new List<LoaderVersionEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in list ?? new List<OptiFineItem>())
            {
                if (string.IsNullOrWhiteSpace(o.Type) || string.IsNullOrWhiteSpace(o.Patch)) continue;
                string ver = $"{o.Type}_{o.Patch}";
                if (!seen.Add(ver)) continue;
                entries.Add(new LoaderVersionEntry
                {
                    Version = ver,
                    IsStable = !o.Type.Contains("pre", StringComparison.OrdinalIgnoreCase)
                });
            }
            // OptiFine 镜像无明确时间序,保持镜像原顺序(通常按发布时间)
            return LoaderVersionListResult.Ok(entries, fetched.Value.Item2);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[加载器] OptiFine 列表解析失败: {ex.Message}");
            return LoaderVersionListResult.Fail("获取版本失败,返回数据异常,请点击重试");
        }
    }

    // ==================== 版本号降序比较 ====================

    /// <summary>版本号降序比较:按 . - + 分段逐段比较,纯数字段按数值比,其余按字符串比</summary>
    public static int CompareVersionDesc(string a, string b)
    {
        var pa = Regex.Split(a, @"[.\-+]");
        var pb = Regex.Split(b, @"[.\-+]");
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            string sa = i < pa.Length ? pa[i] : "";
            string sb = i < pb.Length ? pb[i] : "";
            bool na = long.TryParse(sa, out long va);
            bool nb = long.TryParse(sb, out long vb);
            int c;
            if (na && nb) c = va.CompareTo(vb);
            else if (na) c = 1;      // 数字段优先于文字段(如 47.3.0 > 47.3.0-beta)
            else if (nb) c = -1;
            else c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return -c;   // 降序
        }
        return 0;
    }
}
