// Copyright © FufuLauncher
//
// 整合包导出服务(LauncherX-6):
// 把当前游戏版本导出成一个整合包 zip,提供两种模式 ——
//   ① 仅用户内容:mods / config / options.txt / resourcepacks / shaderpacks / patches 等自己攒的东西;
//   ② 含游戏本体:在 ① 的基础上再带上 versions 版本本体、libraries 库文件、assets 资源文件,
//      导出的包拷到另一台机器上不联网也能直接跑。
//
// 实现要点:
// 1. 直接用 ZipArchive 流式写入,不落中间暂存目录 —— 大型整合包本体动辄几个 GB,
//    先复制再压缩会把磁盘占用瞬间翻倍;
// 2. 实例目录里的 saves / mods 是联接(Junction),遍历时跳过,改从规范物理目录单独取,
//    否则 zip 里会出现重复内容甚至递归死循环;
// 3. 存档默认不打包(体积大且属于玩家隐私数据),由 includeSaves 显式开启;
// 4. 日志、崩溃报告、.partial 残片、.old 备份一律排除;
// 5. 包内附 fufu-pack.json 清单,记录游戏版本 / 加载器 / 内存 / 窗口 / 导出模式,
//    既方便用户看,也让本服务能把自家导出的包重新导入回来。

using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FufuLauncher.Services;

/// <summary>导出模式</summary>
public enum ModPackExportMode
{
    /// <summary>只导出模组、配置、资源这类用户自己的内容(包小,需要联网补游戏本体)</summary>
    UserContentOnly,
    /// <summary>连带游戏本体、库文件、资源文件一起导出(包大,拷到别的机器离线可用)</summary>
    WithGameCore
}

/// <summary>导出选项</summary>
public sealed class ModPackExportOptions
{
    public ModPackExportMode Mode { get; set; } = ModPackExportMode.UserContentOnly;
    /// <summary>是否连存档一起打包(默认关:存档大且属于玩家数据)</summary>
    public bool IncludeSaves { get; set; }
    /// <summary>是否连禁用的模组(.disabled)一起打包(默认开,保证还原后状态一致)</summary>
    public bool IncludeDisabledMods { get; set; } = true;
    public string ModeDisplay => Mode == ModPackExportMode.WithGameCore ? "含游戏本体(离线可用)" : "仅用户内容(体积小)";
}

/// <summary>导出结果</summary>
public sealed class ModPackExportResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string ZipPath { get; set; } = "";
    public int FileCount { get; set; }
    public long WrittenBytes { get; set; }
    public long ZipBytes { get; set; }
    public int SkippedCount { get; set; }
    public ModPackExportMode Mode { get; set; }
    public TimeSpan Elapsed { get; set; }
    public string Detail =>
        $"打包了 {FileCount} 个文件(原始 {StorageGuardService.FmtSize(WrittenBytes)} → " +
        $"压缩包 {StorageGuardService.FmtSize(ZipBytes)}),跳过 {SkippedCount} 个,耗时 {Elapsed.TotalSeconds:0.0} 秒。";
}

/// <summary>包内清单(fufu-pack.json)</summary>
public sealed class FufuPackManifest
{
    [JsonPropertyName("packFormat")] public int PackFormat { get; set; } = 1;
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("exportedAt")] public DateTime ExportedAt { get; set; }
    [JsonPropertyName("mode")] public string Mode { get; set; } = "UserContentOnly";
    [JsonPropertyName("minecraftVersion")] public string MinecraftVersion { get; set; } = "";
    [JsonPropertyName("modLoader")] public string ModLoader { get; set; } = "";
    [JsonPropertyName("modLoaderVersion")] public string ModLoaderVersion { get; set; } = "";
    [JsonPropertyName("loaderVersionId")] public string LoaderVersionId { get; set; } = "";
    [JsonPropertyName("javaMajorVersion")] public int JavaMajorVersion { get; set; } = 17;
    [JsonPropertyName("xms")] public int Xms { get; set; }
    [JsonPropertyName("xmx")] public int Xmx { get; set; }
    [JsonPropertyName("useCustomMemory")] public bool UseCustomMemory { get; set; }
    [JsonPropertyName("extraJvmArgs")] public string ExtraJvmArgs { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("fullscreen")] public bool Fullscreen { get; set; }
    [JsonPropertyName("hasGameCore")] public bool HasGameCore { get; set; }
    [JsonPropertyName("hasSaves")] public bool HasSaves { get; set; }
    [JsonPropertyName("fileCount")] public int FileCount { get; set; }
    [JsonPropertyName("launcher")] public string Launcher { get; set; } = "FufuLauncher";
}

