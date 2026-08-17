// NativeInteropService.cs — C# 调用 C++ 原生 DLL 的 P/Invoke 封装
// 可爱的芙芙 - 高性能文件哈希与 ZIP 解压
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
                // 1. runtimes\win-x64\native\ (csproj 配置的发布路径)
                // 2. exe 同级目录 (开发调试时可能直接复制)
                // 3. exe 同级 runtimes\win-x64\native\ (自包含发布)
                string baseDir = AppContext.BaseDirectory;
                var candidates = new[]
                {
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                    Path.Combine(baseDir, "runtimes", "win-x64", "native"),
                    baseDir,
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
