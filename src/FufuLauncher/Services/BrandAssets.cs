// BrandAssets.cs — 品牌静态资源(应用 Logo / 默认背景)
// FufuLauncher - 仅负责运行时 UI 资源加载,不涉及 EXE 编译图标(ApplicationIcon 保持不变)
//
// 资源位置(数据目录下,随用户文件分发):
// - {AppPaths.Root}\tupian\tub.png  应用专属 Logo(标题栏/侧边栏/关于页)
// - {AppPaths.Root}\tupian\jm.png   全局默认背景(用户未选择自定义背景时使用)

using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace FufuLauncher.Services;

public static class BrandAssets
{
    /// <summary>应用 Logo 文件路径(tupian\tub\logo.png,与 ImageAssets 素材规范一致)</summary>
    public static string LogoFile => Path.Combine(AppPaths.Root, "tupian", "tub", "logo.png");

    /// <summary>全局默认背景:主题目录 jm 下字典序首个 bg-*.png(新版规范为目录,非单文件)。
    /// 未找到时返回空串,调用方按"无背景"处理(内嵌预设背景会在首次启动释放到 jm\ 目录)。</summary>
    public static string DefaultBackgroundFile
    {
        get
        {
            try
            {
                string dir = Path.Combine(AppPaths.Root, "tupian", "jm");
                if (!Directory.Exists(dir)) return "";
                return Directory.GetFiles(dir, "bg-*.png")
                                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault() ?? "";
            }
            catch { return ""; }
        }
    }

    private static BitmapImage? _logoCache;
    private static bool _logoTried;
    private static readonly object _logoLock = new();

    /// <summary>
    /// 加载应用 Logo(进程内只加载一次,失败返回 null 由调用方回退原占位样式)。
    /// 解码限制 256px:三处展示(22/28/64)在高 DPI 下均足够清晰,且避免大图全解码占内存。
    /// </summary>
    public static BitmapImage? GetLogo()
    {
        lock (_logoLock)
        {
            if (_logoTried) return _logoCache;
            _logoTried = true;
            try
            {
                string path = LogoFile;
                if (!File.Exists(path))
                {
                    App.WriteAppLog($"[品牌] Logo 文件不存在,沿用内置占位样式:{path}");
                    return null;
                }
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // 立即解码并释放文件句柄
                bmp.DecodePixelWidth = 256;                   // 保持宽高比缩放,禁止拉伸变形
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();                                 // 冻结:跨线程安全 + 多处共享同一实例
                _logoCache = bmp;
                App.WriteAppLog($"[品牌] Logo 加载完成:{bmp.PixelWidth}x{bmp.PixelHeight} (解码限制256px)");
                return _logoCache;
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[品牌] Logo 加载失败,沿用内置占位样式:{ex.Message}");
                return null;
            }
        }
    }

    /// <summary>判断默认背景文件是否可用</summary>
    public static bool HasDefaultBackground()
    {
        try { return File.Exists(DefaultBackgroundFile); }
        catch { return false; }
    }
}
