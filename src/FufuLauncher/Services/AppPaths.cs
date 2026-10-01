// FufuLauncher - 数据目录中心(全量重构版)
// Copyright © FufuLauncher
//
// 全部用户数据固化在程序同级目录 APP\MCGAME,绝不写 C 盘用户目录。
// 目录规范(固定 12 个子目录,不增不减):
//   versions   游戏本体(版本 json/jar,全版本共享)
//   runtimes   自动下载的 Java 运行时
//   mods       模组文件(按游戏版本分子目录)
//   instances  游戏版本工作目录(每版本一份配置与数据)
//   saves      存档(按游戏版本分子目录)
//   accounts   账号数据(与游戏数据隔离)
//   libraries  游戏依赖库
//   assets     游戏资源(贴图/声音/语言)
//   installers 安装包临时目录
//   cache      缓存
//   日志       启动器与游戏日志
//   tupian     logo/背景素材(jm 主题,tub 图标)
// 根级另有 config.json 配置文件。
//
// 初始化流程:定位根 → 迁移旧结构 → 建目录 → 可写性探测 → 磁盘余量提示。
// 任何失败都返回友好错误文案由调用方弹窗,不静默崩溃、不回退 C 盘。

using System.IO;

namespace FufuLauncher.Services;

public static class AppPaths
{
    /// <summary>数据根目录:{exe所在目录}\APP\MCGAME(兼容任意大小写的既有目录)</summary>
    public static string Root { get; private set; } = string.Empty;

    // ===== 12 个规范子目录 =====
    public static string Versions => Path.Combine(Root, "versions");
    public static string Runtimes => Path.Combine(Root, "runtimes");
    public static string Mods => Path.Combine(Root, "mods");
    public static string Instances => Path.Combine(Root, "instances");
    public static string Saves => Path.Combine(Root, "saves");
    public static string Accounts => Path.Combine(Root, "accounts");
    public static string Libraries => Path.Combine(Root, "libraries");
    public static string Assets => Path.Combine(Root, "assets");
    public static string Installers => Path.Combine(Root, "installers");
    public static string Cache => Path.Combine(Root, "cache");
    public static string Logs => Path.Combine(Root, "日志");
    public static string Images => Path.Combine(Root, "tupian");

    // ===== 文件级路径 =====
    public static string AppConfigFile => Path.Combine(Root, "config.json");
    public static string AppLogFile => Path.Combine(Logs, "app.log");
    public static string GameLogFile => Path.Combine(Logs, "game.log");
    /// <summary>下载队列落盘文件(2026-09-27 后台/重启续传):未完成任务 + 断点(分片段位)</summary>
    public static string DownloadQueueFile => Path.Combine(Root, "下载队列.json");

    // ===== 兼容旧属性名 =====
    public static string GameVersions => Instances;
    public static string IsolatedJava => Runtimes;

    /// <summary>旧版数据目录名(与 exe 同级,首启自动迁移)</summary>
    private const string LegacyDirName = "appmcGAME";

    /// <summary>初始化过程说明(迁移记录/警告,供启动日志播报)</summary>
    public static List<string> InitNotes { get; } = new();

    /// <summary>初始化数据根。失败返回 false,error 为可直接展示给用户的文案</summary>
    public static bool Initialize(out string error)
    {
        error = "";
        try
        {
            Root = ResolveRoot();
            MigrateLegacyLayout(Path.GetDirectoryName(Root)!);
            MigrateRenamedSubDirs();
            CreateStandardDirs();
            ProbeWritable();
            NoteIfLowDiskSpace();
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"程序目录没有写入权限,无法初始化数据目录:\n{Root}\n\n" +
                    "请以管理员身份运行,或把程序移到可写目录。\n\n" + ex.Message;
            return false;
        }
        catch (IOException ex)
        {
            error = $"数据目录初始化失败(磁盘空间不足或文件被占用):\n{Root}\n\n" +
                    "请检查磁盘剩余空间,关闭可能占用文件的程序后重试。\n\n" + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = "数据目录初始化异常:\n" + ex.Message;
            return false;
        }
    }

    // ==================== 初始化步骤 ====================

    /// <summary>定位部署根:取 exe 真实目录;程序收在子文件夹(如 Start\Program\)且上级有 APP\ 时回退上级。
    /// 互斥体/入口壳等需要与数据根同源的位置标识时用本方法,与 ResolveRoot 的回退规则保持一致(见 8.16/8.17)</summary>
    public static string ResolveDeployRoot()
    {
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        exeDir = Path.GetFullPath(exeDir).TrimEnd('\\', '/');

        string? parent = Path.GetDirectoryName(exeDir);

        // 程序子文件夹布局(见 8.16):触发条件严格——
        // exe 目录自身无 APP\ 且上级已有 APP\;旧布局(exe 直接放部署根)不受影响。
        if (parent != null
            && !Directory.Exists(Path.Combine(exeDir, "APP"))
            && Directory.Exists(Path.Combine(parent, "APP")))
            return parent;

        return exeDir;
    }

    /// <summary>定位数据根:取 exe 真实目录;exe 已在 APP\MCGAME 内时就地;
    /// 程序收在子文件夹(如 Start\Program\)时回退上级找同级 APP\(见 8.16)</summary>
    private static string ResolveRoot()
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

