// NativeInteropService.cs — C# 调用 C++ 原生 DLL 的 P/Invoke 封装
// FufuLauncher - 高性能文件哈希与 ZIP 解压
//
// 策略:C++ 原生 DLL(FufuNative.dll)作为可选的性能加速路径;
//       若 DLL 缺失或调用失败,自动 fallback 到纯 .NET 托管实现,
//       功能完全等价,保证程序在无 C++ DLL 的环境也能正常运行。
//
// 对应 native\FufuNative\FufuNative.dll
// DLL 路径:runtimes\win-x64\native\FufuNative.dll(由 csproj 复制,可选)

using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FufuLauncher.Services;

public class NativeInteropService
{
    private const string DllName = "FufuNative.dll";

    // 标记原生 DLL 是否已确认可用(首次调用后缓存,避免重复探测开销)
    private bool? _nativeAvailable;

    // DLL 是否已尝试过路径修复
    private bool _dllPathResolved;
    private readonly object _dllLock = new();

    public NativeInteropService()
    {
        // 注册 DLL 导入解析器,让 DllImport 能找到 runtimes 目录下的原生 DLL
        TryResolveDllPath();
    }

    /// <summary>
    /// 尝试将 FufuNative.dll 的搜索路径注册到 .NET 的原生库解析器。
    /// DLL 可能被复制到多个候选位置,逐一探测并注册第一个找到的目录。
    /// </summary>
    private void TryResolveDllPath()
    {
        lock (_dllLock)
        {
            if (_dllPathResolved) return;
            _dllPathResolved = true;

            try
            {
                // 候选路径(按优先级排列):
                // 1. exe 真实目录的 runtimes\win-x64\native\(单文件发布:AppContext.BaseDirectory
                //    指向 Temp 自解压目录,必须用 ProcessPath 才能找到 exe 旁的松散 runtimes)
                // 2. AppContext.BaseDirectory 的 runtimes\win-x64\native\(非单文件场景)
                // 3. exe 同级目录 (开发调试时可能直接复制)
                // 4/5. 源码树 native 构建产物(开发调试)
                string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                string baseDir = AppContext.BaseDirectory;
                var candidates = new[]
                {
                    Path.Combine(exeDir, "runtimes", "win-x64", "native"),
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                    exeDir,
                    Path.Combine(baseDir, "..", "..", "native", "FufuNative", "x64", "Release"),
                    Path.Combine(baseDir, "..", "..", "native", "FufuNative", "x64", "Debug"),
                };

                foreach (var dir in candidates)
                {
                    string dllPath = Path.Combine(dir, DllName);
                    if (File.Exists(dllPath))
                    {
                        // 将找到的目录注册为原生库搜索路径
                        string capturedDir = dir; // 闭包捕获
                        NativeLibrary.SetDllImportResolver(
                            typeof(NativeInteropService).Assembly,
                            (libraryName, assembly, searchPath) =>
                            {
                                if (libraryName == DllName)
                                {
                                    string resolved = Path.Combine(capturedDir, DllName);
                                    if (File.Exists(resolved))
                                    {
                                        if (NativeLibrary.TryLoad(resolved, out var handle))
                                            return handle;
                                    }
                                }
                                return IntPtr.Zero;
                            });
                        App.WriteAppLog($"[Native] DLL 解析器已注册,路径={dllPath}");
                        return;
                    }
                }
                App.WriteAppLog("[Native] 未找到 FufuNative.dll,将使用纯 .NET 托管实现");
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[Native] DLL 路径解析异常:{ex.Message}");
            }
        }
    }

    // ===== 文件哈希 =====
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FufuComputeFileSHA1([MarshalAs(UnmanagedType.LPUTF8Str)] string filePath);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FufuComputeFileSHA256([MarshalAs(UnmanagedType.LPUTF8Str)] string filePath);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FufuFreeString(IntPtr ptr);

