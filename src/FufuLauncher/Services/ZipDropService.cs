// Copyright © FufuLauncher
//
// 拖拽文件自动识别服务(LauncherX-3):
// 把 zip / jar 拖到启动器窗口,程序自动分辨它到底是模组、资源包、光影包还是整合包,
// 并按类型走不同的处理流程 —— 整合包会引导新建实例完成导入,其余直接装进当前实例对应目录。
//
// 实现要点:
// 1. 判定完全基于压缩包内部条目结构,不看文件名猜(网上下载的包名字千奇百怪);
// 2. 读压缩包只读中央目录(前 400 个条目),不解压任何数据,大文件也是毫秒级返回;
// 3. 整合包格式判定复用 ModPackImportService.DetectFormat,不自造一套格式表;
// 4. 单个文件判定失败只影响它自己,批量拖入时其余文件照常处理;
// 5. 模组安装走 ModManagerService.ImportModFile(已有的元数据校验 + 版本兼容检查),不重复造轮子。

using System.IO;
using System.IO.Compression;

namespace FufuLauncher.Services;

/// <summary>拖入内容的类型</summary>
public enum DropKind
{
    /// <summary>认不出来</summary>
    Unknown,
    /// <summary>单个模组(.jar 或含模组元数据的 .zip)</summary>
    Mod,
    /// <summary>一次拖进来一堆模组</summary>
    MultiMod,
    /// <summary>资源包(含 pack.mcmeta)</summary>
    ResourcePack,
    /// <summary>光影包(含 shaders/ 目录或 shaders.properties)</summary>
    ShaderPack,
    /// <summary>整合包(manifest.json / modrinth.index.json / fufu-pack.json 等)</summary>
    ModPack,
    /// <summary>存档压缩包(含 level.dat)</summary>
    SaveGame,
    /// <summary>不支持的文件类型</summary>
    Unsupported
}

/// <summary>单个拖入文件的识别结果</summary>
public sealed class DropAnalysis
{
    public string Path { get; set; } = "";
    public DropKind Kind { get; set; } = DropKind.Unknown;
    /// <summary>判定依据(UI 展示"为什么认成这个")</summary>
    public string Reason { get; set; } = "";
    /// <summary>整合包格式(仅 Kind == ModPack 有意义)</summary>
    public ModPackFormat PackFormat { get; set; } = ModPackFormat.Unknown;
    /// <summary>识别出的模组文件列表(MultiMod 时是多条,Mod 时是一条)</summary>
    public List<string> ModFiles { get; set; } = new();
    /// <summary>识别出的资源包文件列表</summary>
    public List<string> ResourcePacks { get; set; } = new();
    /// <summary>识别出的光影包文件列表</summary>
    public List<string> ShaderPacks { get; set; } = new();
    /// <summary>建议的实例名(整合包用)</summary>
    public string SuggestedName { get; set; } = "";
    /// <summary>是否需要走"新建实例"流程</summary>
    public bool NeedNewInstance => Kind == DropKind.ModPack;
    public string FileName => System.IO.Path.GetFileName(Path);
    public long Size { get; set; }
    public string SizeDisplay => StorageGuardService.FmtSize(Size);
    public string KindDisplay => Kind switch
    {
        DropKind.Mod => "模组",
        DropKind.MultiMod => "多个模组",
        DropKind.ResourcePack => "资源包",
        DropKind.ShaderPack => "光影包",
        DropKind.ModPack => "整合包",
        DropKind.SaveGame => "存档",
        DropKind.Unsupported => "不支持",
        _ => "未知"
    };
    public string Display => $"{FileName} · {KindDisplay} · {SizeDisplay}" +
                             (string.IsNullOrEmpty(Reason) ? "" : $"({Reason})");
}

/// <summary>批量安装结果</summary>
public sealed class DropInstallResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<string> Installed { get; set; } = new();
    public List<string> Failed { get; set; } = new();
}

public sealed class ZipDropService
{
    /// <summary>识别时最多读取的压缩包条目数(只读中央目录,不解压)</summary>
    private const int MaxEntriesToScan = 400;

    private static readonly string[] SupportedExts =
        { ".zip", ".jar", ".mrpack", ".disabled", ".7z" };