public sealed class ModPackExportService
{
    /// <summary>实例目录里不打包的东西</summary>
    private static readonly HashSet<string> ExcludeDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "crash-reports", "natives", "shaderpacks-cache", "local", "webcache",
        "modrinth-cache", "backups", ".fabric", ".quilt", "replay_recordings", "screenshots-tmp"
    };

    /// <summary>不打包的文件后缀 / 名字</summary>
    private static readonly string[] ExcludeFileSuffixes =
    {
        ".partial", ".old", ".tmp", ".disabled.bak", ".lock", ".log", ".log.gz"
    };

    private static readonly string[] ExcludeFileNames =
    {
        "instance.json.tmp", "options.txt.bak"
    };

    private readonly InstanceService _instances;
    private readonly StorageGuardService _storage;
    private readonly IntegrityRepairService _integrity;

    public ModPackExportService(InstanceService instances, StorageGuardService storage,
                                IntegrityRepairService integrity)
    {
        _instances = instances;
        _storage = storage;
        _integrity = integrity;
    }

    /// <summary>是否有导出正在进行</summary>
    public bool IsBusy { get; private set; }

    // ==================== 导出 ====================

    /// <summary>
    /// 导出实例为整合包 zip。progress 收到的是中文进度文案。
    /// </summary>
    public async Task<ModPackExportResult> ExportAsync(
        string instanceId, string outputZipPath, ModPackExportOptions options,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = new ModPackExportResult { Mode = options?.Mode ?? ModPackExportMode.UserContentOnly };
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null)
        {
            res.Message = "找不到这个游戏版本,请刷新后重试。";
            return res;
        }
        options ??= new ModPackExportOptions();

        string instDir = _instances.GetInstanceDir(instanceId);
        if (!Directory.Exists(instDir))
        {
            res.Message = "这个游戏版本的文件夹不存在,可能已经被删掉了。";
            return res;
        }

        IsBusy = true;
        try
        {
            string outDir = Path.GetDirectoryName(Path.GetFullPath(outputZipPath)) ?? AppPaths.Root;
            Directory.CreateDirectory(outDir);
            var pre = _storage.Precheck(outputZipPath, 256L * 1024 * 1024);
            if (pre.Result != StorageCheckResult.Ok)
            {
                res.Message = "导出前存储检查没通过:" + pre.Message;
                return res;
            }

            // 先收集条目清单(源路径 → 包内路径),再统一写 zip,方便提前算数量与预估体积
            progress?.Report("正在清点要打包的文件…");
            var entries = await Task.Run(() => CollectEntries(inst, instDir, options), ct).ConfigureAwait(false);
            if (entries.Count == 0)
            {
                res.Message = "这个游戏版本里没有可导出的内容(模组、配置、资源都是空的)。";
                return res;
            }

            long estBytes = entries.Sum(e => e.Size);
            if (options.Mode == ModPackExportMode.WithGameCore)
            {
                progress?.Report("正在清点游戏本体 / 库 / 资源文件(这一步会慢一点)…");
                var core = await _integrity.BuildChecklistAsync(instanceId, ct).ConfigureAwait(false);
                if (core != null)
                {
                    foreach (var t in core)
                    {
                        if (!File.Exists(t.LocalPath)) continue;
                        string? rel = RelativeToRoot(t.LocalPath);
                        if (rel == null) continue;
                        entries.Add(new PackEntry { Source = t.LocalPath, EntryName = "gamecore/" + rel.Replace('\\', '/'), Size = t.Size });
                    }
                    estBytes += core.Where(t => File.Exists(t.LocalPath)).Sum(t => Math.Max(0, t.Size));
                }
                else
                {
                    progress?.Report("游戏本体文件还没装好,本次只导出用户内容。");
                }
            }

            var manifest = BuildManifest(inst, options, entries.Count);
            entries.Insert(0, new PackEntry
            {
                Source = null,
                EntryName = "fufu-pack.json",
                Size = 0,
                Inline = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true })
            });

            // 磁盘空间再按真实体积预检一次(压缩率保守按 0.9 估)
            var pre2 = _storage.Precheck(outputZipPath, (long)(estBytes * 0.9));
            if (pre2.Result != StorageCheckResult.Ok)
            {
                res.Message = $"磁盘空间不够放下这个整合包(预计 {StorageGuardService.FmtSize(estBytes)}):{pre2.Message}";
                return res;
            }

            progress?.Report($"正在压缩 {entries.Count} 个文件…");
            string tmp = outputZipPath + ".partial";
            if (File.Exists(tmp)) File.Delete(tmp);

            int done = 0;
            long written = 0;
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seen.Add(e.EntryName)) { res.SkippedCount++; continue; }
                    try
                    {
                        if (!await WriteEntryAsync(zip, e, ct).ConfigureAwait(false)) { res.SkippedCount++; continue; }
                        written += e.Size;
                        res.FileCount++;
                    }
                    catch (Exception ex)
                    {
                        res.SkippedCount++;
                        App.WriteAppLog($"[整合包导出] 跳过 {e.Source}:{ex.Message}");
                        continue;
                    }
                    done++;
                    if (done % 40 == 0)
                        progress?.Report($"正在压缩… {done}/{entries.Count}({StorageGuardService.FmtSize(written)})");
                }
            }

            File.Move(tmp, outputZipPath, overwrite: true);
            res.Ok = true;
            res.ZipPath = outputZipPath;
            res.WrittenBytes = written;
            try { res.ZipBytes = new FileInfo(outputZipPath).Length; } catch { }
            sw.Stop();
            res.Elapsed = sw.Elapsed;
            res.Message = $"整合包已导出到 {outputZipPath}\n{res.Detail}";
            App.WriteAppLog($"[整合包导出] ✓ {inst.Name} → {outputZipPath}({options.ModeDisplay},{res.Detail})");
            return res;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            res.Elapsed = sw.Elapsed;
            res.Message = "导出已取消。";
            return res;
        }
        catch (Exception ex)
        {
            sw.Stop();
            res.Elapsed = sw.Elapsed;
            res.Message = "导出失败:" + ex.Message;
            App.WriteAppLog($"[整合包导出] ✗ {instanceId}:{ex}");
            return res;
        }
        finally { IsBusy = false; }
    }

    /// <summary>写一个 zip 条目;返回 false 表示源文件不可用(跳过)</summary>
    private static async Task<bool> WriteEntryAsync(ZipArchive zip, PackEntry e, CancellationToken ct)
    {
        var entry = zip.CreateEntry(e.EntryName, CompressionLevel.Optimal);
        await using var dst = entry.Open();
        if (e.Inline != null)
        {
            var bytes = Encoding.UTF8.GetBytes(e.Inline);
            await dst.WriteAsync(bytes, ct).ConfigureAwait(false);
            return true;
        }
        if (e.Source == null || !File.Exists(e.Source)) return false;
        await using var src = new FileStream(e.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        return true;
    }

    // ==================== 条目收集 ====================

    private sealed class PackEntry
    {
        public string? Source { get; set; }
        public string EntryName { get; set; } = "";
        public long Size { get; set; }
        /// <summary>内联文本内容(清单文件用,不落磁盘临时文件)</summary>
        public string? Inline { get; set; }
    }

    private List<PackEntry> CollectEntries(GameInstance inst, string instDir, ModPackExportOptions options)
    {
        var entries = new List<PackEntry>();

        // 1) 实例目录本体(config / options.txt / resourcepacks / shaderpacks / patches / instance.json …)
        AddTree(entries, instDir, "overrides", instDir, skipJunctions: true);

        // 2) 模组(物理目录,经联接透传)
        string modsDir = _instances.GetModsDir(inst.Id);
        if (Directory.Exists(modsDir))
        {
            foreach (string f in Directory.EnumerateFiles(modsDir))
            {
                string name = Path.GetFileName(f);
                bool disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                if (disabled && !options.IncludeDisabledMods) continue;
                if (!IsPackable(f, name)) continue;
                entries.Add(MakeEntry(f, $"overrides/mods/{name.Replace('\\', '/')}"));
            }
            // 模组的子目录(有些整合包会往 mods 里放 1.20.1/ 之类的子文件夹)
            foreach (string d in Directory.EnumerateDirectories(modsDir))
                if (!JunctionHelper.IsJunction(d)) AddTree(entries, d, "overrides/mods", modsDir, false);
        }

        // 3) 存档(显式开启才打)
        if (options.IncludeSaves)
        {
            string savesDir = _instances.GetSavesDir(inst.Id);
            if (Directory.Exists(savesDir)) AddTree(entries, savesDir, "saves", savesDir, false);
        }

        return entries;
    }

    private void AddTree(List<PackEntry> entries, string srcDir, string prefix, string baseDir, bool skipJunctions)
    {
        if (!Directory.Exists(srcDir)) return;
        var stack = new Stack<string>();
        stack.Push(srcDir);
        while (stack.Count > 0)
        {
            string cur = stack.Pop();
            foreach (string f in SafeFiles(cur))
            {
                string name = Path.GetFileName(f);
                if (!IsPackable(f, name)) continue;
                string rel = Path.GetRelativePath(baseDir, f).Replace('\\', '/');
                entries.Add(MakeEntry(f, $"{prefix}/{rel}"));
            }
            foreach (string d in SafeDirs(cur))
            {
                if (skipJunctions && JunctionHelper.IsJunction(d)) continue;
                string dn = Path.GetFileName(d);
                // 实例根目录下的日志/崩溃报告等排除;子目录里同名的照样打包
                if (string.Equals(cur.TrimEnd('\\', '/'), baseDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) &&
                    ExcludeDirs.Contains(dn)) continue;
                stack.Push(d);
            }
        }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir); }
        catch (Exception ex) { App.WriteAppLog($"[整合包导出] 枚举文件失败 {dir}:{ex.Message}"); return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch (Exception ex) { App.WriteAppLog($"[整合包导出] 枚举目录失败 {dir}:{ex.Message}"); return Array.Empty<string>(); }
    }

    private static PackEntry MakeEntry(string source, string entryName)
    {
        long size = 0;
        try { size = new FileInfo(source).Length; } catch { }
        return new PackEntry { Source = source, EntryName = entryName, Size = size };
    }

    private static bool IsPackable(string fullPath, string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        if (ExcludeFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)) return false;
        foreach (string suf in ExcludeFileSuffixes)
            if (fileName.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) return false;
        // .disabled 是"被禁用的模组",要保留(它不是垃圾后缀),单独放行
        if (fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch { return false; }   // 被占用/无权限的直接跳过,不因为一个文件毁掉整次导出
    }

    private FufuPackManifest BuildManifest(GameInstance inst, ModPackExportOptions options, int fileCount) => new()
    {
        Name = inst.Name,
        Version = DateTime.Now.ToString("yyyy.MM.dd-HHmm"),
        ExportedAt = DateTime.Now,
        Mode = options.Mode.ToString(),
        MinecraftVersion = inst.VersionId ?? "",
        ModLoader = inst.ModLoader ?? "",
        ModLoaderVersion = inst.ModLoaderVersion ?? "",
        LoaderVersionId = inst.LoaderVersionId ?? "",
        JavaMajorVersion = inst.JavaMajorVersion,
        Xms = inst.Xms,
        Xmx = inst.Xmx,
        UseCustomMemory = inst.UseCustomMemory,
        ExtraJvmArgs = inst.ExtraJvmArgs ?? "",
        Width = inst.Width,
        Height = inst.Height,
        Fullscreen = inst.Fullscreen,
        HasGameCore = options.Mode == ModPackExportMode.WithGameCore,
        HasSaves = options.IncludeSaves,
        FileCount = fileCount
    };

    /// <summary>把绝对路径转成相对启动器数据根的路径(gamecore/ 下的条目名用)</summary>
    private static string? RelativeToRoot(string fullPath)
    {
        string[] roots = { AppPaths.Versions, AppPaths.Libraries, AppPaths.Assets };
        string[] names = { "versions", "libraries", "assets" };
        for (int i = 0; i < roots.Length; i++)
        {
            if (fullPath.StartsWith(roots[i], StringComparison.OrdinalIgnoreCase))
                return Path.Combine(names[i], Path.GetRelativePath(roots[i], fullPath));
        }
        return null;
    }

    // ==================== 导入(自家导出的包) ====================

    /// <summary>判断一个 zip 是不是本服务导出的整合包(含 fufu-pack.json)</summary>
    public static bool IsFufuPack(string zipPath)
    {
        try
        {
            if (!File.Exists(zipPath)) return false;
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            return zip.GetEntry("fufu-pack.json") != null;
        }
        catch { return false; }
    }

    /// <summary>读取包内清单(不是本服务的包返回 null)</summary>
    public static FufuPackManifest? ReadManifest(string zipPath)
    {
        try
        {
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = zip.GetEntry("fufu-pack.json");
            if (entry == null) return null;
            using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
            return JsonSerializer.Deserialize<FufuPackManifest>(sr.ReadToEnd());
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[整合包导入] 清单读取失败 {zipPath}:{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把本服务导出的整合包导入成新实例。
    /// 含游戏本体的包直接把 versions / libraries / assets 落回共享目录;
    /// 不含本体的包只建实例骨架,提示用户去下载中心补装版本。
    /// </summary>
    public async Task<(bool Ok, GameInstance? Instance, string Message)> ImportFufuPackAsync(
        string zipPath, string? customName, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var manifest = ReadManifest(zipPath);
        if (manifest == null)
            return (false, null, "这个压缩包不是本启动器导出的整合包(找不到 fufu-pack.json)。" +
                                "如果是 Modrinth / CurseForge 的整合包,请用「导入整合包」功能。");

        string name = string.IsNullOrWhiteSpace(customName)
            ? (string.IsNullOrEmpty(manifest.Name) ? Path.GetFileNameWithoutExtension(zipPath) : manifest.Name)
            : customName!.Trim();

        try
        {
            progress?.Report("正在创建新的游戏版本…");
            string mcVersion = manifest.MinecraftVersion;
            var inst = _instances.CreateInstance(name, mcVersion,
                manifest.JavaMajorVersion > 0 ? manifest.JavaMajorVersion : JavaRuntimeService.RecommendJavaMajor(mcVersion));
            if (inst == null) return (false, null, "创建游戏版本失败,请检查磁盘空间后重试。");

            // 回写实例设置
            inst.ModLoader = string.IsNullOrEmpty(manifest.ModLoader) ? null : manifest.ModLoader;
            inst.ModLoaderVersion = manifest.ModLoaderVersion;
            inst.LoaderVersionId = string.IsNullOrEmpty(manifest.LoaderVersionId) ? null : manifest.LoaderVersionId;
            inst.Xms = manifest.Xms > 0 ? manifest.Xms : inst.Xms;
            inst.Xmx = manifest.Xmx > 0 ? manifest.Xmx : inst.Xmx;
            inst.UseCustomMemory = manifest.UseCustomMemory;
            inst.ExtraJvmArgs = manifest.ExtraJvmArgs;
            if (manifest.Width > 0) inst.Width = manifest.Width;
            if (manifest.Height > 0) inst.Height = manifest.Height;
            inst.Fullscreen = manifest.Fullscreen;

            string instDir = _instances.GetInstanceDir(inst.Id);
            string modsDir = _instances.GetModsDir(inst.Id);
            string savesDir = _instances.GetSavesDir(inst.Id);
            Directory.CreateDirectory(instDir);
            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(savesDir);

            int userFiles = 0, coreFiles = 0;
            await using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                int total = zip.Entries.Count, idx = 0;
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    idx++;
                    if (idx % 50 == 0) progress?.Report($"正在解压文件 {idx}/{total}");
                    if (entry.Length == 0 && entry.Name.Length == 0) continue;

                    // 包内布局:gamecore/ = 共享游戏本体;overrides/ = 实例目录;
                    //            overrides/mods/ 单独落到规范 mods\{id} 物理目录
                    //            (实例目录里的 mods 是联接,写进去等于写物理目录,但分开处理更清楚);
                    //            saves/ = 规范 saves\{id} 物理目录
                    string dest;
                    bool isCore;
                    string fullName = entry.FullName.Replace('\\', '/');
                    if (fullName.StartsWith("gamecore/", StringComparison.OrdinalIgnoreCase))
                    {
                        dest = ResolveGameCorePath(fullName["gamecore/".Length..]) ?? "";
                        isCore = true;
                    }
                    else if (fullName.StartsWith("overrides/mods/", StringComparison.OrdinalIgnoreCase))
                    {
                        dest = SafeUnder(fullName["overrides/mods/".Length..], modsDir);
                        isCore = false;
                    }
                    else if (fullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                    {
                        dest = SafeUnder(fullName["overrides/".Length..], instDir);
                        isCore = false;
                    }
                    else if (fullName.StartsWith("saves/", StringComparison.OrdinalIgnoreCase))
                    {
                        dest = SafeUnder(fullName["saves/".Length..], savesDir);
                        isCore = false;
                    }
                    else continue;
                    if (string.IsNullOrEmpty(dest)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await using var es = entry.Open();
                    await using var ds = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                    await es.CopyToAsync(ds, ct).ConfigureAwait(false);
                    if (isCore) coreFiles++; else userFiles++;
                }
            }

            _instances.SaveInstance(inst);
            _instances.RefreshInstances();

            bool needCore = !manifest.HasGameCore && coreFiles == 0;
            string msg = $"已导入整合包「{name}」:用户内容 {userFiles} 个文件" +
                         (coreFiles > 0 ? $",游戏本体 {coreFiles} 个文件。" : "。") +
                         (needCore ? "\n这个包不含游戏本体,请到「下载中心」把游戏版本 " + mcVersion +
                                     (string.IsNullOrEmpty(manifest.ModLoader) ? "" : $" 与 {manifest.ModLoader} 加载器") +
                                     " 装好再启动。" : "");
            App.WriteAppLog($"[整合包导入] ✓ {zipPath} → {name}({msg})");
            return (true, inst, msg);
        }
        catch (OperationCanceledException)
        {
            return (false, null, "导入已取消。");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[整合包导入] ✗ {zipPath}:{ex}");
            return (false, null, "导入失败:" + ex.Message);
        }
    }

    /// <summary>包内相对路径 → 目标目录下的安全绝对路径(zip-slip 防护,非法返回 "")</summary>
    private static string SafeUnder(string rel, string baseDir)
        => Next.Foundation.NextPaths.SanitizeEntryPath(rel, baseDir) ?? "";

    /// <summary>gamecore/ 下的条目还原回共享目录的真实路径(带路径穿越防护)</summary>
    private static string? ResolveGameCorePath(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return null;
        string[] prefixes = { "versions", "libraries", "assets" };
        string[] roots = { AppPaths.Versions, AppPaths.Libraries, AppPaths.Assets };
        for (int i = 0; i < prefixes.Length; i++)
        {
            if (!rel.StartsWith(prefixes[i] + "/", StringComparison.OrdinalIgnoreCase)) continue;
            string root = roots[i];
            string? safe = Next.Foundation.NextPaths.SanitizeEntryPath(rel, root);
            return string.IsNullOrEmpty(safe) ? null : safe;
        }
        return null;
    }
}
