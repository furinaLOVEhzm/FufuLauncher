// Copyright © FufuLauncher
//
// 实例快速克隆服务(Axolotl-5):
// 一键把现有游戏版本完整复制成一个新实例 —— 模组、各项设置、JVM 参数、窗口设置全部继承,
// 用户只需要改个名字就能得到一个全新的游戏版本。
//
// 实现要点:
// 1. 目录骨架复用 InstanceService.CreateInstance(建实例目录 + resourcepacks/shaderpacks +
//    saves/mods 联接),不自己重造一套目录规范,避免和底层约定跑偏;
// 2. 复制文件时严格跳过 saves / mods 两个联接点 —— 联接一旦递归进去就是复制物理目录本身,
//    轻则内容重复计算,重则死循环;物理存档/模组目录另外单独按需复制;
// 3. 存档默认不复制:体积大、且属于玩家隐私,克隆的目的是"复用一套配置和模组";
// 4. 实例级附加数据(配置快照 / 前后置脚本 / 磁盘缓存 / 分组归属)走 InstanceExtrasService.CopyTo
//    一起搬到新实例,启动历史记录默认不继承(那是源实例自己的历史);
// 5. 复制前先按源实例实测体积做磁盘预检,空间不足直接拒绝并说清楚差多少,绝不在复制中途炸掉;
// 6. 全程 async + 进度回调,大实例(几 GB 模组)复制时界面不卡死。

using System.IO;

namespace FufuLauncher.Services;

/// <summary>克隆选项</summary>
public sealed class CloneOptions
{
    /// <summary>复制存档(saves)。默认关:体积大且属于玩家隐私</summary>
    public bool CopySaves { get; set; }
    /// <summary>继承实例的配置快照(Axolotl-1)。默认开</summary>
    public bool CopySnapshots { get; set; } = true;
    /// <summary>继承前后置脚本设置(Celestial-7)。默认开</summary>
    public bool CopyScripts { get; set; } = true;
    /// <summary>继承源实例的启动历史记录(BlockHelm-6)。默认关</summary>
    public bool CopyLaunchHistory { get; set; }
    /// <summary>把新实例放进源实例所在的分组(LauncherX-4)。默认开</summary>
    public bool InheritGroup { get; set; } = true;
}

/// <summary>克隆结果</summary>
public sealed class CloneResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public GameInstance? Instance { get; set; }
    public int CopiedFiles { get; set; }
    public long CopiedBytes { get; set; }
    /// <summary>因文件占用/权限跳过而没复制成功的文件数</summary>
    public int SkippedFiles { get; set; }
    public TimeSpan Elapsed { get; set; }
    public string Detail =>
        $"复制 {CopiedFiles} 个文件({StorageGuardService.FmtSize(CopiedBytes)})" +
        (SkippedFiles > 0 ? $",跳过 {SkippedFiles} 个(被占用或无权限)" : "") +
        $",耗时 {Elapsed.TotalSeconds:F1} 秒。";
}

public sealed class InstanceCloneService
{
    private readonly InstanceService _instances;
    private readonly InstanceExtrasService _extras;
    private readonly StorageGuardService _storage;
    private readonly DiskUsageService _disk;

    public InstanceCloneService(InstanceService instances, InstanceExtrasService extras,
                                StorageGuardService storage, DiskUsageService disk)
    {
        _instances = instances;
        _extras = extras;
        _storage = storage;
        _disk = disk;
    }

    /// <summary>是否正在克隆(防止连点按钮起两个复制任务)</summary>
    public bool IsBusy { get; private set; }

