// InstanceService.cs — 游戏实例管理服务
// 可爱的芙芙 - 阶段2 模块
//
// 多隔离实例系统:每个实例一个独立游戏工作目录。
// 目录规范(严格固化在 APP\mcGAME 下):
//   instances\{InstanceId}\      游戏工作目录(--gameDirectory)
//     ├── saves → 联接 → saves\{InstanceId}   存档(物理存放在规范 saves 目录)
//     ├── mods  → 联接 → mods\{InstanceId}    模组(物理存放在规范 mods 目录)
//     ├── resourcepacks\         资源包
//     ├── shaderpacks\           光影包
//     ├── options.txt            游戏配置
//     └── instance.json          实例元信息
//   游戏本体/依赖/资源全实例共享,统一存放于 versions\、libraries\、assets\。
//
// 支持:重命名、复制、删除、导出 zip 备份、导入已有 .minecraft
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
    public string? ModLoader { get; set; }              // Forge / Fabric / Quilt / null
    public string? ModLoaderVersion { get; set; }
    public int JavaMajorVersion { get; set; } = 17;
    public string JavaPath { get; set; } = "";
    public int Xms { get; set; } = 1024;
    public int Xmx { get; set; } = 4096;
    public string ExtraJvmArgs { get; set; } = "";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public bool Fullscreen { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastPlayedAt { get; set; }
    public long TotalPlayTimeSeconds { get; set; }

    /// <summary>列表控件(ComboBox/ListBox)默认显示文本,避免输出全命名空间类名</summary>
    public override string ToString() =>
        string.IsNullOrEmpty(Name) ? (string.IsNullOrEmpty(Id) ? "游戏版本" : Id) : Name;
}

public class InstanceService
{
    // 实例目录固化在 APP\mcGAME\instances(避免 static readonly 在目录就绪前初始化)
    private static string InstancesDir => AppPaths.Instances;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly NativeInteropService _nativeInterop;
    private readonly JavaScanService _javaScanService;

    public List<GameInstance> Instances { get; } = new();

    public InstanceService(NativeInteropService nativeInterop, JavaScanService javaScanService)
    {
        _nativeInterop = nativeInterop;
        _javaScanService = javaScanService;
    }

