// FufuLauncher - 配置中心(全量重构版)
// Copyright © FufuLauncher
//
// AppConfig:全部用户设置的单一模型(System.Text.Json 序列化,缺字段走默认值不炸)。
// ConfigService:加载(损坏回退默认) + 原子保存(tmp → Flush → Move,杜绝半截文件)。

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FufuLauncher.Next.Foundation;

namespace FufuLauncher.Services;

public class AppConfig
{
    // ===== 外观 =====
    public string Theme { get; set; } = "Light";
    /// <summary>背景类型:None / Image / Video(自定义背景特色功能)</summary>
    public string BackgroundType { get; set; } = "None";
    public string BackgroundPath { get; set; } = "";
    public double BackgroundOpacity { get; set; } = 0.6;
    public bool VideoMuted { get; set; } = true;
    public double VideoSpeed { get; set; } = 1.0;
    public int VideoFps { get; set; } = 30;
    public double VideoVolume { get; set; } = 0.0;
    public bool SidebarExpanded { get; set; } = false;

    // ===== 下载与网络 =====
    /// <summary>游戏下载源:Auto(智能自动,下载前测速选最快源) / BMCLAPI / Mojang(官方源,默认,最稳)
    /// / Custom(自有镜像,取 CustomDownloadBaseUrl)</summary>
    public string DownloadSource { get; set; } = "Mojang";
    /// <summary>自有镜像根地址(按 BMCLAPI 目录结构同步的自建源),仅在 DownloadSource=Custom 时生效;
    /// 只接受 https 绝对地址,连续失败自动退回 BMCLAPI,不会把下载链路卡死</summary>
    public string CustomDownloadBaseUrl { get; set; } = "";
    /// <summary>模组下载源:Modrinth / CurseForge / MCMod</summary>
    public string ModDownloadSource { get; set; } = "Modrinth";
    /// <summary>并发下载线程数(2026-08-28 默认提速至 60 且硬性下限 60:对齐主流多线程下载器档位,
    /// 资产/库等海量小文件场景收益显著;设置页可调 60~128)</summary>
    public int DownloadThreads { get; set; } = 60;
    /// <summary>大文件分片数上限(2026-09-26 可在下载设置调):≥4MB 的文件自动分片并发下载,
    /// 单文件分片数不超过该值且每片不小于 4MB;调大对超大文件(游戏本体/整合包)提速明显</summary>
    public int DownloadShardCount { get; set; } = 16;
    /// <summary>下载限速(MB/s),0 = 不限速</summary>
    public double DownloadRateLimitMbps { get; set; } = 0;
    /// <summary>下载完成后 SHA1 校验(失败自动重试)</summary>
    public bool VerifyAfterDownload { get; set; } = true;
    /// <summary>HTTP 代理地址(空 = 直连,如 http://127.0.0.1:7890)</summary>
    public string ProxyUrl { get; set; } = "";
    /// <summary>游戏核心 / 资源 / 库 / Java 下载是否走代理(BlockHelm-7:分类独立开关)</summary>
    public bool UseProxyForGame { get; set; } = true;
    /// <summary>模组 / 整合包下载是否走代理(BlockHelm-7:分类独立开关)</summary>
    public bool UseProxyForMod { get; set; } = true;
    /// <summary>CurseForge API Key(可选;导入 CF 整合包时自动补齐在线模组用,console.curseforge.com 免费注册获取)</summary>
    public string CurseForgeApiKey { get; set; } = "";

    // ===== 路径(自定义覆盖,空 = 默认 APP\mcGAME 布局) =====
    /// <summary>自定义游戏数据路径(versions/instances 所在根)</summary>
    public string CustomGamePath { get; set; } = "";
    /// <summary>自定义资源路径(assets)</summary>
    public string CustomAssetsPath { get; set; } = "";

    // ===== Java =====
    public string JavaPath { get; set; } = "";
    public int JavaVersion { get; set; } = 17;
    /// <summary>Java 下载镜像:Tsinghua(默认,国内最稳) / Huaweicloud / Official</summary>
    public string JavaDownloadMirror { get; set; } = "Tsinghua";
    public bool AutoScanJavaOnStartup { get; set; } = true;   // 2026-09-25:默认自动扫系统 Java,有 JDK 直接可用

    // ===== 游戏启动参数 =====
    public int Xms { get; set; } = 1024;
    public int Xmx { get; set; } = 4096;
    public string ExtraJvmArgs { get; set; } = "";
    public int GameWidth { get; set; } = 1280;
    public int GameHeight { get; set; } = 720;
    public bool Fullscreen { get; set; } = false;

    // ===== 启动前置检查与后台预加载 =====
    /// <summary>启动前自动校验环境完整性(BlockHelm-3:核心 jar / 库文件 / 资源文件)</summary>
    public bool VerifyBeforeLaunch { get; set; } = true;
    /// <summary>校验时逐个算 SHA1(深度模式,慢很多;关掉只查文件是否存在)</summary>
    public bool DeepVerifyBeforeLaunch { get; set; } = false;
    /// <summary>校验到损坏/缺失时自动补下载修复(BlockHelm-3)</summary>
    public bool AutoRepairBeforeLaunch { get; set; } = true;
    /// <summary>新建实例 / 安装加载器时后台静默预加载依赖(Axolotl-6)</summary>
    public bool PreloadDependencies { get; set; } = true;

