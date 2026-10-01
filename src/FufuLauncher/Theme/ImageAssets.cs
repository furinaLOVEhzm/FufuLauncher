// FufuLauncher - 图片素材加载(全量重构版)
// Copyright © FufuLauncher
//
// 素材目录规范(固定不可改):
//   tupian     素材根
//   tupian\jm  主题素材(主题 json、主题背景)
//   tupian\tub 程序图标与 logo(tub.png 为用户素材,禁止程序改动)
//
// 加载策略:外部文件优先,logo 缺失时走 exe 内嵌资源兜底;
// 一律 OnLoad 即时解码并 Freeze,读完即释放文件句柄。

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;   // WebP 降级解码(SaveAsPng 扩展方法);本文件未裸用 Image/Color 等,无命名冲突
using FufuLauncher.Next.Foundation;

namespace FufuLauncher.Theme;

public static class ImageAssets
{
    /// <summary>素材根 {数据根}\tupian</summary>
    public static string TupianDir { get; } = Path.Combine(NextPaths.Root, "tupian");

    /// <summary>主题素材目录 jm</summary>
    public static string ThemeDir { get; } = Path.Combine(NextPaths.Root, "tupian", "jm");

    /// <summary>图标素材目录 tub</summary>
    public static string LogoDir { get; } = Path.Combine(NextPaths.Root, "tupian", "tub");

    /// <summary>外部 logo 首选路径 tub\logo.png</summary>
    public static string ExternalLogoPath { get; } = Path.Combine(NextPaths.Root, "tupian", "tub", "logo.png");

    /// <summary>exe 内嵌兜底 logo(Resource 构建项)</summary>
    private const string EmbeddedLogoUri = "pack://application:,,,/FufuLauncher;component/assets/embedded-logo.png";   // 资源名实际全小写(大小写敏感)

    // ==================== 通用加载 ====================

