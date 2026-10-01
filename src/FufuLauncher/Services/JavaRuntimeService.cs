// Copyright © FufuLauncher
//
// Java 运行时隔离下载与管理:
//   下载到 APP\MCGAME\runtimes\jdk-{major}-{arch}\,每个版本一套,互不混用。
//   镜像源(ConfigService.JavaDownloadMirror 切换):
//     - Official   :Adoptium API(api.adoptium.net),JDK 全版本
//     - Huaweicloud:华为云 OpenJDK 镜像,仅 LTS 8/11/17/21 x64
//     - Tsinghua   :清华 TUNA Adoptium 镜像,11+ 全版本(8 的命名特殊,走其它源)
// 下载 URL 经 DownloadService.GetSourceUrl 处理,自动跟随当前下载源。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

/// <summary>已安装的 Java 运行时条目(完整 JDK)</summary>
public class InstalledJavaEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string JavaExe { get; set; } = "";
    public string Status { get; set; } = "";      // 已就绪 / 已损坏 / 不完整
    public string Kind { get; set; } = "";        // 完整 JDK
    public string MajorVersion { get; set; } = ""; // 主版本号(可读)
    public string Architecture { get; set; } = "";// x64 / x86 / 未知
}

/// <summary>可下载的完整 JDK 版本条目</summary>
public class JdkVersionInfo
{
    public int MajorVersion { get; set; }
    public string DisplayName { get; set; } = "";
    public bool IsLts { get; set; }
    /// <summary>当前镜像源是否支持该版本</summary>
    public bool SupportedByCurrentMirror { get; set; } = true;

    /// <summary>ComboBox 直接 Add 该对象时显示文本,避免输出全命名空间类名</summary>
    public override string ToString() => DisplayName;
}

public class JavaRuntimeService
{
    // Adoptium API(官方源):可用版本列表 + 二进制下载(jdk 镜像,与 jdk-* 目录命名一致)
    private const string AdoptiumReleasesUrl = "https://api.adoptium.net/v3/info/available_releases";
    private const string AdoptiumBinaryUrlTemplate =
        "https://api.adoptium.net/v3/binary/latest/{0}/ga/windows/{1}/jdk/hotspot/normal/eclipse";

    // 华为云镜像只支持 LTS x64
    private static readonly int[] HuaweicloudSupportedMajors = { 8, 11, 17, 21 };

    // 清华 TUNA 镜像 Adoptium Temurin 11+(8 的 zip 命名为 8uXXXbXX 特殊格式,不参与动态解析)
    private static readonly int[] TsinghuaSupportedMajors =
        { 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24 };

    // 完整 JDK 支持的主版本范围(实际可用由镜像源决定)
    private static readonly int[] AllKnownMajors =
        { 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24 };

    private readonly DownloadService _downloadService;
    private readonly ConfigService _configService;
    private readonly NativeInteropService _nativeInterop;
    private readonly StorageGuardService _storageGuard;
    // 30s 超时(拉 manifest / 解析镜像目录);过长会让用户长时间无反馈
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>同版本并发下载互斥:第二路直接等第一路结果,避免双倍流量与解压冲突</summary>
    private readonly Dictionary<string, Task<string?>> _inflightDownloads = new();

    // ---- 系统 Java 扫描缓存(2026-09-25):30 分钟过期,避免每次切页都跑 java -version ----
    private List<InstalledJavaEntry>? _sysJavaCache;
    private DateTime _sysJavaCacheAt = DateTime.MinValue;
    private const int SysJavaCacheMinutes = 30;

    /// <summary>最近一次下载失败的具体原因(中文,供 UI 明确提示;成功时为空)</summary>
    public string LastDownloadError { get; private set; } = "";

    public event Action<string>? ProgressChanged;

    public JavaRuntimeService(DownloadService downloadService,
                              ConfigService configService,
                              NativeInteropService nativeInterop,
                              StorageGuardService storageGuard)
    {
        _downloadService = downloadService;
        _configService = configService;
        _nativeInterop = nativeInterop;
        _storageGuard = storageGuard;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/3.0 (Windows)");
    }

    /// <summary>Java 运行时根目录:自动下载的 Java 全部在此</summary>
    public static string RuntimesDir => AppPaths.Runtimes;

