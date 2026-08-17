// EnvironmentCheckService.cs — 程序启动环境自检服务(完整重写)
// 可爱的芙芙
//
// 检测项:
// 1. .NET 8 Desktop 运行时(本进程已加载 → 必然存在,但验证版本完整性)
// 2. 操作系统架构(必须 64 位)
// 3. Visual C++ 2015-2022 可再发行组件(LWJGL 原生库依赖)
// 4. Java 运行时(runtimes 目录,非系统 Java)
// 5. 磁盘剩余空间(≥5GB)
// 6. 网络连通(Mojang + BMCLAPI)
//
// 每个检测项带 DownloadUrl,UI 弹窗可直接跳转官方下载页

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

/// <summary>单个检测项</summary>
public class CheckItem
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public string Status { get; set; } = "";      // "✓" / "✗" / "⚠"
    public string Message { get; set; } = "";
    /// <summary>失败时的官方下载 URL(为空表示无需下载)</summary>
    public string DownloadUrl { get; set; } = "";
    public string DownloadLabel { get; set; } = "";
    /// <summary>严重级别:Critical=阻止启动,Warning=仅提示</summary>
    public SeverityLevel Severity { get; set; } = SeverityLevel.Warning;
    /// <summary>失败且有下载链接时为 true,供 UI 显示下载按钮</summary>
    public bool HasDownload => !Ok && !string.IsNullOrEmpty(DownloadUrl);
    /// <summary>分级标识文字:仅失败项显示(致命 / 警告)</summary>
    public string SeverityLabel => !Ok && Severity == SeverityLevel.Critical ? "致命" : (!Ok ? "警告" : "");
    /// <summary>是否需要显示分级标识</summary>
    public bool ShowSeverityLabel => SeverityLabel.Length > 0;
    /// <summary>是否致命失败(红色高亮)</summary>
    public bool IsCriticalFailure => !Ok && Severity == SeverityLevel.Critical;
}

public enum SeverityLevel { Info, Warning, Critical }

/// <summary>环境自检汇总结果</summary>
public class EnvironmentCheckResult
{
    public List<CheckItem> Items { get; } = new();
    public bool AllOk => Items.All(i => i.Ok);
    public bool HasCriticalFailure => Items.Any(i => !i.Ok && i.Severity == SeverityLevel.Critical);

    // 兼容旧接口
    public bool DotNetRuntimeOk => Items.FirstOrDefault(i => i.Name.Contains(".NET"))?.Ok ?? false;
    public string DotNetRuntimeMessage => Items.FirstOrDefault(i => i.Name.Contains(".NET"))?.Message ?? "";
    public bool OsArchOk => Items.FirstOrDefault(i => i.Name.Contains("架构"))?.Ok ?? false;
    public string OsArchMessage => Items.FirstOrDefault(i => i.Name.Contains("架构"))?.Message ?? "";
    public bool DiskSpaceOk => Items.FirstOrDefault(i => i.Name.Contains("磁盘"))?.Ok ?? false;
    public string DiskSpaceMessage => Items.FirstOrDefault(i => i.Name.Contains("磁盘"))?.Message ?? "";
    public bool NetworkOk => Items.FirstOrDefault(i => i.Name.Contains("网络"))?.Ok ?? false;
    public string NetworkMessage => Items.FirstOrDefault(i => i.Name.Contains("网络"))?.Message ?? "";
    public int JavaCount { get; set; }
    public string JavaMessage => Items.FirstOrDefault(i => i.Name.Contains("Java"))?.Message ?? "";
}

public class EnvironmentCheckService
{
    private readonly NetworkService _networkService;
    private readonly ConfigService _configService;
    private readonly JavaRuntimeService _javaRuntimeService;
    private readonly NativeInteropService _nativeInterop;

    public EnvironmentCheckResult LastResult { get; private set; } = new();

    // ==================== 下载 URL 常量 ====================

    public const string DotNet8DesktopUrl = "https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0";
    public const string VCppRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    public const string AdoptiumJdkUrl = "https://adoptium.net/temurin/releases/";
    public const string OracleJdkUrl = "https://www.oracle.com/java/technologies/downloads/";

    public EnvironmentCheckService(JavaScanService javaScanService,
                                       NetworkService networkService,
                                       ConfigService configService,
                                       JavaRuntimeService javaRuntimeService,
                                       NativeInteropService nativeInterop)
    {
        _networkService = networkService;
        _configService = configService;
        _javaRuntimeService = javaRuntimeService;
        _nativeInterop = nativeInterop;
        // javaScanService 保留参数(DI 兼容),实际使用 javaRuntimeService 扫描 runtimes 目录
    }