    public void LoadInstances()
    {
        Instances.Clear();
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
                    Instances.Add(inst);
                }
            }
            catch { /* 忽略损坏实例 */ }
        }
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
                    catch (Exception ex) { App.WriteAppLog($"[实例] 迁移 .minecraft/{name} 失败:{ex.Message}"); }
                }
                TryDeleteEmptyDir(mcOld);
                App.WriteAppLog($"[实例] {instanceId} 旧 .minecraft 结构已迁移到新规范");
            }

            // ---- 2. saves/mods:实体目录迁入规范位置,再建联接 ----
            EnsureJunctionDir(instDir, "saves", GetSavesPhysicalDir(instanceId), instanceId);
            EnsureJunctionDir(instDir, "mods", GetModsPhysicalDir(instanceId), instanceId);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[实例] 布局迁移异常 {instanceId}:{ex.Message}");
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
            App.WriteAppLog($"[实例] {instanceId} 联接创建失败,{name} 回退为实例内实体目录");
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
        catch (Exception ex) { App.WriteAppLog($"[实例] MoveMerge 失败 {src} → {dst}:{ex.Message}"); }
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

    public GameInstance CreateInstance(string name, string versionId, int javaMajor)
    {
        string id = $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}";
        string dir = GetInstanceDir(id);
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
            JavaPath = _javaScanService.GetBestJava(javaMajor)?.Path ?? "",
            CreatedAt = DateTime.Now
        };
        SaveInstance(inst);
        Instances.Add(inst);
        return inst;
    }

    public void SaveInstance(GameInstance inst)
    {
        string metaPath = Path.Combine(GetInstanceDir(inst.Id), "instance.json");
        var json = JsonSerializer.Serialize(inst, JsonOpts);
        File.WriteAllText(metaPath, json);
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
    }

    public void DeleteInstance(string instanceId)
    {
        string dir = GetInstanceDir(instanceId);
        if (Directory.Exists(dir))
        {
            // 先移除联接,防止递归删除误入规范 saves/mods 物理目录
            JunctionHelper.DeleteJunctionOnly(Path.Combine(dir, "saves"));
            JunctionHelper.DeleteJunctionOnly(Path.Combine(dir, "mods"));
            Directory.Delete(dir, recursive: true);
        }
        // 同步清理规范目录下该实例的存档/模组物理目录
        TryDeleteDirTree(GetSavesPhysicalDir(instanceId));
        TryDeleteDirTree(GetModsPhysicalDir(instanceId));
        Instances.RemoveAll(i => i.Id == instanceId);
    }

    private static void TryDeleteDirTree(string dir)
    {
        try { if (Directory.Exists(dir) && !JunctionHelper.IsJunction(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { App.WriteAppLog($"[实例] 删除目录失败 {dir}:{ex.Message}"); }
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
                // 先判断版本是否被其它实例共享(必须在移除列表前判断)
                bool sharedByOthers = Instances.Any(i => i.Id != instanceId && i.VersionId == versionId);

                // 1) 实例目录本体(含版本配置文件):先拆 saves/mods 联接,防止递归删除误入物理目录
                string dir = GetInstanceDir(instanceId);
                if (Directory.Exists(dir))
                {
                    JunctionHelper.DeleteJunctionOnly(Path.Combine(dir, "saves"));
                    JunctionHelper.DeleteJunctionOnly(Path.Combine(dir, "mods"));
                    Directory.Delete(dir, recursive: true);
                }

                // 2) 该版本对应的模组物理目录(严禁触碰 saves\{id},存档完整保留)
                TryDeleteDirTree(GetModsPhysicalDir(instanceId));

                // 3) 版本本体:共享资源,仅无其它实例引用时删除
                if (!string.IsNullOrEmpty(versionId) && !sharedByOthers)
                    TryDeleteDirTree(Path.Combine(AppPaths.Versions, versionId));

                Instances.RemoveAll(i => i.Id == instanceId);
                App.WriteAppLog($"[卸载] ✓ 实例 {inst.Name}({instanceId})卸载完成" +
                    (sharedByOthers ? $";版本 {versionId} 被其它实例引用,版本本体已保留" : $";版本本体 {versionId} 已删除") +
                    ";存档目录已完整保留");
                return (true, "");
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[卸载] ✗ 实例 {instanceId} 卸载失败:{ex}");
                return (false, ex.Message);
            }
        });
    }

    /// <summary>复制实例(含全部文件)</summary>
    public GameInstance? DuplicateInstance(string instanceId, string newName)
    {
        var src = Instances.FirstOrDefault(i => i.Id == instanceId);
        if (src == null) return null;
        string newId = $"{newName}_{DateTime.Now:yyyyMMdd_HHmmss}";
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
            ModLoader = src.ModLoader,
            ModLoaderVersion = src.ModLoaderVersion,
            JavaMajorVersion = src.JavaMajorVersion,
            JavaPath = src.JavaPath,
            Xms = src.Xms,
            Xmx = src.Xmx,
            ExtraJvmArgs = src.ExtraJvmArgs,
            Width = src.Width,
            Height = src.Height,
            Fullscreen = src.Fullscreen,
            CreatedAt = DateTime.Now
        };
        SaveInstance(copy);
        Instances.Add(copy);
        return copy;
    }

    /// <summary>导出实例为 zip 备份包</summary>
    public bool ExportInstance(string instanceId, string outputZipPath)
    {
        string srcDir = GetInstanceDir(instanceId);
        if (!Directory.Exists(srcDir)) return false;
        return _nativeInterop.CreateZip(srcDir, outputZipPath);
    }

    /// <summary>导入已有的 .minecraft 目录作为新实例</summary>
    public GameInstance? ImportExistingMinecraft(string minecraftDir, string instanceName)
    {
        if (!Directory.Exists(minecraftDir)) return null;
        string id = $"{instanceName}_{DateTime.Now:yyyyMMdd_HHmmss}";
        string instDir = GetInstanceDir(id);
        Directory.CreateDirectory(instDir);

        // 共享资源上提到全局规范目录
        foreach (var shared in new[] { ("versions", AppPaths.Versions),
                                       ("libraries", AppPaths.Libraries),
                                       ("assets", AppPaths.Assets) })
        {
            string src = Path.Combine(minecraftDir, shared.Item1);
            if (Directory.Exists(src)) CopyDirectory(src, shared.Item2);
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
            if (Directory.Exists(entry)) CopyDirectory(entry, dst);
            else if (!File.Exists(dst)) File.Copy(entry, dst, overwrite: true);
        }

        // 标准子目录 + 联接
        Directory.CreateDirectory(Path.Combine(instDir, "resourcepacks"));
        Directory.CreateDirectory(Path.Combine(instDir, "shaderpacks"));
        EnsureJunctionDir(instDir, "saves", GetSavesPhysicalDir(id), id);
        EnsureJunctionDir(instDir, "mods", GetModsPhysicalDir(id), id);

        // 尝试识别版本号(以导入源 .minecraft\versions 第一个子目录为准)
        string versionId = "";
        string importedVersionsDir = Path.Combine(minecraftDir, "versions");
        if (Directory.Exists(importedVersionsDir))
        {
            var firstVer = Directory.GetDirectories(importedVersionsDir).FirstOrDefault();
            if (firstVer != null) versionId = Path.GetFileName(firstVer);
        }

        var inst = new GameInstance
        {
            Id = id,
            Name = instanceName,
            VersionId = versionId,
            JavaMajorVersion = 17,
            CreatedAt = DateTime.Now
        };
        SaveInstance(inst);
        Instances.Add(inst);
        return inst;
    }

    private void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            // 联接目录不递归(避免重复拷贝物理目录内容)
            if (JunctionHelper.IsJunction(dir)) continue;
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
    }
}
