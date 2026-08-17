// AppPaths.cs — 数据目录中心(统一固化路径)
// 可爱的芙芙
//
// 全部用户数据、缓存、下载资源、配置、版本库、Java 隔离运行时统一限定在
// 程序同级目录 APP\mcGAME,不再向 C 盘用户目录/AppData 写入任何业务数据。
// 程序运行库(exe/.NET dll)只存在于 Start 根目录,绝不混入 APP\mcGAME;
// 游戏业务数据绝不跑到 Start 根目录。
//
// 目录结构(严格遵守,不新增任何多余目录):
//   APP\mcGAME
//   ├─ versions     MC 各版本游戏本体(版本 json/jar,全实例共享)
//   ├─ runtimes     自动下载的 Java 运行库(Java 选择器读取此目录)
//   ├─ mods         全部模组文件(按实例分子目录统一管理)
//   ├─ instances    游戏实例/整合包(每实例一个游戏工作目录)
//   ├─ saves        全部游戏存档(按实例分子目录)
//   ├─ accounts     账号数据(微软登录/离线账号,与游戏数据完全隔离)
//   ├─ libraries    Minecraft 游戏依赖库文件
//   ├─ assets       游戏资源文件(音乐/贴图/语言文件等)
//   ├─ installers   游戏安装包临时存放目录
//   ├─ cache        全部游戏缓存、网络请求缓存
//   ├─ 日志         启动器运行日志、游戏输出日志统一输出到此
//   ├─ tupian       程序 logo、用户上传的背景图片/背景视频素材
//   └─ config.json  启动器配置文件(根级,不单独建目录)
//
// 旧结构数据(GameVersions/IsolatedJava/Logs/Config 等)在首次启动时自动迁移,
// 迁移失败弹窗告知,不静默丢弃。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace FufuLauncher.Services;

public static class AppPaths
{
    /// <summary>数据根目录:{exe所在目录}\APP\mcGAME</summary>
    public static string Root { get; private set; } = string.Empty;

    // ===== 12 个规范子目录(严格固定,禁止新增/改名)=====
    /// <summary>MC 各版本游戏本体(全实例共享)</summary>
    public static string Versions => Path.Combine(Root, "versions");
    /// <summary>自动下载的 Java 运行库,Java 选择器读取此目录</summary>
    public static string Runtimes => Path.Combine(Root, "runtimes");
    /// <summary>全部模组文件(按实例分子目录统一管理)</summary>
    public static string Mods => Path.Combine(Root, "mods");
    /// <summary>游戏实例、整合包存放目录</summary>
    public static string Instances => Path.Combine(Root, "instances");
    /// <summary>全部游戏存档(按实例分子目录)</summary>
    public static string Saves => Path.Combine(Root, "saves");
    /// <summary>账号数据(微软登录/离线账号)</summary>
    public static string Accounts => Path.Combine(Root, "accounts");
    /// <summary>Minecraft 游戏依赖库文件</summary>
    public static string Libraries => Path.Combine(Root, "libraries");
    /// <summary>游戏资源文件(音乐、贴图、语言文件等)</summary>
    public static string Assets => Path.Combine(Root, "assets");
    /// <summary>游戏安装包临时存放目录</summary>
    public static string Installers => Path.Combine(Root, "installers");
    /// <summary>全部游戏缓存、网络请求缓存</summary>
    public static string Cache => Path.Combine(Root, "cache");
    /// <summary>日志目录:启动器运行日志、游戏输出日志统一输出到此</summary>
    public static string Logs => Path.Combine(Root, "日志");
    /// <summary>程序 logo、用户上传的背景图片、背景视频素材</summary>
    public static string Images => Path.Combine(Root, "tupian");

    // ===== 文件级路径 =====
    /// <summary>启动器配置文件(根级)</summary>
    public static string AppConfigFile => Path.Combine(Root, "config.json");
    /// <summary>应用日志文件</summary>
    public static string AppLogFile => Path.Combine(Logs, "app.log");
    /// <summary>游戏输出日志文件</summary>
    public static string GameLogFile => Path.Combine(Logs, "game.log");

    // ===== 兼容旧属性名(内部全部已指向新规范目录)=====
    /// <summary>兼容旧名:实例目录 = instances</summary>
    public static string GameVersions => Instances;
    /// <summary>兼容旧名:Java 运行时目录 = runtimes</summary>
    public static string IsolatedJava => Runtimes;

