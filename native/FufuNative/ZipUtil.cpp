// ZipUtil.cpp — ZIP 解压与打包实现
// 可爱的芙芙 - 使用 Windows 内置 COM Shell 解压 + 简易 ZIP 写入
//
// 实现:调用 Shell.Application 的 CopyHere 解压 ZIP 内容
// 打包:写入空 ZIP 容器后用 Shell 复制文件进去

#include "pch.h"
#include "ZipUtil.h"
#include <shlobj.h>
#include <sstream>
#include <fileapi.h>

std::wstring ZipUtil::Utf8ToWide(const std::string& utf8) {
    if (utf8.empty()) return L"";
    int len = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), (int)utf8.size(), nullptr, 0);
    if (len <= 0) return L"";
    std::wstring wide(len, 0);
    int written = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), (int)utf8.size(), wide.data(), len);
    if (written <= 0) return L"";
    return wide;
}

std::string ZipUtil::WideToUtf8(const std::wstring& wide) {
    if (wide.empty()) return "";
    int len = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), (int)wide.size(),
                                    nullptr, 0, nullptr, nullptr);
    if (len <= 0) return "";
    std::string utf8(len, 0);
    int written = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), (int)wide.size(),
                                          utf8.data(), len, nullptr, nullptr);
    if (written <= 0) return "";
    return utf8;
}

bool ZipUtil::EnsureDirectory(const std::wstring& path) {
    if (path.empty()) return false;
    // 先检查路径是否已存在,且是目录(避免把已存在文件误判为目录)
    DWORD attr = GetFileAttributesW(path.c_str());
    if (attr != INVALID_FILE_ATTRIBUTES) {
        return (attr & FILE_ATTRIBUTE_DIRECTORY) != 0;
    }
    if (CreateDirectoryW(path.c_str(), nullptr)) return true;
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        // 复查属性,避免误把文件当目录
        attr = GetFileAttributesW(path.c_str());
        return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY);
    }
    // 递归创建父目录
    std::wstring parent = path;
    size_t pos = parent.find_last_of(L"\\/");
    if (pos != std::wstring::npos && pos > 0) {
        if (!EnsureDirectory(parent.substr(0, pos))) return false;
    }
    return CreateDirectoryW(path.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS;
}

int ZipUtil::ExtractZip(const std::string& zipPath, const std::string& destDir) {
    return ExtractZipWithProgress(zipPath, destDir, nullptr);
}

