// Copyright © FufuLauncher
//
// 模组诊断服务:承载四个参考启动器里全部与"模组健康度"相关的能力 ——
//   Axolotl-2 冲突快速诊断(选中模组 → ID 冲突 / 缺失前置 / 来源标注 / 文字修复提示)
//   Axolotl-3 版本智能匹配(绑定实例游戏版本,导入时过滤不兼容,不匹配高亮,一键下载适配版本)
//   Celestial-3 依赖树可视化(树形结构,区分必需/可选前置,缺失标红)
//   LauncherX-7 更新聚合面板(汇总有新版本的模组,支持单个更新与批量更新)
//
// 实现要点:
// 1. 离线诊断(冲突/缺失/依赖树骨架)只读本地已解析的 ModInfo,零网络、瞬时完成;
// 2. 联网部分(适配版本查找、更新检查)一律 async + CancellationToken,单个模组失败不影响整体;
// 3. 网络并发受 SemaphoreSlim 限制,避免批量检查时被 Modrinth 限流;
// 4. 版本号比较用"数字段为主、后缀为辅"的宽松比较,兼容 1.20.1-forge / 2.0.0-beta 这类写法。

using System.IO;
using System.Text.RegularExpressions;

namespace FufuLauncher.Services;

/// <summary>诊断条目类型</summary>
public enum ModIssueKind
{
    /// <summary>同一 modid 出现多个文件(重复安装/多版本共存)</summary>
    DuplicateId,
    /// <summary>模组自己声明与另一个模组冲突(mods.toml / fabric.mod.json 的 conflicts 字段)</summary>
    DeclaredConflict,
    /// <summary>缺少必需前置</summary>
    MissingRequired,
    /// <summary>缺少可选前置(仅提示,不影响启动)</summary>
    MissingOptional,
    /// <summary>模组不支持当前游戏版本</summary>
    VersionMismatch,
    /// <summary>模组不支持当前加载器</summary>
    LoaderMismatch,
    /// <summary>模组已禁用(仅作为状态提示)</summary>
    Disabled
}

/// <summary>一条诊断结论</summary>
public sealed class ModIssue
{
    public ModIssueKind Kind { get; set; }
    /// <summary>涉及的文件名(冲突对方 / 缺失依赖的 modid)</summary>
    public string Target { get; set; } = "";
    /// <summary>冲突来源标注:说清楚"这个问题是谁报出来的"</summary>
    public string Source { get; set; } = "";
    /// <summary>一句话修复提示</summary>
    public string Fix { get; set; } = "";
    /// <summary>严重级别:true = 会直接崩游戏,false = 只是提醒</summary>
    public bool Severe { get; set; }
    public string KindDisplay => Kind switch
    {
        ModIssueKind.DuplicateId => "ID 冲突",
        ModIssueKind.DeclaredConflict => "声明冲突",
        ModIssueKind.MissingRequired => "缺失必需前置",
        ModIssueKind.MissingOptional => "缺失可选前置",
        ModIssueKind.VersionMismatch => "游戏版本不符",
        ModIssueKind.LoaderMismatch => "加载器不符",
        _ => "已禁用"
    };
    public override string ToString() => $"[{KindDisplay}] {Target} —— {Source}";
}

