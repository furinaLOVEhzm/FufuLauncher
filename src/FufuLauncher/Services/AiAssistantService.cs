// AiAssistantService.cs — 泡芙助理:一句话生成模组整合包
//
// 架构(参考 ModSmith / ModForge-AI / packwiz 的思路):
//   - 本地 LLM(Qwen3-4B, AiEngineService)只做「自然语言 → 结构化 JSON 意图」解析;
//   - 模组检索/依赖校验/版本兼容/下载/实例构建全部由确定性后端完成,LLM 不参与下载决策;
//   - 资源检索以 Modrinth 官方 API 为准(国内可直连、数据最全);
//     MC 百科(zh.minecraft.wiki opensearch)作为中文名→条目映射的尽力而为辅助;
//     MC 论坛(MCBBS)已闭站、无公开接口,不纳入检索链路,回复中如实说明。
//
// 构建管线:意图解析 → 游戏版本解析(前缀匹配最新 release) → 模组解析(Modrinth)
//           → 加载器仲裁(覆盖数最优) → 创建实例 → 装本体 → 装加载器 → 逐模组含依赖下载。

using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FufuLauncher.Next.UI;
using LLama.Common;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Services;

// ==================== 意图与结果数据结构 ====================

public class AiIntent
{
    [JsonPropertyName("type")] public string Type { get; set; } = "chat";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("gameVersion")] public string? GameVersion { get; set; }
    [JsonPropertyName("loader")] public string? Loader { get; set; }   // fabric/neoforge/forge/quilt/auto
    [JsonPropertyName("mods")] public List<AiModQuery> Mods { get; set; } = new();
    [JsonPropertyName("reply")] public string? Reply { get; set; }
    [JsonPropertyName("mod")] public string? Mod { get; set; }           // query_mod:被查询的模组名
    [JsonPropertyName("field")] public string? Field { get; set; }       // query_mod:versions/dependencies/info
    [JsonPropertyName("category")] public string? Category { get; set; } // recommend_mods:主题(科技/魔法/生存…)
    [JsonPropertyName("theme")] public string? Theme { get; set; }       // set_theme:dark/light/toggle
    [JsonPropertyName("accountName")] public string? AccountName { get; set; } // 离线账号昵称/切换目标账号名
}

public class AiModQuery
{
    [JsonPropertyName("zh")] public string? Zh { get; set; }
    [JsonPropertyName("en")] public string? En { get; set; }
}

/// <summary>模组整合包构建结果(供 UI 展示与日志)</summary>
public class ModpackBuildResult
{
    public bool Success { get; set; }
    public string? InstanceId { get; set; }
    public string InstanceName { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public string Loader { get; set; } = "";
    public List<string> InstalledMods { get; } = new();
    public List<string> SkippedMods { get; } = new();      // 「名称 · 原因」
    public List<string> Notes { get; } = new();
    public string Summary { get; set; } = "";
}

/// <summary>单个模组在 Modrinth 上的解析结果</summary>
internal class ResolvedMod
{
    public string Zh { get; set; } = "";
    public string En { get; set; } = "";
    public string Display => string.IsNullOrEmpty(Zh) ? En : $"{Zh}({En})";
    public ModrinthProject? Project { get; set; }
    public ModrinthVersion? Version { get; set; }
    public List<string> Loaders { get; set; } = new();
}

public class AiAssistantService
{
    private const string IntentSystemPrompt =
        "你是芙芙启动器内置的泡芙助理意图解析器。根据用户请求只输出一个JSON对象,不输出任何其它文字。\n" +
        "通用规则:gameVersion只写纯版本号如1.21.1,加载器从版本号里拆出来(\"1.21-Forge\"→gameVersion\"1.21\",loader\"forge\";Fabric/Quilt同理,倒装句/口语句如\"来个1.14.4\"\"我要1.21.1\"\"1.18.2帮我安装\"照常提取);" +
        "loader取fabric/neoforge/forge/quilt/auto,未说则auto;mods里的en必须是该模组在Modrinth上的准确英文名(例如 匠魂→Tinkers' Construct,机械动力→Create,沉浸工程→Immersive Engineering,应用能源2→Applied Energistics 2,JEI→Just Enough Items);" +
        "\"补全依赖/带上依赖\"无需额外字段,后端自动处理;用户说\"不要光影/模组不要太多\"时不要把光影类、大型模组放进mods;不要编造不存在的模组;不要输出JSON以外的内容。\n" +
        "1.安装/下载游戏版本,不要模组(如 帮我下载1.21、来个1.14.4、我要1.21.1、1.18.2帮我安装、帮我装1.21-Forge)输出:\n" +
        "{\"type\":\"install_client\",\"gameVersion\":\"版本号(未说则留空)\",\"loader\":\"fabric/neoforge/forge/quilt/auto(未说则auto)\"}\n" +
        "2.做整合包/生成实例存档,或加装模组(如 给我加上匠魂、把机械动力加进实例、1.21版本加上匠魂、做个1.21匠魂机械动力整合包、1.19.2匠魂加神秘时代帮我做整合包)输出:\n" +
        "{\"type\":\"create_modpack\",\"name\":\"整合包名(用户未说则留空)\",\"gameVersion\":\"游戏版本号如1.21.1(未说则留空)\",\"loader\":\"fabric/neoforge/forge/quilt/auto(未说则auto)\",\"mods\":[{\"zh\":\"模组中文名\",\"en\":\"模组英文原名\"}]}\n" +
        "3.查询模组(如 匠魂模组适配哪些版本、机械动力需要什么前置、这个模组的前置是什么、帮我查这个模组信息)输出:\n" +
        "{\"type\":\"query_mod\",\"mod\":\"模组名(用户没指明则留空)\",\"field\":\"versions/dependencies/info(适配版本选versions,前置依赖选dependencies,其它选info)\",\"gameVersion\":\"若用户指定了版本则填,否则留空\"}\n" +
        "4.推荐/找模组(如 1.21有什么好玩的模组、推荐科技类模组、1.21 Forge有哪些主流模组、帮我找适合1.21的生存模组)输出:\n" +
        "{\"type\":\"recommend_mods\",\"gameVersion\":\"版本号(未说则留空)\",\"loader\":\"fabric/neoforge/forge/quilt/auto(未说则auto)\",\"category\":\"主题词如科技/魔法/冒险/生存/优化(未说则留空)\"}\n" +
        "5.切换主题/更换界面配色(如 帮我切换主题、换深色主题、切到浅色、换个配色)输出:\n" +
        "{\"type\":\"set_theme\",\"theme\":\"dark/light/toggle(说深色/黑暗/夜间选dark,说浅色/明亮选light,没指明选toggle)\"}\n" +
        "6.创建/添加离线账号(如 帮我做一个离线账号名字叫芙芙、建个离线账号)输出:\n" +
        "{\"type\":\"create_offline_account\",\"accountName\":\"离线账号昵称(用户说了就填,没说留空)\"}\n" +
        "7.打开微软登录/要登录微软账号(如 打开微软登录、我要登微软账号)输出:{\"type\":\"open_ms_login\"}\n" +
        "8.切换账号(如 切换账号、换到某某账号)输出:{\"type\":\"switch_account\",\"accountName\":\"要切换到的账号名(没指明则留空)\"}\n" +
        "9.启动/打开游戏(如 帮我打开游戏、启动游戏、启动1.21、开始玩)输出:\n" +
        "{\"type\":\"launch_game\",\"gameVersion\":\"要启动的游戏版本号或游戏名(没指明则留空)\"}\n" +
        "10.其它任何情况(闲聊/提问/帮助)输出:{\"type\":\"chat\",\"reply\":\"给用户的中文简短回复\"}";

