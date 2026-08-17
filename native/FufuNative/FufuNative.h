// FufuNative.h — 可爱的芙芙 原生 DLL 导出接口
// 供 C# 通过 P/Invoke 调用的高性能文件哈希与 ZIP 解压接口
//
// 调用约定: cdecl (C# 侧使用 CallingConvention.Cdecl)
// 字符串编码: UTF-8 (C# 侧使用 [MarshalAs(UnmanagedType.LPUTF8Str)] string)

#pragma once

#ifdef FUFUNATIVE_EXPORTS
#define FUFU_API __declspec(dllexport)
#else
#define FUFU_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

    /// 计算文件 SHA1 哈希,返回小写十六进制字符串
    /// 调用方负责用 FufuFreeString 释放返回的内存
    /// 失败返回 nullptr
    FUFU_API const char* FufuComputeFileSHA1(const char* filePath);

    /// 计算文件 SHA256 哈希,返回小写十六进制字符串
    FUFU_API const char* FufuComputeFileSHA256(const char* filePath);

    /// 释放由本 DLL 分配的字符串内存
    FUFU_API void FufuFreeString(const char* ptr);

    /// 解压 ZIP 文件到指定目录
    /// zipPath: ZIP 文件路径 (UTF-8)
    /// destDir: 目标目录 (UTF-8)
    /// 返回 0 成功,非 0 失败(错误码)
    FUFU_API int FufuExtractZip(const char* zipPath, const char* destDir);

    /// 解压 ZIP 并回调每个文件的解压进度
    /// progressCallback: void(int current, int total, const char* fileName)
    FUFU_API int FufuExtractZipWithProgress(const char* zipPath, const char* destDir,
                                             void (*progressCallback)(int, int, const char*));

    /// 创建 ZIP 备份包(用于实例导出)
    /// srcDir: 源目录 (UTF-8)
    /// zipPath: 输出 ZIP 路径 (UTF-8)
    /// 返回 0 成功
    FUFU_API int FufuCreateZip(const char* srcDir, const char* zipPath);

    /// 获取 DLL 版本号字符串
    FUFU_API const char* FufuGetVersion();

#ifdef __cplusplus
}
#endif
