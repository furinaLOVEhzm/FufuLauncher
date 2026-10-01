// Copyright © FufuLauncher
//
// 磁盘占用统计服务(Celestial-5):
// 逐实例统计占用大小 + 全局总占用,占用特别大的实例做高亮提醒。
//
// 实现要点:
// 1. 实例目录里的 saves / mods 是指向规范目录的联接(Junction),递归统计时必须跳过,
//    否则同一份文件会被算两遍 —— 物理目录单独统计后再相加;
// 2. 全程后台线程 + IProgress,单个目录 IO 异常只记日志不中断整体;
// 3. 统计很慢(大实例几万个文件),结果缓存进 InstanceExtrasService,默认 6 小时内直接复用;
// 4. "占用过大"阈值:单实例 ≥ 2GB 或 ≥ 全局平均值的 2.5 倍(取较宽松的那个),避免小盘机器全员高亮。

using System.IO;

namespace FufuLauncher.Services;

/// <summary>单个实例的磁盘占用明细</summary>
public sealed class InstanceDiskUsage
{
    public string InstanceId { get; set; } = "";
    public string InstanceName { get; set; } = "";
    /// <summary>实例目录本体(配置、options.txt、resourcepacks、shaderpacks 等,不含联接指向的物理目录)</summary>
    public long InstanceDirBytes { get; set; }
    public long ModsBytes { get; set; }
    public long SavesBytes { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes => InstanceDirBytes + ModsBytes + SavesBytes;
    public string TotalDisplay => StorageGuardService.FmtSize(TotalBytes);
    /// <summary>是否被判定为占用过大(UI 高亮)</summary>
    public bool Oversize { get; set; }
    /// <summary>统计时间(缓存复用判断)</summary>
    public DateTime MeasuredAt { get; set; } = DateTime.Now;
    public string Detail =>
        $"实例本体 {StorageGuardService.FmtSize(InstanceDirBytes)} · " +
        $"模组 {StorageGuardService.FmtSize(ModsBytes)} · 存档 {StorageGuardService.FmtSize(SavesBytes)}" +
        $" · 共 {FileCount} 个文件";
}

/// <summary>全局磁盘统计结果</summary>
public sealed class DiskUsageReport
{
    public List<InstanceDiskUsage> Instances { get; set; } = new();
    /// <summary>全部实例合计</summary>
    public long InstancesTotalBytes => Instances.Sum(i => i.TotalBytes);
    /// <summary>共享资源(versions / libraries / assets / runtimes),被所有实例共用</summary>
    public long VersionsBytes { get; set; }
    public long LibrariesBytes { get; set; }
    public long AssetsBytes { get; set; }
    public long RuntimesBytes { get; set; }
    public long SharedBytes => VersionsBytes + LibrariesBytes + AssetsBytes + RuntimesBytes;
    /// <summary>启动器全部游戏相关占用 = 实例合计 + 共享资源</summary>
    public long GrandTotalBytes => InstancesTotalBytes + SharedBytes;
    public int TotalFileCount => Instances.Sum(i => i.FileCount);
    public int OversizeCount => Instances.Count(i => i.Oversize);
    public DateTime MeasuredAt { get; set; } = DateTime.Now;

    public string Summary =>
        $"全部游戏版本合计 {StorageGuardService.FmtSize(InstancesTotalBytes)}" +
        $"(共享的版本本体 / 库 / 资源另有 {StorageGuardService.FmtSize(SharedBytes)})" +
        $" · 总占用 {StorageGuardService.FmtSize(GrandTotalBytes)}" +
        (OversizeCount > 0 ? $" · {OversizeCount} 个版本占用偏大" : "");
}

public sealed class DiskUsageService
{
    /// <summary>单实例"占用过大"的绝对阈值(2GB)</summary>
    public const long OversizeAbsoluteBytes = 2L * 1024 * 1024 * 1024;
    /// <summary>缓存有效期(小时):期内直接复用上次结果,不重复扫盘</summary>
    public const int CacheHours = 6;

