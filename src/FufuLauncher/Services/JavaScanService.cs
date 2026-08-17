// JavaScanService.cs — 本机 Java 按需扫描服务
// 可爱的芙芙 - 段3 重构
//
// 扫描策略(按需触发,不再开机自动全盘扫描):
// 1. JAVA_HOME 环境变量
// 2. Program Files / Program Files (x86) 下的 Java、JDK 目录
// 3. 注册表 HKLM\SOFTWARE\JavaSoft (64位与32位视图)
// 4. PATH 环境变量中的 java
// 5. 常见位置:%LocalAppData%\Programs\Eclipse Adoptium 等
//
// 完整版本识别:解析 java -version 输出,支持 Java 1~24 全部主版本
// (1.8 旧式格式与 9+ 新式格式均识别)
// 提供 x86/x64、厂商、主版本筛选方法,供 HomePage / SettingsPage 按需调用

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace FufuLauncher.Services;

public class JavaInfo
{
    public string Path { get; set; } = "";          // java.exe 完整路径
    public string JavaHome { get; set; } = "";
    public int MajorVersion { get; set; }           // 8 / 17 / 21 等主版本
    public string FullVersion { get; set; } = "";   // "17.0.9"
    public bool IsJdk { get; set; }                 // JDK 还是 JRE
    public string Vendor { get; set; } = "";        // Oracle / Microsoft / Adoptium / Amazon 等
    public string Architecture { get; set; } = "";  // x64 / x86
}

public class JavaScanService
{
    public List<JavaInfo> FoundJavas { get; } = new();

    /// <summary>最后一次扫描完成时间(null=从未扫描),供 UI 判断是否需要触发新扫描</summary>
    public DateTime? LastScanAt { get; private set; }

    /// <summary>是否已扫描过(快速判断,避免重复扫描)</summary>
    public bool HasScanned => LastScanAt.HasValue;

    /// <summary>按需触发本机 Java 扫描(不自动调用,需 UI/服务显式调用)</summary>
    public async Task ScanAsync()
    {
        FoundJavas.Clear();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. JAVA_HOME
        TryAddJavaFromHome(Environment.GetEnvironmentVariable("JAVA_HOME"), found);

        // 2. Program Files / LocalAppData
        var programDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        foreach (var dir in programDirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            await Task.Run(() => ScanDirectoryForJava(dir, found, depth: 0, maxDepth: 2));
        }

        // 3. 注册表
        ScanRegistry(found);

        // 4. PATH
        TryAddJavaFromPath(found);

        LastScanAt = DateTime.Now;
    }

    private void TryAddJavaFromHome(string? javaHome, HashSet<string> found)
    {
        if (string.IsNullOrEmpty(javaHome)) return;
        string exe = Path.Combine(javaHome, "bin", "java.exe");
        TryAddJava(exe, found);
    }