/// <summary>诊断报告</summary>
public sealed class ModDiagnosisReport
{
    public string InstanceId { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "";
    /// <summary>被选中的模组(null = 全量诊断)</summary>
    public ModInfo? Selected { get; set; }
    public int ScannedMods { get; set; }
    public List<ModIssue> Issues { get; set; } = new();
    public int SevereCount => Issues.Count(i => i.Severe);
    public bool HasIssues => Issues.Count > 0;
    public string Summary
    {
        get
        {
            if (!HasIssues) return $"扫描了 {ScannedMods} 个模组,没有发现冲突或缺失前置,可以放心启动。";
            int dup = Issues.Count(i => i.Kind == ModIssueKind.DuplicateId);
            int miss = Issues.Count(i => i.Kind == ModIssueKind.MissingRequired);
            int conf = Issues.Count(i => i.Kind == ModIssueKind.DeclaredConflict);
            int ver = Issues.Count(i => i.Kind == ModIssueKind.VersionMismatch || i.Kind == ModIssueKind.LoaderMismatch);
            return $"扫描了 {ScannedMods} 个模组,发现 {Issues.Count} 处问题" +
                   $"(ID 冲突 {dup} · 声明冲突 {conf} · 缺失前置 {miss} · 版本不符 {ver})," +
                   $"其中 {SevereCount} 处会导致启动失败。";
        }
    }
}

/// <summary>依赖树节点(Celestial-3)</summary>
public sealed class DependencyNode
{
    public string ModId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>required / optional / embedded / incompatible</summary>
    public string DependencyType { get; set; } = "required";
    /// <summary>依赖声明的版本区间(如 >=1.2.0)</summary>
    public string VersionRange { get; set; } = "";
    /// <summary>本地是否已安装</summary>
    public bool Installed { get; set; }
    /// <summary>本地已装但是被禁用</summary>
    public bool InstalledButDisabled { get; set; }
    /// <summary>缺失(必需依赖缺失 → UI 标红)</summary>
    public bool Missing => !Installed && DependencyType == "required";
    /// <summary>Modrinth 上是否有适配当前游戏版本的版本(缺失时可一键下载)</summary>
    public bool Downloadable { get; set; }
    /// <summary>可下载时对应的 Modrinth 项目 Id(供一键下载)</summary>
    public string? ModrinthProjectId { get; set; }
    public List<DependencyNode> Children { get; set; } = new();
    public string TypeDisplay => DependencyType switch
    {
        "required" => "必需前置",
        "optional" => "可选前置",
        "embedded" => "已内置",
        "incompatible" => "不兼容",
        _ => "依赖"
    };
}

/// <summary>可下载的适配版本候选(Axolotl-3)</summary>
public sealed class ModMatchCandidate
{
    public ModrinthVersion? Version { get; set; }
    public string ProjectTitle { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string GameVersions { get; set; } = "";
    public string Loaders { get; set; } = "";
    public long Size { get; set; }
    public string FileName { get; set; } = "";
    public string Display => $"{ProjectTitle} {VersionNumber} · {GameVersions} · {Loaders} · {StorageGuardService.FmtSize(Size)}";
}

/// <summary>模组更新条目(LauncherX-7)</summary>
public sealed class ModUpdateInfo
{
    public ModInfo Mod { get; set; } = null!;
    public string ModrinthProjectId { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public ModrinthVersion? Latest { get; set; }
    public string LatestVersion => Latest?.VersionNumber ?? "";
    public string LatestType => Latest?.VersionType ?? "release";
    public bool HasUpdate { get; set; }
    /// <summary>检查失败原因(网络问题 / Modrinth 上找不到该模组)</summary>
    public string? Error { get; set; }
    public bool Checked { get; set; }
    public string Display => $"{Mod.DisplayName} {CurrentVersion} → {LatestVersion}";
}

public sealed class ModDiagnosticsService
{
    /// <summary>联网检查并发上限(Modrinth 有限流,过高会 429)</summary>
    private static readonly SemaphoreSlim NetGate = new(3, 3);

    private static readonly Regex VersionNumRegex = new(@"(\d+)", RegexOptions.Compiled);

    private readonly InstanceService _instances;
    private readonly ModManagerService _mods;
    private readonly ModrinthService _modrinth;

    public ModDiagnosticsService(InstanceService instances, ModManagerService mods, ModrinthService modrinth)
    {
        _instances = instances;
        _mods = mods;
        _modrinth = modrinth;
    }

    // ==================== Axolotl-2 冲突诊断 ====================

    /// <summary>
    /// 诊断指定模组(selected 为 null 时诊断整个实例的模组列表)。
    /// 全部本地计算,不做任何网络请求,点击即出结果。
    /// </summary>
    public ModDiagnosisReport Diagnose(string instanceId, ModInfo? selected = null)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        var report = new ModDiagnosisReport
        {
            InstanceId = instanceId,
            InstanceName = inst?.Name ?? instanceId,
            McVersion = inst?.VersionId ?? "",
            Loader = inst?.ModLoader ?? "",
            Selected = selected
        };

        List<ModInfo> all = SafeLoadMods(instanceId);
        report.ScannedMods = all.Count;
        if (all.Count == 0) return report;

        // 只诊断选中的模组时,冲突比对仍要拿全量列表(冲突是"两者之间"的关系)
        List<ModInfo> scope = selected == null ? all : all.Where(m => SameMod(m, selected)).ToList();
        if (scope.Count == 0) scope = all;

