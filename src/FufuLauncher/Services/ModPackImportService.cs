// ModPackImportService.cs — 多格式整合包导入服务(重构版)
// FufuLauncher - 模组市场模块(重构要求 ⑤-4)
//
// 支持格式:
// 1. Modrinth 整合包(.mrpack)   → modrinth.index.json + overrides/,附带文件按索引在线补齐
// 2. CurseForge 整合包(.zip)    → manifest.json + overrides/,在线文件经官方 API 补齐(需 Key)
// 3. Prism/MultiMC 导出          → mmc-pack.json + .minecraft(容忍版本名套壳根,如 GTNH 官方包)
// 4. HMCL / Technic 整合包(.zip) → modpack.json + .minecraft 整目录(HMCL 字段 minecraft/forge/fabric;
//    Technic 用 minecraftVersion),版本/加载器从清单读,内容整目录解压即用 —— 2026-09-26 补全
// 5. 第三方启动器导出包          → 根目录游戏结构 + 可选版本配置,整目录导入
// 6. 懒人包 / 第三方自制包(全目录) → 整个 .minecraft / mods / config 结构直接解压即用,
//    版本隔离包 versions\1.12.2-forge-xxx 自动提取主版本号
//
// 结构设计(重做后):
// · zip 导入统一走「结构探测(全量中央目录 + 候选根)→ 暂存区解压 → 磁盘定位包根 → 搬运内容子树」,
//   不再依赖条目名顶层匹配,套壳根/条目排序/分隔符差异全部免疫;
// · 清单元数据(版本/加载器/包名)从探测到的清单条目读取,在线补齐从暂存区磁盘读清单;
// · 解压优先走 C++(FufuNative)字节级进度,DLL 缺失时托管实现等价兜底;
// · 导入前 StorageGuard 预检(暂存 + 拷贝 + 版本本体空间),失败绝不带隐患开工;
// · 任何阶段失败自动回滚:删除已建游戏版本目录与物理 mods/saves 目录,不留残骸;
// · 全部异常分类为中文友好提示,禁止把堆栈甩给玩家。

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

/// <summary>整合包格式枚举</summary>
public enum ModPackFormat
{
    Unknown,        // 无法识别
    Modrinth,       // .mrpack
    CurseForge,     // manifest.json 包
    Prism,          // mmc-pack.json 包
    LazyPack        // 懒人包 / 第三方启动器全目录包
}

/// <summary>整合包导入结果</summary>
public class ModPackImportResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public ModPackFormat Format { get; set; }
    public GameInstance? Instance { get; set; }
    /// <summary>解压落盘文件数</summary>
    public int ExtractedFiles { get; set; }
    /// <summary>mrpack 索引补齐下载的文件数</summary>
    public int DownloadedFiles { get; set; }
    /// <summary>跳过(服务端可选/不支持)的文件数</summary>
    public int SkippedFiles { get; set; }
}

/// <summary>目录导入结果(NeedManualVersion=true 表示目录内无版本本体,需用户手动指定版本后续导入)</summary>
public class DirImportResult : ModPackImportResult
{
    public bool NeedManualVersion { get; set; }
}

// ===== 各格式清单模型(只保留导入所需字段) =====

internal class MrIndex
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("versionId")] public string VersionId { get; set; } = "";
    [JsonPropertyName("files")] public List<MrFile> Files { get; set; } = new();
    [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = new();
}

internal class MrFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("fileSize")] public long FileSize { get; set; }
    [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = new();
    [JsonPropertyName("env")] public Dictionary<string, string> Env { get; set; } = new();
}

internal class CfManifest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("minecraft")] public List<CfMcSection> Minecraft { get; set; } = new();
    [JsonPropertyName("files")] public List<CfFile> Files { get; set; } = new();
}

internal class CfFile
{
    [JsonPropertyName("projectID")] public long ProjectId { get; set; }
    [JsonPropertyName("fileID")] public long FileId { get; set; }
    [JsonPropertyName("required")] public bool Required { get; set; } = true;
}

internal class CfMcSection
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("modLoaders")] public List<CfLoader> ModLoaders { get; set; } = new();
}

internal class CfLoader
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
}

internal class PrismPack
{
    [JsonPropertyName("components")] public List<PrismComponent> Components { get; set; } = new();
}

