// UpdateService.cs — 程序自身更新检测与应用框架
// 可爱的芙芙 - 更新系统
//
// 功能:
// 1. 读取更新清单 JSON(Version/FullUrl/FullSha1/PatchUrl/PatchSha1/Notes)对比本地版本
// 2. 增量(patch)更新优先,失败自动回退完整包更新
// 3. 下载进度接入全局下载进度系统(DownloadService)
// 4. 更新前备份旧程序文件到 APP\MCGAME\Updater\backup-{时间戳}\,更新失败自动回退
// 5. 只替换程序本体(exe/dll/json/pdb),绝不触碰 APP\MCGAME 用户数据目录
// 6. 更新事件全部写入应用日志
//
// 清单格式示例:
// {
//   "Version": "1.2.0.0",
//   "FullUrl": "https://example.com/fufu-1.2.0.0.zip",
//   "FullSha1": "....",
//   "FullSize": 0,
//   "PatchUrl": "https://example.com/fufu-patch-1.1.5-to-1.2.0.zip",
//   "PatchSha1": "....",
//   "PatchSize": 0,
//   "Notes": "更新说明..."
// }

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

public class UpdateManifest
{
    public string Version { get; set; } = "";
    public string FullUrl { get; set; } = "";
    public string FullSha1 { get; set; } = "";
    public long FullSize { get; set; }
    public string? PatchUrl { get; set; }
    public string? PatchSha1 { get; set; }
    public long PatchSize { get; set; }
    public string Notes { get; set; } = "";
}

public class UpdateCheckResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public bool HasUpdate { get; set; }
    public string LocalVersion { get; set; } = "";
    public UpdateManifest? Manifest { get; set; }
}

public class UpdateService
{
    private readonly ConfigService _configService;
    private readonly DownloadService _downloadService;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>程序文件扩展名(更新只替换这些,不动 APP\MCGAME 用户数据)</summary>
    private static readonly string[] ProgramFileExts = { ".exe", ".dll", ".json", ".pdb" };

    /// <summary>更新工作目录:APP\mcGAME\cache\updater(更新临时文件属缓存,不新增规范外根级目录)</summary>
    public static string UpdaterDir => Path.Combine(AppPaths.Cache, "updater");
    /// <summary>备份根目录</summary>
    public static string BackupRootDir => Path.Combine(UpdaterDir, "backups");

    public UpdateService(ConfigService configService, DownloadService downloadService)
    {
        _configService = configService;
        _downloadService = downloadService;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/1.0 (Windows)");
    }

    /// <summary>本地程序版本号</summary>
    public static string GetLocalVersion()
    {
        try
        {
            var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return ver?.ToString() ?? "0.0.0.0";
        }
        catch { return "0.0.0.0"; }
    }

    /// <summary>检测新版本:拉取清单对比本地版本。网络失败返回 Ok=false 带原因,不抛异常</summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        string local = GetLocalVersion();
        var result = new UpdateCheckResult { LocalVersion = local };