int ZipUtil::ExtractZipWithProgress(const std::string& zipPath, const std::string& destDir,
                                      std::function<void(int, int, const std::string&)> callback) {
    std::wstring wideZip = Utf8ToWide(zipPath);
    std::wstring wideDest = Utf8ToWide(destDir);

    // 确保目标目录存在
    if (!EnsureDirectory(wideDest)) {
        return 1; // 无法创建目标目录
    }

    // 规范化路径,确保末尾带分隔符
    if (!wideDest.empty() && wideDest.back() != L'\\' && wideDest.back() != L'/') {
        wideDest.push_back(L'\\');
    }

    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    bool needUninit = SUCCEEDED(hr);
    if (hr == RPC_E_CHANGED_MODE) {
        // 已经初始化为 MTA,尝试用 STA 模式不可行,继续使用现有
        needUninit = false;
    }

    int resultCode = 0;
    IDispatch* pShell = nullptr;
    IDispatch* pZipFolder = nullptr;
    VARIANT vZipFile;
    VariantInit(&vZipFile);

    // 创建 Shell.Application 对象
    hr = CoCreateInstance(CLSID_Shell, nullptr, CLSCTX_INPROC_SERVER,
                            IID_IDispatch, (void**)&pShell);
    if (FAILED(hr) || !pShell) {
        resultCode = 2;
        goto cleanup;
    }

    // 调用 Shell.NameSpace(zipPath) 获取 ZIP 文件夹对象
    {
        DISPID dispid;
        OLECHAR* methodName = const_cast<OLECHAR*>(L"NameSpace");
        hr = pShell->GetIDsOfNames(IID_NULL, &methodName, 1, LOCALE_USER_DEFAULT, &dispid);
        if (FAILED(hr)) { resultCode = 3; goto cleanup; }

        VARIANT args[1];
        args[0].vt = VT_BSTR;
        args[0].bstrVal = SysAllocString(wideZip.c_str());

        DISPPARAMS dp = { args, nullptr, 1, 0 };
        hr = pShell->Invoke(dispid, IID_NULL, LOCALE_USER_DEFAULT,
                              DISPATCH_METHOD, &dp, &vZipFile, nullptr, nullptr);
        SysFreeString(args[0].bstrVal);
        if (FAILED(hr) || vZipFile.vt != VT_DISPATCH) { resultCode = 4; goto cleanup; }
        // 注意:pZipFolder 仅做浅拷贝,不调用 AddRef,引用由 vZipFile 持有
        // 清理时只由 VariantClear(&vZipFile) 释放,不重复 Release
        pZipFolder = vZipFile.pdispVal;
    }

    // 调用 Shell.NameSpace(destDir) 获取目标文件夹对象
    {
        DISPID dispid;
        OLECHAR* methodName = const_cast<OLECHAR*>(L"NameSpace");
        hr = pShell->GetIDsOfNames(IID_NULL, &methodName, 1, LOCALE_USER_DEFAULT, &dispid);
        if (FAILED(hr)) { resultCode = 3; goto cleanup; }

        VARIANT args[1];
        args[0].vt = VT_BSTR;
        args[0].bstrVal = SysAllocString(wideDest.c_str());

        VARIANT vDestFolder;
        VariantInit(&vDestFolder);
        DISPPARAMS dp = { args, nullptr, 1, 0 };
        hr = pShell->Invoke(dispid, IID_NULL, LOCALE_USER_DEFAULT,
                              DISPATCH_METHOD, &dp, &vDestFolder, nullptr, nullptr);
        SysFreeString(args[0].bstrVal);
        if (FAILED(hr) || vDestFolder.vt != VT_DISPATCH) {
            VariantClear(&vDestFolder);
            resultCode = 5;
            goto cleanup;
        }

        // 调用 destFolder.CopyHere(zipFolder.Items()) 拷贝全部内容
        // 先获取 pZipFolder.Items() 集合
        {
            DISPID dispidItems;
            OLECHAR* itemsMethod = const_cast<OLECHAR*>(L"Items");
            hr = pZipFolder->GetIDsOfNames(IID_NULL, &itemsMethod, 1, LOCALE_USER_DEFAULT, &dispidItems);
            if (FAILED(hr)) { VariantClear(&vDestFolder); resultCode = 6; goto cleanup; }

            VARIANT vItems;
            VariantInit(&vItems);
            DISPPARAMS dpEmpty = { nullptr, nullptr, 0, 0 };
            hr = pZipFolder->Invoke(dispidItems, IID_NULL, LOCALE_USER_DEFAULT,
                                      DISPATCH_METHOD, &dpEmpty, &vItems, nullptr, nullptr);
            if (FAILED(hr) || vItems.vt != VT_DISPATCH) {
                VariantClear(&vItems);
                VariantClear(&vDestFolder);
                resultCode = 7;
                goto cleanup;
            }

            // 调用 destFolder.CopyHere(vItem, vOptions) - 静默无 UI
            // Flag 16: 不显示进度 UI;4: 不显示错误;1024: 不显示确认
            // 注意 DISPPARAMS.rgvarg 是逆序存放:rgvarg[0] 是最后一个参数 vOptions
            DISPID dispidCopy;
            OLECHAR* copyMethod = const_cast<OLECHAR*>(L"CopyHere");
            hr = vDestFolder.pdispVal->GetIDsOfNames(IID_NULL, &copyMethod, 1,
                                                      LOCALE_USER_DEFAULT, &dispidCopy);
            if (FAILED(hr)) { VariantClear(&vItems); VariantClear(&vDestFolder); resultCode = 8; goto cleanup; }

            VARIANT copyArgs[2];
            // rgvarg[0] = 最后一个参数 vOptions (int)
            copyArgs[0].vt = VT_I4;
            copyArgs[0].lVal = 16 | 4 | 1024;
            // rgvarg[1] = 第一个参数 vItem (FolderItems 集合)
            copyArgs[1] = vItems;

            DISPPARAMS dpCopy = { copyArgs, nullptr, 2, 0 };
            VARIANT vResult;
            VariantInit(&vResult);
            hr = vDestFolder.pdispVal->Invoke(dispidCopy, IID_NULL, LOCALE_USER_DEFAULT,
                                                 DISPATCH_METHOD, &dpCopy, &vResult, nullptr, nullptr);
            VariantClear(&vResult);
            VariantClear(&vItems);
            VariantClear(&vDestFolder);
            if (FAILED(hr)) { resultCode = 9; goto cleanup; }
        }
    }

    // 回调:由于 Shell 解压是异步的,这里做一次完成回调
    if (callback) {
        callback(1, 1, "解压完成");
    }

cleanup:
    // 注意:pZipFolder 不在此 Release,由 VariantClear(&vZipFile) 统一释放,避免双重释放
    if (pShell) pShell->Release();
    VariantClear(&vZipFile);
    if (needUninit) CoUninitialize();
    return resultCode;
}