    private static readonly HttpClient WikiHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly string[] LoaderCandidates = { "fabric", "neoforge", "forge", "quilt" };

    private readonly AiEngineService _engine;
    private readonly VersionManifestService _manifests;
    private readonly InstanceService _instances;
    private readonly GameInstallService _installer;
    private readonly ModLoaderInstallService _loaderInstall;
    private readonly LoaderVersionProvider _loaderProvider;
    private readonly ModrinthService _modrinth;

    public AiAssistantService(
        AiEngineService engine,
        VersionManifestService manifests,
        InstanceService instances,
        GameInstallService installer,
        ModLoaderInstallService loaderInstall,
        LoaderVersionProvider loaderProvider,
        ModrinthService modrinth)
    {
        _engine = engine;
        _manifests = manifests;
        _instances = instances;
        _installer = installer;
        _loaderInstall = loaderInstall;
        _loaderProvider = loaderProvider;
        _modrinth = modrinth;
    }

    /// <summary>统一入口:只处理我的世界相关任务(已移除日常闲聊)。onToken 为回复回调,progress 为构建进度(0..1 + 文案)。
    /// 路由:非 MC 指令一律静态拒答;MC 指令走 JSON 意图解析,支持三大类:
    /// ① MC 内容任务:安装版本(可带加载器)/建整合包装模组/模组查询/模组推荐/无效版本拦截;
    /// ② 启动器本地功能:主题切换/建离线账号/打开微软登录/切换账号/启动游戏;
    /// 解析失败或识别不出可执行意图时给出可识别指令示例</summary>
    public async Task<string> HandleMessageAsync(
        string userText,
        Action<string>? onToken,
        Action<double, string>? progress,
        CancellationToken ct)
    {
        // 非 MC 类闲聊的统一拒答文案(闲聊能力已移除)
        const string notMc = "我仅处理我的世界相关任务,请输入游戏相关指令。";

        var cfg = App.Services.GetRequiredService<ConfigService>().Config;
        bool taskSignal = LooksLikeTask(userText);

        // 非 MC 指令:不进模型,直接拒答
        if (!taskSignal)
        {
            App.WriteAppLog("[泡芙助理] 非 MC 指令,拒答:" + userText);
            onToken?.Invoke(notMc);
            return notMc;
        }

        // MC 任务开关关闭:收到 MC 指令也不执行任何自动化
        if (!cfg.AiMcTasksEnabled)
        {
            const string off = "【处理 MC 任务】开关目前是关闭的,我不会执行任何我的世界相关操作(下载版本、做整合包等)。\n需要的话打开开关再跟我说就行。";
            App.WriteAppLog("[泡芙助理] MC 任务开关关闭,拒绝执行:" + userText);
            onToken?.Invoke(off);
            return off;
        }

        progress?.Invoke(0.02, "泡芙助理正在理解你的需求…");
        var intent = await DetectIntentAsync(userText, ct);
        if (intent == null)
        {
            // 解析失败:给出可识别指令示例,引导重新表述
            const string guide = "这条指令我没看明白,可以这样说:\n· 帮我安装 1.12.2\n· 给我做一个 1.16.5 Forge 匠魂整合包\n· 帮我切换主题 / 做个离线账号 / 帮我打开游戏";
            App.WriteAppLog("[泡芙助理] 意图解析失败,给出指令示例");
            onToken?.Invoke(guide);
            return guide;
        }

        // ===== 启动器本地功能调用(主题/账号/启动游戏) =====
        if (intent.Type == "set_theme")
            return SetThemeCommand(intent.Theme, onToken);
        if (intent.Type == "create_offline_account")
            return CreateOfflineAccountCommand(intent.AccountName ?? intent.Name, onToken);
        if (intent.Type == "open_ms_login")
            return OpenMicrosoftLoginCommand(onToken);
        if (intent.Type == "switch_account")
            return SwitchAccountCommand(intent.AccountName ?? intent.Name, onToken);
        if (intent.Type == "launch_game")
            return await LaunchGameCommandAsync(intent.GameVersion ?? intent.Name, progress, ct);

        // 2026-09-25 完善:Quilt 加载器已全链路支持(安装服务/启动匹配/Modrinth 搜索均支持),
        // 不再拒绝——用户说「装 1.19.4-Quilt」「做个 Quilt 整合包」直接可用
        string reqLoader = (intent.Loader ?? "").Trim().ToLowerInvariant();

        if (intent.Type == "create_modpack" && intent.Mods.Count > 0)
        {
            var res = await BuildModpackAsync(intent, progress, ct);
            return res.Summary;
        }

        // 安装游戏本体(可带加载器,如「帮我装 1.21-Forge」)
        if (intent.Type == "install_client")
            return await InstallClientAsync(intent.GameVersion, reqLoader, progress, ct);

        // 模组查询:适配版本/前置依赖/模组信息
        if (intent.Type == "query_mod")
            return await QueryModAsync(intent.Mod, intent.Field, intent.GameVersion, progress, ct);

        // 模组推荐:按版本/加载器/主题检索热门模组
        if (intent.Type == "recommend_mods")
            return await RecommendModsAsync(intent.GameVersion, reqLoader, intent.Category, progress, ct);

        if (intent.Type == "create_modpack")
        {
            // 识别出想做整合包但没说清要哪些模组:引导补全
            const string guide = "好呀!告诉我要哪些模组和游戏版本,我就能开工~\n例如:给我做一个 1.16.5 Forge 匠魂整合包";
            onToken?.Invoke(guide);
            return guide;
        }

        // 意图解析器判定为闲聊/无法识别为可执行 MC 任务:按非 MC 指令统一拒答
        App.WriteAppLog("[泡芙助理] 未识别出可执行 MC 意图,拒答:" + userText);
        onToken?.Invoke(notMc);
        return notMc;
    }

