// FufuLauncher - 下载引擎(全量重构版)
// Copyright © FufuLauncher
//
// 架构:
// 1. 统一队列:任务按类别(Game/Asset/Java/Mod/Other)分派独立信号量,互不阻塞;
//    并发数由 Config.DownloadThreads 驱动,限速由 Config.DownloadRateLimitMbps 驱动,
//    代理由 Config.ProxyUrl 驱动,全部设置页可调。
// 2. 多批次可并行(2026-08-28 修复):新批次不再取消旧批次,多个下载请求可同时跑。
// 3. 单文件:Range 断点续传(.partial);大文件(≥4MB)自动多分片并发下载(主流多线程分段方案,
//    默认 8 分片;未知大小先 HEAD 探测;服务端不支持 Range 自动回退单连接)。
// 4. 30 秒活动看门狗:流式读取期间持续无字节即判定连接僵死,触发重试。
// 5. 失败指数退避重试(双源交替:官方 ↔ BMCLAPI 互为兜底)。
// 6. SHA1 校验(可由 Config.VerifyAfterDownload 关闭),失败自动重下一次。
// 7. 暂停保留断点,继续时自动重新入队续传(不再依赖调用方重发)。
// 8. 双进度:单文件字节级 + 全局累计,节流 150ms,完成时强制收尾。
// 9. 磁盘空间预检(1GB 缓冲),错误全分类中文提示,异常全捕获不崩。

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FufuLauncher.Next.UI;

namespace FufuLauncher.Services;

public class DownloadTaskItem
{
    public string Url { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public long Downloaded { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
    public string Error { get; set; } = "";
    public int RetryCount { get; set; }
    /// <summary>任务类别,决定使用哪个独立并发槽位</summary>
    public DownloadCategory Category { get; set; } = DownloadCategory.Game;
    /// <summary>是否为大文件分片下载(内部使用)</summary>
    public bool IsSharded { get; set; }
    /// <summary>分片断点续传(2026-09-27):本文件上次采用的分片总数。
    /// 与本次算出的分片数不一致(用户改了分片设置)时,段位索引失效,按新方案从头预分配。</summary>
    public int ShardTotal { get; set; }
    /// <summary>分片断点续传(2026-09-27):已完成段位索引(升序去重)。
    /// 失败/重试/重启后只补这些段位以外的部分,大文件不再整份重下。</summary>
    public List<int> DoneShards { get; set; } = new();
    /// <summary>开始下载时间(2026-09-26:完成状态显示耗时)</summary>
    public DateTime StartedAt { get; set; }
    /// <summary>实际下载耗时(2026-09-26)</summary>
    public TimeSpan? Duration { get; set; }
}

public enum DownloadStatus { Pending, Downloading, Paused, Verifying, Completed, Failed, Cancelled }
public enum DownloadCategory { Game, Asset, Java, Mod, Other }

public class DownloadProgressInfo
{
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public TimeSpan EstimatedRemaining { get; set; }
    /// <summary>进度永远钳位 [0,1],防止解压体积/断点重复计数导致进度条溢出</summary>
    public double Progress => TotalBytes > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0.0, 1.0) : 0;
}

public class DownloadService
{
    private readonly ConfigService _configService;
    private readonly NativeInteropService _nativeInterop;
    private readonly HttpClient _httpClient;
    /// <summary>代理客户端(BlockHelm-7:按下载类别分别决定走不走代理;null = 未配置代理)</summary>
    private HttpClient? _proxyHttpClient;

    /// <summary>单任务取消令牌(BlockHelm-1:支持删除/单独撤销正在下载的单条任务)</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _taskCts = new();
    /// <summary>已被用户从队列删除的任务 Url(取消后不再回落成「已暂停」,直接判为已取消)</summary>
    private readonly ConcurrentDictionary<string, byte> _removedUrls = new();
    /// <summary>代理已禁用(连续网络失败自动回退直连,重新保存代理配置时重置)</summary>
    private bool _proxyDisabled;
    private int _proxyFailStreak;
    /// <summary>BMCLAPI 连续失败计数(镜像抽风自动降级官方,成功一次归零)</summary>
    private int _bmclFailStreak;
    private bool BmclHealthy => _bmclFailStreak < 2;
    /// <summary>自有镜像连续失败计数(自建源抖动时自动退回 BMCLAPI,成功一次归零)</summary>
    private int _customFailStreak;
    private bool CustomHealthy => _customFailStreak < 2;
    /// <summary>单任务暂停集合(2026-09-26:不被全局「继续」误恢复,全局暂停后并入全局)</summary>
    private readonly HashSet<string> _singlePausedUrls = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>本次会话累计下载统计(2026-09-26:完成提示展示「共下载 N 个文件 / X MB」)</summary>
    private long _sessionDownloadedBytes;
    private int _sessionFileCount;
    public long SessionDownloadedBytes => _sessionDownloadedBytes;
    public int SessionFileCount => _sessionFileCount;
    /// <summary>本会话已计入总进度的 URL(重试/重复安装不再重复累加总字节)</summary>
    private readonly HashSet<string> _overallCountedPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>全局批次令牌(暂停/取消时统一撤销)</summary>
    private CancellationTokenSource? _cts;

    // 各类别独立并发槽位(构造时按 Config.DownloadThreads 推导,互不阻塞)
    private readonly SemaphoreSlim _semGame;
    private readonly SemaphoreSlim _semAsset;
    private readonly SemaphoreSlim _semJava;
    private readonly SemaphoreSlim _semMod;
    private readonly SemaphoreSlim _semOther;
    /// <summary>分片并发总闸(2026-09-26 调优):分片请求绕过类别信号量,若不设上限,
    /// N 个并发大文件 × M 分片会瞬时打爆连接池(MaxConnectionsPerServer 512);
    /// 统一按线程数封顶,保证「多文件并发」与「单文件多分片」总量可控。</summary>
    private readonly SemaphoreSlim _semShard;

    /// <summary>运行中任务(供暂停/取消遍历)</summary>
    private readonly ConcurrentDictionary<string, DownloadTaskItem> _tasks = new();

    /// <summary>队列自动落盘定时器(2026-09-27):下载进行中每 15s 落一次断点,
    /// 进程被强杀/断电也只丢最后一次快照,不至于整份大文件重下。
    /// 无活跃任务时回调直接返回,不做任何 IO。</summary>
    private readonly Timer _queueSaveTimer;

    /// <summary>最大重试次数(额外尝试次数,默认 3 次,总计 4 次尝试)</summary>
    public int MaxRetry { get; set; } = 3;
    /// <summary>当前是否处于暂停状态</summary>
    public bool IsPaused { get; private set; }
    /// <summary>大文件分片阈值(≥4MB 自动分片;主流多线程下载方案起步值)</summary>
    public long ShardThreshold { get; set; } = 4L * 1024 * 1024;
    /// <summary>大文件分片数(2026-08-28 由 8 提升至 16:配合 60+ 线程并发档位,大文件下载提速明显;
    /// 实际分片数受文件大小约束:每片不小于 4MB)</summary>
    public int ShardCount { get; set; } = 16;

    /// <summary>流式读取活动看门狗:持续无字节超过该秒数判定连接僵死</summary>
    private const int IdleWatchdogSeconds = 30;

    /// <summary>服务端不支持 Range 请求时抛出,分片下载捕获后回退单连接(避免 200 全量响应被当分片写坏文件)</summary>
    private sealed class RangeNotSupportedException : Exception
    {
        public RangeNotSupportedException() : base("服务端不支持 Range 请求,回退单连接下载") { }
    }

    // ==================== 事件(公开 API,与 UI/服务层契约) ====================

    /// <summary>单文件字节级进度(UI 子进度条)</summary>
    public event Action<DownloadTaskItem, DownloadProgressInfo>? ProgressChanged;
    public event Action<DownloadTaskItem>? TaskCompleted;
    public event Action<DownloadTaskItem>? TaskFailed;
    /// <summary>任务状态变化(排队/下载中/校验/暂停/完成/失败/取消),供任务面板实时刷新</summary>
    public event Action<DownloadTaskItem>? TaskStatusChanged;
    /// <summary>全局总进度(所有批次累计,节流 150ms)</summary>
    public event Action<DownloadProgressInfo>? OverallProgressChanged;
    /// <summary>下载源切换提示(域名/文件,消息)——切源自救时让用户看到下载在动,而非"卡死"</summary>
    public event Action<string, string>? SourceSwitched;

    /// <summary>当前批次任务列表(任务面板数据源,生命周期随最近一次 DownloadAllAsync)。
    /// 多批次并行后读写需经 _batchLock;外部遍历一律用 CurrentBatchSnapshot() 取快照(2026-08-28 全局审计修复)</summary>
    public List<DownloadTaskItem> CurrentBatch { get; } = new();
    private readonly object _batchLock = new();

    /// <summary>线程安全的批次快照(供 UI/重试等外部遍历,避免与并发批次收口争用)</summary>
    public List<DownloadTaskItem> CurrentBatchSnapshot()
    {
        lock (_batchLock) { return new List<DownloadTaskItem>(CurrentBatch); }
    }

    // 全局总进度统计(跨批累计)
    private long _overallTotalBytes;
    private long _overallDownloadedBytes;
    private long _lastOverallReportBytes;
    private long _lastOverallReportMs;
    private readonly object _overallLock = new();
    private readonly Stopwatch _overallSw = Stopwatch.StartNew();

    // 全局限速(令牌窗口:每 1s 窗口内字节数超限时延迟等待)
    private readonly object _rateLock = new();
    private long _rateWindowStartMs;
    private long _rateWindowBytes;