        var byId = all.Where(m => m.Enabled && !string.IsNullOrEmpty(m.ModId))
                      .GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase)
                      .Where(g => g.Count() > 1)
                      .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var mod in scope)
        {
            // 1) 同 modid 多文件
            if (mod.Enabled && !string.IsNullOrEmpty(mod.ModId) && byId.TryGetValue(mod.ModId, out var dup))
            {
                foreach (var other in dup.Where(o => !ReferenceEquals(o, mod)))
                {
                    report.Issues.Add(new ModIssue
                    {
                        Kind = ModIssueKind.DuplicateId,
                        Target = other.FileName,
                        Source = $"文件 {mod.FileName} 与 {other.FileName} 同时注册了 modid「{mod.ModId}」",
                        Fix = string.Equals(mod.Version, other.Version, StringComparison.OrdinalIgnoreCase)
                            ? $"两个文件完全同版本,删掉其中一个即可(建议保留 {mod.FileName})。"
                            : $"这是同一个模组的两个版本({mod.Version} / {other.Version}),只留最新的一个,另一个删掉或禁用。",
                        Severe = true
                    });
                }
            }

            // 2) 模组自己声明的冲突
            foreach (var other in all)
            {
                if (ReferenceEquals(other, mod)) continue;
                if (!mod.Enabled || !other.Enabled) continue;
                if (DeclaredConflictWith(mod, other))
                {
                    report.Issues.Add(new ModIssue
                    {
                        Kind = ModIssueKind.DeclaredConflict,
                        Target = other.FileName,
                        Source = $"「{mod.DisplayName}」在自己的配置文件里声明了不兼容「{other.DisplayName}」",
                        Fix = $"两者不能同时启用,禁用其中一个(通常保留你更常用的那个)。",
                        Severe = true
                    });
                }
                else if (DeclaredConflictWith(other, mod))
                {
                    report.Issues.Add(new ModIssue
                    {
                        Kind = ModIssueKind.DeclaredConflict,
                        Target = other.FileName,
                        Source = $"「{other.DisplayName}」在自己的配置文件里声明了不兼容「{mod.DisplayName}」",
                        Fix = $"两者不能同时启用,禁用其中一个(通常保留你更常用的那个)。",
                        Severe = true
                    });
                }
            }

            // 3) 前置依赖
            foreach (var dep in mod.Dependencies)
            {
                if (string.IsNullOrEmpty(dep.ModId)) continue;
                bool isOptional = string.Equals(dep.DependencyType, "optional", StringComparison.OrdinalIgnoreCase);
                var hit = all.FirstOrDefault(m => MatchesId(m, dep.ModId));
                if (hit != null)
                {
                    if (!hit.Enabled)
                    {
                        report.Issues.Add(new ModIssue
                        {
                            Kind = isOptional ? ModIssueKind.MissingOptional : ModIssueKind.MissingRequired,
                            Target = hit.FileName,
                            Source = $"「{mod.DisplayName}」需要「{hit.DisplayName}」,但它当前是禁用状态",
                            Fix = $"把 {hit.FileName} 重新启用即可(模组页点一下启用)。",
                            Severe = !isOptional
                        });
                    }
                    continue;
                }
                // java / minecraft / forge / fabricloader 这类"运行时依赖"不是模组文件,直接跳过
                if (IsRuntimeDep(dep.ModId)) continue;
                // 2026-09-26 修复:Modrinth 生态的 fabric-*-api-v* 库由「fabric-api」聚合模组统一提供
                // (Sodium/Iris 等声明这些依赖,装了 fabric-api 即满足)——不再误报缺失
                if (dep.ModId.StartsWith("fabric-", StringComparison.Ordinal))
                {
                    var fab = all.FirstOrDefault(m => MatchesId(m, "fabric-api"));
                    if (fab != null)
                    {
                        if (!fab.Enabled)
                        {
                            report.Issues.Add(new ModIssue
                            {
                                Kind = isOptional ? ModIssueKind.MissingOptional : ModIssueKind.MissingRequired,
                                Target = fab.FileName,
                                Source = $"「{mod.DisplayName}」需要「{fab.DisplayName}」(内含 {dep.ModId}),但它当前是禁用状态",
                                Fix = $"把 {fab.FileName} 重新启用即可(模组页点一下启用)。",
                                Severe = !isOptional
                            });
                        }
                        continue;
                    }
                }
                report.Issues.Add(new ModIssue
                {
                    Kind = isOptional ? ModIssueKind.MissingOptional : ModIssueKind.MissingRequired,
                    Target = dep.ModId,
                    Source = $"「{mod.DisplayName}」声明需要前置「{dep.ModId}」" +
                             (string.IsNullOrEmpty(dep.VersionRange) ? "" : $"(版本要求 {dep.VersionRange})") +
                             ",但模组文件夹里没有找到",
                    Fix = isOptional
                        ? "这是可选前置,不装也能正常启动;想要完整功能就去模组页搜这个名字下载安装。"
                        : $"去模组页搜索「{dep.ModId}」,选适配 {report.McVersion} / {LoaderOrAny(report.Loader)} 的版本下载安装。",
                    Severe = !isOptional
                });
            }

            // 4) 版本 / 加载器匹配
            if (mod.Enabled)
            {
                if (!string.IsNullOrEmpty(report.McVersion) && mod.SupportedMcVersions.Count > 0 &&
                    !mod.SupportedMcVersions.Any(v => McVersionMatches(v, report.McVersion)))
                {
                    report.Issues.Add(new ModIssue
                    {
                        Kind = ModIssueKind.VersionMismatch,
                        Target = mod.FileName,
                        Source = $"「{mod.DisplayName}」标注支持 {string.Join(" / ", mod.SupportedMcVersions.Take(4))}," +
                                 $"而这个游戏版本是 {report.McVersion}",
                        Fix = $"用「下载适配版本」按钮换一个 {report.McVersion} 的版本,或者把游戏版本切到模组支持的那一档。",
                        Severe = true
                    });
                }
                if (!string.IsNullOrEmpty(report.Loader) && !string.IsNullOrEmpty(mod.ModLoader) &&
                    !LoaderMatches(mod.ModLoader, report.Loader))
                {
                    report.Issues.Add(new ModIssue
                    {
                        Kind = ModIssueKind.LoaderMismatch,
                        Target = mod.FileName,
                        Source = $"「{mod.DisplayName}」是给 {mod.ModLoader} 用的,而这个游戏版本装的是 {report.Loader}",
                        Fix = $"下载 {report.Loader} 版本的同名模组,或者给这个版本补装 {mod.ModLoader} 加载器。",
                        Severe = true
                    });
                }
            }
        }

