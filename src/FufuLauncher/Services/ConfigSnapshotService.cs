// Copyright © FufuLauncher
//
// 实例多配置快照服务(Axolotl-1):
// 同一游戏实例可保存多套配置快照,快照内容 = 模组启用状态 + JVM/内存参数 + 游戏窗口设置;
// 一键应用即可整套切回,不用手动改参数、也不用反复增删模组文件。
//
// 实现要点:
// 1. 模组启用状态用「被禁用的文件名集合」表达 —— 与底层 .jar ↔ .disabled 重命名机制天然对齐,
//    应用快照时按集合差异做最小重命名(只动状态不一致的文件),不整目录扫描重写;
// 2. 快照数据存 InstanceExtrasService(根级 fufu-extras.json),不污染 instance.json;
// 3. 应用快照前先保存实例,任何单文件切换失败都收集成中文明细返回,绝不半途抛异常;
// 4. 新增模组(快照里没有的)保持当前状态不动,避免刚装的模组被莫名禁用。

using System.IO;

namespace FufuLauncher.Services;

/// <summary>快照应用结果</summary>
public sealed class SnapshotApplyResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    /// <summary>实际发生启用/禁用切换的模组数</summary>
    public int ToggledMods { get; set; }
    /// <summary>切换失败的模组明细</summary>
    public List<string> Failures { get; set; } = new();
}

public sealed class ConfigSnapshotService
{
    private readonly InstanceService _instances;
    private readonly ModManagerService _mods;
    private readonly InstanceExtrasService _extras;

    public ConfigSnapshotService(InstanceService instances, ModManagerService mods, InstanceExtrasService extras)
    {
        _instances = instances;
        _mods = mods;
        _extras = extras;
    }

    // ==================== 采集 ====================