    public DownloadService(ConfigService configService, NativeInteropService nativeInterop)
    {
        _configService = configService;
        _nativeInterop = nativeInterop;

        // 并发槽位由配置线程数推导(硬性下限 60,上限 256),资产类小文件多给、Java 大压缩包少给;
        // 资产槽位 = 线程数*2(最高 512),需 MaxConnectionsPerServer 同步放宽避免连接池成为瓶颈。
        int threads = Math.Clamp(configService.Config.DownloadThreads, 60, 256);
        _semGame = new SemaphoreSlim(Math.Max(2, threads));
        _semAsset = new SemaphoreSlim(Math.Max(4, threads * 2));
        _semJava = new SemaphoreSlim(Math.Max(2, threads / 2));
        _semMod = new SemaphoreSlim(Math.Max(2, threads / 2));
        _semOther = new SemaphoreSlim(Math.Max(2, threads / 2));
        _semShard = new SemaphoreSlim(Math.Max(4, threads));   // 分片总闸与线程数同级
        // 队列断点定时落盘(仅在有活跃任务时真正写盘)
        _queueSaveTimer = new Timer(_ => { if (!_tasks.IsEmpty) SaveQueue(); }, null, 15000, 15000);
        // 大文件分片数上限由下载设置驱动(4~32 保护区间)
        ShardCount = Math.Clamp(configService.Config.DownloadShardCount, 4, 32);

        // 直连客户端常驻;代理客户端按需另建一份,两者互不干扰
        _httpClient = BuildClient(null);
        BuildProxyClient();
    }

    /// <summary>直连 HttpClient 单例(2026-09-25 根治 socket 耗尽:
    /// 每次 new HttpClient 会残留 TIME_WAIT 连接,下载数百文件=数百连接占满端口;
    /// 单例复用线程安全,超时语义与原来一致)</summary>
    private static readonly object _directClientLock = new();
    private static HttpClient? _directClient;

    private static HttpClient BuildDirectClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            // 与线程上限对齐(资产槽位最高 512 并发),避免单主机连接数限流拖慢多线程下载
            MaxConnectionsPerServer = 512,
            // 2026-09-25 根治:显式禁用系统代理。HttpClientHandler 默认 UseProxy=true 会读
            // Windows 系统全局代理(Clash/v2ray 等科学上网工具)——系统代理一挂(曾实测
            // 15.749333.xyz:443 失联)所有下载反复失败 30 分钟,用户未配显式代理却无从排查。
            // 仅下方显式配置 ProxyUrl 时才启用代理,系统级代理一律不碰。
            UseProxy = false
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        // BMCLAPI / Mojang 均建议携带 User-Agent,缺失可能被限流或拒绝
        c.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/3.0 (Windows)");
        return c;
    }

