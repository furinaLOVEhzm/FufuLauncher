// DownloadService.cs — 多线程分片断点续传下载服务
// 可爱的芙芙 - 核心下载引擎
//
// 功能:
// 1. 多线程并发下载(可配置并发数,按任务类别独立队列互不阻塞)
// 2. 单大文件多 Range 分片下载(>=8MB 文件自动分片,提升大文件吞吐)
// 3. 断点续传(.partial 临时文件 + 已下载字节数记录,中断后继续下载)
// 4. 双进度:单文件字节级进度 + 全局总进度(修复进度不跑动 bug)
// 5. 暂停 / 继续 / 取消(单任务与全局)
// 6. 失败自动重试(指数退避,默认 2 次,总计 3 次尝试)
// 7. 下载完成 SHA1 校验,校验失败自动重下(最多 1 次)
// 8. 官方源 / BMCLAPI 镜像源切换 + DNS 容错 + 连接超时控制
// 9. 模组下载源独立配置(ModDownloadSource),与游戏源分离
// 10. 错误分类中文提示(网络超时/404/DNS/权限/磁盘空间/连接中断)
//
// 任务类别:Game(游戏本体)、Asset(资源)、Java(运行库)、Mod(模组)
// 各类别使用独立 SemaphoreSlim,互不阻塞。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

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
    /// <summary>任务类别,决定使用哪个独立队列</summary>
    public DownloadCategory Category { get; set; } = DownloadCategory.Game;
    /// <summary>是否为大文件分片下载(内部使用)</summary>
    public bool IsSharded { get; set; }
}

public enum DownloadStatus { Pending, Downloading, Paused, Verifying, Completed, Failed, Cancelled }
public enum DownloadCategory { Game, Asset, Java, Mod, Other }

public class DownloadProgressInfo
{
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public TimeSpan EstimatedRemaining { get; set; }
    public double Progress => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes : 0;
}

public class DownloadService
{
    private readonly ConfigService _configService;
    private readonly NativeInteropService _nativeInterop;

    // 每个类别独立 HttpClient,避免不同源互相影响;统一超时与连接限制
    private readonly HttpClient _httpClient;
    private readonly HttpClientHandler _handler;

    // 全局取消令牌(覆盖前先取消旧的,避免失控)
    private CancellationTokenSource? _cts;

    // 各类别独立并发信号量(互不阻塞)
    private readonly SemaphoreSlim _semGame = new(8);
    private readonly SemaphoreSlim _semAsset = new(16);
    private readonly SemaphoreSlim _semJava = new(4);
    private readonly SemaphoreSlim _semMod = new(4);
    private readonly SemaphoreSlim _semOther = new(4);

    // 运行中任务(供暂停/取消遍历)
    private readonly ConcurrentDictionary<string, DownloadTaskItem> _tasks = new();

    /// <summary>最大重试次数(即额外尝试次数,默认 2 次,总计 3 次尝试)</summary>
    public int MaxRetry { get; set; } = 2;
    /// <summary>当前是否处于暂停状态(暂停后新批次不会启动,继续时重新发起即可断点续传)</summary>
    public bool IsPaused { get; private set; }
    /// <summary>大文件分片阈值(>=8MB 自动分片)</summary>
    public long ShardThreshold { get; set; } = 8L * 1024 * 1024;
    /// <summary>大文件分片数</summary>
    public int ShardCount { get; set; } = 4;

    // 单文件进度(仅最后报告的任务,UI 子进度条)
    public event Action<DownloadProgressInfo>? ProgressChanged;
    public event Action<DownloadTaskItem>? TaskCompleted;
    public event Action<DownloadTaskItem>? TaskFailed;
    /// <summary>全局总进度(所有批次累计):修复旧版进度不跑动 bug</summary>
    public event Action<DownloadProgressInfo>? OverallProgressChanged;

    // 全局总进度统计(累计多批)
    private long _overallTotalBytes;
    private long _overallDownloadedBytes;
    private long _lastOverallReportBytes;
    private long _lastOverallReportMs;
    private readonly object _overallLock = new();
    private readonly System.Diagnostics.Stopwatch _overallSw = System.Diagnostics.Stopwatch.StartNew();