    /// <summary>快速任务信号识别:命中才值得走意图解析,其余按非 MC 指令拒答。
    /// 覆盖正向/倒装/口语变体(来个/整个/搞一份/我要/部署/安排)、查询推荐句式(适配/前置/依赖/推荐/找/查)
    /// 与启动器本地功能句式(主题/配色/账号/登录/启动游戏)</summary>
    private static bool LooksLikeTask(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.ToLowerInvariant();
        return t.Contains("整合包") || t.Contains("modpack")
            || t.Contains("帮我做") || t.Contains("帮我装") || t.Contains("帮我搭配") || t.Contains("帮我找") || t.Contains("帮我查")
            || t.Contains("给我做") || t.Contains("给我来") || t.Contains("给我装") || t.Contains("给我加") || t.Contains("给我安排") || t.Contains("给我推荐")
            || t.Contains("做个") || t.Contains("做一套") || t.Contains("装一套")
            || t.Contains("制作") || t.Contains("创建") || t.Contains("生成")
            || t.Contains("推荐") || t.Contains("安装") || t.Contains("装上") || t.Contains("装个") || t.Contains("装进") || t.Contains("加装")
            || t.Contains("下载") || t.Contains("部署") || t.Contains("来个") || t.Contains("整个") || t.Contains("搞一份") || t.Contains("搞个") || t.Contains("我要")
            || t.Contains("加上") || t.Contains("加进") || t.Contains("安排")
            || t.Contains("适配") || t.Contains("前置") || t.Contains("依赖")
            || t.Contains("模组") || t.Contains("版本") || t.Contains("存档") || t.Contains("光影")
            // 启动器本地功能:外观/账号/启动游戏
            || t.Contains("主题") || t.Contains("配色") || t.Contains("深色") || t.Contains("浅色") || t.Contains("夜间模式")
            || t.Contains("账号") || t.Contains("登录") || t.Contains("登入") || t.Contains("微软")
            || t.Contains("启动") || t.Contains("打开游戏") || t.Contains("开游戏") || t.Contains("进游戏") || t.Contains("开始玩") || t.Contains("玩mc") || t.Contains("玩 mc")
            || System.Text.RegularExpressions.Regex.IsMatch(t, @"\d+\.\d+");
    }

    /// <summary>流式输出清洗器:① 跳过 Qwen3 的 思考段(含跨 token 标签边界);
    /// ② 同一字符(非空白)连续刷屏超过阈值时熔断停发。FilteredText 保留全部干净文本供非流式调用方使用</summary>
    private sealed class ChatTokenFilter
    {
        // Qwen3 思考段标签(拼接构造,避免源码层面被工具链误处理)
        private static readonly string ThinkOpen = "<" + "think" + ">";
        private static readonly string ThinkClose = "<" + "/" + "think" + ">";
        private const int MaxRepeatRun = 8;

        private readonly Action<string>? _emit;
        private readonly StringBuilder _clean = new();
        private string _pending = "";
        private bool _inThink;
        private char _lastChar;
        private int _charRun;
        private bool _muted;

        /// <summary>过滤后的全部干净文本(不依赖 emit 回调)</summary>
        public string FilteredText => _clean.ToString();

        public ChatTokenFilter(Action<string>? emit) { _emit = emit; }

        public void Push(string token)
        {
            if (_muted) return;
            _pending += token;
            Drain();
        }

        /// <summary>流结束后把滞留的不完整标签前缀当普通文本发出</summary>
        public void Flush()
        {
            if (_muted || _inThink) return;
            if (_pending.Length > 0) { EmitChecked(_pending); _pending = ""; }
        }

        private void Drain()
        {
            while (_pending.Length > 0 && !_muted)
            {
                if (_inThink)
                {
                    int end = _pending.IndexOf(ThinkClose, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        // 还没等到闭合标签:超长则判为无界思考段,整体丢弃(留尾巴防跨 token 匹配漏判)
                        if (_pending.Length > 4096) _pending = _pending[^ThinkClose.Length..];
                        return;
                    }
                    _pending = _pending[(end + ThinkClose.Length)..];
                    _inThink = false;
                    continue;
                }

                int open = _pending.IndexOf(ThinkOpen, StringComparison.Ordinal);
                if (open >= 0)
                {
                    EmitChecked(_pending[..open]);
                    if (_muted) return;
                    _pending = _pending[(open + ThinkOpen.Length)..];
                    _inThink = true;
                    continue;
                }

                // 尾部若是不完整的  前缀(如 "<" "  ")则扣住,防止拆散发给用户
                int hold = LongestTagPrefixSuffix(_pending);
                int safeLen = _pending.Length - hold;
                if (safeLen > 0)
                {
                    EmitChecked(_pending[..safeLen]);
                    _pending = _pending[safeLen..];
                }
                return;
            }
        }

        private void EmitChecked(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || c != _lastChar) _charRun = 0;
                _lastChar = c;
                _charRun++;
                if (_charRun > MaxRepeatRun)
                {
                    _muted = true;
                    App.WriteAppLog("[泡芙助理] 检测到回复重复刷屏(同字符连续输出),已提前截断");
                    break;
                }
                sb.Append(c);
            }
            if (sb.Length > 0)
            {
                _clean.Append(sb);
                _emit?.Invoke(sb.ToString());
            }
        }

        private static int LongestTagPrefixSuffix(string s)
        {
            int max = Math.Min(s.Length, ThinkOpen.Length - 1);
            for (int n = max; n >= 1; n--)
                if (s.EndsWith(ThinkOpen[..n], StringComparison.Ordinal)) return n;
            return 0;
        }

