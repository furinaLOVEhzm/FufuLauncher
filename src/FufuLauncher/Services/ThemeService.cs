// ThemeService.cs — 主题与背景服务
// 可爱的芙芙 - 完全重写的单层背景系统
//
// 背景类型:None / Image / Video
// 配置属性:BackgroundType / BackgroundPath / BackgroundOpacity / VideoMuted / VideoSpeed

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FufuLauncher.Services;

public class ThemeService
{
    private readonly ConfigService _configService;

    // 当前活动的视觉元素(由 ApplyBackground 创建)
    private MediaElement? _activeVideo;
    private Image? _activeImage;
    private Border? _targetLayer;

    public ThemeService(ConfigService configService)
    {
        _configService = configService;
    }

    // ===== 主题(Light / Dark)=====

    public void ApplyTheme()
    {
        ApplyTheme(_configService.Config.Theme);
    }

    public void ApplyTheme(string theme)
    {
        bool dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
        var res = Application.Current.Resources;
        res["AppBackground"]  = res[dark ? "DarkBackground"  : "LightBackground"];
        res["AppForeground"]  = res[dark ? "DarkForeground"  : "LightForeground"];
        res["CardBackground"] = res[dark ? "DarkCardBackground" : "LightCardBackground"];
        // 次要文字色:浅色主题用深灰、深色主题用浅灰,保证与背景 5:1 以上对比度
        res["SecondaryTextBrush"] = res[dark ? "DarkSecondaryText" : "LightSecondaryText"];
        res["CardBorder"]     = dark
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)) { Opacity = 0.16 }
            : new SolidColorBrush(Color.FromRgb(0x1F, 0x20, 0x28)) { Opacity = 0.14 };
        res["InputBorder"]    = res[dark ? "DarkInputBorder" : "LightInputBorder"];
        // 蒙版:深色主题黑蒙版压暗壁纸;浅色主题白蒙版提亮壁纸(避免深色文字压在花壁纸上看不清)
        res["OverlayTint"]    = dark
            ? new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1F)) { Opacity = 0.62 }
            : new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF6)) { Opacity = 0.66 };
        res["StatusTextBrush"] = dark
            ? new SolidColorBrush(Color.FromRgb(0xE6, 0xE7, 0xF0)) { Opacity = 0.9 }
            : new SolidColorBrush(Color.FromRgb(0x2A, 0x2B, 0x36)) { Opacity = 0.9 };
    }

    public void SetTheme(string theme)
    {
        if (theme != "Light" && theme != "Dark") return;
        _configService.Config.Theme = theme;
        _configService.Save();
        ApplyTheme(theme);
    }

    // ===== 背景:设置与清除 =====

    /// <summary>设置背景(图片路径或视频路径),自动检测类型并立即应用。
    /// 用户选择的素材统一复制到规范 tupian 目录存放,程序只读写该目录内的背景文件。</summary>
    public void SetBackground(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            App.WriteAppLog($"[背景] 文件不存在:{filePath}");
            return;
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string type = (ext is ".mp4" or ".mkv" or ".avi" or ".wmv" or ".mov") ? "Video" : "Image";

        // 素材归位到 tupian 目录(已在 tupian 内的不重复复制)
        string storedPath = filePath;
        try
        {
            string imagesDir = AppPaths.Images;
            string fullSrc = Path.GetFullPath(filePath);
            if (!fullSrc.StartsWith(Path.GetFullPath(imagesDir), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(imagesDir);
                string dst = Path.Combine(imagesDir, $"bg_{DateTime.Now:yyyyMMddHHmmss}{ext}");
                File.Copy(fullSrc, dst, overwrite: true);
                storedPath = dst;
                App.WriteAppLog($"[背景] 素材已复制到 tupian:{dst}");
            }
        }
        catch (Exception ex)
        {
            // 复制失败不阻断:退回直接使用原文件
            App.WriteAppLog($"[背景] 复制到 tupian 失败,直接使用原文件:{ex.Message}");
        }

        _configService.Config.BackgroundType = type;
        _configService.Config.BackgroundPath = storedPath;
        _configService.Save();

        ApplyBackgroundNow();
    }

    /// <summary>清除背景(恢复为无背景)</summary>
    public void ClearBackground()
    {
        _configService.Config.BackgroundType = "None";
        _configService.Config.BackgroundPath = "";
        _configService.Save();

        ApplyBackgroundNow();
    }

    /// <summary>修改背景透明度并立即生效</summary>
    public void SetOpacity(double opacity)
    {
        _configService.Config.BackgroundOpacity = opacity;
        _configService.Save();

        // 直接修改当前视觉元素的透明度
        if (_activeVideo != null) _activeVideo.Opacity = opacity;
        else if (_activeImage != null) _activeImage.Opacity = opacity;
        else
        {
            // 无活跃视觉元素,尝试重新应用背景
            App.WriteAppLog($"[背景] SetOpacity: 无活跃视觉元素,尝试重新应用背景");
            ApplyBackgroundNow();
        }

        SyncMaskOpacity();
    }

    /// <summary>修改视频静音状态</summary>
    public void SetVideoMuted(bool muted)
    {
        _configService.Config.VideoMuted = muted;
        _configService.Save();
        if (_activeVideo != null) _activeVideo.IsMuted = muted;
    }

    /// <summary>修改视频播放速度</summary>
    public void SetVideoSpeed(double speed)
    {
        _configService.Config.VideoSpeed = speed;
        _configService.Save();
        if (_activeVideo != null) _activeVideo.SpeedRatio = speed;
    }

    // ===== 背景:应用到主窗口 =====

    /// <summary>启动时调用:传入 BackgroundLayer Border,首次应用背景</summary>
    public void Init(Border backgroundLayer)
    {
        _targetLayer = backgroundLayer;
        App.WriteAppLog($"[背景] Init: BackgroundLayer={backgroundLayer.GetType().Name}, " +
            $"ActualWidth={backgroundLayer.ActualWidth:F0}, ActualHeight={backgroundLayer.ActualHeight:F0}");
        ApplyBackgroundNow();
    }

    /// <summary>从主窗口查找 BackgroundLayer 并应用背景(不依赖外部传参)</summary>
    public void ApplyBackgroundNow()
    {
        try
        {
            // 确保有目标 Border
            if (_targetLayer == null)
            {
                var mainWindow = Application.Current?.MainWindow;
                if (mainWindow == null)
                {
                    App.WriteAppLog("[背景] ApplyBackgroundNow: MainWindow 为 null,跳过");
                    return;
                }
                _targetLayer = mainWindow.FindName("BackgroundLayer") as Border;
                if (_targetLayer == null)
                {
                    App.WriteAppLog("[背景] ApplyBackgroundNow: BackgroundLayer 未找到或类型不是 Border");
                    return;
                }
            }

            // 释放旧资源
            ReleaseActiveMedia();
            _targetLayer.Child = null;

            var cfg = _configService.Config;
            // 解析生效背景:用户配置优先;未选择/路径失效时回退全局默认背景(tupian\jm.png)
            var (bgType, bgPath) = ResolveEffectiveBackground();
            App.WriteAppLog($"[背景] ApplyBackgroundNow: Type={cfg.BackgroundType}, Path={cfg.BackgroundPath}, Opacity={cfg.BackgroundOpacity}, 生效={bgType}:{bgPath}");

            // 无背景 且 无可用默认背景 → 只更新蒙版
            if (bgType == "None" || string.IsNullOrEmpty(bgPath))
            {
                App.WriteAppLog($"[背景] 无有效背景且无默认背景,清除背景层 (Type={cfg.BackgroundType})");
                SyncMaskOpacity();
                return;
            }

            double opacity = cfg.BackgroundOpacity;

            if (bgType == "Video")
            {
                var video = new MediaElement
                {
                    Source = new Uri(bgPath, UriKind.Absolute),
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Manual,
                    IsMuted = cfg.VideoMuted,
                    Stretch = Stretch.UniformToFill,
                    Opacity = opacity,
                    SpeedRatio = cfg.VideoSpeed,
                };
                video.MediaEnded += (_, _) => { video.Position = TimeSpan.Zero; };
                video.MediaFailed += (_, args) =>
                {
                    App.WriteAppLog($"[背景] 视频加载失败:{args.ErrorException?.Message}");
                };
                _targetLayer.Child = video;
                _activeVideo = video;
                video.Play();
                App.WriteAppLog($"[背景] 视频已添加并开始播放");
            }
            else // Image
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(bgPath, UriKind.Absolute);
                bmp.EndInit();

                App.WriteAppLog($"[背景] BitmapImage 加载完成: {bmp.PixelWidth}x{bmp.PixelHeight}");

                var img = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.UniformToFill,
                    Opacity = opacity,
                };
                _targetLayer.Child = img;
                _activeImage = img;
                App.WriteAppLog($"[背景] Image 已添加到背景层");
            }

            App.WriteAppLog($"[背景] 背景层状态: Child={_targetLayer.Child?.GetType().Name}, " +
                $"LayerSize={_targetLayer.ActualWidth:F0}x{_targetLayer.ActualHeight:F0}");

            SyncMaskOpacity();
            App.WriteAppLog($"[背景] 已应用:{bgType} = {bgPath}");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[背景] ApplyBackgroundNow 异常:{ex}");
        }
    }

    /// <summary>
    /// 解析当前生效的背景:用户自定义配置优先;
    /// 未选择(None)、路径为空或文件丢失时,回退全局默认背景 tupian\jm.png(存在则生效)。
    /// 用户随时可用设置页的自定义背景覆盖默认背景,清除后再次回退默认。
    /// </summary>
    private (string Type, string Path) ResolveEffectiveBackground()
    {
        var cfg = _configService.Config;
        if (cfg.BackgroundType != "None"
            && !string.IsNullOrEmpty(cfg.BackgroundPath)
            && File.Exists(cfg.BackgroundPath))
        {
            return (cfg.BackgroundType, cfg.BackgroundPath);
        }
        string def = BrandAssets.DefaultBackgroundFile;
        if (File.Exists(def)) return ("Image", def);
        return ("None", "");
    }

    /// <summary>更新蒙版透明度:背景越透明,蒙版越浓(保证前景可读性)</summary>
    private void SyncMaskOpacity()
    {
        try
        {
            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow == null) { App.WriteAppLog("[背景] SyncMaskOpacity: MainWindow 为 null"); return; }
            var mask = mainWindow.FindName("OverlayMaskBorder") as Border;
            if (mask == null) { App.WriteAppLog("[背景] SyncMaskOpacity: OverlayMaskBorder 未找到"); return; }

            var cfg = _configService.Config;
            var (bgType, bgPath) = ResolveEffectiveBackground();
            bool hasBackground = bgType != "None" && !string.IsNullOrEmpty(bgPath);

            if (!hasBackground)
            {
                mask.Opacity = 0;
            }
            else
            {
                // opacity=1.0 → 蒙版 0(背景全显示);opacity=0.2 → 蒙版 0.4
                mask.Opacity = (1.0 - cfg.BackgroundOpacity) * 0.5;
            }
            App.WriteAppLog($"[背景] SyncMaskOpacity: hasBackground={hasBackground}, mask.Opacity={mask.Opacity:F2}");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[背景] SyncMaskOpacity 异常:{ex.Message}");
        }
    }

    /// <summary>释放当前活动的视频/图片资源</summary>
    private void ReleaseActiveMedia()
    {
        if (_activeVideo != null)
        {
            try { _activeVideo.Stop(); _activeVideo.Close(); _activeVideo.Source = null; }
            catch (Exception ex) { App.WriteAppLog($"[背景] 释放视频失败:{ex.Message}"); }
            _activeVideo = null;
        }
        if (_activeImage != null)
        {
            _activeImage.Source = null;
            _activeImage = null;
        }
    }

    /// <summary>释放所有资源(窗口关闭时调用)</summary>
    public void Shutdown()
    {
        ReleaseActiveMedia();
        if (_targetLayer != null)
        {
            _targetLayer.Child = null;
            _targetLayer = null;
        }
    }

    // ===== 视频暂停/恢复(窗口最小化时节省资源)=====

    public void PauseVideo()
    {
        try { _activeVideo?.Pause(); } catch { /* 忽略 */ }
    }

    public void ResumeVideo()
    {
        try { _activeVideo?.Play(); } catch { /* 忽略 */ }
    }
}
