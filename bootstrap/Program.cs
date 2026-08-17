// 可爱的芙芙 - 启动器打包引导程序
// 版权:Copyright © 可爱的芙芙
// 功能:便携安装 Go 工具链到 .tools\go,安装 goversioninfo,
//       生成 resource.syso,编译 Go 启动器到"开始\FufuLauncher.exe"。
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace FufuBootstrap;

internal static class Program
{
    private const string GoVersion = "go1.26.5";
    private const string GoversioninfoPkg = "github.com/josephspurrier/goversioninfo/cmd/goversioninfo";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            // 工具模式:bootstrap --kill <exeName>
            // 用于结束占用 exe 的进程(PowerShell 执行策略被禁,只能借 dotnet 执行)
            if (args.Length >= 2 && string.Equals(args[0], "--kill", StringComparison.OrdinalIgnoreCase))
            {
                return KillProcesses(args[1]);
            }

            return await RunAsync();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[FAIL] {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
            Console.Error.WriteLine(ex.StackTrace);
            return 99;
        }
    }

    /// <summary>结束指定名称的所有进程(忽略找不到的情况)</summary>
    private static int KillProcesses(string exeName)
    {
        var killed = 0;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)))
        {
            try
            {
                Console.WriteLine($"  终止 PID={p.Id} {p.ProcessName}");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
                killed++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [跳过] PID={p.Id}: {ex.Message}");
            }
        }
        Console.WriteLine($"已终止 {killed} 个进程。");
        return 0;
    }

    private static async Task<int> RunAsync()
    {
        var projectRoot = FindProjectRoot(AppContext.BaseDirectory);
        Log($"项目根目录: {projectRoot}");

        var toolsDir = Path.Combine(projectRoot, ".tools");
        var portableGoRoot = Path.Combine(toolsDir, "go");
        var goPath = Path.Combine(toolsDir, "gopath");
        var goCache = Path.Combine(toolsDir, "gocache");
        var packagerDir = Path.Combine(projectRoot, "packager");
        var outputDir = Path.Combine(projectRoot, "开始");
        var outputExe = Path.Combine(outputDir, "FufuLauncher.exe");
        var versioninfoJson = Path.Combine(packagerDir, "versioninfo.json");

        if (!Directory.Exists(packagerDir))
            throw new DirectoryNotFoundException($"未找到 packager 目录: {packagerDir}");
        if (!File.Exists(versioninfoJson))
            throw new FileNotFoundException($"未找到 versioninfo.json: {versioninfoJson}");
        if (!File.Exists(Path.Combine(packagerDir, "main.go")))
            throw new FileNotFoundException("未找到 packager/main.go");
        if (!File.Exists(Path.Combine(packagerDir, "go.mod")))
            throw new FileNotFoundException("未找到 packager/go.mod");

        Directory.CreateDirectory(toolsDir);
        Directory.CreateDirectory(goPath);
        Directory.CreateDirectory(goCache);
        Directory.CreateDirectory(outputDir);

        // === Step 0: 网络诊断(打印代理配置,便于排查) ===
        PrintNetworkDiagnostics();

        // === Step 1: 检测可用 Go (优先级: 便携 .tools\go > 系统已装 Go) ===
        var (goExe, goRoot, useSystemGo) = await ResolveGoAsync(portableGoRoot, toolsDir);

        var env = new Dictionary<string, string?>
        {
            ["GOROOT"] = goRoot,
            ["GOPATH"] = goPath,
            ["GOCACHE"] = goCache,
            ["GO111MODULE"] = "on",
            ["GOPROXY"] = "https://goproxy.cn,direct",
            ["GOSUMDB"] = "sum.golang.org",
            // GOENV 指向项目本地文件,避免 go env -w 写入用户配置目录
            ["GOENV"] = Path.Combine(toolsDir, "go.env"),
        };

        // === Step 2: 验证 Go ===
        Log("验证 Go 工具链...");
        Run(goExe, new[] { "version" }, projectRoot, env);

        // === Step 3: 安装 goversioninfo ===
        var goversioninfoExe = Path.Combine(goPath, "bin", "goversioninfo.exe");
        if (!File.Exists(goversioninfoExe))
        {
            Log("安装 goversioninfo (通过 goproxy.cn)...");
            Run(goExe, new[] { "install", GoversioninfoPkg + "@latest" }, packagerDir, env);
        }
        else
        {
            Log($"已检测到 goversioninfo: {goversioninfoExe}");
        }
        if (!File.Exists(goversioninfoExe))
            throw new FileNotFoundException("goversioninfo 安装后未在 GOPATH/bin 找到", goversioninfoExe);

        // === Step 4: 生成 resource.syso (从 packager 目录运行,自动读取 versioninfo.json) ===
        var sysoPath = Path.Combine(packagerDir, "resource.syso");
        Log("生成 resource.syso (嵌入版权/版本信息)...");
        // goversioninfo 默认读取 cwd 的 versioninfo.json,输出 resource.syso
        Run(goversioninfoExe, Array.Empty<string>(), packagerDir, env);
        if (!File.Exists(sysoPath))
            throw new FileNotFoundException("goversioninfo 未生成 resource.syso", sysoPath);
        var sysoSize = new FileInfo(sysoPath).Length;
        Log($"  resource.syso 已生成 ({sysoSize} 字节)");

        // === Step 5: go build ===
        Log("编译 Go 启动器 (-H windowsgui -s -w)...");
        var relOut = Path.GetRelativePath(packagerDir, outputExe);
        // 清理可能存在的旧产物
        if (File.Exists(outputExe))
        {
            File.Delete(outputExe);
            Log("  已清理旧的 FufuLauncher.exe");
        }
        Run(goExe, new[] { "build", "-ldflags", "-H windowsgui -s -w", "-o", relOut, "." }, packagerDir, env);

        if (!File.Exists(outputExe))
            throw new FileNotFoundException("go build 未输出目标 exe", outputExe);

        var info = new FileInfo(outputExe);

        // === Step 6: 验证嵌入的版本/版权信息 ===
        var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(outputExe);
        var verifyOk =
            fvi.LegalCopyright == "Copyright © 可爱的芙芙" &&
            fvi.CompanyName == "可爱的芙芙" &&
            fvi.ProductName == "可爱的芙芙 Minecraft启动器" &&
            fvi.FileDescription == "可爱的芙芙 MC-Java版启动器" &&
            fvi.OriginalFilename == "FufuLauncher.exe";

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine($"  编译成功!");
        Console.WriteLine($"  输出: {outputExe}");
        Console.WriteLine($"  大小: {info.Length / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine("  文件属性验证 (Windows 资源信息):");
        Console.WriteLine($"    CompanyName      = {fvi.CompanyName}");
        Console.WriteLine($"    FileDescription  = {fvi.FileDescription}");
        Console.WriteLine($"    ProductName      = {fvi.ProductName}");
        Console.WriteLine($"    LegalCopyright   = {fvi.LegalCopyright}");
        Console.WriteLine($"    OriginalFilename = {fvi.OriginalFilename}");
        Console.WriteLine($"    FileVersion      = {fvi.FileVersion}");
        Console.WriteLine($"    ProductVersion   = {fvi.ProductVersion}");
        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine($"  版权验证: {(verifyOk ? "通过 ✓" : "失败 ✗")}");
        Console.WriteLine("============================================================");
        Console.ResetColor();

        if (!verifyOk)
            throw new InvalidOperationException("文件属性中的版权信息与预期不符,请检查 versioninfo.json 与 resource.syso。");
        return 0;
    }

    private static void PrintNetworkDiagnostics()
    {
        Log("网络诊断:");
        var proxyVars = new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy", "NO_PROXY", "no_proxy", "ALL_PROXY", "all_proxy" };
        foreach (var v in proxyVars)
        {
            var val = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrEmpty(val))
                Log($"  {v} = {val}");
        }
        var sysProxy = HttpClient.DefaultProxy;
        Log($"  系统默认代理: {(sysProxy == null ? "<null>" : sysProxy.ToString())}");
    }

    /// <summary>
    /// 解析可用的 Go:优先用便携 .tools\go,其次用系统已装的 Go,都没有则下载。
    /// 返回 (goExe, goRoot, useSystemGo)。
    /// </summary>
    private static async Task<(string goExe, string goRoot, bool useSystemGo)> ResolveGoAsync(string portableGoRoot, string toolsDir)
    {
        // 1) 便携 Go
        var portableGoExe = Path.Combine(portableGoRoot, "bin", "go.exe");
        if (File.Exists(portableGoExe))
        {
            Log($"已检测到便携 Go: {portableGoExe}");
            return (portableGoExe, portableGoRoot, false);
        }

        // 2) 系统已装的 Go (常见安装路径)
        var systemCandidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Go", "bin", "go.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Go", "bin", "go.exe"),
            @"C:\Go\bin\go.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Go", "bin", "go.exe"),
        };
        // 也检查 PATH 中的 go (通过 where 等价物)
        var pathGo = FindInPath("go.exe");
        if (!string.IsNullOrEmpty(pathGo))
            systemCandidates.Insert(0, pathGo);

        foreach (var cand in systemCandidates.Where(File.Exists).Distinct())
        {
            Log($"已检测到系统 Go: {cand}");
            var root = Path.GetFullPath(Path.Combine(cand, "..", ".."));
            return (cand, root, true);
        }

        // 3) 都没有,下载(国内镜像优先,官方源兜底)
        Log($"未检测到 Go,开始下载 {GoVersion} (约 150MB)...");
        var goExe = portableGoExe;
        var goRoot = portableGoRoot;

        var zipPath = Path.Combine(toolsDir, $"{GoVersion}.windows-amd64.zip");
        if (!File.Exists(zipPath))
        {
            var urls = new[]
            {
                // 国内镜像优先 (直连,不重定向到 dl.google.com)
                $"https://mirrors.aliyun.com/golang/{GoVersion}.windows-amd64.zip",
                $"https://mirrors.huaweicloud.com/golang/{GoVersion}.windows-amd64.zip",
                $"https://mirrors.ustc.edu.cn/golang/{GoVersion}.windows-amd64.zip",
                $"https://mirrors.tuna.tsinghua.edu.cn/golang/{GoVersion}.windows-amd64.zip",
                // 官方源(国内通常不可达,作为兜底)
                $"https://go.dev/dl/{GoVersion}.windows-amd64.zip",
                $"https://golang.google.cn/dl/{GoVersion}.windows-amd64.zip",
            };
            await DownloadWithFallbackAsync(urls, zipPath);
        }

        Log($"解压到 {goRoot} ...");
        if (Directory.Exists(goRoot))
            Directory.Delete(goRoot, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, toolsDir, overwriteFiles: true);
        if (!File.Exists(goExe))
            throw new InvalidOperationException($"解压后未在预期位置找到 go.exe: {goExe}");
        Log("Go 安装完成。");
        return (goExe, goRoot, false);
    }

    private static string? FindInPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(full)) return Path.GetFullPath(full);
            }
            catch { /* ignore invalid PATH entries */ }
        }
        return null;
    }

    private static async Task DownloadWithFallbackAsync(IEnumerable<string> urls, string destPath)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                await DownloadAsync(url, destPath);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Log($"  [尝试失败] {url}");
                Log($"    原因: {ex.GetType().Name}: {ex.Message}");
            }
        }
        throw new InvalidOperationException("所有下载源均失败。请检查网络,或手动下载 Go zip 放到 .tools 目录后重试。", last);
    }

    private static async Task DownloadAsync(string url, string destPath)
    {
        Log($"  正在下载: {url}");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        // 模拟浏览器 UA,避免被某些 CDN 拒绝
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) FufuBootstrap/1.0");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        var tmpPath = destPath + ".tmp";
        try
        {
            await using (var fs = File.Create(tmpPath))
            await using (var stream = await resp.Content.ReadAsStreamAsync())
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                var lastReport = DateTime.MinValue;
                while ((n = await stream.ReadAsync(buffer)) != 0)
                {
                    await fs.WriteAsync(buffer, 0, n);
                    read += n;
                    // 限频输出进度,避免刷屏
                    var now = DateTime.UtcNow;
                    if (now - lastReport > TimeSpan.FromMilliseconds(500))
                    {
                        if (total > 0)
                            Console.Write($"\r    已下载 {read / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0:F1} MB");
                        else
                            Console.Write($"\r    已下载 {read / 1024.0 / 1024.0:F1} MB");
                        lastReport = now;
                    }
                }
            }
            Console.WriteLine();
            File.Move(tmpPath, destPath, overwrite: true);
            Log($"  下载完成: {Path.GetFileName(destPath)}");
        }
        catch
        {
            if (File.Exists(tmpPath))
                try { File.Delete(tmpPath); } catch { /* ignore */ }
            throw;
        }
    }

    private static void Run(string exe, IReadOnlyList<string> args, string workDir, IReadOnlyDictionary<string, string?> env)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        foreach (var kv in env)
            psi.EnvironmentVariables[kv.Key] = kv.Value ?? string.Empty;

        var argDisplay = string.Join(' ', args.Select(QuoteForDisplay));
        Log($"> {Path.GetFileName(exe)} {argDisplay}");

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动进程: {exe}");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(Indent(stdout));
            Console.ResetColor();
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.ForegroundColor = p.ExitCode == 0 ? ConsoleColor.DarkGray : ConsoleColor.Yellow;
            Console.Error.Write(Indent(stderr));
            Console.ResetColor();
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"命令退出码非零 ({p.ExitCode}): {Path.GetFileName(exe)} {argDisplay}");
    }

    private static string QuoteForDisplay(string arg)
        => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static string Indent(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var lines = s.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select(l => l.Length == 0 ? "    " : "    " + l));
    }

    private static string FindProjectRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FufuLauncher.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("未找到项目根目录(缺少 FufuLauncher.sln)");
    }

    private static void Log(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[bootstrap] {msg}");
        Console.ResetColor();
    }
}
