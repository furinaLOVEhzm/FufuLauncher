// JunctionHelper.cs — NTFS 目录联接(Junction)工具
// 可爱的芙芙
//
// 用途:游戏工作目录必须包含 saves/mods 子目录(MC 机制决定),
// 而规范要求存档/模组统一存放在 APP\mcGAME\saves、APP\mcGAME\mods。
// 通过在实例目录内创建目录联接(实例\saves → 根\saves\{实例}),
// 让游戏读写的物理文件实际落在规范目录,两全其美。
//
// Junction 特性:同卷目录链接,创建不需要管理员权限,所有程序透明穿透。

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FufuLauncher.Services;

public static class JunctionHelper
{
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const int MAXIMUM_REPARSE_DATA_BUFFER_SIZE = 16 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct REPARSE_DATA_BUFFER
    {
        public uint ReparseTag;
        public ushort ReparseDataLength;
        public ushort Reserved;
        public ushort SubstituteNameOffset;
        public ushort SubstituteNameLength;
        public ushort PrintNameOffset;
        public ushort PrintNameLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x3FF0)]
        public byte[] PathBuffer;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, FileShare dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>判断目录是否为联接/符号链接</summary>
    public static bool IsJunction(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            if (!di.Exists) return false;
            return di.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }

    /// <summary>
    /// 创建目录联接 linkDir → targetDir(不需要管理员权限)。
    /// linkDir 必须是空目录或尚不存在;targetDir 不存在会自动创建。
    /// 若 linkDir 已是联接且指向正确目标,直接返回 true。
    /// </summary>
    public static bool CreateJunction(string linkDir, string targetDir)
    {
        try
        {
            targetDir = Path.GetFullPath(targetDir);
            Directory.CreateDirectory(targetDir);

            if (Directory.Exists(linkDir))
            {
                if (IsJunction(linkDir))
                {
                    // 已是联接:目标一致则无需重建
                    var existing = GetJunctionTarget(linkDir);
                    if (!string.IsNullOrEmpty(existing) &&
                        Path.GetFullPath(existing).Equals(targetDir, StringComparison.OrdinalIgnoreCase))
                        return true;
                    // 指向错误:删除联接本身(不影响目标数据)重建
                    Directory.Delete(linkDir);
                }
                else
                {
                    // 普通目录:必须先由调用方迁空,这里不动数据
                    if (Directory.EnumerateFileSystemEntries(linkDir).Any())
                        return false;
                    Directory.Delete(linkDir);
                }
            }
            Directory.CreateDirectory(linkDir);

            string substituteName = @"\??\" + targetDir;
            string printName = targetDir;
            byte[] subBytes = System.Text.Encoding.Unicode.GetBytes(substituteName);
            byte[] printBytes = System.Text.Encoding.Unicode.GetBytes(printName);

            // 手工构造精确长度的 REPARSE_DATA_BUFFER:
            // 头 8 字节(ReparseTag 4 + ReparseDataLength 2 + Reserved 2)
            // + MountPointReparseBuffer 8 字节(四个 ushort 偏移/长度)
            // + PathBuffer 实际内容(SubstituteName 与 PrintName 均需各自的 UTF-16 空终止符)。
            // FSCTL_SET_REPARSE_POINT 要求输入缓冲区大小与 ReparseDataLength 严格一致,
            // 否则报 4392(ERROR_INVALID_REPARSE_DATA)。
            ushort reparseDataLength = (ushort)(8 + subBytes.Length + 2 + printBytes.Length + 2);
            int totalSize = 8 + reparseDataLength;
            byte[] raw = new byte[totalSize];
            void WriteU32(int offset, uint v)
            {
                raw[offset] = (byte)(v & 0xFF);
                raw[offset + 1] = (byte)((v >> 8) & 0xFF);
                raw[offset + 2] = (byte)((v >> 16) & 0xFF);
                raw[offset + 3] = (byte)((v >> 24) & 0xFF);
            }
            void WriteU16(int offset, ushort v)
            {
                raw[offset] = (byte)(v & 0xFF);
                raw[offset + 1] = (byte)((v >> 8) & 0xFF);
            }
            WriteU32(0, IO_REPARSE_TAG_MOUNT_POINT);          // ReparseTag
            WriteU16(4, reparseDataLength);                    // ReparseDataLength
            WriteU16(6, 0);                                    // Reserved
            WriteU16(8, 0);                                    // SubstituteNameOffset
            WriteU16(10, (ushort)subBytes.Length);             // SubstituteNameLength
            WriteU16(12, (ushort)(subBytes.Length + 2));       // PrintNameOffset
            WriteU16(14, (ushort)printBytes.Length);           // PrintNameLength
            Array.Copy(subBytes, 0, raw, 16, subBytes.Length);
            // subBytes 后留 2 字节空终止符,再写 PrintName,末尾再留 2 字节空终止符
            // (byte[] 初始全零,终止符天然就位,只需长度计入)
            Array.Copy(printBytes, 0, raw, 16 + subBytes.Length + 2, printBytes.Length);

            using var handle = CreateFile(linkDir, GENERIC_WRITE,
                FileShare.None, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                App.WriteAppLog($"[联接] CreateFile 失败:{Marshal.GetLastWin32Error()} {linkDir}");
                return false;
            }

            IntPtr inBuffer = Marshal.AllocHGlobal(totalSize);
            try
            {
                Marshal.Copy(raw, 0, inBuffer, totalSize);
                bool ok = DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT,
                    inBuffer, totalSize, IntPtr.Zero, 0, out _, IntPtr.Zero);
                if (!ok)
                {
                    App.WriteAppLog($"[联接] DeviceIoControl 失败:{Marshal.GetLastWin32Error()} {linkDir} → {targetDir}");
                    return false;
                }
                App.WriteAppLog($"[联接] 创建成功:{linkDir} → {targetDir}");
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(inBuffer);
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[联接] 创建异常 {linkDir} → {targetDir}:{ex.Message}");
            return false;
        }
    }

    /// <summary>读取联接目标路径(非联接或失败返回空)</summary>
    public static string GetJunctionTarget(string linkDir)
    {
        try
        {
            if (!Directory.Exists(linkDir)) return "";
            using var handle = CreateFile(linkDir, GENERIC_READ,
                FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid) return "";

            IntPtr outBuffer = Marshal.AllocHGlobal(MAXIMUM_REPARSE_DATA_BUFFER_SIZE);
            try
            {
                if (!DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT,
                        IntPtr.Zero, 0, outBuffer, MAXIMUM_REPARSE_DATA_BUFFER_SIZE,
                        out int returned, IntPtr.Zero))
                    return "";

                var buffer = Marshal.PtrToStructure<REPARSE_DATA_BUFFER>(outBuffer);
                if (buffer.ReparseTag != IO_REPARSE_TAG_MOUNT_POINT) return "";
                string sub = System.Text.Encoding.Unicode.GetString(
                    buffer.PathBuffer, buffer.SubstituteNameOffset, buffer.SubstituteNameLength);
                if (sub.StartsWith(@"\??\", StringComparison.Ordinal)) sub = sub[4..];
                return sub;
            }
            finally
            {
                Marshal.FreeHGlobal(outBuffer);
            }
        }
        catch { return ""; }
    }

    /// <summary>
    /// 删除联接本身(只删链接,不删除目标目录内的数据)。
    /// 普通目录时不做任何操作(返回 false),防止误删实体数据。
    /// </summary>
    public static bool DeleteJunctionOnly(string linkDir)
    {
        try
        {
            if (!Directory.Exists(linkDir) || !IsJunction(linkDir)) return false;
            Directory.Delete(linkDir); // 对 reparse point,Directory.Delete 只删链接
            return true;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[联接] 删除失败 {linkDir}:{ex.Message}");
            return false;
        }
    }
}