    /// <summary>为实例拍一张快照(名称重复返回 null,由 UI 提示改名)</summary>
    public InstanceConfigSnapshot? Capture(string instanceId, string name, string note = "")
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null)
        {
            App.WriteAppLog($"[快照] 采集失败:实例 {instanceId} 不存在");
            return null;
        }

        List<ModInfo> mods = SafeLoadMods(instanceId);
        var snap = new InstanceConfigSnapshot
        {
            Name = (name ?? "").Trim(),
            Note = (note ?? "").Trim(),
            UseCustomMemory = inst.UseCustomMemory,
            Xms = inst.Xms,
            Xmx = inst.Xmx,
            ExtraJvmArgs = inst.ExtraJvmArgs,
            JavaPath = inst.JavaPath,
            JavaMajorVersion = inst.JavaMajorVersion,
            Width = inst.Width,
            Height = inst.Height,
            Fullscreen = inst.Fullscreen,
            TotalMods = mods.Count,
            DisabledMods = mods.Where(m => !m.Enabled)
                               .Select(m => Path.GetFileName(m.FilePath))
                               .Where(f => !string.IsNullOrEmpty(f))
                               .ToList()
        };
        if (snap.Name.Length == 0)
            snap.Name = $"快照 {DateTime.Now:MM-dd HH:mm}";

        var saved = _extras.AddSnapshot(instanceId, snap);
        if (saved != null)
            App.WriteAppLog($"[快照] 已保存「{saved.Name}」→ {inst.Name}(禁用 {snap.DisabledMods.Count}/{snap.TotalMods} 个模组)");
        return saved;
    }

    /// <summary>把当前实时配置覆盖写回指定快照(相当于「更新快照」):
    /// 先删同名旧档再采集,避免同名冲突导致采集失败</summary>
    public InstanceConfigSnapshot? Update(string instanceId, string snapshotId)
    {
        var old = _extras.FindSnapshot(instanceId, snapshotId);
        if (old == null) return null;
        _extras.DeleteSnapshot(instanceId, snapshotId);
        return Capture(instanceId, old.Name, old.Note);
    }

    // ==================== 应用 ====================

    /// <summary>应用快照:回写实例配置 + 对齐模组启用状态</summary>
    public SnapshotApplyResult Apply(string instanceId, string snapshotId)
    {
        var res = new SnapshotApplyResult();
        var snap = _extras.FindSnapshot(instanceId, snapshotId);
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (snap == null || inst == null)
        {
            res.Message = "快照或游戏版本不存在,请刷新后重试。";
            return res;
        }

        try
        {
            // 1) 实例配置整套回写
            inst.UseCustomMemory = snap.UseCustomMemory;
            inst.Xms = snap.Xms;
            inst.Xmx = snap.Xmx;
            inst.ExtraJvmArgs = snap.ExtraJvmArgs;
            if (!string.IsNullOrEmpty(snap.JavaPath)) inst.JavaPath = snap.JavaPath;
            if (snap.JavaMajorVersion > 0) inst.JavaMajorVersion = snap.JavaMajorVersion;
            inst.Width = snap.Width > 0 ? snap.Width : inst.Width;
            inst.Height = snap.Height > 0 ? snap.Height : inst.Height;
            inst.Fullscreen = snap.Fullscreen;
            _instances.SaveInstance(inst);

            // 2) 模组启用状态对齐(只动不一致的)
            var disabled = new HashSet<string>(
                snap.DisabledMods.Select(f => f ?? "").Where(f => f.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            List<ModInfo> mods = SafeLoadMods(instanceId);
            foreach (var m in mods)
            {
                string file = Path.GetFileName(m.FilePath);
                if (string.IsNullOrEmpty(file)) continue;
                // 2026-09-25 修复:只有文件名出现在快照禁用集(含 .disabled / 去后缀变体)才算快照记录过。
                // 原 known 含恒真条件(IsInSnapshotScope => TotalMods>0),导致快照后新装的模组被当成
                // 「快照里启用」强行启用,与注释「新增模组保持现状」矛盾。
                bool inSnapshot = disabled.Contains(file) || disabled.Contains(file + ".disabled")
                                  || disabled.Contains(TrimDisabled(file));
                if (!inSnapshot) continue;
                bool wantEnabled = false;   // 快照禁用集里的模组 → 快照时刻处于禁用,应用为禁用
                if (m.Enabled == wantEnabled) continue;
                if (_mods.ToggleMod(m.FilePath, wantEnabled)) res.ToggledMods++;
                else res.Failures.Add($"{m.DisplayName}(切换为「禁用」失败)");
            }

            res.Ok = true;
            res.Message = $"已切换到快照「{snap.Name}」:" +
                          $"内存 {(snap.UseCustomMemory ? $"{snap.Xms}/{snap.Xmx}MB" : "自动")}、" +
                          $"窗口 {(snap.Fullscreen ? "全屏" : $"{snap.Width}×{snap.Height}")}" +
                          (res.ToggledMods > 0 ? $",已同步 {res.ToggledMods} 个模组的启用状态。" : ",模组状态本就一致。");
            if (res.Failures.Count > 0)
                res.Message += $"\n以下 {res.Failures.Count} 个模组切换失败(可能被游戏占用):\n" + string.Join("\n", res.Failures.Take(8));
            App.WriteAppLog($"[快照] 已应用「{snap.Name}」→ {inst.Name},切换模组 {res.ToggledMods} 个,失败 {res.Failures.Count} 个");
            return res;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[快照] 应用失败:{ex}");
            res.Ok = false;
            res.Message = "应用快照失败:" + ex.Message;
            return res;
        }
    }

    private static string TrimDisabled(string fileName)
        => fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length] : fileName;

    // ==================== 管理 ====================

    public List<InstanceConfigSnapshot> List(string instanceId) => _extras.SnapshotsOf(instanceId);

    public bool Delete(string instanceId, string snapshotId) => _extras.DeleteSnapshot(instanceId, snapshotId);

    public bool Rename(string instanceId, string snapshotId, string newName)
        => _extras.RenameSnapshot(instanceId, snapshotId, newName);

    /// <summary>读取模组列表(切当前实例后加载,异常返回空表不抛)</summary>
    private List<ModInfo> SafeLoadMods(string instanceId)
    {
        try
        {
            _mods.SetCurrentInstance(instanceId);
            return _mods.LoadMods();
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[快照] 模组列表读取失败({instanceId}):{ex.Message}");
            return new List<ModInfo>();
        }
    }
}
