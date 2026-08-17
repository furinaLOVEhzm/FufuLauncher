// ConfigService.cs — 配置服务
// 可爱的芙芙 - 管理用户设置持久化
//
// 保存内容:主题、下载源、Java 路径、JVM 参数、分辨率、背景设置等

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FufuLauncher.Services;

public class AppConfig
{
    public string Theme { get; set; } = "Light";           // Light / Dark
    public string DownloadSource { get; set; } = "BMCLAPI"; // Mojang / BMCLAPI
    /// <summary>模组下载源(独立于游戏下载源): Modrinth / CurseForge / MCMod(默认 Modrinth)</summary>
    public string ModDownloadSource { get; set; } = "Modrinth";
    public string JavaPath { get; set; } = "";
    public int JavaVersion { get; set; } = 17;
    public int Xms { get; set; } = 1024;                   // MB
    public int Xmx { get; set; } = 4096;                   // MB
    public string ExtraJvmArgs { get; set; } = "";
    public int GameWidth { get; set; } = 1280;
    public int GameHeight { get; set; } = 720;
    public bool Fullscreen { get; set; } = false;

    // ===== 单层背景:None / Image / Video =====
    /// <summary>背景类型:None / Image / Video</summary>
    public string BackgroundType { get; set; } = "None";
    /// <summary>背景文件路径(图片或视频)</summary>
    public string BackgroundPath { get; set; } = "";
    /// <summary>背景透明度 0~1(1=不透明,0=全透明)</summary>
    public double BackgroundOpacity { get; set; } = 0.6;
    public bool VideoMuted { get; set; } = true;
    public double VideoSpeed { get; set; } = 1.0;
    public int VideoFps { get; set; } = 30;
    public double VideoVolume { get; set; } = 0.0;

    public string LastInstanceId { get; set; } = "";
    public string DevPassword { get; set; } = "1234567";

    // ===== 多账号:当前使用的账号 UUID(重启后恢复,避免每次回退第一个账号)=====
    public string CurrentAccountUuid { get; set; } = "";

    // ===== Java 运行库管理(段3)=====
    /// <summary>Java 下载镜像:Official / BMCLAPI / Huaweicloud(默认 Huaweicloud 国内优先)</summary>
    public string JavaDownloadMirror { get; set; } = "Huaweicloud";
    /// <summary>开机是否自动全盘扫描 Java(默认 false,改为按需校验)</summary>
    public bool AutoScanJavaOnStartup { get; set; } = false;

    // ===== JVM 多核优化预设(段3)=====
    /// <summary>是否启用 GC 多核优化预设(ParallelGCThreads/CICompilerCount/ConcGCThreads)</summary>
    public bool MultiCoreGcOptimize { get; set; } = false;
    /// <summary>是否设置 Java 进程高优先级(禁止 Realtime)</summary>
    public bool HighPriorityProcess { get; set; } = false;
    /// <summary>是否启用 CPU 亲和性增强</summary>
    public bool CpuAffinityEnabled { get; set; } = false;

    // ===== 内存智能分配(借鉴 PCL2/HMCL/PrismLauncher)=====
    /// <summary>是否启用智能自动内存分配(关闭则用手动 Xmx)</summary>
    public bool AutoMemoryMode { get; set; } = true;
    /// <summary>内存档位:Vanilla 纯净 / Modded 中型模组 / Shader 大型光影</summary>
    public string MemoryTier { get; set; } = "Modded";
    /// <summary>内存预提交(AlwaysPreTouch):启动时一次性提交全部堆页,避免游戏中途缺页抖动(借鉴内存池预分配思路)</summary>
    public bool MemoryPreCommit { get; set; } = true;
    /// <summary>智能分配时为系统强制预留的空闲内存(MB),下限 1536(1.5GB)不可再降</summary>
    public int MemoryReserveMb { get; set; } = 1536;
    /// <summary>智能分配内存上限(MB),防止溢出</summary>
    public int AutoMemoryMaxMb { get; set; } = 16384;
    /// <summary>智能分配内存下限(MB)</summary>
    public int AutoMemoryMinMb { get; set; } = 1024;
    /// <summary>堆外直接内存上限 MaxDirectMemorySize(MB);0 = 自动取 Xmx 的 0.75 倍,硬锁 DirectBuffer 防止堆外无限膨胀</summary>
    public int MaxDirectMemoryMb { get; set; } = 0;

    // ===== 应用日志自动清理(每 N 次启动清理一次,保留最新 N 份)=====
    /// <summary>是否启用应用日志自动清理</summary>
    public bool LogAutoCleanEnabled { get; set; } = true;
    /// <summary>每启动多少次启动器执行一次自动清理</summary>
    public int LogCleanEveryNLaunches { get; set; } = 3;
    /// <summary>保留最新多少份历史日志(当前活跃日志不计入)</summary>
    public int LogKeepCount { get; set; } = 10;
    /// <summary>启动器累计启动次数(用于按次触发日志清理)</summary>
    public int AppLaunchCount { get; set; } = 0;

    // ===== 程序自身更新系统 =====
    /// <summary>更新清单 JSON 的 URL(发布前在配置中指向实际地址)</summary>
    public string UpdateManifestUrl { get; set; } = "";
    /// <summary>启动时是否自动检测新版本</summary>
    public bool AutoCheckUpdate { get; set; } = true;

    // ===== 已废弃的旧配置字段(保留以兼容旧 config.json,读取时自动迁移后清空) =====
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
    [JsonPropertyName("BackgroundEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? _oldBackgroundEnabled { get; set; }
}

public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // 配置文件固化在 APP\MCGAME\Config\config.json(避免 static readonly 在目录就绪前初始化的顺序耦合)
    private static string ConfigPath => AppPaths.AppConfigFile;

    public AppConfig Config { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                Config = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
            }
            // 旧配置迁移:OverlayPath/OverlayType → BackgroundPath/BackgroundType
            if (!string.IsNullOrEmpty(Config._oldOverlayPath)
                && string.IsNullOrEmpty(Config.BackgroundPath))
            {
                Config.BackgroundPath = Config._oldOverlayPath;
                Config.BackgroundType = string.IsNullOrEmpty(Config._oldOverlayType)
                    ? "Image" : Config._oldOverlayType;
                if (Config._oldOverlayOpacity.HasValue && Config._oldOverlayOpacity.Value > 0)
                    Config.BackgroundOpacity = Config._oldOverlayOpacity.Value;
            }
            if (!string.IsNullOrEmpty(Config._oldBaseImagePath)
                && string.IsNullOrEmpty(Config.BackgroundPath))
            {
                Config.BackgroundPath = Config._oldBaseImagePath;
                if (Config.BackgroundType == "None")
                    Config.BackgroundType = "Image";
            }
            // 旧配置迁移:系统预留 3GB → 1.5GB(旧默认值 3072 直接跟随新默认)
            if (Config.MemoryReserveMb == 3072)
                Config.MemoryReserveMb = 1536;
            // 迁移完成后清空旧字段,避免下次重复迁移
            Config._oldBaseImagePath = null;
            Config._oldOverlayType = null;
            Config._oldOverlayPath = null;
            Config._oldOverlayOpacity = null;
        }
        catch
        {
            Config = new AppConfig();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Config, JsonOpts);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // 保存失败不抛出,避免崩溃
        }
    }
}