        /// <summary>整段尾部若是同字符刷屏(如 ))))))  ),裁掉重复段</summary>
        public static string CleanRepeatedTail(string text)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, @"(.)\1{7,}\s*$");
            return m.Success ? text[..m.Index].TrimEnd() : text;
        }
    }

    // ==================== 意图解析(LLM → JSON) ====================

    private async Task<AiIntent?> DetectIntentAsync(string userText, CancellationToken ct)
    {
        if (!await _engine.EnsureLoadedAsync())
            throw new InvalidOperationException("泡芙助理模型尚未就绪:" + _engine.StatusDetail);

        var history = new ChatHistory();
        history.AddMessage(AuthorRole.System, IntentSystemPrompt);
        history.AddMessage(AuthorRole.User, userText + "\n/no_think");

        var sb = new StringBuilder();
        var filter = new ChatTokenFilter(null);   // 意图输出不展示,但仍需滤掉思考段防污染 JSON
        try
        {
            // 意图输出很短;逐 token 累积,完成后做括号配平截取
            await _engine.ChatAsync(history, filter.Push, ct);
            filter.Flush();
            sb.Append(filter.FilteredText);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            App.WriteAppLog($"[泡芙助理] 意图推理异常:{ex.Message}");
            return null;
        }

        var intent = ParseIntentJson(sb.ToString());
        App.WriteAppLog($"[泡芙助理] 意图解析:{(intent == null ? "失败" : intent.Type)}");
        return intent;
    }

    internal static AiIntent? ParseIntentJson(string raw)
    {
        int start = raw.IndexOf('{');
        if (start < 0) return null;

        // 括号配平截取,容忍模型在 JSON 后追加的多余文字
        int depth = 0;
        int end = -1;
        bool inStr = false, esc = false;
        for (int i = start; i < raw.Length; i++)
        {
            char c = raw[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) { end = i; break; } }
        }
        string json = end > start ? raw.Substring(start, end - start + 1) : raw.Substring(start);

        try
        {
            var opt = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var intent = JsonSerializer.Deserialize<AiIntent>(json, opt);
            if (intent == null) return null;
            intent.Mods.RemoveAll(m => string.IsNullOrWhiteSpace(m?.Zh) && string.IsNullOrWhiteSpace(m?.En));
            return intent;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[泡芙助理] 意图 JSON 解析失败:{ex.Message} 原文:{Truncate(json, 300)}");
            return null;
        }
    }

    // ==================== 原版游戏本体下载 ====================

    /// <summary>无效版本统一拒答:不存在/格式错误的版本号(如 26.2、99.0)直接拒绝执行</summary>
    private static string InvalidVersionReply(string? requested) =>
        string.IsNullOrWhiteSpace(requested)
            ? "识别到无效 MC 版本,请输入正确版本(如 1.12.2、1.20.1)。"
            : $"识别到无效 MC 版本「{requested.Trim()}」,请输入正确版本(如 1.12.2、1.20.1)。";

    /// <summary>「帮我下载 1.12.2 / 帮我装 1.21-Forge」:装游戏本体,可选带加载器,装好即可启动</summary>
    private async Task<string> InstallClientAsync(string? gameVersion, string loader, Action<double, string>? progress, CancellationToken ct)
    {
        progress?.Invoke(0.05, "正在解析目标游戏版本…");
        var mv = await ResolveGameVersionAsync(gameVersion);
        if (mv == null)
            return InvalidVersionReply(gameVersion);

        // 重复检查:已有同名版本就不再重复下载
        if (_instances.Instances.Any(i => i.Name == mv.Id))
            return $"游戏版本 {mv.Id} 已经存在啦,直接在【版本管理】里就能看到,点启动就能玩。";

        bool withLoader = !string.IsNullOrEmpty(loader) && loader != "auto" && LoaderCandidates.Contains(loader);
        App.WriteAppLog($"[泡芙助理] 下载游戏本体:{gameVersion} → {mv.Id}(loader={loader})");
        int javaMajor = JavaRuntimeService.RecommendJavaMajor(mv.Id);
        var inst = _instances.CreateInstance(mv.Id, mv.Id, javaMajor);
        try
        {
            progress?.Invoke(0.15, $"正在安装游戏本体 {mv.Id}(libraries / assets / natives)…");
            bool ok = await _installer.InstallVersionAsync(inst.Id, mv);
            if (!ok)
            {
                _instances.DeleteInstance(inst.Id);
                string detail = _installer.LastError;
                return string.IsNullOrEmpty(detail)
                    ? "游戏本体下载失败(多为网络中断,详见日志),请稍后再试。"
                    : "游戏本体下载失败:" + detail;
            }

            // 可选加载器(如 1.21-Forge):本体装好后接着装,失败不回滚本体
            if (withLoader)
            {
                progress?.Invoke(0.75, $"正在安装加载器 {loader}…");
                string? loaderVersion = await PickStableLoaderVersionAsync(loader, mv.Id);
                var lr = await _loaderInstall.InstallLoaderAsync(inst.Id, mv.Id, loader, loaderVersion);
                if (!lr.Success)
                {
                    App.WriteAppLog($"[泡芙助理] 加载器安装失败:{lr.ErrorMessage}");
                    return $"游戏版本 {mv.Id} 已下载完成,但加载器 {loader} 安装失败:{lr.ErrorMessage}\n可在版本页手动重装加载器。";
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            try { _instances.DeleteInstance(inst.Id); } catch { }
            App.WriteAppLog($"[泡芙助理] 下载游戏本体异常:{ex.Message}");
            return "下载游戏本体时出错了,请稍后再试。";
        }
        progress?.Invoke(1.0, "完成");
        return $"游戏版本 {mv.Id}{(withLoader ? $"({loader})" : "")} 已安装完成,在【版本管理】里找到它就能直接启动~";
    }

    // ==================== 模组整合包构建管线 ====================

    private async Task<ModpackBuildResult> BuildModpackAsync(
        AiIntent intent, Action<double, string>? progress, CancellationToken ct)
    {
        var result = new ModpackBuildResult();
        try
        {
            // 1) 游戏版本解析
            progress?.Invoke(0.04, "正在解析目标游戏版本…");
            var mv = await ResolveGameVersionAsync(intent.GameVersion);
            if (mv == null)
            {
                result.Summary = InvalidVersionReply(intent.GameVersion);
                return result;
            }
            result.GameVersion = mv.Id;
            App.WriteAppLog($"[泡芙助理] 游戏版本解析:{intent.GameVersion} → {mv.Id}");

            // 2) 模组解析:Modrinth 检索 + MC 百科辅助映射(best effort)
            var resolved = new List<ResolvedMod>();
            int i = 0;
            foreach (var q in intent.Mods)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                double p = 0.06 + 0.20 * (i - 1) / intent.Mods.Count;
                progress?.Invoke(p, $"正在检索模组({i}/{intent.Mods.Count}):{(string.IsNullOrEmpty(q.Zh) ? q.En : q.Zh)}…");
                var rm = await ResolveModAsync(q, mv.Id, ct);
                if (rm?.Version != null)
                {
                    resolved.Add(rm);
                    App.WriteAppLog($"[泡芙助理] ✓ {rm.Display} → {rm.Project!.Title} {rm.Version.VersionNumber}(loaders: {string.Join("/", rm.Loaders)})");
                }
                else
                {
                    string label = string.IsNullOrEmpty(q.Zh) ? (q.En ?? "?") : q.Zh;
                    result.SkippedMods.Add($"{label} · 未找到适配 {mv.Id} 的版本");
                    App.WriteAppLog($"[泡芙助理] ✗ 未找到模组:{label}(zh={q.Zh}, en={q.En})");
                }
            }
            if (resolved.Count == 0)
            {
                result.Summary = $"所有模组都没有在 Modrinth 上找到适配 {mv.Id} 的版本,本次没有创建整合包。\n" +
                                 "提示:可以换个更常见的游戏版本(如 1.20.1、1.21.1),或确认模组名称。";
                return result;
            }

            // 3) 加载器仲裁:覆盖模组数最优;用户指定且全覆盖则尊重指定
            progress?.Invoke(0.28, "正在校验版本兼容性与加载器…");
            string loader = PickLoader(intent.Loader, resolved, result);
            result.Loader = loader;
            resolved = resolved.Where(r => r.Loaders.Contains(loader)).ToList();
            if (resolved.Count == 0)
            {
                result.Summary = $"模组与加载器 {loader} 均不兼容,本次没有创建整合包。";
                return result;
            }
            App.WriteAppLog($"[泡芙助理] 加载器仲裁:{loader}(兼容模组 {resolved.Count} 个)");

            // 4) 创建实例 + 安装游戏本体
            result.InstanceName = !string.IsNullOrWhiteSpace(intent.Name)
                ? intent.Name!.Trim()
                : MakeDefaultName(resolved, mv.Id);
            int javaMajor = JavaRuntimeService.RecommendJavaMajor(mv.Id);

            progress?.Invoke(0.30, $"正在创建整合包「{result.InstanceName}」…");
            var inst = _instances.CreateInstance(result.InstanceName, mv.Id, javaMajor);
            result.InstanceId = inst.Id;

            progress?.Invoke(0.33, "正在安装游戏本体(libraries / assets / natives)…");
            bool ok = await _installer.InstallVersionAsync(inst.Id, mv);
            if (!ok)
            {
                _instances.DeleteInstance(inst.Id);
                result.InstanceId = null;
                string detail = _installer.LastError;
                result.Summary = string.IsNullOrEmpty(detail)
                    ? "游戏本体安装失败,已回滚(多为网络中断,详见日志)。"
                    : "游戏本体安装失败,已回滚:" + detail;
                return result;
            }

            // 5) 安装加载器(取最新稳定版)
            progress?.Invoke(0.55, $"正在安装加载器 {loader}…");
            string? loaderVersion = await PickStableLoaderVersionAsync(loader, mv.Id);
            var lr = await _loaderInstall.InstallLoaderAsync(inst.Id, mv.Id, loader, loaderVersion);
            if (!lr.Success)
            {
                result.Notes.Add($"加载器安装失败:{lr.ErrorMessage}(模组已无法生效,可稍后在版本页重装加载器)");
                App.WriteAppLog($"[泡芙助理] 加载器安装失败:{lr.ErrorMessage}");
            }
            else
            {
                // InstallLoaderAsync 内部已回写 ModLoader/LoaderVersionId 并 SaveInstance
                App.WriteAppLog($"[泡芙助理] ✓ 加载器 {loader} {lr.InstalledVersion} 安装完成");
            }

            // 6) 逐模组下载(含 required 依赖自动解析,SHA1 校验由 ModrinthService 完成)
            string modsDir = _instances.GetModsDir(inst.Id);
            Directory.CreateDirectory(modsDir);
            int mi = 0;
            foreach (var rm in resolved)
            {
                ct.ThrowIfCancellationRequested();
                mi++;
                progress?.Invoke(0.58 + 0.38 * mi / resolved.Count, $"正在下载模组({mi}/{resolved.Count}):{rm.Display}…");
                try
                {
                    var (dlOk, files, failedDeps) = await _modrinth.InstallModWithDependenciesAsync(
                        rm.Version!, modsDir, mv.Id, loader, ct);
                    if (dlOk)
                    {
                        if (failedDeps.Count > 0)
                            result.InstalledMods.Add($"{rm.Display} {rm.Version!.VersionNumber}(已装 {files.Count} 文件,但 {failedDeps.Count} 个前置下载失败:{string.Join("、", failedDeps)})");
                        else
                            result.InstalledMods.Add($"{rm.Display} {rm.Version!.VersionNumber}(含依赖共 {files.Count} 个文件)");
                        App.WriteAppLog($"[泡芙助理] ✓ 已安装 {rm.Display} → {string.Join(", ", files)}");
                    }
                    else
                    {
                        result.SkippedMods.Add($"{rm.Display} · 下载失败");
                        App.WriteAppLog($"[泡芙助理] ✗ 下载失败:{rm.Display}");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.SkippedMods.Add($"{rm.Display} · {ex.Message}");
                    App.WriteAppLog($"[泡芙助理] 安装异常 {rm.Display}:{ex.Message}");
                }
            }

            // 7) 汇总
            progress?.Invoke(1.0, "完成");
            bool anyInstalled = result.InstalledMods.Count > 0 && lr.Success;
            result.Success = anyInstalled;
            result.Summary = BuildSummary(result, lr.Success);
            App.WriteAppLog($"[泡芙助理] 构建结束:成功={result.Success},已装 {result.InstalledMods.Count} 个模组,跳过 {result.SkippedMods.Count} 项");
            return result;
        }
        catch (OperationCanceledException)
        {
            if (result.InstanceId != null)
            {
                try { _instances.DeleteInstance(result.InstanceId); } catch { }
            }
            result.Success = false;
            result.Summary = "已取消本次制作,已清理半成品整合包。";
            return result;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[泡芙助理] 构建异常:{ex}");
            result.Success = false;
            result.Summary = "制作过程出错:" + ex.Message;
            return result;
        }
    }

    private static string BuildSummary(ModpackBuildResult r, bool loaderOk)
    {
        var sb = new StringBuilder();
        sb.Append(r.Success
            ? $"整合包「{r.InstanceName}」制作完成,可在本地版本列表直接启动。\n"
            : $"整合包「{r.InstanceName}」已创建但存在未完成项,请查看下方说明。\n");
        sb.Append($"游戏版本:{r.GameVersion}  ·  加载器:{(loaderOk ? r.Loader : r.Loader + "(安装失败)")}\n");
        if (r.InstalledMods.Count > 0)
            sb.Append("已安装模组:\n" + string.Join("\n", r.InstalledMods.Select(m => "  · " + m)) + "\n");
        if (r.SkippedMods.Count > 0)
            sb.Append("未安装:\n" + string.Join("\n", r.SkippedMods.Select(m => "  · " + m)) + "\n");
        foreach (var n in r.Notes) sb.Append(n + "\n");
        sb.Append("说明:模组数据与文件来自 Modrinth 官方接口(依赖已自动校验下载);MC 论坛(MCBBS)已闭站无公开接口,未纳入检索。");
        return sb.ToString().TrimEnd();
    }

    // ==================== 解析辅助 ====================

    private async Task<MojangVersion?> ResolveGameVersionAsync(string? requested)
    {
        var manifest = await _manifests.FetchManifestAsync();
        if (manifest == null || manifest.Versions.Count == 0) return null;

        if (string.IsNullOrWhiteSpace(requested))
        {
            // 未指定 → 最新正式版
            string latest = manifest.Latest.Release;
            return manifest.Versions.FirstOrDefault(v => v.Id == latest && v.Type == "release")
                   ?? manifest.Versions.FirstOrDefault(v => v.Type == "release");
        }

        string req = requested.Trim();
        var exact = manifest.Versions.FirstOrDefault(v => string.Equals(v.Id, req, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // 前缀匹配:「1.21」→ 最新 1.21.x release(清单按发布时间倒序,取第一个)
        return manifest.Versions.FirstOrDefault(v =>
            v.Type == "release" && v.Id.StartsWith(req + ".", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ResolvedMod?> ResolveModAsync(AiModQuery q, string gameVersion, CancellationToken ct)
    {
        string zh = (q.Zh ?? "").Trim();
        string en = (q.En ?? "").Trim();
        var rm = new ResolvedMod { Zh = zh, En = en };

        var project = await FindProjectAsync(zh, en, gameVersion, ct);
        if (project == null) return null;

        // 目标版本的可用文件(不先限定加载器,交由加载器仲裁)
        List<ModrinthVersion> versions;
        try { versions = await _modrinth.GetProjectVersionsAsync(project.ProjectId, gameVersion, null, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { App.WriteAppLog($"[泡芙助理] 版本列表获取失败 {project.Title}:{ex.Message}"); return null; }

        var best = versions.FirstOrDefault();
        if (best == null) return null;

        rm.Project = project;
        rm.Version = best;
        rm.Loaders = best.Loaders.Select(l => l.ToLowerInvariant()).Distinct().ToList();
        if (string.IsNullOrEmpty(rm.En)) rm.En = project.Title;
        return rm;
    }

    /// <summary>在 Modrinth 上检索模组项目(英文优先,中文走 MC 百科辅助映射);gameVersion 可空=不限版本</summary>
    private async Task<ModrinthProject?> FindProjectAsync(string zh, string en, string? gameVersion, CancellationToken ct)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(en)) candidates.Add(en);
        if (!string.IsNullOrEmpty(zh))
        {
            string? wikiAlias = await TryWikiAliasAsync(zh, ct);
            if (!string.IsNullOrEmpty(wikiAlias) && !candidates.Contains(wikiAlias, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(wikiAlias);
                App.WriteAppLog($"[泡芙助理] MC百科辅助映射:{zh} → {wikiAlias}");
            }
            candidates.Add(zh);
        }

        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(c)) continue;
            try
            {
                var res = await _modrinth.SearchAsync(c, 0, 8, gameVersion, null, "mod", "relevance", null, ct);
                var project = res.Hits
                    .Where(h => h.Loaders == null || h.Loaders.Count == 0 ||
                                h.Loaders.Any(l => LoaderCandidates.Contains(l)))
                    .OrderByDescending(h => h.Downloads)
                    .FirstOrDefault();
                if (project != null) return project;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { App.WriteAppLog($"[泡芙助理] 检索「{c}」异常:{ex.Message}"); }
        }
        return null;
    }

    // ==================== 模组查询 / 推荐 ====================

    /// <summary>查询类:适配哪些版本 / 需要什么前置 / 模组信息(数据来自 Modrinth)</summary>
    private async Task<string> QueryModAsync(string? mod, string? field, string? gameVersion,
        Action<double, string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mod))
            return "请告诉我要查询的模组名字,比如:匠魂模组适配哪些版本、机械动力需要什么前置。";

        progress?.Invoke(0.2, $"正在检索模组 {mod}…");
        var project = await FindProjectAsync(mod.Trim(), "", null, ct);
        if (project == null)
            return $"没有在 Modrinth 上找到模组「{mod}」,请确认模组名称(用英文原名更准)。";

        string f = (field ?? "info").Trim().ToLowerInvariant();
        try
        {
            if (f == "versions")
            {
                progress?.Invoke(0.5, "正在汇总适配版本…");
                var versions = await _modrinth.GetProjectVersionsAsync(project.ProjectId, null, null, ct);
                var gvs = versions.SelectMany(v => v.GameVersions)
                    .Where(g => System.Text.RegularExpressions.Regex.IsMatch(g, @"^\d+\.\d+(\.\d+)?$"))
                    .Distinct()
                    .OrderByDescending(VersionKey)
                    .Take(14)
                    .ToList();
                if (gvs.Count == 0)
                    return $"「{project.Title}」在 Modrinth 上没有找到标注的游戏版本信息。";
                return $"「{project.Title}」适配的游戏版本(新到旧,最多列 14 个):\n{string.Join("、", gvs)}\n想做整合包直接说:给我做一个 {gvs[0]} 的{mod.Trim()}整合包。";
            }

            if (f == "dependencies")
            {
                progress?.Invoke(0.5, "正在解析前置依赖…");
                string? gv = null;
                if (!string.IsNullOrWhiteSpace(gameVersion))
                {
                    var mv = await ResolveGameVersionAsync(gameVersion);
                    if (mv == null) return InvalidVersionReply(gameVersion);
                    gv = mv.Id;
                }
                var versions = await _modrinth.GetProjectVersionsAsync(project.ProjectId, gv, null, ct);
                var latest = versions.FirstOrDefault();
                if (latest == null)
                    return $"「{project.Title}」{(gv == null ? "" : $"在 {gv} 上")}没有找到可用版本,无法查看前置。";

                var required = latest.Dependencies
                    .Where(d => d.DependencyType == "required" && !string.IsNullOrEmpty(d.ProjectId))
                    .ToList();
                if (required.Count == 0)
                    return $"「{project.Title}」({latest.VersionNumber})不需要任何必装前置,直接装就能用。";

                var names = new List<string>();
                foreach (var d in required.Take(6))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var p = await _modrinth.GetProjectAsync(d.ProjectId!, ct);
                        names.Add(p?.Title ?? d.ProjectId!);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { names.Add(d.ProjectId!); }
                }
                int opt = latest.Dependencies.Count(d => d.DependencyType == "optional");
                return $"「{project.Title}」({latest.VersionNumber})需要的前置模组:\n" +
                       string.Join("\n", names.Select(n => "  · " + n)) +
                       (opt > 0 ? $"\n另有 {opt} 个可选前置(不装也能运行)。" : "") +
                       $"\n直接说「帮我装{mod.Trim()}并补全依赖」,我会把前置一起自动下载。";
            }

            // info:模组基本信息
            return $"「{project.Title}」\n{Truncate(project.Description, 150)}\n" +
                   $"下载量:{FormatDownloads(project.Downloads)} · 分类:{(project.Categories.Count > 0 ? string.Join("/", project.Categories) : "未标注")}";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            App.WriteAppLog($"[泡芙助理] 查询模组异常:{ex.Message}");
            return "查询时出错了:" + ex.Message;
        }
    }

    /// <summary>推荐/找模组:按版本 + 加载器 + 主题分类检索热门模组(下载量排序)</summary>
    private async Task<string> RecommendModsAsync(string? gameVersion, string loader, string? category,
        Action<double, string>? progress, CancellationToken ct)
    {
        progress?.Invoke(0.1, "正在解析游戏版本…");
        var mv = await ResolveGameVersionAsync(gameVersion);
        if (mv == null)
            return InvalidVersionReply(gameVersion);   // 未指定版本 → 自动取最新正式版

        // 主题词 → Modrinth 分类 facet;映射不上的(如 生存)退化为关键词检索
        string c = (category ?? "").Trim();
        string? categoryFacet = ModCategories.TryGetValue(c, out string? enCat) ? enCat : null;
        string query = categoryFacet == null ? c : "";
        string? loaderFacet = LoaderCandidates.Contains(loader) ? loader : null;

        progress?.Invoke(0.35, $"正在检索 {mv.Id} 上的热门模组…");
        var res = await _modrinth.SearchAsync(query, 0, 8, mv.Id, loaderFacet, "mod", "downloads", categoryFacet, ct);
        if (res.Hits.Count == 0)
            return $"没有在 Modrinth 上找到 {mv.Id}{(loaderFacet != null ? " " + loaderFacet : "")}{(c.Length > 0 ? $" 的「{c}」类" : "的")}模组,换个主题或版本试试。";

        var sb = new StringBuilder();
        sb.Append($"{mv.Id}{(loaderFacet != null ? " " + loaderFacet : "")}{(c.Length > 0 ? $"「{c}」类" : "")}热门模组(按下载量):\n");
        foreach (var h in res.Hits)
            sb.Append($"  · {h.Title} — {Truncate(h.Description, 60)}(下载 {FormatDownloads(h.Downloads)})\n");
        sb.Append($"想把它们直接装好?说一声:给我做一个 {mv.Id} 的整合包,列上想要的模组就行。");
        return sb.ToString().TrimEnd();
    }

    /// <summary>主题词 → Modrinth 分类映射(映射不上的退化为关键词检索)</summary>
    private static readonly Dictionary<string, string> ModCategories = new()
    {
        ["科技"] = "technology", ["魔法"] = "magic", ["冒险"] = "adventure",
        ["存储"] = "storage", ["优化"] = "optimization", ["装饰"] = "decoration",
        ["交通"] = "transportation", ["世界生成"] = "worldgen", ["实用"] = "utility",
        ["装备"] = "equipment", ["食物"] = "food", ["经济"] = "economy"
    };

    /// <summary>版本号排序键:1.20.1 → (1,20,1)</summary>
    private static (int, int, int) VersionKey(string v)
    {
        var parts = v.Split('.');
        int a = parts.Length > 0 && int.TryParse(parts[0], out int x) ? x : 0;
        int b = parts.Length > 1 && int.TryParse(parts[1], out int y) ? y : 0;
        int cc = parts.Length > 2 && int.TryParse(parts[2], out int z) ? z : 0;
        return (a, b, cc);
    }

    private static string FormatDownloads(long d) => d >= 10000 ? $"{d / 10000.0:F1} 万" : d.ToString();

    /// <summary>加载器仲裁:指定且全覆盖则尊重;否则取覆盖模组数最多者,同数优先 fabric&gt;neoforge&gt;forge</summary>
    private static string PickLoader(string? requested, List<ResolvedMod> resolved, ModpackBuildResult result)
    {
        string req = (requested ?? "auto").Trim().ToLowerInvariant();
        if (req != "auto" && LoaderCandidates.Contains(req))
        {
            if (resolved.All(r => r.Loaders.Contains(req))) return req;
            result.Notes.Add($"指定加载器 {req} 不能覆盖全部模组,已自动切换为覆盖数最优的加载器");
        }

        string best = LoaderCandidates[0];
        int bestCount = -1;
        foreach (var c in LoaderCandidates)
        {
            int cnt = resolved.Count(r => r.Loaders.Contains(c));
            if (cnt > bestCount) { bestCount = cnt; best = c; }
        }
        return best;
    }

    private async Task<string?> PickStableLoaderVersionAsync(string loader, string gameVersion)
    {
        try
        {
            var res = await _loaderProvider.GetVersionsAsync(loader, gameVersion);
            if (!res.Success) return null;
            return res.Versions.Where(x => x.IsStable).Take(60).FirstOrDefault()?.Version;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[泡芙助理] 加载器版本列表获取失败:{ex.Message}");
            return null;
        }
    }

    /// <summary>MC 百科(zh.minecraft.wiki)opensearch 尽力而为映射;失败返回 null,不阻塞主流程</summary>
    private static async Task<string?> TryWikiAliasAsync(string zh, CancellationToken ct)
    {
        try
        {
            string url = "https://zh.minecraft.wiki/api.php?action=opensearch&format=json&limit=3&search="
                         + Uri.EscapeDataString(zh);
            string json = await WikiHttp.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array &&
                doc.RootElement.GetArrayLength() >= 2 &&
                doc.RootElement[1].ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement[1].EnumerateArray())
                {
                    string? s = item.GetString();
                    // 仅接受 ASCII 条目名作为英文别名候选
                    if (!string.IsNullOrWhiteSpace(s) && s.All(c => c < 128)) return s;
                }
            }
            return null;
        }
        catch { return null; }
    }

    private static string MakeDefaultName(List<ResolvedMod> resolved, string versionId)
    {
        string core = string.Join("+", resolved.Take(2).Select(r =>
            string.IsNullOrEmpty(r.Zh) ? r.En : r.Zh));
        if (core.Length > 18) core = core.Substring(0, 18);
        return $"泡芙定制·{core}·{versionId}";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    // ==================== 启动器本地功能调用(主题/账号/启动游戏) ====================

    /// <summary>UI 线程安全执行:主题/页面跳转等 WPF 资源操作必须在 Dispatcher 上</summary>
    private static void RunOnUi(Action action)
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp == null) { action(); return; }
        if (disp.CheckAccess()) action();
        else disp.Invoke(action);
    }

    /// <summary>主窗口转 ShellWindow(供页面跳转)</summary>
    private static ShellWindow? GetShell() => System.Windows.Application.Current?.MainWindow as ShellWindow;

    /// <summary>切换主题/配色:dark/light 直达,未指明则在当前主题间对切</summary>
    private string SetThemeCommand(string? themeReq, Action<string>? onToken)
    {
        var themes = App.Services.GetRequiredService<ThemeService>();
        var cfg = App.Services.GetRequiredService<ConfigService>().Config;

        string t = (themeReq ?? "").Trim().ToLowerInvariant();
        string newTheme;
        if (t.Contains("dark") || t.Contains("黑") || t.Contains("夜")) newTheme = "Dark";
        else if (t.Contains("light") || t.Contains("亮") || t.Contains("白")) newTheme = "Light";
        else newTheme = cfg.Theme == "Dark" ? "Light" : "Dark";   // 未指明 = 对切

        if (cfg.Theme == newTheme)
        {
            string same = $"现在已经是{(newTheme == "Dark" ? "深色" : "浅色")}主题啦,不用切~";
            onToken?.Invoke(same);
            return same;
        }
        RunOnUi(() => themes.SetTheme(newTheme));
        string reply = $"已为你切换到{(newTheme == "Dark" ? "深色" : "浅色")}主题,看看效果如何?";
        App.WriteAppLog($"[泡芙助理] 主题已切换:{cfg.Theme} → {newTheme}");
        onToken?.Invoke(reply);
        return reply;
    }

    /// <summary>创建离线账号(昵称走原版 MC 同款校验 1~16 字符),创建后自动切换为当前账号</summary>
    private string CreateOfflineAccountCommand(string? name, Action<string>? onToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            const string guide = "好呀,告诉我离线账号叫什么名字就行~\n例如:帮我做一个离线账号,名字叫芙芙";
            onToken?.Invoke(guide);
            return guide;
        }

        string? err = AuthService.ValidateOfflineNickname(name);
        if (err != null)
        {
            string invalid = $"这个昵称不能用:{err}。换一个告诉我吧(1~16 字符,支持字母/数字/下划线/中文)。";
            onToken?.Invoke(invalid);
            return invalid;
        }

        var auth = App.Services.GetRequiredService<AuthService>();
        var accounts = App.Services.GetRequiredService<AccountService>();
        string nick = name.Trim();
        var acc = auth.LoginOffline(nick);
        accounts.SetCurrentAccount(acc.Uuid);
        App.WriteAppLog($"[泡芙助理] 离线账号已创建并切换:{nick}");
        string reply = $"离线账号「{nick}」已创建,并切换到这个账号了,现在就能直接启动游戏。";
        onToken?.Invoke(reply);
        return reply;
    }

    /// <summary>打开微软登录:跳转账号页(微软正版登录卡就在页面上,一键授权)</summary>
    private string OpenMicrosoftLoginCommand(Action<string>? onToken)
    {
        RunOnUi(() => GetShell()?.Switch(ShellWindow.DeckKey.Accounts));
        App.WriteAppLog("[泡芙助理] 已打开账号页,引导微软登录");
        const string reply = "已经帮你打开账号页了,点一下「微软账号登录」按钮,浏览器会自动弹出授权,授权完成我就帮你把账号接进来。";
        onToken?.Invoke(reply);
        return reply;
    }

    /// <summary>切换账号:指名则模糊匹配切换;未指名则列出全部账号或跳转账号页</summary>
    private string SwitchAccountCommand(string? name, Action<string>? onToken)
    {
        var accounts = App.Services.GetRequiredService<AccountService>();
        var list = accounts.Accounts;
        if (list.Count == 0)
        {
            const string none = "现在还没有任何账号。可以说:帮我做一个离线账号名字叫xxx,或者:打开微软登录。";
            onToken?.Invoke(none);
            return none;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var names = string.Join("、", list.Select(a => a.Username));
            string reply = $"现在有这些账号:{names}。告诉我要切到哪个?或者我帮你打开账号页慢慢挑。";
            RunOnUi(() => GetShell()?.Switch(ShellWindow.DeckKey.Accounts));
            onToken?.Invoke(reply);
            return reply;
        }

        string key = name.Trim();
        var target = list.FirstOrDefault(a => string.Equals(a.Username, key, StringComparison.OrdinalIgnoreCase))
                     ?? list.FirstOrDefault(a => a.Username.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            var names = string.Join("、", list.Select(a => a.Username));
            string reply = $"没找到叫「{key}」的账号。现有账号:{names}";
            onToken?.Invoke(reply);
            return reply;
        }

        accounts.SetCurrentAccount(target.Uuid);
        App.WriteAppLog($"[泡芙助理] 账号已切换:{target.Username}");
        string ok = $"已切换到账号「{target.Username}」{(target.Type == AccountType.Offline ? "(离线)" : "(微软)")}。";
        onToken?.Invoke(ok);
        return ok;
    }

    /// <summary>启动游戏:指定版本/名称则匹配实例,未指定则上次启动的版本(再退第一个);
    /// 账号令牌/Java/文件完整性校验全部复用主页同款 LaunchAsync 链路</summary>
    private async Task<string> LaunchGameCommandAsync(string? versionOrName, Action<double, string>? progress, CancellationToken ct)
    {
        var launch = App.Services.GetRequiredService<GameLaunchService>();
        if (launch.IsGameRunning)
            return "游戏已经在运行中啦,不用重复启动~要先结束它的话去主页点「结束游戏」。";

        var insts = _instances.Instances;
        if (insts.Count == 0)
            return "还没有安装任何游戏版本,没法启动。可以说:帮我安装 1.20.1,我来帮你装好。";

        var cfgService = App.Services.GetRequiredService<ConfigService>();
        GameInstance? target = null;
        string key = (versionOrName ?? "").Trim();
        if (key.Length > 0)
        {
            // 精确版本 → 前缀版本 → 名称包含(如「启动 1.21」命中 1.21.1-Forge 类实例)
            target = insts.FirstOrDefault(i => string.Equals(i.VersionId, key, StringComparison.OrdinalIgnoreCase))
                     ?? insts.FirstOrDefault(i => i.VersionId.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                     ?? insts.FirstOrDefault(i => i.Name.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                var names = string.Join("、", insts.Select(i => $"{i.Name}({i.VersionId})"));
                return $"没找到「{key}」对应的游戏版本。已安装的有:{names}";
            }
        }
        else
        {
            // 未指定:上次启动的版本优先,其次第一个(与主页版本框默认选中逻辑一致)
            target = insts.FirstOrDefault(i => i.Id == cfgService.Config.LastInstanceId) ?? insts[0];
        }

        progress?.Invoke(0.6, $"正在启动 {target.Name}…");
        App.WriteAppLog($"[泡芙助理] 启动游戏:{target.Name}({target.Id})");
        var result = await launch.LaunchAsync(target.Id);
        if (!result.Success)
            return $"启动失败:{result.ErrorMessage}";

        cfgService.Config.LastInstanceId = target.Id;
        cfgService.Save();
        return $"游戏已启动:{target.Name}(版本 {target.VersionId}),玩得开心~";
    }
}
