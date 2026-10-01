// FufuLauncher - 便携路径中心(全量重构版)
// Copyright © FufuLauncher
//
// 职责:解析并固化全部数据路径。数据根 = {exe目录}\APP\mcGAME;
// 用 Environment.ProcessPath 取真实 exe 目录;程序收在子文件夹(如 Start\Program\)
// 且数据与它平级时回退上级定位(与 AppPaths 一致,见 8.16)。
// 纯静态只读,不依赖任何服务,任何层都可安全引用。

using System.IO;

namespace FufuLauncher.Next.Foundation;

public static class NextPaths
{
    /// <summary>数据根:{exe目录}\APP\mcGAME(exe 已位于 APP\mcGAME 内时直接取该目录)</summary>
    public static string Root { get; }

    public static string Versions { get; }
    public static string Runtimes { get; }
    public static string Mods { get; }
    public static string Instances { get; }
    public static string Accounts { get; }
    public static string Libraries { get; }
    public static string Assets { get; }
    public static string Cache { get; }
    public static string Logs { get; }

    static NextPaths()
    {
        Root = ResolveDataRoot();

        Versions = Path.Combine(Root, "versions");
        Runtimes = Path.Combine(Root, "runtimes");
        Mods = Path.Combine(Root, "mods");
        Instances = Path.Combine(Root, "instances");
        Accounts = Path.Combine(Root, "accounts");
        Libraries = Path.Combine(Root, "libraries");
        Assets = Path.Combine(Root, "assets");
        Cache = Path.Combine(Root, "cache");
        Logs = Path.Combine(Root, "日志");
    }

    /// <summary>定位数据根:exe 已在 APP\mcGAME 内 → 就地;程序在子文件夹且数据平级 → 上级;
    /// 否则在 exe 目录下新建约定路径(与 AppPaths.ResolveRoot 保持同步)</summary>
    private static string ResolveDataRoot()
    {
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        exeDir = Path.GetFullPath(exeDir).TrimEnd('\\', '/');

        string? parent = Path.GetDirectoryName(exeDir);
        string? grand = parent == null ? null : Path.GetDirectoryName(parent);
        bool insideLayout = parent != null
            && Path.GetFileName(parent).Equals("MCGAME", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(grand)
            && Path.GetFileName(grand).Equals("APP", StringComparison.OrdinalIgnoreCase);

        if (insideLayout) return parent!;

        // 程序子文件夹布局(见 8.16):触发条件与 AppPaths 严格一致——
        // exe 目录自身无 APP\ 且上级已有 APP\,以上级为部署根。
        if (parent != null
            && !Directory.Exists(Path.Combine(exeDir, "APP"))
            && Directory.Exists(Path.Combine(parent, "APP")))
            exeDir = parent;

        // 复用既有数据目录的真实大小写(与 AppPaths 一致,避免大小写敏感卷上裂变重名目录)
        string appDir = Path.Combine(exeDir, "APP");
        try
        {
            foreach (var sub in Directory.GetDirectories(appDir))
                if (Path.GetFileName(sub).Equals("MCGAME", StringComparison.OrdinalIgnoreCase))
                    return sub;
        }
        catch { /* 枚举失败走新建分支 */ }
        return Path.Combine(appDir, "mcGAME");
    }

    /// <summary>确保目录存在(幂等;磁盘只读等极端情况返回 false 不抛)</summary>
    public static bool EnsureDir(string dir)
    {
        try { Directory.CreateDirectory(dir); return true; }
        catch { return false; }
    }

    /// <summary>zip 解压条目路径净化(zip-slip 防护):非法路径返回 null</summary>
    public static string? SanitizeEntryPath(string entryPath, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(entryPath)) return null;
        string rel = entryPath.Replace('\\', '/').TrimStart('/');
        if (rel.Contains("..") || Path.IsPathRooted(rel)) return null;

        string full = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar)));
        string baseFull = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