    /// <summary>旧版数据目录名(同 exe 目录)</summary>
    private const string LegacyDirName = "appmcGAME";

    /// <summary>初始化结果描述(迁移/错误信息,供启动日志)</summary>
    public static List<string> InitNotes { get; } = new();

    /// <summary>
    /// 初始化数据根目录:定位 → 迁移旧数据 → 创建全套子目录 → 可写性校验。
    /// 失败返回 false 并由调用方弹窗,绝不回退到 C 盘。
    /// </summary>
    public static bool Initialize(out string error)
    {
        error = "";
        try
        {
            // 单文件发布兼容:AppContext.BaseDirectory 在单文件模式下指向临时自解压目录,
            // 必须用 Environment.ProcessPath 取 exe 真实所在目录,否则数据目录会建到 TEMP
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            exeDir = Path.GetFullPath(exeDir).TrimEnd('\\', '/');
            // 兼容旧自包含布局:exe 位于 APP\MCGAME\.NET8\ 时,根为其父目录
            string parent = Path.GetDirectoryName(exeDir) ?? exeDir;
            if (Path.GetFileName(parent).Equals("MCGAME", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(Path.GetDirectoryName(parent) ?? "").Equals("APP", StringComparison.OrdinalIgnoreCase))
            {
                Root = parent;
            }
            else
            {
                Root = Path.Combine(exeDir, "APP", "mcGAME");
            }

            // 旧 appmcGAME 数据迁移(同盘 Directory.Move 瞬时完成)
            MigrateLegacy(exeDir, parent);

            // 旧目录名 → 新规范目录名 迁移(GameVersions→instances 等)
            MigrateOldSubDirs();

            // 创建全套规范子目录(严格 12 个,不多不少)
            foreach (var dir in new[] { Root, Versions, Runtimes, Mods, Instances, Saves, Accounts,
                                        Libraries, Assets, Installers, Cache, Logs, Images })
                Directory.CreateDirectory(dir);

            // 可写性校验:写临时文件验证
            string probe = Path.Combine(Root, ".write_probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            // 磁盘剩余空间提示(低于 1GB 警告,但不阻止运行)
            try
            {
                string? driveRoot = Path.GetPathRoot(Path.GetFullPath(Root));
                if (!string.IsNullOrEmpty(driveRoot))
                {
                    var drive = new DriveInfo(driveRoot);
                    if (drive.IsReady && drive.AvailableFreeSpace < 1024L * 1024 * 1024)
                        InitNotes.Add($"警告:磁盘 {drive.Name} 剩余空间不足 1GB,游戏安装可能失败。");
                }
            }
            catch { }

            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"程序目录无写入权限,无法初始化数据目录:\n{Root}\n\n" +
                    "请以管理员身份运行,或将程序移动到可写目录。\n" +
                    "(不会自动迁移到 C 盘)\n\n" + ex.Message;
            return false;
        }
        catch (IOException ex)
        {
            error = $"数据目录初始化失败(磁盘空间不足或文件被占用):\n{Root}\n\n" +
                    "请检查磁盘剩余空间,关闭可能占用文件的程序后重试。\n" +
                    "(不会自动迁移到 C 盘)\n\n" + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = $"数据目录初始化异常:\n{ex.Message}";
            return false;
        }
    }

    // ==================== 旧结构迁移 ====================

    /// <summary>
    /// 旧子目录名 → 新规范目录名 迁移:
    /// GameVersions→instances、IsolatedJava→runtimes、Logs→日志、
    /// Config\accounts→accounts、Config\config.json→config.json(随后删除空 Config),
    /// 并修正 Mods/Cache 等目录的大小写为规范小写。
    /// </summary>
    private static void MigrateOldSubDirs()
    {
        // 目录级迁移(旧名 → 新目录完整路径)
        var renameMap = new (string OldName, string NewPath)[]
        {
            ("GameVersions", Instances),
            ("IsolatedJava", Runtimes),
            ("Logs", Logs),
        };
        foreach (var (oldName, newPath) in renameMap)
        {
            string oldPath = Path.Combine(Root, oldName);
            MoveDir(oldPath, newPath, oldName);
        }

        // Config 目录拆解:accounts → Root\accounts,config.json → Root\config.json,其余并入 Root
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
                    MoveDir(sub, dst, $"Config/{name}");
                }
                foreach (var file in Directory.GetFiles(configDir))
                {
                    string name = Path.GetFileName(file);
                    MergeSingleFile(file, Path.Combine(Root, name), $"Config/{name}");
                }
                if (!Directory.EnumerateFileSystemEntries(configDir).Any())
                    Directory.Delete(configDir);
                else
                    InitNotes.Add("迁移:Config 目录仍有残留内容,未删除,请手动检查");
            }
            catch (Exception ex) { InitNotes.Add($"迁移:Config 目录拆解部分失败:{ex.Message}"); }
        }

