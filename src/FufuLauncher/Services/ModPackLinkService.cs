// Copyright © FufuLauncher
//
// 整合包链接导入服务(Celestial-6):
// 粘贴 Modrinth / CurseForge 的整合包网页链接 → 自动拉取整合包元数据 → 预览确认
// → 下载整合包文件 → 交给 ModPackImportService 完成建实例 + 装加载器 + 补下模组。
//
// 实现要点:
// 1. 链接解析纯本地正则,支持 modrinth.com/modpack/{slug}、curseforge.com/minecraft/modpacks/{slug}
//    以及带查询串/锚点/短链尾巴的写法,识别不了直接给中文原因,绝不瞎猜;
// 2. CurseForge 走官方 API v1,需要用户在设置页填过 CurseForge API Key;没填时给出明确指引,
//    不静默失败;文件下载优先用 API 返回的 downloadUrl,拿不到就退回 edge.forgecdn.net 直链拼法;
// 3. Modrinth 直接复用 ModrinthService(已有重试/超时/限流处理),不重复造 HTTP 层;
// 4. 下载的整合包落到 cache\packs\,导入完成后保留(方便重复导入),由日志清理服务统一回收。

using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FufuLauncher.Services;

/// <summary>链接来源</summary>
public enum ModPackLinkSource
{
    Unknown,
    Modrinth,
    CurseForge
}

/// <summary>链接解析结果(预览用)</summary>
public sealed class ModPackLinkInfo
{
    public ModPackLinkSource Source { get; set; } = ModPackLinkSource.Unknown;
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Summary { get; set; } = "";
    public string IconUrl { get; set; } = "";
    /// <summary>整合包对应的游戏版本</summary>
    public string McVersion { get; set; } = "";
    /// <summary>加载器(Fabric / Forge / Quilt / NeoForge)</summary>
    public string Loader { get; set; } = "";
    public long DownloadSize { get; set; }
    public string FileName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    /// <summary>Modrinth 版本号 / CurseForge 文件 Id(下载时二次定位用)</summary>
    public string VersionRef { get; set; } = "";
    public long Downloads { get; set; }
    public string PageUrl { get; set; } = "";
    public bool IsModPack { get; set; }
    /// <summary>该来源可用的全部整合包版本(供 UI 让用户挑版本,默认取最新)</summary>
    public List<(string VersionRef, string Label, string McVersion, string Loader, long Size, string Url)> AvailableVersions { get; set; } = new();

    public string SourceDisplay => Source switch
    {
        ModPackLinkSource.Modrinth => "Modrinth",
        ModPackLinkSource.CurseForge => "CurseForge",
        _ => "未知来源"
    };
    public string Display => $"{Title}{(string.IsNullOrEmpty(Author) ? "" : $" by {Author}")} · {SourceDisplay}" +
                             $"{(string.IsNullOrEmpty(McVersion) ? "" : $" · {McVersion}")}" +
                             $"{(string.IsNullOrEmpty(Loader) ? "" : $" · {Loader}")}" +
                             $" · {StorageGuardService.FmtSize(DownloadSize)}";
}

public sealed class ModPackLinkService
{
    /// <summary>CurseForge 的游戏 Id(Minecraft 固定 432)</summary>
    private const int CfGameId = 432;
    private const string CfApiBase = "https://api.curseforge.com/v1";
    private const string CfEdgeBase = "https://edge.forgecdn.net/files";