// 创建 ZIP:使用最简实现,通过 Shell 复制到空 ZIP 容器
int ZipUtil::CreateZip(const std::string& srcDir, const std::string& zipPath) {
    std::wstring wideSrc = Utf8ToWide(srcDir);
    std::wstring wideZip = Utf8ToWide(zipPath);

    // 确保输出目录存在
    {
        size_t pos = wideZip.find_last_of(L"\\/");
        if (pos != std::wstring::npos && pos > 0) {
            EnsureDirectory(wideZip.substr(0, pos));
        }
    }

    // 写入一个空 ZIP 文件头(最小合法 ZIP)
    // 写入 22 字节空 EOCD(End of Central Directory),含 PK\x05\x06 签名
    {
        HANDLE hFile = CreateFileW(wideZip.c_str(), GENERIC_WRITE, 0, nullptr,
                                     CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (hFile == INVALID_HANDLE_VALUE) return 10;
        const uint8_t emptyZip[] = {
            0x50, 0x4B, 0x05, 0x06, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };
        DWORD written = 0;
        BOOL ok = WriteFile(hFile, emptyZip, sizeof(emptyZip), &written, nullptr);
        CloseHandle(hFile);
        if (!ok || written != sizeof(emptyZip)) {
            return 11;  // 写入空 ZIP 头失败
        }
    }

    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    bool needUninit = SUCCEEDED(hr);
    if (hr == RPC_E_CHANGED_MODE) needUninit = false;

    int resultCode = 0;
    IDispatch* pShell = nullptr;

    hr = CoCreateInstance(CLSID_Shell, nullptr, CLSCTX_INPROC_SERVER,
                            IID_IDispatch, (void**)&pShell);
    if (FAILED(hr) || !pShell) { resultCode = 2; goto create_cleanup; }

    {
        // 获取 zip 文件夹对象
        DISPID dispid;
        OLECHAR* methodName = const_cast<OLECHAR*>(L"NameSpace");
        hr = pShell->GetIDsOfNames(IID_NULL, &methodName, 1, LOCALE_USER_DEFAULT, &dispid);
        if (FAILED(hr)) { resultCode = 3; goto create_cleanup; }

        VARIANT args[1];
        args[0].vt = VT_BSTR;
        args[0].bstrVal = SysAllocString(wideZip.c_str());
        VARIANT vZip;
        VariantInit(&vZip);
        DISPPARAMS dp = { args, nullptr, 1, 0 };
        hr = pShell->Invoke(dispid, IID_NULL, LOCALE_USER_DEFAULT,
                              DISPATCH_METHOD, &dp, &vZip, nullptr, nullptr);
        SysFreeString(args[0].bstrVal);
        if (FAILED(hr) || vZip.vt != VT_DISPATCH) {
            VariantClear(&vZip);
            resultCode = 4;
            goto create_cleanup;
        }

        // 拷贝整个源目录内容到 zip
        DISPID dispidCopy;
        OLECHAR* copyMethod = const_cast<OLECHAR*>(L"CopyHere");
        hr = vZip.pdispVal->GetIDsOfNames(IID_NULL, &copyMethod, 1,
                                            LOCALE_USER_DEFAULT, &dispidCopy);
        if (FAILED(hr)) { VariantClear(&vZip); resultCode = 5; goto create_cleanup; }

        // 注意 DISPPARAMS.rgvarg 是逆序存放:rgvarg[0] 是最后一个参数 vOptions
        VARIANT copyArgs[2];
        // rgvarg[0] = 最后一个参数 vOptions (int)
        copyArgs[0].vt = VT_I4;
        copyArgs[0].lVal = 16 | 4 | 1024;
        // rgvarg[1] = 第一个参数 vItem (源目录路径 BSTR)
        copyArgs[1].vt = VT_BSTR;
        copyArgs[1].bstrVal = SysAllocString(wideSrc.c_str());

        DISPPARAMS dpCopy = { copyArgs, nullptr, 2, 0 };
        VARIANT vResult;
        VariantInit(&vResult);
        hr = vZip.pdispVal->Invoke(dispidCopy, IID_NULL,
                                       LOCALE_USER_DEFAULT,
                                       DISPATCH_METHOD, &dpCopy,
                                       &vResult, nullptr, nullptr);
        VariantClear(&vResult);
        SysFreeString(copyArgs[1].bstrVal);
        VariantClear(&vZip);
        if (FAILED(hr)) { resultCode = 6; goto create_cleanup; }
    }

create_cleanup:
    if (pShell) pShell->Release();
    if (needUninit) CoUninitialize();
    return resultCode;
}