internal class PrismComponent
{
    [JsonPropertyName("uid")] public string Uid { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public class ModPackImportService
{
    private readonly InstanceService _instanceService;
    private readonly NativeInteropService _nativeInterop;
    private readonly StorageGuardService _storageGuard;
    private readonly DownloadService _downloadService;
    private readonly VersionManifestService _manifestService;
    private readonly GameInstallService _gameInstallService;
    private readonly ConfigService _configService;
    private readonly MemoryMonitorService _memoryMonitor;
    private readonly ModLoaderInstallService _loaderInstall;

    /// <summary>CurseForge API 专用 HTTP 客户端(逐请求携带 x-api-key 头)</summary>
    private static readonly HttpClient CfHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    public ModPackImportService(InstanceService instanceService,
                                NativeInteropService nativeInterop,
                                StorageGuardService storageGuard,
                                DownloadService downloadService,
                                VersionManifestService manifestService,
                                GameInstallService gameInstallService,
                                ConfigService configService,
                                MemoryMonitorService memoryMonitor,
                                ModLoaderInstallService loaderInstall)
    {
        _instanceService = instanceService;
        _nativeInterop = nativeInterop;
        _storageGuard = storageGuard;
        _downloadService = downloadService;
        _manifestService = manifestService;
        _gameInstallService = gameInstallService;
        _configService = configService;
        _memoryMonitor = memoryMonitor;
        _loaderInstall = loaderInstall;
    }

    /// <summary>文件是否为受支持的整合包(.mrpack / .zip)</summary>
    public static bool IsModPackFile(string path) =>
        path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>zip 结构探测结果(基于中央目录,不解压)</summary>
    private sealed class ZipProbe
    {
        public ModPackFormat Format = ModPackFormat.Unknown;
        /// <summary>包内真实根目录前缀("" = 顶层;官方导出常用版本名再套一层目录)</summary>
        public string RootPrefix = "";
        /// <summary>清单条目完整路径(modrinth.index.json / manifest.json / mmc-pack.json)</summary>
        public string? MetaEntry;
        public string Mc = "", Loader = "", LoaderVer = "", Name = "";
        /// <summary>版本隔离懒人包的完整版本 ID(如 26.1.2-NeoForge_26.1.2.78,2026-09-26 第三方包适配)</summary>
        public string VersionId = "";
    }

    /// <summary>探测整合包格式(只读压缩包中央目录,不解压)</summary>
    public ModPackFormat DetectFormat(string packPath)
    {
        try { return ProbeZip(packPath).Format; }
        catch { return ModPackFormat.Unknown; } // 损坏压缩包等单独在导入时报错
    }

    /// <summary>探测 zip 结构:全量扫描中央目录,先确定“真实根目录”(官方导出常用版本名再套一层,
    /// 如 GT New Horizons 2.8.4/mmc-pack.json),再按该根下的清单标记判格式并读取元数据</summary>
    private static ZipProbe ProbeZip(string packPath)
    {
        var p = new ZipProbe();
        using var zip = ZipFile.OpenRead(packPath);
        var names = new HashSet<string>(zip.Entries.Select(e => e.FullName.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);

        // 候选根:顶层 "" + 每个一级目录前缀;顶层优先
        var roots = new List<string> { "" };
        roots.AddRange(names.Select(n => { int i = n.IndexOf('/'); return i > 0 ? n[..(i + 1)] : null; })
                            .Where(r => r != null).Select(r => r!).Distinct());
        foreach (var r in roots)
        {
            if (names.Contains(r + "modrinth.index.json")) { p.Format = ModPackFormat.Modrinth; p.RootPrefix = r; p.MetaEntry = r + "modrinth.index.json"; break; }
            if (names.Contains(r + "manifest.json")) { p.Format = ModPackFormat.CurseForge; p.RootPrefix = r; p.MetaEntry = r + "manifest.json"; break; }
            if (names.Contains(r + "mmc-pack.json")) { p.Format = ModPackFormat.Prism; p.RootPrefix = r; p.MetaEntry = r + "mmc-pack.json"; break; }
            if (names.Contains(r + "modpack.json"))   // HMCL / Technic 整合包:modpack.json + .minecraft 整目录(2026-09-26 补全)
            { p.Format = ModPackFormat.LazyPack; p.RootPrefix = r; p.MetaEntry = r + "modpack.json"; ReadGenericPackMeta(zip, p); break; }
        }
        if (p.Format == ModPackFormat.Unknown)
        {
            // 全目录包(懒人包/各第三方启动器导出):有任一游戏目录特征即认(含套壳根)
            // 2026-09-26 加宽:config/resourcepacks/shaderpacks/saves/logs/bin 都算特征,
            // 修复"纯 config 包 / Technic 包(bin\modpack.jar)识别不了"的问题
            foreach (var r in roots)
            {
                if (names.Any(n => n.StartsWith(r + "versions/", StringComparison.OrdinalIgnoreCase) ||
                                   n.StartsWith(r + ".minecraft/", StringComparison.OrdinalIgnoreCase) ||
                                   n.StartsWith(r + "mods/", StringComparison.OrdinalIgnoreCase) ||
                                   n.StartsWith(r + "config/", StringComparison.OrdinalIgnoreCase) ||
                                   n.StartsWith(r + "resourcepacks/", StringComparison.OrdinalIgnoreCase) ||
                                   n.StartsWith(r + "shaderpacks/", StringComparison.OrdinalIgnoreCase) ||
                                   n.StartsWith(r + "saves/", StringComparison.OrdinalIgnoreCase) ||
                                   n.StartsWith(r + "logs/", StringComparison.OrdinalIgnoreCase) ||
                                   n.StartsWith(r + "bin/", StringComparison.OrdinalIgnoreCase)) ||
                    names.Contains(r + "options.txt"))
                { p.Format = ModPackFormat.LazyPack; p.RootPrefix = r; break; }
            }
        }
        // .mrpack 扩展名兜底(索引不在目录前两层的极端情况)
        if (p.Format == ModPackFormat.Unknown && packPath.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            p.Format = ModPackFormat.Modrinth;

        if (p.MetaEntry != null) ReadZipMeta(zip, p);
        if (p.Format == ModPackFormat.LazyPack) GuessLazyMcVersion(names, p);
        return p;
    }

    /// <summary>导入整合包为新游戏版本(异步;进度文案经 onProgress 回调)。
    /// 管线:结构探测 → 存储预检 → 建骨架 → 解压/搬运 → 布局规范 → 版本本体补齐 → 大包调优;
    /// 全程打点日志,任何阶段失败自动回滚不留残骸</summary>
    public async Task<ModPackImportResult> ImportAsync(string packPath, string? customName,
                                                       Action<string>? onProgress = null)
    {
        var result = new ModPackImportResult();
        void Report(string s) { onProgress?.Invoke(s); App.WriteAppLog($"[整合包] {s}"); }

        GameInstance? inst = null;
        string staging = "";
        try
        {
            if (!File.Exists(packPath))
            {
                result.Message = "整合包文件不存在,请检查路径。";
                return result;
            }

            // 1. 结构探测(全量中央目录,套壳根导出可识别)
            Report("解析整合包结构…");
            ZipProbe probe;
            try { probe = ProbeZip(packPath); }
            catch (InvalidDataException)
            {
                result.Message = "该整合包已损坏或不是有效的 zip 压缩包,请重新下载后重试。";
                return result;
            }
            if (probe.Format == ModPackFormat.Unknown)
            {
                result.Message = "无法识别该整合包格式。\n支持:Modrinth(.mrpack)、CurseForge、MultiMC/Prism、主流第三方启动器导出与懒人包(.zip)。";
                return result;
            }
            result.Format = probe.Format;
            App.WriteAppLog($"[整合包] 探测结果:格式={probe.Format} 根前缀=\"{probe.RootPrefix}\" MC={probe.Mc} 加载器={probe.Loader} {probe.LoaderVer} 包名={probe.Name}");

            // 2. 存储预检(暂存区 + 目标目录拷贝 + 版本本体预留)
            long packSize = new FileInfo(packPath).Length;
            var pre = _storageGuard.Precheck(AppPaths.Instances, packSize * 4 + 1536L * 1024 * 1024);
            if (pre.Result != StorageCheckResult.Ok)
            {
                result.Message = "存储环境预检未通过:" + pre.Message;
                return result;
            }

            // 3. 创建游戏版本骨架(失败时可整体回滚)
            string packName = string.IsNullOrWhiteSpace(probe.Name)
                ? Path.GetFileNameWithoutExtension(packPath) : probe.Name;
            string instName = string.IsNullOrWhiteSpace(customName) ? packName : customName.Trim();
            Report($"创建游戏版本「{instName}」…");
            inst = _instanceService.CreateInstance(instName, probe.Mc,
                string.IsNullOrEmpty(probe.Mc) ? 17 : JavaRuntimeService.RecommendJavaMajor(probe.Mc));
            inst.ModLoader = string.IsNullOrEmpty(probe.Loader) ? null : probe.Loader;
            inst.ModLoaderVersion = string.IsNullOrEmpty(probe.LoaderVer) ? null : probe.LoaderVer;
            _instanceService.SaveInstance(inst);
            string instDir = _instanceService.GetInstanceDir(inst.Id);

            // 4. 解压落盘:全目录包直接解到目标目录;清单包先入暂存区再搬运内容子树
            if (probe.Format == ModPackFormat.LazyPack)
            {
                Report("解压整合包内容…");
                if (!await ExtractAsync(packPath, instDir))
                    throw new InvalidDataException("整合包解压失败,文件可能损坏。");
                PromoteSingleRoot(instDir);
                RemovePackJunk(instDir);                // 2026-09-26:剔除导入包内残留的外来启动器文件
                ConfirmLazyVersionFromDisk(instDir, probe); // 2026-09-26:落盘复核版本目录(先于展平,防探测猜错)
                FlattenVersionIsolation(instDir, probe); // 2026-09-26:版本隔离目录内容合并到游戏根,防 mods 丢失
                result.ExtractedFiles = -1; // 整包解压,文件数不单独统计
            }
            else
            {
                staging = Path.Combine(AppPaths.Cache, $"pack_{inst.Id}");
                Directory.CreateDirectory(staging);
                Report("解压整合包内容…");
                if (!await ExtractAsync(packPath, staging))
                    throw new InvalidDataException("整合包解压失败,文件可能损坏。");

                // 在磁盘上重新定位真实包根(与 zip 内探测互为印证,容忍套壳)
                string packRoot = LocatePackRoot(staging, probe)
                    ?? throw new InvalidDataException("解压后未能在包内定位到清单文件,结构可能不受支持。");
                string contentDir = probe.Format switch
                {
                    ModPackFormat.Prism => Path.Combine(packRoot, ".minecraft"),
                    _ => Directory.Exists(Path.Combine(packRoot, "overrides"))
                            ? Path.Combine(packRoot, "overrides") : packRoot
                };
                if (!Directory.Exists(contentDir))
                    throw new InvalidDataException(probe.Format == ModPackFormat.Prism
                        ? "整合包内未找到 .minecraft 游戏目录。"
                        : "整合包内未找到内容目录。");

                Report("复制游戏文件…");
                result.ExtractedFiles = await Task.Run(() => CopyDirInto(contentDir, instDir, skipMeta: true));

                // 在线文件补齐(mrpack 索引有直链;CF 走官方 API 需 Key)——清单从暂存区磁盘读取
                string metaFile = Path.Combine(packRoot, Path.GetFileName(probe.MetaEntry!));
                if (probe.Format == ModPackFormat.Modrinth)
                    await DownloadMrFilesAsync(metaFile, instDir, result, Report);
                else if (probe.Format == ModPackFormat.CurseForge)
                    await DownloadCfFilesAsync(metaFile, instDir, result, Report);
            }

            // 5. 目录规范修复(解压可能触碰联接目录,统一重建布局)
            _instanceService.EnsureInstanceLayout(inst.Id);
            _instanceService.RefreshInstances();

            // 6. 版本本体补齐:识别出 MC 版本但全局缺失版本 JSON 时,自动安装原版本体
            //    (懒人包版本隔离:包内已有完整版本 JSON 时不下载原版)
            await EnsureVersionBodyAsync(inst, probe.Mc, Report, probe.VersionId);

            // 6.4 版本隔离懒人包:包内自带完整版本(含加载器)时,实例直接用该版本 ID 启动
            bool hasFullVersion = !string.IsNullOrEmpty(probe.VersionId)
                && File.Exists(Path.Combine(AppPaths.Versions, probe.VersionId, probe.VersionId + ".json"));
            if (hasFullVersion && inst.VersionId != probe.VersionId)
            {
                inst.VersionId = probe.VersionId;
                _instanceService.SaveInstance(inst);
                Report($"识别到包内版本 {probe.VersionId},直接使用该版本启动");
            }

            // 6.45 包内已含加载器本体时,导入阶段就把 LoaderVersionId 登记好(2026-09-26)。
            //      懒人包把加载器与游戏本体合并在同一份版本 JSON 里(自包含、无 inheritsFrom),
            //      LoaderVersionId 若留空,启动前的空壳判定会把已装好的加载器当成缺失、
            //      反复去下载安装器 —— 网络源不稳时必然失败,用户看到的就是「导入完了还要补全」。
            //      这里当场写全实例元数据,导入完成即是可启动状态,不依赖启动时再补救。
            if (hasFullVersion && inst.LoaderVersionId != probe.VersionId)
            {
                string? bakedLoader = LoaderInVersionName(probe.VersionId);
                if (bakedLoader != null)
                {
                    inst.LoaderVersionId = probe.VersionId;
                    if (string.IsNullOrEmpty(inst.ModLoader)) inst.ModLoader = bakedLoader;
                    _instanceService.SaveInstance(inst);
                    App.WriteAppLog($"[整合包] 加载器本体已并入 {probe.VersionId},导入即登记为 LoaderVersionId,无需再补装");
                }
            }

            // 6.5 加载器自动安装(修"能导入不能启动":无加载器本体时启动链只能拉原版主类;
            //     包内已含完整版本本体时跳过,避免重复安装)
            string loaderNote = hasFullVersion
                ? (string.IsNullOrEmpty(probe.Loader) ? "" : $"包内已含 {probe.Loader} {probe.LoaderVer},无需重复安装。")
                : await InstallLoaderSafeAsync(inst, probe, Report);

            // 7. 大型整合包调优(GTNH 等):自动配置建议大内存 + 专属 JVM 参数 + 禁用 Forge 闪屏
            string tuningNote = "";
            try
            {
                if (GtnhTuner.IsGtnh(inst.Name, instDir))
                {
                    tuningNote = GtnhTuner.Apply(inst, instDir, _memoryMonitor);
                    if (!string.IsNullOrEmpty(tuningNote)) _instanceService.SaveInstance(inst);
                }
            }
            catch (Exception ex) { App.WriteAppLog($"[整合包] 大型整合包调优检测失败(不阻断):{ex.Message}"); }

            result.Ok = true;
            result.Instance = inst;
            string loaderText = string.IsNullOrEmpty(probe.Loader) ? "" : $" · {probe.Loader} {probe.LoaderVer}";
            string mcText = string.IsNullOrEmpty(probe.Mc) ? "未识别游戏版本(可手动指定版本后重装)" : $"MC {probe.Mc}";
            string notes = string.Join("\n", new[] { result.Message, loaderNote, tuningNote }.Where(s => !string.IsNullOrEmpty(s)));
            string extractText = result.ExtractedFiles < 0 ? "整包解压(懒人包)" : $"解压文件 {result.ExtractedFiles} 个";
            result.Message = $"导入完成:{mcText}{loaderText}\n{extractText}" +
                             (result.DownloadedFiles > 0 ? $",在线补齐 {result.DownloadedFiles} 个" : "") +
                             (result.SkippedFiles > 0 ? $",跳过 {result.SkippedFiles} 个(仅服务端/可选)" : "") + "。" +
                             (string.IsNullOrEmpty(notes) ? "" : "\n" + notes);
            Report("导入完成");
            return result;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[整合包] 导入失败:{ex}");
            // 失败回滚:删除已创建的游戏版本与物理目录,不留残骸
            if (inst != null) Rollback(inst.Id);
            result.Ok = false;
            result.Instance = null;
            result.Message = StorageGuardService.ClassifyIoError(ex, "整合包导入");
            return result;
        }
        finally
        {
            if (!string.IsNullOrEmpty(staging)) TryDeleteDir(staging);
        }
    }

    /// <summary>加载器自动安装(失败不阻断导入,仅提示手动安装);
    /// 安装成功后由安装方法内部写入 LoaderVersionId,启动链自动切换到加载器版本 JSON</summary>
    private async Task<string> InstallLoaderSafeAsync(GameInstance inst, ZipProbe probe, Action<string> report)
    {
        if (string.IsNullOrEmpty(probe.Loader) || string.IsNullOrEmpty(probe.Mc)) return "";
        if (string.IsNullOrEmpty(probe.LoaderVer))
        {
            App.WriteAppLog($"[整合包] 清单未含 {probe.Loader} 具体版本号,跳过自动安装");
            return $"未识别 {probe.Loader} 具体版本,请在「新装版本」向导手动安装该加载器。";
        }
        try
        {
            report($"正在安装 {probe.Loader} {probe.LoaderVer}…");
            var res = probe.Loader switch
            {
                "Forge" => await _loaderInstall.InstallForgeAsync(inst.Id, probe.Mc, probe.LoaderVer),
                "Fabric" => await _loaderInstall.InstallFabricAsync(inst.Id, probe.Mc, probe.LoaderVer),
                "Quilt" => await _loaderInstall.InstallQuiltAsync(inst.Id, probe.Mc, probe.LoaderVer),
                "NeoForge" => await _loaderInstall.InstallNeoForgeAsync(inst.Id, probe.Mc, probe.LoaderVer),
                _ => null
            };
            if (res == null)
            {
                App.WriteAppLog($"[整合包] 加载器 {probe.Loader} 无对应安装通道,跳过");
                return $"加载器 {probe.Loader} 暂无自动安装通道,请手动安装。";
            }
            if (!res.Success)
            {
                App.WriteAppLog($"[整合包] {probe.Loader} {probe.LoaderVer} 自动安装失败:{res.ErrorMessage}");
                return $"{probe.Loader} 自动安装失败({res.ErrorMessage}),可在「新装版本」向导手动安装该加载器。";
            }
            App.WriteAppLog($"[整合包] ✓ {probe.Loader} {probe.LoaderVer} 已随导入自动安装");
            return "";
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[整合包] 加载器自动安装异常(不阻断):{ex.Message}");
            return $"{probe.Loader} 自动安装异常,可手动安装该加载器。";
        }
    }

    /// <summary>在暂存区磁盘上重新定位真实包根目录(按清单标记文件;单根套壳下探 ≤3 层 + 两层扫描兜底)</summary>
    private static string? LocatePackRoot(string staging, ZipProbe probe)
    {
        string marker = probe.Format switch
        {
            ModPackFormat.Modrinth => "modrinth.index.json",
            ModPackFormat.CurseForge => "manifest.json",
            ModPackFormat.Prism => "mmc-pack.json",
            _ => ""
        };
        if (marker == "") return null;
        if (File.Exists(Path.Combine(staging, marker))) return staging;
        // 单一根目录套壳:逐层下探
        string cur = staging;
        for (int i = 0; i < 3; i++)
        {
            string[] subs;
            try { subs = Directory.GetDirectories(cur); } catch { break; }
            if (subs.Length != 1) break;
            cur = subs[0];
            if (File.Exists(Path.Combine(cur, marker))) return cur;
        }
        // 兜底:前两层目录扫描标记文件
        try
        {
            foreach (var s1 in Directory.GetDirectories(staging))
            {
                if (File.Exists(Path.Combine(s1, marker))) return s1;
                foreach (var s2 in Directory.GetDirectories(s1))
                    if (File.Exists(Path.Combine(s2, marker))) return s2;
            }
        }
        catch { }
        return null;
    }

    // ==================== 内部实现 ====================

    /// <summary>解压(恒走托管字节级进度实现;原生 Shell COM CopyHere 为异步返回,
    /// 报"完成"时文件可能尚未落盘,会造成导入不完整,故整合包解压不启用原生路径)</summary>
    private async Task<bool> ExtractAsync(string zipPath, string destDir)
    {
        var (ok, _) = await _nativeInterop.ExtractZipWithByteProgress(zipPath, destDir, null);
        return ok;
    }
    
    /// <summary>版本本体补齐:全局 versions\{mcVersion}\{mcVersion}.json 缺失时,
    /// 经官方清单定位并安装原版本体(失败不阻断导入,仅提示手动补装)</summary>
    private async Task EnsureVersionBodyAsync(GameInstance inst, string mcVersion, Action<string> report,
                                              string? fullVersionId = null)
    {
        if (string.IsNullOrEmpty(mcVersion)) return;
        string versionJson = Path.Combine(AppPaths.Versions, mcVersion, mcVersion + ".json");
        if (File.Exists(versionJson)) return;
        // 2026-09-26:懒人包版本隔离,包内已有完整版本 JSON(如 26.1.2-NeoForge_26.1.2.78)时直接认定已有本体,不下载原版
        if (!string.IsNullOrEmpty(fullVersionId))
        {
            string fullJson = Path.Combine(AppPaths.Versions, fullVersionId, fullVersionId + ".json");
            if (File.Exists(fullJson)) return;
        }
        // 2026-09-26:实例实际使用的版本 JSON 自包含(无 inheritsFrom 的合并版)时,
        // 原版本体已内含,无需再下载 —— 这是「导入后仍被补全」的主要来源之一
        if (EffectiveVersionIsSelfContained(inst)) return;
        try
        {
            report($"正在安装游戏本体 MC {mcVersion}…");
            var manifest = await _manifestService.FetchManifestAsync(false);
            var mv = manifest?.Versions.FirstOrDefault(v => v.Id == mcVersion);
            if (mv == null)
            {
                App.WriteAppLog($"[整合包] 官方清单中未找到版本 {mcVersion},无法自动补齐本体");
                return;
            }
            bool ok = await _gameInstallService.InstallVersionAsync(inst.Id, mv);
            if (ok)
                App.WriteAppLog($"[整合包] ✓ 版本本体 {mcVersion} 已自动补齐");
            else
                App.WriteAppLog($"[整合包] 版本本体 {mcVersion} 安装失败:{_gameInstallService.LastError}(可在「新装版本」重装)");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[整合包] 版本本体 {mcVersion} 安装异常:{ex.Message}");
        }
    }

    /// <summary>实例实际使用的版本 JSON(加载器版本优先)是否为自包含(无 inheritsFrom)。
    /// 自包含意味着原版本体已内含于该 JSON,再下原版纯属重复劳动与带宽浪费。</summary>
    private static bool EffectiveVersionIsSelfContained(GameInstance inst)
    {
        try
        {
            foreach (var vid in new[] { inst.LoaderVersionId, inst.VersionId })
            {
                if (string.IsNullOrEmpty(vid)) continue;
                string p = Path.Combine(AppPaths.Versions, vid, vid + ".json");
                if (!File.Exists(p)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                return !doc.RootElement.TryGetProperty("inheritsFrom", out _);
            }
        }
        catch (Exception ex) { App.WriteAppLog($"[整合包] 版本 JSON 自包含判定失败(按需下载):{ex.Message}"); }
        return false;
    }

    /// <summary>HMCL / Technic 整合包 modpack.json 宽松解析:MC 版本 / 加载器 / 包名(2026-09-26 补全)。
    /// HMCL 字段:minecraft + forge/fabric/quilt/neoforge;Technic 字段:minecraftVersion + name。</summary>
    private static void ReadGenericPackMeta(ZipArchive zip, ZipProbe p)
    {
        try
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == p.MetaEntry);
            if (entry == null) return;
            string json;
            using (var sr = new StreamReader(entry.Open())) json = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 版本:Technic 用 minecraftVersion;HMCL 用 minecraft
            if (root.TryGetProperty("minecraftVersion", out var mcv)) p.Mc = mcv.GetString() ?? "";
            else if (root.TryGetProperty("minecraft", out var mc)) p.Mc = mc.GetString() ?? "";
            // 加载器:HMCL 字段 forge/fabric/quilt/neoforge(fabric-loader 兜底)
            foreach (var (key, loader) in new[]{("forge","Forge"),("fabric-loader","Fabric"),
                                                ("fabric","Fabric"),("quilt","Quilt"),("neoforge","NeoForge")})
                if (root.TryGetProperty(key, out var lv)) { p.Loader = loader; p.LoaderVer = lv.GetString() ?? ""; break; }
            if (root.TryGetProperty("name", out var nm)) p.Name = nm.GetString() ?? "";
        }
        catch (Exception ex) { App.WriteAppLog($"[整合包] HMCL/Technic 元数据解析失败(不阻断):{ex.Message}"); }
    }

    /// <summary>从压缩包读取清单元数据:MC 版本 / 加载器 / 包名(解析失败不阻断)</summary>
    private static void ReadZipMeta(ZipArchive zip, ZipProbe p)
    {
        try
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == p.MetaEntry);
            if (entry == null) return;
            string json;
            using (var sr = new StreamReader(entry.Open())) json = sr.ReadToEnd();

            if (p.Format == ModPackFormat.Modrinth)
            {
                var idx = JsonSerializer.Deserialize<MrIndex>(json);
                if (idx != null)
                {
                    if (!string.IsNullOrEmpty(idx.Name)) p.Name = idx.Name;
                    if (idx.Dependencies.TryGetValue("minecraft", out var m)) p.Mc = m;
                    foreach (var (k, v) in idx.Dependencies)
                    {
                        if (k == "forge") { p.Loader = "Forge"; p.LoaderVer = v; }
                        else if (k == "fabric-loader") { p.Loader = "Fabric"; p.LoaderVer = v; }
                        else if (k == "quilt-loader") { p.Loader = "Quilt"; p.LoaderVer = v; }
                        else if (k == "neoforge") { p.Loader = "NeoForge"; p.LoaderVer = v; }
                    }
                }
            }
            else if (p.Format == ModPackFormat.CurseForge)
            {
                var mf = JsonSerializer.Deserialize<CfManifest>(json);
                if (mf != null)
                {
                    if (!string.IsNullOrEmpty(mf.Name)) p.Name = mf.Name;
                    var sec = mf.Minecraft.FirstOrDefault();
                    if (sec != null)
                    {
                        p.Mc = sec.Version;
                        var ld = sec.ModLoaders.FirstOrDefault();
                        if (ld != null)
                        {
                            // CF 加载器 id 形如 forge-47.2.0 / fabric-0.15.0
                            int dash = ld.Id.IndexOf('-');
                            p.Loader = dash > 0 ? char.ToUpperInvariant(ld.Id[0]) + ld.Id[1..dash] : ld.Id;
                            p.LoaderVer = dash > 0 ? ld.Id[(dash + 1)..] : "";
                        }
                    }
                }
            }
            else if (p.Format == ModPackFormat.Prism)
            {
                var pp = JsonSerializer.Deserialize<PrismPack>(json);
                if (pp?.Components != null)
                {
                    foreach (var c in pp.Components)
                    {
                        if (c.Uid == "net.minecraft") p.Mc = c.Version;
                        else if (c.Uid == "net.minecraftforge") { p.Loader = "Forge"; p.LoaderVer = c.Version; }
                        else if (c.Uid == "net.fabricmc.fabric-loader") { p.Loader = "Fabric"; p.LoaderVer = c.Version; }
                        else if (c.Uid == "org.quiltmc.quilt-loader") { p.Loader = "Quilt"; p.LoaderVer = c.Version; }
                    }
                }
            }
        }
        catch (Exception ex) { App.WriteAppLog($"[整合包] 清单元数据解析失败(不阻断导入):{ex.Message}"); }
    }

    /// <summary>懒人包无统一清单:从 versions 目录名推断 MC 版本 / 完整版本 ID / 加载器。
    /// 2026-09-26 版本隔离包适配:优先选含同名 .json/.jar 的真实版本目录,
    /// 目录名如 26.1.2-NeoForge_26.1.2.78 → 主版本 26.1.2 + 完整版本 ID + NeoForge 26.1.2.78</summary>
    private static void GuessLazyMcVersion(HashSet<string> names, ZipProbe p)
    {
        var vdirs = names.Select(n =>
            {
                int i = n.IndexOf("/versions/", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return null;
                var rest = n[(i + "/versions/".Length)..];
                int slash = rest.IndexOf('/');
                return slash > 0 ? rest[..slash] : null;
            })
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList();
        if (vdirs.Count == 0) return;
        // 优先选含 <同名>.json 或 <同名>.jar 的真实版本目录(过滤外来启动器 ini 之类杂项目录);
        // 2026-09-26 用后缀匹配替代固定前缀,兼容套壳根(如 包名\.minecraft\versions\…)
        string? best = null;
        foreach (var v in vdirs)
        {
            string suffixJson = $"/versions/{v}/{v}.json";
            string suffixJar = $"/versions/{v}/{v}.jar";
            if (names.Any(n => n.EndsWith(suffixJson, StringComparison.OrdinalIgnoreCase) ||
                               n.EndsWith(suffixJar, StringComparison.OrdinalIgnoreCase) ||
                               n.Equals(suffixJson.TrimStart('/'), StringComparison.OrdinalIgnoreCase) ||
                               n.Equals(suffixJar.TrimStart('/'), StringComparison.OrdinalIgnoreCase)))
            { best = v; break; }
        }
        // 兜底:取以数字开头(真实 MC 版本特征)的目录,避免杂项目录抢占
        best ??= vdirs.FirstOrDefault(v => v.Length > 0 && char.IsDigit(v[0])) ?? vdirs[0];
        if (best is null) return;   // 编译器可空收窄(实际 vdirs.Count>0 不会走到)
        p.VersionId = best;
        p.Mc = ExtractBaseVersion(best);
        ApplyLoaderFromVersionName(p, best);
    }

    /// <summary>从版本目录名推断加载器与版本号(NeoForge_26.1.2.78 / forge14.23.5.2860 / fabric0.15.0)</summary>
    private static void ApplyLoaderFromVersionName(ZipProbe p, string versionName)
    {
        string lower = versionName.ToLowerInvariant();
        string loader = "";
        if (lower.Contains("neoforge")) loader = "NeoForge";
        else if (lower.Contains("forge")) loader = "Forge";
        else if (lower.Contains("fabric")) loader = "Fabric";
        else if (lower.Contains("quilt")) loader = "Quilt";
        if (loader.Length == 0) return;
        p.Loader = loader;
        int idx = lower.IndexOf(loader.ToLowerInvariant());
        string tail = versionName[(idx + loader.Length)..].TrimStart('_', '-', ' ');
        int j = 0;
        while (j < tail.Length && (char.IsDigit(tail[j]) || tail[j] == '.')) j++;
        if (j > 0) p.LoaderVer = tail[..j];
    }

    /// <summary>版本目录名是否自带加载器(懒人包把加载器并入版本本体,如 26.1.2-NeoForge_26.1.2.78)。
    /// 复用 ApplyLoaderFromVersionName 的关键字表,纯原版目录名返回 null ——
    /// 避免「清单声明了加载器、但版本目录其实是纯原版」时把原版误登记成加载器本体。</summary>
    private static string? LoaderInVersionName(string versionId)
    {
        var tmp = new ZipProbe();
        ApplyLoaderFromVersionName(tmp, versionId);
        return string.IsNullOrEmpty(tmp.Loader) ? null : tmp.Loader;
    }

    /// <summary>mrpack 索引文件在线补齐(仅 client 环境需要的文件;失败计入跳过不阻断)
    /// manifestFile 为暂存区磁盘上的 modrinth.index.json</summary>
    private async Task DownloadMrFilesAsync(string manifestFile, string instDir,
                                            ModPackImportResult result, Action<string> report)
    {
        if (!File.Exists(manifestFile)) return;
        MrIndex? idx;
        try { idx = JsonSerializer.Deserialize<MrIndex>(File.ReadAllText(manifestFile)); }
        catch (Exception ex) { App.WriteAppLog($"[整合包] mrpack 索引解析失败:{ex.Message}"); return; }
        if (idx == null) return;

        var tasks = new List<DownloadTaskItem>();
        foreach (var f in idx.Files)
        {
            // env.client == "unsupported" 表示客户端不需要该文件
            if (f.Env.TryGetValue("client", out var env) && env == "unsupported")
            {
                result.SkippedFiles++;
                continue;
            }
            string url = f.Downloads.FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(url)) { result.SkippedFiles++; continue; }
            // 安全校验(zip-slip 防护):索引路径含 .. 或为绝对路径时拒绝,防止恶意包越狱写入
            string relPath = f.Path.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrEmpty(relPath) || relPath.Contains("..") || Path.IsPathRooted(relPath))
            {
                App.WriteAppLog($"[整合包] 跳过非法索引路径:{f.Path}");
                result.SkippedFiles++;
                continue;
            }
            string local = Path.Combine(instDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            tasks.Add(new DownloadTaskItem
            {
                Url = url,
                LocalPath = local,
                Size = f.FileSize,
                Category = DownloadCategory.Mod
            });
        }
        if (tasks.Count == 0) return;

        report($"在线补齐 {tasks.Count} 个索引文件…");
        _downloadService.ResetOverallProgress();
        bool ok = await _downloadService.DownloadAllAsync(tasks);
        result.DownloadedFiles = tasks.Count(t => t.Status == DownloadStatus.Completed);
        result.SkippedFiles += tasks.Count - result.DownloadedFiles;
        if (!ok)
            App.WriteAppLog($"[整合包] mrpack 部分文件补齐失败({tasks.Count - result.DownloadedFiles} 个),可稍后在校验修复中重试");
    }

    /// <summary>CurseForge 整合包在线文件补齐(官方 API 需 Key;未配置 Key 时给出明确中文指引不阻断)
    /// manifestFile 为暂存区磁盘上的 manifest.json</summary>
    private async Task DownloadCfFilesAsync(string manifestFile, string instDir,
                                            ModPackImportResult result, Action<string> report)
    {
        List<CfFile> files;
        try
        {
            if (!File.Exists(manifestFile)) return;
            var mf = JsonSerializer.Deserialize<CfManifest>(File.ReadAllText(manifestFile));
            files = mf?.Files?.Where(f => f.Required).ToList() ?? new List<CfFile>();
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[整合包] CF 清单 files 解析失败:{ex.Message}");
            return;
        }
        if (files.Count == 0)
        {
            result.Message = "该 CurseForge 包无在线文件条目,已导入包内本地内容。";
            return;
        }

        string key = _configService.Config.CurseForgeApiKey.Trim();
        if (string.IsNullOrEmpty(key))
        {
            result.SkippedFiles += files.Count;
            result.Message = $"该 CurseForge 整合包含 {files.Count} 个在线模组需补齐。\n" +
                             "请在【设置 → 网络设置】填写 CurseForge API Key(console.curseforge.com 免费注册)后重新导入以自动补齐;\n" +
                             "本次已导入包内本地内容,缺失模组可手动下载后放入 mods 目录。";
            return;
        }

        report($"正在查询 {files.Count} 个在线模组的下载地址…");
        var tasks = new List<DownloadTaskItem>();
        int queryFail = 0;
        foreach (var f in files)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.curseforge.com/v1/mods/{f.ProjectId}/files/{f.FileId}/download-url");
                req.Headers.Add("x-api-key", key);
                req.Headers.UserAgent.ParseAdd("FufuLauncher/1.9.8.6 (Windows)");
                using var resp = await CfHttp.SendAsync(req);
                if (!resp.IsSuccessStatusCode) { queryFail++; continue; }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                string url = doc.RootElement.GetProperty("data").GetString() ?? "";
                if (string.IsNullOrEmpty(url)) { queryFail++; continue; }
                string fileName = Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath));
                if (string.IsNullOrWhiteSpace(fileName)) { queryFail++; continue; }
                tasks.Add(new DownloadTaskItem
                {
                    Url = url,
                    LocalPath = Path.Combine(instDir, "mods", fileName),
                    Size = 0,
                    Category = DownloadCategory.Mod
                });
            }
            catch (Exception ex)
            {
                queryFail++;
                App.WriteAppLog($"[整合包] CF 文件 {f.ProjectId}/{f.FileId} 查询失败:{ex.Message}");
            }
        }

        if (tasks.Count > 0)
        {
            report($"正在下载 {tasks.Count} 个模组…");
            _downloadService.ResetOverallProgress();
            await _downloadService.DownloadAllAsync(tasks);
            result.DownloadedFiles = tasks.Count(t => t.Status == DownloadStatus.Completed);
        }
        result.SkippedFiles += files.Count - result.DownloadedFiles;
        result.Message = result.DownloadedFiles == files.Count
            ? "CurseForge 整合包在线模组已全部自动补齐。"
            : $"CurseForge 在线模组已补齐 {result.DownloadedFiles}/{files.Count}" +
              (queryFail > 0 ? $"({queryFail} 个因作者禁止第三方分发或网络原因失败,需手动下载)" : "") + "。";
    }

    // ==================== 目录导入(主流启动器 .minecraft 迁移) ====================

    /// <summary>导入外部游戏目录(拷贝迁移,不改动源文件)。目录无版本本体时返回 NeedManualVersion,
    /// 由 UI 引导用户手动指定版本后调 ImportDirWithVersionAsync 续导</summary>
    public async Task<DirImportResult> ImportDirAsync(string sourceDir, string? customName,
                                                      Action<string>? onProgress = null)
    {
        var result = new DirImportResult();
        void Report(string s) { onProgress?.Invoke(s); App.WriteAppLog($"[目录导入] {s}"); }
        try
        {
            if (!Directory.Exists(sourceDir))
            {
                result.Message = "所选目录不存在,请检查路径。";
                return result;
            }
            Report("解析目录结构…");
            // Prism/MultiMC 已解压实例(mmc-pack.json + .minecraft + patches)优先走专属导入
            string? prismRoot = LocatePrismRoot(sourceDir);
            if (prismRoot != null)
                return await ImportPrismDirCoreAsync(prismRoot, customName, Report);

            string root = LocateMcRoot(sourceDir);
            string name = string.IsNullOrWhiteSpace(customName)
                ? Path.GetFileName(sourceDir.TrimEnd('\\', '/')) : customName.Trim();

            string versionsDir = Path.Combine(root, "versions");
            bool hasVersion = Directory.Exists(versionsDir) && Directory.GetDirectories(versionsDir).Length > 0;
            if (!hasVersion)
            {
                result.NeedManualVersion = true;
                result.Message = "目录中未检测到游戏版本本体(versions 目录下无可用版本)。\n请手动指定游戏版本后继续导入。";
                return result;
            }

            long size = DirSizeSafe(root);
            var pre = _storageGuard.Precheck(AppPaths.Instances, size + 512L * 1024 * 1024);
            if (pre.Result != StorageCheckResult.Ok)
            {
                result.Message = "存储环境预检未通过:" + pre.Message;
                return result;
            }

            Report("复制游戏文件中(不会改动原文件)…");
            var inst = await Task.Run(() => _instanceService.ImportExistingMinecraft(root, name));
            if (inst == null)
            {
                result.Message = "导入失败:目录中未找到可用的游戏版本文件。";
                return result;
            }

            string instDir = _instanceService.GetInstanceDir(inst.Id);
            DetectLoader(inst, instDir);
            string tuningNote = "";
            if (GtnhTuner.IsGtnh(inst.Name, instDir))
                tuningNote = GtnhTuner.Apply(inst, instDir, _memoryMonitor);
            _instanceService.SaveInstance(inst);
            _instanceService.RefreshInstances();

            // 版本本体补齐:合成版本名(如 1.12.2-forge…)取主版本段去官方清单补齐原版 JSON
            await EnsureVersionBodyAsync(inst, ExtractBaseVersion(inst.VersionId), Report);

            result.Ok = true;
            result.Instance = inst;
            result.Format = ModPackFormat.LazyPack;
            string loaderText = string.IsNullOrEmpty(inst.ModLoader) ? "" : $" · {inst.ModLoader}";
            result.Message = $"导入完成:{inst.VersionId}{loaderText}\n已复制 {root} 的游戏文件,原文件未改动。" +
                             (string.IsNullOrEmpty(tuningNote) ? "" : "\n" + tuningNote);
            Report("导入完成");
            return result;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[目录导入] 导入失败:{ex}");
            result.Message = StorageGuardService.ClassifyIoError(ex, "目录导入");
            return result;
        }
    }

    /// <summary>手动指定游戏版本后的目录导入(版本未识别/缺失时的兜底路径)</summary>
    public async Task<ModPackImportResult> ImportDirWithVersionAsync(string sourceDir, string customName,
                                                                     string mcVersion, Action<string>? onProgress = null)
    {
        var result = new ModPackImportResult { Format = ModPackFormat.LazyPack };
        void Report(string s) { onProgress?.Invoke(s); App.WriteAppLog($"[目录导入] {s}"); }
        GameInstance? inst = null;
        try
        {
            if (!Directory.Exists(sourceDir))
            {
                result.Message = "所选目录不存在,请检查路径。";
                return result;
            }
            string root = LocateMcRoot(sourceDir);
            long size = DirSizeSafe(root);
            var pre = _storageGuard.Precheck(AppPaths.Instances, size + 1536L * 1024 * 1024); // 额外预留版本本体下载空间
            if (pre.Result != StorageCheckResult.Ok)
            {
                result.Message = "存储环境预检未通过:" + pre.Message;
                return result;
            }

            Report($"创建游戏版本「{customName}」(MC {mcVersion})…");
            inst = _instanceService.CreateInstance(customName, mcVersion, JavaRuntimeService.RecommendJavaMajor(mcVersion));
            string instDir = _instanceService.GetInstanceDir(inst.Id);

            Report("复制游戏文件中(不会改动原文件)…");
            await Task.Run(() => CopyDirWithProgress(root, instDir, Report));

            // 布局规范重建:实例目录内混入的 versions/libraries/assets 上提全局,mods/saves 归位物理目录
            _instanceService.EnsureInstanceLayout(inst.Id);
            _instanceService.RefreshInstances();

            await EnsureVersionBodyAsync(inst, mcVersion, Report);

            DetectLoader(inst, instDir);
            string tuningNote = "";
            if (GtnhTuner.IsGtnh(customName, instDir))
                tuningNote = GtnhTuner.Apply(inst, instDir, _memoryMonitor);
            _instanceService.SaveInstance(inst);

            result.Ok = true;
            result.Instance = inst;
            result.Message = $"导入完成:MC {mcVersion}\n已复制 {root} 的游戏文件,原文件未改动。" +
                             (string.IsNullOrEmpty(tuningNote) ? "" : "\n" + tuningNote);
            Report("导入完成");
            return result;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[目录导入] 导入失败:{ex}");
            if (inst != null) Rollback(inst.Id);
            result.Ok = false;
            result.Instance = null;
            result.Message = StorageGuardService.ClassifyIoError(ex, "目录导入");
            return result;
        }
    }

    // ==================== Prism/MultiMC 已解压实例导入 ====================

    /// <summary>定位 Prism/MultiMC 实例根目录(含 mmc-pack.json,容忍单一顶层套壳,最多下探两层)</summary>
    private static string? LocatePrismRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "mmc-pack.json"))) return dir;
        try
        {
            foreach (var s in Directory.GetDirectories(dir))
            {
                if (File.Exists(Path.Combine(s, "mmc-pack.json"))) return s;
                foreach (var ss in Directory.GetDirectories(s))
                    if (File.Exists(Path.Combine(ss, "mmc-pack.json"))) return ss;
            }
        }
        catch { }
        return null;
    }

    /// <summary>从磁盘文件解析 Prism 清单(mmc-pack.json)的版本/加载器信息</summary>
    private static (string Mc, string Loader, string LoaderVer) ReadPrismMetaFile(string mmcPackPath)
    {
        string mc = "", loader = "", loaderVer = "";
        try
        {
            var pp = JsonSerializer.Deserialize<PrismPack>(File.ReadAllText(mmcPackPath));
            if (pp?.Components != null)
            {
                foreach (var c in pp.Components)
                {
                    if (c.Uid == "net.minecraft") mc = c.Version;
                    else if (c.Uid == "net.minecraftforge") { loader = "Forge"; loaderVer = c.Version; }
                    else if (c.Uid == "net.fabricmc.fabric-loader") { loader = "Fabric"; loaderVer = c.Version; }
                    else if (c.Uid == "org.quiltmc.quilt-loader") { loader = "Quilt"; loaderVer = c.Version; }
                }
            }
        }
        catch (Exception ex) { App.WriteAppLog($"[目录导入] mmc-pack.json 解析失败(不阻断):{ex.Message}"); }
        return (mc, loader, loaderVer);
    }

    /// <summary>导入已解压的 Prism/MultiMC 实例文件夹(.minecraft 内容拷贝迁移,源文件不改动)
    /// 注:patches 链式版本由启动器按识别出的 MC 版本 + 加载器自行重建,不直接搬运</summary>
    private async Task<DirImportResult> ImportPrismDirCoreAsync(string prismRoot, string? customName,
                                                                 Action<string> Report)
    {
        var result = new DirImportResult { Format = ModPackFormat.Prism };
        GameInstance? inst = null;
        try
        {
            string mcDir = Path.Combine(prismRoot, ".minecraft");
            if (!Directory.Exists(mcDir))
            {
                result.Message = "该 Prism/MultiMC 实例缺少 .minecraft 目录,无法导入。";
                return result;
            }

            var (mc, loader, loaderVer) = ReadPrismMetaFile(Path.Combine(prismRoot, "mmc-pack.json"));
            string name = string.IsNullOrWhiteSpace(customName)
                ? Path.GetFileName(prismRoot.TrimEnd('\\', '/')) : customName.Trim();

            long size = DirSizeSafe(mcDir);
            var pre = _storageGuard.Precheck(AppPaths.Instances, size + 1536L * 1024 * 1024); // 预留版本本体下载空间
            if (pre.Result != StorageCheckResult.Ok)
            {
                result.Message = "存储环境预检未通过:" + pre.Message;
                return result;
            }

            Report($"创建游戏版本「{name}」{(string.IsNullOrEmpty(mc) ? "" : $"(MC {mc})")}…");
            inst = _instanceService.CreateInstance(name, mc,
                string.IsNullOrEmpty(mc) ? 17 : JavaRuntimeService.RecommendJavaMajor(mc));
            inst.ModLoader = string.IsNullOrEmpty(loader) ? null : loader;
            inst.ModLoaderVersion = string.IsNullOrEmpty(loaderVer) ? null : loaderVer;
            _instanceService.SaveInstance(inst);
            string instDir = _instanceService.GetInstanceDir(inst.Id);

            Report("复制游戏文件中(不会改动原文件)…");
            await Task.Run(() => CopyDirWithProgress(mcDir, instDir, Report));

            // 布局规范重建:mods/saves 归位物理目录,混入的 versions/libraries/assets 上提全局
            _instanceService.EnsureInstanceLayout(inst.Id);
            _instanceService.RefreshInstances();

            await EnsureVersionBodyAsync(inst, mc, Report);

            DetectLoader(inst, instDir);
            string tuningNote = "";
            if (GtnhTuner.IsGtnh(inst.Name, instDir))
                tuningNote = GtnhTuner.Apply(inst, instDir, _memoryMonitor);
            _instanceService.SaveInstance(inst);

            result.Ok = true;
            result.Instance = inst;
            string loaderText = string.IsNullOrEmpty(loader) ? "" : $" · {loader} {loaderVer}";
            result.Message = $"导入完成:MC {mc}{loaderText}\n已复制实例 {prismRoot} 的游戏文件,原文件未改动。" +
                             (string.IsNullOrEmpty(tuningNote) ? "" : "\n" + tuningNote);
            Report("导入完成");
            return result;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[目录导入] Prism 实例导入失败:{ex}");
            if (inst != null) Rollback(inst.Id);
            result.Ok = false;
            result.Instance = null;
            result.Message = StorageGuardService.ClassifyIoError(ex, "Prism 实例导入");
            return result;
        }
    }

    /// <summary>手动指定版本用的候选列表:本地已有版本 + 官方清单正式版</summary>
    public async Task<List<string>> GetKnownVersionsAsync()
    {
        var list = new List<string>();
        try
        {
            if (Directory.Exists(AppPaths.Versions))
                list.AddRange(Directory.GetDirectories(AppPaths.Versions)
                                       .Select(Path.GetFileName)!);
        }
        catch { /* 本地扫描失败不阻断 */ }
        try
        {
            var manifest = await _manifestService.FetchManifestAsync(false);
            if (manifest?.Versions != null)
                list.AddRange(manifest.Versions.Where(v => v.Type == "release").Take(400).Select(v => v.Id));
        }
        catch (Exception ex) { App.WriteAppLog($"[目录导入] 获取官方版本清单失败:{ex.Message}"); }
        return list.Distinct().ToList();
    }

    /// <summary>定位游戏根目录(容忍 .minecraft 子层/单一顶层套壳/懒人包嵌套)</summary>
    private static string LocateMcRoot(string dir)
    {
        if (LooksLikeMcRoot(dir)) return dir;
        string mc = Path.Combine(dir, ".minecraft");
        if (LooksLikeMcRoot(mc)) return mc;
        try
        {
            var subs = Directory.GetDirectories(dir);
            if (subs.Length == 1 && LooksLikeMcRoot(subs[0])) return subs[0];
            foreach (var s in subs)
                if (LooksLikeMcRoot(s)) return s;
        }
        catch { }
        return dir;
    }

    private static bool LooksLikeMcRoot(string d) =>
        Directory.Exists(d) &&
        (Directory.Exists(Path.Combine(d, "versions")) ||
         Directory.Exists(Path.Combine(d, "mods")) ||
         File.Exists(Path.Combine(d, "options.txt")) ||
         Directory.Exists(Path.Combine(d, ".minecraft")));

    /// <summary>合成版本名取主版本段(如 1.12.2-forge14.23 → 1.12.2;首字符非数字则原样返回)</summary>
    private static string ExtractBaseVersion(string versionId)
    {
        if (string.IsNullOrEmpty(versionId)) return "";
        int dash = versionId.IndexOf('-');
        string head = dash > 0 ? versionId[..dash] : versionId;
        return head.Length > 0 && char.IsDigit(head[0]) ? head : versionId;
    }

    /// <summary>按模组 jar 文件名尽力推断加载器(不解压,失败不阻断)</summary>
    private static void DetectLoader(GameInstance inst, string instDir)
    {
        if (!string.IsNullOrEmpty(inst.ModLoader)) return;
        try
        {
            string modsDir = Path.Combine(instDir, "mods");
            if (!Directory.Exists(modsDir)) return;
            var names = Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.AllDirectories)
                                  .Take(2000).Select(f => Path.GetFileName(f) ?? "").ToList();
            if (names.Any(n => n.Contains("fabric", StringComparison.OrdinalIgnoreCase))) inst.ModLoader = "Fabric";
            else if (names.Any(n => n.Contains("quilt", StringComparison.OrdinalIgnoreCase))) inst.ModLoader = "Quilt";
            else if (names.Any(n => n.Contains("forge", StringComparison.OrdinalIgnoreCase))) inst.ModLoader = "Forge";
            if (inst.ModLoader != null) App.WriteAppLog($"[目录导入] 推断加载器:{inst.ModLoader}");
        }
        catch { /* 推断失败不阻断 */ }
    }

    /// <summary>带进度回调的整目录拷贝(枚举不跟随联接目录,避免物理目录重复拷贝)</summary>
    private static void CopyDirWithProgress(string srcDir, string destDir, Action<string> report)
    {
        var files = Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories).ToList();
        int total = files.Count, done = 0;
        foreach (var file in files)
        {
            string rel = Path.GetRelativePath(srcDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try { File.Copy(file, dst, overwrite: true); }
            catch (Exception ex) { App.WriteAppLog($"[目录导入] 复制失败 {rel}:{ex.Message}"); }
            done++;
            if (done % 200 == 0) report($"复制游戏文件…{done}/{total}");
        }
    }

    /// <summary>目录体积统计(异常容忍,失败按 0 处理)</summary>
    private static long DirSizeSafe(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                            .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0L; }
    }

    /// <summary>目录拷贝(overrides → 游戏版本目录);skipMeta 跳过清单文件本身</summary>
    private static int CopyDirInto(string srcDir, string destDir, bool skipMeta)
    {
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcDir, file);
            if (skipMeta && rel is "manifest.json" or "modrinth.index.json" or "mmc-pack.json" or "instance.cfg")
                continue;
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
            count++;
        }
        return count;
    }

    /// <summary>懒人包单一顶层目录提升(包内整体套了一层文件夹时自动展开)</summary>
    private static void PromoteSingleRoot(string instDir)
    {
        try
        {
            var dirs = Directory.GetDirectories(instDir);
            var files = Directory.GetFiles(instDir);
            if (files.Length > 0 || dirs.Length != 1) return;
            string root = dirs[0];
            // 只有当子目录看起来像游戏根目录时才提升
            bool looksLikeMc = Directory.Exists(Path.Combine(root, "versions")) ||
                               Directory.Exists(Path.Combine(root, "mods")) ||
                               File.Exists(Path.Combine(root, "options.txt"));
            if (!looksLikeMc) return;
            foreach (var d in Directory.GetDirectories(root))
                Directory.Move(d, Path.Combine(instDir, Path.GetFileName(d)));
            foreach (var f in Directory.GetFiles(root))
                File.Move(f, Path.Combine(instDir, Path.GetFileName(f)));
            Directory.Delete(root);
        }
        catch (Exception ex) { App.WriteAppLog($"[整合包] 顶层目录提升失败(保持原样):{ex.Message}"); }
    }

    /// <summary>剔除懒人包内残留的外来启动器文件(2026-09-26 实测):
    /// 顶层启动器目录 + 启动器 exe + 其 ini/launcher_profiles.json + 版本目录内同类残留</summary>
    private static void RemovePackJunk(string instDir)
    {
        TryDeleteDir(Path.Combine(instDir, "PCL"));
        TryDeleteFile(Path.Combine(instDir, "Plain Craft Launcher 2.exe"));
        string mcDir = Directory.Exists(Path.Combine(instDir, ".minecraft"))
            ? Path.Combine(instDir, ".minecraft") : instDir;
        TryDeleteFile(Path.Combine(mcDir, "PCL.ini"));
        TryDeleteFile(Path.Combine(mcDir, "launcher_profiles.json"));
        string versionsDir = Path.Combine(mcDir, "versions");
        if (Directory.Exists(versionsDir))
            foreach (var vd in Directory.GetDirectories(versionsDir))
                TryDeleteDir(Path.Combine(vd, "PCL"));
    }

    /// <summary>版本隔离懒人包展平(2026-09-26):部分第三方包开版本隔离时 mods/config/saves 全在
    /// versions\<完整ID>\ 里,不展平则导入后模组丢失。把游戏内容子目录合并到游戏根,
    /// versions 目录本体留给 EnsureInstanceLayout 上提全局(版本 json 随行)。</summary>
    private static void FlattenVersionIsolation(string instDir, ZipProbe probe)
    {
        if (string.IsNullOrEmpty(probe.VersionId)) return;
        string mcDir = Directory.Exists(Path.Combine(instDir, ".minecraft"))
            ? Path.Combine(instDir, ".minecraft") : instDir;
        string vdir = Path.Combine(mcDir, "versions", probe.VersionId);
        if (!Directory.Exists(vdir)) return;
        foreach (var sub in new[] { "mods", "config", "saves", "resourcepacks", "shaderpacks",
                                    "datapacks", "defaultconfigs", "serverconfig" })
        {
            string s = Path.Combine(vdir, sub);
            if (!Directory.Exists(s)) continue;
            string dst = Path.Combine(mcDir, sub);
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(s, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(s, f);
                string d = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                try { File.Copy(f, d, overwrite: true); }
                catch (Exception ex) { App.WriteAppLog($"[整合包] 版本隔离展平复制失败 {rel}:{ex.Message}"); }
            }
            TryDeleteDir(s);
        }
        // 版本目录顶层文件(options.txt 等)上移;外来启动器残留跳过
        foreach (var f in Directory.EnumerateFiles(vdir))
        {
            string name = Path.GetFileName(f);
            if (name.Contains("PCL", StringComparison.OrdinalIgnoreCase)) continue;
            string d = Path.Combine(mcDir, name);
            try { File.Copy(f, d, overwrite: true); }
            catch (Exception ex) { App.WriteAppLog($"[整合包] 版本隔离顶层文件上移失败 {name}:{ex.Message}"); }
        }
        App.WriteAppLog($"[整合包] 版本隔离已展平:{probe.VersionId} → 游戏根");
    }

    /// <summary>懒人包版本目录落盘复核:解压后以磁盘实际内容为准修正 probe.VersionId/Mc/加载器。
    /// 探测阶段仅凭中央目录名推断,套壳/杂项目录可能猜错;猜错会导致误判「包内无完整版本」
    /// 而重复下载原版本体与加载器,正是导入后仍要「补全」的根因之一。</summary>
    private static void ConfirmLazyVersionFromDisk(string instDir, ZipProbe probe)
    {
        try
        {
            string mcDir = Directory.Exists(Path.Combine(instDir, ".minecraft"))
                ? Path.Combine(instDir, ".minecraft") : instDir;
            string versionsDir = Path.Combine(mcDir, "versions");
            if (!Directory.Exists(versionsDir)) return;
            var names = Directory.GetDirectories(versionsDir)
                                 .Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n))
                                 .Select(n => n!).ToList();
            if (names.Count == 0) return;

            // 当前认定有效则保持不动
            if (!string.IsNullOrEmpty(probe.VersionId))
            {
                string cur = Path.Combine(versionsDir, probe.VersionId);
                if (File.Exists(Path.Combine(cur, probe.VersionId + ".json")) ||
                    File.Exists(Path.Combine(cur, probe.VersionId + ".jar"))) return;
            }

            // 优先选含 <同名>.json/.jar 的真实版本目录,其次数字开头,最后第一个
            string? best = names.FirstOrDefault(n =>
                    File.Exists(Path.Combine(versionsDir, n, n + ".json")) ||
                    File.Exists(Path.Combine(versionsDir, n, n + ".jar")))
                ?? names.FirstOrDefault(n => char.IsDigit(n[0]))
                ?? names[0];
            if (best == null || best == probe.VersionId) return;

            string old = probe.VersionId;
            probe.VersionId = best;
            probe.Mc = ExtractBaseVersion(best);
            ApplyLoaderFromVersionName(probe, best);
            App.WriteAppLog($"[整合包] 版本目录落盘复核:\"{old}\" → \"{best}\"(MC {probe.Mc} 加载器 {probe.Loader} {probe.LoaderVer})");
        }
        catch (Exception ex) { App.WriteAppLog($"[整合包] 版本目录落盘复核失败(保持原判):{ex.Message}"); }
    }

    /// <summary>导入失败回滚:删除游戏版本目录 + 物理 mods/saves 目录</summary>
    private void Rollback(string instanceId)
    {
        try
        {
            string instDir = _instanceService.GetInstanceDir(instanceId);
            if (Directory.Exists(instDir)) Directory.Delete(instDir, recursive: true);
            TryDeleteDir(InstanceService.GetModsPhysicalDir(instanceId));
            TryDeleteDir(InstanceService.GetSavesPhysicalDir(instanceId));
            _instanceService.RefreshInstances();
            App.WriteAppLog($"[整合包] 已回滚游戏版本 {instanceId} 的全部残留");
        }
        catch (Exception ex) { App.WriteAppLog($"[整合包] 回滚清理失败:{ex.Message}"); }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { App.WriteAppLog($"[整合包] 删除临时目录失败 {dir}:{ex.Message}"); }
    }

    private static void TryDeleteFile(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception ex) { App.WriteAppLog($"[整合包] 删除临时文件失败 {file}:{ex.Message}"); }
    }
}