    private static readonly Regex ModrinthUrlRegex = new(
        @"modrinth\.com/(?:modpack|modpacks|project|mod|p)/([a-z0-9\-_]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CurseUrlRegex = new(
        @"curseforge\.com/minecraft/(?:modpacks|mod-packs|mc-mods|mods)/([a-z0-9\-_]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CurseShortUrlRegex = new(
        @"curseforge\.com/(?:p|project)/([a-z0-9\-_]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HttpClient Http = CreateHttp();

    private readonly ModrinthService _modrinth;
    private readonly ModPackImportService _importer;
    private readonly ConfigService _config;

    public ModPackLinkService(ModrinthService modrinth, ModPackImportService importer, ConfigService config)
    {
        _modrinth = modrinth;
        _importer = importer;
        _config = config;
    }

    /// <summary>最近一次错误(UI 直接展示)</summary>
    public string? LastError { get; private set; }

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/3.0 (Windows)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    /// <summary>整合包下载缓存目录</summary>
    public static string PacksCacheDir
    {
        get
        {
            string dir = Path.Combine(AppPaths.Cache, "packs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ==================== 链接识别 ====================

    /// <summary>快速判断是不是本站支持的整合包链接(不联网)</summary>
    public static ModPackLinkSource DetectSource(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return ModPackLinkSource.Unknown;
        if (ModrinthUrlRegex.IsMatch(url)) return ModPackLinkSource.Modrinth;
        if (CurseUrlRegex.IsMatch(url) || CurseShortUrlRegex.IsMatch(url)) return ModPackLinkSource.CurseForge;
        return ModPackLinkSource.Unknown;
    }

    /// <summary>从链接里抠出 slug(不联网)</summary>
    public static string? ExtractSlug(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = ModrinthUrlRegex.Match(url!);
        if (m.Success) return m.Groups[1].Value;
        m = CurseUrlRegex.Match(url!);
        if (m.Success) return m.Groups[1].Value;
        m = CurseShortUrlRegex.Match(url!);
        if (m.Success) return m.Groups[1].Value;
        return null;
    }

    // ==================== 元数据拉取 ====================

    /// <summary>拉取整合包元数据(预览)。失败返回 null,原因写在 LastError</summary>
    public async Task<ModPackLinkInfo?> FetchAsync(string url, CancellationToken ct = default)
    {
        LastError = null;
        var source = DetectSource(url);
        string? slug = ExtractSlug(url);
        if (source == ModPackLinkSource.Unknown || string.IsNullOrEmpty(slug))
        {
            LastError = "这个链接没看懂。目前支持 Modrinth 整合包页(modrinth.com/modpacks/xxx)" +
                        "和 CurseForge 整合包页(curseforge.com/minecraft/modpacks/xxx),请把浏览器地址栏的完整网址粘进来。";
            return null;
        }

        try
        {
            var info = source == ModPackLinkSource.Modrinth
                ? await FetchModrinthAsync(slug!, ct).ConfigureAwait(false)
                : await FetchCurseForgeAsync(slug!, ct).ConfigureAwait(false);
            if (info != null) info.PageUrl = url.Trim();
            return info;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = "拉取整合包信息失败:" + ex.Message;
            App.WriteAppLog($"[链接导入] ✗ {source} {slug} 元数据拉取失败:{ex}");
            return null;
        }
    }

    private async Task<ModPackLinkInfo?> FetchModrinthAsync(string slug, CancellationToken ct)
    {
        var proj = await _modrinth.GetProjectAsync(slug, ct).ConfigureAwait(false);
        if (proj == null)
        {
            LastError = $"Modrinth 上找不到「{slug}」:" + (_modrinth.LastError ?? "网络异常或该项目已下架");
            return null;
        }

        var info = new ModPackLinkInfo
        {
            Source = ModPackLinkSource.Modrinth,
            Slug = proj.Slug.Length > 0 ? proj.Slug : slug,
            Title = proj.Title,
            Author = proj.Author,
            Summary = proj.Description,
            IconUrl = proj.IconUrl ?? "",
            Downloads = proj.Downloads,
            IsModPack = string.Equals(proj.ProjectType, "modpack", StringComparison.OrdinalIgnoreCase)
        };

        // 整合包版本列表(不带 gameVersion/loader 过滤,整合包本身就锁定了版本)
        var versions = await _modrinth.GetProjectVersionsAsync(proj.ProjectId, null, null, ct).ConfigureAwait(false);
        var usable = versions
            .Where(v => v.GetPrimaryFile() != null)
            .OrderByDescending(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
            .ThenBy(v => v.GameVersions.FirstOrDefault() ?? "")
            .ToList();
        if (usable.Count == 0)
        {
            LastError = $"「{proj.Title}」在 Modrinth 上没有可下载的整合包文件。";
            return null;
        }

        foreach (var v in usable.Take(20))
        {
            var f = v.GetPrimaryFile()!;
            info.AvailableVersions.Add((v.Id, $"{v.VersionNumber} · {string.Join("/", v.GameVersions.Take(3))} · {v.VersionType}",
                                        v.GameVersions.FirstOrDefault() ?? "", string.Join("/", v.Loaders), f.Size, f.Url));
        }
        var latest = usable[0];
        var latestFile = latest.GetPrimaryFile()!;
        info.VersionRef = latest.Id;
        info.McVersion = latest.GameVersions.FirstOrDefault() ?? "";
        info.Loader = latest.Loaders.FirstOrDefault() ?? "";
        info.DownloadSize = latestFile.Size;
        info.FileName = latestFile.Filename;
        info.DownloadUrl = latestFile.Url;
        return info;
    }

    private async Task<ModPackLinkInfo?> FetchCurseForgeAsync(string slug, CancellationToken ct)
    {
        string apiKey = (_config.Config.CurseForgeApiKey ?? "").Trim();
        if (apiKey.Length == 0)
        {
            LastError = "导入 CurseForge 整合包需要 API Key:去 console.curseforge.com 免费注册一个," +
                        "填到「设置 → 下载与网络 → CurseForge API Key」里再试。" +
                        "也可以先在 CurseForge 网页上手动下载整合包文件,再把文件拖进启动器导入。";
            return null;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{CfApiBase}/mods/search?gameId={CfGameId}&slug={Uri.EscapeDataString(slug)}");
        req.Headers.Add("x-api-key", apiKey);
        // 2026-09-25 修复:CurseForge 未授权限流(429)常见,加退避重试,不再一抖就失败
        using var resp = await SendWithRetryAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            LastError = $"CurseForge 接口返回 {(int)resp.StatusCode}:{((int)resp.StatusCode == 401 ? "API Key 无效或已过期" : "请稍后重试")}。";
            return null;
        }
        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var arr = root?["data"] as JsonArray;
        if (arr == null || arr.Count == 0)
        {
            LastError = $"CurseForge 上找不到「{slug}」,请确认链接是整合包页面。";
            return null;
        }
        var proj = arr[0]!;
        int modId = proj["id"]?.GetValue<int>() ?? 0;
        var info = new ModPackLinkInfo
        {
            Source = ModPackLinkSource.CurseForge,
            Slug = slug,
            Title = proj["name"]?.GetValue<string>() ?? slug,
            Author = (proj["authors"] as JsonArray)?.FirstOrDefault()?["name"]?.GetValue<string>() ?? "",
            Summary = proj["summary"]?.GetValue<string>() ?? "",
            IconUrl = proj["logo"]?["thumbnailUrl"]?.GetValue<string>() ?? proj["logo"]?["url"]?.GetValue<string>() ?? "",
            Downloads = proj["downloadCount"]?.GetValue<long>() ?? 0,
            IsModPack = (proj["classId"]?.GetValue<int>() ?? 0) == 4471 ||
                        (proj["gameId"]?.GetValue<int>() ?? 0) == CfGameId
        };

        // 文件列表:取最新一批(整合包文件通常几百 MB,只列前 12 个够挑了)
        using var fReq = new HttpRequestMessage(HttpMethod.Get, $"{CfApiBase}/mods/{modId}/files?pageSize=12&sortDescending=true");
        fReq.Headers.Add("x-api-key", apiKey);
        using var fResp = await SendWithRetryAsync(fReq, ct).ConfigureAwait(false);
        if (!fResp.IsSuccessStatusCode)
        {
            LastError = $"读取 CurseForge 文件列表失败({(int)fResp.StatusCode})。";
            return null;
        }
        var fRoot = JsonNode.Parse(await fResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var files = fRoot?["data"] as JsonArray;
        if (files == null || files.Count == 0)
        {
            LastError = $"「{info.Title}」在 CurseForge 上没有可下载的文件。";
            return null;
        }

        foreach (var f in files)
        {
            if (f == null) continue;
            long fileId = f["id"]?.GetValue<long>() ?? 0;
            if (fileId == 0) continue;
            string fileName = f["fileName"]?.GetValue<string>() ?? $"pack-{fileId}.zip";
            long size = f["fileLength"]?.GetValue<long>() ?? 0;
            string url = f["downloadUrl"]?.GetValue<string>() ?? "";
            if (url.Length == 0) url = BuildEdgeUrl(fileId, fileName);
            var gvs = (f["gameVersions"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? new();
            string mc = gvs.FirstOrDefault(v => v.StartsWith("1.")) ?? gvs.FirstOrDefault() ?? "";
            string loader = (f["modLoader"]?.GetValue<int>() ?? 0) switch
            {
                1 => "Forge",
                4 => "Fabric",
                5 => "Quilt",
                6 => "NeoForge",
                _ => ""
            };
            string label = $"{f["displayName"]?.GetValue<string>() ?? fileName} · {mc}{(loader.Length > 0 ? " · " + loader : "")}";
            info.AvailableVersions.Add((fileId.ToString(), label, mc, loader, size, url));
        }

        if (info.AvailableVersions.Count == 0)
        {
            LastError = $"「{info.Title}」的文件列表解析失败,请换个链接或手动下载。";
            return null;
        }

        var first = info.AvailableVersions[0];
        info.VersionRef = first.VersionRef;
        info.McVersion = first.McVersion;
        info.Loader = first.Loader;
        info.DownloadSize = first.Size;
        info.FileName = Path.GetFileName(new Uri(first.Url).AbsolutePath);
        info.DownloadUrl = first.Url;
        return info;
    }

    /// <summary>带退避重试的 CurseForge 请求:429/5xx 自动重试 3 次(1s/2s/4s),
    /// 瞬时限流不再直接判失败(与 ModrinthService 的 RateLimitBackoff 对齐)。
    /// 4xx(401 等)立即返回,不浪费重试。</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage req, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        for (int i = 0; ; i++)
        {
            var resp = await Http.SendAsync(req, completion, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode || i >= 2) return resp;
            int code = (int)resp.StatusCode;
            if (code is 429 or >= 500)
            {
                resp.Dispose();
                try { await Task.Delay(TimeSpan.FromSeconds(1 << i), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                continue;
            }
            return resp;
        }
    }

    /// <summary>CurseForge 直链兜底拼法(第三方分发受限时的经典 edge.forgecdn 规则)</summary>
    private static string BuildEdgeUrl(long fileId, string fileName)
        => $"{CfEdgeBase}/{fileId / 1000}/{fileId % 1000}/{Uri.EscapeDataString(fileName)}";

    // ==================== 下载并导入 ====================

    /// <summary>
    /// 下载整合包文件到本地缓存。progress 收到的是 0~1 的百分比。
    /// </summary>
    public async Task<(bool Ok, string FilePath, string Message)> DownloadPackAsync(
        ModPackLinkInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        LastError = null;
        if (info == null || string.IsNullOrEmpty(info.DownloadUrl))
            return (false, "", "没有可用的下载地址,请重新解析链接。");

        string dir = PacksCacheDir;
        string fileName = SanitizeFileName(
            string.IsNullOrEmpty(info.FileName) ? $"{info.Slug}-{info.VersionRef}.mrpack" : info.FileName);
        string dest = Path.Combine(dir, fileName);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            if (info.Source == ModPackLinkSource.CurseForge)
            {
                string apiKey = (_config.Config.CurseForgeApiKey ?? "").Trim();
                if (apiKey.Length > 0) req.Headers.Add("x-api-key", apiKey);
            }
            // 响应头失败(429/5xx)自动退避重试;流式读取中失败不重试(避免浪费已下载字节)
            // 大文件必须 ResponseHeadersRead 流式读,否则整包缓冲进内存
            using var resp = await SendWithRetryAsync(req, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"下载失败(HTTP {(int)resp.StatusCode})。CurseForge 的文件有时需要在网页上先点一次下载,可以手动下载后把文件拖进启动器。";
                return (false, "", LastError);
            }

            long total = resp.Content.Headers.ContentLength ?? info.DownloadSize;
            string tmp = dest + ".part";
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0) progress?.Report(read * 1.0 / total);
                }
                await dst.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(tmp, dest, overwrite: true);
            App.WriteAppLog($"[链接导入] ✓ 整合包已下载 {dest}({StorageGuardService.FmtSize(new FileInfo(dest).Length)})");
            return (true, dest, $"整合包已下载完成({StorageGuardService.FmtSize(new FileInfo(dest).Length)})。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 失败清理 .part 残片,不留垃圾占用缓存目录
            try { if (File.Exists(dest + ".part")) File.Delete(dest + ".part"); } catch { }
            LastError = "下载整合包失败:" + ex.Message;
            App.WriteAppLog($"[链接导入] ✗ 下载失败 {info.DownloadUrl}:{ex}");
            return (false, "", LastError);
        }
    }

    /// <summary>
    /// 一条龙:解析链接 → 下载 → 导入建实例。
    /// versionRef 传空表示用最新版;progress 收中文进度文案。
    /// </summary>
    public async Task<ModPackImportResult> ImportFromLinkAsync(
        string url, string? customName, string? versionRef = null,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var fail = new ModPackImportResult { Ok = false };
        progress?.Invoke("正在解析整合包链接…");
        var info = await FetchAsync(url, ct).ConfigureAwait(false);
        if (info == null)
        {
            fail.Message = LastError ?? "链接解析失败。";
            return fail;
        }
        if (!info.IsModPack)
        {
            fail.Message = $"「{info.Title}」看起来不是整合包(是 {info.SourceDisplay} 上的单个项目)。" +
                           "这个功能只支持整合包链接;想装单个模组请去「模组」页搜索安装。";
            return fail;
        }

        // 用户指定了别的版本 → 换下载地址
        if (!string.IsNullOrEmpty(versionRef))
        {
            var pick = info.AvailableVersions.FirstOrDefault(v => v.VersionRef == versionRef);
            if (!string.IsNullOrEmpty(pick.Url))
            {
                info.VersionRef = pick.VersionRef;
                info.DownloadUrl = pick.Url;
                info.McVersion = pick.McVersion;
                info.Loader = pick.Loader;
                info.DownloadSize = pick.Size;
                if (!string.IsNullOrEmpty(pick.McVersion) || pick.Url.Contains(".zip"))
                    info.FileName = SanitizeFileName(Path.GetFileName(new Uri(pick.Url).AbsolutePath));
            }
        }

        progress?.Invoke($"正在下载整合包「{info.Title}」…");
        var dlProgress = new Progress<double>(p => progress?.Invoke($"正在下载整合包 {p:P0}…"));
        var (ok, path, msg) = await DownloadPackAsync(info, dlProgress, ct).ConfigureAwait(false);
        if (!ok)
        {
            fail.Message = msg;
            return fail;
        }

        progress?.Invoke("正在导入整合包、安装游戏版本与模组…");
        var name = string.IsNullOrWhiteSpace(customName) ? info.Title : customName!.Trim();
        var result = await _importer.ImportAsync(path, name, p => progress?.Invoke(p)).ConfigureAwait(false);
        App.WriteAppLog($"[链接导入] {(result.Ok ? "✓" : "✗")} {info.SourceDisplay}/{info.Slug} → {name}:{result.Message}");
        return result;
    }

    /// <summary>文件名净化(整合包名可能带 emoji / 斜杠等非法字符)</summary>
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "modpack.mrpack";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().Trim('.');
        if (name.Length == 0) return "modpack.mrpack";
        return name.Length > 120 ? name[..120] : name;
    }
}
