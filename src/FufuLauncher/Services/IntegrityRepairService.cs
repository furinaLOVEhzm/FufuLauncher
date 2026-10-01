// Copyright © FufuLauncher
//
// 环境完整性校验与自动修复(BlockHelm-3):
// 游戏启动之前校验游戏核心 jar、库文件、资源文件是否完好;
// 检测到损坏或缺失就自动补下载,并把"哪些项目被修复了"整理成清单交给弹窗展示。
//
// 实现要点:
// 1. 两档校验:
//    - QuickCheck:只查文件存在性(毫秒级),给启动前的默认闸门用,不拖慢启动;
//    - DeepCheck:逐个算 SHA1(慢,几万个资产文件),给用户在界面上主动点"深度体检"用;
// 2. 修复走 DownloadService 既有链路(断点续传 / 双源交替 / 分片 / SHA1 复核),不另写下载器;
// 3. 只补缺失与损坏项,已完好的文件一个字节都不重下;
// 4. 版本 JSON 缺失(整个版本没装)时直接判定"需要完整安装",不做半吊子修复;
// 5. 修复清单按类别分组(核心 / 库 / 资源),每类给出条数与前若干条文件名,弹窗可读。

using System.IO;
using System.Text;
using System.Text.Json;

namespace FufuLauncher.Services;

/// <summary>校验模式</summary>
public enum IntegrityCheckMode
{
    /// <summary>只查存在性(快,启动前默认)</summary>
    Quick,
    /// <summary>逐个算 SHA1(慢,深度体检)</summary>
    Deep
}

/// <summary>待修复的一个文件</summary>
public sealed class RepairItem
{
    public string LocalPath { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public long Size { get; set; }
    public DownloadCategory Category { get; set; } = DownloadCategory.Game;
    /// <summary>缺失 / 损坏 / 大小不符</summary>
    public string Reason { get; set; } = "缺失";
    /// <summary>是否已修复成功</summary>
    public bool Fixed { get; set; }
    public string FileName => Path.GetFileName(LocalPath);
    /// <summary>类别中文名(弹窗分组标题)</summary>
    public string KindDisplay => LocalPath.Contains(Path.Combine("assets", "objects"), StringComparison.OrdinalIgnoreCase)
        ? "资源文件"
        : LocalPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
          LocalPath.Contains(AppPaths.Versions, StringComparison.OrdinalIgnoreCase)
        ? "游戏核心"
        : "库文件";
}

/// <summary>校验 + 修复结果</summary>
public sealed class IntegrityReport
{
    public string InstanceId { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public IntegrityCheckMode Mode { get; set; } = IntegrityCheckMode.Quick;
    /// <summary>总共检查了多少个文件</summary>
    public int CheckedCount { get; set; }
    /// <summary>需要修复的条目</summary>
    public List<RepairItem> Problems { get; set; } = new();
    /// <summary>修复成功的条目</summary>
    public List<RepairItem> Repaired { get; set; } = new();
    /// <summary>修复失败的条目</summary>
    public List<RepairItem> Failed { get; set; } = new();
    /// <summary>整个版本都没装(需要去下载页完整安装,不是修修补补能解决的)</summary>
    public bool NeedFullInstall { get; set; }
    public bool Ok => !NeedFullInstall && Problems.Count == 0 && Failed.Count == 0;
    public TimeSpan Elapsed { get; set; }

    /// <summary>一句话结论(弹窗标题)</summary>
    public string Headline
    {
        get
        {
            if (NeedFullInstall) return "游戏版本本体还没装好,需要先完整安装。";
            if (Problems.Count == 0 && Failed.Count == 0)
                return $"环境完整,已检查 {CheckedCount} 个文件,没有发现缺失或损坏。";
            if (Failed.Count == 0 && Repaired.Count > 0)
                return $"已自动修复 {Repaired.Count} 个文件,现在可以正常启动。";
            if (Failed.Count > 0)
                return $"有 {Failed.Count} 个文件修复失败,请检查网络后重试。";
            return $"发现 {Problems.Count} 个文件缺失或损坏。";
        }
    }

    /// <summary>弹窗正文:按类别分组列出被修复/失败的项目</summary>
    public string Detail
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine($"检查模式:{(Mode == IntegrityCheckMode.Quick ? "快速(只查文件在不在)" : "深度(逐个校验 SHA1)")}");
            sb.AppendLine($"共检查 {CheckedCount} 个文件,耗时 {Elapsed.TotalSeconds:0.0} 秒。");
            if (NeedFullInstall)
            {
                sb.AppendLine();
                sb.AppendLine("版本 JSON 都还没下载,说明这个版本压根没装完。");
                sb.AppendLine("请到「下载中心」重新安装这个游戏版本。");
                return sb.ToString();
            }
            AppendGroup(sb, "已修复", Repaired);
            AppendGroup(sb, "修复失败", Failed);
            if (Repaired.Count == 0 && Failed.Count == 0 && Problems.Count > 0)
                AppendGroup(sb, "待修复", Problems);
            return sb.ToString().TrimEnd();
        }
    }

