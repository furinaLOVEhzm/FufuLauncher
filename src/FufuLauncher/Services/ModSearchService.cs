// Copyright © FufuLauncher
//
// 模组搜索增强服务(LauncherX-2):
// 搜索同时匹配「模组名称 / modid / 作者名字」,再顺带匹配文件名与简介;
// 输入文字就实时过滤刷新结果,不需要点搜索按钮 —— 服务层提供防抖定时器,
// 用户每敲一个字符都触发一次筛选,但只有停顿下来才真正跑网络查询。
//
// 实现要点:
// 1. 本地过滤是纯内存操作(几百个模组毫秒级),每敲一个字符直接刷,不做防抖;
// 2. 联网搜索才需要防抖(350ms),避免每敲一个字母就打一次 Modrinth 被限流;
// 3. 命中打分:modid 精确 > 名称开头 > 名称包含 > 作者包含 > 简介包含 > 文件名包含,
//    分数高的排前面,同分按名称字典序,保证多次输入结果顺序稳定;
// 4. 关键词按空格切成多个词,词与词之间是"与"关系(找"sodium 光影"要两个都命中)。

using System.Windows.Threading;

namespace FufuLauncher.Services;

/// <summary>一次本地过滤的结果</summary>
public sealed class ModFilterResult
{
    public List<ModInfo> Mods { get; set; } = new();
    public int TotalCount { get; set; }
    public int MatchCount => Mods.Count;
    public int EnabledCount => Mods.Count(m => m.Enabled);
    public int DisabledCount => Mods.Count(m => !m.Enabled);
    public string Keyword { get; set; } = "";
    public string Summary => string.IsNullOrEmpty(Keyword)
        ? $"共 {TotalCount} 个模组(启用 {Mods.Count(m => m.Enabled)} · 禁用 {Mods.Count(m => !m.Enabled)})"
        : $"关键词「{Keyword}」命中 {MatchCount} / {TotalCount} 个模组";
}

public sealed class ModSearchService
{
    /// <summary>联网搜索防抖间隔(毫秒)</summary>
    public const int DebounceMs = 350;

    private readonly ModrinthService _modrinth;
    private DispatcherTimer? _debounce;
    private string _pendingKeyword = "";

    public ModSearchService(ModrinthService modrinth)
    {
        _modrinth = modrinth;
    }

    // ==================== 本地实时过滤 ====================

    /// <summary>
    /// 本地实时过滤:名称 / modid / 作者 / 文件名 / 简介全部参与匹配,按命中权重排序。
    /// </summary>
    public static ModFilterResult Filter(IEnumerable<ModInfo>? mods, string? keyword, bool? onlyDisabled = null)
    {
        var all = (mods ?? Enumerable.Empty<ModInfo>()).ToList();
        var res = new ModFilterResult { TotalCount = all.Count, Keyword = (keyword ?? "").Trim() };

        IEnumerable<ModInfo> query = all;
        // 只看禁用(模组禁用状态持久标记 Celestial-8 的筛选入口)
        if (onlyDisabled == true) query = query.Where(m => !m.Enabled);
        else if (onlyDisabled == false) query = query.Where(m => m.Enabled);

        if (res.Keyword.Length == 0)
        {
            res.Mods = query.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            return res;
        }

        var terms = res.Keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scored = new List<(ModInfo Mod, int Score)>();
        foreach (var m in query)
        {
            int score = 0;
            bool allHit = true;
            foreach (string t in terms)
            {
                int s = Score(m, t);
                if (s <= 0) { allHit = false; break; }
                score += s;
            }
            if (allHit && score > 0) scored.Add((m, score));
        }

        res.Mods = scored.OrderByDescending(x => x.Score)
                         .ThenBy(x => x.Mod.DisplayName, StringComparer.OrdinalIgnoreCase)
                         .Select(x => x.Mod)
                         .ToList();
        return res;
    }

    /// <summary>单个关键词对单个模组的命中打分(0 = 没命中)</summary>
    public static int Score(ModInfo mod, string term)
    {
        if (string.IsNullOrEmpty(term)) return 0;
        string t = term.Trim().ToLowerInvariant();
        if (t.Length == 0) return 0;

        string modId = (mod.ModId ?? "").ToLowerInvariant();
        string name = (mod.Name ?? "").ToLowerInvariant();
        string file = (mod.FileName ?? "").ToLowerInvariant();
        string author = (mod.Author ?? "").ToLowerInvariant();
        string desc = (mod.Description ?? "").ToLowerInvariant();

        // modid 精确命中权重最高(用户通常就是照着 modid 搜)
        if (modId.Length > 0 && modId == t) return 1000;
        if (name.Length > 0 && name == t) return 900;
        if (modId.Length > 0 && modId.StartsWith(t, StringComparison.Ordinal)) return 700;
        if (name.Length > 0 && name.StartsWith(t, StringComparison.Ordinal)) return 650;
        if (modId.Length > 0 && modId.Contains(t, StringComparison.Ordinal)) return 500;
        if (name.Length > 0 && name.Contains(t, StringComparison.Ordinal)) return 450;
        if (author.Length > 0 && author.Contains(t, StringComparison.Ordinal)) return 300;
        if (file.Contains(t, StringComparison.Ordinal)) return 200;
        if (desc.Contains(t, StringComparison.Ordinal)) return 100;
        // 中文/多语言简介里可能带全角空格,宽松再试一次
        if (desc.Replace(" ", "").Contains(t.Replace(" ", ""), StringComparison.Ordinal)) return 60;
        return 0;
    }

