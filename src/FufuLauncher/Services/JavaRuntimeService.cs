// JavaRuntimeService.cs — Java 运行时隔离下载与管理服务
// 可爱的芙芙 - 段3 重构 / 批5 清理 Mojang Runtime
//
// 完整 JDK 通道:下载任意主版本 Java(支持 8~24 全部可用版本)
//   下载到:APP\mcGAME\runtimes\jdk-{major}-{arch}\
//   镜像源(由 ConfigService.JavaDownloadMirror 切换):
//     - Official   :Adoptium API(https://api.adoptium.net),支持 8/11/16~24
//     - Huaweicloud:华为云镜像(https://mirrors.huaweicloud.com/openjdk/),支持官方 OpenJDK 版本
//
// 下载 URL 经 DownloadService.GetSourceUrl 处理,自动跟随当前下载源

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
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
    public string Status { get; set; } = "";      // 已就绪 / 不完整
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
    // Adoptium API(官方源):可用版本列表 + 二进制下载
    private const string AdoptiumReleasesUrl = "https://api.adoptium.net/v3/info/available_releases";
    private const string AdoptiumBinaryUrlTemplate =
        "https://api.adoptium.net/v3/binary/latest/{0}/ga/windows/{1}/jre/hotspot/normal/eclipse";

    // 华为云镜像:OpenJDK 官方版本(只支持 LTS 8/11/17/21 等)
    private const string HuaweicloudOpenJdkUrlTemplate =
        "https://mirrors.huaweicloud.com/openjdk/{0}/OpenJDK{0}_U_{1}_windows.zip";

    // 完整 JDK 支持的主版本范围(实际可用由镜像源决定)
    private static readonly int[] AllKnownMajors =
        { 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24 };

    // 各镜像源支持的主版本(用于 UI 标记)
    private static readonly int[] HuaweicloudSupportedMajors = { 8, 11, 17, 21 };

    private readonly DownloadService _downloadService;
    private readonly ConfigService _configService;
    private readonly NativeInteropService _nativeInterop;
    // 拉取 runtime manifest 用 30s 超时(原 5 分钟过长,失败时用户长时间无反馈)
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public event Action<string>? ProgressChanged;

    public JavaRuntimeService(DownloadService downloadService,
                                 ConfigService configService,
                                 NativeInteropService nativeInterop)
    {
        _downloadService = downloadService;
        _configService = configService;
        _nativeInterop = nativeInterop;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/1.0.5.1 (Windows)");
    }

    /// <summary>Java 运行时根目录:APP\mcGAME\runtimes(自动下载的 Java 全部在此,每个版本一套,互不混用)</summary>
    public static string RuntimesDir => AppPaths.Runtimes;

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
        _ => "官方源 (Adoptium API)"
    };

    /// <summary>拉取当前镜像源下可下载的完整 JDK 版本列表</summary>
    public async Task<List<JdkVersionInfo>> FetchAvailableJdkVersionsAsync()
    {
        var mirror = _configService.Config.JavaDownloadMirror;
        var result = new List<JdkVersionInfo>();

        // Official / Huaweicloud:返回全部已知版本,UI 标记当前镜像源是否支持
        // Official 用 Adoptium API 拉取真实可用版本(失败时回退到硬编码)
        HashSet<int>? adoptiumAvailable = null;
        if (mirror == "Official")
        {
            adoptiumAvailable = await TryFetchAdoptiumAvailableReleasesAsync();
        }

        foreach (var major in AllKnownMajors)
        {
            bool supported;
            string display = $"Java {major}";
            bool lts = major == 8 || major == 11 || major == 17 || major == 21;

            if (mirror == "Official")
            {
                supported = adoptiumAvailable == null || adoptiumAvailable.Contains(major);
                display += supported ? " (Adoptium Temurin)" : " (当前镜像不支持)";
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
            // available_releases 数组
            if (doc.RootElement.TryGetProperty("available_releases", out var arr))
            {
                foreach (var v in arr.EnumerateArray())
                {
                    if (v.TryGetInt32(out int n)) result.Add(n);
                }
            }
            // available_lts_releases 数组也加入
            if (doc.RootElement.TryGetProperty("available_lts_releases", out var ltsArr))
            {
                foreach (var v in ltsArr.EnumerateArray())
                {
                    if (v.TryGetInt32(out int n)) result.Add(n);
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Java] 拉取 Adoptium 可用版本列表失败:{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 下载完整 JDK(任意主版本)到 runtimes/jdk-{major}-{arch}/,返回 java.exe 路径(null=失败)
    /// 根据 ConfigService.JavaDownloadMirror 选择下载源:
    ///   - Official   :Adoptium API(下载 zip 后解压)
    ///   - Huaweicloud:华为云镜像(下载 zip 后解压)
    /// </summary>
    public async Task<string?> DownloadJdkAsync(int majorVersion, string arch = "x64")
    {
        if (string.IsNullOrEmpty(arch)) arch = "x64";
        string archLower = arch.ToLowerInvariant();
        string dirName = GetJdkDirName(majorVersion, archLower);
        string localDir = Path.Combine(RuntimesDir, dirName);
        string javaExe = Path.Combine(localDir, "bin", "java.exe");
        if (File.Exists(javaExe)) return javaExe;  // 已下载

        var mirror = _configService.Config.JavaDownloadMirror;

        // Official / Huaweicloud:下载 zip 并解压
        string zipUrl = await BuildJdkZipUrlAsync(majorVersion, archLower, mirror);
        if (string.IsNullOrEmpty(zipUrl))
        {
            ProgressChanged?.Invoke($"当前镜像({mirror})不支持 Java {majorVersion}({archLower})");
            return null;
        }

        Directory.CreateDirectory(RuntimesDir);
        Directory.CreateDirectory(localDir);
        string zipPath = Path.Combine(RuntimesDir, $"{dirName}.zip");

        ProgressChanged?.Invoke($"下载 Java {majorVersion} {archLower} ({mirror})...");
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
            ProgressChanged?.Invoke("JDK 压缩包下载失败");
            return null;
        }

        // 解压到 localDir
        ProgressChanged?.Invoke("解压 JDK 压缩包...");
        bool extracted = await Task.Run(() =>
        {
            try
            {
                // 优先用原生解压(性能),失败回退到托管
                if (_nativeInterop.ExtractZip(zipPath, localDir)) return true;
                // 托管 fallback
                ZipFile.ExtractToDirectory(zipPath, localDir, overwriteFiles: true);
                return true;
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[Java] JDK 解压失败:{ex.Message}");
                return false;
            }
        });

        // 删除 zip 临时文件(无论解压成功与否)
        TryDeleteFile(zipPath);

        if (!extracted)
        {
            ProgressChanged?.Invoke("JDK 解压失败");
            return null;
        }

        // 解压后通常有一层子目录(jdk-17.0.9+9),把内部文件上提
        FlattenJdkDirectory(localDir);

        // 校验 java.exe 是否就位
        if (!File.Exists(javaExe))
        {
            ProgressChanged?.Invoke($"解压完成但未找到 java.exe(预期:{javaExe})");
            return null;
        }

        ProgressChanged?.Invoke($"Java {majorVersion} {archLower} 安装完成");

        // 完整性校验:实际执行 java -version 确认可用,避免下载完无法启动游戏的静默故障
        ProgressChanged?.Invoke("正在校验 Java 可执行性(java -version)...");
        if (!VerifyJavaIntegrity(javaExe))
        {
            App.WriteAppLog($"[Java] 下载后完整性校验失败:{javaExe}");
            ProgressChanged?.Invoke($"Java {majorVersion} 下载完成但无法执行,已标记损坏,可一键重新下载");
            return javaExe; // 返回路径,UI 列表会标记「已损坏」并提供重新下载按钮
        }
        App.WriteAppLog($"[Java] Java {majorVersion} ({archLower}) 下载并校验完成:{javaExe}");
        return javaExe;
    }

    /// <summary>删除指定已安装 Java 目录(用于损坏后重新下载前清理)</summary>
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

    /// <summary>构建 JDK zip 下载 URL(根据镜像源)。华为云需动态解析目录页取最新版本号</summary>
    private async Task<string> BuildJdkZipUrlAsync(int majorVersion, string archLower, string mirror)
    {
        if (mirror == "Official")
        {
            // Adoptium API:直接重定向到 GitHub Release zip
            return string.Format(AdoptiumBinaryUrlTemplate, majorVersion, archLower);
        }
        if (mirror == "Huaweicloud")
        {
            if (Array.IndexOf(HuaweicloudSupportedMajors, majorVersion) < 0) return "";
            if (archLower != "x64") return "";
            // 华为云 OpenJDK 镜像目录:https://mirrors.huaweicloud.com/openjdk/{major}/
            // 旧版用 _latest.zip 通配会 404,改为动态拉取目录页 HTML 解析最新版本文件名
            string listingUrl = $"https://mirrors.huaweicloud.com/openjdk/{majorVersion}/";
            try
            {
                ProgressChanged?.Invoke($"解析华为云 OpenJDK {majorVersion} 目录...");
                using var resp = await _http.GetAsync(listingUrl);
                resp.EnsureSuccessStatusCode();
                var html = await resp.Content.ReadAsStringAsync();
                // 匹配:OpenJDK17_U_jdk_x64_windows_hotspot_17.0.9_9.zip
                string pattern = $@"OpenJDK{majorVersion}_U_jdk_x64_windows_hotspot_(\d+)\.(\d+)\.(\d+)_(\d+)\.zip";
                var matches = System.Text.RegularExpressions.Regex.Matches(html, pattern);
                if (matches.Count == 0)
                {
                    App.WriteAppLog($"[Java] 华为云目录未匹配到 OpenJDK{majorVersion} zip 文件");
                    return "";
                }
                // 按版本号元组排序,取最大(最新)
                string bestFile = "";
                int[] bestVer = { -1, -1, -1, -1 };
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    var ver = new int[]
                    {
                        int.Parse(m.Groups[1].Value),
                        int.Parse(m.Groups[2].Value),
                        int.Parse(m.Groups[3].Value),
                        int.Parse(m.Groups[4].Value)
                    };
                    if (CompareVersionTuple(ver, bestVer) > 0)
                    {
                        bestVer = ver;
                        bestFile = m.Value;
                    }
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

    /// <summary>解压 JDK 后,把内层 jdk-x.x.x 子目录的内容上提到 localDir 根</summary>
    private static void FlattenJdkDirectory(string localDir)
    {
        try
        {
            // 查找唯一的内层目录(jdk-17.0.9+9 等)
            var subDirs = Directory.GetDirectories(localDir);
            if (subDirs.Length != 1) return;
            string inner = subDirs[0];
            // 检查内层是否含 bin/java.exe,否则不处理
            if (!File.Exists(Path.Combine(inner, "bin", "java.exe"))) return;

            // 把 inner/* 全部移动到 localDir
            foreach (var entry in Directory.EnumerateFileSystemEntries(inner))
            {
                string name = Path.GetFileName(entry);
                string dst = Path.Combine(localDir, name);
                if (Directory.Exists(entry))
                    Directory.Move(entry, dst);
                else
                    File.Move(entry, dst, overwrite: true);
            }
            Directory.Delete(inner, recursive: true);
        }
        catch (Exception ex)
        {
            // 上提失败不影响主流程(java.exe 可能在内层,后续启动器会读内层)
            App.WriteAppLog($"[Java] JDK 目录上提失败,内层目录可能保留:{ex.Message}");
        }
    }

    /// <summary>列出本地已安装的全部 Java 运行时(完整 JDK)</summary>
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
                bool ready = File.Exists(javaExe);
                // 文件存在时实际执行 java -version 校验,损坏的直接标记(允许一键重下)
                if (ready && !VerifyJavaIntegrity(javaExe))
                {
                    ready = false;
                    App.WriteAppLog($"[Java] 已安装运行时损坏:{javaExe}");
                }

                var entry = new InstalledJavaEntry
                {
                    Name = name,
                    Path = dir,
                    JavaExe = javaExe,
                    Status = ready ? "已就绪" : (File.Exists(javaExe) ? "已损坏" : "不完整"),
                    Kind = name.StartsWith("jdk-", StringComparison.OrdinalIgnoreCase) ? "完整 JDK" : "未知"
                };

                // 探测主版本与架构(用 java -version,失败时从目录名推断)
                if (ready && TryQueryJavaMeta(javaExe, out int major, out string arch, out string vendor))
                {
                    entry.MajorVersion = $"Java {major}";
                    entry.Architecture = arch;
                    entry.Kind += $" · {vendor}";
                }
                else
                {
                    // 从目录名推断主版本(jdk-{major}-{arch})
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
        return list;
    }

    /// <summary>调用 java -version 探测主版本与架构</summary>
    private static bool TryQueryJavaMeta(string javaExe, out int major, out string arch, out string vendor)
    {
        major = 0;
        arch = "";
        vendor = "";
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
            using var p = Process.Start(psi);
            if (p == null) return false;
            string output = p.StandardError.ReadToEnd();
            if (string.IsNullOrEmpty(output)) output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            var vm = Regex.Match(output, @"version\s+""(\d+)\.?(\d*)");
            if (vm.Success)
            {
                int m = int.Parse(vm.Groups[1].Value);
                if (m == 1 && vm.Groups[2].Success) m = int.Parse(vm.Groups[2].Value);
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
        catch (Exception ex)
        {
            App.WriteAppLog($"[Java] 探测 Java 元数据失败 {javaExe}:{ex.Message}");
            return false;
        }
    }

    /// <summary>校验本地 Java 完整性(文件存在 + java -version 可执行)</summary>
    public static bool VerifyJavaIntegrity(string javaExe)
    {
        if (string.IsNullOrEmpty(javaExe) || !File.Exists(javaExe)) return false;
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
            using var p = Process.Start(psi);
            if (p == null) return false;
            string output = p.StandardError.ReadToEnd();
            if (string.IsNullOrEmpty(output)) output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            // 输出含 "version" 字样视为通过
            return output.Contains("version", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Java] 完整性校验失败 {javaExe}:{ex.Message}");
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { App.WriteAppLog($"[Java] 删除文件失败 {path}:{ex.Message}"); }
    }
}
