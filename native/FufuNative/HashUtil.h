// HashUtil.h — 文件哈希计算工具
// 使用 Windows BCrypt API 实现高性能 SHA1/SHA256

#pragma once
#include "pch.h"

class HashUtil {
public:
    /// 计算文件 SHA1,返回小写十六进制字符串,失败返回空串
    static std::string ComputeFileSHA1(const std::string& filePath);

    /// 计算文件 SHA256,返回小写十六进制字符串,失败返回空串
    static std::string ComputeFileSHA256(const std::string& filePath);

private:
    /// 通用文件哈希计算(分块读取 + BCrypt)
    /// algorithm: L"SHA1" 或 L"SHA256"
    static std::string ComputeFileHash(const std::string& filePath, LPCWSTR algorithm);

    /// 字节数组转小写十六进制字符串
    static std::string ToHexLower(const uint8_t* data, size_t len);
};
