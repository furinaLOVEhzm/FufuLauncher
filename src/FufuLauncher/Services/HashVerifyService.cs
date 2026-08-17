// HashVerifyService.cs — 文件哈希校验与修复服务
// 可爱的芙芙 - 阶段2 模块
//
// 校验 jar / libraries 文件 SHA1,损坏或缺失文件标记重新下载

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

public class FileVerifyResult
{
    public string Path { get; set; } = "";
    public string ExpectedSha1 { get; set; } = "";
    public string ActualSha1 { get; set; } = "";
    public bool Exists { get; set; }
    public bool Valid { get; set; }
    public long ExpectedSize { get; set; }
    public long ActualSize { get; set; }
}

public class HashVerifyService
{
    private readonly NativeInteropService _nativeInterop;

    public HashVerifyService(NativeInteropService nativeInterop)
    {
        _nativeInterop = nativeInterop;
    }

    /// <summary>校验单个文件,缺失或哈希不符返回失败</summary>
    public FileVerifyResult Verify(string filePath, string expectedSha1, long expectedSize = 0)
    {
        var result = new FileVerifyResult
        {
            Path = filePath,
            ExpectedSha1 = expectedSha1,
            ExpectedSize = expectedSize
        };

        result.Exists = File.Exists(filePath);
        if (!result.Exists)
        {
            result.Valid = false;
            return result;
        }

        var fi = new FileInfo(filePath);
        result.ActualSize = fi.Length;
        if (expectedSize > 0 && fi.Length != expectedSize)
        {
            result.Valid = false;
            return result;
        }

        if (!string.IsNullOrEmpty(expectedSha1))
        {
            result.ActualSha1 = _nativeInterop.ComputeFileSHA1(filePath);
            result.Valid = string.Equals(result.ActualSha1, expectedSha1,
                                           StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            result.Valid = true;
        }

        return result;
    }

    /// <summary>批量校验,返回需重新下载的文件列表</summary>
    public List<FileVerifyResult> VerifyBatch(
        IEnumerable<(string Path, string Sha1, long Size)> files)
    {
        var needRedownload = new List<FileVerifyResult>();
        foreach (var (path, sha1, size) in files)
        {
            var r = Verify(path, sha1, size);
            if (!r.Valid) needRedownload.Add(r);
        }
        return needRedownload;
    }
}
