// Copyright © FufuLauncher
//
// 游戏实例(游戏版本)管理服务:多隔离实例,每个实例一个独立游戏工作目录。
// 目录规范(严格固化在 APP\MCGAME 下):
//   instances\{InstanceId}\      游戏工作目录(--gameDirectory)
//     ├── saves → 联接 → saves\{InstanceId}   存档(物理存放在规范 saves 目录)
//     ├── mods  → 联接 → mods\{InstanceId}    模组(物理存放在规范 mods 目录)
//     ├── resourcepacks\         资源包
//     ├── shaderpacks\           光影包
//     ├── options.txt            游戏配置
//     └── instance.json          实例元信息
//   游戏本体/依赖/资源全实例共享,统一存放于 versions\、libraries\、assets\。
// 支持:重命名、复制、删除、导出 zip 备份、导入已有 .minecraft。
// 旧结构(实例内 .minecraft 层/实例内 mods/实例内 saves)在加载时自动迁移到新规范。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

public class GameInstance
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string VersionId { get; set; } = "";        // Mojang 版本号
    /// <summary>加载器版本 JSON 的 id(安装 Forge/Fabric 等后生成,inheritsFrom 原版);启动时优先使用</summary>
    public string? LoaderVersionId { get; set; }
    public string? ModLoader { get; set; }              // Forge / Fabric / Quilt / null
    public string? ModLoaderVersion { get; set; }
    public int JavaMajorVersion { get; set; } = 17;
    public string JavaPath { get; set; } = "";
    public int Xms { get; set; } = 1024;
    public int Xmx { get; set; } = 4096;
    /// <summary>单版本自定义内存开关(版本设置分区设置):为 true 时启动优先采用本实例 Xms/Xmx,
    /// 覆盖全局智能/手动策略;启动时实时复验仍兜底,绝不超分</summary>
    public bool UseCustomMemory { get; set; }
    /// <summary>整合包级建议内存(MB;GTNH 等大型整合包导入时写入);0=无建议。
    /// 智能内存模式下若计算值低于该值则上调至此,但启动时实时复验仍兜底,绝不超分</summary>
    public int RecommendedMemoryMb { get; set; }
    public string ExtraJvmArgs { get; set; } = "";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public bool Fullscreen { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>安装是否已完成。失败保留实例时置 false,版本管理卡片显示「安装未完成」红标,
    /// 启动时引导重新安装(已下载文件幂等复用,不重复下)。</summary>
    public bool InstallComplete { get; set; } = true;
    public DateTime LastPlayedAt { get; set; }
    public long TotalPlayTimeSeconds { get; set; }

    /// <summary>列表控件(ComboBox/ListBox)默认显示文本,避免输出全命名空间类名</summary>
    public override string ToString() =>
        string.IsNullOrEmpty(Name) ? (string.IsNullOrEmpty(Id) ? "游戏版本" : Id) : Name;
}

public class InstanceService
{
    // 实例目录固化在 APP\MCGAME\instances(避免 static readonly 在目录就绪前初始化)
    private static string InstancesDir => AppPaths.Instances;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly object _saveLock = new();

    private readonly NativeInteropService _nativeInterop;
    private readonly JavaScanService _javaScanService;
    private readonly JavaRuntimeService _javaRuntimeService;

    private List<GameInstance> _instances = new();
    /// <summary>会话内实例列表。RefreshInstances 引用交换整体替换(并发读者持旧引用安全)。</summary>
    public List<GameInstance> Instances => _instances;
    /// <summary>会话内实例列表是否已加载(缓存标记,避免各页重复扫盘)</summary>
    private bool _instancesLoaded;

    /// <summary>游戏版本列表变更(创建/重命名/删除/卸载/复制);订阅方自行 Dispatcher 调度。
    /// 注意:RefreshInstances 不触发本事件,避免订阅方回环重扫。</summary>
    public event Action? InstancesChanged;

