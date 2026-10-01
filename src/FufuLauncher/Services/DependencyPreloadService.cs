// Copyright © FufuLauncher
//
// 后台依赖预加载服务(Axolotl-6):
// 新建游戏版本、或给版本装上 Fabric / Forge 加载器之后,把该版本需要的依赖
// (游戏核心 jar、库文件、原生库、资产索引与资产对象)放到后台静默下载,
// 界面完全不阻塞 —— 用户可以马上切去别的页面继续操作。
//
// 实现要点:
// 1. 单条后台工作线程串行消费队列,绝不并发抢带宽(与前台下载错峰),也不弹任何模态窗;
// 2. 下载前先自己算磁盘空间,不够就静默放弃并记日志 —— DownloadService 空间不足时会弹
//    DialogKit 模态窗,而模态窗只能在 UI 线程创建,后台线程弹会直接崩;
// 3. 队列按 key 去重(同一版本重复入队只跑一次),支持整体取消;
// 4. 进度只通过事件广播,UI 想显示就在角标上显示,不想显示就完全不显示;
// 5. 加载器安装走 ModLoaderInstallService 现成链路,预加载只负责"装完之后把依赖补齐"。

using System.Collections.Concurrent;
using System.IO;

namespace FufuLauncher.Services;

/// <summary>预加载任务类型</summary>
public enum PreloadKind
{
    /// <summary>补齐版本本体依赖(核心 jar / 库 / 资产)</summary>
    VersionDeps,
    /// <summary>安装模组加载器(Fabric / Forge / Quilt / NeoForge)</summary>
    Loader,
    /// <summary>加载器 + 版本依赖一条龙</summary>
    LoaderAndDeps
}

/// <summary>一条预加载任务</summary>
public sealed class PreloadJob
{
    public string Key { get; set; } = "";
    public PreloadKind Kind { get; set; } = PreloadKind.VersionDeps;
    public string InstanceId { get; set; } = "";
    public string InstanceName { get; set; } = "";
    /// <summary>加载器类型(fabric / forge / quilt / neoforge),VersionDeps 时为空</summary>
    public string LoaderKind { get; set; } = "";
    /// <summary>加载器版本(可空 = 装最新)</summary>
    public string? LoaderVersion { get; set; }
    public string GameVersion { get; set; } = "";
    /// <summary>入队原因(日志与 UI 提示用,如"新建版本"、"切换加载器")</summary>
    public string Reason { get; set; } = "";
    public DateTime QueuedAt { get; set; } = DateTime.Now;
}

/// <summary>预加载进度(UI 角标数据源)</summary>
public sealed class PreloadProgress
{
    public PreloadJob Job { get; set; } = null!;
    public string Stage { get; set; } = "";
    public int Done { get; set; }
    public int Total { get; set; }
    /// <summary>0~1;Total 未知时为 -1</summary>
    public double Percent => Total > 0 ? Math.Clamp(Done * 1.0 / Total, 0, 1) : -1;
    public bool Running { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Display => Running
        ? $"{Job.InstanceName}:{Stage}{(Total > 0 ? $" {Done}/{Total}" : "")}"
        : $"{Job.InstanceName}:{Message}";
}

public sealed class DependencyPreloadService : IDisposable
{
    private readonly InstanceService _instances;
    private readonly IntegrityRepairService _integrity;
    private readonly DownloadService _download;
    private readonly ModLoaderInstallService _loaderInstall;
    private readonly StorageGuardService _storage;

    private readonly ConcurrentQueue<PreloadJob> _queue = new();
    private readonly ConcurrentDictionary<string, PreloadJob> _enqueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _worker;

    /// <summary>预加载功能总开关(关掉后入队一律忽略)</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 已创建的单例(null = 本次运行根本没用到预加载)。
    /// 退出时用这个字段收尾后台线程,避开 GetRequiredService 为了 Dispose 反而新建一个线程。
    /// </summary>
    public static DependencyPreloadService? Created { get; private set; }

    /// <summary>正在执行的任务(null = 空闲)</summary>
    public PreloadJob? Current { get; private set; }

    /// <summary>排队中的任务数</summary>
    public int PendingCount => _queue.Count;

    /// <summary>是否空闲(没有正在跑的,也没有排队的)</summary>
    public bool IsIdle => Current == null && _queue.IsEmpty;

    /// <summary>进度广播(UI 自行决定要不要显示)。
    /// ⚠ 在后台工作线程上触发,订阅方自己 Dispatcher 切回 UI 线程再改控件</summary>
    public event Action<PreloadProgress>? Progress;

    /// <summary>队列长度变化(角标刷新)。同样在后台线程触发</summary>
    public event Action? QueueChanged;

    public DependencyPreloadService(InstanceService instances, IntegrityRepairService integrity,
                                    DownloadService download, ModLoaderInstallService loaderInstall,
                                    StorageGuardService storage)
    {
        _instances = instances;
        _integrity = integrity;
        _download = download;
        _loaderInstall = loaderInstall;
        _storage = storage;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "FufuPreload",
            Priority = ThreadPriority.BelowNormal   // 让路给前台交互与前台下载
        };
        _worker.Start();
        Created = this;
    }

