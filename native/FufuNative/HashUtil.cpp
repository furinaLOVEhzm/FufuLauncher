// HashUtil.cpp — 文件哈希计算实现
// 可爱的芙芙 - 使用 Windows BCrypt API 高性能计算 SHA1/SHA256

#include "pch.h"
#include "HashUtil.h"
#include <bcrypt.h>
#include <sstream>
#include <iomanip>

#pragma comment(lib, "bcrypt.lib")

// 将 UTF-8 字符串转为 UTF-16(wstring),用于 CreateFileW
static std::wstring Utf8ToWide(const std::string& utf8) {
    if (utf8.empty()) return L"";
    int len = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), (int)utf8.size(), nullptr, 0);
    if (len <= 0) return L"";
    std::wstring wide(len, 0);
    int written = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), (int)utf8.size(), wide.data(), len);
    if (written <= 0) return L"";
    return wide;
}

std::string HashUtil::ToHexLower(const uint8_t* data, size_t len) {
    static const char hex[] = "0123456789abcdef";
    std::string result;
    result.reserve(len * 2);
    for (size_t i = 0; i < len; ++i) {
        result.push_back(hex[(data[i] >> 4) & 0x0F]);
        result.push_back(hex[data[i] & 0x0F]);
    }
    return result;
}

std::string HashUtil::ComputeFileHash(const std::string& filePath, LPCWSTR algorithm) {
    std::wstring widePath = Utf8ToWide(filePath);

    HANDLE hFile = CreateFileW(widePath.c_str(), GENERIC_READ, FILE_SHARE_READ,
                                nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hFile == INVALID_HANDLE_VALUE) {
        return std::string();
    }

    // 获取文件大小
    LARGE_INTEGER fileSize;
    if (!GetFileSizeEx(hFile, &fileSize)) {
        CloseHandle(hFile);
        return std::string();
    }

    std::string result;
    BCRYPT_ALG_HANDLE hAlg = nullptr;
    BCRYPT_HASH_HANDLE hHash = nullptr;
    uint8_t* hashData = nullptr;
    DWORD hashLen = 0;
    DWORD cbData = 0;

    NTSTATUS status = BCryptOpenAlgorithmProvider(&hAlg, algorithm, nullptr, 0);
    if (status != 0) goto cleanup;

    status = BCryptGetProperty(hAlg, BCRYPT_HASH_LENGTH,
                                (PUCHAR)&hashLen, sizeof(hashLen), &cbData, 0);
    if (status != 0) goto cleanup;

    hashData = new (std::nothrow) uint8_t[hashLen];
    if (!hashData) { resultCode = -3; goto cleanup; }

    status = BCryptCreateHash(hAlg, &hHash, nullptr, 0, nullptr, 0, 0);
    if (status != 0) goto cleanup;

    {
        // 1MB 缓冲区,流式读取
        const DWORD bufSize = 1 * 1024 * 1024;
        uint8_t* buffer = new (std::nothrow) uint8_t[bufSize];
        if (!buffer) { resultCode = -3; goto cleanup; }
        DWORD bytesRead = 0;
        ULONG64 totalRead = 0;

        while (totalRead < (ULONG64)fileSize.QuadPart) {
            DWORD toRead = bufSize;
            if (!ReadFile(hFile, buffer, toRead, &bytesRead, nullptr) || bytesRead == 0) {
                delete[] buffer;
                goto cleanup;
            }
            status = BCryptHashData(hHash, buffer, bytesRead, 0);
            if (status != 0) {
                delete[] buffer;
                goto cleanup;
            }
            totalRead += bytesRead;
        }
        delete[] buffer;
    }

    status = BCryptFinishHash(hHash, hashData, hashLen, 0);
    if (status != 0) goto cleanup;

    result = ToHexLower(hashData, hashLen);

cleanup:
    if (hHash) BCryptDestroyHash(hHash);
    if (hAlg) BCryptCloseAlgorithmProvider(hAlg, 0);
    if (hashData) delete[] hashData;
    CloseHandle(hFile);
    return result;
}

std::string HashUtil::ComputeFileSHA1(const std::string& filePath) {
    return ComputeFileHash(filePath, BCRYPT_SHA1_ALGORITHM);
}

std::string HashUtil::ComputeFileSHA256(const std::string& filePath) {
    return ComputeFileHash(filePath, BCRYPT_SHA256_ALGORITHM);
}
