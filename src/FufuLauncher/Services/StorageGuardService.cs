// StorageGuardService.cs — 存储环境预检与 IO 异常分类服务
// FufuLauncher - 底层内核模块
//
// 职责(对应重构要求 ①-3):
// 1. 磁盘剩余空间检测(下载/解压/导入前预检,含 1GB 安全缓冲)
// 2. 文件锁探测(目标文件是否被其他程序占用)
// 3. 目录读写权限校验(临时文件试写,失败即判定无权限)
// 4. IO 异常统一分类 → 玩家友好中文提示(断网/压缩包损坏/磁盘已满/权限不足/文件占用/版本损坏)
//
// C++ 加速路径:FufuNative.dll 提供等价导出(FufuGetDiskFreeBytes/FufuIsFileLocked/FufuCanWriteDir),
// 由 NativeInteropService 转发;DLL 缺失时本类的纯 .NET 实现完全等价兜底,功能不受影响。

using System;
using System.IO;

namespace FufuLauncher.Services;

/// <summary>存储预检结果枚举,供 UI 区分提示与图标</summary>
public enum StorageCheckResult
{
    Ok,             // 通过
    DiskFull,       // 磁盘空间不足
    FileLocked,     // 文件被占用
    NoPermission,   // 无读写权限
    PathInvalid     // 路径非法(空/过长/非法字符)
}

public class StorageGuardService
{
    /// <summary>磁盘空间安全缓冲(解压峰值可能超出压缩包体积,预留 1GB)</summary>
    public const long SafetyBufferBytes = 1024L * 1024 * 1024;

    private readonly NativeInteropService _nativeInterop;

    public StorageGuardService(NativeInteropService nativeInterop)
    {
        _nativeInterop = nativeInterop;
    }

    /// <summary>
    /// 综合预检:路径合法性 → 磁盘空间 → 目录写权限 → 目标文件锁。
    /// 下载/解压/整合包导入前统一调用,任何一项失败即返回具体原因,绝不带着隐患开工。
    /// </summary>
    public (StorageCheckResult Result, string Message) Precheck(string targetPath, long requiredBytes)
    {
        // 1. 路径合法性
        if (string.IsNullOrWhiteSpace(targetPath))
            return (StorageCheckResult.PathInvalid, "目标路径为空,无法继续。");
        try { _ = Path.GetFullPath(targetPath); }
        catch (Exception ex)
        {
            return (StorageCheckResult.PathInvalid, $"目标路径非法:{ex.Message}");
        }

        // 2. 磁盘空间(按目标路径所在盘检测,而不是想当然用启动器所在盘)
        string checkDir = Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath) ?? targetPath;
        if (!EnsureDiskSpace(checkDir, requiredBytes, out string diskMsg))
            return (StorageCheckResult.DiskFull, diskMsg);

        // 3. 目录写权限(临时文件试写)
        if (Directory.Exists(checkDir) && !EnsureWritable(checkDir, out string permMsg))
            return (StorageCheckResult.NoPermission, permMsg);

        // 4. 目标文件锁(若目标文件已存在)
        if (File.Exists(targetPath) && IsFileLocked(targetPath))
            return (StorageCheckResult.FileLocked, $"文件「{Path.GetFileName(targetPath)}」正被其他程序占用,请关闭相关程序后重试。");

        return (StorageCheckResult.Ok, "预检通过");
    }

    /// <summary>检测目标目录所在盘剩余空间是否满足 requiredBytes + 安全缓冲</summary>
    public bool EnsureDiskSpace(string dir, long requiredBytes, out string message)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root))
                root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

            // 优先走 C++ 加速路径(内核级 GetDiskFreeSpaceEx),失败回退托管 DriveInfo
            long available = _nativeInterop.GetDiskFreeBytes(root);
            if (available < 0)
                available = new DriveInfo(root).AvailableFreeSpace;

            long needed = Math.Max(0, requiredBytes) + SafetyBufferBytes;
            if (available < needed)
            {
                message = $"磁盘 {root} 剩余 {FmtSize(available)},本次操作需要约 {FmtSize(needed)}(含 1GB 安全缓冲)。\n" +
                          "请清理磁盘空间或更换安装位置后重试。";
                return false;
            }
            message = $"磁盘 {root} 剩余 {FmtSize(available)},空间充足。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"磁盘空间检测异常:{ex.Message}";
            return false;
        }
    }

    /// <summary>文件锁探测:尝试独占打开,失败即视为被占用(只读探测,不改文件内容)</summary>
    public static bool IsFileLocked(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; } // 权限问题单独归类
        catch { return true; }
    }

    /// <summary>目录写权限校验:试写一个 0 字节临时文件后立即删除</summary>
    public static bool EnsureWritable(string dir, out string message)
    {
        string probe = Path.Combine(dir, $".fufu_probe_{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.WriteByte(0);
            }
            message = "目录可写。";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            message = $"目录「{dir}」无写入权限,请以管理员身份运行或更换目录。";
            return false;
        }
        catch (IOException ex)
        {
            message = $"目录写入测试失败:{ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            message = $"目录权限检测异常:{ex.Message}";
            return false;
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* 探测残留无害 */ }
        }
    }

    /// <summary>IO 异常统一分类 → 玩家友好中文提示(禁止把堆栈直接甩给用户)</summary>
    public static string ClassifyIoError(Exception ex, string context)
    {
        return ex switch
        {
            PathTooLongException => $"{context}失败:路径过长,请缩短目录层级。",
            UnauthorizedAccessException => $"{context}失败:权限不足,请检查目录权限或以管理员身份运行。",
            InvalidDataException => $"{context}失败:压缩包损坏或格式不受支持,请重新下载。",
            IOException io when IsLockMessage(io.Message) => $"{context}失败:文件被其他程序占用,请关闭相关程序后重试。",
            IOException io when io.Message.Contains("空间") || io.Message.Contains("space", StringComparison.OrdinalIgnoreCase)
                => $"{context}失败:磁盘空间已满,请清理磁盘后重试。",
            IOException io => $"{context}失败:{io.Message}",
            OutOfMemoryException => $"{context}失败:内存不足,请关闭其他程序后重试。",
            _ => $"{context}失败:{Truncate(ex.Message)}"
        };
    }

    private static bool IsLockMessage(string msg) =>
        msg.Contains("占用") || msg.Contains("being used") || msg.Contains("process") || msg.Contains("another process");

    private static string Truncate(string s) => string.IsNullOrEmpty(s) ? "未知错误" : (s.Length > 160 ? s[..160] + "…" : s);

    /// <summary>字节数人性化格式(B/KB/MB/GB)</summary>
    public static string FmtSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
    };
}
