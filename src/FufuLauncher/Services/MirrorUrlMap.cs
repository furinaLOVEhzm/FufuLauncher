// Copyright © FufuLauncher
//
// 下载源 URL 映射中心:「官方源 → 镜像源」的主机与路径映射,以及「自有镜像」的换根规则。
// 自建镜像按 BMCLAPI 的目录结构同步(fabric-meta / maven / assets / optifine / versions 等前缀),
// 所以启用自有源只需要换根、路径原样保留;不属于镜像覆盖范围的 URL(Modrinth、CurseForge 等)不改写。
// 这份映射原先在 DownloadService 与 VersionManifestService 各存一份且已经不一致,统一到这里。

using System;

namespace FufuLauncher.Services;

public static class MirrorUrlMap
{
    /// <summary>BMCLAPI 根地址,同时是自有镜像的路径布局基准</summary>
    public const string BmclapiRoot = "https://bmclapi2.bangbang93.com";

    /// <summary>
    /// 规范化用户填写的镜像根地址。只接受 https 绝对地址、不带查询串;返回 null 表示不可用。
    /// 强制 https 的原因:下载链路拿到的是可执行 jar 与版本清单,明文回源等于把完整性交给中间人。
    /// </summary>
    public static string? NormalizeBase(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        if (!s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        if (string.IsNullOrEmpty(u.Host) || u.Query.Length > 1 || u.Fragment.Length > 0) return null;
        return s.TrimEnd('/');
    }

    /// <summary>官方 / Maven 仓库 URL 换成 BMCLAPI 布局的等价 URL;不在映射表内的原样返回</summary>
    public static string ToBmclapi(string originalUrl)
    {
        if (originalUrl.Contains("piston-meta.mojang.com") ||
            originalUrl.Contains("launchermeta.mojang.com") ||
            originalUrl.Contains("launcher.mojang.com") ||
            originalUrl.Contains("piston-data.mojang.com") ||
            originalUrl.Contains("libraries.minecraft.net") ||
            originalUrl.Contains("resources.download.minecraft.net"))
        {
            return originalUrl
                .Replace("piston-meta.mojang.com", "bmclapi2.bangbang93.com")
                .Replace("launchermeta.mojang.com", "bmclapi2.bangbang93.com")
                // 旧版(<1.8)游戏本体 jar 走 launcher.mojang.com,BMCLAPI 同路径镜像
                .Replace("launcher.mojang.com", "bmclapi2.bangbang93.com")
                .Replace("piston-data.mojang.com", "bmclapi2.bangbang93.com")
                .Replace("libraries.minecraft.net", "bmclapi2.bangbang93.com/maven")
                // 2026-09-25 修复(BMCLAPI 官方文档):资源文件必须带 /assets 前缀,
                // 原实现替换成裸域名导致资源 URL 全部 404,每次资源下载白走一跳再切官方兜底
                .Replace("resources.download.minecraft.net", "bmclapi2.bangbang93.com/assets");
        }
        if (originalUrl.Contains("repo1.maven.org") ||
            originalUrl.Contains("repo.maven.apache.org") ||
            originalUrl.Contains("maven.fabricmc.net") ||
            originalUrl.Contains("meta.fabricmc.net") ||
            originalUrl.Contains("maven.minecraftforge.net") ||
            originalUrl.Contains("maven.neoforged.net") ||
            originalUrl.Contains("maven.quiltmc.org") ||
            originalUrl.Contains("files.minecraftforge.net"))
        {
            return originalUrl
                .Replace("repo1.maven.org/maven2", "bmclapi2.bangbang93.com/maven-central")
                .Replace("repo.maven.apache.org/maven2", "bmclapi2.bangbang93.com/maven-central")
                .Replace("maven.fabricmc.net", "bmclapi2.bangbang93.com/maven")
                // Fabric 版本元数据(meta.fabricmc.net)走 BMCLAPI fabric-meta 镜像
                .Replace("meta.fabricmc.net", "bmclapi2.bangbang93.com/fabric-meta")
                .Replace("maven.minecraftforge.net", "bmclapi2.bangbang93.com/maven")
                // NeoForge:域名替换 + /releases → /maven(BMCLAPI 官方映射)
                .Replace("maven.neoforged.net/releases", "bmclapi2.bangbang93.com/maven")
                .Replace("maven.quiltmc.org/repository/release", "bmclapi2.bangbang93.com/maven")
                .Replace("files.minecraftforge.net/maven", "bmclapi2.bangbang93.com/maven");
        }
        return originalUrl;
    }

    /// <summary>
    /// 换成自有镜像下的同路径 URL。两种入口都支持:
    /// 已经是 BMCLAPI 形态的 URL 直接换根(加载器安装链路上大量 URL 一拼出来就是镜像形态),
    /// 官方/Maven 形态的先按 BMCLAPI 映射定出路径再换根——因此自有镜像只需按 BMCLAPI 目录同步,
    /// 启动器侧不需要第二套映射表。不在镜像覆盖范围内的 URL 返回原值,继续直连官方。
    /// </summary>
    public static string ToCustom(string originalUrl, string? customBase)
    {
        if (string.IsNullOrEmpty(customBase)) return originalUrl;
        if (originalUrl.StartsWith(BmclapiRoot, StringComparison.OrdinalIgnoreCase))
            return customBase + originalUrl.Substring(BmclapiRoot.Length);
        string bmcl = ToBmclapi(originalUrl);
        if (bmcl == originalUrl) return originalUrl;
        return customBase + bmcl.Substring(BmclapiRoot.Length);
    }

    /// <summary>该 URL 是否落在镜像覆盖范围内(用于失败计数归属哪个源)</summary>
    public static bool IsCustomUrl(string url, string? customBase)
        => !string.IsNullOrEmpty(customBase) && url.StartsWith(customBase, StringComparison.OrdinalIgnoreCase);
}