    private static void AppendGroup(StringBuilder sb, string title, List<RepairItem> items)
    {
        if (items.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"【{title} {items.Count} 项】");
        foreach (var g in items.GroupBy(i => i.KindDisplay))
        {
            var list = g.ToList();
            sb.AppendLine($"  {g.Key}({list.Count} 个):");
            foreach (var it in list.Take(6))
                sb.AppendLine($"    · {it.FileName} —— {it.Reason}");
            if (list.Count > 6) sb.AppendLine($"    · …… 另有 {list.Count - 6} 个");
        }
    }
}

public sealed class IntegrityRepairService
{
    private readonly InstanceService _instances;
    private readonly GameInstallService _installer;
    private readonly DownloadService _download;
    private readonly HashVerifyService _hashVerify;

    public IntegrityRepairService(InstanceService instances, GameInstallService installer,
                                  DownloadService download, HashVerifyService hashVerify)
    {
        _instances = instances;
        _installer = installer;
        _download = download;
        _hashVerify = hashVerify;
    }

    /// <summary>是否有校验/修复正在进行</summary>
    public bool IsBusy { get; private set; }

    /// <summary>快速校验(不修复):启动前闸门用,毫秒级返回</summary>
    public Task<IntegrityCheckResult> QuickCheckOnlyAsync(string instanceId)
        => _installer.VerifyInstanceIntegrityAsync(instanceId);

    /// <summary>
    /// 校验并自动修复。
    /// quickFirst = true 时先用快速存在性检查筛出候选,只对这些候选算 SHA1,
    /// 兼顾"启动前不能卡太久"与"文件内容坏了也能发现"。
    /// </summary>
    public async Task<IntegrityReport> CheckAndRepairAsync(
        string instanceId, IntegrityCheckMode mode, bool autoRepair,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = new IntegrityReport { InstanceId = instanceId, Mode = mode };
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        report.InstanceName = inst?.Name ?? instanceId;

        if (inst == null)
        {
            report.NeedFullInstall = true;
            report.Elapsed = sw.Elapsed;
            return report;
        }

        IsBusy = true;
        try
        {
            progress?.Report("正在读取版本文件清单…");
            var tasks = await BuildChecklistAsync(inst, ct).ConfigureAwait(false);
            if (tasks == null)
            {
                report.NeedFullInstall = true;
                report.Elapsed = sw.Elapsed;
                return report;
            }
            report.CheckedCount = tasks.Count;

            // 逐项判定:先查存在性;深度模式再算 SHA1
            int idx = 0;
            foreach (var t in tasks)
            {
                ct.ThrowIfCancellationRequested();
                idx++;
                if (idx % 200 == 0)
                    progress?.Report($"正在检查文件完整性 {idx}/{tasks.Count}");

                if (!File.Exists(t.LocalPath))
                {
                    report.Problems.Add(ToRepairItem(t, "文件缺失"));
                    continue;
                }
                if (mode != IntegrityCheckMode.Deep) continue;
                if (string.IsNullOrEmpty(t.Sha1)) continue;
                var r = _hashVerify.Verify(t.LocalPath, t.Sha1, t.Size);
                if (!r.Exists) report.Problems.Add(ToRepairItem(t, "文件缺失"));
                else if (r.ExpectedSize > 0 && r.ActualSize != r.ExpectedSize)
                    report.Problems.Add(ToRepairItem(t, $"大小不符({StorageGuardService.FmtSize(r.ActualSize)} / 应为 {StorageGuardService.FmtSize(r.ExpectedSize)})"));
                else if (!r.Valid) report.Problems.Add(ToRepairItem(t, "内容损坏(SHA1 不一致)"));
            }

            if (report.Problems.Count == 0 || !autoRepair)
            {
                report.Elapsed = sw.Elapsed;
                App.WriteAppLog($"[完整性] {report.InstanceName} {(mode == IntegrityCheckMode.Quick ? "快速" : "深度")}校验:" +
                                $"检查 {report.CheckedCount} 个,问题 {report.Problems.Count} 个");
                return report;
            }

            // 自动修复:删掉损坏文件后重下(缺失的直接下)
            progress?.Report($"正在补下载 {report.Problems.Count} 个文件…");
            var repairTasks = new List<DownloadTaskItem>();
            foreach (var p in report.Problems)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(p.LocalPath))
                {
                    try { File.Delete(p.LocalPath); }
                    catch (Exception ex) { App.WriteAppLog($"[完整性] 删除损坏文件失败 {p.LocalPath}:{ex.Message}"); }
                }
                repairTasks.Add(new DownloadTaskItem
                {
                    Url = p.Url,
                    LocalPath = p.LocalPath,
                    Sha1 = p.Sha1,
                    Size = p.Size,
                    Category = p.Category
                });
            }