    /// <summary>
    /// 按游戏版本推荐 Java 主版本:≥1.20.5→21,1.17~1.20.4→17,≤1.16→8。
    /// 快照/无法解析的版本返回 17(主流默认)。
    /// </summary>
    public static int RecommendJavaMajor(string? versionId)
    {
        var m = Regex.Match(versionId ?? "", @"^(\d+)\.(\d+)(?:\.(\d+))?$");
        if (!m.Success) return 17;
        int major = int.Parse(m.Groups[1].Value);
        int minor = int.Parse(m.Groups[2].Value);
        int patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        if (major > 1) return 21;
        if (minor >= 21) return 21;
        if (minor == 20 && patch >= 5) return 21;
        if (minor >= 17) return 17;
        return 8;   // 1.16 及以下(含 1.12~1.16 提示用 Java 8)
    }

    /// <summary>从 runtimes 公共池查找指定主版本的已就绪 Java</summary>
    public InstalledJavaEntry? FindReadyRuntime(int major)
    {
        try
        {
            return ListInstalledRuntimes().FirstOrDefault(r =>
                r.Status == "已就绪" && r.MajorVersion == $"Java {major}");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Java] 查找 runtimes Java {major} 失败:{ex.Message}");
            return null;
        }
    }

    /// <summary>获取完整 JDK 的本地 java.exe 路径</summary>
    public static string GetLocalJdkPath(int majorVersion, string arch = "x64")
    {
        string name = GetJdkDirName(majorVersion, arch);
        return Path.Combine(RuntimesDir, name, "bin", "java.exe");
    }

    /// <summary>完整 JDK 的目录名约定</summary>
    public static string GetJdkDirName(int majorVersion, string arch) =>
        $"jdk-{majorVersion}-{arch.ToLowerInvariant()}";

    /// <summary>获取当前镜像源显示名</summary>
    public string GetCurrentMirrorLabel() => _configService.Config.JavaDownloadMirror switch
    {
        "Official" => "官方源 (Adoptium API)",
        "Huaweicloud" => "国内镜像 (华为云)",
        "Tsinghua" => "清华 TUNA 镜像 (Adoptium Temurin)",
        _ => "官方源 (Adoptium API)"
    };

    // ==================== 版本列表 ====================

    /// <summary>拉取当前镜像源下可下载的完整 JDK 版本列表</summary>
    public async Task<List<JdkVersionInfo>> FetchAvailableJdkVersionsAsync()
    {
        var mirror = _configService.Config.JavaDownloadMirror;
        var result = new List<JdkVersionInfo>();

        // Official 用 Adoptium API 拉取真实可用版本(失败时回退到全部视为可用)
        HashSet<int>? adoptiumAvailable = null;
        if (mirror == "Official")
            adoptiumAvailable = await TryFetchAdoptiumAvailableReleasesAsync();

        foreach (var major in AllKnownMajors)
        {
            bool lts = major is 8 or 11 or 17 or 21;
            bool supported;
            string display = $"Java {major}";

            if (mirror == "Official")
            {
                supported = adoptiumAvailable == null || adoptiumAvailable.Contains(major);
                display += supported ? " (Adoptium Temurin)" : " (当前镜像不支持)";
            }
            else if (mirror == "Tsinghua")
            {
                supported = Array.IndexOf(TsinghuaSupportedMajors, major) >= 0;
                display += supported ? " (清华 TUNA · Temurin)" : " (当前镜像不支持)";
            }
            else // Huaweicloud
            {
                supported = Array.IndexOf(HuaweicloudSupportedMajors, major) >= 0;
                display += supported ? " (华为云 OpenJDK)" : " (当前镜像不支持)";
            }
            if (lts) display += " [LTS]";

            result.Add(new JdkVersionInfo
            {
                MajorVersion = major,
                DisplayName = display,
                IsLts = lts,
                SupportedByCurrentMirror = supported
            });
        }
        return result;
    }

    /// <summary>尝试拉取 Adoptium 实际可用版本列表(网络失败返回 null)</summary>
    private async Task<HashSet<int>?> TryFetchAdoptiumAvailableReleasesAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(AdoptiumReleasesUrl);
            using var doc = JsonDocument.Parse(json);
            var result = new HashSet<int>();
            foreach (var key in new[] { "available_releases", "available_lts_releases" })
            {
                if (doc.RootElement.TryGetProperty(key, out var arr))
                    foreach (var v in arr.EnumerateArray())
                        if (v.TryGetInt32(out int n)) result.Add(n);
            }
            return result;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Java] 拉取 Adoptium 可用版本列表失败:{ex.Message}");
            return null;
        }
    }

    // ==================== 下载 ====================

    /// <summary>
    /// 下载完整 JDK 到 runtimes/jdk-{major}-{arch}/,返回 java.exe 路径(null=失败)。
    /// 同版本并发调用复用同一任务,避免双倍流量。
    /// </summary>
    public Task<string?> DownloadJdkAsync(int majorVersion, string arch = "x64")
    {
        if (string.IsNullOrEmpty(arch)) arch = "x64";
        string key = $"{majorVersion}-{arch.ToLowerInvariant()}";
        lock (_inflightDownloads)
        {
            if (_inflightDownloads.TryGetValue(key, out var inflight)) return inflight;
            var task = DownloadJdkCoreAsync(majorVersion, arch);
            _inflightDownloads[key] = task;
            // 完成后从表里摘掉,下次调用重新评估(损坏重下等场景)
            _ = task.ContinueWith(_ => { lock (_inflightDownloads) { _inflightDownloads.Remove(key); } });
            return task;
        }
    }

    private async Task<string?> DownloadJdkCoreAsync(int majorVersion, string arch)
    {
        LastDownloadError = "";
        string archLower = arch.ToLowerInvariant();
        string dirName = GetJdkDirName(majorVersion, archLower);
        string localDir = Path.Combine(RuntimesDir, dirName);
        string javaExe = Path.Combine(localDir, "bin", "java.exe");
        if (File.Exists(javaExe)) return javaExe;  // 已下载

        // 跨源兜底链:先按选定镜像,不行依次 清华 TUNA(国内最稳) → 华为云 → Adoptium 官方(全版本);
        // 只有全部源都无适配包才判定失败,并给出明确文字
        var selected = _configService.Config.JavaDownloadMirror;
        string zipUrl = "";
        string usedMirror = "";
        foreach (var m in MirrorFallbackChain(selected))
        {
            string u = await BuildJdkZipUrlAsync(majorVersion, archLower, m);
            if (!string.IsNullOrEmpty(u)) { zipUrl = u; usedMirror = m; break; }
        }
        if (string.IsNullOrEmpty(zipUrl))
        {
            LastDownloadError =
                $"未找到 Java {majorVersion}({archLower})的适配安装包:选定镜像 {MirrorLabel(selected)} 及兜底源"
                + "(清华 TUNA / 华为云 / Adoptium 官方)均不支持该版本。\n"
                + (majorVersion <= 10
                    ? "说明:Java 8〜10 国内镜像覆盖有限,请切换「Adoptium 官方源」后重试。"
                    : "说明:请确认版本主号正确(常用 8 / 17 / 21),或稍后重试。");
            ProgressChanged?.Invoke(LastDownloadError.Replace("\n", " "));
            App.WriteAppLog($"[Java] {LastDownloadError.Replace("\n", " ")}");
            return null;
        }
        if (usedMirror != selected)
            ProgressChanged?.Invoke($"选定镜像无适配包,已自动切换:{MirrorLabel(selected)} → {MirrorLabel(usedMirror)}");

        Directory.CreateDirectory(RuntimesDir);
        Directory.CreateDirectory(localDir);
        string zipPath = Path.Combine(RuntimesDir, $"{dirName}.zip");

        ProgressChanged?.Invoke($"下载 Java {majorVersion} {archLower} ({MirrorLabel(usedMirror)})...");
        var task = new DownloadTaskItem
        {
            Url = zipUrl,
            LocalPath = zipPath,
            Category = DownloadCategory.Java,
            IsSharded = true  // JDK zip 通常 >= 50MB,启用分片下载
        };
        bool ok = await _downloadService.DownloadAllAsync(new() { task });
        if (!ok || !File.Exists(zipPath))
        {
            LastDownloadError = $"JDK 压缩包下载失败({MirrorLabel(usedMirror)}):网络中断或镜像源临时故障,请稍后重试或切换镜像源。";
            ProgressChanged?.Invoke(LastDownloadError);
            return null;
        }

        // 解压前预检:磁盘空间(解压后约为压缩包体积 3 倍) + 目录写权限
        long zipSize = 0;
        try { zipSize = new FileInfo(zipPath).Length; } catch { }
        var pre = _storageGuard.Precheck(localDir, zipSize * 3);
        if (pre.Result != StorageCheckResult.Ok)
        {
            LastDownloadError = $"解压预检未通过:{pre.Message}";
            ProgressChanged?.Invoke(LastDownloadError);
            TryDeleteFile(zipPath);
            return null;
        }

        ProgressChanged?.Invoke("解压 JDK 压缩包...");
        bool extracted = await Task.Run(() =>
        {
            try
            {
                // 优先原生解压(性能),失败回退托管
                if (_nativeInterop.ExtractZip(zipPath, localDir)) return true;
                ZipFile.ExtractToDirectory(zipPath, localDir, overwriteFiles: true);
                return true;
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[Java] JDK 解压失败:{ex.Message}");
                return false;
            }
        });

        TryDeleteFile(zipPath);   // 无论解压成败都删临时 zip

        if (!extracted)
        {
            // 失败回滚:删除半成品目录,避免残留损坏 Java 导致后续误判已安装
            try { if (Directory.Exists(localDir)) Directory.Delete(localDir, recursive: true); }
            catch (Exception rex) { App.WriteAppLog($"[Java] 回滚清理失败:{rex.Message}"); }
            ProgressChanged?.Invoke("JDK 解压失败(压缩包可能损坏,已回滚清理)");
            LastDownloadError = "JDK 压缩包解压失败(文件可能下载不完整),已回滚清理,请重试下载。";
            return null;
        }

        // 解压后通常有 1~2 层子目录(jdk-17.0.9+9),把内部文件上提
        FlattenJdkDirectory(localDir);

        if (!File.Exists(javaExe))
        {
            ProgressChanged?.Invoke($"解压完成但未找到 java.exe(预期:{javaExe})");
            return null;
        }

        ProgressChanged?.Invoke($"Java {majorVersion} {archLower} 安装完成");

        // 完整性校验:实际执行 java -version 确认可用,避免下载完无法启动的静默故障
        ProgressChanged?.Invoke("正在校验 Java 可执行性(java -version)...");
        if (!VerifyJavaIntegrity(javaExe))
        {
            App.WriteAppLog($"[Java] 下载后完整性校验失败:{javaExe}");
            ProgressChanged?.Invoke($"Java {majorVersion} 下载完成但无法执行,已标记损坏,可重新下载");
            return javaExe; // 返回路径,UI 列表标记「已损坏」并提供重新下载按钮
        }
        App.WriteAppLog($"[Java] Java {majorVersion} ({archLower}) 下载并校验完成:{javaExe}");
        return javaExe;
    }

    /// <summary>删除指定已安装 Java 目录(损坏后重新下载前清理)</summary>
    public static bool RemoveRuntimeDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Java] 删除运行时目录失败 {dir}:{ex.Message}");
            return false;
        }
    }

    /// <summary>镜像显示名(内部键 → 中文)</summary>
    private static string MirrorLabel(string mirror) => mirror switch
    {
        "Official" => "Adoptium 官方源",
        "Huaweicloud" => "华为云镜像",
        "Tsinghua" => "清华 TUNA 镜像",
        _ => mirror
    };

    /// <summary>跨源兜底链:选定源优先,其余按国内稳定性排序(清华 → 华为 → 官方),去重</summary>
    private static string[] MirrorFallbackChain(string selected)
    {
        var chain = new List<string> { selected, "Tsinghua", "Huaweicloud", "Official" };
        return chain.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();
    }

    /// <summary>构建 JDK zip 下载 URL(根据镜像源)。华为云需动态解析目录页取最新版本号</summary>
    private async Task<string> BuildJdkZipUrlAsync(int majorVersion, string archLower, string mirror)
    {
        if (mirror == "Official")
        {
            // Adoptium API:302 重定向到 GitHub Release zip
            return string.Format(AdoptiumBinaryUrlTemplate, majorVersion, archLower);
        }
        if (mirror == "Tsinghua")
        {
            if (Array.IndexOf(TsinghuaSupportedMajors, majorVersion) < 0) return "";
            // 目录页动态解析最新 zip 文件名(Adoptium 官方命名 OpenJDK{major}U-jdk_*)
            string listingUrl = $"https://mirrors.tuna.tsinghua.edu.cn/Adoptium/{majorVersion}/jdk/{archLower}/windows/";
            try
            {
                ProgressChanged?.Invoke($"解析清华 TUNA Adoptium {majorVersion} 目录...");
                using var resp = await _http.GetAsync(listingUrl);
                resp.EnsureSuccessStatusCode();
                var html = await resp.Content.ReadAsStringAsync();
                // 匹配:OpenJDK17U-jdk_x64_windows_hotspot_17.0.19_10.zip
                string pattern = $@"OpenJDK{majorVersion}U-jdk_{archLower}_windows_hotspot_(\d+)\.(\d+)\.(\d+)_(\d+)\.zip";
                var matches = Regex.Matches(html, pattern);
                if (matches.Count == 0)
                {
                    App.WriteAppLog($"[Java] 清华 TUNA 目录未匹配到 OpenJDK{majorVersion}U zip 文件");
                    return "";
                }
                string bestFile = "";
                int[] bestVer = { -1, -1, -1, -1 };
                foreach (Match m in matches)
                {
                    var ver = new[]
                    {
                        int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                        int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)
                    };
                    if (CompareVersionTuple(ver, bestVer) > 0) { bestVer = ver; bestFile = m.Value; }
                }
                return string.IsNullOrEmpty(bestFile) ? "" : listingUrl + bestFile;
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[Java] 解析清华 TUNA Adoptium 目录失败:{ex.Message}");
                return "";
            }
        }
        if (mirror == "Huaweicloud")
        {
            if (Array.IndexOf(HuaweicloudSupportedMajors, majorVersion) < 0) return "";
            if (archLower != "x64") return "";
            // 目录页动态解析最新 zip 文件名(_latest.zip 通配会 404)
            string listingUrl = $"https://mirrors.huaweicloud.com/openjdk/{majorVersion}/";
            try
            {
                ProgressChanged?.Invoke($"解析华为云 OpenJDK {majorVersion} 目录...");
                using var resp = await _http.GetAsync(listingUrl);
                resp.EnsureSuccessStatusCode();
                var html = await resp.Content.ReadAsStringAsync();
                // 匹配:OpenJDK17_U_jdk_x64_windows_hotspot_17.0.9_9.zip
                string pattern = $@"OpenJDK{majorVersion}_U_jdk_x64_windows_hotspot_(\d+)\.(\d+)\.(\d+)_(\d+)\.zip";
                var matches = Regex.Matches(html, pattern);
                if (matches.Count == 0)
                {
                    App.WriteAppLog($"[Java] 华为云目录未匹配到 OpenJDK{majorVersion} zip 文件");
                    return "";
                }
                // 按版本号元组排序,取最大(最新)
                string bestFile = "";
                int[] bestVer = { -1, -1, -1, -1 };
                foreach (Match m in matches)
                {
                    var ver = new[]
                    {
                        int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                        int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)
                    };
                    if (CompareVersionTuple(ver, bestVer) > 0) { bestVer = ver; bestFile = m.Value; }
                }
                return string.IsNullOrEmpty(bestFile) ? "" : listingUrl + bestFile;
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[Java] 解析华为云 OpenJDK 目录失败:{ex.Message}");
                return "";
            }
        }
        return "";
    }

    /// <summary>逐位比较版本号元组(长度不足补 0)</summary>
    private static int CompareVersionTuple(int[] a, int[] b)
    {
        int len = Math.Max(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int av = i < a.Length ? a[i] : 0;
            int bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    /// <summary>解压 JDK 后,把内层子目录内容上提到 localDir 根(最多两层,兼容个别发行版嵌套)</summary>
    private static void FlattenJdkDirectory(string localDir)
    {
        try
        {
            for (int depth = 0; depth < 2; depth++)
            {
                var subDirs = Directory.GetDirectories(localDir);
                if (subDirs.Length != 1) return;
                string inner = subDirs[0];
                if (!File.Exists(Path.Combine(inner, "bin", "java.exe"))) return;

                foreach (var entry in Directory.EnumerateFileSystemEntries(inner))
                {
                    string dst = Path.Combine(localDir, Path.GetFileName(entry));
                    if (Directory.Exists(entry)) Directory.Move(entry, dst);
                    else File.Move(entry, dst, overwrite: true);
                }
                Directory.Delete(inner, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // 上提失败不影响主流程(校验步骤会发现 java.exe 缺失)
            App.WriteAppLog($"[Java] JDK 目录上提失败,内层目录可能保留:{ex.Message}");
        }
    }

    // ==================== 本地列表与探测 ====================

    /// <summary>列出本地已安装的全部 Java 运行时(含 java -version 实探,慢操作建议 Task.Run)</summary>
    public List<InstalledJavaEntry> ListInstalledRuntimes()
    {
        var list = new List<InstalledJavaEntry>();
        try
        {
            if (!Directory.Exists(RuntimesDir)) return list;

            foreach (var dir in Directory.EnumerateDirectories(RuntimesDir))
            {
                string name = Path.GetFileName(dir);
                string javaExe = Path.Combine(dir, "bin", "java.exe");
                bool exists = File.Exists(javaExe);
                // 文件存在时实际执行 java -version 校验,损坏的直接标记(允许重新下载)
                bool ready = exists && VerifyJavaIntegrity(javaExe);
                if (exists && !ready)
                    App.WriteAppLog($"[Java] 已安装运行时损坏:{javaExe}");

                var entry = new InstalledJavaEntry
                {
                    Name = name,
                    Path = dir,
                    JavaExe = javaExe,
                    Status = ready ? "已就绪" : (exists ? "已损坏" : "不完整"),
                    Kind = name.StartsWith("jdk-", StringComparison.OrdinalIgnoreCase) ? "完整 JDK" : "未知"
                };

                // 探测主版本与架构(java -version,失败时从目录名 jdk-{major}-{arch} 推断)
                if (ready && TryQueryJavaMeta(javaExe, out int major, out string arch, out string vendor))
                {
                    entry.MajorVersion = $"Java {major}";
                    entry.Architecture = arch;
                    entry.Kind += $" · {vendor}";
                }
                else
                {
                    var m = Regex.Match(name, @"jdk-(\d+)-(x64|x86)", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        entry.MajorVersion = $"Java {m.Groups[1].Value}";
                        entry.Architecture = m.Groups[2].Value.ToLowerInvariant();
                    }
                }
                list.Add(entry);
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Java] 列出已安装运行时失败:{ex.Message}");
        }

        // 2026-09-25 完善:合并系统已装 Java(AutoScanJavaOnStartup 开关,带缓存)。
        // 用户机器上已有 JDK 时直接可用,不必强制走自管下载;按 java.exe 路径去重
        if (_configService.Config.AutoScanJavaOnStartup)
        {
            try
            {
                foreach (var s in ScanSystemJavaPaths())
                    if (!list.Any(x => string.Equals(x.JavaExe, s.JavaExe, StringComparison.OrdinalIgnoreCase)))
                        list.Add(s);
            }
            catch { /* 系统扫描失败不影响自管列表 */ }
        }
        return list;
    }

    /// <summary>扫描 Windows 常见系统 Java 安装位置(30 分钟缓存)。返回的条目 Kind 标记「系统 Java」,
    /// 与自管 RuntimesDir 去重;找不到或关闭开关时返回空列表。</summary>
    public List<InstalledJavaEntry> ScanSystemJavaPaths()
    {
        if (!_configService.Config.AutoScanJavaOnStartup)
            return new List<InstalledJavaEntry>();
        if (_sysJavaCache != null && (DateTime.Now - _sysJavaCacheAt).TotalMinutes < SysJavaCacheMinutes)
            return _sysJavaCache;

        var found = new List<InstalledJavaEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var roots = new List<string>();
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var baseDir in new[]
            {
                Path.Combine(pf, "Java"), Path.Combine(pf, "Eclipse Adoptium"),
                Path.Combine(pf, "Microsoft"), Path.Combine(pf, "Zulu"),
                Path.Combine(pf, "Amazon Corretto"), Path.Combine(pf, "BellSoft"),
                Path.Combine(pf86, "Java")
            })
            {
                if (Directory.Exists(baseDir))
                    roots.AddRange(Directory.EnumerateDirectories(baseDir));
            }
            string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(javaHome) && Directory.Exists(javaHome)) roots.Add(javaHome);
            // 用户级安装(非管理员装的 JDK 常见位置)
            string localPrograms = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            foreach (var sub in new[] { "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft" })
            {
                string d = Path.Combine(localPrograms, sub);
                if (Directory.Exists(d)) roots.AddRange(Directory.EnumerateDirectories(d));
            }

            foreach (var root in roots)
            {
                string javaExe = Path.Combine(root, "bin", "java.exe");
                if (!File.Exists(javaExe)) continue;
                string full = Path.GetFullPath(root);
                if (!seen.Add(full)) continue;
                bool ready = VerifyJavaIntegrity(javaExe);
                var entry = new InstalledJavaEntry
                {
                    Name = Path.GetFileName(full.TrimEnd('\\', '/')) + " (系统)",
                    Path = full,
                    JavaExe = javaExe,
                    Status = ready ? "已就绪" : "已损坏",
                    Kind = "系统 Java"
                };
                if (ready && TryQueryJavaMeta(javaExe, out int major, out string arch, out string vendor))
                {
                    entry.MajorVersion = $"Java {major}";
                    entry.Architecture = arch;
                    entry.Kind += $" · {vendor}";
                }
                found.Add(entry);
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Java] 系统扫描失败:{ex.Message}");
        }
        _sysJavaCache = found;
        _sysJavaCacheAt = DateTime.Now;
        return found;
    }

    /// <summary>调用 java -version 探测主版本、架构与发行商</summary>
    private static bool TryQueryJavaMeta(string javaExe, out int major, out string arch, out string vendor)
    {
        major = 0;
        arch = "";
        vendor = "";
        string output = RunJavaVersion(javaExe);
        if (string.IsNullOrEmpty(output)) return false;

        var vm = Regex.Match(output, @"version\s+""(\d+)\.?(\d*)");
        if (vm.Success)
        {
            int m = int.Parse(vm.Groups[1].Value);
            if (m == 1 && vm.Groups[2].Success && vm.Groups[2].Length > 0) m = int.Parse(vm.Groups[2].Value);
            major = m;
        }
        arch = output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase) ? "x64" : "x86";

        if (output.Contains("Temurin", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Adoptium", StringComparison.OrdinalIgnoreCase))
            vendor = "Eclipse Adoptium";
        else if (output.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            vendor = "Microsoft";
        else if (output.Contains("Oracle", StringComparison.OrdinalIgnoreCase))
            vendor = "Oracle";
        else
            vendor = "OpenJDK";

        return major > 0;
    }

    /// <summary>校验本地 Java 完整性(文件存在 + java -version 可执行)</summary>
    public static bool VerifyJavaIntegrity(string javaExe)
    {
        if (string.IsNullOrEmpty(javaExe) || !File.Exists(javaExe)) return false;
        string output = RunJavaVersion(javaExe);
        return output.Contains("version", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>执行 java -version 并读取输出;5 秒超时强杀,防止挂死卡 UI 线程。
    /// 异步读取 stderr/stdout 两个流,避免管道缓冲写满死锁。</summary>
    private static string RunJavaVersion(string javaExe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = new Process { StartInfo = psi };
            var errTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errSb = new StringBuilder();
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) errTcs.TrySetResult(errSb.ToString());
                else errSb.AppendLine(e.Data);
            };
            p.Start();
            p.BeginErrorReadLine();
            string stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { }
                App.WriteAppLog($"[Java] java -version 超时已强杀:{javaExe}");
                return "";
            }
            string stderr = errTcs.Task.Wait(2000) ? errTcs.Task.Result : "";
            return string.IsNullOrEmpty(stderr) ? stdout : stderr;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Java] 执行 java -version 失败 {javaExe}:{ex.Message}");
            return "";
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { App.WriteAppLog($"[Java] 删除文件失败 {path}:{ex.Message}"); }
    }
}