    /// <summary>从磁盘加载位图;失败返回 null 不抛异常</summary>
    public static BitmapImage? LoadFromFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // 读完即释放流
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();   // 冻结后跨线程安全
            return bmp;
        }
        catch (Exception ex)
        {
            NextLog.Warn($"[资源] 图片加载失败 {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>按文件名从主题目录 jm 加载贴图(不存在返回 null)</summary>
    public static BitmapImage? LoadThemeImage(string fileName)
        => string.IsNullOrWhiteSpace(fileName) ? null : LoadFromFile(Path.Combine(ThemeDir, fileName));

    /// <summary>把网络下载的原始字节解码为可渲染位图(已 Freeze,跨线程安全);失败返回 null 不抛异常。
    /// ① 优先走 WPF 原生 WIC(PNG/JPG/GIF/BMP 等既有行为零改动);
    /// ② 仅当 WIC 无对应解码器抛异常时(典型:WebP —— Win8/Win11 默认不带 WebP 编解码器,
    ///    Modrinth/CurseForge 图标绝大多数为 WebP,原逻辑静默吞异常导致"下载成功却不显示"),
    ///    降级用 ImageSharp 解码后转内存 PNG 再交回 WPF 原生渲染。</summary>
    public static BitmapSource? DecodeBytes(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;

        // ① 原生 WIC 路径:绝大多数格式命中,行为与历史完全一致
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            // WIC 无解码器(WebP 等)会抛 NotSupportedException;记录后降级 ImageSharp
            NextLog.Warn($"[资源] 原生解码失败({bytes.Length}B),尝试 ImageSharp 降级: {ex.Message}");
        }

        // ② 降级路径:ImageSharp 解码(WebP 等 WIC 不支持的格式)→ 内存 PNG → WPF 原生渲染
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load(bytes);
            using var png = new MemoryStream();
            img.SaveAsPng(png);
            png.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = png;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            NextLog.Error("[资源] ImageSharp 降级解码亦失败", ex);
            return null;
        }
    }

    /// <summary>把可能为 WebP 的原始字节归一化为 WPF 原生可渲染的字节(WebP → PNG)。
    /// 非 WebP 原样返回(不重编码,零损失零开销)。用于图标磁盘缓存:
    /// WIC 无 WebP 解码器,归一化后重复启动直接走原生快路径,免去每次 ImageSharp 解码。</summary>
    public static byte[] NormalizeToRenderableBytes(byte[] raw)
    {
        // WebP 魔数:"RIFF"...."WEBP";不足 12 字节或非 WebP 一律原样返回
        if (raw == null || raw.Length < 12) return raw ?? Array.Empty<byte>();
        bool isWebP = raw[0] == (byte)'R' && raw[1] == (byte)'I' && raw[2] == (byte)'F' && raw[3] == (byte)'F'
                   && raw[8] == (byte)'W' && raw[9] == (byte)'E' && raw[10] == (byte)'B' && raw[11] == (byte)'P';
        if (!isWebP) return raw;
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load(raw);
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            // 归一化失败不致命:保留原始字节,渲染时再走 DecodeBytes 降级路径
            NextLog.Warn($"[资源] WebP→PNG 归一化失败,保留原始字节: {ex.Message}");
            return raw;
        }
    }

    // ==================== Logo / 窗口图标 ====================

    private static ImageSource? _cachedLogo;   // 启动加速:logo 一次解码全程复用(窗口图标 + 标题栏两处调用)

    /// <summary>软件 logo:外部 tub\logo.png 优先,缺失走 exe 内嵌兜底。
    /// 结果静态缓存(已 Freeze 跨线程安全):构造期不再重复解码同一张 PNG</summary>
    public static ImageSource? ResolveLogo()
        => _cachedLogo ??= LoadFromFile(ExternalLogoPath) ?? LoadEmbeddedLogo();

    /// <summary>读取 exe 内嵌兜底 logo</summary>
    public static BitmapImage? LoadEmbeddedLogo()
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(EmbeddedLogoUri, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            NextLog.Error("[资源] 内嵌 logo 读取失败(外部与内置资源均不可用)", ex);
            return null;
        }
    }

    /// <summary>从主题目录挑选一张背景图(jm\bg-*.png 取字典序首个),无则 null</summary>
    public static BitmapImage? LoadAnyThemeBackground()
    {
        try
        {
            if (!Directory.Exists(ThemeDir)) return null;
            var hit = Directory.GetFiles(ThemeDir, "bg-*.png")
                               .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                               .FirstOrDefault();
            return hit == null ? null : LoadFromFile(hit);
        }
        catch { return null; }
    }

    // ==================== 内嵌预设背景释放 ====================

    /// <summary>预设主题背景文件名(与 Assets\themes 内嵌资源一一对应)</summary>
    private static readonly string[] BuiltinBackgrounds =
    {
        "bg-light.png", "bg-default.png", "bg-ocean.png",
        "bg-forest.png", "bg-dusk.png", "bg-ember.png", "bg-aura.png"
    };

    /// <summary>把 exe 内嵌的预设背景素材释放到 tupian\jm(已存在则跳过,用户可自行替换)</summary>
    public static void EnsureBuiltinBackgrounds()
    {
        try
        {
            Directory.CreateDirectory(ThemeDir);
            foreach (var name in BuiltinBackgrounds)
            {
                string dst = Path.Combine(ThemeDir, name);
                if (File.Exists(dst)) continue;   // 用户素材优先,绝不覆盖
                try
                {
                    var uri = new Uri($"pack://application:,,,/FufuLauncher;component/assets/themes/{name}", UriKind.Absolute);   // 资源名全小写
                    var res = System.Windows.Application.GetResourceStream(uri);
                    if (res == null) continue;
                    using (var input = res.Stream)
                    using (var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                    }
                    NextLog.Info($"[资源] 释放内嵌主题背景: {name}");
                }
                catch (Exception ex) { NextLog.Warn($"[资源] 内嵌背景释放失败 {name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { NextLog.Warn("[资源] 主题背景目录准备失败: " + ex.Message); }
    }
}
