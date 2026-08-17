// LoaderVersionProvider.cs — 模组加载器版本列表拉取服务
// 可爱的芙芙
//
// 按「加载器 + 游戏版本」动态拉取可选加载器版本,区分正式版 / 测试版。
// 源策略:本地缓存 → 国内镜像(短超时) → 官方源兜底(长超时),全程异步不阻塞 UI。
//   Fabric   → BMCLAPI 国内镜像(fabric-meta),回退 Fabric 官方 meta
//   Forge    → BMCLAPI 国内镜像(/forge/minecraft/{mc})
//   NeoForge → BMCLAPI maven 镜像,回退 NeoForge 官方 maven(maven-metadata.xml)
//   Quilt    → BMCLAPI 预留路径,回退 Quilt 官方 meta(国内暂无镜像接口)
//   OptiFine → BMCLAPI 国内镜像(/optifine/{mc})
// 拉取成功的原始数据写入 缓存\loader-meta 目录,切换游戏版本时秒开(NeoForge 元数据为全量列表,缓存后任意版本免网络)。

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

    // ===== 缓存(内存 + 磁盘):避免切换游戏版本反复请求慢源 =====
    private static readonly Dictionary<string, (DateTime Utc, string Content)> MemCache = new();
    private static readonly object CacheLock = new();
    private static string CacheDir => Path.Combine(AppPaths.Cache, "loader-meta");

    private static readonly TimeSpan TtlLong = TimeSpan.FromHours(24);   // NeoForge 全量元数据
    private static readonly TimeSpan TtlShort = TimeSpan.FromHours(12);  // 其余按游戏版本的列表

    public LoaderVersionProvider()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/1.0 (Windows)");
        _http.Timeout = Timeout.InfiniteTimeSpan;   // 超时由每个请求的 CancellationToken 单独控制
    }

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
    private async Task<(string Content, string Label)?> FetchFirstAsync(
        string cacheKey, TimeSpan cacheTtl,
        (string Url, string Label, int TimeoutSec)[] sources)
    {
        // 1) 内存缓存
        lock (CacheLock)
        {
            if (MemCache.TryGetValue(cacheKey, out var m) && DateTime.UtcNow - m.Utc < cacheTtl)
                return (m.Content, "本地缓存");
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
                    return (cached, "本地缓存");
                }
            }
        }
        catch { /* 缓存损坏忽略,走网络 */ }

        // 3) 网络:镜像优先,官方兜底;每源独立超时,失败快速切换下一源
        foreach (var (url, label, timeoutSec) in sources)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    App.WriteAppLog($"[加载器] 源 {label} 返回 {(int)resp.StatusCode},尝试下一源");
                    continue;
                }
                string content = await resp.Content.ReadAsStringAsync(cts.Token);
                if (string.IsNullOrWhiteSpace(content)) continue;

                lock (CacheLock) MemCache[cacheKey] = (DateTime.UtcNow, content);
                _ = Task.Run(() =>   // 写盘不阻塞主流程
                {
                    try { Directory.CreateDirectory(CacheDir); File.WriteAllText(CacheFile(cacheKey), content); }
                    catch { /* 写缓存失败不影响本次结果 */ }
                });
                return (content, label);
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
        return null;
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
        if (fetched == null)
            return LoaderVersionListResult.Fail("获取版本失败,镜像与官方源均不可达,请点击重试");

        try
        {
            var list = JsonSerializer.Deserialize<List<FabricMetaItem>>(fetched.Value.Content);
            var entries = (list ?? new List<FabricMetaItem>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Loader.Version))
                .Select(x => new LoaderVersionEntry { Version = x.Loader.Version, IsStable = x.Loader.Stable })
                .ToList();
            entries.Sort((a, b) => CompareVersionDesc(a.Version, b.Version));
            return LoaderVersionListResult.Ok(entries, fetched.Value.Label);
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
        var fetched = await FetchFirstAsync($"forge-{mc}.json", TtlShort, new[]
        {
            ($"https://bmclapi2.bangbang93.com/forge/minecraft/{mc}", "BMCLAPI 国内镜像", 8)
        });
        if (fetched == null)
            return LoaderVersionListResult.Fail("获取版本失败,无法连接 Forge 镜像源,请点击重试");

        try
        {
            var list = JsonSerializer.Deserialize<List<ForgeBuildItem>>(fetched.Value.Content);
            var entries = new List<LoaderVersionEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in list ?? new List<ForgeBuildItem>())
            {
                if (string.IsNullOrWhiteSpace(b.Version)) continue;
                // 过滤掉早期以游戏版本开头的怪异条目(如 "1.6.1-xxx"),仅保留纯版本号
                if (b.Version.StartsWith(mc, StringComparison.Ordinal)) continue;
                if (!seen.Add(b.Version)) continue;
                entries.Add(new LoaderVersionEntry
                {
                    Version = b.Version,
                    IsStable = !Regex.IsMatch(b.Version, "beta|alpha|pre|rc", RegexOptions.IgnoreCase)
                });
            }
            entries.Sort((a, b) => CompareVersionDesc(a.Version, b.Version));
            return LoaderVersionListResult.Ok(entries, fetched.Value.Label);
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
        if (fetched == null)
            return LoaderVersionListResult.Fail("获取版本失败,镜像与官方源均不可达,请点击重试");

        try
        {
            var matches = Regex.Matches(fetched.Value.Content, @"<version>([^<]+)</version>");
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
            return LoaderVersionListResult.Ok(entries, fetched.Value.Label);
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
        if (fetched == null)
            return LoaderVersionListResult.Fail("获取版本失败,无法连接 Quilt 版本源,请点击重试");

        try
        {
            var list = JsonSerializer.Deserialize<List<FabricMetaItem>>(fetched.Value.Content);
            var entries = (list ?? new List<FabricMetaItem>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Loader.Version))
                .Select(x => new LoaderVersionEntry
                {
                    Version = x.Loader.Version,
                    IsStable = x.Loader.Stable && !Regex.IsMatch(x.Loader.Version, "beta|alpha|rc", RegexOptions.IgnoreCase)
                })
                .ToList();
            entries.Sort((a, b) => CompareVersionDesc(a.Version, b.Version));
            return LoaderVersionListResult.Ok(entries, fetched.Value.Label);
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
        if (fetched == null)
            return LoaderVersionListResult.Fail("获取版本失败,无法连接 OptiFine 镜像源,请点击重试");

        try
        {
            var list = JsonSerializer.Deserialize<List<OptiFineItem>>(fetched.Value.Content);
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
            return LoaderVersionListResult.Ok(entries, fetched.Value.Label);
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
