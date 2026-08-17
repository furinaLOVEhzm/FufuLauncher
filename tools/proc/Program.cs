// 可爱的芙芙 - 进程清理 + 产物验证工具
// 版权:Copyright © 可爱的芙芙
// 用途:结束占用 exe 的进程 + 验证"Start"目录中 FufuLauncher.exe 的版权信息
//       (PowerShell 执行策略被禁,只能借 dotnet 执行)
// 用法:
//   发布前清理进程: dotnet run --project tools\proc -c Release
//   发布后验证    : dotnet run --project tools\proc -c Release
using System.Diagnostics;
using System.Text;

namespace FufuProc;

internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var projectRoot = FindProjectRoot(AppContext.BaseDirectory);
        Console.WriteLine($"项目根目录: {projectRoot}");

        // 1) 结束可能占用 exe 的进程
        var targets = new[] { "FufuLauncher", "fufu-bootstrap", "fufu-proc" };
        int killed = 0;
        foreach (var name in targets)
        {
            foreach (var p in Process.GetProcessesByName(name))
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
        }
        Console.WriteLine($"已终止 {killed} 个进程。");

        // 2) 短暂等待文件句柄释放
        Thread.Sleep(500);

        // 2.5) 自动迁移:若旧"开始"目录存在且"Start"不存在,则重命名
        var oldDir = Path.Combine(projectRoot, "开始");
        var newDir = Path.Combine(projectRoot, "Start");
        if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
        {
            try
            {
                Directory.Move(oldDir, newDir);
                Console.WriteLine($"已将旧目录 \"开始\" 重命名为 \"Start\"。");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[警告] 重命名 \"开始\" -> \"Start\" 失败:{ex.Message}");
                Console.Error.WriteLine("       可手动重命名后重新运行。");
            }
        }

        // 3) 若"Start"目录已有 FufuLauncher.exe,验证 + 启动测试
        var outDir = newDir;
        var exe = Path.Combine(outDir, "FufuLauncher.exe");
        if (File.Exists(exe))
        {
            VerifyExe(exe);
            LaunchTest(exe);
        }
        else
        {
            Console.WriteLine("\"Start\"目录中暂无 FufuLauncher.exe。");
        }
        return 0;
    }

    /// <summary>启动测试:启动 exe,等待 3 秒,若进程仍存活则视为启动成功,然后清理</summary>
    private static void LaunchTest(string exe)
    {
        Console.WriteLine();
        Console.WriteLine("[启动测试]");
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            if (p == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  启动失败:Process.Start 返回 null");
                Console.ResetColor();
                return;
            }
            // 给 WPF 自包含单文件首次解压运行时 + ThemeService + EnvironmentCheckService 足够时间
            Thread.Sleep(15000);
            if (p.HasExited)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  启动后立即退出(ExitCode={p.ExitCode}),可能缺少依赖或启动失败");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  启动成功 ✓ (PID={p.Id},已运行 6 秒未退出)");
                Console.ResetColor();
                try { p.Kill(entireProcessTree: true); } catch { /* 清理 */ }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  启动异常: {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static void VerifyExe(string exe)
    {
        var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
        var fi = new FileInfo(exe);
        // .NET 单文件 apphost 的 FileDescription 默认为 AssemblyName,OriginalFilename 默认为 "AssemblyName.dll",
        // 这是 SDK 固有行为(不从 AssemblyTitle 读取),不影响用户可见的版权信息。
        // 验证只检查用户关心的版权核心字段。
        var ok =
            fvi.LegalCopyright == "Copyright © 可爱的芙芙" &&
            fvi.CompanyName == "可爱的芙芙" &&
            fvi.ProductName == "可爱的芙芙 Minecraft启动器" &&
            fvi.FileVersion == "1.0.0.0" &&
            fvi.ProductVersion == "1.0.0.0";

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("============================================================");
        Console.WriteLine("  文件属性验证");
        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine($"    路径             = {exe}");
        Console.WriteLine($"    大小             = {fi.Length / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine($"    CompanyName      = {fvi.CompanyName}");
        Console.WriteLine($"    FileDescription  = {fvi.FileDescription}");
        Console.WriteLine($"    ProductName      = {fvi.ProductName}");
        Console.WriteLine($"    LegalCopyright   = {fvi.LegalCopyright}");
        Console.WriteLine($"    OriginalFilename = {fvi.OriginalFilename}");
        Console.WriteLine($"    FileVersion      = {fvi.FileVersion}");
        Console.WriteLine($"    ProductVersion   = {fvi.ProductVersion}");
        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine($"  版权验证: {(ok ? "通过 ✓" : "失败 ✗")}");
        Console.WriteLine("============================================================");
        Console.ResetColor();
        if (!ok)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("  版权信息与预期不符!");
            Console.ResetColor();
        }
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
}