    public InstanceService(NativeInteropService nativeInterop, JavaScanService javaScanService,
                           JavaRuntimeService javaRuntimeService)
    {
        _nativeInterop = nativeInterop;
        _javaScanService = javaScanService;
        _javaRuntimeService = javaRuntimeService;
    }

    public void LoadInstances()
    {
        // 会话内缓存:磁盘扫描一次即可,增删改均就地维护 Instances 列表;
        // 各页面反复调用不再重复扫盘(高频 IO 消除),失效点用 RefreshInstances() 强制重读
        if (_instancesLoaded) return;
        RefreshInstances();
    }

    /// <summary>强制从磁盘重新扫描实例列表(失效点:安装完成后等)。同步版:就地重扫并换入结果。</summary>
    public void RefreshInstances()
    {
        _instancesLoaded = true;
        var scanned = ScanInstancesFromDisk();
        // 引用交换而非就地 Clear+AddRange:后台线程(预加载 worker)替换引用时,
        // UI 线程正在 foreach 的旧列表不再被修改,枚举器安全走完,杜绝"集合已修改"竞态崩溃
        _instances = scanned;
    }

    /// <summary>异步重扫(消除版本管理页偶发卡顿的关键):磁盘枚举 / instance.json 读取解析 /
    /// 旧结构迁移(EnsureInstanceLayout,含目录移动与联接重建)全部放到后台线程,
    /// 仅在 UI 线程快速换入结果列表,大批量实例时也不阻塞主线程。
    /// 约定在 UI 线程调用:await 之后的换入自动回到调用上下文(UI 线程),与其它读者串行不竞态。</summary>
    public async Task RefreshInstancesAsync()
    {
        _instancesLoaded = true;
        var scanned = await Task.Run(ScanInstancesFromDisk);
        _instances = scanned; // 引用交换,同上:任何并发读者持有旧引用均安全
    }