        // 程序子文件夹布局(见 8.16):程序收在独立子文件夹(如 Start\Program\)、数据 APP\ 与它平级。
        // 触发条件严格:exe 目录自身无 APP\ 且上级已有 APP\——
        // 旧布局(exe 直接放部署根)不受影响;全新部署随包自带 APP\ 也命中。
        if (parent != null
            && !Directory.Exists(Path.Combine(exeDir, "APP"))
            && Directory.Exists(Path.Combine(parent, "APP")))
            exeDir = parent;

        // 优先复用既有数据目录的真实大小写(MCGAME/mcGAME/…):
        // 硬编码异写名在启用大小写敏感的卷上会找不到文件、还会裂变出重名目录
        string appDir = Path.Combine(exeDir, "APP");
        if (Directory.Exists(appDir))
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(appDir))
                    if (Path.GetFileName(sub).Equals("MCGAME", StringComparison.OrdinalIgnoreCase))
                        return sub;
            }
            catch { /* 枚举失败走新建分支 */ }
        }
        return Path.Combine(appDir, "MCGAME");
    }

    /// <summary>旧 appmcGAME 目录整体迁移(同盘 Move 瞬时完成,冲突跳过不覆盖)</summary>
    private static void MigrateLegacyLayout(string exeDir)
    {
        string? legacy = null;
        foreach (var cand in new[] { Path.Combine(exeDir, LegacyDirName), Path.Combine(Root, "..", LegacyDirName) })
        {
            string full = Path.GetFullPath(cand);
            if (Directory.Exists(full) && !full.Equals(Root, StringComparison.OrdinalIgnoreCase))
            { legacy = full; break; }
        }
        if (legacy == null) return;

        try
        {
            Directory.CreateDirectory(Root);
            foreach (var sub in Directory.GetDirectories(legacy))
            {
                string dst = Path.Combine(Root, Path.GetFileName(sub));
                if (Directory.Exists(dst)) { InitNotes.Add($"迁移:{Path.GetFileName(sub)}/ 目标已存在,跳过"); continue; }
                Directory.Move(sub, dst);
                InitNotes.Add($"迁移:{Path.GetFileName(sub)}/ → {dst}");
            }
            foreach (var file in Directory.GetFiles(legacy))
            {
                string name = Path.GetFileName(file);
                string dst = name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(Logs, name) : Path.Combine(Root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!File.Exists(dst)) File.Move(file, dst);
            }
            InitNotes.Add("旧版 appmcGAME 数据迁移完成。");
        }
        catch (Exception ex)
        {
            InitNotes.Add($"旧数据迁移部分失败(数据保留在原目录):{ex.Message}");
        }
    }

    /// <summary>旧子目录名归位:GameVersions→instances、IsolatedJava→runtimes、Logs→日志、Config 拆解</summary>
    private static void MigrateRenamedSubDirs()
    {
        RenameInto(Path.Combine(Root, "GameVersions"), Instances, "GameVersions");
        RenameInto(Path.Combine(Root, "IsolatedJava"), Runtimes, "IsolatedJava");
        RenameInto(Path.Combine(Root, "Logs"), Logs, "Logs");

        // Config 目录拆解:accounts 独立、config.json 提到根级、其余并入根
        string configDir = Path.Combine(Root, "Config");
        if (Directory.Exists(configDir))
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(configDir))
                {
                    string name = Path.GetFileName(sub);
                    string dst = name.Equals("accounts", StringComparison.OrdinalIgnoreCase)
                        ? Accounts : Path.Combine(Root, name);
                    RenameInto(sub, dst, $"Config/{name}");
                }
                foreach (var file in Directory.GetFiles(configDir))
                {
                    string dst = Path.Combine(Root, Path.GetFileName(file));
                    if (!File.Exists(dst)) File.Move(file, dst);
                }
                if (!Directory.EnumerateFileSystemEntries(configDir).Any()) Directory.Delete(configDir);
            }
            catch (Exception ex) { InitNotes.Add($"迁移:Config 目录拆解失败:{ex.Message}"); }
        }
    }

    /// <summary>目录改名/并入:目标已存在则跳过(不覆盖用户新数据)</summary>
    private static void RenameInto(string src, string dst, string label)
    {
        if (!Directory.Exists(src) || src.Equals(dst, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (Directory.Exists(dst)) { InitNotes.Add($"迁移:{label} 目标已存在,跳过"); return; }
            Directory.Move(src, dst);
            InitNotes.Add($"迁移:{label} → {dst}");
        }
        catch (Exception ex) { InitNotes.Add($"迁移:{label} 失败:{ex.Message}"); }
    }

    private static void CreateStandardDirs()
    {
        foreach (var dir in new[] { Root, Versions, Runtimes, Mods, Instances, Saves, Accounts,
                                    Libraries, Assets, Installers, Cache, Logs, Images })
            Directory.CreateDirectory(dir);
    }

    /// <summary>可写性探测:写删临时文件,失败直接抛由外层转友好文案</summary>
    private static void ProbeWritable()
    {
        string probe = Path.Combine(Root, ".write_probe");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
    }

    /// <summary>磁盘余量提示(低于 1GB 警告,不阻断启动)</summary>
    private static void NoteIfLowDiskSpace()
    {
        try
        {
            string? driveRoot = Path.GetPathRoot(Path.GetFullPath(Root));
            if (string.IsNullOrEmpty(driveRoot)) return;
            var drive = new DriveInfo(driveRoot);
            if (drive.IsReady && drive.AvailableFreeSpace < 1024L * 1024 * 1024)
                InitNotes.Add($"警告:磁盘 {drive.Name} 剩余空间不足 1GB,游戏安装可能失败。");
        }
        catch { /* 余量检查失败不影响启动 */ }
    }
}