    /// <summary>这个模组为什么被搜出来了(侧边预览里给用户看命中依据)</summary>
    public static string MatchReason(ModInfo mod, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return "";
        var hits = new List<string>();
        foreach (string raw in keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string t = raw.ToLowerInvariant();
            if ((mod.ModId ?? "").ToLowerInvariant().Contains(t)) hits.Add("modid");
            if ((mod.Name ?? "").ToLowerInvariant().Contains(t)) hits.Add("名称");
            if ((mod.Author ?? "").ToLowerInvariant().Contains(t)) hits.Add("作者");
            if ((mod.FileName ?? "").ToLowerInvariant().Contains(t)) hits.Add("文件名");
            if ((mod.Description ?? "").ToLowerInvariant().Contains(t)) hits.Add("简介");
        }
        return hits.Count == 0 ? "" : "命中:" + string.Join("、", hits.Distinct());
    }

    // ==================== 联网搜索(带防抖) ====================

    /// <summary>
    /// 输入即搜:注册一个防抖回调,用户停顿 DebounceMs 后才真正打 Modrinth。
    /// onSearch 收到的关键词就是最终要查的那个。
    /// </summary>
    public void DebouncedSearch(string keyword, Action<string> onSearch)
    {
        _pendingKeyword = keyword ?? "";
        if (_debounce == null)
        {
            _debounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(DebounceMs)
            };
            _debounce.Tick += (s, e) =>
            {
                _debounce?.Stop();
                onSearch(_pendingKeyword);
            };
        }
        _debounce.Stop();
        // 关键词清空时立即回调,不等防抖(用户按下退格想马上看到全量列表)
        if (_pendingKeyword.Trim().Length == 0)
        {
            onSearch("");
            return;
        }
        _debounce.Start();
    }

    /// <summary>取消挂起的防抖查询(切页/关窗口时调用)</summary>
    public void CancelPending()
    {
        _debounce?.Stop();
        _pendingKeyword = "";
    }

    /// <summary>
    /// 联网搜索并本地二次排序:Modrinth 的相关度排序对 modid / 作者命中并不敏感,
    /// 拿回来后按同一套打分再排一次,保证"输入即搜"的结果顺序稳定可预期。
    /// </summary>
    public async Task<List<ModrinthProject>> SearchOnlineAsync(string keyword, string? gameVersion, string? loader,
                                                              int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new List<ModrinthProject>();
        string loaderKey = ModDiagnosticsService.NormalizeLoader(loader) ?? "";
        var result = await _modrinth.SearchAsync(keyword.Trim(), 0, Math.Max(limit, 20), gameVersion,
                                                 loaderKey.Length > 0 ? loaderKey : null,
                                                 "mod", "relevance", null, ct).ConfigureAwait(false);
        var hits = result?.Hits ?? new List<ModrinthProject>();
        if (hits.Count == 0) return hits;

        var terms = keyword.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return hits
            .Select(p => (Project: p, Score: OnlineScore(p, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Project.Downloads * -1)
            .Take(limit)
            .Select(x => x.Project)
            .ToList();
    }

    /// <summary>联网结果打分:标题 / slug(≈modid) / 作者 三路匹配</summary>
    private static int OnlineScore(ModrinthProject p, string[] terms)
    {
        string title = (p.Title ?? "").ToLowerInvariant();
        string slug = (p.Slug ?? "").ToLowerInvariant().Replace('-', ' ');
        string author = (p.Author ?? "").ToLowerInvariant();
        int total = 0;
        foreach (string t in terms)
        {
            string term = t.ToLowerInvariant();
            int s = 0;
            if (title == term || slug == term) s = 1000;
            else if (title.StartsWith(term, StringComparison.Ordinal)) s = 700;
            else if (title.Contains(term, StringComparison.Ordinal)) s = 450;
            else if (slug.Contains(term, StringComparison.Ordinal)) s = 500;
            else if (author.Contains(term, StringComparison.Ordinal)) s = 300;
            if (s == 0) return 0;   // 任一词没命中就整体淘汰(与关系)
            total += s;
        }
        return total;
    }
}