    private readonly ModPackImportService _importer;
    private readonly InstanceService _instances;
    private readonly ModManagerService _mods;
    private readonly StorageGuardService _storage;

    public ZipDropService(ModPackImportService importer, InstanceService instances,
                          ModManagerService mods, StorageGuardService storage)
    {
        _importer = importer;
        _instances = instances;
        _mods = mods;
        _storage = storage;
    }

    /// <summary>这个扩展名本服务认不认</summary>
    public static bool IsSupportedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Directory.Exists(path)) return true;   // 拖文件夹进来也支持(整合包解压后的目录)
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return SupportedExts.Contains(ext);
    }

    // ==================== 识别 ====================

    /// <summary>批量识别(拖进来一堆文件时逐个判)</summary>
    public List<DropAnalysis> AnalyzeAll(IEnumerable<string> paths)
    {
        var list = new List<DropAnalysis>();
        foreach (string p in paths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            list.Add(Analyze(p));
        }
        // 整合包优先展示(它要走建实例流程,信息量最大)
        return list.OrderByDescending(a => a.Kind == DropKind.ModPack)
                   .ThenBy(a => a.Kind == DropKind.Unknown)
                   .ToList();
    }

    /// <summary>识别单个文件/文件夹</summary>
    public DropAnalysis Analyze(string path)
    {
        var res = new DropAnalysis { Path = path };
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) && !Directory.Exists(path))
            {
                res.Kind = DropKind.Unsupported;
                res.Reason = "文件不存在";
                return res;
            }

            // 拖进来的是文件夹:交给整合包目录导入判定
            if (Directory.Exists(path))
            {
                res.Size = 0;
                bool hasManifest = File.Exists(System.IO.Path.Combine(path, "manifest.json"))
                                || File.Exists(System.IO.Path.Combine(path, "modrinth.index.json"))
                                || File.Exists(System.IO.Path.Combine(path, "fufu-pack.json"));
                if (hasManifest)
                {
                    res.Kind = DropKind.ModPack;
                    res.PackFormat = ModPackFormat.CurseForge;
                    res.Reason = "目录里有整合包清单文件";
                    res.SuggestedName = new DirectoryInfo(path).Name;
                }
                else
                {
                    res.Kind = DropKind.Unknown;
                    res.Reason = "文件夹里没有整合包清单";
                }
                return res;
            }

            res.Size = SafeLen(path);
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

            // .jar / .disabled 直接按模组处理(jar 内部结构下面还会再确认一次)
            if (ext == ".jar" || ext == ".disabled")
            {
                res.Kind = DropKind.Mod;
                res.ModFiles.Add(path);
                res.Reason = ext == ".jar" ? "模组 jar 文件" : "被禁用的模组文件";
                return res;
            }

            // .mrpack 是 Modrinth 整合包的固定后缀
            if (ext == ".mrpack")
            {
                res.Kind = DropKind.ModPack;
                res.PackFormat = ModPackFormat.Modrinth;
                res.Reason = "Modrinth 整合包(.mrpack)";
                res.SuggestedName = NameFromFile(path);
                return res;
            }

            if (!SupportedExts.Contains(ext))
            {
                res.Kind = DropKind.Unsupported;
                res.Reason = $"不支持的格式({ext})";
                return res;
            }

            // ---------- zip 内部结构判定 ----------
            List<string> entries;
            try
            {
                using var zip = ZipFile.OpenRead(path);
                entries = zip.Entries.Take(MaxEntriesToScan).Select(e => e.FullName.Replace('\\', '/')).ToList();
            }
            catch (InvalidDataException)
            {
                res.Kind = DropKind.Unsupported;
                res.Reason = "压缩包已损坏,打不开";
                return res;
            }
            catch (Exception ex)
            {
                res.Kind = DropKind.Unsupported;
                res.Reason = "无法读取:" + ex.Message;
                return res;
            }

            if (entries.Count == 0)
            {
                res.Kind = DropKind.Unsupported;
                res.Reason = "压缩包是空的";
                return res;
            }

            // 整合包清单文件优先级最高(CurseForge / Modrinth / 自家包)
            if (entries.Any(e => BaseName(e) is "manifest.json" or "modrinth.index.json"
                                                or "minecraftinstance.json" or "fufu-pack.json"))
            {
                res.Kind = DropKind.ModPack;
                res.PackFormat = _importer.DetectFormat(path);
                res.Reason = "含整合包清单文件";
                res.SuggestedName = NameFromFile(path);
                return res;
            }

            // 资源包 / 光影包:pack.mcmeta 与 shaders 目录
            bool hasPackMcmeta = entries.Any(e => BaseName(e) == "pack.mcmeta");
            bool hasShaders = entries.Any(e => e.Contains("shaders/", StringComparison.OrdinalIgnoreCase))
                           || entries.Any(e => BaseName(e) == "shaders.properties");
            if (hasShaders)
            {
                res.Kind = DropKind.ShaderPack;
                res.ShaderPacks.Add(path);
                res.Reason = "含 shaders 目录,是光影包";
                return res;
            }
            if (hasPackMcmeta)
            {
                res.Kind = DropKind.ResourcePack;
                res.ResourcePacks.Add(path);
                res.Reason = "含 pack.mcmeta,是资源包";
                return res;
            }

            // 存档压缩包
            if (entries.Any(e => BaseName(e) == "level.dat"))
            {
                res.Kind = DropKind.SaveGame;
                res.Reason = "含 level.dat,是存档压缩包";
                return res;
            }

            // 模组元数据(jar 改后缀成 zip 的情况很常见)
            if (entries.Any(e => BaseName(e) is "fabric.mod.json" or "quilt.mod.json" or "mcmod.info")
             || entries.Any(e => e.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase)
                              || e.Equals("META-INF/neoforge.mods.toml", StringComparison.OrdinalIgnoreCase)))
            {
                res.Kind = DropKind.Mod;
                res.ModFiles.Add(path);
                res.Reason = "含模组元数据文件";
                return res;
            }

            // 一堆散装 jar → 多模组
            var jars = entries.Where(e => e.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).ToList();
            if (jars.Count > 0)
            {
                // 压缩包里的 jar 没法直接装,需要用户先解压;这里如实告知
                res.Kind = DropKind.MultiMod;
                res.Reason = $"压缩包里有 {jars.Count} 个 jar,需要先解压出来再拖进来";
                return res;
            }

            // 全是图片/音频,大概率是没带 pack.mcmeta 的散装资源包
            int assetHits = entries.Count(e =>
            {
                string n = BaseName(e).ToLowerInvariant();
                return n.EndsWith(".png") || n.EndsWith(".ogg") || n.EndsWith(".json") || n.EndsWith(".mcmeta");
            });
            if (assetHits * 2 >= entries.Count)
            {
                res.Kind = DropKind.ResourcePack;
                res.ResourcePacks.Add(path);
                res.Reason = "内容是贴图/音频,按资源包处理(没有 pack.mcmeta,进游戏后可能不显示)";
                return res;
            }

            res.Kind = DropKind.Unknown;
            res.Reason = "认不出这是什么,压缩包里没有已知的结构特征";
            return res;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[拖拽识别] ✗ {path}:{ex.Message}");
            res.Kind = DropKind.Unknown;
            res.Reason = "识别出错:" + ex.Message;
            return res;
        }
    }

    // ==================== 安装 ====================

    /// <summary>把识别出的模组装进指定实例</summary>
    public DropInstallResult InstallMods(IEnumerable<string> files, string instanceId)
    {
        var res = new DropInstallResult();
        if (string.IsNullOrEmpty(instanceId))
        {
            res.Message = "请先选择一个游戏版本,再把模组拖进来。";
            return res;
        }
        _mods.SetCurrentInstance(instanceId);
        foreach (string f in files ?? Enumerable.Empty<string>())
        {
            var (ok, err) = _mods.ImportModFile(f);
            if (ok) res.Installed.Add(System.IO.Path.GetFileName(f));
            else res.Failed.Add($"{System.IO.Path.GetFileName(f)}:{err}");
        }
        res.Ok = res.Failed.Count == 0 && res.Installed.Count > 0;
        res.Message = BuildMessage("模组", res);
        App.WriteAppLog($"[拖拽识别] 模组导入 → {instanceId}:成功 {res.Installed.Count},失败 {res.Failed.Count}");
        return res;
    }

    /// <summary>把资源包装进指定实例的 resourcepacks 目录</summary>
    public DropInstallResult InstallResourcePacks(IEnumerable<string> files, string instanceId)
        => CopyInto(files, instanceId, PackTarget.ResourcePack);

    /// <summary>把光影包装进指定实例的 shaderpacks 目录</summary>
    public DropInstallResult InstallShaderPacks(IEnumerable<string> files, string instanceId)
        => CopyInto(files, instanceId, PackTarget.ShaderPack);

    private enum PackTarget { ResourcePack, ShaderPack }

    private DropInstallResult CopyInto(IEnumerable<string> files, string instanceId, PackTarget target)
    {
        var res = new DropInstallResult();
        string kindName = target == PackTarget.ResourcePack ? "资源包" : "光影包";
        if (string.IsNullOrEmpty(instanceId))
        {
            res.Message = $"请先选择一个游戏版本,再把{kindName}拖进来。";
            return res;
        }

        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null)
        {
            res.Message = "找不到这个游戏版本,请刷新列表后重试。";
            return res;
        }

        string dir = target == PackTarget.ResourcePack
            ? _instances.GetResourcePacksDir(instanceId)
            : _instances.GetShaderPacksDir(instanceId);

        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            res.Message = $"创建{kindName}目录失败:{ex.Message}";
            return res;
        }

        foreach (string f in files ?? Enumerable.Empty<string>())
        {
            try
            {
                if (!File.Exists(f)) { res.Failed.Add($"{System.IO.Path.GetFileName(f)}:文件不存在"); continue; }
                long len = SafeLen(f);
                var (chk, chkMsg) = _storage.Precheck(dir, len);
                if (chk != StorageCheckResult.Ok) { res.Failed.Add($"{System.IO.Path.GetFileName(f)}:{chkMsg}"); continue; }

                string target_ = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(f));
                string tmp = target_ + ".tmp";
                File.Copy(f, tmp, overwrite: true);
                File.Move(tmp, target_, overwrite: true);
                res.Installed.Add(System.IO.Path.GetFileName(f));
            }
            catch (Exception ex)
            {
                res.Failed.Add($"{System.IO.Path.GetFileName(f)}:{StorageGuardService.ClassifyIoError(ex, "复制" + kindName)}");
            }
        }

        res.Ok = res.Failed.Count == 0 && res.Installed.Count > 0;
        res.Message = BuildMessage(kindName, res) +
                      (res.Installed.Count > 0 ? $"\n已放进{dir}" : "");
        App.WriteAppLog($"[拖拽识别] {kindName}导入 → {instanceId}:成功 {res.Installed.Count},失败 {res.Failed.Count}");
        return res;
    }

    /// <summary>整合包导入(引导新建实例),直接转调已有的导入服务</summary>
    public Task<ModPackImportResult> ImportModPackAsync(string packPath, string? customName,
        Action<string>? onProgress = null)
        => _importer.ImportAsync(packPath, customName, onProgress);

    /// <summary>整合包目录导入(拖进来的是解压后的文件夹)</summary>
    public Task<DirImportResult> ImportModPackDirAsync(string dirPath, string? customName,
        Action<string>? onProgress = null)
        => _importer.ImportDirAsync(dirPath, customName, onProgress);

    // ==================== 辅助 ====================

    private static string BuildMessage(string kind, DropInstallResult res)
    {
        if (res.Installed.Count == 0 && res.Failed.Count == 0) return $"没有可导入的{kind}。";
        string msg = $"{kind}导入完成:成功 {res.Installed.Count} 个" +
                     (res.Failed.Count > 0 ? $",失败 {res.Failed.Count} 个" : "") + "。";
        if (res.Failed.Count > 0)
            msg += "\n失败明细:\n" + string.Join("\n", res.Failed.Take(8));
        return msg;
    }

    private static string BaseName(string entry)
    {
        int i = entry.LastIndexOf('/');
        return i >= 0 ? entry[(i + 1)..] : entry;
    }

    private static string NameFromFile(string path)
    {
        string n = System.IO.Path.GetFileNameWithoutExtension(path);
        // 常见的 "包名-1.20.1-fabric-1.0" 只取第一段做实例名,后面版本信息交给导入流程解析
        int cut = n.IndexOfAny(new[] { '-', '_', '[' });
        return cut > 2 ? n[..cut].Trim() : n.Trim();
    }

    private static long SafeLen(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }
}
