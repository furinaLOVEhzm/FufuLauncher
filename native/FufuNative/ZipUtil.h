// ZipUtil.h — ZIP 解压与打包工具
// 可爱的芙芙 - 使用 Windows shell ZIP 解压(无需第三方依赖)
// 注意:此实现通过 Shell COM 调用 Windows 内置 ZIP 支持

#pragma once
#include "pch.h"
#include <functional>

class ZipUtil {
public:
    /// 解压 ZIP 到指定目录
    /// 返回 0 成功,非 0 失败
    static int ExtractZip(const std::string& zipPath, const std::string& destDir);

    /// 解压 ZIP 并回调进度
    static int ExtractZipWithProgress(const std::string& zipPath, const std::string& destDir,
                                       std::function<void(int, int, const std::string&)> callback);

    /// 创建 ZIP 备份包
    static int CreateZip(const std::string& srcDir, const std::string& zipPath);

private:
    static std::wstring Utf8ToWide(const std::string& utf8);
    static std::string WideToUtf8(const std::wstring& wide);

    /// 确保目录存在(递归创建)
    static bool EnsureDirectory(const std::wstring& path);
};