    /// <summary>
    /// 克隆实例。newName 为空时自动取「源名 副本」。
    /// </summary>
    public async Task<CloneResult> CloneAsync(string sourceInstanceId, string newName, CloneOptions? options = null,
                                              IProgress<string>? progress = null, CancellationToken ct = default)
    {
        options ??= new CloneOptions();
        if (IsBusy)
            return Fail("上一次克隆还没结束,请等它跑完再试。");

        var src = _instances.Instances.FirstOrDefault(i => i.Id == sourceInstanceId);
        if (src == null)
            return Fail("找不到要克隆的游戏版本,请先刷新列表。");

        string name = (newName ?? "").Trim();
        if (name.Length == 0) name = SuggestName(src.Name, _instances.Instances);
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Fail("实例名称不能为空,也不能包含 \\ / : * ? \" < > | 这些字符。");

        IsBusy = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // ---------- 1. 体积实测 + 磁盘预检 ----------
            progress?.Report("正在清点源实例体积…");
            var usage = await _disk.MeasureInstanceAsync(sourceInstanceId, force: false, ct).ConfigureAwait(false);
            long need = usage.TotalBytes;
            if (!options.CopySaves) need = Math.Max(0, need - usage.SavesBytes);
            need = Math.Max(need, 64L * 1024 * 1024);   // 兜底 64MB,应对空实例统计为 0

            var (chk, chkMsg) = _storage.Precheck(_instances.GetInstanceDir(sourceInstanceId), need);
            if (chk != StorageCheckResult.Ok)
            {
                App.WriteAppLog($"[克隆] ✗ 预检未通过 {chk}:{chkMsg}");
                return Fail($"磁盘预检没通过:{chkMsg}");
            }

            // ---------- 2. 建目录骨架(含 saves/mods 联接) ----------
            ct.ThrowIfCancellationRequested();
            progress?.Report($"正在创建新实例「{name}」…");
            var copy = await Task.Run(() => _instances.CreateInstance(name, src.VersionId ?? "", src.JavaMajorVersion), ct)
                                 .ConfigureAwait(false);
            if (copy == null)
                return Fail("创建实例目录失败,请检查磁盘权限。");

            try
            {
                // ---------- 3. 复制实例目录(跳过 saves/mods 联接) ----------
                progress?.Report("正在复制实例配置与资源文件…");
                var stat = await CopyTreeAsync(_instances.GetInstanceDir(sourceInstanceId),
                                               _instances.GetInstanceDir(copy.Id),
                                               skipJunctions: true, progress, ct).ConfigureAwait(false);

                // ---------- 4. 复制模组物理目录 ----------
                progress?.Report("正在复制模组文件…");
                var modStat = await CopyTreeAsync(_instances.GetModsDir(sourceInstanceId),
                                                  _instances.GetModsDir(copy.Id),
                                                  skipJunctions: false, progress, ct).ConfigureAwait(false);

                // ---------- 5. 按需复制存档物理目录 ----------
                var saveStat = (Files: 0, Bytes: 0L, Skipped: 0);
                if (options.CopySaves)
                {
                    progress?.Report("正在复制存档(存档通常比较大,请耐心等待)…");
                    saveStat = await CopyTreeAsync(_instances.GetSavesDir(sourceInstanceId),
                                                   _instances.GetSavesDir(copy.Id),
                                                   skipJunctions: false, progress, ct).ConfigureAwait(false);
                }

                // ---------- 6. 回写实例级字段(克隆必须整套继承) ----------
                copy.LoaderVersionId = src.LoaderVersionId;
                copy.ModLoader = src.ModLoader;
                copy.ModLoaderVersion = src.ModLoaderVersion;
                copy.JavaPath = src.JavaPath;
                copy.Xms = src.Xms;
                copy.Xmx = src.Xmx;
                copy.UseCustomMemory = src.UseCustomMemory;
                copy.RecommendedMemoryMb = src.RecommendedMemoryMb;
                copy.ExtraJvmArgs = src.ExtraJvmArgs;
                copy.Width = src.Width;
                copy.Height = src.Height;
                copy.Fullscreen = src.Fullscreen;
                copy.CreatedAt = DateTime.Now;
                // 游玩时长与最后游玩时间属于源实例的历史,新实例从零开始
                copy.LastPlayedAt = default;
                copy.TotalPlayTimeSeconds = 0;
                _instances.SaveInstance(copy);

                // ---------- 7. 搬实例级附加数据(快照 / 脚本 / 分组 / 磁盘缓存) ----------
                CopyExtras(sourceInstanceId, copy.Id, options);

                sw.Stop();
                var res = new CloneResult
                {
                    Ok = true,
                    Instance = copy,
                    CopiedFiles = stat.Files + modStat.Files + saveStat.Files,
                    CopiedBytes = stat.Bytes + modStat.Bytes + saveStat.Bytes,
                    SkippedFiles = stat.Skipped + modStat.Skipped + saveStat.Skipped,
                    Elapsed = sw.Elapsed
                };
                res.Message = $"已克隆出新游戏版本「{copy.Name}」。{res.Detail}";
                if (res.SkippedFiles > 0)
                    res.Message += $"\n有 {res.SkippedFiles} 个文件正在被占用没能复制,建议关掉游戏后再手动补一次。";
                App.WriteAppLog($"[克隆] ✓ {src.Name}({sourceInstanceId}) → {copy.Name}({copy.Id}):{res.Detail}");
                return res;
            }
            catch
            {
                // 复制过程炸了就回滚,绝不留一个半成品实例挂在列表里误导用户
                Rollback(copy.Id);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            App.WriteAppLog($"[克隆] 已取消:{src.Name} → {name}");
            return Fail("克隆已被取消。");
        }
        catch (Exception ex)
        {
            sw.Stop();
            App.WriteAppLog($"[克隆] ✗ 失败:{ex}");
            return Fail("克隆失败:" + StorageGuardService.ClassifyIoError(ex, "复制实例文件"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把源实例的附加数据搬到新实例(逐项开关)</summary>
    private void CopyExtras(string srcId, string dstId, CloneOptions options)
    {
        try
        {
            // CopyTo 一次性搬快照 / 脚本 / 磁盘缓存 / 分组归属,再按选项回退不要的部分
            _extras.CopyTo(srcId, dstId, options.InheritGroup);

            var dst = _extras.Get(dstId);
            if (!options.CopySnapshots) dst.Snapshots.Clear();
            if (!options.CopyLaunchHistory) dst.LaunchHistory.Clear();
            if (!options.CopyScripts)
            {
                dst.ScriptsEnabled = false;
                dst.PreLaunchScript = "";
                dst.PostExitScript = "";
            }
            _extras.Save();
        }
        catch (Exception ex)
        {
            // 附加数据搬不动不影响实例本身能用,只记日志
            App.WriteAppLog($"[克隆] 附加数据迁移失败({srcId} → {dstId}):{ex.Message}");
        }
    }

    /// <summary>克隆失败回滚:删掉刚建的实例目录与物理模组目录(存档目录硬性保留,绝不碰)</summary>
    private void Rollback(string newId)
    {
        try
        {
            string dir = _instances.GetInstanceDir(newId);
            if (Directory.Exists(dir))
            {
                JunctionHelper.DeleteJunctionOnly(Path.Combine(dir, "saves"));
                JunctionHelper.DeleteJunctionOnly(Path.Combine(dir, "mods"));
                Directory.Delete(dir, recursive: true);
            }
            string mods = _instances.GetModsDir(newId);
            if (Directory.Exists(mods) && !JunctionHelper.IsJunction(mods))
                Directory.Delete(mods, recursive: true);
            _instances.Instances.RemoveAll(i => i.Id == newId);
            _extras.Remove(newId);
            // InstancesChanged 是 InstanceService 的事件,外部不能直接触发;
            // 目录已删干净,重扫磁盘即可把半成品从列表里摘掉
            _instances.RefreshInstances();
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[克隆] 回滚残留清理失败 {newId}:{ex.Message}");
        }
    }

    /// <summary>
    /// 递归复制目录树。skipJunctions=true 时遇到联接点(saves/mods)只建空目录不深入。
    /// 返回复制统计,单个文件失败只计数不中断整体。
    /// </summary>
    private static async Task<(int Files, long Bytes, int Skipped)> CopyTreeAsync(
        string srcDir, string dstDir, bool skipJunctions, IProgress<string>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(srcDir)) return (0, 0, 0);
        return await Task.Run(() =>
        {
            int files = 0, skipped = 0;
            long bytes = 0;
            Directory.CreateDirectory(dstDir);

            var stack = new Stack<(string Src, string Dst)>();
            stack.Push((srcDir, dstDir));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (s, d) = stack.Pop();
                Directory.CreateDirectory(d);

                foreach (string sub in SafeDirs(s))
                {
                    // 联接点绝不能递归进去:那等于复制物理目录本身,会造成内容重复甚至死循环
                    if (skipJunctions && JunctionHelper.IsJunction(sub)) continue;
                    stack.Push((sub, Path.Combine(d, Path.GetFileName(sub))));
                }

                foreach (string f in SafeFiles(s))
                {
                    ct.ThrowIfCancellationRequested();
                    string target = Path.Combine(d, Path.GetFileName(f));
                    try
                    {
                        File.Copy(f, target, overwrite: true);
                        files++;
                        bytes += SafeLen(target);
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        App.WriteAppLog($"[克隆] 跳过文件 {f}:{ex.Message}");
                    }
                    if (files % 120 == 0)
                        progress?.Report($"已复制 {files} 个文件({StorageGuardService.FmtSize(bytes)})…");
                }
            }
            return (files, bytes, skipped);
        }, ct).ConfigureAwait(false);
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch (Exception ex) { App.WriteAppLog($"[克隆] 目录读取失败 {dir}:{ex.Message}"); return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir); }
        catch (Exception ex) { App.WriteAppLog($"[克隆] 文件读取失败 {dir}:{ex.Message}"); return Array.Empty<string>(); }
    }

    private static long SafeLen(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>推荐克隆名:源名 副本 / 源名 副本 2 / 源名 副本 3…(不撞现有实例名)</summary>
    public static string SuggestName(string sourceName, IEnumerable<GameInstance> existing)
    {
        string baseName = string.IsNullOrWhiteSpace(sourceName) ? "游戏版本" : sourceName.Trim();
        var used = new HashSet<string>(existing.Select(i => i.Name ?? ""), StringComparer.OrdinalIgnoreCase);
        string first = $"{baseName} 副本";
        if (!used.Contains(first)) return first;
        for (int n = 2; n < 999; n++)
        {
            string cand = $"{baseName} 副本 {n}";
            if (!used.Contains(cand)) return cand;
        }
        return $"{first}_{DateTime.Now:HHmmss}";
    }

    private static CloneResult Fail(string message) => new() { Ok = false, Message = message };
}