        // 旧非单文件布局遗留的 dll 运行库目录:单文件发布后不再需要,
        // 且约束要求程序运行库不得混入 mcGAME,仅清理该目录内 *.dll 后删除空目录
        string legacyDllDir = Path.Combine(Root, "dll");
        if (Directory.Exists(legacyDllDir))
        {
            try
            {
                foreach (var f in Directory.GetFiles(legacyDllDir, "*.dll"))
                    File.Delete(f);
                if (!Directory.EnumerateFileSystemEntries(legacyDllDir).Any())
                {
                    Directory.Delete(legacyDllDir);
                    InitNotes.Add("迁移:已清理旧布局遗留的 dll/ 运行库目录");
                }
                else
                    InitNotes.Add("迁移:dll/ 目录仍有非 dll 残留,未删除,请手动检查");
            }
            catch (Exception ex) { InitNotes.Add($"迁移:dll/ 目录清理失败:{ex.Message}"); }
        }

        // 大小写修正:Mods→mods、Cache→cache、saves 等(NTFS 不区分大小写,需两段改名)
        foreach (var name in new[] { "mods", "cache", "versions", "assets", "libraries",
                                     "saves", "installers", "accounts", "instances", "runtimes", "tupian" })
        {
            FixCase(Path.Combine(Root, name), name);
        }
    }

    /// <summary>移动目录(同盘瞬时);目标已存在时跳过并记录,不覆盖不丢数据</summary>
    private static void MoveDir(string src, string dst, string label)
    {
        if (!Directory.Exists(src) || src.Equals(dst, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (Directory.Exists(dst))
            {
                // 目标已存在:深度合并全部子项(不覆盖同名文件,防止嵌套子目录如 accounts/*.json 残留)
                MergeDirDeep(src, dst, label);
                if (!Directory.EnumerateFileSystemEntries(src).Any()) Directory.Delete(src);
                InitNotes.Add($"迁移:{label} → {dst}(合并)");
                return;
            }
            Directory.Move(src, dst);
            InitNotes.Add($"迁移:{label} → {dst}");
        }
        catch (Exception ex) { InitNotes.Add($"迁移:{label} 失败:{ex.Message}"); }
    }

    /// <summary>深度合并目录:递归把 src 全部内容并入 dst。
    /// 同名文件:内容相同则删源;否则保留修改时间较新的一份为主文件,较旧的改名 .bak 不丢数据</summary>
    private static void MergeDirDeep(string src, string dst, string label)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
        {
            string name = Path.GetFileName(file);
            string fileDst = Path.Combine(dst, name);
            if (!File.Exists(fileDst))
            {
                File.Move(file, fileDst);
                continue;
            }
            MergeSingleFile(file, fileDst, $"{label}/{name}");
        }
        foreach (var sub in Directory.GetDirectories(src))
        {
            string name = Path.GetFileName(sub);
            MergeDirDeep(sub, Path.Combine(dst, name), $"{label}/{name}");
            if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
        }
    }

    /// <summary>合并单个同名冲突文件:内容相同删源;否则较新者为主文件,较旧者改名 .bak 不丢数据</summary>
    private static void MergeSingleFile(string srcFile, string dstFile, string label)
    {
        try
        {
            var srcInfo = new FileInfo(srcFile);
            var dstInfo = new FileInfo(dstFile);
            if (SameContent(srcFile, dstFile))
            {
                File.Delete(srcFile);
                return;
            }
            if (srcInfo.LastWriteTime >= dstInfo.LastWriteTime)
            {
                // 旧目录里的文件更新(旧版本程序最后写入的位置),以它为准,原目标备份
                File.Move(dstFile, dstFile + ".bak", overwrite: true);
                File.Move(srcFile, dstFile);
                InitNotes.Add($"迁移:{label} 内容冲突,已采用较新版本,原文件备份为 .bak");
            }
            else
            {
                File.Move(srcFile, srcFile + ".bak", overwrite: true);
                InitNotes.Add($"迁移:{label} 内容冲突,目标已是较新版本,源文件备份为 .bak");
            }
        }
        catch (Exception ex) { InitNotes.Add($"迁移:{label} 合并失败,源文件保留:{ex.Message}"); }
    }

    /// <summary>比较两个文件内容是否一致(长度不同直接判否,长度相同逐字节比较)</summary>
    private static bool SameContent(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            using var sa = fa.OpenRead();
            using var sb = fb.OpenRead();
            var bufA = new byte[8192];
            var bufB = new byte[8192];
            int read;
            while ((read = sa.Read(bufA, 0, bufA.Length)) > 0)
            {
                int readB = 0;
                while (readB < read)
                {
                    int n = sb.Read(bufB, readB, read - readB);
                    if (n <= 0) return false;
                    readB += n;
                }
                for (int i = 0; i < read; i++)
                    if (bufA[i] != bufB[i]) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>NTFS 大小写修正:目录存在但磁盘显示名与规范不一致时两段改名</summary>
    private static void FixCase(string path, string exactName)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            string? parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return;
            foreach (var d in Directory.GetDirectories(parent))
            {
                if (d.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(d) != exactName)
                {
                    string tmp = Path.Combine(parent, exactName + "_case_tmp_");
                    Directory.Move(d, tmp);
                    Directory.Move(tmp, path);
                }
            }
        }
        catch { /* 大小写修正失败不影响功能(NTFS 不区分大小写) */ }
    }

    /// <summary>旧版 appmcGAME 目录 → APP\mcGAME 结构迁移(子目录改名映射)</summary>
    private static void MigrateLegacy(string exeDir, string parentDir)
    {
        // 候选旧目录:exe 同级 appmcGAME,或自包含发布时父目录 appmcGAME
        string? legacy = null;
        foreach (var cand in new[] { Path.Combine(exeDir, LegacyDirName), Path.Combine(parentDir, LegacyDirName) })
        {
            if (Directory.Exists(cand) && !cand.Equals(Root, StringComparison.OrdinalIgnoreCase))
            { legacy = cand; break; }
        }
        // 自包含发布旧布局:exe 在 appmcGAME\.NET8 内,父目录即旧数据根
        if (legacy == null &&
            Path.GetFileName(parentDir).Equals(LegacyDirName, StringComparison.OrdinalIgnoreCase))
            legacy = parentDir;
        if (legacy == null || Root.Equals(legacy, StringComparison.OrdinalIgnoreCase)) return;

        // 旧名 → 新目录 映射(未列出的按原名搬入 Root,随后由 MigrateOldSubDirs 二次归位)
        var renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["instances"] = Instances,
            ["runtimes"] = Runtimes,
            ["mods"] = Mods,
            ["cache"] = Cache,
            ["logs"] = Logs,
        };

        try
        {
            Directory.CreateDirectory(Root);
            foreach (var sub in Directory.GetDirectories(legacy))
            {
                string name = Path.GetFileName(sub);
                string dst = renameMap.TryGetValue(name, out var mapped) ? mapped : Path.Combine(Root, name);
                if (Directory.Exists(dst))
                {
                    InitNotes.Add($"迁移:{name}/ 目标已存在,跳过(旧文件保留于 {legacy})");
                    continue;
                }
                Directory.Move(sub, dst);
                InitNotes.Add($"迁移:{name}/ → {dst}");
            }
            // 根级散落文件:config.json → Root\config.json,*.log → 日志\,其余入 Root
            foreach (var file in Directory.GetFiles(legacy))
            {
                string name = Path.GetFileName(file);
                string dst = name.Equals("config.json", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(Root, name)
                    : name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Logs, name)
                        : Path.Combine(Root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!File.Exists(dst)) File.Move(file, dst);
            }
            InitNotes.Add("旧版 appmcGAME 数据迁移完成。");
        }
        catch (Exception ex)
        {
            InitNotes.Add($"旧数据迁移部分失败:{ex.Message}");
            // 迁移失败不阻断启动,但必须告知用户(由调用方弹窗)
            MessageBox.Show(
                $"旧版数据目录迁移未完成:\n{legacy} → {Root}\n\n{ex.Message}\n\n" +
                "程序将继续启动,旧数据仍保留在原目录,请手动检查。",
                "可爱的芙芙 - 数据迁移提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