    /// <summary>构建 HttpClient(proxyUrl 为空即直连单例)</summary>
    private static HttpClient BuildClient(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            var hit = _directClient;
            if (hit != null) return hit;
            lock (_directClientLock)
            {
                if (_directClient == null) _directClient = BuildDirectClient();
                return _directClient;
            }
        }
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 512,
            UseProxy = false
        };
        try
        {
            handler.Proxy = new WebProxy(proxyUrl, BypassOnLocal: true);
            handler.UseProxy = true;
        }
        catch (Exception ex)
        {
            // 安全审计:代理地址可能内嵌 user:pass 凭据,日志只记脱敏后的地址(见 MaskUrl)
            App.WriteAppLog($"[下载] 代理配置无效已忽略({SensitiveData.MaskUrl(proxyUrl)}): {ex.Message}");
        }
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        // BMCLAPI / Mojang 均建议携带 User-Agent,缺失可能被限流或拒绝
        c.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/3.0 (Windows)");
        return c;
    }

    /// <summary>按当前配置构建代理客户端(没配代理则为 null)</summary>
    private void BuildProxyClient()
    {
        string proxy = (_configService.Config.ProxyUrl ?? "").Trim();
        if (proxy.Length == 0)
        {
            _proxyHttpClient = null;
            return;
        }
        _proxyHttpClient = BuildClient(proxy);
        App.WriteAppLog($"[下载] 代理已就绪: {SensitiveData.MaskUrl(proxy)}");
    }

    /// <summary>
    /// 按下载类别选客户端(BlockHelm-7:游戏核心下载与模组下载各自独立的代理开关)。
    /// 开关关掉或根本没配代理时一律回落直连客户端。
    /// </summary>
    private HttpClient ClientFor(DownloadCategory cat)
    {
        var p = _proxyHttpClient;
        if (p == null || _proxyDisabled) return _httpClient;
        bool useProxy = cat == DownloadCategory.Mod
            ? _configService.Config.UseProxyForMod
            : _configService.Config.UseProxyForGame;
        return useProxy ? p : _httpClient;
    }

    /// <summary>设置页改了代理地址后调用:重建代理客户端,不用重启启动器</summary>
    public void ReloadProxy()
    {
        // 重新保存代理配置 = 用户显式重启用:清掉自动回退状态
        _proxyDisabled = false;
        _proxyFailStreak = 0;
        // 旧客户端不主动 Dispose:可能仍有在途请求,强行释放会打断正在跑的下载;
        // 交给 GC 回收即可(HttpClientHandler 终结器会关掉连接池)
        BuildProxyClient();
        App.WriteAppLog($"[下载] 代理配置已重载:{(_proxyHttpClient == null ? "直连" : "已启用")}");
    }

    /// <summary>下载源(含自有镜像地址)变更后调用:清掉自动降级计数与 Auto 测速缓存,
    /// 让新源立刻生效——否则刚填对的镜像会被上一轮的失败计数继续屏蔽 5 分钟</summary>
    public void NotifySourceChanged()
    {
        _customFailStreak = 0;
        _bmclFailStreak = 0;
        _autoSource = "";
        _autoSourceCheckedAt = DateTime.MinValue;
        App.WriteAppLog($"[下载源] 已切换到:{CurrentSourceName}");
    }

    /// <summary>校验自有镜像地址:探测「版本清单 + Fabric 元数据 + 库文件」三条代表路径,
    /// 返回可直接展示给用户的结论。三条分开探测是因为镜像只做了一半时,
    /// 只测清单会显示「可用」,用户装到库文件阶段才发现缺东西。</summary>
    public async Task<(bool Ok, string Message)> ValidateCustomMirrorAsync(string rawBase)
    {
        string? base_ = MirrorUrlMap.NormalizeBase(rawBase);
        if (base_ == null)
            return (false, string.IsNullOrWhiteSpace(rawBase)
                ? "地址为空:填形如 https://dl.你的域名 的完整地址,留空即不启用"
                : "地址无效:必须以 https:// 开头、不带空格与查询参数(明文 http 会被拒绝)");

        var probes = new (string Name, string Url)[]
        {
            ("版本清单", base_ + "/mc/game/version_manifest_v2.json"),
            ("Fabric 元数据", base_ + "/fabric-meta/v2/versions/loader/1.20.1"),
            ("库文件索引", base_ + "/maven/net/minecraftforge/forge/maven-metadata.xml")
        };
        var results = await Task.WhenAll(probes.Select(async p =>
        {
            var sw = Stopwatch.StartNew();
            bool ok = false;
            string state = "不可达";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var resp = await ClientFor(DownloadCategory.Game)
                    .GetAsync(p.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                sw.Stop();
                ok = resp.IsSuccessStatusCode;
                state = ok ? $"{sw.ElapsedMilliseconds}ms" : $"HTTP {(int)resp.StatusCode}";
            }
            catch (OperationCanceledException) { sw.Stop(); state = "超时(15s)"; }
            catch (Exception ex) { sw.Stop(); state = ex.GetType().Name; }
            return (p.Name, Ok: ok, State: state);
        }));

        int hit = results.Count(r => r.Ok);
        string detail = string.Join("  ·  ", results.Select(r => $"{r.Name} {r.State}"));
        // 措辞上不能把「超时」说成「还没同步到」——前者多半是链路问题,后者会让人白等镜像
        return hit == results.Length
            ? (true, $"三条路径全部可达|{detail}")
            : hit > 0
                ? (true, $"可用,{results.Length - hit} 条路径没取到(取不到的会自动回落到 BMCLAPI 与官方源)|{detail}")
                : (false, $"一条都取不到|{detail}|若全是超时请先查网络/代理,全 404 才说明镜像没同步或没开公开读");
    }

    // ==================== 全局总进度 ====================

    /// <summary>重置全局总进度(开始新下载任务前调用)</summary>
    public void ResetOverallProgress()
    {
        lock (_overallLock)
        {
            // 2026-09-25 并发下载修复:仍有活跃下载任务时不清零——否则下载游戏A时
            // 再下载游戏B/模组/整合包,后者的 Reset 会把 A 的总进度清零、UI 跳回 0;
            // 新任务体积会经 AddOverallTotalBytes 正确累加(URL 去重已有)
            lock (_batchLock)
            {
                bool hasActive = CurrentBatch.Any(t =>
                    t.Status is DownloadStatus.Pending or DownloadStatus.Downloading or DownloadStatus.Paused);
                if (hasActive) return;
            }
            _overallTotalBytes = 0;
            _overallDownloadedBytes = 0;
            _lastOverallReportBytes = 0;
            _lastOverallReportMs = _overallSw.ElapsedMilliseconds;
        }
        // 新安装批次从 0 开始:清空已计 URL,后续批次全部重新计入(重复安装场景也准确)
        _overallCountedPaths.Clear();
        OverallProgressChanged?.Invoke(new DownloadProgressInfo());
    }

    /// <summary>累加全局总字节数(外部模块调用,如解压阶段补计体积)</summary>
    public void AddOverallTotalBytes(long bytes)
    {
        lock (_overallLock) { _overallTotalBytes += bytes; }
    }

    /// <summary>累加全局已下载字节并节流触发事件(完成时强制收尾)</summary>
    public void AddOverallDownloadedBytes(long deltaBytes)
    {
        lock (_overallLock)
        {
            _overallDownloadedBytes += deltaBytes;
            long nowMs = _overallSw.ElapsedMilliseconds;
            long elapsed = nowMs - _lastOverallReportMs;
            bool forceFire = _overallTotalBytes > 0 && _overallDownloadedBytes >= _overallTotalBytes;
            if (elapsed < 150 && !forceFire) return;

            long total = _overallTotalBytes, downloaded = _overallDownloadedBytes;
            // 2026-09-26 修复:重试任务字节重复累加会超总量——钳位到总量,避免"94.2MB/93.0MB"
            if (downloaded > total) downloaded = total;
            double speed = elapsed > 0 ? (downloaded - _lastOverallReportBytes) / (elapsed / 1000.0) : 0;
            _lastOverallReportBytes = downloaded;
            _lastOverallReportMs = nowMs;
            OverallProgressChanged?.Invoke(new DownloadProgressInfo
            {
                TotalBytes = total,
                DownloadedBytes = downloaded,
                SpeedBytesPerSec = speed,
                // 2026-09-26 修复:下完时剩余时间归零(原来 TimeSpan.MaxValue 格式化溢出成"48:05")
                EstimatedRemaining = speed > 0 && total > downloaded
                    ? TimeSpan.FromSeconds((total - downloaded) / speed)
                    : (total <= downloaded ? TimeSpan.Zero : TimeSpan.MaxValue)
            });
        }
    }

    // ==================== 下载源解析(官方 ↔ BMCLAPI 双源) ====================

    // 智能自动档缓存:首次下载前并发测两源延迟,选低者;缓存 5 分钟避免每次下载都测速
    private string _autoSource = "";
    private DateTime _autoSourceCheckedAt = DateTime.MinValue;

    /// <summary>解析智能自动档当前源(BMCLAPI / 官方),测速失败默认 BMCLAPI(国内首选)</summary>
    private string ResolveAutoSource()
    {
        if (_autoSource != "" && (DateTime.Now - _autoSourceCheckedAt).TotalMinutes < 5)
            return _autoSource;
        string best = "BMCLAPI";
        try
        {
            // 并发测两源版本清单延迟,谁快用谁(HMCL 同款思路;测速失败的一方直接排除)
            var tasks = new[]
            {
                ("Mojang", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"),
                ("BMCLAPI", "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json")
            }.Select(async pair =>
            {
                long ms = -1;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var resp = await ClientFor(DownloadCategory.Game)
                        .GetAsync(pair.Item2, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    sw.Stop();
                    if (resp.IsSuccessStatusCode) ms = sw.ElapsedMilliseconds;
                }
                catch { /* 该源不可达 */ }
                return (pair.Item1, ms);
            }).ToList();
            Task.WaitAll(tasks.ToArray());
            long bestMs = long.MaxValue;
            foreach (var r in tasks.Select(x => x.Result))
            {
                if (r.ms >= 0 && r.ms < bestMs) { bestMs = r.ms; best = r.Item1; }
            }
        }
        catch { /* 测速整体失败用默认 */ }
        _autoSource = best;
        _autoSourceCheckedAt = DateTime.Now;
        App.WriteAppLog($"[下载源] 智能自动已选:{best}");
        return best;
    }

    /// <summary>当前生效下载源名称(用户配置 + Auto 实际解析),供 UI 展示</summary>
    public string CurrentSourceName
    {
        get
        {
            string src = _configService.Config.DownloadSource;
            if (src == "Auto")
                return ResolveAutoSource() == "BMCLAPI" ? "BMCLAPI(自动)" : "官方(自动)";
            if (src == "Custom")
            {
                string? b = CustomBaseUrl;
                if (string.IsNullOrEmpty(b)) return "官方(自有镜像地址无效)";
                return CustomHealthy ? $"自有镜像({b})" : "BMCLAPI(自有镜像暂时降级)";
            }
            return src == "BMCLAPI" ? "BMCLAPI" : "官方";
        }
    }

    /// <summary>自有镜像根地址(已规范化;未启用或地址非法返回 null)</summary>
    public string? CustomBaseUrl
        => _configService.Config.DownloadSource == "Custom"
            ? MirrorUrlMap.NormalizeBase(_configService.Config.CustomDownloadBaseUrl)
            : null;

    /// <summary>获取当前下载源对应的 URL(Auto 自动测速选最快源 / BMCLAPI 镜像 / 自有镜像 / 官方)</summary>
    public string GetSourceUrl(string originalUrl)
    {
        string src = _configService.Config.DownloadSource;
        if (src == "Auto")
            return ResolveAutoSource() == "BMCLAPI" ? (BmclHealthy ? ForceBmclUrl(originalUrl) : originalUrl) : originalUrl;
        // 2026-09-26 修复:BMCLAPI 连续下载失败自动降级官方——镜像抽风时不再锁死慢源
        if (src == "BMCLAPI")
            return BmclHealthy ? ForceBmclUrl(originalUrl) : originalUrl;
        // 2026-09-30:自有镜像连续失败自动退回 BMCLAPI——自建源刚上线时抽风不影响装机
        if (src == "Custom")
        {
            string? b = CustomBaseUrl;
            if (string.IsNullOrEmpty(b)) return originalUrl;
            return CustomHealthy ? MirrorUrlMap.ToCustom(originalUrl, b) : ForceBmclUrl(originalUrl);
        }
        return originalUrl;
    }

    /// <summary>获取下载 URL(带重试次数):多源交替互为兜底,避免单源抽风反复失败</summary>
    public string GetSourceUrl(string originalUrl, int attempt)
    {
        if (attempt <= 0) return GetSourceUrl(originalUrl);
        string? custom = CustomBaseUrl;
        if (custom != null)
            // 自有镜像 → 官方 → BMCLAPI 三档轮转:自建源覆盖不全时下一跳就有人补
            return (attempt % 3) switch
            {
                1 => originalUrl,
                2 => ForceBmclUrl(originalUrl),
                _ => MirrorUrlMap.ToCustom(originalUrl, custom)
            };
        // Auto 与 BMCLAPI 同策略:奇数次回退官方,偶数次用镜像——双源交替兜底,单源抽风不卡死
        bool mirrorFirst = _configService.Config.DownloadSource != "Mojang";
        if (mirrorFirst)
            return attempt % 2 == 1 ? originalUrl : ForceBmclUrl(originalUrl);
        return ForceBmclUrl(originalUrl);                                        // 官方源重试切镜像
    }

    /// <summary>模组下载源 URL(独立于游戏源;当前仅 Modrinth,其余降级原始 URL)</summary>
    public string GetModSourceUrl(string originalUrl) => originalUrl;

    /// <summary>强制替换为 BMCLAPI 镜像 URL(Mojang 官方 + Maven 仓库国内加速)</summary>
    private static string ForceBmclUrl(string originalUrl) => MirrorUrlMap.ToBmclapi(originalUrl);

    private SemaphoreSlim GetSemaphore(DownloadCategory cat) => cat switch
    {
        DownloadCategory.Game => _semGame,
        DownloadCategory.Asset => _semAsset,
        DownloadCategory.Java => _semJava,
        DownloadCategory.Mod => _semMod,
        _ => _semOther
    };

    /// <summary>任务状态机唯一入口,保证 UI 与内部状态一致</summary>
    private void Transition(DownloadTaskItem task, DownloadStatus next)
    {
        if (task.Status == next) return;
        App.WriteLog(App.AppLogLevel.Debug, $"[下载状态机] {Path.GetFileName(task.LocalPath)}:{task.Status} → {next}");
        task.Status = next;
        try { TaskStatusChanged?.Invoke(task); } catch { /* UI 异常不影响下载链路 */ }
    }

    // ==================== 批量下载(统一队列入口) ====================

    /// <summary>批量下载,返回全部是否成功</summary>
    public async Task<bool> DownloadAllAsync(List<DownloadTaskItem> tasks)
    {
        // 2026-09-26:空批次/空列表直接成功,不做无意义处理
        if (tasks == null || tasks.Count == 0) return true;
        if (IsPaused)
        {
            App.WriteAppLog("[下载] 当前处于暂停状态,拒绝启动新批次");
            return false;
        }

        // 2026-09-26 修复:新批次入队先重置总进度——上一批全完成后不重置,
        // 新任务总量累加但已下字节残留,出现"总进度100% + 剩余48:05 + 94.2MB/93.0MB"。
        // Reset 内部有活跃任务保护:上一批还在跑(并行批次)时不清零,正常累加。
        ResetOverallProgress();

        // 多批次并行(2026-08-28 修复):新任务并入当前批次一起跑,不再取消旧批次——
        // 旧行为会踢掉正在下载的任务,导致用户反馈「只能一个一个下」。全部任务共享同一令牌,
        // 暂停/取消依旧全局生效(遍历 _tasks 字典)。
        // 2026-09-25:入队去重——同对象重复入队(加载器多源换 URL 重试场景)只保留一次,
        // 避免 CurrentBatch 出现同一任务的多个引用导致「任务总数」虚高、列表与计数不同步
        lock (_batchLock)
        {
            var seen = new HashSet<DownloadTaskItem>();
            foreach (var t in tasks)
                if (seen.Add(t)) CurrentBatch.Add(t);
        }

        // 2026-09-25 修复:同目标文件去重——不同镜像 URL(双源)可能指向同一 LocalPath,
        // 队列已有该文件的活跃任务时再并发写 .partial 会互锁(FileShare.None 独占),
        // 表现「文件被占用」且重试反复失败(真实日志:23:27 与 08:12 各出现整轮 4 连败)。
        // 直接复用旧任务,新任务标记已完成跳过;旧任务完成即代表文件就绪。
        var dups = new HashSet<DownloadTaskItem>();
        lock (_batchLock)
        {
            foreach (var t in tasks)
            {
                var dup = CurrentBatch.FirstOrDefault(x => !ReferenceEquals(x, t)
                    && (x.Status is DownloadStatus.Pending or DownloadStatus.Downloading)
                    && string.Equals(x.LocalPath, t.LocalPath, StringComparison.OrdinalIgnoreCase));
                if (dup != null)
                {
                    dups.Add(t);
                    t.Downloaded = dup.Downloaded;
                    t.Size = dup.Size;
                    t.Status = DownloadStatus.Completed;
                }
            }
        }

        // 磁盘空间预检(含 1GB 缓冲)。重复任务(与队列活跃任务同文件)不计入空间与总进度
        long totalSize = 0, newTotal = 0;
        foreach (var t in tasks)
        {
            if (dups.Contains(t)) continue;
            totalSize += Math.Max(0, t.Size);
            // 2026-09-25 修复:总进度只计入首次入队的文件路径——重试/换源/重复入队
            // (同文件多个镜像 URL)不再重复累加总字节,根治「已下载 > 总计」虚高与多源虚低
            if (_overallCountedPaths.Add(t.LocalPath)) newTotal += Math.Max(0, t.Size);
        }
        if (!CheckDiskSpace(totalSize, out string diskMsg))
        {
            DialogKit.Warn(
                $"磁盘剩余空间不足,无法开始下载。\n\n{diskMsg}\n\n" +
                $"本次下载需要约 {totalSize / 1024.0 / 1024.0:F1} MB 空间。",
                "磁盘空间不足");
            return false;
        }
        if (newTotal > 0) AddOverallTotalBytes(newTotal);

        // 批次令牌:不存在或已撤销(用户全部取消过)才重建,绝不误杀在跑的旧批次任务
        if (_cts == null || _cts.IsCancellationRequested)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }
        var cts = _cts;

        var allTasks = new List<Task<bool>>(tasks.Count);
        foreach (var task in tasks)
        {
            if (dups.Contains(task)) { allTasks.Add(Task.FromResult(true)); continue; }
            _tasks[task.Url] = task;
            if (task.Status is DownloadStatus.Pending or DownloadStatus.Paused)
                Transition(task, DownloadStatus.Pending);
            allTasks.Add(DownloadOneAsync(task, cts.Token));
        }

        var results = await Task.WhenAll(allTasks);
        // 面板收口:批次已多批合并,只摘除已终态任务,未完成的保留可见可重试;
        // 终态超过 200 个时顺带裁掉最早的,防面板无限膨胀。
        // 加锁:多个并发批次同时完成时会同时收口,裸 List 并发 RemoveAll 会损坏内部数组(2026-08-28 全局审计修复)
        lock (_batchLock)
        {
            CurrentBatch.RemoveAll(t => t.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled);
            if (CurrentBatch.Count > 200)
                CurrentBatch.RemoveRange(0, CurrentBatch.Count - 200);
        }
        foreach (var t in tasks)
        {
            if (t.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
                _tasks.TryRemove(t.Url, out _);
        }
        SaveQueue();   // 批次收口即落盘:队列清空则删除断点文件,仍有未完成项则更新快照
        return Array.TrueForAll(results, r => r);
    }

    /// <summary>下载前校验目标磁盘剩余空间(预留 1GB 缓冲)</summary>
    public static bool CheckDiskSpace(long requiredBytes, out string message)
    {
        try
        {
            string checkDir = App.AppDataDir;
            string? root = Path.GetPathRoot(checkDir);
            if (string.IsNullOrEmpty(root))
                root = Path.GetPathRoot(Environment.SystemDirectory) ?? throw new InvalidOperationException("无法确定磁盘根目录");
            var drive = new DriveInfo(root);
            long available = drive.AvailableFreeSpace;
            long needed = requiredBytes + 1024L * 1024 * 1024;
            if (available < needed)
            {
                message = $"盘符 {drive.Name} 剩余 {available / 1024.0 / 1024 / 1024:F1} GB," +
                          $"本次下载需要 {needed / 1024.0 / 1024 / 1024:F1} GB(含 1GB 缓冲)。";
                return false;
            }
            message = $"盘符 {drive.Name} 剩余 {available / 1024.0 / 1024 / 1024:F1} GB,空间充足。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"磁盘空间检测异常:{ex.Message}";
            return false;
        }
    }

    // ==================== 单任务执行(重试 + 校验 + 状态机) ====================

    private async Task<bool> DownloadOneAsync(DownloadTaskItem task, CancellationToken ct)
    {
        // 单任务令牌:全局暂停/取消与「删除这一条」都能撤销它;
        // 下游一律用重绑后的 ct,不用改每个子方法的签名
        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _taskCts[task.Url] = taskCts;
        try
        {
            task.StartedAt = DateTime.Now;   // 2026-09-26:耗时统计起点(重试会刷新)
            return await DownloadOneCoreAsync(task, taskCts.Token);
        }
        finally
        {
            // 原子比对删除:任务被重新入队时字典里可能已经是新令牌,不能误删
            _taskCts.TryRemove(new KeyValuePair<string, CancellationTokenSource>(task.Url, taskCts));
            _removedUrls.TryRemove(task.Url, out _);
        }
    }

    private async Task<bool> DownloadOneCoreAsync(DownloadTaskItem task, CancellationToken ct)
    {
        var sem = GetSemaphore(task.Category);
        try { await sem.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            Transition(task, CancelTargetState(task));
            return false;
        }
        try
        {
            Transition(task, DownloadStatus.Downloading);
            string? dir = Path.GetDirectoryName(task.LocalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 主流多线程方案补强:未知大小的任务先 HEAD 探测体积,达标自动升级为分片并发;
            // 探测失败不阻断,照旧走单连接续传。
            await ProbeSizeAsync(task, ct);

            for (int attempt = 0; attempt <= MaxRetry; attempt++)
            {
                try
                {
                    // 大文件分片并发,小文件单连接 Range 续传;
                    // 服务端不支持 Range(分片收到 200)时不消耗重试机会,直接回退单连接续传。
                    try
                    {
                        if (task.Size >= ShardThreshold || (task.Size == 0 && task.IsSharded))
                            await DownloadShardedAsync(task, attempt, ct);
                        else
                            await DownloadWithRangeAsync(task, attempt, ct);
                    }
                    catch (RangeNotSupportedException)
                    {
                        App.WriteAppLog($"[下载] {Path.GetFileName(task.LocalPath)} 服务端不支持分片,回退单连接续传");
                        // 2026-09-27:分片路径预分配过整份 .partial(含未填充的空洞),
                        // 单连接续传是按文件长度当续传起点,直接复用会把空洞当已下载数据 →
                        // 必须先清残片与段位表,让单连接从 0 重新下。
                        CleanupPartial(task);
                        task.Downloaded = 0;
                        await DownloadWithRangeAsync(task, attempt, ct);
                    }

                    // SHA1 校验(设置可关闭),失败删除重下一次
                    Transition(task, DownloadStatus.Verifying);
                    if (!VerifySha1(task.LocalPath, task.Sha1))
                    {
                        App.WriteAppLog($"[下载] SHA1 校验失败,重下:{Path.GetFileName(task.LocalPath)}");
                        TryDeleteFile(task.LocalPath);
                        task.Downloaded = 0;
                        if (task.Size >= ShardThreshold)
                            await DownloadShardedAsync(task, attempt, ct);
                        else
                            await DownloadWithRangeAsync(task, attempt, ct);
                        if (!VerifySha1(task.LocalPath, task.Sha1))
                            throw new InvalidDataException($"文件 SHA1 校验失败:{Path.GetFileName(task.LocalPath)}");
                    }

                    if (task.Url.Contains("bmclapi")) _bmclFailStreak = 0;   // 镜像成功一次即恢复
                    // 2026-09-30:自有镜像按「本次实际请求的 URL」计数,不是按配置——
                    // 降级到 BMCLAPI 的那几次成功不该把自建源的计数归零
                    string actualUrl = ResolveUrl(task, attempt);
                    if (MirrorUrlMap.IsCustomUrl(actualUrl, CustomBaseUrl)) _customFailStreak = 0;
                    Transition(task, DownloadStatus.Completed);
                    long finalSize = 0;
                    try { finalSize = new FileInfo(task.LocalPath).Length; } catch { }
                    // 2026-09-26:成功即从字典摘除(防几百个库任务导致字典无限增长);
                    // 会话累计统计(完成提示里可见「本次会话共下载…」)
                    _tasks.TryRemove(task.Url, out _);
                    task.Duration = DateTime.Now - task.StartedAt;   // 2026-09-26:记录耗时
                    _sessionFileCount++;
                    _sessionDownloadedBytes += finalSize;
                    App.WriteAppLog($"[下载] 成功 {Path.GetFileName(task.LocalPath)} | 大小={finalSize} 字节 | " +
                                    $"哈希={(string.IsNullOrEmpty(task.Sha1) ? "无" : task.Sha1)} | URL={task.Url}");
                    try { TaskCompleted?.Invoke(task); } catch { }
                    return true;
                }
                catch (OperationCanceledException)
                {
                    // 暂停保留断点,取消清理残片
                    bool removed = _removedUrls.ContainsKey(task.Url);
                    Transition(task, CancelTargetState(task));
                    task.Error = removed ? "已从下载队列删除" : (IsPaused ? "下载已暂停(断点已保留)" : "下载已取消");
                    if (removed || !IsPaused) CleanupPartial(task);
                    return false;
                }
                catch (Exception ex)
                {
                    // 代理连不上自动回退直连:连续 3 次网络类失败即本会话禁用代理,
                    // 全部改走直连(设置页重新保存代理配置后 ReloadProxy 会重置)。
                    if (_proxyHttpClient != null && !_proxyDisabled &&
                        ex is HttpRequestException or IOException or TaskCanceledException)
                    {
                        _proxyFailStreak++;
                        if (_proxyFailStreak >= 3)
                        {
                            _proxyDisabled = true;
                            App.WriteAppLog("[下载] 代理连续多次连接失败,已全局回退直连下载(设置页重新保存代理配置可启用)");
                        }
                    }
                    task.RetryCount = attempt + 1;
                    task.Error = ClassifyDownloadError(ex, task.Url);
                    // 2026-09-26:BMCLAPI 连续失败计数(上限 3,自动降级官方源)
                    if (task.Url.Contains("bmclapi")) _bmclFailStreak = Math.Min(3, _bmclFailStreak + 1);
                    // 2026-09-30:自有镜像按本次实际请求的 URL 计数,连续 2 次失败退回 BMCLAPI
                    string failedUrl = ResolveUrl(task, attempt);
                    if (MirrorUrlMap.IsCustomUrl(failedUrl, CustomBaseUrl))
                        _customFailStreak = Math.Min(3, _customFailStreak + 1);
                    App.WriteAppLog($"[下载] 第 {attempt + 1} 次失败 {Path.GetFileName(task.LocalPath)}:{ex.GetType().Name} - {ex.Message}");
                    // 切源自救提示:首次失败后重试会切换镜像/官方源,让用户看到下载在动而非"卡死"
                    if (attempt == 0)
                        SourceSwitched?.Invoke(Path.GetFileName(task.LocalPath),
                            "当前下载源响应缓慢,已自动切换镜像重试");
                    if (attempt >= MaxRetry)
                    {
                        Transition(task, DownloadStatus.Failed);
                        App.WriteAppLog($"[下载] 最终失败(已重试 {MaxRetry} 次) {Path.GetFileName(task.LocalPath)} | " +
                                        $"原因={task.Error} | URL={task.Url}");
                        CleanupPartial(task);
                        try { TaskFailed?.Invoke(task); } catch { }
                        return false;
                    }
                    // 2026-09-26:限流(429)不原地重试 —— 429 是该主机明确拒绝,同源再撞只会
                    // 继续被拒(旧实现每次白等 5s/10s/15s,加载器安装器卡 30 秒才换源,用户观感就是
                    // 「镜像源极不稳定」)。立刻把该 URL 判失败,交给上层多镜像逻辑秒切下一个源。
                    if ((task.Error ?? "").Contains("429"))
                    {
                        Transition(task, DownloadStatus.Failed);
                        App.WriteAppLog($"[下载] 命中限流(429),立即换源 {Path.GetFileName(task.LocalPath)} | URL={task.Url}");
                        CleanupPartial(task);
                        try { TaskFailed?.Invoke(task); } catch { }
                        return false;
                    }
                    // 退避:普通失败 1s/2s/4s 指数
                    int delayMs = 1000 * (1 << attempt);
                    try { await Task.Delay(delayMs, ct); }
                    catch (OperationCanceledException)
                    {
                        Transition(task, CancelTargetState(task));
                        return false;
                    }
                }
            }
            return false;
        }
        finally { sem.Release(); }
    }

    /// <summary>被撤销时该落到哪个状态:用户主动删除 → 已取消;全局暂停 → 已暂停(保留断点)</summary>
    private DownloadStatus CancelTargetState(DownloadTaskItem task)
        => _removedUrls.ContainsKey(task.Url)
            ? DownloadStatus.Cancelled
            : _singlePausedUrls.Contains(task.Url)
                ? DownloadStatus.Paused
                : !IsPaused
                    ? DownloadStatus.Cancelled
                    : DownloadStatus.Paused;

    /// <summary>解析任务真实下载 URL(模组类别走独立模组源)</summary>
    private string ResolveUrl(DownloadTaskItem task, int attempt)
        => task.Category == DownloadCategory.Mod
            ? GetModSourceUrl(GetSourceUrl(task.Url, attempt))
            : GetSourceUrl(task.Url, attempt);

    /// <summary>HEAD 探测文件体积与 Range 支持(分片下载的前提):
    /// 仅当大小未知且最终文件不存在时执行;探测失败静默返回,不阻断下载。</summary>
    private async Task ProbeSizeAsync(DownloadTaskItem task, CancellationToken ct)
    {
        if (task.Size > 0 || File.Exists(task.LocalPath)) return;
        string url = ResolveUrl(task, 0);
        try
        {
            using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headCts.CancelAfter(TimeSpan.FromSeconds(IdleWatchdogSeconds));
            using var resp = await ClientFor(task.Category).SendAsync(new HttpRequestMessage(HttpMethod.Head, url),
                HttpCompletionOption.ResponseHeadersRead, headCts.Token);
            if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is long cl && cl > 0)
            {
                // 2026-09-26 修复:未知大小任务探测到真实大小后补计总进度总量
                // (下载字节按实际累加、总量漏计会导致「已下载 > 总计」超量)
                if (task.Size <= 0) AddOverallTotalBytes(cl);
                task.Size = cl;
                bool rangeOk = resp.Headers.AcceptRanges.Contains("bytes");
                App.WriteAppLog($"[下载] 大小探测 {Path.GetFileName(task.LocalPath)}:{cl / 1024.0 / 1024.0:F1} MB | Range={(rangeOk ? "支持" : "不支持")}");
                if (!rangeOk) return;   // 不支持 Range:保持单连接,分片路由会因 200 响应再次兜底
                if (task.Size >= ShardThreshold) task.IsSharded = true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 探测失败照旧单连接下载,不阻断 */ }
    }

    // ==================== 单连接 Range 断点续传 ====================

    private async Task DownloadWithRangeAsync(DownloadTaskItem task, int attempt, CancellationToken ct)
    {
        string url = ResolveUrl(task, attempt);
        long startPosition = task.Downloaded;
        string partPath = task.LocalPath + ".partial";

        // 幂等:最终文件已存在且校验通过直接跳过(计入总进度防进度条卡住)
        if (File.Exists(task.LocalPath) && VerifySha1(task.LocalPath, task.Sha1))
        {
            task.Downloaded = new FileInfo(task.LocalPath).Length;
            AddOverallDownloadedBytes(task.Downloaded);
            return;
        }

        // 断点续传:从 .partial 继续
        if (startPosition == 0 && File.Exists(partPath))
        {
            try { startPosition = new FileInfo(partPath).Length; task.Downloaded = startPosition; }
            catch { startPosition = 0; }
        }
        if (task.Size > 0 && startPosition >= task.Size)
        {
            if (File.Exists(partPath)) File.Move(partPath, task.LocalPath, overwrite: true);
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (startPosition > 0)
            req.Headers.Range = new RangeHeaderValue(startPosition, null);

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(TimeSpan.FromSeconds(IdleWatchdogSeconds));
        HttpResponseMessage? resp = null;
        try
        {
            resp = await ClientFor(task.Category).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
            resp.EnsureSuccessStatusCode();

            // 200 而非 206:服务端不支持断点续传,从头开始
            if (startPosition > 0 && resp.StatusCode == HttpStatusCode.OK)
            {
                startPosition = 0;
                task.Downloaded = 0;
                TryDeleteFile(partPath);
            }
            if (task.Size == 0 && resp.Content.Headers.ContentLength is long len)
                task.Size = startPosition + len;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(partPath, startPosition > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true);
            var buffer = new byte[64 * 1024];
            int read;
            var sw = Stopwatch.StartNew();
            long lastReportBytes = task.Downloaded;

            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await ThrottleAsync(read, ct);
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                task.Downloaded += read;
                AddOverallDownloadedBytes(read);
                watchdog.CancelAfter(TimeSpan.FromSeconds(IdleWatchdogSeconds));   // 有字节即重置看门狗

                if (sw.ElapsedMilliseconds > 200)
                {
                    double speed = (task.Downloaded - lastReportBytes) / sw.Elapsed.TotalSeconds;
                    ReportSingleProgress(task, speed);
                    lastReportBytes = task.Downloaded;
                    sw.Restart();
                }
            }
            await dst.FlushAsync(ct);
            if (File.Exists(task.LocalPath)) TryDeleteFile(task.LocalPath);
            File.Move(partPath, task.LocalPath);
            ReportSingleProgress(task, 0);
        }
        finally { resp?.Dispose(); }
    }

    // ==================== 大文件多 Range 分片并发 ====================

    private async Task DownloadShardedAsync(DownloadTaskItem task, int attempt, CancellationToken ct)
    {
        string url = ResolveUrl(task, attempt);
        string partPath = task.LocalPath + ".partial";
        task.IsSharded = true;

        if (File.Exists(task.LocalPath) && VerifySha1(task.LocalPath, task.Sha1))
        {
            task.Downloaded = new FileInfo(task.LocalPath).Length;
            AddOverallDownloadedBytes(task.Downloaded);
            return;
        }

        // 未知大小先 HEAD 探测(30 秒超时)
        if (task.Size <= 0)
        {
            using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headCts.CancelAfter(TimeSpan.FromSeconds(IdleWatchdogSeconds));
            using var headResp = await ClientFor(task.Category).SendAsync(new HttpRequestMessage(HttpMethod.Head, url),
                HttpCompletionOption.ResponseHeadersRead, headCts.Token);
            if (headResp.Content.Headers.ContentLength is long cl) task.Size = cl;
            if (task.Size <= 0) throw new IOException("无法获取文件大小,无法分片下载");
        }

        // 分片范围(每片不小于 4MB)
        int shards = Math.Clamp((int)(task.Size / (4L * 1024 * 1024)), 1, ShardCount);
        long shardSize = task.Size / shards;
        var ranges = new List<(long Start, long End)>();
        for (int i = 0; i < shards; i++)
        {
            long s = i * shardSize;
            long e = (i == shards - 1) ? task.Size - 1 : (s + shardSize - 1);
            ranges.Add((s, e));
        }

        // 预分配 .partial 与断点复用(2026-09-27 分片续传):
        // - 同尺寸 .partial + 同一套分片方案 → 复用已完成段位,只补缺失段位(大文件失败/重试
        //   不再整份重下;这正是「断点续传」对 ≥4MB 大文件的核心价值);
        // - 新任务、或用户在设置里改过分片数导致段位索引失效 → 从头预分配并清空段位表。
        bool resumable = task.ShardTotal == shards
            && File.Exists(partPath) && new FileInfo(partPath).Length == task.Size;
        if (resumable)
        {
            task.DoneShards.RemoveAll(i => i < 0 || i >= shards);
            // 已下载字节一律以「已完成段位」为准:上一轮某段写到一半就失败时,
            // 那部分字节早被计进 task.Downloaded,只有按段位重算才不会重复累加。
            long doneBytes = task.DoneShards.Sum(i => ranges[i].End - ranges[i].Start + 1);
            lock (_overallLock)
            {
                long old = Math.Max(0, task.Downloaded);
                if (old != doneBytes) _overallDownloadedBytes = Math.Max(0, _overallDownloadedBytes - old + doneBytes);
            }
            task.Downloaded = doneBytes;
            if (task.DoneShards.Count > 0)
                App.WriteAppLog($"[下载] {Path.GetFileName(task.LocalPath)} 分片续传:已完成 " +
                                $"{task.DoneShards.Count}/{shards} 段,直接跳过 {doneBytes / 1024.0 / 1024.0:F1} MB");
        }
        else
        {
            // 旧实现每次重试都 FileMode.Create 截断重下(2026-09-26 修复过进度重复累加,
            // 但断点一并丢掉了)——现在只在真正不可续时才从头来过。
            lock (_overallLock)
            {
                long old = Math.Max(0, task.Downloaded);
                if (old > 0) _overallDownloadedBytes = Math.Max(0, _overallDownloadedBytes - old);
            }
            using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
                fs.SetLength(task.Size);
            task.Downloaded = 0;
            task.DoneShards.Clear();
            task.ShardTotal = shards;
        }

        var progressLock = new object();
        var sw = Stopwatch.StartNew();
        long lastReport = 0;
        var doneLock = new object();

        var shardTasks = Enumerable.Range(0, shards).Select(i => Task.Run(async () =>
        {
            // 断点续传:上一轮已完成的段位直接跳过,不重复消耗流量
            if (task.DoneShards.Contains(i)) return;
            var range = ranges[i];
            // 分片总闸:并发分片数封顶,避免「多文件并发 × 单文件多分片」打爆连接池
            await _semShard.WaitAsync(ct);
            HttpResponseMessage? resp = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(range.Start, range.End);
                using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
                watchdog.CancelAfter(TimeSpan.FromSeconds(IdleWatchdogSeconds));
                resp = await ClientFor(task.Category).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
                resp.EnsureSuccessStatusCode();
                // 206 才算分片成功:服务端不支持 Range 时会返回 200 全量响应,
                // 继续写会把整份文件写进单个分片区段,造成文件损坏——立即抛异常回退单连接。
                if (resp.StatusCode != HttpStatusCode.PartialContent)
                    throw new RangeNotSupportedException();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                // 各分片写各自不重叠区段,共享文件句柄
                using var dst = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.Write,
                    bufferSize: 64 * 1024, useAsync: true);
                dst.Position = range.Start;
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await ThrottleAsync(read, ct);
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    watchdog.CancelAfter(TimeSpan.FromSeconds(IdleWatchdogSeconds));
                    lock (progressLock)
                    {
                        task.Downloaded += read;
                        AddOverallDownloadedBytes(read);
                        if (sw.ElapsedMilliseconds > 200)
                        {
                            double speed = (task.Downloaded - lastReport) / sw.Elapsed.TotalSeconds;
                            ReportSingleProgress(task, speed);
                            lastReport = task.Downloaded;
                            sw.Restart();
                        }
                    }
                }
                // 整段写完才登记:写一半就失败的段位不登记,下次从该段起点重下(覆盖旧残数据)
                lock (doneLock)
                    if (!task.DoneShards.Contains(i)) task.DoneShards.Add(i);
            }
            finally { resp?.Dispose(); _semShard.Release(); }
        }, ct)).ToList();

        try
        {
            await Task.WhenAll(shardTasks);
        }
        catch
        {
            // 2026-09-27:失败不再删除 .partial —— 完整的段位 + 段位表本身就是可续的断点,
            // 下一轮(重试/重启续传)只补缺失段位。取消/移除/最终失败仍由 CleanupPartial 清残片。
            App.WriteAppLog($"[下载] {Path.GetFileName(task.LocalPath)} 分片中断,已保留断点 " +
                            $"({task.DoneShards.Count}/{shards} 段),下次只补未完成段位");
            throw;
        }
        if (File.Exists(task.LocalPath)) TryDeleteFile(task.LocalPath);
        File.Move(partPath, task.LocalPath);
        task.DoneShards.Clear();
        task.ShardTotal = 0;
        ReportSingleProgress(task, 0);
    }

    // ==================== 全局限速(1 秒滑动窗口) ====================

    /// <summary>限速闸门:Config.DownloadRateLimitMbps > 0 时按秒窗口限速,0 = 不限</summary>
    private async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        double limitMbps = _configService.Config.DownloadRateLimitMbps;
        if (limitMbps <= 0) return;
        long limitBytes = (long)(limitMbps * 125000);   // Mbps → Bytes/s
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long delayMs = 0;
            lock (_rateLock)
            {
                long now = Environment.TickCount64;
                if (now - _rateWindowStartMs >= 1000)
                {
                    _rateWindowStartMs = now;
                    _rateWindowBytes = 0;
                }
                _rateWindowBytes += bytes;
                if (_rateWindowBytes > limitBytes)
                    delayMs = Math.Min(1000, (long)((_rateWindowBytes - limitBytes) * 1000.0 / limitBytes) + 20);
            }
            if (delayMs <= 0) return;
            await Task.Delay((int)delayMs, ct);
        }
    }

    private void ReportSingleProgress(DownloadTaskItem task, double speed)
    {
        // 2026-09-25 修复:事件携带 task 引用——DownloadDeck 才能定位到对应行刷新进度条,
        // 否则多任务并发时每行进度都动不了(旧实现只能猜"唯一下载中任务")
        ProgressChanged?.Invoke(task, new DownloadProgressInfo
        {
            TotalBytes = task.Size,
            DownloadedBytes = task.Downloaded,
            SpeedBytesPerSec = speed,
            EstimatedRemaining = speed > 0 && task.Size > task.Downloaded
                ? TimeSpan.FromSeconds((task.Size - task.Downloaded) / speed)
                : TimeSpan.MaxValue
        });
    }

    // ==================== 暂停 / 继续 / 取消 / 回滚 ====================

    /// <summary>暂停全部下载(保留 .partial 断点)</summary>
    public void Pause()
    {
        IsPaused = true;
        _singlePausedUrls.Clear();   // 全局暂停后,单暂停并入全局(继续时一起恢复)
        _cts?.Cancel();
        foreach (var t in _tasks.Values)
            if (t.Status == DownloadStatus.Downloading) Transition(t, DownloadStatus.Paused);
        App.WriteAppLog("[下载] 用户暂停全部下载任务");
        SaveQueue();   // 暂停态落盘:重启后保持暂停,不会自己跑流量
    }

    /// <summary>继续下载:解除暂停并把暂停中的任务自动重新入队(断点自动续传)</summary>
    public void Resume()
    {
        IsPaused = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var paused = CurrentBatchSnapshot().Where(t => t.Status == DownloadStatus.Paused).ToList();
        foreach (var t in paused)
        {
            if (_singlePausedUrls.Contains(t.Url)) continue;   // 单任务暂停的不被全局恢复
            _tasks[t.Url] = t;
            _ = DownloadOneAsync(t, token);   // 后台续传,不阻塞调用方
        }
        App.WriteAppLog($"[下载] 用户继续下载,自动续传 {paused.Count} 个暂停任务");
        SaveQueue();   // 暂停解除后同步落盘(队列此刻仍在跑,由定时器接力)
    }

    /// <summary>单任务暂停:只停这一条,保留断点,其余任务继续跑(2026-09-26 补全)</summary>
    public void PauseTask(DownloadTaskItem task)
    {
        if (task == null || task.Status is not (DownloadStatus.Downloading or DownloadStatus.Pending)) return;
        _singlePausedUrls.Add(task.Url);
        if (_taskCts.TryGetValue(task.Url, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        Transition(task, DownloadStatus.Paused);
        task.Error = "单任务已暂停(断点已保留)";
        App.WriteAppLog($"[下载] 单任务暂停:{Path.GetFileName(task.LocalPath)}");
        SaveQueue();
    }

    /// <summary>单任务继续:恢复该条任务,断点自动续传(2026-09-26 补全)</summary>
    public void ResumeTask(DownloadTaskItem task)
    {
        if (task == null || task.Status != DownloadStatus.Paused) return;
        _singlePausedUrls.Remove(task.Url);
        if (_cts == null || _cts.IsCancellationRequested)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }
        _tasks[task.Url] = task;
        _ = DownloadOneAsync(task, _cts.Token);
        App.WriteAppLog($"[下载] 单任务继续:{Path.GetFileName(task.LocalPath)}");
        SaveQueue();
    }

    // ==================== 下载队列落盘 / 重启续传(2026-09-27) ====================
    // 「后台下载」的另一半:进程退出(含关窗到托盘后真正退出、异常退出)时,
    // 把未完成任务连同断点(分片段位表)落盘;下次启动自动接着下,
    // 大文件已完成段位不会重下 —— 与 DownloadShardedAsync 的段位续传配套。

    /// <summary>队列落盘条目:只存恢复所需的最小字段</summary>
    private sealed class QueueEntry
    {
        public string Url { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public string? Sha1 { get; set; }
        public long Size { get; set; }
        public DownloadCategory Category { get; set; }
        public bool IsSharded { get; set; }
        public int ShardTotal { get; set; }
        public List<int> DoneShards { get; set; } = new();
    }

    /// <summary>队列落盘根对象(附带全局暂停态,重启后暂停中不会自己跑流量)</summary>
    private sealed class QueueFileModel
    {
        public bool Paused { get; set; }
        public List<QueueEntry> Tasks { get; set; } = new();
    }

    private static readonly JsonSerializerOptions QueueJson = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }   // 枚举按名字存,加成员不会错位
    };

    /// <summary>把未完成任务 + 断点写盘(等待/下载中/已暂停/校验中)。
    /// 终态(已完成/失败/取消)不落盘:失败任务的残片已清,没有可续的断点,
    /// 也不该在下次启动时被悄悄重下。队列空则删除落盘文件。</summary>
    public void SaveQueue()
    {
        try
        {
            var entries = CurrentBatchSnapshot()
                .Where(t => t.Status is DownloadStatus.Pending or DownloadStatus.Downloading
                            or DownloadStatus.Paused or DownloadStatus.Verifying)
                .Where(t => !string.IsNullOrEmpty(t.Url) && !string.IsNullOrEmpty(t.LocalPath))
                .Select(t => new QueueEntry
                {
                    Url = t.Url,
                    LocalPath = t.LocalPath,
                    Sha1 = t.Sha1,
                    Size = t.Size,
                    Category = t.Category,
                    IsSharded = t.IsSharded,
                    ShardTotal = t.ShardTotal,
                    DoneShards = new List<int>(t.DoneShards)
                })
                .ToList();

            if (entries.Count == 0) { TryDeleteFile(AppPaths.DownloadQueueFile); return; }

            var model = new QueueFileModel { Paused = IsPaused, Tasks = entries };
            string? dir = Path.GetDirectoryName(AppPaths.DownloadQueueFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(AppPaths.DownloadQueueFile, JsonSerializer.Serialize(model, QueueJson), Encoding.UTF8);
        }
        catch (Exception ex) { App.WriteAppLog($"[下载] 队列落盘失败:{ex.Message}"); }
    }

    /// <summary>启动时恢复上次未完成的下载队列(后台续传),返回恢复的任务数。
    /// 上一次其实已经下完的(文件在且 SHA1 通过)直接跳过;上次处于全局暂停则保持暂停,
    /// 其余按等待态重新入队并立即继续下载,断点原样复用。</summary>
    public int RestoreQueue()
    {
        try
        {
            if (!File.Exists(AppPaths.DownloadQueueFile)) return 0;
            var model = JsonSerializer.Deserialize<QueueFileModel>(
                File.ReadAllText(AppPaths.DownloadQueueFile), QueueJson);
            TryDeleteFile(AppPaths.DownloadQueueFile);   // 一次性消费:恢复后即清,避免下次重复恢复
            if (model?.Tasks == null || model.Tasks.Count == 0) return 0;

            var tasks = new List<DownloadTaskItem>();
            int skipped = 0;
            foreach (var e in model.Tasks)
            {
                if (string.IsNullOrEmpty(e.Url) || string.IsNullOrEmpty(e.LocalPath)) continue;
                if (File.Exists(e.LocalPath) && VerifySha1(e.LocalPath, e.Sha1)) { skipped++; continue; }
                var t = new DownloadTaskItem
                {
                    Url = e.Url,
                    LocalPath = e.LocalPath,
                    Sha1 = e.Sha1,
                    Size = e.Size,
                    Category = e.Category,
                    IsSharded = e.IsSharded,
                    ShardTotal = e.ShardTotal,
                    DoneShards = e.DoneShards ?? new List<int>()
                };
                // 断点字节:单连接按 .partial 实际长度;分片的段位表在 DownloadShardedAsync
                // 里按段位重算(那里才知道分片方案),此处不必预估。
                string part = e.LocalPath + ".partial";
                if (!e.IsSharded && File.Exists(part))
                {
                    try { t.Downloaded = new FileInfo(part).Length; } catch { t.Downloaded = 0; }
                }
                tasks.Add(t);
            }

            if (tasks.Count == 0)
            {
                if (skipped > 0) App.WriteAppLog($"[下载] 上次未完成任务均已下完 {skipped} 个,无需恢复");
                return 0;
            }

            if (model.Paused)
            {
                IsPaused = true;
                foreach (var t in tasks) t.Status = DownloadStatus.Paused;
                lock (_batchLock) CurrentBatch.AddRange(tasks);
                App.WriteAppLog($"[下载] 恢复上次未完成任务 {tasks.Count} 个(上次为暂停态,保持暂停,点「继续」即续传)");
                return tasks.Count;
            }

            App.WriteAppLog($"[下载] 恢复上次未完成任务 {tasks.Count} 个(跳过已下完 {skipped} 个),后台继续下载(断点已复用)");
            _ = DownloadAllAsync(tasks);   // 后台续传,不阻塞启动
            return tasks.Count;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[下载] 队列恢复失败(忽略,不影响启动):{ex.Message}");
            return 0;
        }
    }

    // ==================== 队列精细化管控(BlockHelm-1) ====================

    /// <summary>队列里还没跑完的任务数(待下载 / 下载中 / 已暂停 / 校验中)</summary>
    public int PendingCount => CurrentBatchSnapshot().Count(t =>
        t.Status == DownloadStatus.Pending || t.Status == DownloadStatus.Downloading ||
        t.Status == DownloadStatus.Paused || t.Status == DownloadStatus.Verifying);

    /// <summary>
    /// 删除单条任务:下载中的先撤销单任务令牌,再清残片,最后从队列与字典里摘除。
    /// 只动这一条,其余任务继续跑。
    /// </summary>
    public bool RemoveTask(DownloadTaskItem? task)
    {
        if (task == null) return false;
        _removedUrls[task.Url] = 0;
        if (_taskCts.TryGetValue(task.Url, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        // 2026-09-26 修复:移除任务扣回已下字节——总进度残留,重新下同文件会重复累加超量;
        // 已完成任务不扣(它已贡献过字节,移除只是清列表,进度不能倒退)
        if (task.Status != DownloadStatus.Completed)
        {
            lock (_overallLock)
            {
                long old = Math.Max(0, task.Downloaded);
                if (old > 0) _overallDownloadedBytes = Math.Max(0, _overallDownloadedBytes - old);
            }
        }
        Transition(task, DownloadStatus.Cancelled);
        task.Error = "已从下载队列删除";
        CleanupPartial(task);
        _tasks.TryRemove(task.Url, out _);
        lock (_batchLock) CurrentBatch.Remove(task);
        App.WriteAppLog($"[下载] 队列删除单条任务:{Path.GetFileName(task.LocalPath)} | URL={task.Url}");
        return true;
    }

    /// <summary>批量删除选中的任务</summary>
    public int RemoveTasks(IEnumerable<DownloadTaskItem> tasks)
    {
        int n = 0;
        foreach (var t in tasks ?? Enumerable.Empty<DownloadTaskItem>())
            if (RemoveTask(t)) n++;
        if (n > 0) SaveQueue();   // 移除后同步落盘,被移除的任务不再参与重启续传
        return n;
    }

    /// <summary>
    /// 清空下载队列。includeFinished=false 时只清未完成的,已下好的记录留在面板上。
    /// </summary>
    public int ClearQueue(bool includeFinished = true)
    {
        var snapshot = CurrentBatchSnapshot();
        int n = 0;
        foreach (var t in snapshot)
        {
            bool finished = t.Status == DownloadStatus.Completed
                         || t.Status == DownloadStatus.Failed
                         || t.Status == DownloadStatus.Cancelled;
            if (finished && !includeFinished) continue;
            if (!finished)
            {
                // 2026-09-26 修复:清队列同样扣回已下字节
                lock (_overallLock)
                {
                    long old = Math.Max(0, t.Downloaded);
                    if (old > 0) _overallDownloadedBytes = Math.Max(0, _overallDownloadedBytes - old);
                }
                _removedUrls[t.Url] = 0;
                if (_taskCts.TryGetValue(t.Url, out var cts))
                {
                    try { cts.Cancel(); } catch { }
                }
                Transition(t, DownloadStatus.Cancelled);
                t.Error = "队列已清空";
                CleanupPartial(t);
            }
            _tasks.TryRemove(t.Url, out _);
            n++;
        }
        lock (_batchLock)
        {
            if (includeFinished) CurrentBatch.Clear();
            else CurrentBatch.RemoveAll(t =>
                t.Status != DownloadStatus.Completed &&
                t.Status != DownloadStatus.Failed &&
                t.Status != DownloadStatus.Cancelled);
        }
        App.WriteAppLog($"[下载] 队列已清空:{n} 条任务(包含已完成={includeFinished})");
        // 2026-09-26 补全:清空后复位总进度——避免旧批次进度残留在 UI(无活跃任务时才清零)
        ResetOverallProgress();
        SaveQueue();   // 清空的队列同步落盘(无未完成项时会删除断点文件)
        return n;
    }

    /// <summary>
    /// 拖拽调整优先级:把任务移到队列指定位置。排在前面的任务优先抢到并发槽位。
    /// </summary>
    public bool MoveTask(DownloadTaskItem? task, int newIndex)
    {
        if (task == null) return false;
        lock (_batchLock)
        {
            int old = CurrentBatch.IndexOf(task);
            if (old < 0) return false;
            newIndex = Math.Clamp(newIndex, 0, CurrentBatch.Count - 1);
            if (old == newIndex) return true;
            CurrentBatch.RemoveAt(old);
            CurrentBatch.Insert(newIndex, task);
        }
        App.WriteAppLog($"[下载] 队列顺序调整:{Path.GetFileName(task.LocalPath)} → 第 {newIndex + 1} 位");
        return true;
    }

    /// <summary>整体重排(拖拽结束后按新顺序一次性写回)。不在新顺序里的任务原样挂到末尾,绝不丢任务。</summary>
    public void ApplyOrder(IReadOnlyList<DownloadTaskItem> ordered)
    {
        lock (_batchLock)
        {
            var keep = CurrentBatch.ToList();
            CurrentBatch.Clear();
            foreach (var t in ordered)
                if (keep.Remove(t)) CurrentBatch.Add(t);
            CurrentBatch.AddRange(keep);
        }
    }

    /// <summary>
    /// 让重排真正生效:把还在排队等并发槽位的任务撤销后按新顺序重新入队。
    /// 已经开始下载的不碰(断点还在,不浪费已下流量)。
    /// </summary>
    public async Task ReprioritizeAsync()
    {
        var waiting = CurrentBatchSnapshot().Where(t => t.Status == DownloadStatus.Pending).ToList();
        if (waiting.Count == 0) return;
        foreach (var t in waiting)
        {
            _removedUrls.TryRemove(t.Url, out _);   // 不是用户删除,只是重新排队
            if (_taskCts.TryGetValue(t.Url, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }
        // 给它们一点时间退出等待(没抢到槽位的会在 sem.WaitAsync 抛取消,瞬时返回)
        await Task.Delay(150).ConfigureAwait(false);
        foreach (var t in waiting)
        {
            CleanupPartial(t);
            t.Downloaded = 0;
            t.RetryCount = 0;
            t.Error = "";
            Transition(t, DownloadStatus.Pending);
        }
        // waiting 的顺序就是 CurrentBatch 里的新顺序,直接按它入队
        await RequeueAsync(waiting).ConfigureAwait(false);
        App.WriteAppLog($"[下载] 已按新优先级重新排队 {waiting.Count} 个等待中的任务");
    }

    // ==================== 失败重试(不清空面板批次,支持整批/单任务) ====================

    /// <summary>重新入队执行指定任务(不清空当前批次面板,用于失败补下)。
    /// 复用仍有效的批次令牌;令牌已撤销(用户取消过)时才重建,绝不误杀在跑任务</summary>
    public async Task<bool> RequeueAsync(List<DownloadTaskItem> tasks)
    {
        if (tasks.Count == 0) return true;
        if (IsPaused)
        {
            App.WriteAppLog("[下载] 当前处于暂停状态,拒绝重试");
            return false;
        }
        if (_cts == null || _cts.IsCancellationRequested)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
        }
        var cts = _cts;

        foreach (var t in tasks) RollbackTask(t);   // 清残片/计数清零/状态回未开始
        var allTasks = new List<Task<bool>>(tasks.Count);
        foreach (var task in tasks)
        {
            _tasks[task.Url] = task;
            allTasks.Add(DownloadOneAsync(task, cts.Token));
        }
        var results = await Task.WhenAll(allTasks);
        foreach (var t in tasks)
        {
            if (t.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
                _tasks.TryRemove(t.Url, out _);
        }
        bool allOk = Array.TrueForAll(results, r => r);
        App.WriteAppLog($"[下载] 失败补下完成:{tasks.Count} 个任务,{(allOk ? "全部成功" : "仍有失败")}");
        return allOk;
    }

    /// <summary>重试当前批次中全部失败/已取消的任务(下载页「重试失败」入口)</summary>
    public Task<bool> RetryFailedAsync()
        => RequeueAsync(CurrentBatchSnapshot()
            .Where(t => t.Status is DownloadStatus.Failed or DownloadStatus.Cancelled).ToList());

    /// <summary>重试单个失败任务(任务行「重试」入口)</summary>
    public Task<bool> RetryTaskAsync(DownloadTaskItem task)
        => RequeueAsync(new List<DownloadTaskItem> { task });

    /// <summary>取消全部下载,清理半成品残损文件</summary>
    public void Cancel()
    {
        IsPaused = false;
        _cts?.Cancel();
        foreach (var t in _tasks.Values)
        {
            if (t.Status == DownloadStatus.Completed) continue;   // 已完成任务不受取消影响
            // 2026-09-26 修复:取消全部同样扣回已下字节
            lock (_overallLock)
            {
                long old = Math.Max(0, t.Downloaded);
                if (old > 0) _overallDownloadedBytes = Math.Max(0, _overallDownloadedBytes - old);
            }
            Transition(t, DownloadStatus.Cancelled);
            CleanupPartial(t);
        }
        App.WriteAppLog("[下载] 用户取消全部下载任务,已清理残留临时文件");
        SaveQueue();   // 取消即清断点:残留段位索引会让下次下载跳过未完成段位
    }

    /// <summary>失败任务回滚:清理残片与最终文件,计数清零,状态回到未开始</summary>
    public void RollbackTask(DownloadTaskItem task)
    {
        // 2026-09-25 修复:重试前把已计入总进度的字节扣回,否则失败-重试会重复累加,
        // 总进度出现「已下载 > 总计」超额(曾实测 953.0MB / 876.2MB 显示 100% 而实际未完成)。
        lock (_overallLock)
        {
            long old = Math.Max(0, task.Downloaded);
            if (old > 0) _overallDownloadedBytes = Math.Max(0, _overallDownloadedBytes - old);
        }
        CleanupPartial(task);
        TryDeleteFile(task.LocalPath);
        task.Downloaded = 0;
        task.RetryCount = 0;
        task.Error = "";
        Transition(task, DownloadStatus.Pending);
        App.WriteAppLog($"[下载] 任务回滚 {Path.GetFileName(task.LocalPath)}:残片已清理,可重新入队 | URL={task.Url}");
    }

    // ==================== 源测速 / 错误分类 / 校验 ====================

    /// <summary>下载源连通性测速:探测 Mojang 官方源与 BMCLAPI 镜像,返回延迟(ms;-1=不可达)</summary>
    public async Task<List<(string Name, long LatencyMs, bool Reachable)>> TestSourceSpeedAsync()
    {
        // 2026-09-25 扩充:覆盖游戏本体 + Forge/NeoForge/Fabric/OptiFine/Quilt 全部下载链路,
        // 「下载源测速」不再只测版本清单 2 源,让用户看清每个加速源的真实状态
        var targets = new List<(string Name, string Url)>
        {
            ("Mojang 官方源", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"),
            ("BMCLAPI 国内镜像", "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json"),
            ("Forge 官方源", "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"),
            ("Forge BMCLAPI", "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/maven-metadata.xml"),
            ("NeoForge 官方源", "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"),
            ("NeoForge BMCLAPI", "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/maven-metadata.xml"),
            ("Fabric 官方源", "https://meta.fabricmc.net/v2/versions/loader/1.20.1"),
            ("Fabric BMCLAPI", "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/1.20.1"),
            ("OptiFine BMCLAPI", "https://bmclapi2.bangbang93.com/optifine/1.20.1"),
            ("Quilt 官方源", "https://meta.quiltmc.org/v3/versions/loader/1.20.1")
        };
        // 2026-09-30:填了自有镜像就把它的清单/库/资源三个代表路径一起测,
        // 自建源是否同步到位、延迟多少,用户在设置页直接看得见
        string? custom = MirrorUrlMap.NormalizeBase(_configService.Config.CustomDownloadBaseUrl);
        if (custom != null)
        {
            targets.Add(("自有镜像 · 版本清单", custom + "/mc/game/version_manifest_v2.json"));
            targets.Add(("自有镜像 · 库文件", custom + "/maven/net/minecraftforge/forge/maven-metadata.xml"));
            targets.Add(("自有镜像 · Fabric", custom + "/fabric-meta/v2/versions/loader/1.20.1"));
        }
        // 2026-09-26 修复:并发测速——原来串行 10 源×8s 超时(最坏等 80 秒),并发 ~8s 内全部出结果
        var results = await Task.WhenAll(targets.Select(async (pair) =>
        {
            var (name, url) = pair;
            var sw = Stopwatch.StartNew();
            bool ok = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var resp = await ClientFor(DownloadCategory.Game).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                sw.Stop();
                ok = resp.IsSuccessStatusCode;
            }
            catch { sw.Stop(); }
            long lat = ok ? sw.ElapsedMilliseconds : -1;
            App.WriteAppLog($"[下载] 源测速 {name}:{(ok ? $"{lat}ms" : "不可达")}");
            return (name, lat, ok);
        }));
        return results.ToList();
    }

    /// <summary>下载错误分类,返回中文友好提示(不暴露堆栈细节)</summary>
    public static string ClassifyDownloadError(Exception ex, string url)
    {
        if (ex is TaskCanceledException or TimeoutException)
            return "网络连接超时,请检查网络或切换下载源。";
        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode == HttpStatusCode.NotFound)
                return "文件不存在(404),服务器上找不到该文件。";
            if (httpEx.StatusCode == HttpStatusCode.Forbidden)
                return "服务器拒绝访问(403),可能被限流,请稍后重试。";
            if (httpEx.StatusCode == HttpStatusCode.TooManyRequests)
                return "请求过于频繁(429),请稍后重试。";
            if (httpEx.Message.Contains("Name") || httpEx.Message.Contains("resolve") ||
                httpEx.Message.Contains("DNS") || httpEx.Message.Contains("No such host"))
                return "域名解析失败,请检查网络或 DNS 设置。";
            if (httpEx.Message.Contains("refused") || httpEx.Message.Contains("reset"))
                return "连接被拒绝或重置,服务器可能暂时不可用。";
            return $"网络请求异常:{ex.Message}";
        }
        if (ex is IOException ioEx && ioEx.Message.Contains("空间"))
            return "磁盘空间不足,无法写入文件。";
        if (ex is UnauthorizedAccessException)
            return "文件权限不足,无法写入目标位置。";
        if (ex is IOException ioExLock && (ioExLock.Message.Contains("占用") || ioExLock.Message.Contains("being used")))
            return "文件被其他程序占用,无法写入。";
        if (ex is InvalidDataException)
            return "文件校验失败,下载的文件可能损坏。";
        string msg = ex.Message;
        if (msg.Length > 200) msg = msg[..200] + "...";
        return $"下载失败:{msg}";
    }

    /// <summary>下载完成后校验文件 SHA1(配置关闭校验时恒通过)</summary>
    public bool VerifySha1(string filePath, string? expectedSha1)
    {
        if (string.IsNullOrEmpty(expectedSha1)) return true;
        if (!_configService.Config.VerifyAfterDownload) return true;
        if (!File.Exists(filePath)) return false;
        try
        {
            string actual = _nativeInterop.ComputeFileSHA1(filePath);
            return string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[下载] SHA1 计算异常({Path.GetFileName(filePath)}): {ex.Message}");
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { App.WriteAppLog($"[下载] 删除文件失败 {path}:{ex.Message}"); }
    }

    /// <summary>清理任务的半成品 .partial 临时文件,并重置分片段位断点
    /// (2026-09-27:取消/移除/最终失败时必须一并清段位表,否则残留索引会让
    /// 下次下载误判「这些段已完成」而跳过,产出不完整文件)</summary>
    private static void CleanupPartial(DownloadTaskItem task)
    {
        TryDeleteFile(task.LocalPath + ".partial");
        task.DoneShards.Clear();
        task.ShardTotal = 0;
    }
}
