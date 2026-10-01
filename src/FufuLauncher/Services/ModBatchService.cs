// Copyright © FufuLauncher
//
// 本地模组批量管理服务(BlockHelm-2):
// 多选模组后一键批量启用 / 批量禁用 / 批量导出成模组包 / 批量迁移到别的游戏版本。
//
// 实现要点:
// 1. 启用禁用一律走 ModManagerService.ToggleMod(底层 .jar ↔ .disabled 重命名,含 5 次退避重试),
//    不自己搬文件,避免和禁用状态持久标记(Celestial-8)对不上;
// 2. 批量操作是"逐个尽力而为":某个模组被游戏占用切不动,只记进失败清单,其余照常处理,
//    最后把成功/失败明细一并回报给用户,绝不中途抛异常;
// 3. 导出走 ZipArchive 流式写入,不建中间暂存目录;
// 4. 迁移默认是"移动"(源实例删掉),同时提供"复制"模式;目标实例不存在或路径越界一律拒绝。

using System.IO;
using System.IO.Compression;

namespace FufuLauncher.Services;

/// <summary>批量操作结果</summary>
public sealed class ModBatchResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<string> Succeeded { get; set; } = new();
    public List<string> Failed { get; set; } = new();
    public long WrittenBytes { get; set; }
    public string Detail =>
        $"成功 {Succeeded.Count} 个" + (Failed.Count > 0 ? $",失败 {Failed.Count} 个" : "") +
        (WrittenBytes > 0 ? $",共 {StorageGuardService.FmtSize(WrittenBytes)}" : "") + "。";
}

public sealed class ModBatchService
{
    private readonly InstanceService _instances;
    private readonly ModManagerService _mods;
    private readonly StorageGuardService _storage;

    public ModBatchService(InstanceService instances, ModManagerService mods, StorageGuardService storage)
    {
        _instances = instances;
        _mods = mods;
        _storage = storage;
    }

    /// <summary>是否正在跑批量任务(防连点)</summary>
    public bool IsBusy { get; private set; }

    // ==================== 批量启用 / 禁用 ====================

    /// <summary>
    /// 批量启用或禁用。enable=true → 批量启用,false → 批量禁用。
    /// 被禁用的模组依旧留在列表里(灰色标记),文件不会被删除。
    /// </summary>
    public ModBatchResult SetEnabled(IEnumerable<ModInfo>? selected, bool enable)
    {
        var res = new ModBatchResult();
        var list = (selected ?? Enumerable.Empty<ModInfo>()).ToList();
        if (list.Count == 0)
        {
            res.Message = "请先勾选要处理的模组。";
            return res;
        }

        string action = enable ? "启用" : "禁用";
        foreach (var m in list)
        {
            if (string.IsNullOrEmpty(m.FilePath) || !File.Exists(m.FilePath))
            {
                res.Failed.Add($"{m.DisplayName}:文件不存在(可能已被移动或删除)");
                continue;
            }
            if (m.Enabled == enable)
            {
                // 已经是目标状态,算成功但不重复动文件
                res.Succeeded.Add(m.DisplayName);
                continue;
            }
            try
            {
                if (_mods.ToggleMod(m.FilePath, enable)) res.Succeeded.Add(m.DisplayName);
                else res.Failed.Add($"{m.DisplayName}:文件重命名失败(可能正被游戏占用)");
            }
            catch (Exception ex)
            {
                res.Failed.Add($"{m.DisplayName}:{ex.Message}");
            }
        }

        res.Ok = res.Failed.Count == 0;
        res.Message = $"批量{action}完成:{res.Detail}" + FailedTail(res);
        App.WriteAppLog($"[模组批量] {action} → 成功 {res.Succeeded.Count},失败 {res.Failed.Count}");
        return res;
    }

    // ==================== 批量导出 ====================

