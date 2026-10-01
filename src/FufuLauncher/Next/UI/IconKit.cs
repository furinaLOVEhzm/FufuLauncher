// FufuLauncher - 图标库
// Copyright © FufuLauncher
//
// 图片优先 + 矢量兜底:
// 方块图标按版本类型区分(草方块=正式版/命令方块=快照/圆石=远古版/TNT=愚人节版,
// 优先用 tupian\icon\mc-*.png 像素图,缺失时逐级降级)/ Java 咖啡杯(运行时)/ 加载器官方 Logo。

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FufuLauncher.Services;
using FufuLauncher.Theme;
using Path = System.Windows.Shapes.Path;
using Shape = System.Windows.Shapes.Shape;

namespace FufuLauncher.Next.UI;

internal static class IconKit
{
    // ---- 方块图片图标(按类型缓存:grass/command/cobble/tnt,三态:未探测/已加载/缺失) ----
    private static readonly Dictionary<string, ImageSource?> _blockImgs = new(StringComparer.OrdinalIgnoreCase);

    private static ImageSource? BlockImage(string kind)
    {
        lock (_blockImgs)
        {
            if (_blockImgs.TryGetValue(kind, out var cached)) return cached;
            var img = ImageAssets.LoadFromFile(System.IO.Path.Combine(AppPaths.Images, "icon", $"mc-{kind}.png"));
            // 2026-09-25 缓存上限:方块种类超 1024 清空重建,防长期浏览只涨不降
        if (_blockImgs.Count > 1024) _blockImgs.Clear();
        _blockImgs[kind] = img;   // 先记录再返回,避免并发重复 IO
            return img;
        }
    }

    // ---- 调色(固定品牌色,不随主题翻转,保证图标辨识度)----
    // 2026-09-26 批5 说明:以下为「令牌豁免」色 —— 品牌/游戏官方色必须恒定,
    // 不接入 T.* 主题令牌,否则浅色主题下会因反转失去辨识度;新增此类颜色请沿用本注释约定。
    private static readonly Brush GrassGreen = Frozen("#6EBB4E");
    private static readonly Brush GrassLight = Frozen("#8CD468");
    private static readonly Brush DirtBrown = Frozen("#8A5A3B");
    private static readonly Brush JavaOrange = Frozen("#F0741A");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>Minecraft 方块图标(按版本类型区分):优先用官方风格像素图,
    /// 指定类型图片缺失时降级草方块,再缺失降级渐变自绘。
    /// kind:grass=正式版 / command=快照 / cobble=远古版 / tnt=愚人节版</summary>
    public static FrameworkElement McBlock(double size = 34, string kind = "grass")
    {
        var img = BlockImage(kind);
        if (img == null && kind != "grass") img = BlockImage("grass");   // 类型图缺失降级草方块
        if (img != null)
        {
            var im = new Image
            {
                Source = img,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,   // 保持原始比例,不拉伸压扁
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.Fant);
            return im;
        }

        var bg = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1),
            GradientStops =
            {
                new GradientStop(((SolidColorBrush)GrassLight).Color, 0),
                new GradientStop(((SolidColorBrush)GrassLight).Color, 0.20),
                new GradientStop(((SolidColorBrush)GrassGreen).Color, 0.20),
                new GradientStop(((SolidColorBrush)GrassGreen).Color, 0.40),
                new GradientStop(((SolidColorBrush)DirtBrown).Color, 0.40),
                new GradientStop(((SolidColorBrush)DirtBrown).Color, 1)
            }
        };
        bg.Freeze();
        var bd = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.22),
            Background = bg,
            VerticalAlignment = VerticalAlignment.Center
        };
        bd.BorderThickness = new Thickness(1);
        bd.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        return bd;
    }

    /// <summary>Java 咖啡杯图标:杯身 + 杯柄 + 两缕热气(Java 品牌橙)</summary>
    public static FrameworkElement JavaCup(double size = 30)
    {
        var body = new Path
        {
            Data = Geometry.Parse("M4.5,10.5 L19.5,10.5 L18.3,16.2 C17.8,19.4 6.2,19.4 5.7,16.2 Z"),
            Fill = JavaOrange
        };
        var handle = new Path
        {
            Data = Geometry.Parse("M19.6,11.6 C23.4,11.8 23.4,15.4 18.8,15.7"),
            Stroke = JavaOrange,
            StrokeThickness = 1.9,
            Fill = Brushes.Transparent
        };
        var steam1 = new Path
        {
            Data = Geometry.Parse("M9.4,3.2 C10.6,4.6 8.2,5.5 9.4,7.2"),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        steam1.SetResourceReference(Shape.StrokeProperty, "T.ForegroundDim");
        var steam2 = new Path
        {
            Data = Geometry.Parse("M14.6,3.2 C15.8,4.6 13.4,5.5 14.6,7.2"),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        steam2.SetResourceReference(Shape.StrokeProperty, "T.ForegroundDim");

        var box = new Viewbox
        {
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Grid { Children = { body, handle, steam1, steam2 } }
        };
        return box;
    }

    /// <summary>加载器字母徽章:Fabric/Forge/NeoForge/Quilt/OptiFine 各自品牌色,原版用中性圆点</summary>
    public static FrameworkElement LoaderGlyph(string kind, double size = 30)
    {
        var (letter, hex) = (kind ?? "").ToLowerInvariant() switch
        {
            "fabric" => ("F", "#DBB45B"),
            "forge" => ("Fo", "#E0703A"),
            "neoforge" => ("N", "#DC8F46"),
            "quilt" => ("Q", "#9C5BC4"),
            "optifine" => ("Of", "#62B04D"),
            _ => ("·", "#6E7681")
        };
        var bd = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.28),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = letter,
                FontSize = size * (letter.Length > 1 ? 0.36 : 0.48),
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        bd.Background = Frozen(hex);
        return bd;
    }

    // ---- 加载器官方 Logo(嵌入资源 PNG,按 kind 缓存;加载失败回退字母徽章) ----
    private static readonly Dictionary<string, ImageSource?> _loaderLogoCache = new(StringComparer.OrdinalIgnoreCase);

    private static ImageSource? LoaderLogoImage(string kind)
    {
        lock (_loaderLogoCache)
        {
            if (_loaderLogoCache.TryGetValue(kind, out var cached)) return cached;
            ImageSource? img = null;
            try
            {
                var uri = new Uri($"pack://application:,,,/assets/loaders/{kind.ToLowerInvariant()}.png", UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
            }
            catch { /* 资源缺失走字母徽章回退 */ }
            _loaderLogoCache[kind] = img;
            return img;
        }
    }

    /// <summary>加载器官方 Logo 图标:优先嵌入的 PNG(带圆角裁剪),缺失/未知加载器回退字母徽章。
    /// kind 支持 fabric/forge/neoforge/quilt/optifine(不区分大小写),空或未知 → 中性圆点徽章。</summary>
    public static FrameworkElement LoaderLogo(string kind, double size = 28)
    {
        var key = (kind ?? "").Trim();
        var img = key.Length > 0 ? LoaderLogoImage(key) : null;
        if (img == null) return LoaderGlyph(key, size);

        var image = new Image
        {
            Source = img,
            Width = size,
            Height = size,
            Stretch = Stretch.UniformToFill,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Clip = new RectangleGeometry(new Rect(0, 0, size, size), size * 0.24, size * 0.24)
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }
}