    private void ScanDirectoryForJava(string dir, HashSet<string> found, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        try
        {
            // 检查 bin\java.exe 是否直接存在
            string exe = Path.Combine(dir, "bin", "java.exe");
            if (File.Exists(exe))
            {
                TryAddJava(exe, found);
                return;
            }

            // 遍历子目录(只进入名字含 Java/JDK/JRE/Adoptium/Microsoft/Zulu/Corretto/Oracle/Temurin 的)
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.Contains("Java", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("JDK", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("JRE", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Adoptium", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Temurin", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Zulu", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Corretto", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Oracle", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("GraalVM", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase))
                {
                    ScanDirectoryForJava(sub, found, depth + 1, maxDepth);
                }
            }
        }
        catch
        {
            // 权限或路径问题,忽略
        }
    }

    private void ScanRegistry(HashSet<string> found)
    {
        var keys = new[]
        {
            @"SOFTWARE\JavaSoft\Java Runtime Environment",
            @"SOFTWARE\JavaSoft\Java Development Kit",
            @"SOFTWARE\JavaSoft\JRE",
            @"SOFTWARE\JavaSoft\JDK",
            @"SOFTWARE\Eclipse Adoptium\JDK",
            @"SOFTWARE\Microsoft\JDK"
        };

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            foreach (var keyPath in keys)
            {
                try
                {
                    using var key = baseKey.OpenSubKey(keyPath);
                    if (key == null) continue;
                    foreach (var subName in key.GetSubKeyNames())
                    {
                        using var subKey = key.OpenSubKey(subName);
                        if (subKey?.GetValue("JavaHome") is string home)
                        {
                            TryAddJava(Path.Combine(home, "bin", "java.exe"), found);
                        }
                    }
                }
                catch { /* 忽略 */ }
            }
        }
    }

    private void TryAddJavaFromPath(HashSet<string> found)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string exe = Path.Combine(dir.Trim('"'), "java.exe");
            TryAddJava(exe, found);
        }
    }

    private void TryAddJava(string exePath, HashSet<string> found)
    {
        try
        {
            string fullPath = Path.GetFullPath(exePath);
            if (!File.Exists(fullPath)) return;
            if (!found.Add(fullPath)) return;

            // 调用 java -version 解析版本
            var info = QueryJavaVersion(fullPath);
            if (info != null)
            {
                FoundJavas.Add(info);
            }
        }
        catch
        {
            // 忽略单个 Java 检测失败
        }
    }

    private JavaInfo? QueryJavaVersion(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string output = p.StandardError.ReadToEnd();
            if (string.IsNullOrEmpty(output))
                output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            return ParseJavaVersionOutput(exePath, output);
        }
        catch
        {
            return null;
        }
    }

    private JavaInfo ParseJavaVersionOutput(string exePath, string output)
    {
        var info = new JavaInfo { Path = exePath };
        info.JavaHome = Path.GetDirectoryName(Path.GetDirectoryName(exePath)) ?? "";

        // 输出示例:
        //   openjdk version "17.0.9" 2023-10-17
        //   OpenJDK Runtime Environment Temurin-17.0.9+9 (build 17.0.9+9)
        //   OpenJDK 64-Bit Server VM Temurin-17.0.9+9 (build 17.0.9+9, mixed mode, sharing)
        //   java version "1.8.0_361"  (Java 8 旧式格式)
        var versionMatch = Regex.Match(output, @"version\s+""(\d+)\.?(\d*)\.?(\d*).*?""");
        if (versionMatch.Success)
        {
            int major = int.Parse(versionMatch.Groups[1].Value);
            // Java 8 之前格式:1.8.0_xxx
            if (major == 1 && versionMatch.Groups[2].Success)
            {
                major = int.Parse(versionMatch.Groups[2].Value);
            }
            info.MajorVersion = major;
            info.FullVersion = $"{versionMatch.Groups[1].Value}" +
                                (versionMatch.Groups[2].Success ? $".{versionMatch.Groups[2].Value}" : "") +
                                (versionMatch.Groups[3].Success ? $".{versionMatch.Groups[3].Value}" : "");
        }

        info.IsJdk = output.Contains("Development Kit", StringComparison.OrdinalIgnoreCase) ||
                       output.Contains("JDK", StringComparison.OrdinalIgnoreCase);
        info.Architecture = output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase) ? "x64" : "x86";

        // 解析厂商
        if (output.Contains("Temurin", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Adoptium", StringComparison.OrdinalIgnoreCase))
            info.Vendor = "Eclipse Adoptium";
        else if (output.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            info.Vendor = "Microsoft";
        else if (output.Contains("Zulu", StringComparison.OrdinalIgnoreCase))
            info.Vendor = "Azul Zulu";
        else if (output.Contains("Corretto", StringComparison.OrdinalIgnoreCase))
            info.Vendor = "Amazon Corretto";
        else if (output.Contains("Oracle", StringComparison.OrdinalIgnoreCase))
            info.Vendor = "Oracle";
        else if (output.Contains("GraalVM", StringComparison.OrdinalIgnoreCase))
            info.Vendor = "GraalVM";
        else
            info.Vendor = "Unknown";

        return info;
    }

    /// <summary>根据主版本号筛选 Java(例如筛选所有 Java 17)</summary>
    public List<JavaInfo> FilterByMajorVersion(int major) =>
        FoundJavas.FindAll(j => j.MajorVersion == major);

    /// <summary>根据架构筛选(x64 / x86)</summary>
    public List<JavaInfo> FilterByArchitecture(string arch) =>
        FoundJavas.FindAll(j => string.Equals(j.Architecture, arch, StringComparison.OrdinalIgnoreCase));

    /// <summary>根据厂商筛选</summary>
    public List<JavaInfo> FilterByVendor(string vendor) =>
        FoundJavas.FindAll(j => string.Equals(j.Vendor, vendor, StringComparison.OrdinalIgnoreCase));

    /// <summary>获取最优 Java(优先匹配指定大版本 + 架构)</summary>
    public JavaInfo? GetBestJava(int requiredMajor) => GetBestJava(requiredMajor, "x64");

    /// <summary>获取最优 Java(优先匹配指定大版本 + 架构,无匹配则忽略架构再找一次)</summary>
    public JavaInfo? GetBestJava(int requiredMajor, string preferArch)
    {
        var match = FoundJavas.Find(j => j.MajorVersion == requiredMajor &&
                                            string.Equals(j.Architecture, preferArch, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;
        match = FoundJavas.Find(j => j.MajorVersion == requiredMajor);
        return match;
    }
}