    /// <summary>把选中的模组打包成一个 zip(默认文件名 mods-export_yyyyMMdd_HHmmss.zip)</summary>
    public async Task<ModBatchResult> ExportAsync(IEnumerable<ModInfo>? selected, string? outputZipPath,
                                                  IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var res = new ModBatchResult();
        var list = (selected ?? Enumerable.Empty<ModInfo>()).Where(m => File.Exists(m.FilePath)).ToList();
        if (list.Count == 0)
        {
            res.Message = "请先勾选要导出的模组(勾选的模组文件必须真实存在)。";
            return res;
        }

        string zipPath = string.IsNullOrWhiteSpace(outputZipPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                           $"mods-export_{DateTime.Now:yyyyMMdd_HHmmss}.zip")
            : outputZipPath.Trim();

        long need = list.Sum(m => m.Size > 0 ? m.Size : SafeLen(m.FilePath));
        var (chk, chkMsg) = _storage.Precheck(zipPath, need);
        if (chk != StorageCheckResult.Ok)
        {
            res.Message = $"导出前预检没通过:{chkMsg}";
            return res;
        }

        if (IsBusy) { res.Message = "上一个批量任务还没结束,请稍等。"; return res; }
        IsBusy = true;
        try
        {
            string? dir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await Task.Run(() =>
            {
                string tmp = zipPath + ".partial";
                if (File.Exists(tmp)) TryDelete(tmp);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
                {
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int i = 0;
                    foreach (var m in list)
                    {
                        ct.ThrowIfCancellationRequested();
                        // 同名文件去重(不同实例的模组可能撞名),撞了就加序号
                        string entryName = UniqueName(Path.GetFileName(m.FilePath), used);
                        try
                        {
                            zip.CreateEntryFromFile(m.FilePath, entryName, CompressionLevel.Optimal);
                            res.Succeeded.Add(m.DisplayName);
                            res.WrittenBytes += SafeLen(m.FilePath);
                        }
                        catch (Exception ex)
                        {
                            res.Failed.Add($"{m.DisplayName}:{ex.Message}");
                        }
                        if (++i % 10 == 0) progress?.Report($"已打包 {i}/{list.Count} 个模组…");
                    }
                }
                if (File.Exists(zipPath)) TryDelete(zipPath);
                File.Move(tmp, zipPath);
            }, ct).ConfigureAwait(false);

            res.Ok = res.Failed.Count == 0 && res.Succeeded.Count > 0;
            res.Message = $"模组包已导出:{res.Detail}\n保存位置:{zipPath}" + FailedTail(res);
            App.WriteAppLog($"[模组批量] 导出 {res.Succeeded.Count} 个模组 → {zipPath}");
            return res;
        }
        catch (OperationCanceledException)
        {
            res.Message = "导出已取消。";
            return res;
        }
        catch (Exception ex)
        {
            res.Message = "导出失败:" + StorageGuardService.ClassifyIoError(ex, "打包模组");
            App.WriteAppLog($"[模组批量] ✗ 导出失败:{ex}");
            return res;
        }
        finally { IsBusy = false; }
    }

    // ==================== 批量迁移到别的实例 ====================

    /// <summary>
    /// 把选中模组迁移到目标实例。move=true 是移动(源实例不再保留),false 是复制一份过去。
    /// 禁用状态跟着文件名一起走(.disabled 后缀原样保留),迁移过去依旧是禁用的。
    /// </summary>
    public async Task<ModBatchResult> MoveToAsync(IEnumerable<ModInfo>? selected, string targetInstanceId,
                                                  bool move = true, IProgress<string>? progress = null,
                                                  CancellationToken ct = default)
    {
        var res = new ModBatchResult();
        var list = (selected ?? Enumerable.Empty<ModInfo>()).ToList();
        if (list.Count == 0) { res.Message = "请先勾选要迁移的模组。"; return res; }

        var target = _instances.Instances.FirstOrDefault(i => i.Id == targetInstanceId);
        if (target == null)
        {
            res.Message = "找不到目标游戏版本,请刷新列表后重试。";
            return res;
        }

        string srcInstanceId = _mods.CurrentInstanceId ?? "";
        if (srcInstanceId == targetInstanceId)
        {
            res.Message = "目标游戏版本就是当前这个,不用迁移。";
            return res;
        }

        string dstDir = _instances.GetModsDir(targetInstanceId);
        if (!IsUnderModsRoot(dstDir))
        {
            res.Message = "目标模组目录路径异常,已拒绝迁移(防止文件被写到意料之外的位置)。";
            return res;
        }

        long need = list.Sum(m => m.Size > 0 ? m.Size : SafeLen(m.FilePath));
        var (chk, chkMsg) = _storage.Precheck(dstDir, need);
        if (chk != StorageCheckResult.Ok) { res.Message = $"迁移前预检没通过:{chkMsg}"; return res; }

        if (IsBusy) { res.Message = "上一个批量任务还没结束,请稍等。"; return res; }
        IsBusy = true;
        try
        {
            Directory.CreateDirectory(dstDir);
            string action = move ? "移动" : "复制";
            await Task.Run(() =>
            {
                int i = 0;
                foreach (var m in list)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(m.FilePath))
                    {
                        res.Failed.Add($"{m.DisplayName}:源文件不存在");
                        continue;
                    }
                    string dst = Path.Combine(dstDir, UniqueNameOnDisk(Path.GetFileName(m.FilePath), dstDir));
                    try
                    {
                        string tmp = dst + ".tmp";
                        File.Copy(m.FilePath, tmp, overwrite: true);
                        File.Move(tmp, dst, overwrite: true);
                        res.WrittenBytes += SafeLen(dst);
                        if (move)
                        {
                            // 移动模式:确认目标落地后再删源文件,中途出错也不会两头空
                            TryDelete(m.FilePath);
                        }
                        res.Succeeded.Add($"{m.DisplayName} → {target.Name}");
                    }
                    catch (Exception ex)
                    {
                        res.Failed.Add($"{m.DisplayName}:{StorageGuardService.ClassifyIoError(ex, action + "模组")}");
                    }
                    if (++i % 10 == 0) progress?.Report($"已{action} {i}/{list.Count} 个模组…");
                }
            }, ct).ConfigureAwait(false);