            bool allOk = await _download.DownloadAllAsync(repairTasks).ConfigureAwait(false);
            // 失败的再补一轮(双源交替)
            if (!allOk)
            {
                var failed = repairTasks.Where(t => t.Status is DownloadStatus.Failed or DownloadStatus.Cancelled).ToList();
                if (failed.Count > 0)
                {
                    progress?.Report($"正在重试 {failed.Count} 个失败文件…");
                    allOk = await _download.RequeueAsync(failed).ConfigureAwait(false);
                }
            }

            // 逐项复核,分出"真修好了"和"还是不行"
            foreach (var p in report.Problems)
            {
                bool good = File.Exists(p.LocalPath) &&
                            (string.IsNullOrEmpty(p.Sha1) || _hashVerify.Verify(p.LocalPath, p.Sha1, p.Size).Valid);
                p.Fixed = good;
                if (good) report.Repaired.Add(p); else report.Failed.Add(p);
            }

            report.Elapsed = sw.Elapsed;
            App.WriteAppLog($"[完整性] {report.InstanceName} 修复完成:发现 {report.Problems.Count} 个," +
                            $"修好 {report.Repaired.Count} 个,失败 {report.Failed.Count} 个,耗时 {report.Elapsed.TotalSeconds:0.0}s");
            return report;
        }
        catch (OperationCanceledException)
        {
            report.Elapsed = sw.Elapsed;
            return report;
        }
        catch (Exception ex)
        {
            report.Elapsed = sw.Elapsed;
            App.WriteAppLog($"[完整性] ✗ 校验/修复异常:{ex}");
            report.Failed.Add(new RepairItem { LocalPath = "", Reason = "校验过程出错:" + ex.Message });
            return report;
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// 构建"该实例需要哪些文件"的完整清单(client.jar + 库 + 原生库 + 资产索引 + 资产对象)。
    /// 版本 JSON 不存在返回 null(= 需要完整安装)。
    /// 预加载服务(Axolotl-6)也走这份清单,保证"装什么"与"查什么"完全一致。
    /// </summary>
    public Task<List<DownloadTaskItem>?> BuildChecklistAsync(string instanceId, CancellationToken ct = default)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        return inst == null ? Task.FromResult<List<DownloadTaskItem>?>(null) : BuildChecklistAsync(inst, ct);
    }

    private async Task<List<DownloadTaskItem>?> BuildChecklistAsync(GameInstance inst, CancellationToken ct)
    {
        string versionId = inst.VersionId ?? "";
        string versionDir = Path.Combine(AppPaths.Versions, versionId);
        string jsonPath = Path.Combine(versionDir, $"{versionId}.json");
        if (!File.Exists(jsonPath)) return null;

        MojangVersionJson? json;
        try
        {
            json = JsonSerializer.Deserialize<MojangVersionJson>(await File.ReadAllTextAsync(jsonPath, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[完整性] 版本 JSON 解析失败 {jsonPath}:{ex.Message}");
            return null;
        }
        if (json == null) return null;

        var tasks = new List<DownloadTaskItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(DownloadTaskItem t)
        {
            if (string.IsNullOrEmpty(t.LocalPath) || string.IsNullOrEmpty(t.Url)) return;
            if (seen.Add(t.LocalPath)) tasks.Add(t);
        }

        // 1) 游戏核心 jar(继承型版本 JSON 里可能没有 downloads.client,jar 名走 inheritsFrom)
        if (json.Downloads?.Client != null)
        {
            var c = json.Downloads.Client;
            Add(new DownloadTaskItem
            {
                Url = c.Url,
                LocalPath = Path.Combine(versionDir, $"{json.Id}.jar"),
                Sha1 = c.Sha1,
                Size = c.Size,
                Category = DownloadCategory.Game
            });
        }

        // 2) 库 + 原生库(只查当前系统适用的)
        foreach (var lib in json.Libraries)
        {
            // 2026-09-26 修复:与 GameInstallService 对齐,按 OS rules 过滤——
            // 之前把 linux/macos 原生库也当缺失补下,下载失败 → 启动前检查报「修复失败」→ 启动失败
            if (!IsLibraryAllowedForOs(lib)) continue;
            if (lib.Downloads?.Artifact != null)
            {
                var a = lib.Downloads.Artifact;
                if (!string.IsNullOrEmpty(a.Path))
                    Add(new DownloadTaskItem
                    {
                        Url = a.Url,
                        LocalPath = Path.Combine(AppPaths.Libraries, a.Path),
                        Sha1 = a.Sha1,
                        Size = a.Size,
                        Category = DownloadCategory.Game
                    });
            }
            if (lib.Natives != null && lib.Downloads?.Classifiers != null)
            {
                string key = (lib.Natives.GetValueOrDefault("windows") ?? "")
                    .Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                if (key.Length > 0 && lib.Downloads.Classifiers.TryGetValue(key, out var na) && !string.IsNullOrEmpty(na.Path))
                    Add(new DownloadTaskItem
                    {
                        Url = na.Url,
                        LocalPath = Path.Combine(AppPaths.Libraries, na.Path),
                        Sha1 = na.Sha1,
                        Size = na.Size,
                        Category = DownloadCategory.Game
                    });
            }
        }

        // 3) 资产索引 + 资产对象
        if (json.AssetIndex != null && !string.IsNullOrEmpty(json.AssetIndex.Url))
        {
            string indexPath = Path.Combine(AppPaths.Assets, "indexes", $"{json.AssetIndex.Id}.json");
            Add(new DownloadTaskItem
            {
                Url = json.AssetIndex.Url,
                LocalPath = indexPath,
                Sha1 = json.AssetIndex.Sha1,
                Size = json.AssetIndex.Size,
                Category = DownloadCategory.Asset
            });

            if (File.Exists(indexPath))
            {
                try
                {
                    var index = JsonSerializer.Deserialize<AssetIndexManifest>(
                        await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false));
                    if (index?.Objects != null)
                    {
                        foreach (var kv in index.Objects)
                        {
                            var o = kv.Value;
                            if (string.IsNullOrEmpty(o.Hash) || o.Hash.Length < 2) continue;
                            string sub = o.Hash[..2];
                            Add(new DownloadTaskItem
                            {
                                Url = $"https://resources.download.minecraft.net/{sub}/{o.Hash}",
                                LocalPath = Path.Combine(AppPaths.Assets, "objects", sub, o.Hash),
                                Sha1 = o.Hash,
                                Size = o.Size,
                                Category = DownloadCategory.Asset
                            });
                        }
                    }
                }
                catch (Exception ex) { App.WriteAppLog($"[完整性] 资产索引解析失败:{ex.Message}"); }
            }
        }

        return tasks;
    }

    private static RepairItem ToRepairItem(DownloadTaskItem t, string reason) => new()
    {
        LocalPath = t.LocalPath,
        Url = t.Url,
        Sha1 = t.Sha1 ?? "",
        Size = t.Size,
        Category = t.Category,
        Reason = reason
    };

    /// <summary>判断库是否适用于当前操作系统(与 GameInstallService 同规则)</summary>
    private static bool IsLibraryAllowedForOs(MojangLibrary lib)
    {
        if (lib.Rules == null || lib.Rules.Count == 0) return true;
        bool allow = false;
        foreach (var rule in lib.Rules)
        {
            bool osMatch = rule.Os == null || rule.Os.Name == "windows";
            if (rule.Action == "allow")
            {
                if (osMatch) allow = true;
            }
            else if (rule.Action == "disallow")
            {
                if (osMatch) allow = false;
            }
        }
        return allow;
    }

}