    // ===== 内存智能分配 =====
    public bool AutoMemoryMode { get; set; } = true;
    /// <summary>内存档位:Vanilla / Modded / Shader</summary>
    public string MemoryTier { get; set; } = "Modded";
    /// <summary>堆页预提交(AlwaysPreTouch)</summary>
    public bool MemoryPreCommit { get; set; } = true;
    /// <summary>智能分配为系统预留的空闲内存(MB),下限 1536</summary>
    public int MemoryReserveMb { get; set; } = 1536;
    public int AutoMemoryMaxMb { get; set; } = 16384;
    public int AutoMemoryMinMb { get; set; } = 1024;
    /// <summary>堆外直接内存上限(MB);0 = 自动取 Xmx×0.25(夹在 256~2048)</summary>
    public int MaxDirectMemoryMb { get; set; } = 0;

    // ===== JVM 高级优化 =====
    public bool MultiCoreGcOptimize { get; set; } = false;
    public bool HighPriorityProcess { get; set; } = false;
    public bool CpuAffinityEnabled { get; set; } = false;

    // ===== 状态记忆 =====
    public string LastInstanceId { get; set; } = "";
    public string CurrentAccountUuid { get; set; } = "";
    // 安全审计修复(2026-08-28):原 DevPassword 硬编码后门密码字段已整体移除,
    // 旧 config.json 中残留的同名字段反序列化时自动忽略,不再写入。

    // ===== 日志清理 =====
    public bool LogAutoCleanEnabled { get; set; } = true;
    public int LogCleanEveryNLaunches { get; set; } = 3;
    public int LogKeepCount { get; set; } = 10;
    public int AppLaunchCount { get; set; } = 0;
    /// <summary>开发者详细日志开关(Debug 级是否落盘)</summary>
    public bool VerboseLog { get; set; } = false;

    // ===== 更新 =====
    public string UpdateManifestUrl { get; set; } = "";
    public bool AutoCheckUpdate { get; set; } = true;

    // ===== 其它 =====
    public bool SuppressEnvCheckAutoPrompt { get; set; } = false;

    // ===== 泡芙助理(AI) =====
    /// <summary>GPU 加速开关:默认开启,CUDA 优先、失败自动回退 CPU;关闭则强制 CPU 推理</summary>
    public bool AiGpuAcceleration { get; set; } = true;
    /// <summary>泡芙助理开关:处理 MC 任务(整合包构建/版本下载等自动化);关闭则不执行任何 MC 自动化。日常闲聊能力已移除,无需第二个开关</summary>
    public bool AiMcTasksEnabled { get; set; } = true;
    public bool WindowPlacementSaved { get; set; } = false;
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // ===== UI 布局偏好(纯界面显示,不参与任何业务判断) =====
    /// <summary>模组下载页热门卡片列数:3 或 4(默认 3;脏值一律按 3 处理)</summary>
    public int ModsCardColumns { get; set; } = 3;

    // ===== 旧配置遗留字段(读取时迁移后清空) =====
    [JsonPropertyName("BaseImagePath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? _oldBaseImagePath { get; set; }
    [JsonPropertyName("OverlayType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? _oldOverlayType { get; set; }
    [JsonPropertyName("OverlayPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? _oldOverlayPath { get; set; }
    [JsonPropertyName("OverlayOpacity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? _oldOverlayOpacity { get; set; }
}

public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true
    };

    private static string ConfigPath => AppPaths.AppConfigFile;

    /// <summary>当前生效配置(永不为 null)</summary>
    public AppConfig Config { get; private set; } = new();

    /// <summary>加载配置:文件缺失/JSON 损坏一律回退默认值,绝不抛出</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new AppConfig();
                MigrateLegacyFields();
            }
        }
        catch (Exception ex)
        {
            Config = new AppConfig();
            NextLog.Warn($"[配置] config.json 解析失败,已回退默认配置:{ex.Message}");
        }
    }

    /// <summary>旧字段迁移:Overlay* → Background*;旧预留内存默认值跟随新默认</summary>
    private void MigrateLegacyFields()
    {
        if (!string.IsNullOrEmpty(Config._oldOverlayPath) && string.IsNullOrEmpty(Config.BackgroundPath))
        {
            Config.BackgroundPath = Config._oldOverlayPath;
            Config.BackgroundType = string.IsNullOrEmpty(Config._oldOverlayType) ? "Image" : Config._oldOverlayType;
            if (Config._oldOverlayOpacity is > 0) Config.BackgroundOpacity = Config._oldOverlayOpacity.Value;
        }
        if (!string.IsNullOrEmpty(Config._oldBaseImagePath) && string.IsNullOrEmpty(Config.BackgroundPath))
        {
            Config.BackgroundPath = Config._oldBaseImagePath;
            if (Config.BackgroundType == "None") Config.BackgroundType = "Image";
        }
        if (Config.MemoryReserveMb == 3072) Config.MemoryReserveMb = 1536;

        Config._oldBaseImagePath = null;
        Config._oldOverlayType = null;
        Config._oldOverlayPath = null;
        Config._oldOverlayOpacity = null;
    }

    /// <summary>原子保存:tmp 写入 → 刷盘 → 覆盖移动,失败不抛只记日志。
    /// 2026-09-25 修复:加锁串行化并发 Save——下载完成/设置页/启动计时等会并发保存,
    /// 无锁时 tmp 文件互相覆盖,偶发「保存失败」或旧配置覆盖新配置。</summary>
    private readonly object _saveLock = new();
    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                NextPaths.EnsureDir(AppPaths.Root);
                string tmp = ConfigPath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, Config, JsonOpts);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, ConfigPath, overwrite: true);
            }
            catch (Exception ex) { NextLog.Error("[配置] 保存失败", ex); }
        }
    }
}