    /// <summary>扫描 instances 目录并返回全新列表:纯后台工作,不触碰共享 Instances,
    /// 因此可安全地在 Task.Run 内执行(同步版与异步版共用,行为一致)。</summary>
    private List<GameInstance> ScanInstancesFromDisk()
    {
        var result = new List<GameInstance>();
        try
        {
            Directory.CreateDirectory(InstancesDir);
            foreach (var dir in Directory.EnumerateDirectories(InstancesDir))
            {
                string metaPath = Path.Combine(dir, "instance.json");
                if (!File.Exists(metaPath)) continue;
                try
                {
                    var json = File.ReadAllText(metaPath);
                    var inst = JsonSerializer.Deserialize<GameInstance>(json, JsonOpts);
                    if (inst != null)
                    {
                        inst.Id = Path.GetFileName(dir);
                        EnsureInstanceLayout(inst.Id); // 旧结构自动迁移到新规范
                        result.Add(inst);
                    }
                }
                catch (Exception ex)
                {
                    App.WriteAppLog($"[游戏版本] 跳过损坏的实例 {Path.GetFileName(dir)}:{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[游戏版本] 实例目录扫描失败:{ex.Message}");
        }
        return result;
    }

    /// <summary>该实例的存档物理目录(规范 saves\{实例id})</summary>
    public static string GetSavesPhysicalDir(string instanceId) =>
        Path.Combine(AppPaths.Saves, instanceId);

    /// <summary>该实例的模组物理目录(规范 mods\{实例id})</summary>
    public static string GetModsPhysicalDir(string instanceId) =>
        Path.Combine(AppPaths.Mods, instanceId);

    /// <summary>
    /// 确保实例目录符合新规范:
    /// 1. 旧 .minecraft 层拆解:versions/libraries/assets 上提为全局共享,
    ///    saves/mods 迁入规范目录,其余内容上提到实例目录;
    /// 2. saves/mods 联接(实例目录内 → 规范物理目录)缺失或错误时重建。
    /// 迁移全部同盘 Move,失败仅记日志不阻断。
    /// </summary>
    public void EnsureInstanceLayout(string instanceId)
    {
        try
        {
            string instDir = GetInstanceDir(instanceId);
            if (!Directory.Exists(instDir)) return;

            // ---- 0. 实例目录顶层混入的共享资源上提到全局(懒人包整目录解压场景) ----
            foreach (var shared in new[] { ("versions", AppPaths.Versions),
                                           ("libraries", AppPaths.Libraries),
                                           ("assets", AppPaths.Assets) })
            {
                string srcTop = Path.Combine(instDir, shared.Item1);
                if (Directory.Exists(srcTop) && !JunctionHelper.IsJunction(srcTop))
                {
                    MoveMerge(srcTop, shared.Item2);
                    TryDeleteEmptyDir(srcTop);
                }
            }

            // ---- 1. 旧 .minecraft 层拆解 ----
            string mcOld = Path.Combine(instDir, ".minecraft");
            if (Directory.Exists(mcOld) && !JunctionHelper.IsJunction(mcOld))
            {
                // 共享资源上提到 Root 级
                foreach (var shared in new[] { ("versions", AppPaths.Versions),
                                               ("libraries", AppPaths.Libraries),
                                               ("assets", AppPaths.Assets) })
                {
                    string src = Path.Combine(mcOld, shared.Item1);
                    if (Directory.Exists(src) && !JunctionHelper.IsJunction(src))
                    {
                        MoveMerge(src, shared.Item2);
                        TryDeleteEmptyDir(src);
                    }
                }
                // 存档 → saves\{id}
                string mcSaves = Path.Combine(mcOld, "saves");
                if (Directory.Exists(mcSaves) && !JunctionHelper.IsJunction(mcSaves))
                {
                    MoveMerge(mcSaves, GetSavesPhysicalDir(instanceId));
                    TryDeleteEmptyDir(mcSaves);
                }
                // .minecraft 内散落的 mods 归位到 mods\{id}
                string mcMods = Path.Combine(mcOld, "mods");
                if (Directory.Exists(mcMods) && !JunctionHelper.IsJunction(mcMods))
                {
                    MoveMerge(mcMods, GetModsPhysicalDir(instanceId));
                    TryDeleteEmptyDir(mcMods);
                }
                // 其余内容(config/options.txt/resourcepacks 等)上提到实例目录
                foreach (var entry in Directory.EnumerateFileSystemEntries(mcOld))
                {
                    string name = Path.GetFileName(entry);
                    string dst = Path.Combine(instDir, name);
                    try
                    {
                        if (Directory.Exists(entry))
                        {
                            if (!Directory.Exists(dst)) Directory.Move(entry, dst);
                            else { MoveMerge(entry, dst); TryDeleteEmptyDir(entry); }
                        }
                        else if (!File.Exists(dst)) File.Move(entry, dst);
                    }
                    catch (Exception ex) { App.WriteAppLog($"[游戏版本] 迁移 .minecraft/{name} 失败:{ex.Message}"); }
                }
                TryDeleteEmptyDir(mcOld);
                App.WriteAppLog($"[游戏版本] {instanceId} 旧 .minecraft 结构已迁移到新规范");
            }

            // ---- 2. saves/mods:实体目录迁入规范位置,再建联接 ----
            EnsureJunctionDir(instDir, "saves", GetSavesPhysicalDir(instanceId), instanceId);
            EnsureJunctionDir(instDir, "mods", GetModsPhysicalDir(instanceId), instanceId);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[游戏版本] 布局迁移异常 {instanceId}:{ex.Message}");
        }
    }

    /// <summary>
    /// 确保实例目录内 {name} 是指向 physicalDir 的联接:
    /// 实体目录有内容时先迁入物理目录;联接缺失/指向错误时重建。
    /// 联接创建失败时回退保留实体目录(功能不受影响)。
    /// </summary>
    private static void EnsureJunctionDir(string instDir, string name, string physicalDir, string instanceId)
    {
        string link = Path.Combine(instDir, name);
        if (JunctionHelper.IsJunction(link))
        {
            var target = JunctionHelper.GetJunctionTarget(link);
            if (!string.IsNullOrEmpty(target) &&
                Path.GetFullPath(target).Equals(Path.GetFullPath(physicalDir), StringComparison.OrdinalIgnoreCase))
                return; // 已正确
            JunctionHelper.DeleteJunctionOnly(link);
        }
        else if (Directory.Exists(link))
        {
            // 实体目录:内容迁入规范物理目录(空目录直接删)
            MoveMerge(link, physicalDir);
            TryDeleteEmptyDir(link);
            if (Directory.Exists(link)) return; // 迁移未腾空,保留实体目录不强推联接
        }
        if (!JunctionHelper.CreateJunction(link, physicalDir))
        {
            // 联接失败兜底:保证游戏目录内有可用的实体目录
            Directory.CreateDirectory(link);
            App.WriteAppLog($"[游戏版本] {instanceId} 联接创建失败,{name} 回退为版本目录内普通目录");
        }
    }

    /// <summary>把 src 目录内容合并移动到 dst(同名不覆盖),搬空后删除 src</summary>
    private static void MoveMerge(string src, string dst)
    {
        try
        {
            Directory.CreateDirectory(dst);
            foreach (var sub in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(sub);
                string subDst = Path.Combine(dst, name);
                if (Directory.Exists(subDst)) MoveMerge(sub, subDst);
                else Directory.Move(sub, subDst);
            }
            foreach (var file in Directory.GetFiles(src))
            {
                string name = Path.GetFileName(file);
                string fileDst = Path.Combine(dst, name);
                if (!File.Exists(fileDst)) File.Move(file, fileDst);
            }
            TryDeleteEmptyDir(src);
        }
        catch (Exception ex) { App.WriteAppLog($"[游戏版本] MoveMerge 失败 {src} → {dst}:{ex.Message}"); }
    }

    private static void TryDeleteEmptyDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !JunctionHelper.IsJunction(dir) &&
                !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { }
    }

    /// <summary>实例名 → 目录名:清洗文件系统非法字符,防中文以外的特殊符号炸目录创建</summary>
    private static string SanitizeDirName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c == '.' ? '_' : c).ToArray();
        string clean = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(clean) ? "game" : clean;
    }

    /// <summary>安全审计兜底(2026-08-28):规范化路径并断言其位于预期根目录之内,
    /// 防 '..' 逃逸/联接指向造成路径遍历;越界直接抛中文异常阻断操作。</summary>
    private static string AssertPathInside(string path, string root)
    {
        string full = Path.GetFullPath(path);
        string rootFull = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"非法路径越出数据目录,操作已阻断:{full}");
        return full;
    }

    /// <summary>生成唯一实例 ID(秒级时间戳):同秒重复创建同名实例时自动追加递增后缀,
    /// 杜绝目录撞车导致的 meta 互覆盖与存档/模组物理目录串联(2026-08-28 全局审计修复)</summary>
    private string MakeUniqueId(string sanitizedBase)
    {
        string id = $"{sanitizedBase}_{DateTime.Now:yyyyMMdd_HHmmss}";
        int n = 2;
        while (Directory.Exists(GetInstanceDir(id)) || Instances.Any(i => i.Id == id))
            id = $"{sanitizedBase}_{DateTime.Now:yyyyMMdd_HHmmss}_{n++}";
        return id;
    }

    public GameInstance CreateInstance(string name, string versionId, int javaMajor)
    {
        string id = MakeUniqueId(SanitizeDirName(name));
        string dir = AssertPathInside(GetInstanceDir(id), InstancesDir);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(dir, "shaderpacks"));
        // 存档/模组物理目录落在规范 saves/mods,实例目录内建联接
        Directory.CreateDirectory(GetSavesPhysicalDir(id));
        Directory.CreateDirectory(GetModsPhysicalDir(id));
        EnsureJunctionDir(dir, "saves", GetSavesPhysicalDir(id), id);
        EnsureJunctionDir(dir, "mods", GetModsPhysicalDir(id), id);

        var inst = new GameInstance
        {
            Id = id,
            Name = name,
            VersionId = versionId,
            JavaMajorVersion = javaMajor,
            JavaPath = ResolveBestJavaPath(javaMajor),
            CreatedAt = DateTime.Now
        };
        SaveInstance(inst);
        Instances.Add(inst);
        InstancesChanged?.Invoke();
        return inst;
    }

    /// <summary>环境隔离:优先 runtimes 公共池(严格 AppPaths.Runtimes),其次本机扫描结果</summary>
    private string ResolveBestJavaPath(int javaMajor)
    {
        var rt = javaMajor > 0 ? _javaRuntimeService.FindReadyRuntime(javaMajor) : null;
        if (rt != null)
        {
            App.WriteAppLog($"[游戏版本] 创建时指派 runtimes Java:{rt.JavaExe}");
            return rt.JavaExe;
        }
        string? scanned = _javaScanService.GetBestJava(javaMajor)?.Path;
        if (!string.IsNullOrEmpty(scanned))
            App.WriteAppLog($"[游戏版本] 创建时指派本机扫描 Java:{scanned}");
        else if (javaMajor > 0)
            App.WriteAppLog($"[游戏版本] 创建时未找到 Java {javaMajor},可前往下载中心下载对应 JDK");
        return scanned ?? "";
    }

    /// <summary>实例元信息原子落盘(tmp → Flush → Move,防断电半写);返回是否成功(供加载器安装回滚用)</summary>
    public bool SaveInstance(GameInstance inst)
    {
        lock (_saveLock)
        {
            try
            {
            string metaPath = Path.Combine(GetInstanceDir(inst.Id), "instance.json");
            string tmp = metaPath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, inst, JsonOpts);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, metaPath, overwrite: true);
            return true;
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[游戏版本] 实例元信息保存失败 {inst.Id}:{ex.Message}");
                return false;
            }
        }
    }

    public string GetInstanceDir(string instanceId) =>
        Path.Combine(InstancesDir, instanceId);

    /// <summary>游戏工作目录(--gameDirectory):实例目录本身即游戏根目录</summary>
    public string GetMinecraftDir(string instanceId) =>
        Path.Combine(InstancesDir, instanceId);

    /// <summary>模组目录:物理位于规范 mods\{实例id}(实例目录内经联接透传)</summary>
    public string GetModsDir(string instanceId) => GetModsPhysicalDir(instanceId);

    public string GetResourcePacksDir(string instanceId) =>
        Path.Combine(GetInstanceDir(instanceId), "resourcepacks");

    public string GetShaderPacksDir(string instanceId) =>
        Path.Combine(GetInstanceDir(instanceId), "shaderpacks");

    /// <summary>存档目录:物理位于规范 saves\{实例id}(实例目录内经联接透传)</summary>
    public string GetSavesDir(string instanceId) => GetSavesPhysicalDir(instanceId);

    public void RenameInstance(string instanceId, string newName)
    {
        var inst = Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null) return;
        inst.Name = newName;
        SaveInstance(inst);
        InstancesChanged?.Invoke();
    }

    public void DeleteInstance(string instanceId)
    {
        string dir = GetInstanceDir(instanceId);
        if (Directory.Exists(dir))
        {
            // 先移除联接,防止递归删除误入规范 saves/mods 物理目录
            JunctionHelper.DeleteJunctionOnly(Path.Combine(dir, "saves"));
            JunctionHelper.DeleteJunctionOnly(Path.Combine(dir, "mods"));
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { App.WriteAppLog($"[游戏版本] 删除实例目录失败 {dir}:{ex.Message}"); }
        }
        // 同步清理规范目录下该实例的模组物理目录
        // ⚠ saves\{id} 存档目录硬性保留,绝不删除(白名单断言兜底,见 TryDeleteDirTree)
        TryDeleteDirTree(GetModsPhysicalDir(instanceId));
        Instances.RemoveAll(i => i.Id == instanceId);
        InstancesChanged?.Invoke();
    }

    /// <summary>saves 硬性防护:判断路径是否位于规范存档目录 AppPaths.Saves 之内</summary>
    private static bool IsPathUnderSaves(string dir)
    {
        try
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\', '/');
            string savesRoot = Path.GetFullPath(AppPaths.Saves).TrimEnd('\\', '/');
            return full.StartsWith(savesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void TryDeleteDirTree(string dir)
    {
        // 路径白名单断言:任何以 saves 目录开头的待删路径一律拒绝并记日志
        if (IsPathUnderSaves(dir))
        {
            App.WriteAppLog($"[安全防护] ✗ 拒绝删除存档相关路径:{dir}(saves 硬性保护已生效)");
            return;
        }
        try { if (Directory.Exists(dir) && !JunctionHelper.IsJunction(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { App.WriteAppLog($"[游戏版本] 删除目录失败 {dir}:{ex.Message}"); }
    }

    /// <summary>
    /// 安全卸载指定游戏版本(【管理游戏版本】页专用,后台异步执行不阻塞 UI)。
    /// 删除范围(严格限定):
    ///   1. instances\{id} 实例目录本体(版本配置文件 instance.json / options.txt / config 等)
    ///   2. mods\{id} 该版本对应的模组物理目录
    ///   3. versions\{VersionId} 版本本体 —— 仅当没有其他实例引用同一版本时才删(全实例共享目录)
    /// ⚠ 硬性约束:
    ///   - 严禁删除 saves\{id} 存档目录(先拆联接再删实例目录,防止递归误入)
    ///   - 绝不触碰 runtimes\ 下的 Java 运行时(Java 卸载只允许在 Java 管理页执行)
    /// 返回 (Ok, 错误信息)。
    /// </summary>
    public async Task<(bool Ok, string Error)> UninstallInstanceAsync(string instanceId)
    {
        return await Task.Run(() =>
        {
            var inst = Instances.FirstOrDefault(i => i.Id == instanceId);
            if (inst == null) return (false, "游戏版本不存在");
            try
            {
                string versionId = inst.VersionId ?? "";
                string loaderVersionId = inst.LoaderVersionId ?? "";
                // 先判断版本是否被其它实例共享(必须在移除列表前判断;加载器版本一并纳入)
                bool sharedByOthers = Instances.Any(i => i.Id != instanceId && i.VersionId == versionId);
                bool loaderSharedByOthers = !string.IsNullOrEmpty(loaderVersionId) &&
                    Instances.Any(i => i.Id != instanceId &&
                        (i.VersionId == loaderVersionId || i.LoaderVersionId == loaderVersionId));

                // 1) 实例目录本体(含版本配置文件):先拆 saves/mods 联接,防止递归删除误入物理目录
                string dir = GetInstanceDir(instanceId);
                if (Directory.Exists(dir))
                {
                    JunctionHelper.DeleteJunctionOnly(Path.Combine(dir, "saves"));
                    JunctionHelper.DeleteJunctionOnly(Path.Combine(dir, "mods"));
                    Directory.Delete(dir, recursive: true);
                }

                // 2) 该版本对应的模组物理目录(严禁触碰 saves\{id},存档完整保留;
                //    TryDeleteDirTree 内置 saves 白名单断言双保险)
                TryDeleteDirTree(GetModsPhysicalDir(instanceId));

                // 3) 版本本体:共享资源,仅无其它实例引用时删除(加载器版本目录同理)
                if (!string.IsNullOrEmpty(versionId) && !sharedByOthers)
                    TryDeleteDirTree(Path.Combine(AppPaths.Versions, versionId));
                if (!string.IsNullOrEmpty(loaderVersionId) && !loaderSharedByOthers)
                    TryDeleteDirTree(Path.Combine(AppPaths.Versions, loaderVersionId));

                Instances.RemoveAll(i => i.Id == instanceId);
                App.WriteAppLog($"[卸载] ✓ 游戏版本 {inst.Name}({instanceId})卸载完成" +
                    (sharedByOthers ? $";版本 {versionId} 被其它游戏版本引用,版本本体已保留" : $";版本本体 {versionId} 已删除") +
                    ";存档目录已完整保留");
                InstancesChanged?.Invoke();
                return (true, "");
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[卸载] ✗ 游戏版本 {instanceId} 卸载失败:{ex}");
                return (false, ex.Message);
            }
        });
    }

    /// <summary>复制实例(含全部文件)</summary>
    public GameInstance? DuplicateInstance(string instanceId, string newName)
    {
        var src = Instances.FirstOrDefault(i => i.Id == instanceId);
        if (src == null) return null;
        string newId = MakeUniqueId(SanitizeDirName(newName));
        string srcDir = GetInstanceDir(instanceId);
        string dstDir = GetInstanceDir(newId);
        CopyDirectory(srcDir, dstDir);

        // 存档/模组物理目录单独复制到新实例的规范目录,并重建联接
        Directory.CreateDirectory(GetSavesPhysicalDir(newId));
        Directory.CreateDirectory(GetModsPhysicalDir(newId));
        if (Directory.Exists(GetSavesPhysicalDir(instanceId)))
            CopyDirectory(GetSavesPhysicalDir(instanceId), GetSavesPhysicalDir(newId));
        if (Directory.Exists(GetModsPhysicalDir(instanceId)))
            CopyDirectory(GetModsPhysicalDir(instanceId), GetModsPhysicalDir(newId));
        EnsureJunctionDir(dstDir, "saves", GetSavesPhysicalDir(newId), newId);
        EnsureJunctionDir(dstDir, "mods", GetModsPhysicalDir(newId), newId);

        var copy = new GameInstance
        {
            Id = newId,
            Name = newName,
            VersionId = src.VersionId,
            // 加载器版本 JSON 的 id 必须一起带过来,否则克隆出来的实例启动时会退回原版
            LoaderVersionId = src.LoaderVersionId,
            ModLoader = src.ModLoader,
            ModLoaderVersion = src.ModLoaderVersion,
            JavaMajorVersion = src.JavaMajorVersion,
            JavaPath = src.JavaPath,
            Xms = src.Xms,
            Xmx = src.Xmx,
            UseCustomMemory = src.UseCustomMemory,
            // 整合包级建议内存一并继承,大型整合包克隆后不用重新设
            RecommendedMemoryMb = src.RecommendedMemoryMb,
            ExtraJvmArgs = src.ExtraJvmArgs,
            Width = src.Width,
            Height = src.Height,
            Fullscreen = src.Fullscreen,
            CreatedAt = DateTime.Now
        };
        SaveInstance(copy);
        Instances.Add(copy);
        InstancesChanged?.Invoke();
        return copy;
    }

    /// <summary>导出实例为 zip 备份包(恒走托管打包:原生 Shell COM 复制为异步返回,有漏文件风险)</summary>
    public bool ExportInstance(string instanceId, string outputZipPath)
    {
        string srcDir = GetInstanceDir(instanceId);
        if (!Directory.Exists(srcDir)) return false;
        try
        {
            return _nativeInterop.CreateZipManaged(srcDir, outputZipPath);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[游戏版本] 导出 zip 失败 {instanceId}:{ex.Message}");
            return false;
        }
    }

    /// <summary>导入已有的 .minecraft 目录作为新实例</summary>
    public GameInstance? ImportExistingMinecraft(string minecraftDir, string instanceName)
    {
        if (!Directory.Exists(minecraftDir)) return null;

        // 版本探测前置:源目录没有版本本体时直接拒绝导入,避免大体积复制后只得到空壳
        string versionId = "";
        string importedVersionsDir = Path.Combine(minecraftDir, "versions");
        if (Directory.Exists(importedVersionsDir))
        {
            var firstVer = Directory.GetDirectories(importedVersionsDir).FirstOrDefault();
            if (firstVer != null) versionId = Path.GetFileName(firstVer);
        }
        if (string.IsNullOrEmpty(versionId))
        {
            App.WriteAppLog($"[游戏版本] 导入取消:{minecraftDir} 的 versions 目录下未找到游戏版本本体");
            return null;
        }

        string id = MakeUniqueId(SanitizeDirName(instanceName));
        string instDir = GetInstanceDir(id);
        Directory.CreateDirectory(instDir);

        // 共享资源上提到全局规范目录(第三方启动器目录复用:libraries/assets 为内容寻址海量文件,
        // 已存在同尺寸文件直接跳过,避免把已有几个 GB 重复拷贝一遍;versions 体积小仍以源为准覆盖)
        foreach (var shared in new[] { ("versions", AppPaths.Versions),
                                       ("libraries", AppPaths.Libraries),
                                       ("assets", AppPaths.Assets) })
        {
            string src = Path.Combine(minecraftDir, shared.Item1);
            if (!Directory.Exists(src)) continue;
            CopyDirectory(src, shared.Item2, skipExisting: shared.Item1 != "versions");
        }
        // 存档 → saves\{id}
        string srcSaves = Path.Combine(minecraftDir, "saves");
        if (Directory.Exists(srcSaves)) CopyDirectory(srcSaves, GetSavesPhysicalDir(id));
        else Directory.CreateDirectory(GetSavesPhysicalDir(id));
        // mods → mods\{id}
        string srcMods = Path.Combine(minecraftDir, "mods");
        if (Directory.Exists(srcMods)) CopyDirectory(srcMods, GetModsPhysicalDir(id));
        else Directory.CreateDirectory(GetModsPhysicalDir(id));

        // 其余内容(config/options/resourcepacks 等)复制到实例目录
        foreach (var entry in Directory.EnumerateFileSystemEntries(minecraftDir))
        {
            string name = Path.GetFileName(entry);
            if (new[] { "versions", "libraries", "assets", "saves", "mods" }
                    .Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            string dst = Path.Combine(instDir, name);
            try
            {
                if (Directory.Exists(entry)) CopyDirectory(entry, dst);
                else if (!File.Exists(dst)) File.Copy(entry, dst, overwrite: true);
            }
            catch (Exception ex) { App.WriteAppLog($"[游戏版本] 导入复制 {name} 失败:{ex.Message}"); }
        }

        // 标准子目录 + 联接
        Directory.CreateDirectory(Path.Combine(instDir, "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(instDir, "shaderpacks"));
        EnsureJunctionDir(instDir, "saves", GetSavesPhysicalDir(id), id);
        EnsureJunctionDir(instDir, "mods", GetModsPhysicalDir(id), id);

        var inst = new GameInstance
        {
            Id = id,
            Name = instanceName,
            VersionId = versionId,
            JavaMajorVersion = JavaRuntimeService.RecommendJavaMajor(versionId),
            CreatedAt = DateTime.Now
        };
        SaveInstance(inst);
        Instances.Add(inst);
        InstancesChanged?.Invoke();
        return inst;
    }

    private void CopyDirectory(string src, string dst, bool skipExisting = false)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            string target = Path.Combine(dst, Path.GetFileName(file));
            try
            {
                // 目标已有同尺寸文件即视为同一份(内容寻址资源),免重复拷贝
                if (skipExisting && IsSameSizeFile(file, target)) continue;
                File.Copy(file, target, overwrite: true);
            }
            catch (Exception ex) { App.WriteAppLog($"[游戏版本] 复制文件失败 {file}:{ex.Message}"); }
        }
        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            // 联接目录不递归(避免重复拷贝物理目录内容)
            if (JunctionHelper.IsJunction(dir)) continue;
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)), skipExisting);
        }
    }

    /// <summary>目标文件已存在且大小与源一致 → 视为同一份,免重复拷贝
    /// (复用外部启动器目录时避免重拷 libraries/assets 数 GB)</summary>
    private static bool IsSameSizeFile(string src, string dst)
    {
        try
        {
            var fi = new FileInfo(dst);
            return fi.Exists && fi.Length == new FileInfo(src).Length;
        }
        catch { return false; }
    }
}
