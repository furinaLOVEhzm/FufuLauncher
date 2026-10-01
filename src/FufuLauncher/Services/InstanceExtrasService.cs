// Copyright © FufuLauncher
//
// 实例级扩展配置存储(根级 fufu-extras.json):
// 承载 instance.json 之外的全部新增持久化数据 —— 配置快照、实例分组、启动会话记录、
// 前后置脚本开关与内容、磁盘占用缓存、用户自定义 JVM 模板、日志筛选收藏条件。
//
// 设计原则:
// 1. 完全不改动 GameInstance 既有字段与 instance.json 结构(老存档 100% 向后兼容);
// 2. 全部新增数据集中到一个根级 JSON,按实例 Id 索引,单文件原子落盘(tmp → Flush → Move);
// 3. 文件缺失/损坏一律回退空档,只记日志绝不把异常抛给 UI;
// 4. 所有读写走内部锁,允许后台线程(磁盘统计、下载预加载)与 UI 线程并发访问。

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FufuLauncher.Services;

/// <summary>实例配置快照:同一实例可保存多套,一键切换(模组启用状态 + JVM 参数 + 游戏窗口设置)</summary>
public sealed class InstanceConfigSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Note { get; set; } = "";

    // ---- 内存与 JVM ----
    public bool UseCustomMemory { get; set; }
    public int Xms { get; set; }
    public int Xmx { get; set; }
    public string ExtraJvmArgs { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public int JavaMajorVersion { get; set; }

    // ---- 游戏窗口 ----
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Fullscreen { get; set; }

    // ---- 模组启用状态:只记录「被禁用」的模组文件名(.disabled),应用快照时按此对齐 ----
    public List<string> DisabledMods { get; set; } = new();
    /// <summary>拍快照时模组总数(供 UI 展示「N 个模组中禁用 M 个」)</summary>
    public int TotalMods { get; set; }

    public string Summary =>
        $"内存 {(UseCustomMemory ? $"{Xms}/{Xmx}MB" : "自动")} · 窗口 {(Fullscreen ? "全屏" : $"{Width}×{Height}")}" +
        $" · 模组 {TotalMods} 个(禁用 {DisabledMods.Count})";
}

/// <summary>一次游戏启动会话记录(启动时间 / 是否成功 / 崩溃简要原因)</summary>
public sealed class LaunchSessionRecord
{
    public DateTime StartTime { get; set; } = DateTime.Now;
    public DateTime EndTime { get; set; }
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string VersionId { get; set; } = "";
    public string Loader { get; set; } = "";
    /// <summary>失败/崩溃简要原因(已从日志提取的一句话,非完整堆栈)</summary>
    public string Reason { get; set; } = "";
    /// <summary>本次启动前完整性校验修复的文件数(0 = 未修复)</summary>
    public int RepairedFiles { get; set; }

    public int DurationSeconds => EndTime > StartTime ? (int)(EndTime - StartTime).TotalSeconds : 0;
    public string DurationDisplay
    {
        get
        {
            int s = DurationSeconds;
            if (s <= 0) return "-";
            return s < 60 ? $"{s} 秒" : s < 3600 ? $"{s / 60} 分 {s % 60} 秒" : $"{s / 3600} 小时 {s % 3600 / 60} 分";
        }
    }
}

/// <summary>实例分组(LauncherX-4:用户自建文件夹,实例可拖入,支持折叠)</summary>
public sealed class InstanceGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Collapsed { get; set; }
    public int Order { get; set; }
}

/// <summary>JVM 参数预设模板(Celestial-2:内置 + 用户自定义)</summary>
public sealed class JvmTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Args { get; set; } = "";
    /// <summary>内置模板不可删除/不可改名</summary>
    public bool BuiltIn { get; set; }
    /// <summary>最低 Java 主版本(低于此版本参数可能不被识别)</summary>
    public int MinJavaMajor { get; set; } = 8;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>日志筛选收藏条件(Axolotl-4)</summary>