            res.Ok = res.Failed.Count == 0 && res.Succeeded.Count > 0;
            res.Message = $"模组{(move ? "迁移" : "复制")}完成:{res.Detail}" + FailedTail(res);
            App.WriteAppLog($"[模组批量] {(move ? "移动" : "复制")} {res.Succeeded.Count} 个模组 {srcInstanceId} → {targetInstanceId}");
            return res;
        }
        catch (OperationCanceledException)
        {
            res.Message = "迁移已取消。";
            return res;
        }
        catch (Exception ex)
        {
            res.Message = "迁移失败:" + StorageGuardService.ClassifyIoError(ex, "迁移模组");
            App.WriteAppLog($"[模组批量] ✗ 迁移失败:{ex}");
            return res;
        }
        finally { IsBusy = false; }
    }

    /// <summary>批量删除(附带能力:勾错模组后清理)</summary>
    public ModBatchResult Delete(IEnumerable<ModInfo>? selected)
    {
        var res = new ModBatchResult();
        foreach (var m in selected ?? Enumerable.Empty<ModInfo>())
        {
            try
            {
                if (_mods.DeleteMod(m.FilePath)) res.Succeeded.Add(m.DisplayName);
                else res.Failed.Add($"{m.DisplayName}:删除失败(可能正被游戏占用)");
            }
            catch (Exception ex) { res.Failed.Add($"{m.DisplayName}:{ex.Message}"); }
        }
        res.Ok = res.Failed.Count == 0 && res.Succeeded.Count > 0;
        res.Message = $"批量删除完成:{res.Detail}" + FailedTail(res);
        App.WriteAppLog($"[模组批量] 删除 → 成功 {res.Succeeded.Count},失败 {res.Failed.Count}");
        return res;
    }

    // ==================== 内部工具 ====================

    /// <summary>目标路径必须落在规范 mods 根目录内(防路径拼接跑出预期范围)</summary>
    private static bool IsUnderModsRoot(string dir)
    {
        try
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\', '/');
            string root = Path.GetFullPath(AppPaths.Mods).TrimEnd('\\', '/');
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string UniqueName(string fileName, HashSet<string> used)
    {
        string name = fileName;
        int n = 1;
        while (!used.Add(name))
        {
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            name = $"{baseName}_{n++}{ext}";
        }
        return name;
    }

    /// <summary>目标目录已存在同名文件时改名(不覆盖目标实例原有的模组)</summary>
    private static string UniqueNameOnDisk(string fileName, string dir)
    {
        string candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate)) return fileName;
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int n = 1; n < 999; n++)
        {
            string name = $"{baseName}_{n}{ext}";
            if (!File.Exists(Path.Combine(dir, name))) return name;
        }
        return $"{baseName}_{DateTime.Now:HHmmss}{ext}";
    }

    private static string FailedTail(ModBatchResult res)
        => res.Failed.Count == 0 ? "" : "\n失败明细:\n" + string.Join("\n", res.Failed.Take(10))
                                        + (res.Failed.Count > 10 ? $"\n…另有 {res.Failed.Count - 10} 条" : "");

    private static long SafeLen(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { App.WriteAppLog($"[模组批量] 删除源文件失败 {path}:{ex.Message}"); }
    }
}