    public DownloadService(ConfigService configService, NativeInteropService nativeInterop)
    {
        _configService = configService;
        _nativeInterop = nativeInterop;
        _handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 64
        };
        _httpClient = new HttpClient(_handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        // BMCLAPI / Mojang 均建议携带 User-Agent,缺失可能被限流或拒绝
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/1.0.5.1 (Windows)");
    }

    /// <summary>重置全局总进度(开始新下载任务前调用)</summary>
    public void ResetOverallProgress()
    {
        lock (_overallLock)
        {
            _overallTotalBytes = 0;
            _overallDownloadedBytes = 0;
            _lastOverallReportBytes = 0;
            _lastOverallReportMs = _overallSw.ElapsedMilliseconds;
        }
        OverallProgressChanged?.Invoke(new DownloadProgressInfo
        {
            TotalBytes = 0,
            DownloadedBytes = 0,
            SpeedBytesPerSec = 0
        });
    }

    /// <summary>累加全局总进度字节数(外部模块调用)</summary>
    public void AddOverallTotalBytes(long bytes)
    {
        lock (_overallLock) { _overallTotalBytes += bytes; }
    }

    /// <summary>累加全局已下载字节数并触发事件(修复:节流后仍保证最终触发)</summary>
    public void AddOverallDownloadedBytes(long deltaBytes)
    {
        long total, downloaded;
        lock (_overallLock)
        {
            _overallDownloadedBytes += deltaBytes;
            long nowMs = _overallSw.ElapsedMilliseconds;
            long elapsed = nowMs - _lastOverallReportMs;
            // 节流 150ms(从 300ms 降低,让进度条视觉更平滑跟随),下载完成时强制触发
            bool forceFire = _overallTotalBytes > 0 && _overallDownloadedBytes >= _overallTotalBytes;
            if (elapsed < 150 && !forceFire) return;
            total = _overallTotalBytes;
            downloaded = _overallDownloadedBytes;
            long delta = downloaded - _lastOverallReportBytes;
            double speed = elapsed > 0 ? delta / (elapsed / 1000.0) : 0;
            _lastOverallReportBytes = downloaded;
            _lastOverallReportMs = nowMs;
            OverallProgressChanged?.Invoke(new DownloadProgressInfo
            {
                TotalBytes = total,
                DownloadedBytes = downloaded,
                SpeedBytesPerSec = speed,
                EstimatedRemaining = speed > 0 && total > downloaded
                    ? TimeSpan.FromSeconds((total - downloaded) / speed)
                    : TimeSpan.MaxValue
            });
            return;
        }
    }

    /// <summary>获取当前下载源对应的 URL(官方 / BMCLAPI)</summary>
    public string GetSourceUrl(string originalUrl)
    {
        if (_configService.Config.DownloadSource == "BMCLAPI")
        {
            return ForceBmclUrl(originalUrl);
        }
        return originalUrl;
    }

    /// <summary>获取下载 URL(带重试次数):双源互为兜底。
    /// attempt=0 用配置源;重试时切到另一源(Mojang→BMCLAPI,BMCLAPI→官方原始 URL),
    /// 避免镜像 404/抽风时一直卡在同一源反复失败。</summary>
    public string GetSourceUrl(string originalUrl, int attempt)
    {
        if (attempt <= 0) return GetSourceUrl(originalUrl);

        if (_configService.Config.DownloadSource == "BMCLAPI")
        {
            // 镜像源重试时:奇数次回退官方原始 URL,偶数次再回镜像(交替尝试两源)
            return attempt % 2 == 1 ? originalUrl : ForceBmclUrl(originalUrl);
        }
        // 官方源重试时强制切 BMCLAPI 国内镜像
        return ForceBmclUrl(originalUrl);
    }