    // ==================== 入队 ====================

    /// <summary>
    /// 入队"补齐版本依赖"。已排队或正在跑同一个版本时直接忽略(幂等)。
    /// 返回 false 表示被忽略(开关关闭 / 重复入队 / 实例不存在)。
    /// </summary>
    public bool EnqueueVersionDeps(string instanceId, string reason = "补齐依赖")
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null) return false;
        return Enqueue(new PreloadJob
        {
            Kind = PreloadKind.VersionDeps,
            InstanceId = inst.Id,
            InstanceName = inst.Name,
            GameVersion = inst.VersionId ?? "",
            Reason = reason
        });
    }

    /// <summary>
    /// 入队"安装加载器 + 补齐依赖"(用户改加载器时调用,全程后台静默)。
    /// </summary>
    public bool EnqueueLoader(string instanceId, string loaderKind, string? loaderVersion, string reason = "安装加载器")
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null || string.IsNullOrWhiteSpace(loaderKind)) return false;
        return Enqueue(new PreloadJob
        {
            Kind = PreloadKind.LoaderAndDeps,
            InstanceId = inst.Id,
            InstanceName = inst.Name,
            GameVersion = inst.VersionId ?? "",
            LoaderKind = loaderKind.Trim().ToLowerInvariant(),
            LoaderVersion = loaderVersion,
            Reason = reason
        });
    }

    private bool Enqueue(PreloadJob job)
    {
        if (!Enabled)
        {
            App.WriteAppLog($"[预加载] 已关闭,忽略入队:{job.InstanceName} / {job.Reason}");
            return false;
        }
        if (string.IsNullOrEmpty(job.GameVersion))
        {
            App.WriteAppLog($"[预加载] {job.InstanceName} 还没选游戏版本,跳过预加载");
            return false;
        }
        job.Key = $"{job.Kind}|{job.InstanceId}|{job.LoaderKind}|{job.LoaderVersion}";
        if (!_enqueued.TryAdd(job.Key, job)) return false;   // 同一件事已经在队列里了

        _queue.Enqueue(job);
        _signal.Release();
        App.WriteAppLog($"[预加载] ⇢ 入队 {job.InstanceName} / {job.Kind} / {job.Reason}(队列 {_queue.Count} 条)");
        SafeQueueChanged();
        return true;
    }

    /// <summary>清空尚未开始的排队任务(正在跑的那条会跑完)</summary>
    public int ClearQueue()
    {
        int removed = 0;
        while (_queue.TryDequeue(out var job))
        {
            _enqueued.TryRemove(job.Key, out _);
            removed++;
        }
        if (removed > 0)
        {
            App.WriteAppLog($"[预加载] 已清空排队任务 {removed} 条");
            SafeQueueChanged();
        }
        return removed;
    }

    /// <summary>取消指定实例的排队任务</summary>
    public int CancelFor(string instanceId)
    {
        var keep = new List<PreloadJob>();
        int removed = 0;
        while (_queue.TryDequeue(out var job))
        {
            if (job.InstanceId == instanceId) { _enqueued.TryRemove(job.Key, out _); removed++; }
            else keep.Add(job);
        }
        foreach (var k in keep) { _queue.Enqueue(k); _signal.Release(); }
        if (removed > 0) SafeQueueChanged();
        return removed;
    }

    // ==================== 后台工作线程 ====================

    private void WorkerLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(_shutdown.Token);
            }
            catch (OperationCanceledException) { break; }

            if (!_queue.TryDequeue(out var job)) continue;
            try
            {
                Current = job;
                SafeQueueChanged();
                RunJob(job).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[预加载] ✗ 任务异常 {job.InstanceName}:{ex}");
                Report(job, "出错", 0, 0, false, "预加载出错:" + ex.Message);
            }
            finally
            {
                Current = null;
                _enqueued.TryRemove(job.Key, out _);
                SafeQueueChanged();
            }
        }
    }

    private async Task RunJob(PreloadJob job)
    {
        Report(job, "排队中", 0, 0, true, "");

        // 1) 加载器安装(需要时才做)
        if (job.Kind is PreloadKind.Loader or PreloadKind.LoaderAndDeps)
        {
            Report(job, $"正在安装 {job.LoaderKind} 加载器", 0, 0, true, "");
            try
            {
                var r = await _loaderInstall.InstallLoaderAsync(job.InstanceId, job.GameVersion,
                                                                job.LoaderKind, job.LoaderVersion)
                                            .ConfigureAwait(false);
                if (!r.Success)
                {
                    Report(job, "加载器安装失败", 0, 0, false,
                           $"{job.LoaderKind} 加载器没能装上:{r.ErrorMessage ?? "未知原因"}。可以到「版本设置 → 修改加载器」手动再试一次。");
                    return;
                }
                App.WriteAppLog($"[预加载] ✓ {job.InstanceName} 加载器 {r.LoaderVersionId ?? r.InstalledVersion} 已装好");
                _instances.RefreshInstances();
            }
            catch (Exception ex)
            {
                Report(job, "加载器安装异常", 0, 0, false, "加载器安装出错:" + ex.Message);
                App.WriteAppLog($"[预加载] ✗ 加载器安装异常 {job.InstanceName}:{ex}");
                return;
            }
        }

        if (job.Kind == PreloadKind.Loader)
        {
            Report(job, "完成", 0, 0, false, "加载器已在后台装好。");
            return;
        }

        // 2) 版本依赖补齐
        await PreloadVersionDepsAsync(job).ConfigureAwait(false);
    }

    private async Task PreloadVersionDepsAsync(PreloadJob job)
    {
        List<DownloadTaskItem>? checklist;
        try
        {
            Report(job, "正在清点缺失的依赖文件", 0, 0, true, "");
            checklist = await _integrity.BuildChecklistAsync(job.InstanceId, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(job, "清点依赖失败", 0, 0, false, "清点依赖文件出错:" + ex.Message);
            return;
        }

        if (checklist == null)
        {
            Report(job, "版本未安装", 0, 0, false,
                   "这个游戏版本的本体文件还没装,请到「下载中心」完整安装一次,之后依赖会自动补齐。");
            return;
        }

        // 只补缺失的(已存在的一律不动,避免无意义重下几个 GB)
        var missing = checklist.Where(t => !File.Exists(t.LocalPath)).ToList();
        if (missing.Count == 0)
        {
            Report(job, "依赖已齐全", checklist.Count, checklist.Count, false,
                   $"检查了 {checklist.Count} 个文件,依赖已经全部就位,不用下载。");
            return;
        }

        long needBytes = missing.Sum(t => Math.Max(0, t.Size));
        // 后台线程绝不能弹模态窗:自己先把磁盘空间查清楚,不够就静默放弃
        var pre = _storage.Precheck(AppPaths.Versions, needBytes);
        if (pre.Result != StorageCheckResult.Ok)
        {
            Report(job, "磁盘空间不足", 0, missing.Count, false,
                   $"还差 {missing.Count} 个依赖文件没下,但磁盘空间不够:{pre.Message}");
            App.WriteAppLog($"[预加载] ✗ {job.InstanceName} 磁盘预检未通过:{pre.Result} - {pre.Message}");
            return;
        }

        Report(job, $"正在后台下载 {missing.Count} 个依赖文件", 0, missing.Count, true, "");
        int done = 0;
        // 分批下:每批 400 个,批间上报一次进度,UI 角标能动起来,也不至于一次占满并发槽
        foreach (var chunk in Chunk(missing, 400))
        {
            if (_shutdown.IsCancellationRequested) return;
            try
            {
                await _download.DownloadAllAsync(chunk).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[预加载] 分批下载异常(继续下一批):{ex.Message}");
            }
            done += chunk.Count;
            Report(job, $"正在后台下载依赖文件", done, missing.Count, true, "");
        }

        int okCount = missing.Count(t => File.Exists(t.LocalPath));
        int failCount = missing.Count - okCount;
        bool success = failCount == 0;
        Report(job, success ? "依赖补齐完成" : "部分依赖没下下来", okCount, missing.Count, !success,
            success
                ? $"已在后台补齐 {okCount} 个依赖文件({StorageGuardService.FmtSize(needBytes)}),现在可以直接启动了。"
                : $"补齐了 {okCount} 个,还有 {failCount} 个失败。可以稍后再点一次预加载,或检查网络/切换下载源。");
        App.WriteAppLog($"[预加载] {(success ? "✓" : "△")} {job.InstanceName} 依赖补齐:成功 {okCount} / 失败 {failCount}");
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> src, int size)
    {
        for (int i = 0; i < src.Count; i += size)
            yield return src.GetRange(i, Math.Min(size, src.Count - i));
    }

    private void Report(PreloadJob job, string stage, int done, int total, bool running, string message)
    {
        try
        {
            Progress?.Invoke(new PreloadProgress
            {
                Job = job,
                Stage = stage,
                Done = done,
                Total = total,
                Running = running,
                Success = !running && message.Length > 0 && !message.Contains("失败") && !message.Contains("不足") && !message.Contains("没能"),
                Message = message
            });
        }
        catch { /* UI 订阅者异常绝不影响后台预加载 */ }
    }

    private void SafeQueueChanged()
    {
        try { QueueChanged?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        if (ReferenceEquals(Created, this)) Created = null;
        Enabled = false;   // 关掉入队,正在收尾的队列不再接受新任务
        try
        {
            _shutdown.Cancel();
            try { _signal.Release(); } catch { }
            if (_worker.IsAlive) _worker.Join(1500);
            _signal.Dispose();
            _shutdown.Dispose();
        }
        catch (Exception ex) { App.WriteAppLog($"[预加载] 停止后台线程异常:{ex.Message}"); }
    }
}
