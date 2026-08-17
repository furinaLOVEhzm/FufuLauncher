// FufuNative.cpp — DLL 导出入口实现
// 可爱的芙芙 - 原生性能模块

#include "pch.h"
#include "FufuNative.h"
#include "HashUtil.h"
#include "ZipUtil.h"

// DLL 入口
BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    switch (ul_reason_for_call) {
        case DLL_PROCESS_ATTACH:
        case DLL_THREAD_ATTACH:
        case DLL_THREAD_DETACH:
        case DLL_PROCESS_DETACH:
            break;
    }
    return TRUE;
}

// 辅助:将 std::string 复制为 C 字符串(供 C# 释放)
static const char* AllocCString(const std::string& s) {
    char* buf = new char[s.size() + 1];
    memcpy(buf, s.c_str(), s.size() + 1);
    return buf;
}

extern "C" {

FUFU_API const char* FufuComputeFileSHA1(const char* filePath) {
    if (!filePath) return nullptr;
    std::string path(filePath);
    std::string hash = HashUtil::ComputeFileSHA1(path);
    if (hash.empty()) return nullptr;
    return AllocCString(hash);
}

FUFU_API const char* FufuComputeFileSHA256(const char* filePath) {
    if (!filePath) return nullptr;
    std::string path(filePath);
    std::string hash = HashUtil::ComputeFileSHA256(path);
    if (hash.empty()) return nullptr;
    return AllocCString(hash);
}

FUFU_API void FufuFreeString(const char* ptr) {
    if (ptr) {
        delete[] ptr;
    }
}

FUFU_API int FufuExtractZip(const char* zipPath, const char* destDir) {
    if (!zipPath || !destDir) return -1;
    return ZipUtil::ExtractZip(std::string(zipPath), std::string(destDir));
}

FUFU_API int FufuExtractZipWithProgress(const char* zipPath, const char* destDir,
                                         void (*progressCallback)(int, int, const char*)) {
    if (!zipPath || !destDir) return -1;
    auto cb = [progressCallback](int cur, int total, const std::string& name) {
        if (progressCallback) {
            progressCallback(cur, total, name.c_str());
        }
    };
    return ZipUtil::ExtractZipWithProgress(std::string(zipPath), std::string(destDir), cb);
}

FUFU_API int FufuCreateZip(const char* srcDir, const char* zipPath) {
    if (!srcDir || !zipPath) return -1;
    return ZipUtil::CreateZip(std::string(srcDir), std::string(zipPath));
}

FUFU_API const char* FufuGetVersion() {
    return AllocCString("FufuNative 1.0.0.0 - 可爱的芙芙");
}

} // extern "C"