    /// <summary>计算文件 SHA1,返回小写十六进制字符串,失败返回空串</summary>
    public string ComputeFileSHA1(string filePath)
    {
        // 优先使用原生实现(性能更高)
        if (IsNativeAvailable())
        {
            try
            {
                IntPtr ptr = FufuComputeFileSHA1(filePath);
                return PtrToUtf8StringAndFree(ptr);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // 原生 DLL 在运行时丢失或失效,切换到 fallback 并标记不可用
                _nativeAvailable = false;
            }
        }
        return ManagedComputeFileHash(filePath, SHA1.Create());
    }

    /// <summary>计算文件 SHA256,返回小写十六进制字符串,失败返回空串</summary>
    public string ComputeFileSHA256(string filePath)
    {
        if (IsNativeAvailable())
        {
            try
            {
                IntPtr ptr = FufuComputeFileSHA256(filePath);
                return PtrToUtf8StringAndFree(ptr);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _nativeAvailable = false;
            }
        }
        return ManagedComputeFileHash(filePath, SHA256.Create());
    }

    // ===== ZIP 解压与打包 =====
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FufuExtractZip([MarshalAs(UnmanagedType.LPUTF8Str)] string zipPath,
                                                [MarshalAs(UnmanagedType.LPUTF8Str)] string destDir);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FufuCreateZip([MarshalAs(UnmanagedType.LPUTF8Str)] string srcDir,
                                              [MarshalAs(UnmanagedType.LPUTF8Str)] string zipPath);

    // ===== 存储预检(磁盘空间/文件锁/写权限)=====
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern long FufuGetDiskFreeBytes([MarshalAs(UnmanagedType.LPUTF8Str)] string dir);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FufuIsFileLocked([MarshalAs(UnmanagedType.LPUTF8Str)] string filePath);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FufuCanWriteDir([MarshalAs(UnmanagedType.LPUTF8Str)] string dir);