    /// <summary>获取模组下载源对应的 URL(独立于游戏下载源,使用 ModDownloadSource 配置)。
    /// 当前仅支持 Modrinth,后续可扩展 CurseForge/MCMod。</summary>
    public string GetModSourceUrl(string originalUrl)
    {
        string source = _configService.Config.ModDownloadSource;
        // Modrinth 源:默认就是 Modrinth API,不需要额外替换
        if (source == "Modrinth")
        {
            return originalUrl;
        }
        // 其他源暂不支持,降级使用原始 URL
        return originalUrl;
    }

    /// <summary>强制替换为 BMCLAPI 镜像 URL(国内降级保护)。
    /// 参考 PCL2/HMCL:覆盖 Mojang 官方 + Maven 仓库 + Modrinth CDN 等常见源。</summary>
    private static string ForceBmclUrl(string originalUrl)
    {
        // Mojang 官方源 → BMCLAPI
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

        // Maven 仓库 → BMCLAPI maven 镜像(fabric/forge/quilt 安装器与依赖库国内加速)
        if (originalUrl.Contains("repo1.maven.org") ||
            originalUrl.Contains("repo.maven.apache.org") ||
            originalUrl.Contains("maven.fabricmc.net") ||
            originalUrl.Contains("maven.minecraftforge.net") ||
            originalUrl.Contains("maven.quiltmc.org") ||
            originalUrl.Contains("files.minecraftforge.net"))
        {
            return originalUrl
                .Replace("repo1.maven.org/maven2", "bmclapi2.bangbang93.com/maven-central")
                .Replace("repo.maven.apache.org/maven2", "bmclapi2.bangbang93.com/maven-central")
                .Replace("maven.fabricmc.net", "bmclapi2.bangbang93.com/maven")
                .Replace("maven.minecraftforge.net", "bmclapi2.bangbang93.com/maven")
                .Replace("maven.quiltmc.org/repository/release", "bmclapi2.bangbang93.com/maven")
                .Replace("files.minecraftforge.net/maven", "bmclapi2.bangbang93.com/maven");
        }

        // Modrinth CDN(cdn.modrinth.com) — 国内访问较慢,通过镜像加速
        // 注意:Modrinth 官方没有专门的国内镜像,但 BMCLAPI 可以代理部分
        // 这里不做替换,保持原 URL(Modrinth CDN 全球节点已足够快)

        return originalUrl;
    }

    private SemaphoreSlim GetSemaphore(DownloadCategory cat) => cat switch
    {
        DownloadCategory.Game => _semGame,
        DownloadCategory.Asset => _semAsset,
        DownloadCategory.Java => _semJava,
        DownloadCategory.Mod => _semMod,
        _ => _semOther
    };

