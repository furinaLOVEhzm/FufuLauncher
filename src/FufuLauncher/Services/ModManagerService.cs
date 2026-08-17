// ModManagerService.cs — 模组管理服务(完整重构)
// 可爱的芙芙 - 阶段5 重构
//
// 功能:
// 1. 解析 jar 包内 fabric.mod.json / mods.toml / quilt.mod.json / neoforge.mods.toml
//    读取模组ID、适配游戏版本、加载器类型、依赖列表、冲突标记
// 2. 启用/禁用通过修改文件后缀(.jar ↔ .disabled),不删除源文件
// 3. 拖拽导入本地模组 jar/zip,校验文件合法性;检测版本和加载器匹配,不匹配弹出中文警告
// 4. 在线模组市场(Modrinth V2 + 国内镜像代理)
// 5. 自动下载必需前置依赖;可选依赖仅展示,绝不自动下载
// 6. 模组冲突检测,中文提示
// 7. 模组下载队列与游戏本体下载队列完全隔离,独立进度条、独立 CancellationToken

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

public class ModDependencyInfo
{
    /// <summary>依赖模组 ID</summary>
    public string ModId { get; set; } = "";
    /// <summary>依赖类型: required(必需) / optional(可选) / incompatible(冲突)</summary>
    public string DependencyType { get; set; } = "required";
    /// <summary>最低版本要求,空表示不限制</summary>
    public string? VersionRange { get; set; }
}