    /// <summary>获取目录所在盘可用字节数;原生不可用/失败返回 -1,由调用方回退 DriveInfo</summary>
    public long GetDiskFreeBytes(string dir)
    {
        if (IsNativeAvailable())
        {
            try { return FufuGetDiskFreeBytes(dir); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { _nativeAvailable = false; }
        }
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(dir));
            return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return -1; }
    }

    /// <summary>文件锁探测(原生优先,托管兜底)</summary>
    public bool IsFileLockedNative(string filePath)
    {
        if (IsNativeAvailable())
        {
            try { return FufuIsFileLocked(filePath) == 1; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { _nativeAvailable = false; }
        }
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    /// <summary>目录写权限校验(原生优先,托管兜底)</summary>
    public bool CanWriteDirNative(string dir)
    {
        if (IsNativeAvailable())
        {
            try { return FufuCanWriteDir(dir) == 1; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { _nativeAvailable = false; }
        }
        string probe = Path.Combine(dir, $".fufu_probe_{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) fs.WriteByte(0);
            return true;
        }
        catch { return false; }
        finally { try { if (File.Exists(probe)) File.Delete(probe); } catch { } }
    }

    /// <summary>解压 ZIP 到目标目录,成功返回 true</summary>
    public bool ExtractZip(string zipPath, string destDir)
    {
        if (IsNativeAvailable())
        {
            try
            {
                int code = FufuExtractZip(zipPath, destDir);
                if (code == 0) return true;
                // 非零返回码表示原生侧失败,fallback 到托管实现
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _nativeAvailable = false;
            }
        }
        return ManagedExtractZip(zipPath, destDir);
    }

    /// <summary>创建 ZIP 备份包(源目录 -> ZIP 文件),成功返回 true</summary>
    public bool CreateZip(string srcDir, string zipPath)
    {
        if (IsNativeAvailable())
        {
            try
            {
                int code = FufuCreateZip(srcDir, zipPath);
                if (code == 0) return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _nativeAvailable = false;
            }
        }
        return ManagedCreateZip(srcDir, zipPath);
    }

    /// <summary>托管 ZIP 打包(绕过原生路径;原生 Shell COM 复制为异步返回,备份导出等要求完整的场景恒用托管)</summary>
    public bool CreateZipManaged(string srcDir, string zipPath) => ManagedCreateZip(srcDir, zipPath);

    // ===== 辅助方法 =====

    /// <summary>将 C++ 分配的 UTF-8 C 字符串转 C# string,并释放原生内存</summary>
    private string PtrToUtf8StringAndFree(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return string.Empty;
        try
        {
            // 查找字符串结尾
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            byte[] buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return Encoding.UTF8.GetString(buffer);
        }
        finally
        {
            FufuFreeString(ptr);
        }
    }

    /// <summary>检查原生 DLL 是否可用(首次调用探测,之后缓存结果)</summary>
    public bool IsNativeAvailable()
    {
        if (_nativeAvailable.HasValue) return _nativeAvailable.Value;
        try
        {
            IntPtr ptr = FufuComputeFileSHA1("");
            if (ptr != IntPtr.Zero) FufuFreeString(ptr);
            _nativeAvailable = true;
        }
        catch
        {
            _nativeAvailable = false;
        }
        return _nativeAvailable.Value;
    }

    // ===== 托管 fallback 实现 (纯 .NET,功能等价) =====

    /// <summary>.NET 托管哈希计算,小写十六进制输出,失败返回空串</summary>
    private static string ManagedComputeFileHash(string filePath, HashAlgorithm algorithm)
    {
        try
        {
            using (algorithm)
            using (var fs = File.OpenRead(filePath))
            {
                byte[] hash = algorithm.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>.NET 托管 ZIP 解压</summary>
    private static bool ManagedExtractZip(string zipPath, string destDir)
    {
        try
        {
            Directory.CreateDirectory(destDir);
            ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ===== 字节级进度解压(下载字节与解压字节分开统计的关键基建)=====

    /// <summary>
    /// 解压 ZIP 并按「解压输出字节」报告进度(与网络下载字节完全分开统计)。
    /// 进度分母 = 压缩包内全部条目的未压缩总字节(两遍枚举,第一遍只读条目元数据);
    /// 解压体积大于压缩包体积也不会溢出:上层另有 Math.Clamp 双重钳位。
    /// </summary>
    /// <param name="onBytes">增量字节回调(已解压输出字节增量)</param>
    /// <returns>Ok=是否成功;TotalUncompressed=压缩包全部条目未压缩总字节(进度分母)</returns>
    public async Task<(bool Ok, long TotalUncompressed)> ExtractZipWithByteProgress(
        string zipPath, string destDir, Action<long>? onBytes)
    {
        long totalUncompressed = 0;
        try
        {
            Directory.CreateDirectory(destDir);
            // 第一遍:只读条目元数据统计未压缩总字节(不读取文件内容,开销极小)
            using (var probe = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in probe.Entries) totalUncompressed += e.Length;
            }

            // 第二遍:流式解压,每写一块回调一次增量字节
            using var archive = ZipFile.OpenRead(zipPath);
            byte[] buffer = new byte[128 * 1024];
            foreach (var entry in archive.Entries)
            {
                // 目录条目(以 / 结尾且无文件名)只建目录
                string target = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                // ZIP 路径穿越防护:目标必须仍落在 destDir 内
                if (!target.StartsWith(Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"压缩包包含非法路径条目:{entry.FullName}");
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var src = entry.Open();
                await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                                                      bufferSize: 128 * 1024, useAsync: true);
                int read;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    onBytes?.Invoke(read);
                }
            }
            return (true, totalUncompressed);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[解压] 字节级进度解压失败:{ex.Message}");
            return (false, totalUncompressed);
        }
    }

    /// <summary>.NET 托管 ZIP 打包</summary>
    private static bool ManagedCreateZip(string srcDir, string zipPath)
    {
        try
        {
            // 父目录需存在,否则 FileStream 创建会失败
            var parent = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(srcDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