        // 去重(同一对模组双向声明冲突会产生两条)
        report.Issues = report.Issues
            .GroupBy(i => $"{i.Kind}|{i.Target}|{i.Source}")
            .Select(g => g.First())
            .OrderByDescending(i => i.Severe)
            .ThenBy(i => i.Kind)
            .ToList();

        App.WriteAppLog($"[模组诊断] {report.InstanceName} 扫描 {report.ScannedMods} 个模组" +
                        (selected == null ? "" : $"(选中 {selected.DisplayName})") +
                        $",发现 {report.Issues.Count} 处问题");
        return report;
    }

    private List<ModInfo> SafeLoadMods(string instanceId)
    {
        try
        {
            _mods.SetCurrentInstance(instanceId);
            return _mods.LoadMods();
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[模组诊断] 模组列表读取失败({instanceId}):{ex.Message}");
            return new List<ModInfo>();
        }
    }

    private static bool SameMod(ModInfo a, ModInfo b) =>
        string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(a.ModId) && string.Equals(a.ModId, b.ModId, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesId(ModInfo m, string depId) =>
        string.Equals(m.ModId, depId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(m.Name, depId, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(m.FileName) &&
         Path.GetFileNameWithoutExtension(m.FileName.TrimDisabled()).Equals(depId, StringComparison.OrdinalIgnoreCase));

    private static bool DeclaredConflictWith(ModInfo mod, ModInfo other)
    {
        if (mod.Conflicts.Count == 0) return false;
        return mod.Conflicts.Any(c =>
            !string.IsNullOrEmpty(c) &&
            (string.Equals(c, other.ModId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(c, other.Name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>运行时代码依赖(不是模组文件,不该报缺失)</summary>
    private static readonly HashSet<string> RuntimeDeps = new(StringComparer.OrdinalIgnoreCase)
    {
        "java", "minecraft", "forge", "fabricloader", "fabric-loader", "fabric", "quiltloader",
        "quilt-loader", "quilt", "neoforge", "liteloader", "optifine"
    };

    public static bool IsRuntimeDep(string depId) => RuntimeDeps.Contains(depId ?? "");

    // ==================== Axolotl-3 版本智能匹配 ====================

    /// <summary>
    /// 导入前过滤:把待导入的 jar 分成"能用"和"不能用"两堆(Axolotl-3 自动过滤版本不兼容模组)。
    /// 纯本地判定,读 jar 内的 mods.toml / fabric.mod.json 元数据。
    /// </summary>
    public (List<string> Compatible, List<string> Incompatible) FilterImportFiles(
        IEnumerable<string> files, string mcVersion, string loader)
    {
        var ok = new List<string>();
        var bad = new List<string>();
        foreach (string f in files)
        {
            try
            {
                var res = _mods.CheckModCompatibility(f, mcVersion, loader);
                if (res.IsCompatible) ok.Add(f); else bad.Add(f);
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[版本匹配] 兼容性判定失败 {f}:{ex.Message}");
                bad.Add(f);
            }
        }
        return (ok, bad);
    }

    /// <summary>
    /// 已装模组里"与当前游戏版本不匹配"的那些(Axolotl-3 高亮显示的数据源)。
    /// </summary>
    public List<(ModInfo Mod, string Reason)> FindMismatched(string instanceId)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst == null) return new();
        string mc = inst.VersionId ?? "";
        string loader = inst.ModLoader ?? "";
        var list = new List<(ModInfo, string)>();
        foreach (var m in SafeLoadMods(instanceId))
        {
            var res = _mods.CheckModCompatibility(m.FilePath, mc, loader);
            if (res.IsCompatible) continue;
            list.Add((m, res.WarningMessage ?? (res.IsLoaderMismatch ? "加载器不匹配" : "游戏版本不匹配")));
        }
        return list;
    }

    /// <summary>
    /// 到 Modrinth 找适配当前游戏版本 + 加载器的版本(Axolotl-3 "一键下载适配版本"的候选来源)。
    /// </summary>
    public async Task<List<ModMatchCandidate>> FindCompatibleAsync(
        ModInfo mod, string mcVersion, string loader, int limit = 8, CancellationToken ct = default)
    {
        var result = new List<ModMatchCandidate>();
        string query = !string.IsNullOrEmpty(mod.ModId) ? mod.ModId : mod.Name;
        if (string.IsNullOrWhiteSpace(query)) return result;
        string? loaderKey = NormalizeLoader(loader);

        await NetGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var search = await _modrinth.SearchAsync(query, 0, 5, mcVersion, loaderKey, "mod", "relevance", null, ct)
                                        .ConfigureAwait(false);
            if (search?.Hits == null || search.Hits.Count == 0) return result;

            foreach (var proj in search.Hits.Take(3))
            {
                ct.ThrowIfCancellationRequested();
                var versions = await _modrinth.GetProjectVersionsAsync(proj.ProjectId, mcVersion, loaderKey, ct)
                                             .ConfigureAwait(false);
                var pick = versions
                    .Where(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(v => v.GameVersions.Count)
                    .ThenByDescending(v => ParseVersion(v.VersionNumber))
                    .Take(limit)
                    .ToList();
                // 一个 release 都没有就退回全部版本(beta/alpha 也总比没有强)
                if (pick.Count == 0)
                    pick = versions.Take(Math.Min(limit, versions.Count)).ToList();

                foreach (var v in pick)
                {
                    var file = v.GetPrimaryFile();
                    result.Add(new ModMatchCandidate
                    {
                        Version = v,
                        ProjectId = proj.ProjectId,
                        ProjectTitle = proj.Title,
                        VersionNumber = v.VersionNumber,
                        GameVersions = string.Join(" / ", v.GameVersions.Take(4)),
                        Loaders = string.Join(" / ", v.Loaders),
                        Size = file?.Size ?? 0,
                        FileName = file?.Filename ?? ""
                    });
                }
                if (result.Count >= limit) break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.WriteAppLog($"[版本匹配] 查找适配版本失败 {query}:{ex.Message}");
        }
        finally { NetGate.Release(); }

        return result.Take(limit).ToList();
    }

    /// <summary>下载并安装适配版本(含必需前置),返回 (是否成功, 提示文案)</summary>
    public async Task<(bool Ok, string Message)> InstallCompatibleAsync(
        ModMatchCandidate candidate, string modsDir, string mcVersion, string loader, CancellationToken ct = default)
    {
        if (candidate?.Version == null) return (false, "没有可用的下载版本。");
        Directory.CreateDirectory(modsDir);
        string loaderKey = NormalizeLoader(loader) ?? "fabric";
        await NetGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (success, files, failedDeps) = await _modrinth.InstallModWithDependenciesAsync(
                candidate.Version, modsDir, mcVersion, loaderKey, ct).ConfigureAwait(false);
            if (!success)
                return (false, $"下载失败:{_modrinth.LastError ?? "网络异常,请检查网络后重试"}");
            App.WriteAppLog($"[版本匹配] 已安装适配版本 {candidate.ProjectTitle} {candidate.VersionNumber} → {modsDir}({files.Count} 个文件)");
            string depNote = failedDeps.Count > 0
                ? $",但 {failedDeps.Count} 个前置下载失败:{string.Join("、", failedDeps)}"
                : (files.Count > 1 ? $",并顺带装好 {files.Count - 1} 个必需前置。" : ".");
            return (true, $"已下载 {candidate.ProjectTitle} {candidate.VersionNumber}" + depNote);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.WriteAppLog($"[版本匹配] 安装适配版本失败:{ex}");
            return (false, "下载失败:" + ex.Message);
        }
        finally { NetGate.Release(); }
    }

    /// <summary>加载器名归一(Modrinth facets 只认小写标识)</summary>
    public static string? NormalizeLoader(string? loader)
    {
        if (string.IsNullOrWhiteSpace(loader)) return null;
        string l = loader.Trim().ToLowerInvariant();
        if (l.Contains("neoforge")) return "neoforge";
        if (l.Contains("forge")) return "forge";
        if (l.Contains("quilt")) return "quilt";
        if (l.Contains("lite")) return "liteloader";
        if (l.Contains("fabric")) return "fabric";
        return null;
    }

    private static string LoaderOrAny(string loader) => string.IsNullOrEmpty(loader) ? "任意加载器" : loader;

    /// <summary>MC 版本宽松匹配:1.20.1 的模组标注 1.20 也算支持</summary>
    public static bool McVersionMatches(string declared, string actual)
    {
        if (string.IsNullOrEmpty(declared) || string.IsNullOrEmpty(actual)) return false;
        if (string.Equals(declared, actual, StringComparison.OrdinalIgnoreCase)) return true;
        if (declared == "*") return true;
        // 前缀包含:声明 1.20 而实际 1.20.1 / 1.20.4 都算兼容
        return actual.StartsWith(declared, StringComparison.OrdinalIgnoreCase) ||
               declared.StartsWith(actual, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>加载器宽松匹配:forge 模组在 neoforge 上通常也能跑,但反过来不行</summary>
    public static bool LoaderMatches(string modLoader, string instanceLoader)
    {
        if (string.IsNullOrEmpty(modLoader) || string.IsNullOrEmpty(instanceLoader)) return true;
        string a = NormalizeLoader(modLoader) ?? modLoader.ToLowerInvariant();
        string b = NormalizeLoader(instanceLoader) ?? instanceLoader.ToLowerInvariant();
        if (a == b) return true;
        if (a.Contains("fabric") && b.Contains("quilt")) return true;   // Quilt 兼容 Fabric 模组
        if (a.Contains("forge") && b.Contains("neoforge")) return true; // NeoForge 早期兼容 Forge 模组
        return false;
    }

    // ==================== Celestial-3 依赖树 ====================

    /// <summary>
    /// 构建依赖树(纯本地):根 = 选中模组,子节点 = 它声明的依赖,递归展开已安装依赖的下级依赖。
    /// 缺失的依赖也进树(Missing = true),由 UI 标红。
    /// </summary>
    public DependencyNode BuildDependencyTree(string instanceId, ModInfo mod, int maxDepth = 4)
    {
        List<ModInfo> all = SafeLoadMods(instanceId);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = new DependencyNode
        {
            ModId = mod.ModId ?? "",
            DisplayName = mod.DisplayName,
            Version = mod.Version ?? "",
            DependencyType = "self",
            Installed = true,
            InstalledButDisabled = !mod.Enabled
        };
        if (!string.IsNullOrEmpty(root.ModId)) visited.Add(root.ModId);
        Expand(root, mod, all, visited, 0, maxDepth);
        return root;
    }

    private void Expand(DependencyNode parent, ModInfo parentMod, List<ModInfo> all,
                        HashSet<string> visited, int depth, int maxDepth)
    {
        if (depth >= maxDepth || parentMod.Dependencies.Count == 0) return;
        foreach (var dep in parentMod.Dependencies)
        {
            if (string.IsNullOrEmpty(dep.ModId) || IsRuntimeDep(dep.ModId)) continue;
            string type = string.IsNullOrEmpty(dep.DependencyType) ? "required" : dep.DependencyType.ToLowerInvariant();
            var hit = all.FirstOrDefault(m => MatchesId(m, dep.ModId));

            var node = new DependencyNode
            {
                ModId = dep.ModId,
                DisplayName = hit?.DisplayName ?? dep.ModId,
                Version = hit?.Version ?? "",
                DependencyType = type,
                VersionRange = dep.VersionRange ?? "",
                Installed = hit != null,
                InstalledButDisabled = hit != null && !hit.Enabled
            };
            parent.Children.Add(node);

            if (hit == null) continue;
            string key = hit.ModId ?? dep.ModId;
            if (!visited.Add(key)) continue;   // 环依赖保护
            Expand(node, hit, all, visited, depth + 1, maxDepth);
        }
    }

    /// <summary>
    /// 联网补全依赖树:给缺失的依赖查 Modrinth,标记"是否可一键下载"与项目 Id。
    /// </summary>
    public async Task EnrichDependencyTreeAsync(DependencyNode root, string mcVersion, string loader,
                                                IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var missing = new List<DependencyNode>();
        CollectMissing(root, missing);
        if (missing.Count == 0) return;
        string? loaderKey = NormalizeLoader(loader);
        int done = 0;
        foreach (var node in missing)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"正在查询缺失前置「{node.ModId}」({++done}/{missing.Count})");
            await NetGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var search = await _modrinth.SearchAsync(node.ModId, 0, 3, mcVersion, loaderKey, "mod", "relevance", null, ct)
                                            .ConfigureAwait(false);
                var proj = search?.Hits?.FirstOrDefault();
                if (proj == null) continue;
                var versions = await _modrinth.GetProjectVersionsAsync(proj.ProjectId, mcVersion, loaderKey, ct)
                                             .ConfigureAwait(false);
                if (versions.Count == 0) continue;
                node.Downloadable = true;
                node.ModrinthProjectId = proj.ProjectId;
                if (string.IsNullOrEmpty(node.DisplayName) || node.DisplayName == node.ModId)
                    node.DisplayName = proj.Title;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                App.WriteAppLog($"[依赖树] 查询 {node.ModId} 失败:{ex.Message}");
            }
            finally { NetGate.Release(); }
        }
    }

    private static void CollectMissing(DependencyNode node, List<DependencyNode> acc)
    {
        if (node.Missing) acc.Add(node);
        foreach (var c in node.Children) CollectMissing(c, acc);
    }

    // ==================== LauncherX-7 更新聚合 ====================

    /// <summary>
    /// 检查全部模组是否有新版本(联网)。逐个查询,失败只记 Error 不中断整体。
    /// </summary>
    public async Task<List<ModUpdateInfo>> CheckUpdatesAsync(
        string instanceId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        string mc = inst?.VersionId ?? "";
        string? loaderKey = NormalizeLoader(inst?.ModLoader);
        List<ModInfo> mods = SafeLoadMods(instanceId);
        var result = new List<ModUpdateInfo>();
        int done = 0;

        foreach (var mod in mods)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"检查更新:{mod.DisplayName}({++done}/{mods.Count})");
            var info = new ModUpdateInfo
            {
                Mod = mod,
                CurrentVersion = string.IsNullOrEmpty(mod.Version) ? "未知" : mod.Version
            };
            result.Add(info);

            string query = !string.IsNullOrEmpty(mod.ModId) ? mod.ModId : mod.Name;
            if (string.IsNullOrWhiteSpace(query)) { info.Error = "读不出模组标识,没法联网查更新"; info.Checked = true; continue; }

            await NetGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var search = await _modrinth.SearchAsync(query, 0, 5, null, null, "mod", "relevance", null, ct)
                                            .ConfigureAwait(false);
                var proj = PickProject(search?.Hits, mod);
                if (proj == null) { info.Error = "Modrinth 上没找到这个模组"; info.Checked = true; continue; }
                info.ModrinthProjectId = proj.ProjectId;

                var versions = await _modrinth.GetProjectVersionsAsync(proj.ProjectId, mc, loaderKey, ct)
                                             .ConfigureAwait(false);
                var latest = versions
                    .Where(v => v.GetPrimaryFile() != null)
                    .OrderByDescending(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(v => ParseVersion(v.VersionNumber))
                    .FirstOrDefault();
                if (latest == null) { info.Error = $"没有适配 {mc} / {loaderKey ?? "任意加载器"} 的版本"; info.Checked = true; continue; }

                info.Latest = latest;
                info.Checked = true;
                info.HasUpdate = CompareVersion(latest.VersionNumber, mod.Version) > 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                info.Error = ex.Message;
                info.Checked = true;
            }
            finally { NetGate.Release(); }
        }

        App.WriteAppLog($"[模组更新] {inst?.Name ?? instanceId} 检查 {result.Count} 个模组," +
                        $"{result.Count(r => r.HasUpdate)} 个有新版本,{result.Count(r => r.Error != null)} 个查询失败");
        return result;
    }

    /// <summary>从搜索结果里挑最像的那个项目:优先 modid 精确命中,其次标题包含</summary>
    private static ModrinthProject? PickProject(List<ModrinthProject>? hits, ModInfo mod)
    {
        if (hits == null || hits.Count == 0) return null;
        if (!string.IsNullOrEmpty(mod.ModId))
        {
            var exact = hits.FirstOrDefault(h =>
                string.Equals(h.Slug, mod.ModId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h.ProjectId, mod.ModId, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        if (!string.IsNullOrEmpty(mod.Name))
        {
            var byName = hits.FirstOrDefault(h =>
                string.Equals(h.Title, mod.Name, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;
            var contains = hits.FirstOrDefault(h =>
                h.Title.IndexOf(mod.Name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                mod.Name.IndexOf(h.Title, StringComparison.OrdinalIgnoreCase) >= 0);
            if (contains != null) return contains;
        }
        return hits[0];
    }

    /// <summary>更新单个模组:下载新版本;oldFile 传 true 时把旧文件改成 .old 备份(不删,留给用户自己确认)</summary>
    public async Task<(bool Ok, string Message)> UpdateModAsync(
        ModUpdateInfo info, string modsDir, bool keepOldAsBackup = true, CancellationToken ct = default)
    {
        if (info?.Latest == null) return (false, "没有可更新到的版本。");
        Directory.CreateDirectory(modsDir);
        await NetGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool ok = await _modrinth.DownloadModVersionAsync(info.Latest, modsDir, ct).ConfigureAwait(false);
            if (!ok) return (false, $"下载失败:{_modrinth.LastError ?? "网络异常,请检查网络后重试"}");

            if (keepOldAsBackup && !string.IsNullOrEmpty(info.Mod.FilePath) && File.Exists(info.Mod.FilePath))
            {
                string backup = info.Mod.FilePath + ".old";
                try { File.Move(info.Mod.FilePath, backup, overwrite: true); }
                catch (Exception ex) { App.WriteAppLog($"[模组更新] 旧文件改名失败(可能被游戏占用):{ex.Message}"); }
            }
            App.WriteAppLog($"[模组更新] {info.Mod.DisplayName} {info.CurrentVersion} → {info.LatestVersion}");
            return (true, $"{info.Mod.DisplayName} 已更新到 {info.LatestVersion}" +
                          (keepOldAsBackup ? "(旧文件已改名为 .old 备份,确认没问题后可自行删除)。" : "。"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.WriteAppLog($"[模组更新] 更新失败 {info.Mod.DisplayName}:{ex}");
            return (false, "更新失败:" + ex.Message);
        }
        finally { NetGate.Release(); }
    }

    // ==================== 版本号比较 ====================

    /// <summary>
    /// 宽松版本号比较:抽出全部数字段逐位比,数字段相同时比剩余文本(release &gt; beta &gt; alpha &gt; 其它)。
    /// 返回 &gt;0 表示 a 更新,&lt;0 表示 b 更新,0 表示等价。
    /// </summary>
    public static int CompareVersion(string? a, string? b)
    {
        var na = VersionNumRegex.Matches(a ?? "").Select(m => int.Parse(m.Value)).ToList();
        var nb = VersionNumRegex.Matches(b ?? "").Select(m => int.Parse(m.Value)).ToList();
        if (na.Count == 0 && nb.Count == 0)
            return string.Compare(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        int len = Math.Max(na.Count, nb.Count);
        for (int i = 0; i < len; i++)
        {
            int va = i < na.Count ? na[i] : 0;
            int vb = i < nb.Count ? nb[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        // 数字部分完全一致 → 比预发布后缀
        return ReleaseWeight(a).CompareTo(ReleaseWeight(b));
    }

    private static int ReleaseWeight(string? v)
    {
        string s = (v ?? "").ToLowerInvariant();
        if (s.Contains("alpha")) return 0;
        if (s.Contains("beta")) return 1;
        if (s.Contains("rc") || s.Contains("snapshot") || s.Contains("pre")) return 2;
        return 3;
    }

    /// <summary>把版本号解析成可排序的数值(用于 OrderByDescending)</summary>
    private static long ParseVersion(string? v)
    {
        var nums = VersionNumRegex.Matches(v ?? "").Select(m => int.Parse(m.Value)).Take(4).ToList();
        long acc = 0;
        for (int i = 0; i < 4; i++)
            acc = acc * 10000 + (i < nums.Count ? Math.Min(9999, nums[i]) : 0);
        return acc;
    }
}

/// <summary>.disabled 后缀裁剪(诊断时用于按 modid 反查文件名)</summary>
internal static class ModFileNameExtensions
{
    public static string TrimDisabled(this string fileName) =>
        fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length] : fileName;
}