    private readonly InstanceService _instances;
    private readonly InstanceExtrasService _extras;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DiskUsageService(InstanceService instances, InstanceExtrasService extras)
    {
        _instances = instances;
        _extras = extras;
    }

    /// <summary>是否有正在进行的统计</summary>
    public bool IsMeasuring { get; private set; }

    /// <summary>统计完成(UI 刷新列表)</summary>
    public event Action<DiskUsageReport>? Measured;

    // ==================== 单实例 ====================

    /// <summary>统计单个实例(force = false 时优先返回缓存)</summary>
    public async Task<InstanceDiskUsage> MeasureInstanceAsync(string instanceId, bool force = false,
                                                             CancellationToken ct = default)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        var usage = new InstanceDiskUsage
        {
            InstanceId = instanceId,
            InstanceName = inst?.Name ?? instanceId
        };

        if (!force)
        {
            var (bytes, at, oversize) = _extras.DiskUsageOf(instanceId);
            if (bytes > 0 && at != default && (DateTime.Now - at).TotalHours < CacheHours)
            {
                // 缓存里只有一个总数,拆分项留 0(明细需重新扫盘)
                usage.InstanceDirBytes = bytes;
                usage.Oversize = oversize;
                usage.MeasuredAt = at;
                return usage;
            }
        }

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string dir = _instances.GetInstanceDir(instanceId);
            var (dirBytes, dirFiles) = MeasureDir(dir, skipJunctions: true);
            usage.InstanceDirBytes = dirBytes;
            usage.FileCount = dirFiles;

            var (modsBytes, modsFiles) = MeasureDir(_instances.GetModsDir(instanceId), skipJunctions: false);
            usage.ModsBytes = modsBytes;
            usage.FileCount += modsFiles;

            var (savesBytes, savesFiles) = MeasureDir(_instances.GetSavesDir(instanceId), skipJunctions: false);
            usage.SavesBytes = savesBytes;
            usage.FileCount += savesFiles;
            usage.MeasuredAt = DateTime.Now;