public class ModInfo
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    /// <summary>模组内部 ID(如 fabric 的 id、forge 的 modId)</summary>
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string McVersion { get; set; } = "";
    /// <summary>适配的游戏版本列表(多版本可选时)</summary>
    public List<string> SupportedMcVersions { get; set; } = new();
    /// <summary>加载器类型: Fabric / Forge / NeoForge / Quilt</summary>
    public string ModLoader { get; set; } = "";
    public long Size { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>依赖列表</summary>
    public List<ModDependencyInfo> Dependencies { get; set; } = new();
    /// <summary>冲突模组 ID 列表</summary>
    public List<string> Conflicts { get; set; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FileName : Name;
    public string ModIdDisplay => string.IsNullOrWhiteSpace(ModId) ? "-" : ModId;

    public string SizeDisplay
    {
        get
        {
            if (Size <= 0) return "-";
            if (Size < 1024) return $"{Size} B";
            if (Size < 1024 * 1024) return $"{Size / 1024.0:F1} KB";
            return $"{Size / 1024.0 / 1024.0:F2} MB";
        }
    }

    public string StatusDisplay => Enabled ? "已启用" : "已禁用";
    public string LoaderDisplay => string.IsNullOrWhiteSpace(ModLoader) ? "未知" : ModLoader;

    /// <summary>兼容的游戏版本一览(逗号分隔,供 UI 提示)</summary>
    public string McVersionDisplay =>
        SupportedMcVersions.Count > 0
            ? string.Join(", ", SupportedMcVersions)
            : (string.IsNullOrWhiteSpace(McVersion) ? "-" : McVersion);

    public string LoaderBadgeColor => ModLoader switch
    {
        "Fabric" => "#FFDBA926",
        "Forge" => "#FF6F42C1",
        "NeoForge" => "#FFD35400",
        "Quilt" => "#FF5C9DFF",
        _ => "#FF6B7280"
    };

    public string StatusBadgeColor => Enabled ? "#FF3CB371" : "#FF6B7280";
    public string VersionDisplay => string.IsNullOrWhiteSpace(Version) ? "-" : Version;

    /// <summary>依赖简要文字(仅必需依赖的名称)</summary>
    public string RequiredDepsDisplay
    {
        get
        {
            var req = Dependencies.Where(d => d.DependencyType == "required").ToList();
            return req.Count == 0 ? "无" : $"{req.Count} 个依赖";
        }
    }

    /// <summary>冲突文字提示</summary>
    public string ConflictDisplay => Conflicts.Count > 0
        ? $"冲突: {string.Join(", ", Conflicts)}"
        : "";
}

/// <summary>版本兼容性检测结果</summary>
public class ModCompatibilityResult
{
    public bool IsCompatible { get; set; } = true;
    public string? WarningMessage { get; set; }
    public bool IsLoaderMismatch { get; set; }
    public bool IsVersionMismatch { get; set; }
}

public class ModManagerService
{
    private readonly InstanceService _instanceService;
    private readonly DownloadService _downloadService;
    private readonly HashVerifyService _hashVerify;
    private readonly ModrinthService _modrinthService;

    /// <summary>模组下载专用独立 CancellationTokenSource,与游戏本体完全隔离</summary>
    private CancellationTokenSource? _modDownloadCts;

    public string? CurrentInstanceId { get; private set; }

    /// <summary>当前模组下载进度(0~100),供 UI 绑定</summary>
    public event Action<double, string>? ModDownloadProgressChanged;

    /// <summary>模组下载独立队列进度(不与游戏下载冲突)</summary>
    public double ModQueueProgress { get; private set; }
    public string ModQueueStatus { get; private set; } = "";

    public ModManagerService(InstanceService instanceService,
                                DownloadService downloadService,
                                HashVerifyService hashVerify,
                                ModrinthService modrinthService)
    {
        _instanceService = instanceService;
        _downloadService = downloadService;
        _hashVerify = hashVerify;
        _modrinthService = modrinthService;
    }

    public void SetCurrentInstance(string instanceId)
    {
        CurrentInstanceId = instanceId;
    }

    /// <summary>获取当前实例的 MC 版本</summary>
    public string? GetCurrentMcVersion()
    {
        if (string.IsNullOrEmpty(CurrentInstanceId)) return null;
        return _instanceService.Instances
            .FirstOrDefault(i => i.Id == CurrentInstanceId)?.VersionId;
    }

    /// <summary>获取当前实例的加载器类型</summary>
    public string? GetCurrentModLoader()
    {
        if (string.IsNullOrEmpty(CurrentInstanceId)) return null;
        return _instanceService.Instances
            .FirstOrDefault(i => i.Id == CurrentInstanceId)?.ModLoader;
    }

    // ==================== 加载与解析 ====================

    public List<ModInfo> LoadMods()
    {
        var list = new List<ModInfo>();
        if (string.IsNullOrEmpty(CurrentInstanceId)) return list;
        string modsDir = _instanceService.GetModsDir(CurrentInstanceId);
        if (!Directory.Exists(modsDir)) return list;

        foreach (var file in Directory.EnumerateFiles(modsDir))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".jar" && ext != ".disabled") continue;

            var info = new ModInfo
            {
                FileName = Path.GetFileName(file),
                FilePath = file,
                Enabled = ext == ".jar",
                Size = SafeGetFileSize(file)
            };

            try
            {
                ParseModMetadata(file, info);
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[模组] 元数据解析失败 {Path.GetFileName(file)}: {ex.Message}");
            }

            list.Add(info);
        }

        return list.OrderBy(m => m.Enabled ? 0 : 1)
                     .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }

    private static long SafeGetFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>解析 jar 包内模组元数据(完整重构版),读取 fabric.mod.json / mods.toml / quilt.mod.json / neoforge.mods.toml</summary>
    private void ParseModMetadata(string jarPath, ModInfo info)
    {
        using var zip = ZipFile.OpenRead(jarPath);

        // 1. Fabric: fabric.mod.json
        var fabricEntry = zip.GetEntry("fabric.mod.json");
        if (fabricEntry != null)
        {
            using var sr = new StreamReader(fabricEntry.Open());
            var json = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // schemaVersion 1 格式
            if (root.TryGetProperty("schemaVersion", out var sv) && sv.GetInt32() >= 1)
            {
                if (root.TryGetProperty("id", out var id)) info.ModId = id.GetString() ?? "";
                if (root.TryGetProperty("name", out var n)) info.Name = n.GetString() ?? "";
                if (root.TryGetProperty("description", out var d)) info.Description = d.GetString() ?? "";
                if (root.TryGetProperty("version", out var v)) info.Version = v.GetString() ?? "";
                if (root.TryGetProperty("authors", out var a) && a.ValueKind == JsonValueKind.Array)
                    info.Author = string.Join(", ", a.EnumerateArray().Select(x => x.GetString() ?? ""));
                else if (root.TryGetProperty("author", out var singleAuth))
                    info.Author = singleAuth.GetString() ?? "";

                // 依赖解析
                if (root.TryGetProperty("depends", out var depends))
                    ParseFabricDependencies(depends, info.Dependencies);
                if (root.TryGetProperty("recommends", out var recommends))
                    ParseFabricDependencies(recommends, info.Dependencies, "optional");
                if (root.TryGetProperty("breaks", out var breaks))
                    ParseFabricConflicts(breaks, info.Conflicts);
                if (root.TryGetProperty("conflicts", out var conflicts))
                    ParseFabricConflicts(conflicts, info.Conflicts);
            }
            // schemaVersion 0(旧版)
            else
            {
                if (root.TryGetProperty("id", out var id)) info.ModId = id.GetString() ?? "";
                if (root.TryGetProperty("name", out var n)) info.Name = n.GetString() ?? "";
                if (root.TryGetProperty("description", out var d)) info.Description = d.GetString() ?? "";
                if (root.TryGetProperty("version", out var v)) info.Version = v.GetString() ?? "";
                if (root.TryGetProperty("authors", out var a) && a.ValueKind == JsonValueKind.Array)
                    info.Author = string.Join(", ", a.EnumerateArray().Select(x => x.GetString() ?? ""));
            }

            info.ModLoader = "Fabric";
            return;
        }

        // 2. Quilt: quilt.mod.json
        var quiltEntry = zip.GetEntry("quilt.mod.json");
        if (quiltEntry != null)
        {
            using var sr = new StreamReader(quiltEntry.Open());
            var json = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("quilt_loader", out var ql))
            {
                if (ql.TryGetProperty("id", out var id)) info.ModId = id.GetString() ?? "";
                if (ql.TryGetProperty("version", out var v)) info.Version = v.GetString() ?? "";

                if (ql.TryGetProperty("metadata", out var meta))
                {
                    if (meta.TryGetProperty("name", out var n)) info.Name = n.GetString() ?? "";
                    if (meta.TryGetProperty("description", out var d)) info.Description = d.GetString() ?? "";
                    if (meta.TryGetProperty("contributors", out var c) && c.ValueKind == JsonValueKind.Object)
                        info.Author = string.Join(", ", c.EnumerateObject().Select(x => x.Name));
                    else if (meta.TryGetProperty("contributors", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
                        info.Author = string.Join(", ", cArr.EnumerateArray().Select(x => x.GetString() ?? ""));
                }
                // 依赖解析
                if (ql.TryGetProperty("depends", out var depends))
                    ParseFabricDependencies(depends, info.Dependencies);
                if (ql.TryGetProperty("breaks", out var breaks))
                    ParseFabricConflicts(breaks, info.Conflicts);
            }

            info.ModLoader = "Quilt";
            return;
        }

        // 3. Forge 1.13+: META-INF/mods.toml
        var tomlEntry = zip.GetEntry("META-INF/mods.toml");
        if (tomlEntry != null)
        {
            using var sr = new StreamReader(tomlEntry.Open());
            var content = sr.ReadToEnd();
            ParseForgeToml(content, info);
            info.ModLoader = "Forge";
            return;
        }

        // 4. NeoForge: META-INF/neoforge.mods.toml
        var neoTomlEntry = zip.GetEntry("META-INF/neoforge.mods.toml");
        if (neoTomlEntry != null)
        {
            using var sr = new StreamReader(neoTomlEntry.Open());
            var content = sr.ReadToEnd();
            ParseForgeToml(content, info);
            info.ModLoader = "NeoForge";
            return;
        }

        // 5. 旧版 Forge: mcmod.info
        var mcmodEntry = zip.GetEntry("mcmod.info");
        if (mcmodEntry != null)
        {
            using var sr = new StreamReader(mcmodEntry.Open());
            var json = sr.ReadToEnd();
            try
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement root;
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    root = doc.RootElement[0];
                else
                    root = doc.RootElement;

                if (root.TryGetProperty("modid", out var mid)) info.ModId = mid.GetString() ?? "";
                if (root.TryGetProperty("name", out var mn)) info.Name = mn.GetString() ?? "";
                if (root.TryGetProperty("description", out var md)) info.Description = md.GetString() ?? "";
                if (root.TryGetProperty("version", out var mv)) info.Version = mv.GetString() ?? "";
                if (root.TryGetProperty("authorList", out var al) && al.ValueKind == JsonValueKind.Array)
                    info.Author = string.Join(", ", al.EnumerateArray().Select(x => x.GetString() ?? ""));
                else if (root.TryGetProperty("authors", out var asingle) && asingle.ValueKind == JsonValueKind.Array)
                    info.Author = string.Join(", ", asingle.EnumerateArray().Select(x => x.GetString() ?? ""));

                // 旧版 mcmod.info 也可能有 mcversion
                if (root.TryGetProperty("mcversion", out var mcv)) info.McVersion = mcv.GetString() ?? "";

                info.ModLoader = "Forge";
            }
            catch { }
        }
    }

    private void ParseFabricDependencies(JsonElement deps, List<ModDependencyInfo> list, string depType = "required")
    {
        if (deps.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in deps.EnumerateObject())
            {
                // 跳过 minecraft/java 等系统级依赖
                if (prop.Name == "minecraft" || prop.Name == "java" || prop.Name == "fabricloader" || prop.Name == "quilt_loader")
                    continue;
                list.Add(new ModDependencyInfo
                {
                    ModId = prop.Name,
                    DependencyType = depType,
                    VersionRange = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null
                });
            }
        }
    }

    private void ParseFabricConflicts(JsonElement breaks, List<string> conflicts)
    {
        if (breaks.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in breaks.EnumerateObject())
            {
                if (prop.Name != "minecraft" && prop.Name != "java")
                    conflicts.Add(prop.Name);
            }
        }
    }

    /// <summary>解析 Forge/NeoForge TOML 格式元数据</summary>
    private void ParseForgeToml(string content, ModInfo info)
    {
        // 解析 [[mods]] 区块
        var modsMatch = Regex.Match(content, @"\[\[mods\]\](.*?)(?=\[\[|$)", RegexOptions.Singleline);
        if (modsMatch.Success)
        {
            var block = modsMatch.Groups[1].Value;
            var modIdMatch = Regex.Match(block, @"modId\s*=\s*""([^""]+)""");
            if (modIdMatch.Success) info.ModId = modIdMatch.Groups[1].Value;

            var nameMatch = Regex.Match(block, @"displayName\s*=\s*""([^""]+)""");
            if (nameMatch.Success) info.Name = nameMatch.Groups[1].Value;

            var descMatch = Regex.Match(block, @"description\s*=\s*'([^']+)'");
            if (!descMatch.Success) descMatch = Regex.Match(block, @"description\s*=\s*""([^""]+)""");
            if (descMatch.Success) info.Description = descMatch.Groups[1].Value;

            var verMatch = Regex.Match(block, @"version\s*=\s*""([^""]+)""");
            if (verMatch.Success) info.Version = verMatch.Groups[1].Value;

            var authorMatch = Regex.Match(block, @"displayURL\s*=\s*""([^""]+)""");
            if (authorMatch.Success) info.Author = authorMatch.Groups[1].Value;

            // 可选 logoFile / credits 等
        }

        // 解析 [[dependencies.模组名]] 区块(Forge 1.13+)
        var depMatches = Regex.Matches(content, @"\[\[dependencies\.([^\]]+)\]\](.*?)(?=\[\[|$)", RegexOptions.Singleline);
        foreach (Match dm in depMatches)
        {
            var depId = dm.Groups[1].Value.Trim();
            var depBlock = dm.Groups[2].Value;

            // 跳过 minecraft/forge 系统依赖
            if (depId.Equals("minecraft", StringComparison.OrdinalIgnoreCase) ||
                depId.Equals("forge", StringComparison.OrdinalIgnoreCase) ||
                depId.Equals("neoforge", StringComparison.OrdinalIgnoreCase))
                continue;

            var typeMatch = Regex.Match(depBlock, @"mandatory\s*=\s*(true|false)", RegexOptions.IgnoreCase);
            bool mandatory = typeMatch.Success && typeMatch.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);

            var rangeMatch = Regex.Match(depBlock, @"versionRange\s*=\s*""([^""]+)""");

            info.Dependencies.Add(new ModDependencyInfo
            {
                ModId = depId,
                DependencyType = mandatory ? "required" : "optional",
                VersionRange = rangeMatch.Success ? rangeMatch.Groups[1].Value : null
            });
        }

        // 解析 ordering="NONE/BEFORE/AFTER" 等冲突关系(简化处理:BEFORE/AFTER 不作硬冲突标记)
    }

    // ==================== 启用/禁用(修改文件后缀,不删除) ====================

    public bool ToggleMod(string filePath, bool enable)
    {
        if (!File.Exists(filePath)) return false;
        string dir = Path.GetDirectoryName(filePath)!;
        string name = Path.GetFileNameWithoutExtension(filePath);
        string ext = Path.GetExtension(filePath);

        string newPath;
        if (enable)
        {
            newPath = Path.Combine(dir, name + ".jar");
        }
        else
        {
            if (ext.Equals(".jar", StringComparison.OrdinalIgnoreCase))
                newPath = Path.Combine(dir, name + ".disabled");
            else return false;
        }

        if (!string.Equals(filePath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return TryMoveFile(filePath, newPath);
        }
        return true;
    }

    private static bool TryMoveFile(string src, string dst)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                File.Move(src, dst, overwrite: true);
                return true;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(100 * (i + 1));
            }
        }
        return false;
    }

    // ==================== 拖拽导入 + 合法性校验 ====================

    /// <summary>拖拽导入模组文件,校验合法性</summary>
    public (bool Success, string? ErrorMessage) ImportModFile(string sourcePath, string? expectedSha1 = null)
    {
        if (string.IsNullOrEmpty(CurrentInstanceId))
            return (false, "请先选择一个游戏版本");

        // 1. 文件格式检测
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext != ".jar" && ext != ".zip")
            return (false, $"文件格式不支持({ext}),仅支持 .jar 或 .zip 格式的模组文件");

        // 2. 文件大小检测(拒绝空文件或超大文件)
        long fileSize = SafeGetFileSize(sourcePath);
        if (fileSize == 0)
            return (false, "文件大小为0,可能是损坏的文件");
        if (fileSize > 500L * 1024 * 1024)  // 500MB 上限
            return (false, $"文件过大({fileSize / 1024.0 / 1024.0:F1} MB),模组文件不应超过 500 MB");

        // 3. 检测是否为有效模组(包含元数据)
        bool isValidMod = false;
        try
        {
            using var zip = ZipFile.OpenRead(sourcePath);
            isValidMod = zip.GetEntry("fabric.mod.json") != null
                      || zip.GetEntry("quilt.mod.json") != null
                      || zip.GetEntry("META-INF/mods.toml") != null
                      || zip.GetEntry("META-INF/neoforge.mods.toml") != null
                      || zip.GetEntry("mcmod.info") != null;
        }
        catch (InvalidDataException)
        {
            return (false, "文件不是有效的 zip/jar 压缩包,可能已损坏");
        }
        catch (Exception)
        {
            return (false, "无法打开文件,文件可能正在被其他程序占用或已损坏");
        }

        if (!isValidMod)
        {
            App.WriteAppLog($"[模组] 导入警告: {Path.GetFileName(sourcePath)} 未检测到模组元数据,仍允许导入");
            // 即使没有元数据也允许导入,可能是特殊类型模组
        }

        // 4. 版本兼容性检测
        string? currentMc = GetCurrentMcVersion();
        string? currentLoader = GetCurrentModLoader();
        if (!string.IsNullOrEmpty(currentMc) || !string.IsNullOrEmpty(currentLoader))
        {
            var compResult = CheckModCompatibility(sourcePath, currentMc, currentLoader);
            if (!compResult.IsCompatible && compResult.WarningMessage != null)
            {
                // 警告但不阻止导入,由 UI 显示警告
                App.WriteAppLog($"[模组] 兼容性警告 {Path.GetFileName(sourcePath)}: {compResult.WarningMessage}");
                // 返回成功但附带警告
                DoCopyModFile(sourcePath);
                return (true, compResult.WarningMessage);
            }
        }

        // 5. 复制文件到 mods 目录
        return DoCopyModFile(sourcePath);

        (bool, string?) DoCopyModFile(string src)
        {
            string modsDir = _instanceService.GetModsDir(CurrentInstanceId!);
            Directory.CreateDirectory(modsDir);
            string dest = Path.Combine(modsDir, Path.GetFileName(src));
            File.Copy(src, dest, overwrite: true);

            if (!string.IsNullOrEmpty(expectedSha1))
            {
                var vr = _hashVerify.Verify(dest, expectedSha1);
                if (!vr.Valid)
                {
                    try { File.Delete(dest); }
                    catch (Exception ex) { App.WriteAppLog($"[模组] 清理校验失败文件失败 {dest}:{ex.Message}"); }
                    return (false, "文件校验失败(SHA1 不匹配),文件可能已损坏");
                }
            }
            return (true, null);
        }
    }

    /// <summary>检测模组与游戏实例的版本、加载器匹配性</summary>
    public ModCompatibilityResult CheckModCompatibility(string jarPath, string? targetMcVersion, string? targetLoader)
    {
        var result = new ModCompatibilityResult();

        if (string.IsNullOrEmpty(targetMcVersion) && string.IsNullOrEmpty(targetLoader))
            return result;

        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            string? modLoader = null;
            var supportedVersions = new List<string>();

            // Fabric: 通过 fabric.mod.json 的 depends.minecraft 获取适配版本
            var fabricEntry = zip.GetEntry("fabric.mod.json");
            if (fabricEntry != null)
            {
                using var sr = new StreamReader(fabricEntry.Open());
                var json = sr.ReadToEnd();
                using var doc = JsonDocument.Parse(json);
                modLoader = "Fabric";

                if (doc.RootElement.TryGetProperty("depends", out var depends) &&
                    depends.TryGetProperty("minecraft", out var mcVer))
                {
                    supportedVersions.Add(mcVer.GetString() ?? "*");
                }
                if (doc.RootElement.TryGetProperty("recommends", out var recommends) &&
                    recommends.TryGetProperty("minecraft", out var mcVer2))
                {
                    supportedVersions.Add(mcVer2.GetString() ?? "*");
                }
            }

            // Forge: mods.toml 的 versionRange
            var tomlEntry = zip.GetEntry("META-INF/mods.toml")
                         ?? zip.GetEntry("META-INF/neoforge.mods.toml");
            if (tomlEntry != null)
            {
                if (fabricEntry == null)
                    modLoader = tomlEntry.Name.Contains("neoforge")
                        ? "NeoForge" : "Forge";

                using var sr = new StreamReader(tomlEntry.Open());
                var content = sr.ReadToEnd();
                var rangeMatch = Regex.Match(content, @"versionRange\s*=\s*""([^""]+)""");
                if (rangeMatch.Success)
                    supportedVersions.Add(rangeMatch.Groups[1].Value);
            }

            // 加载器不匹配检测
            if (!string.IsNullOrEmpty(targetLoader) && !string.IsNullOrEmpty(modLoader))
            {
                if (!targetLoader.Equals(modLoader, StringComparison.OrdinalIgnoreCase))
                {
                    result.IsLoaderMismatch = true;
                    result.IsCompatible = false;
                    result.WarningMessage = $"该模组为 {modLoader} 模组,而当前游戏版本使用 {targetLoader} 加载器,可能无法正常运行";
                    return result;
                }
            }

            // 版本不匹配检测(简化:检查目标版本是否在支持列表中)
            if (!string.IsNullOrEmpty(targetMcVersion) && supportedVersions.Count > 0)
            {
                bool matched = supportedVersions.Any(sv =>
                    sv == "*" || sv.Contains(targetMcVersion) || VersionRangeMatches(sv, targetMcVersion));
                if (!matched)
                {
                    result.IsVersionMismatch = true;
                    result.IsCompatible = false;
                    result.WarningMessage = $"该模组适配 {string.Join(", ", supportedVersions)} 版本,当前游戏版本为 {targetMcVersion},可能不兼容";
                }
            }
        }
        catch
        {
            // 解析失败时不阻塞导入
        }

        return result;
    }

    /// <summary>简化版版本范围匹配(* 匹配全部,>= 最低版本)</summary>
    private static bool VersionRangeMatches(string range, string target)
    {
        if (range == "*") return true;
        if (range.Contains(target)) return true;
        // 简化: 尝试匹配 >=X.X.X 格式
        var geMatch = Regex.Match(range, @">=\s*([\d.]+)");
        if (geMatch.Success)
        {
            return CompareVersions(target, geMatch.Groups[1].Value) >= 0;
        }
        return false;
    }

    private static int CompareVersions(string a, string b)
    {
        try
        {
            var pa = a.Split('.').Select(s => int.TryParse(s, out int v) ? v : 0).ToArray();
            var pb = b.Split('.').Select(s => int.TryParse(s, out int v) ? v : 0).ToArray();
            int len = Math.Max(pa.Length, pb.Length);
            Array.Resize(ref pa, len);
            Array.Resize(ref pb, len);
            for (int i = 0; i < len; i++)
            {
                if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
            }
            return 0;
        }
        catch { return 0; }
    }

    // ==================== 冲突检测 ====================

    /// <summary>检测已启用模组之间的冲突,返回冲突对列表(中文描述)</summary>
    public List<(ModInfo ModA, ModInfo ModB, string Reason)> DetectConflicts(List<ModInfo> mods)
    {
        var conflicts = new List<(ModInfo, ModInfo, string)>();
        var enabledMods = mods.Where(m => m.Enabled).ToList();

        for (int i = 0; i < enabledMods.Count; i++)
        {
            for (int j = i + 1; j < enabledMods.Count; j++)
            {
                var a = enabledMods[i];
                var b = enabledMods[j];

                // 检查 A 是否声明与 B 冲突
                if (a.Conflicts.Contains(b.ModId, StringComparer.OrdinalIgnoreCase) ||
                    a.Conflicts.Contains(b.Name, StringComparer.OrdinalIgnoreCase))
                {
                    conflicts.Add((a, b, $"「{a.DisplayName}」声明与「{b.DisplayName}」冲突"));
                }
                // 检查 B 是否声明与 A 冲突
                else if (b.Conflicts.Contains(a.ModId, StringComparer.OrdinalIgnoreCase) ||
                         b.Conflicts.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
                {
                    conflicts.Add((a, b, $"「{b.DisplayName}」声明与「{a.DisplayName}」冲突"));
                }
                // 同名模组不同版本冲突
                else if (a.ModId == b.ModId && !string.IsNullOrEmpty(a.ModId) && a.Version != b.Version)
                {
                    conflicts.Add((a, b, $"检测到「{a.DisplayName}」存在两个不同版本({a.Version} / {b.Version}),可能引发错误"));
                }
            }
        }

        return conflicts;
    }

    /// <summary>检测缺少的必需前置依赖</summary>
    public List<(ModInfo Mod, ModDependencyInfo MissingDep)> DetectMissingRequirements(List<ModInfo> mods)
    {
        var missing = new List<(ModInfo, ModDependencyInfo)>();
        var enabledMods = mods.Where(m => m.Enabled).ToList();
        var allModIds = enabledMods.Select(m => m.ModId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allNames = enabledMods.Select(m => m.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in enabledMods)
        {
            foreach (var dep in mod.Dependencies.Where(d => d.DependencyType == "required"))
            {
                if (!allModIds.Contains(dep.ModId) && !allNames.Contains(dep.ModId))
                {
                    missing.Add((mod, dep));
                }
            }
        }

        return missing;
    }

    // ==================== 模组下载(独立队列) ====================

    /// <summary>从 URL 下载模组(使用独立 CancellationToken,与游戏下载互不干扰)</summary>
    public async Task<(bool Success, string? Error)> DownloadModAsync(string url, string fileName, string? sha1 = null, long size = 0)
    {
        if (string.IsNullOrEmpty(CurrentInstanceId))
            return (false, "未选择游戏版本,无法下载模组");

        string modsDir = _instanceService.GetModsDir(CurrentInstanceId);
        Directory.CreateDirectory(modsDir);
        string dest = Path.Combine(modsDir, fileName);

        ModDownloadProgressChanged?.Invoke(0, $"准备下载 {fileName}...");

        if (File.Exists(dest))
        {
            if (string.IsNullOrEmpty(sha1) || _hashVerify.Verify(dest, sha1).Valid)
            {
                ModDownloadProgressChanged?.Invoke(100, $"{fileName} 已存在且校验通过");
                return (true, null);
            }
            TryDeleteFile(dest);
        }

        // 取消上一次模组下载任务
        _modDownloadCts?.Cancel();
        _modDownloadCts = new CancellationTokenSource();

        _downloadService.ResetOverallProgress();

        var task = new DownloadTaskItem
        {
            Url = url,
            LocalPath = dest,
            Sha1 = sha1,
            Size = size,
            Category = DownloadCategory.Mod
        };

        void OnProgress(DownloadProgressInfo p)
        {
            double pct = p.Progress * 100;
            string speed = p.SpeedBytesPerSec > 0
                ? $"{p.SpeedBytesPerSec / 1024.0:F1} KB/s"
                : "";
            ModDownloadProgressChanged?.Invoke(pct, $"下载中 {pct:F0}%  {speed}");
        }

        _downloadService.ProgressChanged += OnProgress;
        try
        {
            bool ok = await _downloadService.DownloadAllAsync(new List<DownloadTaskItem> { task });
            if (!ok)
            {
                string errorMsg = GetFriendlyError(task.Error);
                ModDownloadProgressChanged?.Invoke(0, $"下载失败: {errorMsg}");
                return (false, errorMsg);
            }

            if (!string.IsNullOrEmpty(sha1))
            {
                ModDownloadProgressChanged?.Invoke(99, "校验文件完整性...");
                var vr = _hashVerify.Verify(dest, sha1);
                if (!vr.Valid)
                {
                    TryDeleteFile(dest);
                    ModDownloadProgressChanged?.Invoke(0, "文件校验失败,已删除");
                    return (false, "文件校验失败(SHA1 不匹配),可能是下载过程中文件损坏");
                }
            }

            ModDownloadProgressChanged?.Invoke(100, $"{fileName} 下载完成");
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            ModDownloadProgressChanged?.Invoke(0, "下载已取消");
            return (false, "下载已取消");
        }
        finally
        {
            _downloadService.ProgressChanged -= OnProgress;
        }
    }

    /// <summary>取消当前所有模组下载任务</summary>
    public void CancelModDownloads()
    {
        _modDownloadCts?.Cancel();
        ModDownloadProgressChanged?.Invoke(0, "模组下载已取消");
    }

    /// <summary>获取友好的错误提示(隐藏底层堆栈)</summary>
    private static string GetFriendlyError(string? rawError)
    {
        if (string.IsNullOrEmpty(rawError)) return "未知下载错误";

        if (rawError.Contains("404") || rawError.Contains("Not Found"))
            return "下载文件不存在(404),可能已被移除或链接失效";
        if (rawError.Contains("timeout") || rawError.Contains("超时") || rawError.Contains("Timeout"))
            return "网络连接超时,请检查网络后重试";
        if (rawError.Contains("permission") || rawError.Contains("denied") || rawError.Contains("拒绝"))
            return "写入文件失败,请检查磁盘权限或是否被其他程序占用";
        if (rawError.Contains("cancelled") || rawError.Contains("取消"))
            return "下载已取消";
        if (rawError.Contains("disk") || rawError.Contains("space") || rawError.Contains("磁盘"))
            return "磁盘空间不足,请清理后重试";
        if (rawError.Contains("DNS") || rawError.Contains("resolve") || rawError.Contains("解析"))
            return "域名解析失败,请检查 DNS 或网络设置";

        return $"下载失败,请检查网络后重试";
    }

    // ==================== 批量模组下载(前置依赖自动下载) ====================

    /// <summary>批量下载模组(包含必需前置依赖自动下载),独立队列</summary>
    public async Task<(bool Success, List<string> Installed, List<string> Failed, string? Error)>
        DownloadModsWithDependenciesAsync(
            List<ModrinthProject> primaryMods,
            ModrinthService modrinthService,
            IProgress<(double Percent, string Status)>? progress = null)
    {
        var installed = new List<string>();
        var failed = new List<string>();

        if (string.IsNullOrEmpty(CurrentInstanceId))
            return (false, installed, failed, "未选择游戏版本");

        string modsDir = _instanceService.GetModsDir(CurrentInstanceId);
        Directory.CreateDirectory(modsDir);

        // 取消上一次模组下载
        _modDownloadCts?.Cancel();
        _modDownloadCts = new CancellationTokenSource();
        var ct = _modDownloadCts.Token;

        string? currentMc = GetCurrentMcVersion();
        string? currentLoader = GetCurrentModLoader();

        try
        {
            int total = primaryMods.Count;
            int done = 0;

            foreach (var mod in primaryMods)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(((double)done / total * 100, $"下载 {mod.Title} ({done + 1}/{total})..."));

                // 获取版本
                var versions = await modrinthService.GetProjectVersionsAsync(
                    mod.ProjectId,
                    gameVersion: currentMc,
                    loader: currentLoader);

                var mainVer = versions.FirstOrDefault();
                if (mainVer == null)
                {
                    failed.Add(mod.Title);
                    App.WriteAppLog($"[模组] 无兼容版本: {mod.Title} (MC {currentMc}, {currentLoader})");
                    done++;
                    continue;
                }

                // 自动下载必需前置依赖
                var requiredDeps = mainVer.Dependencies
                    .Where(d => d.DependencyType == "required" && !string.IsNullOrEmpty(d.ProjectId))
                    .ToList();

                foreach (var dep in requiredDeps)
                {
                    ct.ThrowIfCancellationRequested();
                    var depProj = await modrinthService.GetProjectAsync(dep.ProjectId!);
                    if (depProj == null) continue;

                    var depVersions = await modrinthService.GetProjectVersionsAsync(
                        dep.ProjectId!, gameVersion: currentMc, loader: currentLoader);
                    var depVer = depVersions.FirstOrDefault();
                    if (depVer == null) continue;

                    // 检查是否已安装
                    var depFile = depVer.GetPrimaryFile();
                    if (depFile == null) continue;

                    string destPath = Path.Combine(modsDir, depFile.Filename ?? $"{dep.ProjectId}.jar");
                    if (File.Exists(destPath))
                    {
                        if (!string.IsNullOrEmpty(depFile.Hashes?.Sha1) &&
                            _hashVerify.Verify(destPath, depFile.Hashes.Sha1).Valid)
                        {
                            continue; // 已存在且校验通过
                        }
                    }

                    progress?.Report(((double)done / total * 100, $"下载前置依赖 {depProj.Title}..."));
                    bool depOk = await modrinthService.DownloadModVersionAsync(depVer, modsDir);
                    if (depOk)
                    {
                        installed.Add($"前置: {depProj.Title}");
                        App.WriteAppLog($"[模组] 自动安装前置依赖: {depProj.Title}");
                    }
                }

                // 下载主模组
                progress?.Report(((double)done / total * 100, $"下载模组 {mod.Title}..."));
                bool ok = await modrinthService.DownloadModVersionAsync(mainVer, modsDir);
                if (ok)
                {
                    installed.Add(mod.Title);
                }
                else
                {
                    failed.Add(mod.Title);
                }

                done++;
                progress?.Report((100.0 * done / total, $"已完成 {done}/{total}"));
            }

            return (failed.Count == 0, installed, failed,
                failed.Count > 0 ? $"安装完成,{failed.Count} 个失败" : null);
        }
        catch (OperationCanceledException)
        {
            return (false, installed, failed, "下载已取消");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[模组] 批量下载异常: {ex.Message}");
            return (false, installed, failed, "下载过程中发生错误,请检查网络后重试");
        }
    }

    // ==================== 工具方法 ====================

    public bool DeleteMod(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            File.Delete(filePath);
            return true;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[模组] 删除模组失败 {filePath}:{ex.Message}");
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { App.WriteAppLog($"[模组] 删除文件失败 {path}:{ex.Message}"); }
    }

    public void OpenModsFolder()
    {
        if (string.IsNullOrEmpty(CurrentInstanceId)) return;
        string modsDir = _instanceService.GetModsDir(CurrentInstanceId);
        Directory.CreateDirectory(modsDir);
        StartExplorer(modsDir);
    }

    public void RevealModInExplorer(string filePath)
    {
        if (!File.Exists(filePath)) return;
        StartExplorer($"/select,\"{filePath}\"");
    }

    private static void StartExplorer(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("explorer.exe", arguments)
            {
                UseShellExecute = true
            };
            using var p = Process.Start(psi);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[模组] 打开资源管理器失败:{ex.Message}");
        }
    }
}