        string url = _configService.Config.UpdateManifestUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            result.Error = "尚未配置更新清单地址(Config/UpdateManifestUrl),跳过更新检测。";
            App.WriteAppLog($"[更新] {result.Error}");
            return result;
        }

        try
        {
            App.WriteAppLog($"[更新] 拉取更新清单:{url}");
            string json = await _http.GetStringAsync(url);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest == null || string.IsNullOrEmpty(manifest.Version))
            {
                result.Error = "更新清单格式无效(缺少 Version 字段)。";
                App.WriteAppLog($"[更新] {result.Error}");
                return result;
            }

            result.Ok = true;
            result.Manifest = manifest;
            result.HasUpdate = CompareVersion(manifest.Version, local) > 0;
            App.WriteAppLog($"[更新] 本地={local} 最新={manifest.Version} 有更新={result.HasUpdate}");
            return result;
        }
        catch (Exception ex)
        {
            result.Error = $"检测更新失败:{ex.Message}";
            App.WriteAppLog($"[更新] {result.Error}");
            return result;
        }
    }

    private static int CompareVersion(string a, string b)
    {
        Version.TryParse(a, out var va);
        Version.TryParse(b, out var vb);
        va ??= new Version(0, 0);
        vb ??= new Version(0, 0);
        return va.CompareTo(vb);
    }

    /// <summary>
    /// 应用更新:增量包优先 → 失败回退完整包 → 哈希校验 → 备份旧程序文件 → 写出替换脚本并启动。
    /// 成功后返回 true,调用方应退出主程序让脚本完成替换。
    /// </summary>
    public async Task<(bool Ok, string Message)> ApplyUpdateAsync(UpdateManifest manifest)
    {
        try
        {
            string appDir = AppContext.BaseDirectory.TrimEnd('\\');
            string stagingRoot = Path.Combine(UpdaterDir, "staging");
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
            Directory.CreateDirectory(stagingRoot);

            // ---- 1. 增量包优先,失败回退完整包 ----
            string? packPath = null;
            if (!string.IsNullOrEmpty(manifest.PatchUrl))
            {
                App.WriteAppLog($"[更新] 尝试增量包:{manifest.PatchUrl}");
                packPath = await DownloadPackAsync(manifest.PatchUrl, manifest.PatchSha1, manifest.PatchSize,
                    Path.Combine(stagingRoot, "patch.zip"));
                if (packPath == null)
                    App.WriteAppLog("[更新] 增量包下载/校验失败,回退到完整包更新");
            }
            if (packPath == null)
            {
                if (string.IsNullOrEmpty(manifest.FullUrl))
                    return (false, "清单中缺少完整包下载地址(FullUrl)。");
                App.WriteAppLog($"[更新] 下载完整包:{manifest.FullUrl}");
                packPath = await DownloadPackAsync(manifest.FullUrl, manifest.FullSha1, manifest.FullSize,
                    Path.Combine(stagingRoot, "full.zip"));
                if (packPath == null)
                    return (false, "更新包下载失败或哈希校验未通过(已重试),请稍后重试或检查网络。");
            }

            // ---- 2. 解压到 staging\new(单一根目录自动上提) ----
            string newDir = Path.Combine(stagingRoot, "new");
            Directory.CreateDirectory(newDir);
            ZipFile.ExtractToDirectory(packPath, newDir, overwriteFiles: true);
            FlattenSingleRoot(newDir);
            if (!Directory.EnumerateFiles(newDir).Any())
                return (false, "更新包解压后为空,已中止更新。");

            // ---- 3. 备份旧程序文件(只备份程序本体,不含 APP\MCGAME) ----
            string backupDir = Path.Combine(BackupRootDir, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}");
            BackupProgramFiles(appDir, backupDir);
            App.WriteAppLog($"[更新] 旧程序文件已备份:{backupDir}");

            // ---- 4. 写替换脚本(等待进程退出 → 替换 → 失败回退 → 重启) ----
            string exeName = Path.GetFileName(Environment.ProcessPath ?? "FufuLauncher.exe");
            string batPath = Path.Combine(UpdaterDir, "apply-update.bat");
            WriteApplyScript(batPath, appDir, newDir, backupDir, exeName);

            Process.Start(new ProcessStartInfo
            {
                FileName = batPath,
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = UpdaterDir
            })?.Dispose();

            App.WriteAppLog($"[更新] 替换脚本已启动:{batPath}");
            return (true, "更新文件已就绪,启动器即将退出并完成替换。");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[更新] 应用更新异常:{ex}");
            return (false, $"应用更新失败:{ex.Message}");
        }
    }

    /// <summary>下载更新包并做 SHA1 校验(接入全局下载进度;失败返回 null)</summary>
    private async Task<string?> DownloadPackAsync(string url, string? sha1, long size, string savePath)
    {
        try
        {
            var task = new DownloadTaskItem
            {
                Url = url,
                LocalPath = savePath,
                Sha1 = sha1,
                Size = size,
                Category = DownloadCategory.Other
            };
            bool ok = await _downloadService.DownloadAllAsync(new List<DownloadTaskItem> { task });
            if (!ok || !File.Exists(savePath)) return null;
            // DownloadAllAsync 内部已做 SHA1 校验,这里再确认一次
            if (!string.IsNullOrEmpty(sha1) && !_downloadService.VerifySha1(savePath, sha1))
            {
                App.WriteAppLog($"[更新] 更新包哈希校验失败:{url}");
                return null;
            }
            return savePath;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[更新] 更新包下载异常:{ex.Message}");
            return null;
        }
    }

    /// <summary>备份程序本体文件到备份目录(跳过 APP 用户数据目录与 Updater 工作目录)</summary>
    private static void BackupProgramFiles(string appDir, string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        foreach (var file in Directory.EnumerateFiles(appDir))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (!ProgramFileExts.Contains(ext)) continue;
            try { File.Copy(file, Path.Combine(backupDir, Path.GetFileName(file)), overwrite: true); }
            catch { /* 单个文件复制失败不阻断 */ }
        }
    }

    /// <summary>列出可用的历史备份(按时间倒序)</summary>
    public static List<string> ListBackups()
    {
        try
        {
            if (!Directory.Exists(BackupRootDir)) return new();
            return Directory.EnumerateDirectories(BackupRootDir)
                .OrderByDescending(d => d).ToList();
        }
        catch { return new(); }
    }

    /// <summary>手动回退到指定备份:写回退脚本并启动(成功返回 true,调用方应退出程序)</summary>
    public (bool Ok, string Message) RollbackToBackup(string backupDir)
    {
        try
        {
            if (!Directory.Exists(backupDir)) return (false, "备份目录不存在。");
            string appDir = AppContext.BaseDirectory.TrimEnd('\\');
            string exeName = Path.GetFileName(Environment.ProcessPath ?? "FufuLauncher.exe");
            string batPath = Path.Combine(UpdaterDir, "rollback.bat");

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine($"set \"APPDIR={appDir}\"");
            sb.AppendLine($"set \"BACKUP={backupDir}\"");
            sb.AppendLine($"set \"EXE={exeName}\"");
            sb.AppendLine("timeout /t 2 /nobreak >nul");
            sb.AppendLine(":wait");
            sb.AppendLine("tasklist /FI \"IMAGENAME eq %EXE%\" 2>nul | find /I \"%EXE%\" >nul");
            sb.AppendLine("if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)");
            sb.AppendLine("del /q \"%APPDIR%\\*.dll\" \"%APPDIR%\\*.pdb\" \"%APPDIR%\\*.json\" \"%APPDIR%\\*.exe\" 2>nul");
            sb.AppendLine("xcopy /E /Y \"%BACKUP%\" \"%APPDIR%\" >nul");
            sb.AppendLine("start \"\" \"%APPDIR%\\%EXE%\"");
            sb.AppendLine("del \"%~f0\"");
            File.WriteAllText(batPath, sb.ToString(), Encoding.Default);

            Process.Start(new ProcessStartInfo
            {
                FileName = batPath,
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = UpdaterDir
            })?.Dispose();
            App.WriteAppLog($"[更新] 回退脚本已启动,目标备份:{backupDir}");
            return (true, "正在回退到旧版本,启动器即将退出。");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[更新] 回退异常:{ex.Message}");
            return (false, $"回退失败:{ex.Message}");
        }
    }

    /// <summary>写替换脚本:等待主程序退出 → 删除旧程序文件 → 复制新文件 → 失败回退 → 重启。
    /// 注意:只操作程序本体文件(*.exe/dll/json/pdb),绝不删除 APP\MCGAME 用户数据。</summary>
    private static void WriteApplyScript(string batPath, string appDir, string newDir, string backupDir, string exeName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($"set \"APPDIR={appDir}\"");
        sb.AppendLine($"set \"STAGING={newDir}\"");
        sb.AppendLine($"set \"BACKUP={backupDir}\"");
        sb.AppendLine($"set \"EXE={exeName}\"");
        sb.AppendLine("timeout /t 2 /nobreak >nul");
        // 等待主程序进程退出
        sb.AppendLine(":wait");
        sb.AppendLine("tasklist /FI \"IMAGENAME eq %EXE%\" 2>nul | find /I \"%EXE%\" >nul");
        sb.AppendLine("if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)");
        // 替换:只删除程序本体文件(不碰 APP 子目录)
        sb.AppendLine("del /q \"%APPDIR%\\*.dll\" \"%APPDIR%\\*.pdb\" \"%APPDIR%\\*.json\" \"%APPDIR%\\*.exe\" 2>nul");
        sb.AppendLine("xcopy /E /Y \"%STAGING%\" \"%APPDIR%\" >nul");
        sb.AppendLine("if errorlevel 1 goto rollback");
        sb.AppendLine("start \"\" \"%APPDIR%\\%EXE%\"");
        sb.AppendLine("del \"%~f0\"");
        sb.AppendLine("exit /b");
        // 失败回退:恢复备份
        sb.AppendLine(":rollback");
        sb.AppendLine("del /q \"%APPDIR%\\*.dll\" \"%APPDIR%\\*.pdb\" \"%APPDIR%\\*.json\" \"%APPDIR%\\*.exe\" 2>nul");
        sb.AppendLine("xcopy /E /Y \"%BACKUP%\" \"%APPDIR%\" >nul");
        sb.AppendLine("start \"\" \"%APPDIR%\\%EXE%\"");
        sb.AppendLine("exit /b");
        File.WriteAllText(batPath, sb.ToString(), Encoding.Default);
    }

    /// <summary>解压后若只有单一根目录(如 FufuLauncher-x64/),将其内容上提一层</summary>
    private static void FlattenSingleRoot(string dir)
    {
        try
        {
            var entries = Directory.GetFileSystemEntries(dir);
            if (entries.Length == 1 && Directory.Exists(entries[0]))
            {
                string inner = entries[0];
                foreach (var e in Directory.EnumerateFileSystemEntries(inner))
                {
                    string dst = Path.Combine(dir, Path.GetFileName(e));
                    if (Directory.Exists(e)) Directory.Move(e, dst);
                    else File.Move(e, dst, overwrite: true);
                }
                Directory.Delete(inner, recursive: true);
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[更新] 更新包目录上提失败:{ex.Message}");
        }
    }
}