            _extras.SetDiskUsage(instanceId, usage.TotalBytes, usage.Oversize);
            return usage;
        }, ct).ConfigureAwait(false);
    }

    // ==================== 全局 ====================

    /// <summary>
    /// 统计全部实例 + 共享资源目录。useCache = true 时,6 小时内统计过的实例直接复用缓存。
    /// </summary>
    public async Task<DiskUsageReport> MeasureAllAsync(IProgress<string>? progress = null,
                                                      bool useCache = true,
                                                      CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IsMeasuring = true;
            var report = new DiskUsageReport();
            var list = _instances.Instances.ToList();
            int done = 0;

            foreach (var inst in list)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"正在统计「{inst.Name}」占用({++done}/{list.Count})");

                InstanceDiskUsage usage;
                var (cachedBytes, cachedAt, cachedOver) = _extras.DiskUsageOf(inst.Id);
                if (useCache && cachedBytes > 0 && cachedAt != default &&
                    (DateTime.Now - cachedAt).TotalHours < CacheHours)
                {
                    usage = new InstanceDiskUsage
                    {
                        InstanceId = inst.Id,
                        InstanceName = inst.Name,
                        InstanceDirBytes = cachedBytes,
                        Oversize = cachedOver,
                        MeasuredAt = cachedAt
                    };
                }
                else
                {
                    usage = await MeasureInstanceAsync(inst.Id, force: true, ct).ConfigureAwait(false);
                    usage.InstanceName = inst.Name;
                }
                report.Instances.Add(usage);
            }

            // 共享资源(全部实例共用,只算一次)
            progress?.Report("正在统计共享的版本本体 / 库 / 资源文件…");
            report.VersionsBytes = (await MeasureDirAsync(AppPaths.Versions, ct).ConfigureAwait(false)).Bytes;
            report.LibrariesBytes = (await MeasureDirAsync(AppPaths.Libraries, ct).ConfigureAwait(false)).Bytes;
            report.AssetsBytes = (await MeasureDirAsync(AppPaths.Assets, ct).ConfigureAwait(false)).Bytes;
            report.RuntimesBytes = (await MeasureDirAsync(AppPaths.Runtimes, ct).ConfigureAwait(false)).Bytes;

            ApplyOversizeFlag(report);
            report.MeasuredAt = DateTime.Now;
            App.WriteAppLog($"[磁盘统计] {report.Summary}");
            try { Measured?.Invoke(report); } catch { /* 订阅者异常不影响统计 */ }
            return report;
        }
        finally
        {
            IsMeasuring = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// 打"占用过大"标记:绝对阈值 2GB 或 超过平均值 2.5 倍(两者取较宽松,即先满足的那个就标记)。
    /// 实例数少于 2 个时只看绝对阈值(平均值没有参考意义)。
    /// </summary>
    private void ApplyOversizeFlag(DiskUsageReport report)
    {
        if (report.Instances.Count == 0) return;
        double avg = report.Instances.Average(i => (double)i.TotalBytes);
        double relative = report.Instances.Count >= 2 ? avg * 2.5 : double.MaxValue;
        foreach (var u in report.Instances)
        {
            u.Oversize = u.TotalBytes >= OversizeAbsoluteBytes || u.TotalBytes >= relative;
            _extras.SetDiskUsage(u.InstanceId, u.TotalBytes, u.Oversize);
        }
    }

    /// <summary>只读缓存,不扫盘(列表首屏用,秒回)</summary>
    public List<InstanceDiskUsage> ReadCache()
    {
        var list = new List<InstanceDiskUsage>();
        foreach (var inst in _instances.Instances)
        {
            var (bytes, at, oversize) = _extras.DiskUsageOf(inst.Id);
            list.Add(new InstanceDiskUsage
            {
                InstanceId = inst.Id,
                InstanceName = inst.Name,
                InstanceDirBytes = bytes,
                Oversize = oversize,
                MeasuredAt = at
            });
        }
        return list;
    }

    // ==================== 目录遍历 ====================

    /// <summary>后台线程包装(共享目录统计用)</summary>
    private static Task<(long Bytes, int Files)> MeasureDirAsync(string dir, CancellationToken ct)
        => Task.Run(() => MeasureDir(dir, skipJunctions: true), ct);

    /// <summary>
    /// 统计目录占用。skipJunctions = true 时跳过联接目录(实例目录的 saves / mods 是联接,
    /// 递归进去会把规范物理目录的文件重复算一遍)。手工递归而非 Directory.EnumerateFiles(recursive),
    /// 就是为了能在每层判断联接。
    /// </summary>
    private static (long Bytes, int Files) MeasureDir(string dir, bool skipJunctions)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return (0, 0);
        long total = 0;
        int count = 0;
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            string cur = stack.Pop();
            try
            {
                foreach (string f in Directory.EnumerateFiles(cur))
                {
                    try { total += new FileInfo(f).Length; count++; }
                    catch { /* 单个文件读不到大小就跳过 */ }
                }
            }
            catch (Exception ex) { App.WriteAppLog($"[磁盘统计] 枚举文件失败 {cur}:{ex.Message}"); }

            try
            {
                foreach (string sub in Directory.EnumerateDirectories(cur))
                {
                    if (skipJunctions && JunctionHelper.IsJunction(sub)) continue;
                    stack.Push(sub);
                }
            }
            catch (Exception ex) { App.WriteAppLog($"[磁盘统计] 枚举子目录失败 {cur}:{ex.Message}"); }
        }
        return (total, count);
    }

    /// <summary>所在磁盘剩余空间(供"占用过大"时提示清理)</summary>
    public static (long FreeBytes, long TotalBytes) GetDriveSpace(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "C:\\";
            var drive = new DriveInfo(root);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[磁盘统计] 读取磁盘空间失败:{ex.Message}");
            return (0, 0);
        }
    }
}
