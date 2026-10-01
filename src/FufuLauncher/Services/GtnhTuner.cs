// GtnhTuner.cs — GTNH(格雷科技:新视野)等大型魔改整合包专属调优
// Copyright © FufuLauncher
//
// 依据 GTNH 中文维基「低配电脑优化」官方建议落地:
// · 堆内存 ≥ 6GB,且 Xms = Xmx(本启动器智能模式本就固定 Xms=Xmx,天然满足);
// · 官方给出的 Java 8 参数集中 CMS 收集器与 -d64 在 Java 14+/9+ 上会直接致命,
//   而 GTNH 2.7.3+ 已支持 Java 17~21,故统一改写为 Java 8 与 17+ 双兼容的 G1 参数,
//   保留维基中跨版本安全的条目(DisableExplicitGC / MaxGCPauseMillis=120 /
//   UseCompressedOops / UseCodeCacheFlushing / ParallelGCThreads=物理核数);
// · 维基条目 -XX:+UseStringDeduplication 需 G1 收集器(CMS 下 JVM 拒启),选 G1 后可安全保留;
// · 建议禁用 Forge 启动闪屏(config\splash.properties → enabled=false),低端机可省大量加载时间。
// 内存建议:整机物理内存 ≥24GB → 12GB,≥16GB → 8GB,否则 6GB(官方下限)。

using System;
using System.IO;
using System.Linq;

namespace FufuLauncher.Services;

public static class GtnhTuner
{
    /// <summary>判定导入内容是否为 GTNH 整合包(包名特征 + mods 内 GregTech 核心 jar 双通道)</summary>
    public static bool IsGtnh(string? packName, string instDir)
    {
        if (!string.IsNullOrWhiteSpace(packName))
        {
            string n = packName!.ToLowerInvariant();
            if (n.Contains("gtnh") || n.Contains("gregtech new horizons") ||
                n.Contains("格雷科技新视野") || n.Contains("格雷新视野"))
                return true;
        }
        // mods 目录(实例内为联接,物理目录 mods\{id} 亦可)扫描 GregTech/GTNH 核心 jar
        try
        {
            string modsDir = Path.Combine(instDir, "mods");
            if (Directory.Exists(modsDir))
            {
                var jars = Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.AllDirectories)
                                    .Take(2000)
                                    .Select(Path.GetFileName)
                                    .Where(f => f != null)
                                    .ToList();
                if (jars.Any(f => f!.IndexOf("gregtech", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  f.IndexOf("gtnh", StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            }
        }
        catch (Exception ex) { App.WriteAppLog($"[GTNH] mods 扫描失败:{ex.Message}"); }
        return false;
    }

    /// <summary>按整机物理内存给出 GTNH 级建议堆内存(MB):≥24GB→12G,≥16GB→8G,否则 6G(官方下限)</summary>
    public static int RecommendMemoryMb(MemoryMonitorService memoryMonitor)
    {
        long totalMb = memoryMonitor.GetCurrent().TotalBytes / (1024L * 1024);
        if (totalMb >= 24576) return 12288;
        if (totalMb >= 16384) return 8192;
        return 6144;
    }

    /// <summary>
    /// 对识别为 GTNH 的游戏版本写入专属调优:建议大内存 + 专属 JVM 参数 + 禁用 Forge 闪屏。
    /// 幂等:ExtraJvmArgs 已含 GC 选择器时不再重复注入。返回调优说明(空 = 未调优)。
    /// </summary>
    public static string Apply(GameInstance inst, string instDir, MemoryMonitorService memoryMonitor)
    {
        try
        {
            int recMb = RecommendMemoryMb(memoryMonitor);
            inst.RecommendedMemoryMb = Math.Max(inst.RecommendedMemoryMb, recMb);

            if (!ContainsGcSelector(inst.ExtraJvmArgs))
            {
                int cores = MemoryMonitorService.GetPhysicalCoreCount();
                string args = "-XX:+UseG1GC -XX:+UnlockExperimentalVMOptions " +
                              "-XX:G1NewSizePercent=15 -XX:G1ReservePercent=25 -XX:G1HeapRegionSize=16M " +
                              "-XX:G1MixedGCLiveThresholdPercent=75 -XX:G1HeapWastePercent=5 " +
                              "-XX:+UseStringDeduplication -XX:+DisableExplicitGC -XX:MaxGCPauseMillis=120 " +
                              "-XX:+UseCompressedOops -XX:+UseCodeCacheFlushing " +
                              $"-XX:ParallelGCThreads={Math.Max(1, cores)}";
                inst.ExtraJvmArgs = string.IsNullOrWhiteSpace(inst.ExtraJvmArgs)
                    ? args : inst.ExtraJvmArgs.Trim() + " " + args;
            }

            DisableForgeSplash(instDir);

            App.WriteAppLog($"[GTNH] 已对「{inst.Name}」应用 GTNH 调优:建议内存 {recMb}MB + G1 专属参数 + 禁用 Forge 闪屏");
            return $"已识别为 GTNH 大型整合包,已自动配置建议内存 {recMb / 1024}GB 与专属 JVM 参数。";
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[GTNH] 调优失败(不阻断导入):{ex.Message}");
            return "";
        }
    }

    /// <summary>ExtraJvmArgs 是否已显式选择 GC 收集器(用户/整合包自带参数优先,启动器不再叠加)</summary>
    public static bool ContainsGcSelector(string? extraJvmArgs)
    {
        if (string.IsNullOrWhiteSpace(extraJvmArgs)) return false;
        return extraJvmArgs.Contains("UseG1GC") || extraJvmArgs.Contains("UseZGC") ||
               extraJvmArgs.Contains("UseConcMarkSweepGC") || extraJvmArgs.Contains("UseParallelGC") ||
               extraJvmArgs.Contains("UseSerialGC") || extraJvmArgs.Contains("UseShenandoahGC");
    }

    /// <summary>禁用 Forge 启动闪屏(GTNH 维基建议:低端机闪屏加载可长达数十分钟)</summary>
    private static void DisableForgeSplash(string instDir)
    {
        try
        {
            string splash = Path.Combine(instDir, "config", "splash.properties");
            if (!File.Exists(splash)) return;
            string text = File.ReadAllText(splash);
            if (text.Contains("enabled=false")) return;
            text = text.Contains("enabled=")
                ? System.Text.RegularExpressions.Regex.Replace(text, @"enabled\s*=\s*[^,\r\n]*", "enabled=false")
                : text.TrimEnd() + "\nenabled=false";
            File.WriteAllText(splash, text);
            App.WriteAppLog("[GTNH] 已禁用 Forge 启动闪屏(splash.properties enabled=false)");
        }
        catch (Exception ex) { App.WriteAppLog($"[GTNH] 禁用 Forge 闪屏失败(忽略):{ex.Message}"); }
    }
}