    /// <summary>批量下载,返回全部是否成功</summary>
    public async Task<bool> DownloadAllAsync(List<DownloadTaskItem> tasks)
    {
        if (IsPaused)
        {
            App.WriteAppLog("[下载] 当前处于暂停状态,拒绝启动新批次");
            return false;
        }

        // 下载前磁盘空间校验
        long totalSize = 0;
        foreach (var t in tasks) totalSize += Math.Max(0, t.Size);
        if (!CheckDiskSpace(totalSize, out string diskMsg))
        {
            System.Windows.MessageBox.Show(
                $"磁盘剩余空间不足,无法开始下载。\n\n{diskMsg}\n\n" +
                $"本次下载需要约 {totalSize / 1024.0 / 1024.0:F1} MB 空间。",
                "可爱的芙芙 - 磁盘空间不足",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }

        AddOverallTotalBytes(totalSize);

        // 覆盖前先取消上一次未完成任务
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var cts = _cts;
        var allTasks = new List<Task<bool>>();

        foreach (var task in tasks)
        {
            _tasks[task.Url] = task;
            allTasks.Add(DownloadOneAsync(task, cts.Token));
        }

        var results = await Task.WhenAll(allTasks);
        foreach (var t in tasks)
        {
            if (t.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
            {
                _tasks.TryRemove(t.Url, out _);
            }
        }
        return Array.TrueForAll(results, r => r);
    }

    /// <summary>下载前校验目标磁盘剩余空间是否足够(预留 1GB 缓冲)</summary>
    public static bool CheckDiskSpace(long requiredBytes, out string message)
    {
        try
        {
            string checkDir = App.AppDataDir;
            string? root = Path.GetPathRoot(checkDir);
            // 兜底:启动器数据目录一定基于 AppContext.BaseDirectory(绝对路径),正常不会为空;
            // 极端情况下回退到系统盘根目录(由系统目录推导,杜绝写死盘符)
            if (string.IsNullOrEmpty(root))
            {
                root = Path.GetPathRoot(Environment.SystemDirectory) ?? throw new InvalidOperationException("无法确定磁盘根目录");
            }
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

    private async Task<bool> DownloadOneAsync(DownloadTaskItem task, CancellationToken ct)
    {
        var sem = GetSemaphore(task.Category);
        await sem.WaitAsync(ct);
        try
        {
            task.Status = DownloadStatus.Downloading;
            string? dir = Path.GetDirectoryName(task.LocalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            for (int attempt = 0; attempt <= MaxRetry; attempt++)
            {
                try
                {
                    // 大文件分片下载,小文件单连接
                    if (task.Size >= ShardThreshold || (task.Size == 0 && task.IsSharded))
                    {
                        await DownloadShardedAsync(task, attempt, ct);
                    }
                    else
                    {
                        await DownloadWithRangeAsync(task, attempt, ct);
                    }

                    // SHA1 校验,失败则删除重下(最多 1 次,沿用当前 attempt 的源降级策略)
                    task.Status = DownloadStatus.Verifying;
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
                        {
                            throw new InvalidDataException($"文件 SHA1 校验失败:{Path.GetFileName(task.LocalPath)}");
                        }
                    }

                    task.Status = DownloadStatus.Completed;
                    long finalSize = 0;
                    try { finalSize = new FileInfo(task.LocalPath).Length; } catch { }
                    App.WriteAppLog($"[下载] 成功 {Path.GetFileName(task.LocalPath)} | 大小={finalSize} 字节 | " +
                                    $"哈希={(string.IsNullOrEmpty(task.Sha1) ? "无" : task.Sha1)} | URL={task.Url}");
                    TaskCompleted?.Invoke(task);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    task.Status = DownloadStatus.Cancelled;
                    task.Error = "下载已取消";
                    return false;
                }
                catch (Exception ex)
                {
                    task.RetryCount = attempt + 1;
                    // 错误分类中文提示
                    task.Error = ClassifyDownloadError(ex, task.Url);
                    App.WriteAppLog($"[下载] 第 {attempt + 1} 次失败 {Path.GetFileName(task.LocalPath)}:{ex.GetType().Name} - {ex.Message}");
                    if (attempt >= MaxRetry)
                    {
                        task.Status = DownloadStatus.Failed;
                        App.WriteAppLog($"[下载] 最终失败(已重试 {MaxRetry} 次) {Path.GetFileName(task.LocalPath)} | " +
                                        $"原因={task.Error} | URL={task.Url}");
                        // 失败后清理半成品 .partial,不遗留损坏碎片
                        CleanupPartial(task);
                        TaskFailed?.Invoke(task);
                        return false;
                    }
                    // 指数退避:1s, 2s, 4s...
                    await Task.Delay(1000 * (1 << attempt), ct);
                }
            }
            return false;
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>单连接 Range 断点续传下载(小文件)</summary>
    private async Task DownloadWithRangeAsync(DownloadTaskItem task, int attempt, CancellationToken ct)
    {
        // 模组类别使用独立的模组下载源
        string url = task.Category == DownloadCategory.Mod
            ? GetModSourceUrl(GetSourceUrl(task.Url, attempt))
            : GetSourceUrl(task.Url, attempt);
        long startPosition = task.Downloaded;
        string partPath = task.LocalPath + ".partial";

        // 若最终文件已存在且校验通过,直接跳过(幂等)
        if (File.Exists(task.LocalPath) && VerifySha1(task.LocalPath, task.Sha1))
        {
            task.Downloaded = new FileInfo(task.LocalPath).Length;
            // 跳过下载的文件也要计入全局总进度,否则断点续传/重装时进度条卡住不前
            AddOverallDownloadedBytes(task.Downloaded);
            return;
        }

        // 断点续传:从 .partial 文件继续
        if (startPosition == 0 && File.Exists(partPath))
        {
            try { startPosition = new FileInfo(partPath).Length; task.Downloaded = startPosition; }
            catch { startPosition = 0; }
        }

        if (task.Size > 0 && startPosition >= task.Size)
        {
            // 已下完,直接重命名
            if (File.Exists(partPath)) File.Move(partPath, task.LocalPath, overwrite: true);
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (startPosition > 0)
        {
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startPosition, null);
        }

        // per-request 30 秒首字节超时(连接卡死保护,与 HttpClient 总超时独立)
        // 使用 try-finally 确保 perReqCts 在异常路径下也正确释放
        CancellationTokenSource? perReqCts = null;
        HttpResponseMessage? resp = null;
        try
        {
            perReqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perReqCts.CancelAfter(TimeSpan.FromSeconds(30));
            resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perReqCts.Token);
            resp.EnsureSuccessStatusCode();

            // 服务端返回 200 而非 206,说明不支持断点续传,从头开始
            if (startPosition > 0 && resp.StatusCode == HttpStatusCode.OK)
            {
                startPosition = 0;
                task.Downloaded = 0;
            }

            if (task.Size == 0 && resp.Content.Headers.ContentLength is long len)
            {
                task.Size = startPosition + len;
            }

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            // FileMode.Append 续写 .partial
            await using var dst = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None,
                                                  bufferSize: 64 * 1024, useAsync: true);
            var buffer = new byte[64 * 1024];
            int read;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastReportBytes = startPosition;

            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                task.Downloaded += read;
                AddOverallDownloadedBytes(read);

                if (sw.ElapsedMilliseconds > 200)
                {
                    long delta = task.Downloaded - lastReportBytes;
                    double speed = delta / sw.Elapsed.TotalSeconds;
                    ReportSingleProgress(task, speed);
                    lastReportBytes = task.Downloaded;
                    sw.Restart();
                }
            }
            await dst.FlushAsync(ct);
            // 下载完成,重命名 .partial -> 最终文件
            if (File.Exists(task.LocalPath)) TryDeleteFile(task.LocalPath);
            File.Move(partPath, task.LocalPath);
            ReportSingleProgress(task, 0);
        }
        finally
        {
            perReqCts?.Dispose();
            resp?.Dispose();
        }
    }

    /// <summary>大文件多 Range 分片并发下载(>=8MB)</summary>
    private async Task DownloadShardedAsync(DownloadTaskItem task, int attempt, CancellationToken ct)
    {
        // 模组类别使用独立的模组下载源
        string url = task.Category == DownloadCategory.Mod
            ? GetModSourceUrl(GetSourceUrl(task.Url, attempt))
            : GetSourceUrl(task.Url, attempt);
        string partPath = task.LocalPath + ".partial";
        task.IsSharded = true;

        // 若最终文件已存在且校验通过,直接跳过
        if (File.Exists(task.LocalPath) && VerifySha1(task.LocalPath, task.Sha1))
        {
            task.Downloaded = new FileInfo(task.LocalPath).Length;
            // 跳过下载的文件也要计入全局总进度,否则断点续传/重装时进度条卡住不前
            AddOverallDownloadedBytes(task.Downloaded);
            return;
        }

        // 获取文件总大小(若未知,HEAD 请求 30 秒超时)
        if (task.Size <= 0)
        {
            CancellationTokenSource? headCts = null;
            HttpResponseMessage? headResp = null;
            try
            {
                headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headCts.CancelAfter(TimeSpan.FromSeconds(30));
                headResp = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), HttpCompletionOption.ResponseHeadersRead, headCts.Token);
                if (headResp.Content.Headers.ContentLength is long cl) task.Size = cl;
                if (headResp.StatusCode == HttpStatusCode.OK && task.Size <= 0)
                    throw new Exception("无法获取文件大小,跳过分片下载");
            }
            finally
            {
                headCts?.Dispose();
                headResp?.Dispose();
            }
        }

        // 计算分片范围
        int shards = Math.Min(ShardCount, (int)Math.Max(1, task.Size / (4L * 1024 * 1024)));
        shards = Math.Max(1, shards);
        long shardSize = task.Size / shards;
        var ranges = new List<(long start, long end)>();
        for (int i = 0; i < shards; i++)
        {
            long s = i * shardSize;
            long e = (i == shards - 1) ? task.Size - 1 : (s + shardSize - 1);
            ranges.Add((s, e));
        }

        // 预分配 .partial 文件
        using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(task.Size);
        }

        // 各分片并发下载,写入 .partial 对应偏移
        var shardTasks = new List<Task>();
        var progressLock = new object();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = 0;

        foreach (var (start, end) in ranges)
        {
            shardTasks.Add(Task.Run(async () =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                // per-request 30 秒首字节超时(分片连接卡死保护)
                CancellationTokenSource? perReqCts = null;
                HttpResponseMessage? resp = null;
                try
                {
                    perReqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    perReqCts.CancelAfter(TimeSpan.FromSeconds(30));
                    resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perReqCts.Token);
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    // 以读写方式打开 .partial,按偏移写入(多线程共享读,单线程写各自区段不重叠)
                    using var dst = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.Write,
                                                      bufferSize: 64 * 1024, useAsync: true);
                    dst.Position = start;
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                        lock (progressLock)
                        {
                            task.Downloaded += read;
                            AddOverallDownloadedBytes(read);
                            if (sw.ElapsedMilliseconds > 200)
                            {
                                long delta = task.Downloaded - lastReport;
                                double speed = delta / sw.Elapsed.TotalSeconds;
                                ReportSingleProgress(task, speed);
                                lastReport = task.Downloaded;
                                sw.Restart();
                            }
                        }
                    }
                }
                finally
                {
                    perReqCts?.Dispose();
                    resp?.Dispose();
                }
            }, ct));
        }

        await Task.WhenAll(shardTasks);
        // 分片下载完成,重命名
        if (File.Exists(task.LocalPath)) TryDeleteFile(task.LocalPath);
        File.Move(partPath, task.LocalPath);
        ReportSingleProgress(task, 0);
    }

    private void ReportSingleProgress(DownloadTaskItem task, double speed)
    {
        ProgressChanged?.Invoke(new DownloadProgressInfo
        {
            TotalBytes = task.Size,
            DownloadedBytes = task.Downloaded,
            SpeedBytesPerSec = speed,
            EstimatedRemaining = speed > 0 && task.Size > task.Downloaded
                ? TimeSpan.FromSeconds((task.Size - task.Downloaded) / speed)
                : TimeSpan.MaxValue
        });
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { App.WriteAppLog($"[下载] 删除文件失败 {path}:{ex.Message}"); }
    }

    /// <summary>清理任务的半成品 .partial 临时文件(取消/失败时调用,不遗留损坏碎片)</summary>
    private static void CleanupPartial(DownloadTaskItem task)
    {
        TryDeleteFile(task.LocalPath + ".partial");
    }

    /// <summary>暂停全部下载(保留 .partial 断点,继续时自动续传)</summary>
    public void Pause()
    {
        IsPaused = true;
        _cts?.Cancel();
        foreach (var t in _tasks.Values)
        {
            if (t.Status == DownloadStatus.Downloading) t.Status = DownloadStatus.Paused;
        }
        App.WriteAppLog("[下载] 用户暂停全部下载任务");
    }

    /// <summary>继续下载:解除暂停标记。各任务由调用方重新发起,断点自动续传</summary>
    public void Resume()
    {
        IsPaused = false;
        App.WriteAppLog("[下载] 用户继续下载(断点续传)");
    }

    /// <summary>取消全部下载,并清理半成品残损文件</summary>
    public void Cancel()
    {
        IsPaused = false;
        _cts?.Cancel();
        foreach (var t in _tasks.Values)
        {
            t.Status = DownloadStatus.Cancelled;
            CleanupPartial(t); // 取消后清理半下载残损文件,不遗留损坏碎片
        }
        App.WriteAppLog("[下载] 用户取消全部下载任务,已清理残留临时文件");
    }

    /// <summary>下载源连通性测速:依次探测 Mojang 官方源与 BMCLAPI 镜像,返回延迟(ms;-1=不可达)</summary>
    public async Task<List<(string Name, long LatencyMs, bool Reachable)>> TestSourceSpeedAsync()
    {
        var targets = new List<(string Name, string Url)>
        {
            ("Mojang 官方源", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"),
            ("BMCLAPI 国内镜像", "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json")
        };
        var results = new List<(string, long, bool)>();
        foreach (var (name, url) in targets)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                sw.Stop();
                ok = resp.IsSuccessStatusCode;
            }
            catch
            {
                sw.Stop();
                ok = false;
            }
            results.Add((name, ok ? sw.ElapsedMilliseconds : -1, ok));
            App.WriteAppLog($"[下载] 源测速 {name}:{(ok ? $"{sw.ElapsedMilliseconds}ms" : "不可达")}");
        }
        return results;
    }

    /// <summary>下载错误分类,返回中文友好提示(不暴露堆栈细节)</summary>
    public static string ClassifyDownloadError(Exception ex, string url)
    {
        // 网络连接超时
        if (ex is TaskCanceledException || ex is TimeoutException)
            return $"网络连接超时,请检查网络或切换下载源。";

        // HTTP 404 文件不存在
        if (ex is HttpRequestException httpEx && httpEx.StatusCode == HttpStatusCode.NotFound)
            return $"文件不存在(404),服务器上找不到该文件。";

        // HTTP 403/429 拒绝访问/限流
        if (ex is HttpRequestException httpEx403 && httpEx403.StatusCode == HttpStatusCode.Forbidden)
            return $"服务器拒绝访问(403),可能被限流,请稍后重试。";
        if (ex is HttpRequestException httpEx429 && httpEx429.StatusCode == HttpStatusCode.TooManyRequests)
            return $"请求过于频繁(429),请稍后重试。";

        // DNS 解析失败
        if (ex is HttpRequestException httpExDns &&
            (httpExDns.Message.Contains("Name") || httpExDns.Message.Contains("resolve") ||
             httpExDns.Message.Contains("DNS") || httpExDns.Message.Contains("No such host")))
            return $"域名解析失败,请检查网络或 DNS 设置。";

        // 连接被拒绝/重置
        if (ex is HttpRequestException httpExConn &&
            (httpExConn.Message.Contains("refused") || httpExConn.Message.Contains("reset")))
            return $"连接被拒绝或重置,服务器可能暂时不可用。";

        // 磁盘空间不足
        if (ex is IOException ioEx && ioEx.Message.Contains("空间"))
            return $"磁盘空间不足,无法写入文件。";

        // 文件权限不足
        if (ex is UnauthorizedAccessException)
            return $"文件权限不足,无法写入目标位置。";

        // 文件被占用
        if (ex is IOException ioExLock && (ioExLock.Message.Contains("占用") || ioExLock.Message.Contains("being used")))
            return $"文件被其他程序占用,无法写入。";

        // 校验失败
        if (ex is InvalidDataException)
            return $"文件校验失败,下载的文件可能损坏。";

        // 其他网络异常
        if (ex is HttpRequestException)
            return $"网络请求异常:{ex.Message}";

        // 通用异常,截断过长的消息
        string msg = ex.Message;
        if (msg.Length > 200) msg = msg[..200] + "...";
        return $"下载失败:{msg}";
    }

    /// <summary>下载完成后校验文件 SHA1</summary>
    public bool VerifySha1(string filePath, string? expectedSha1)
    {
        if (string.IsNullOrEmpty(expectedSha1)) return true;
        if (!File.Exists(filePath)) return false;
        string actual = _nativeInterop.ComputeFileSHA1(filePath);
        return string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase);
    }
}