public sealed class LogFilterPreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Keyword { get; set; } = "";
    public bool ShowCrash { get; set; } = true;
    public bool ShowWarn { get; set; } = true;
    public bool ShowInfo { get; set; } = true;
    public bool HideJunk { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>单个实例的扩展配置</summary>
public sealed class InstanceExtras
{
    /// <summary>所属分组 Id("" = 未分组)</summary>
    public string GroupId { get; set; } = "";
    public List<InstanceConfigSnapshot> Snapshots { get; set; } = new();
    public List<LaunchSessionRecord> LaunchHistory { get; set; } = new();

    // ---- 前后置脚本(Celestial-7)----
    /// <summary>脚本功能总开关(关闭后前后置脚本一律不执行)</summary>
    public bool ScriptsEnabled { get; set; }
    /// <summary>游戏启动前执行的脚本(可为 .bat/.cmd/.ps1/.exe 的完整路径,支持带参数)</summary>
    public string PreLaunchScript { get; set; } = "";
    /// <summary>游戏退出后执行的脚本</summary>
    public string PostExitScript { get; set; } = "";
    /// <summary>脚本执行超时秒数(超时强制结束,避免卡住启动/退出流程)</summary>
    public int ScriptTimeoutSeconds { get; set; } = 60;

    // ---- 磁盘占用缓存(Celestial-5:统计很慢,结果缓存复用)----
    public long DiskBytes { get; set; }
    public DateTime DiskMeasuredAt { get; set; }
    /// <summary>是否被判定为「占用过大」(高亮提醒)</summary>
    public bool DiskOversize { get; set; }
}

/// <summary>扩展配置根对象</summary>
public sealed class ExtrasRoot
{
    public int Version { get; set; } = 1;
    public Dictionary<string, InstanceExtras> Instances { get; set; } = new();
    public List<InstanceGroup> Groups { get; set; } = new();
    /// <summary>用户自定义 JVM 模板(内置模板不入库,由 JvmTemplateService 提供)</summary>
    public List<JvmTemplate> JvmTemplates { get; set; } = new();
    public List<LogFilterPreset> LogFilters { get; set; } = new();
}

public sealed class InstanceExtrasService
{
    /// <summary>启动会话记录保留条数(超出丢弃最旧)</summary>
    public const int MaxLaunchHistory = 40;
    /// <summary>单实例配置快照上限(防误操作无限堆积)</summary>
    public const int MaxSnapshots = 24;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string ExtrasFile => Path.Combine(AppPaths.Root, "fufu-extras.json");

    private readonly object _lock = new();
    private bool _loaded;
    private bool _dirty;

    public ExtrasRoot Root { get; private set; } = new();

    /// <summary>数据发生变化(分组/快照/历史/脚本),UI 需自行调度刷新</summary>
    public event Action? Changed;

    // ==================== 加载与落盘 ====================

    public void Load()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(ExtrasFile)) return;
                var root = JsonSerializer.Deserialize<ExtrasRoot>(File.ReadAllText(ExtrasFile));
                if (root == null) return;
                // 反序列化出的字典不带比较器,重建为忽略大小写(实例 Id 大小写在历史数据中可能漂移)
                var dict = new Dictionary<string, InstanceExtras>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in root.Instances)
                    if (!string.IsNullOrEmpty(kv.Key)) dict[kv.Key] = kv.Value ?? new InstanceExtras();
                root.Instances = dict;
                root.Groups ??= new List<InstanceGroup>();
                root.JvmTemplates ??= new List<JvmTemplate>();
                root.LogFilters ??= new List<LogFilterPreset>();
                Root = root;
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[扩展配置] fufu-extras.json 读取失败,已回退空档:{ex.Message}");
                Root = new ExtrasRoot { Instances = new Dictionary<string, InstanceExtras>(StringComparer.OrdinalIgnoreCase) };
            }
        }
    }

    /// <summary>原子落盘(tmp → Flush(flushToDisk) → Move(overwrite))</summary>
    public void Save()
    {
        lock (_lock)
        {
            try
            {
                string path = ExtrasFile;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, Root, JsonOpts);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex) { App.WriteAppLog($"[扩展配置] 保存失败:{ex.Message}"); }
        }
    }

    /// <summary>标记已变更并落盘 + 广播(UI 侧统一走这个方法,避免漏存)</summary>
    private void Commit()
    {
        _dirty = true;
        Save();
        try { Changed?.Invoke(); } catch { /* 订阅者异常不影响存储 */ }
    }

    /// <summary>是否存在未落盘变更(供退出前兜底检查)</summary>
    public bool HasPendingChanges { get { lock (_lock) return _dirty; } }

    // ==================== 实例扩展档 ====================

    /// <summary>取实例扩展档(不存在则新建并入库,不落盘;调用方改动后需自行 Commit)</summary>
    public InstanceExtras Get(string instanceId)
    {
        lock (_lock)
        {
            Load();
            if (string.IsNullOrEmpty(instanceId)) return new InstanceExtras();
            if (!Root.Instances.TryGetValue(instanceId, out var ex))
            {
                ex = new InstanceExtras();
                Root.Instances[instanceId] = ex;
            }
            return ex;
        }
    }

    public bool TryGet(string instanceId, out InstanceExtras extras)
    {
        lock (_lock)
        {
            Load();
            return Root.Instances.TryGetValue(instanceId ?? "", out extras!);
        }
    }

    /// <summary>删除实例扩展档(实例被删除/卸载时调用)</summary>
    public void Remove(string instanceId)
    {
        lock (_lock)
        {
            Load();
            if (Root.Instances.Remove(instanceId ?? "")) Commit();
        }
    }

    /// <summary>实例克隆:把源实例的快照 / 脚本 / 分组一并继承给新实例(Axolotl-5)</summary>
    public void CopyTo(string srcId, string dstId, bool inheritGroup = false)
    {
        if (string.IsNullOrEmpty(srcId) || string.IsNullOrEmpty(dstId)) return;
        lock (_lock)
        {
            Load();
            if (!Root.Instances.TryGetValue(srcId, out var src)) return;
            var dst = new InstanceExtras
            {
                GroupId = inheritGroup ? src.GroupId : "",
                ScriptsEnabled = src.ScriptsEnabled,
                PreLaunchScript = src.PreLaunchScript,
                PostExitScript = src.PostExitScript,
                ScriptTimeoutSeconds = src.ScriptTimeoutSeconds,
                Snapshots = src.Snapshots.Select(CloneSnapshot).ToList(),
                // 启动历史属于源实例的运行痕迹,不继承
                LaunchHistory = new List<LaunchSessionRecord>()
            };
            Root.Instances[dstId] = dst;
            Commit();
        }
    }

    private static InstanceConfigSnapshot CloneSnapshot(InstanceConfigSnapshot s) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Name = s.Name,
        CreatedAt = DateTime.Now,
        Note = s.Note,
        UseCustomMemory = s.UseCustomMemory,
        Xms = s.Xms,
        Xmx = s.Xmx,
        ExtraJvmArgs = s.ExtraJvmArgs,
        JavaPath = s.JavaPath,
        JavaMajorVersion = s.JavaMajorVersion,
        Width = s.Width,
        Height = s.Height,
        Fullscreen = s.Fullscreen,
        DisabledMods = new List<string>(s.DisabledMods),
        TotalMods = s.TotalMods
    };

    /// <summary>实例改名后同步(扩展档以 Id 索引,无需迁移;仅广播刷新)</summary>
    public void NotifyChanged() { lock (_lock) { try { Changed?.Invoke(); } catch { } } }

    // ==================== 实例分组(LauncherX-4) ====================

    public List<InstanceGroup> GroupsSnapshot()
    {
        lock (_lock) { Load(); return Root.Groups.OrderBy(g => g.Order).ThenBy(g => g.Name).ToList(); }
    }

    public InstanceGroup? AddGroup(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 32) return null;
        lock (_lock)
        {
            Load();
            if (Root.Groups.Any(g => g.Name == name)) return null;
            var g = new InstanceGroup
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                Name = name,
                Order = Root.Groups.Count == 0 ? 0 : Root.Groups.Max(x => x.Order) + 1
            };
            Root.Groups.Add(g);
            Commit();
            return g;
        }
    }

    public bool RenameGroup(string groupId, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0 || newName.Length > 32) return false;
        lock (_lock)
        {
            Load();
            var g = Root.Groups.FirstOrDefault(x => x.Id == groupId);
            if (g == null || Root.Groups.Any(x => x.Id != groupId && x.Name == newName)) return false;
            g.Name = newName;
            Commit();
            return true;
        }
    }

    /// <summary>删除分组:组内实例回到「未分组」,绝不连带删除实例</summary>
    public void DeleteGroup(string groupId)
    {
        lock (_lock)
        {
            Load();
            var g = Root.Groups.FirstOrDefault(x => x.Id == groupId);
            if (g == null) return;
            Root.Groups.Remove(g);
            foreach (var kv in Root.Instances.Where(kv => kv.Value.GroupId == groupId))
                kv.Value.GroupId = "";
            Commit();
        }
    }

    public void SetGroupCollapsed(string groupId, bool collapsed)
    {
        lock (_lock)
        {
            Load();
            var g = Root.Groups.FirstOrDefault(x => x.Id == groupId);
            if (g == null || g.Collapsed == collapsed) return;
            g.Collapsed = collapsed;
            Commit();
        }
    }

    /// <summary>把实例放进分组(groupId 传空 = 移出分组)</summary>
    public void SetInstanceGroup(string instanceId, string groupId)
    {
        lock (_lock)
        {
            Load();
            var ex = Get(instanceId);
            if (ex.GroupId == groupId) return;
            ex.GroupId = groupId ?? "";
            Commit();
        }
    }

    /// <summary>调整分组顺序(拖拽排序后按新顺序重写 Order)</summary>
    public void ReorderGroups(IReadOnlyList<string> orderedIds)
    {
        lock (_lock)
        {
            Load();
            for (int i = 0; i < orderedIds.Count; i++)
            {
                var g = Root.Groups.FirstOrDefault(x => x.Id == orderedIds[i]);
                if (g != null) g.Order = i;
            }
            Commit();
        }
    }

    // ==================== 配置快照(Axolotl-1) ====================

    public List<InstanceConfigSnapshot> SnapshotsOf(string instanceId)
    {
        lock (_lock) { Load(); return Get(instanceId).Snapshots.OrderByDescending(s => s.CreatedAt).ToList(); }
    }

    /// <summary>新增快照;超出上限时丢弃最旧的一条。返回 null 表示同名快照已存在</summary>
    public InstanceConfigSnapshot? AddSnapshot(string instanceId, InstanceConfigSnapshot snapshot)
    {
        lock (_lock)
        {
            Load();
            var ex = Get(instanceId);
            if (string.IsNullOrWhiteSpace(snapshot.Name)) return null;
            snapshot.Name = snapshot.Name.Trim();
            if (ex.Snapshots.Any(s => s.Name == snapshot.Name)) return null;
            if (string.IsNullOrEmpty(snapshot.Id)) snapshot.Id = Guid.NewGuid().ToString("N")[..12];
            snapshot.CreatedAt = DateTime.Now;
            ex.Snapshots.Add(snapshot);
            while (ex.Snapshots.Count > MaxSnapshots)
            {
                var oldest = ex.Snapshots.OrderBy(s => s.CreatedAt).First();
                ex.Snapshots.Remove(oldest);
            }
            Commit();
            return snapshot;
        }
    }

    public bool RenameSnapshot(string instanceId, string snapshotId, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return false;
        lock (_lock)
        {
            Load();
            var s = Get(instanceId).Snapshots.FirstOrDefault(x => x.Id == snapshotId);
            if (s == null) return false;
            s.Name = newName;
            Commit();
            return true;
        }
    }

    public bool DeleteSnapshot(string instanceId, string snapshotId)
    {
        lock (_lock)
        {
            Load();
            var ex = Get(instanceId);
            var s = ex.Snapshots.FirstOrDefault(x => x.Id == snapshotId);
            if (s == null) return false;
            ex.Snapshots.Remove(s);
            Commit();
            return true;
        }
    }

    public InstanceConfigSnapshot? FindSnapshot(string instanceId, string snapshotId)
    {
        lock (_lock) { Load(); return Get(instanceId).Snapshots.FirstOrDefault(x => x.Id == snapshotId); }
    }

    // ==================== 启动会话记录(BlockHelm-6) ====================

    /// <summary>追加一条启动记录(自动裁剪到 MaxLaunchHistory 条,最新的在前)</summary>
    public void AddLaunchRecord(string instanceId, LaunchSessionRecord record)
    {
        lock (_lock)
        {
            Load();
            var ex = Get(instanceId);
            ex.LaunchHistory.Insert(0, record);
            while (ex.LaunchHistory.Count > MaxLaunchHistory)
                ex.LaunchHistory.RemoveAt(ex.LaunchHistory.Count - 1);
            Commit();
        }
    }

    /// <summary>更新最近一条记录的结束状态(游戏退出时调用)</summary>
    public void FinishLastLaunch(string instanceId, bool success, int exitCode, string reason)
    {
        lock (_lock)
        {
            Load();
            var ex = Get(instanceId);
            var last = ex.LaunchHistory.FirstOrDefault();
            if (last == null) return;
            last.EndTime = DateTime.Now;
            last.Success = success;
            last.ExitCode = exitCode;
            if (!string.IsNullOrWhiteSpace(reason)) last.Reason = reason.Trim();
            Commit();
        }
    }

    public List<LaunchSessionRecord> LaunchHistoryOf(string instanceId)
    {
        lock (_lock) { Load(); return Get(instanceId).LaunchHistory.ToList(); }
    }

    public void ClearLaunchHistory(string instanceId)
    {
        lock (_lock)
        {
            Load();
            var ex = Get(instanceId);
            if (ex.LaunchHistory.Count == 0) return;
            ex.LaunchHistory.Clear();
            Commit();
        }
    }

    // ==================== 前后置脚本(Celestial-7) ====================

    public (bool Enabled, string Pre, string Post, int Timeout) ScriptsOf(string instanceId)
    {
        lock (_lock)
        {
            Load();
            var ex = Get(instanceId);
            return (ex.ScriptsEnabled, ex.PreLaunchScript, ex.PostExitScript,
                    ex.ScriptTimeoutSeconds <= 0 ? 60 : ex.ScriptTimeoutSeconds);
        }
    }

    public void SaveScripts(string instanceId, bool enabled, string pre, string post, int timeoutSeconds)
    {
        lock (_lock)
        {
            Load();
            var ex = Get(instanceId);
            ex.ScriptsEnabled = enabled;
            ex.PreLaunchScript = (pre ?? "").Trim();
            ex.PostExitScript = (post ?? "").Trim();
            ex.ScriptTimeoutSeconds = Math.Clamp(timeoutSeconds, 5, 3600);
            Commit();
        }
    }

    // ==================== 磁盘占用缓存(Celestial-5) ====================

    public void SetDiskUsage(string instanceId, long bytes, bool oversize)
    {
        lock (_lock)
        {
            Load();
            var ex = Get(instanceId);
            ex.DiskBytes = bytes;
            ex.DiskOversize = oversize;
            ex.DiskMeasuredAt = DateTime.Now;
            Save();   // 统计结果仅缓存,不广播(避免统计过程反复刷新列表)
        }
    }

    public (long Bytes, DateTime At, bool Oversize) DiskUsageOf(string instanceId)
    {
        lock (_lock)
        {
            Load();
            var ex = Get(instanceId);
            return (ex.DiskBytes, ex.DiskMeasuredAt, ex.DiskOversize);
        }
    }

    // ==================== 自定义 JVM 模板(Celestial-2) ====================

    public List<JvmTemplate> CustomJvmTemplates()
    {
        lock (_lock) { Load(); return Root.JvmTemplates.OrderByDescending(t => t.CreatedAt).ToList(); }
    }

    public JvmTemplate? AddJvmTemplate(string name, string description, string args, int minJavaMajor)
    {
        name = (name ?? "").Trim();
        args = (args ?? "").Trim();
        if (name.Length == 0 || args.Length == 0) return null;
        lock (_lock)
        {
            Load();
            if (Root.JvmTemplates.Any(t => t.Name == name)) return null;
            var t = new JvmTemplate
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                Name = name,
                Description = (description ?? "").Trim(),
                Args = args,
                BuiltIn = false,
                MinJavaMajor = minJavaMajor,
                CreatedAt = DateTime.Now
            };
            Root.JvmTemplates.Add(t);
            Commit();
            return t;
        }
    }

    public bool DeleteJvmTemplate(string id)
    {
        lock (_lock)
        {
            Load();
            var t = Root.JvmTemplates.FirstOrDefault(x => x.Id == id);
            if (t == null) return false;
            Root.JvmTemplates.Remove(t);
            Commit();
            return true;
        }
    }

    // ==================== 日志筛选收藏(Axolotl-4) ====================

    public List<LogFilterPreset> LogFilterPresets()
    {
        lock (_lock) { Load(); return Root.LogFilters.OrderBy(p => p.Name).ToList(); }
    }

    public LogFilterPreset? AddLogFilterPreset(LogFilterPreset preset)
    {
        preset.Name = (preset.Name ?? "").Trim();
        if (preset.Name.Length == 0) return null;
        lock (_lock)
        {
            Load();
            if (Root.LogFilters.Any(p => p.Name == preset.Name)) return null;
            if (string.IsNullOrEmpty(preset.Id)) preset.Id = Guid.NewGuid().ToString("N")[..10];
            preset.CreatedAt = DateTime.Now;
            Root.LogFilters.Add(preset);
            Commit();
            return preset;
        }
    }

    public bool DeleteLogFilterPreset(string id)
    {
        lock (_lock)
        {
            Load();
            var p = Root.LogFilters.FirstOrDefault(x => x.Id == id);
            if (p == null) return false;
            Root.LogFilters.Remove(p);
            Commit();
            return true;
        }
    }
}