    public async Task RunEnvironmentCheckAsync()
    {
        var result = new EnvironmentCheckResult();

        // 1. .NET 8 Desktop 运行时
        CheckDotNetRuntime(result);

        // 2. 操作系统架构
        CheckOsArchitecture(result);

        // 3. Visual C++ 可再发行组件
        CheckVCppRedist(result);

        // 4. 磁盘空间
        CheckDiskSpace(result);

        // 4.5 数据目录读写权限(致命项:无法写入则启动器无法工作)
        CheckDataDirPermission(result);

        // 5. 网络连通
        await CheckNetworkAsync(result);

        // 6. Java 运行时(runtimes 目录)
        CheckJavaRuntimes(result);

        // 7. 外部依赖模块完整性(C++ 原生 DLL,缺失时自动降级托管实现)
        CheckNativeDependency(result);

        LastResult = result;
    }

    /// <summary>用浏览器打开指定 URL</summary>
    public static void OpenDownloadUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[环境] 打开下载链接失败:{ex.Message}");
        }
    }

    public string GetStatusSummary()
    {
        var r = LastResult;
        if (r.AllOk)
            return $"环境正常 · Java 运行时:{r.JavaCount} 个";

        var issues = r.Items.Where(i => !i.Ok).Select(i => i.Name).ToList();
        return issues.Count == 0 ? "就绪" : "存在问题:" + string.Join("、", issues);
    }

    // ==================== 各项检测 ====================

    private void CheckDotNetRuntime(EnvironmentCheckResult r)
    {
        // 自包含发布:.NET 8 Desktop Runtime 已打包在发布目录,无需目标机安装
        var ver = Environment.Version;
        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        bool isSelfContained = runtimeDir.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);

        r.Items.Add(new CheckItem
        {
            Name = ".NET 8 Desktop 运行时",
            Ok = true,
            Status = "✓",
            Message = isSelfContained
                ? $"自包含模式 v{ver} (运行时已打包,无需额外安装)"
                : $"已加载 v{ver} ({runtimeDir})",
            Severity = SeverityLevel.Critical,
            DownloadUrl = DotNet8DesktopUrl,
            DownloadLabel = "下载 .NET 8 Desktop Runtime"
        });
    }

    private void CheckOsArchitecture(EnvironmentCheckResult r)
    {
        var arch = RuntimeInformation.OSArchitecture;
        bool ok = arch != Architecture.X86;

        r.Items.Add(new CheckItem
        {
            Name = "操作系统架构",
            Ok = ok,
            Status = ok ? "✓" : "✗",
            Message = ok ? $"64 位系统 ({arch})" : $"检测到 {arch},启动器仅支持 64 位 Windows",
            Severity = ok ? SeverityLevel.Info : SeverityLevel.Critical
        });
    }

    private void CheckVCppRedist(EnvironmentCheckResult r)
    {
        // 检测 Visual C++ 2015-2022 Redistributable (x64)
        // 通过检查注册表项判断是否已安装
        bool installed = IsVCppRedistInstalled();

        r.Items.Add(new CheckItem
        {
            Name = "Visual C++ 运行库",
            Ok = installed,
            Status = installed ? "✓" : "✗",
            Message = installed
                ? "VC++ 2015-2022 可再发行组件(x64)已安装"
                : "未检测到 VC++ 2015-2022 Redistributable(x64),游戏原生库(LWJGL)可能无法加载",
            Severity = installed ? SeverityLevel.Info : SeverityLevel.Warning,
            DownloadUrl = VCppRedistUrl,
            DownloadLabel = "下载 VC++ Redistributable (x64)"
        });
    }

    private static bool IsVCppRedistInstalled()
    {
        try
        {
            // 方法1: 检查注册表 Uninstall 项
            string regPath = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64";
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
            if (key != null)
            {
                var installed = key.GetValue("Installed");
                if (installed is int val && val == 1) return true;
            }

            // 方法2: 检查关键 DLL 是否存在于 System32
            string sysDir = Environment.SystemDirectory; // C:\Windows\System32
            string[] dlls = { "vcruntime140.dll", "msvcp140.dll", "vcruntime140_1.dll" };
            int found = dlls.Count(d => File.Exists(Path.Combine(sysDir, d)));
            return found >= 2; // 至少 2 个核心 DLL 存在即视为已安装
        }
        catch
        {
            return false;
        }
    }

    private void CheckDiskSpace(EnvironmentCheckResult r)
    {
        try
        {
            string baseDir = string.IsNullOrEmpty(AppPaths.Root) ? App.AppDataDir : AppPaths.Root;
            string? root = Path.GetPathRoot(baseDir);
            if (string.IsNullOrEmpty(root))
                root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

            var drive = new DriveInfo(root);
            long freeGB = drive.AvailableFreeSpace / (1024L * 1024 * 1024);

            bool ok = freeGB >= 5;
            r.Items.Add(new CheckItem
            {
                Name = "磁盘剩余空间",
                Ok = ok,
                Status = ok ? "✓" : "⚠",
                Message = ok
                    ? $"{drive.Name.TrimEnd('\\')} 盘剩余 {freeGB} GB"
                    : $"磁盘空间不足:仅剩 {freeGB} GB(建议至少 5GB)",
                Severity = ok ? SeverityLevel.Info : SeverityLevel.Warning
            });
        }
        catch (Exception ex)
        {
            r.Items.Add(new CheckItem
            {
                Name = "磁盘剩余空间",
                Ok = false,
                Status = "⚠",
                Message = $"检测失败:{ex.Message}",
                Severity = SeverityLevel.Warning
            });
        }
    }

    private async Task CheckNetworkAsync(EnvironmentCheckResult r)
    {
        try
        {
            var connectivity = await _networkService.TestConnectivityAsync();
            bool ok = connectivity.MojangOk || connectivity.BmclapiOk;

            r.Items.Add(new CheckItem
            {
                Name = "网络连通",
                Ok = ok,
                Status = ok ? "✓" : "✗",
                Message = connectivity.Message,
                Severity = ok ? SeverityLevel.Info : SeverityLevel.Warning,
                DownloadUrl = "",
                DownloadLabel = ""
            });
        }
        catch (Exception ex)
        {
            r.Items.Add(new CheckItem
            {
                Name = "网络连通",
                Ok = false,
                Status = "✗",
                Message = $"网络检测异常:{ex.Message}",
                Severity = SeverityLevel.Warning
            });
        }
    }

    /// <summary>数据目录(APP\MCGAME)读写权限检测:写探针→读回→删除,任一环节失败即为致命项</summary>
    private void CheckDataDirPermission(EnvironmentCheckResult r)
    {
        string dir = string.IsNullOrEmpty(AppPaths.Root) ? App.AppDataDir : AppPaths.Cache;
        string probe = Path.Combine(dir, "perm_probe_" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(probe, "fufu-permission-probe");
            string readBack = File.ReadAllText(probe);
            File.Delete(probe);

            bool ok = readBack == "fufu-permission-probe";
            r.Items.Add(new CheckItem
            {
                Name = "数据目录读写权限",
                Ok = ok,
                Status = ok ? "✓" : "✗",
                Message = ok
                    ? $"数据目录可正常读写:{dir}"
                    : "数据目录写入后读回校验失败,请检查磁盘权限",
                Severity = ok ? SeverityLevel.Info : SeverityLevel.Critical
            });
        }
        catch (Exception ex)
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            r.Items.Add(new CheckItem
            {
                Name = "数据目录读写权限",
                Ok = false,
                Status = "✗",
                Message = $"数据目录无写入权限或磁盘不可写:{ex.Message}(请检查磁盘权限或将程序移到可写目录)",
                Severity = SeverityLevel.Critical
            });
            App.WriteAppLog($"[环境] 数据目录写权限检测失败:{ex.Message}");
        }
    }

    /// <summary>外部依赖模块完整性(C++ 原生组件):缺失不阻止运行,自动降级托管实现,仅告警</summary>
    private void CheckNativeDependency(EnvironmentCheckResult r)
    {
        try
        {
            bool ok = _nativeInterop.IsNativeAvailable();
            r.Items.Add(new CheckItem
            {
                Name = "外部依赖模块(C++ 原生组件)",
                Ok = true, // 有托管降级实现,功能不受影响,不视为失败
                Status = ok ? "✓" : "⚠",
                Message = ok
                    ? "C++ 原生组件(FufuNative)已加载,解压/哈希加速可用"
                    : "C++ 原生组件未加载,将自动降级使用 .NET 托管实现,功能不受影响",
                Severity = SeverityLevel.Info
            });
        }
        catch (Exception ex)
        {
            r.Items.Add(new CheckItem
            {
                Name = "外部依赖模块(C++ 原生组件)",
                Ok = true,
                Status = "⚠",
                Message = $"原生组件检测异常({ex.Message}),已使用 .NET 托管实现兜底",
                Severity = SeverityLevel.Info
            });
        }
    }

    private void CheckJavaRuntimes(EnvironmentCheckResult r)
    {
        try
        {
            var installed = _javaRuntimeService.ListInstalledRuntimes();
            var ready = installed.Where(j => j.Status == "已就绪").ToList();
            r.JavaCount = ready.Count;

            bool ok = ready.Count > 0;
            string detail;
            if (ready.Count == 0)
            {
                detail = "runtimes 目录下未发现可用的 Java 运行时";
            }
            else
            {
                var names = ready.Select(j => j.Name).ToList();
                detail = $"已就绪 {ready.Count} 个:{string.Join(", ", names)}";
            }

            r.Items.Add(new CheckItem
            {
                Name = "Java 运行时",
                Ok = ok,
                Status = ok ? "✓" : "✗",
                Message = detail,
                Severity = ok ? SeverityLevel.Info : SeverityLevel.Warning,
                DownloadUrl = AdoptiumJdkUrl,
                DownloadLabel = "下载 Adoptium JDK (推荐)"
            });
        }
        catch (Exception ex)
        {
            r.Items.Add(new CheckItem
            {
                Name = "Java 运行时",
                Ok = false,
                Status = "✗",
                Message = $"扫描失败:{ex.Message}",
                Severity = SeverityLevel.Warning,
                DownloadUrl = AdoptiumJdkUrl,
                DownloadLabel = "下载 Adoptium JDK"
            });
        }
    }
}
