// FufuLauncher - 主题引擎(全量重构版)
// Copyright © FufuLauncher
//
// 职责:
// 1. 令牌字典 TokenDict 合并进 App.Resources,全界面经 DynamicResource 引用;
// 2. 内置 5 套预设 + 扫描 tupian\jm\*.json 自定义主题;
// 3. Apply 运行时原地改写令牌画刷 → 全界面即时换肤,免重启;
// 4. HSL 参数化主色:用户选色相,保留预设饱和度重建主色色阶(借鉴主流启动器);
// 5. 用户选择原子写 theme.settings.json,启动自动恢复。

using System.IO;
using System.Text.Json;
using System.Windows.Media;
using FufuLauncher.Next.Foundation;

namespace FufuLauncher.Theme;

public static class ThemeManager
{
    /// <summary>令牌字典(壳窗口启动前合并进 App.Resources)</summary>
    public static System.Windows.ResourceDictionary TokenDict { get; } = new();

    /// <summary>全部可用主题(预设 + 自定义)</summary>
    public static List<ThemeDefinition> All { get; } = new();

    /// <summary>当前生效主题(默认深色)</summary>
    public static ThemeDefinition Current { get; private set; } = Presets()[1];

    /// <summary>用户主题设置(持久化)</summary>
    public static ThemeSettings Settings { get; private set; } = new();

    /// <summary>主题切换通知(壳窗口刷新背景图/透明度)</summary>
    public static event Action? ThemeChanged;

    /// <summary>手动触发视觉刷新(设置页变更透明度/背景后调用)</summary>
    public static void NotifyVisualChanged() => ThemeChanged?.Invoke();

    private static readonly string SettingsPath = Path.Combine(NextPaths.Root, "theme.settings.json");
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    // ==================== 初始化 ====================

    /// <summary>启动初始化:预设 → 自定义扫描 → 恢复设置 → 应用</summary>
    public static void Initialize()
    {
        // 主题初始化分段计时(2026-08-29 启动提速排查):定位 1.7s+ 耗时的具体构成,稳定后可删
        var tw = System.Diagnostics.Stopwatch.StartNew();
        long twLast = 0;
        void TStage(string s)
        {
            long now = tw.ElapsedMilliseconds;
            NextLog.Info($"[主题计时] {s} +{now - twLast}ms 累计{now}ms");
            twLast = now;
        }
        ImageAssets.EnsureBuiltinBackgrounds();   // 内嵌预设背景释放(不覆盖用户素材)
        TStage("内嵌背景释放");
        All.Clear();
        All.AddRange(Presets());
        LoadCustomThemes();
        LoadSettings();
        TStage("主题加载+设置");

        var target = All.FirstOrDefault(t => t.Id == Settings.ThemeId);
        if (target == null)
        {
            NextLog.Warn($"[主题] 设置中的主题「{Settings.ThemeId}」不存在,回退 dark");
            target = All.First(t => t.Id == "dark");
        }
        Apply(target);
        TStage("令牌应用");
        NextLog.Info($"[主题] 已应用: {Current.Name} (自定义主题 {All.Count(t => t.IsCustom)} 个)");
    }

    // ==================== 预设主题(可爱简约:柔和中性色 + 明亮主色) ====================

    private static List<ThemeDefinition> Presets() => new()
    {
        new ThemeDefinition
        {
            Id = "light", Name = "晨雾 · 亮色", IsDark = false,
            BackgroundImage = "bg-light.png",
            BackgroundBlur = 0, CardOpacity = 1.0,
            Colors = new ThemeColors
            {
                Background = "#F3F5F9", Surface = "#FFFFFF", SurfaceAlt = "#E9EDF3",
                Foreground = "#20242C", ForegroundDim = "#6B7280",
                Primary = "#3B7BEC", PrimaryHover = "#5490F2",
                Border = "#D5DAE3", Success = "#2E9E6B", Warning = "#C98A1B", Danger = "#D24B46",
                Overlay = "#26FFFFFF"
            }
        },
        new ThemeDefinition
        {
            Id = "dark", Name = "石墨 · 深色", IsDark = true,
            BackgroundImage = "bg-default.png",
            BackgroundBlur = 0, CardOpacity = 1.0,
            Colors = new ThemeColors
            {
                Background = "#101418", Surface = "#1A2027", SurfaceAlt = "#232B35",
                Foreground = "#E8ECF1", ForegroundDim = "#8A93A0",
                Primary = "#4C8DFF", PrimaryHover = "#6BA1FF",
                Border = "#2A323D", Success = "#3FB27F", Warning = "#E0A93E", Danger = "#E25D5D",
                Overlay = "#A60B0E12"
            }
        },
        new ThemeDefinition
        {
            Id = "ocean", Name = "深海 · 藏蓝", IsDark = true,
            BackgroundImage = "bg-ocean.png",
            BackgroundBlur = 6, CardOpacity = 0.92,
            Colors = new ThemeColors
            {
                Background = "#0B1220", Surface = "#121C2E", SurfaceAlt = "#1A2740",
                Foreground = "#E6EDF7", ForegroundDim = "#7E8CA3",
                Primary = "#38BDF8", PrimaryHover = "#5BCBF9",
                Border = "#233248", Success = "#34D399", Warning = "#FBBF24", Danger = "#F87171",
                Overlay = "#A6070D18"
            }
        },
        new ThemeDefinition
        {
            Id = "forest", Name = "林原 · 暗绿", IsDark = true,
            BackgroundImage = "bg-forest.png",
            BackgroundBlur = 4, CardOpacity = 0.94,
            Colors = new ThemeColors
            {
                Background = "#0D1512", Surface = "#152019", SurfaceAlt = "#1D2B22",
                Foreground = "#E7F0EA", ForegroundDim = "#84998C",
                Primary = "#34C77B", PrimaryHover = "#53D492",
                Border = "#26382D", Success = "#34C77B", Warning = "#E5B454", Danger = "#E06055",
                Overlay = "#A6081009"
            }
        },
        new ThemeDefinition
        {
            Id = "dusk", Name = "暮云 · 暗紫", IsDark = true,
            BackgroundImage = "bg-dusk.png",
            BackgroundBlur = 6, CardOpacity = 0.9,
            Colors = new ThemeColors
            {
                Background = "#131019", Surface = "#1C1725", SurfaceAlt = "#272033",
                Foreground = "#EDE8F5", ForegroundDim = "#948BA6",
                Primary = "#A78BFA", PrimaryHover = "#B9A2FB",
                Border = "#322A42", Success = "#4ADE80", Warning = "#FACC15", Danger = "#FB7185",
                Overlay = "#A60D0A13"
            }
        },
        new ThemeDefinition
        {
            Id = "ember", Name = "赤霞 · 橙红", IsDark = true,
            BackgroundImage = "bg-ember.png",
            BackgroundBlur = 4, CardOpacity = 0.94,
            Colors = new ThemeColors
            {
                Background = "#1A120E", Surface = "#241812", SurfaceAlt = "#2F2018",
                Foreground = "#F2E9E2", ForegroundDim = "#A08D80",
                Primary = "#E8875B", PrimaryHover = "#F09C74",
                Border = "#3A2A1F", Success = "#8FBF6F", Warning = "#E5B454", Danger = "#E06055",
                Overlay = "#A6120B07"
            }
        },
        new ThemeDefinition
        {
            // 沉浸式设计语言旗舰主题:黄昏山林背景 + 暖橙点缀 + 高玻璃感
            Id = "aura", Name = "星晖 · 暖橙", IsDark = true,
            BackgroundImage = "bg-aura.png",
            BackgroundBlur = 6, CardOpacity = 0.88,
            Colors = new ThemeColors
            {
                Background = "#14100C", Surface = "#1E1712", SurfaceAlt = "#2A211A",
                Foreground = "#F5EEE6", ForegroundDim = "#A69483",
                Primary = "#FF9F43", PrimaryHover = "#FFB266",
                Border = "#3B2F24", Success = "#8FBF6F", Warning = "#FACC15", Danger = "#E06055",
                Overlay = "#A60F0B07"
            }
        }
    };

    // ==================== 自定义主题 ====================

    /// <summary>扫描 tupian\jm\*.json 加载外部主题(损坏文件跳过不炸)</summary>
    private static void LoadCustomThemes()
    {
        try
        {
            if (!Directory.Exists(ImageAssets.ThemeDir)) return;
            foreach (var f in Directory.GetFiles(ImageAssets.ThemeDir, "*.json"))
            {
                if (Path.GetFileName(f).Equals("theme.settings.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var t = JsonSerializer.Deserialize<ThemeDefinition>(File.ReadAllText(f), JsonOpt);
                    if (t == null || string.IsNullOrWhiteSpace(t.Id)) continue;
                    t.IsCustom = true;
                    if (string.IsNullOrWhiteSpace(t.Name)) t.Name = t.Id;
                    All.RemoveAll(x => x.Id == t.Id && x.IsCustom);   // 同 ID 覆盖旧自定义
                    All.Add(t);
                    NextLog.Info($"[主题] 加载自定义主题: {t.Name} ({Path.GetFileName(f)})");
                }
                catch (Exception ex) { NextLog.Warn($"[主题] 跳过损坏主题文件 {Path.GetFileName(f)}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { NextLog.Warn("[主题] 自定义主题扫描失败: " + ex.Message); }
    }

    // ==================== 应用 / 切换 ====================

    /// <summary>按 ID 切换主题(运行时免重启)</summary>
    public static bool Apply(string id)
    {
        var t = All.FirstOrDefault(x => x.Id == id);
        if (t == null) return false;
        Apply(t);
        return true;
    }

    private static void Apply(ThemeDefinition t)
    {
        Current = t;
        var c = t.Colors;

        // 自定义主色色相:保留预设主色饱和度,按用户色相重建主色色阶
        var primary = ParseColor(c.Primary);
        var primaryHover = ParseColor(c.PrimaryHover);
        if (Settings.PrimaryHue >= 0)
        {
            RgbToHsl(primary, out _, out double sat, out _);
            sat = Math.Clamp(sat, 0.35, 0.9);
            double baseL = t.IsDark ? 0.58 : 0.48;   // 深色底亮一点、亮色底暗一点,保证对比
            primary = HslToRgb(Settings.PrimaryHue, sat, baseL);
            primaryHover = HslToRgb(Settings.PrimaryHue, sat, Math.Min(baseL + 0.08, 0.9));
        }

        // 原地改写令牌画刷 → DynamicResource 全局即时生效
        SetBrush("T.Background", ParseColor(c.Background));
        // 卡片透明度:用户覆盖 > 主题默认,作用于全部卡片/悬浮面令牌
        double cardAlpha = ResolveCardOpacity();
        SetBrush("T.Surface", WithAlpha(ParseColor(c.Surface), ToAlpha(cardAlpha)));
        SetBrush("T.SurfaceAlt", WithAlpha(ParseColor(c.SurfaceAlt), ToAlpha(cardAlpha)));
        SetBrush("T.Foreground", ParseColor(c.Foreground));
        SetBrush("T.ForegroundDim", ParseColor(c.ForegroundDim));
        SetBrush("T.Primary", primary);
        SetBrush("T.PrimaryHover", primaryHover);
        SetBrush("T.Border", ParseColor(c.Border));
        SetBrush("T.Success", ParseColor(c.Success));
        SetBrush("T.Warning", ParseColor(c.Warning));
        SetBrush("T.Danger", ParseColor(c.Danger));
        SetBrush("T.Overlay", ParseColor(c.Overlay));
        // 派生令牌:主色柔化填充(选中/悬停弱底) + 明暗相关的黑白透明填充
        SetBrush("T.PrimarySoft", WithAlpha(primary, 0x33));
        SetBrush("T.HoverFill", ParseColor(t.IsDark ? "#22FFFFFF" : "#14000000"));
        SetBrush("T.PressedFill", ParseColor(t.IsDark ? "#2EFFFFFF" : "#1F000000"));
        // 滚动条滑块令牌(原为硬编码灰,不跟随主题;深色白雾/亮色黑雾两档,悬停加深一档)
        SetBrush("T.ScrollThumb", ParseColor(t.IsDark ? "#59FFFFFF" : "#33000000"));
        SetBrush("T.ScrollThumbHover", ParseColor(t.IsDark ? "#8CFFFFFF" : "#4D000000"));
        // 毛玻璃雾色令牌(2026-09-26 批5:由 ShellWindow 硬编码常量迁入,取值与原先完全一致)
        SetBrush("T.FrostTint", ParseColor(t.IsDark ? "#1EFFFFFF" : "#4CFFFFFF"));

        ThemeChanged?.Invoke();
        ValidateContrast(t);
    }

    // ==================== 对比度校验(WCAG 相对亮度) ====================

    /// <summary>相对亮度(sRGB 线性化后加权)</summary>
    private static double RelativeLuminance(Color c)
    {
        static double Chan(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Chan(c.R) + 0.7152 * Chan(c.G) + 0.0722 * Chan(c.B);
    }

    /// <summary>两色对比度比值(1~21,正文可读性建议 ≥ 4.5)</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>应用主题后校验关键文字对比度,不达标的组合记录告警日志(预设均已人工校验达标)</summary>
    private static void ValidateContrast(ThemeDefinition t)
    {
        try
        {
            var c = t.Colors;
            var fg = ParseColor(c.Foreground);
            var bg = ParseColor(c.Surface);
            double ratio = ContrastRatio(fg, bg);
            if (ratio < 4.5)
                NextLog.Warn($"[主题] 对比度告警: {t.Name} 主文字/卡片底对比 {ratio:0.0}:1(< 4.5),可能影响可读性");
            double dimRatio = ContrastRatio(ParseColor(c.ForegroundDim), bg);
            if (dimRatio < 3.0)
                NextLog.Warn($"[主题] 对比度告警: {t.Name} 辅助文字对比 {dimRatio:0.0}:1(< 3.0)");
        }
        catch { /* 校验失败不影响主题应用 */ }
    }

    private static byte ToAlpha(double opacity) => (byte)Math.Round(Math.Clamp(opacity, 0.6, 1.0) * 255);

    private static void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();   // 冻结:跨线程安全 + 免变更跟踪开销
        TokenDict[key] = brush;
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>颜色解析容错(非法值回退灰)</summary>
    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromRgb(0x80, 0x80, 0x80); }
    }

    // ==================== HSL 主色参数化 ====================

    /// <summary>设置自定义主色色相:重算色阶 + 全局刷新(落盘防抖,拖动不卡)</summary>
    public static void SetPrimaryHue(double hue)
    {
        Settings.PrimaryHue = Math.Clamp(hue, 0, 360);
        Apply(Current);
        RequestSave();
    }

    /// <summary>恢复预设主题主色并持久化</summary>
    public static void ResetPrimaryHue()
    {
        Settings.PrimaryHue = -1;
        Apply(Current);
        SaveSettings();
    }

    // ==================== 背景模糊 / 卡片透明度 / 显示模式 / 亮度 ====================

    /// <summary>生效背景模糊度:用户覆盖 > 主题默认(0~24)</summary>
    public static double ResolveBlur()
        => Math.Clamp(Settings.BackgroundBlur >= 0 ? Settings.BackgroundBlur : Current.BackgroundBlur, 0, 24);

    /// <summary>生效卡片透明度:用户覆盖 > 主题默认(0.6~1.0)</summary>
    public static double ResolveCardOpacity()
        => Math.Clamp(Settings.CardOpacity > 0 ? Settings.CardOpacity : Current.CardOpacity, 0.6, 1.0);

    /// <summary>生效背景显示模式(Fill/Fit/Tile,非法值回退 Fill)</summary>
    public static string ResolveBackgroundMode()
        => Settings.BackgroundMode is "Fit" or "Tile" ? Settings.BackgroundMode : "Fill";

    /// <summary>生效背景亮度(0.4~1.0)</summary>
    public static double ResolveBrightness()
        => Math.Clamp(Settings.BackgroundBrightness, 0.4, 1.0);

    /// <summary>设置背景模糊度(即时生效;落盘防抖)</summary>
    public static void SetBackgroundBlur(double blur)
    {
        Settings.BackgroundBlur = Math.Clamp(blur, 0, 24);
        ThemeChanged?.Invoke();   // 模糊只影响背景层,无需重建令牌
        RequestSave();
    }

    /// <summary>设置卡片透明度(需重建画刷 → Apply;落盘防抖)</summary>
    public static void SetCardOpacity(double opacity)
    {
        Settings.CardOpacity = Math.Clamp(opacity, 0.6, 1.0);
        Apply(Current);
        RequestSave();
    }

    /// <summary>设置背景显示模式(通知背景层刷新;落盘防抖)</summary>
    public static void SetBackgroundMode(string mode)
    {
        Settings.BackgroundMode = mode is "Fit" or "Tile" ? mode : "Fill";
        ThemeChanged?.Invoke();
        RequestSave();
    }

    /// <summary>设置背景亮度(通知背景层刷新;落盘防抖)</summary>
    public static void SetBackgroundBrightness(double brightness)
    {
        Settings.BackgroundBrightness = Math.Clamp(brightness, 0.4, 1.0);
        ThemeChanged?.Invoke();
        RequestSave();
    }

    /// <summary>设置自定义背景图路径(空 = 恢复主题默认;持久化 + 刷新)</summary>
    public static void SetCustomBackground(string path)
    {
        Settings.CustomBackgroundPath = path ?? "";
        ThemeChanged?.Invoke();
        SaveSettings();
    }

    // ==================== 主题配置导入 / 导出(分享) ====================

    /// <summary>导出当前主题定义为 json(含配色/背景/默认模糊与卡片透明度)</summary>
    public static string ExportThemeJson(ThemeDefinition? theme = null)
        => JsonSerializer.Serialize(theme ?? Current, JsonOpt);

    /// <summary>导入主题 json:校验 → 注册为自定义主题 → 应用并持久化。失败返回 false + 错误描述</summary>
    public static bool ImportThemeJson(string json, out string error)
    {
        error = "";
        try
        {
            var t = JsonSerializer.Deserialize<ThemeDefinition>(json, JsonOpt);
            if (t == null || t.Colors == null) { error = "配置内容无效:缺少主题配色定义"; return false; }
            if (string.IsNullOrWhiteSpace(t.Id)) t.Id = "custom-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            if (string.IsNullOrWhiteSpace(t.Name)) t.Name = t.Id;
            if (Presets().Any(p => p.Id == t.Id)) t.Id += "-import";   // 禁止覆盖内置预设
            t.IsCustom = true;

            All.RemoveAll(x => x.Id == t.Id && x.IsCustom);
            All.Add(t);
            Apply(t);
            Settings.ThemeId = t.Id;
            SaveSettings();
            NextLog.Info($"[主题] 导入主题成功: {t.Name} ({t.Id})");
            return true;
        }
        catch (JsonException ex) { error = "配置文件格式损坏: " + ex.Message; return false; }
        catch (Exception ex) { error = "导入失败: " + ex.Message; return false; }
    }

    /// <summary>HSL → RGB(h:0~360,s/l:0~1)</summary>
    public static Color HslToRgb(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    /// <summary>RGB → HSL(反推预设主色饱和度用)</summary>
    public static void RgbToHsl(Color color, out double h, out double s, out double l)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        l = (max + min) / 2;
        if (d == 0) { h = 0; s = 0; return; }
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        h = max switch
        {
            _ when max == r => 60 * (((g - b) / d) % 6),
            _ when max == g => 60 * ((b - r) / d + 2),
            _ => 60 * ((r - g) / d + 4)
        };
        if (h < 0) h += 360;
    }

    // ==================== 设置持久化 ====================

    private static System.Threading.Timer? _saveTimer;
    private static readonly object SaveLock = new();

    /// <summary>防抖保存:滑块拖动高频变更合并为停止后 400ms 一次落盘,避免逐帧磁盘 IO 卡顿</summary>
    private static void RequestSave()
    {
        lock (SaveLock)
        {
            _saveTimer?.Dispose();
            _saveTimer = new System.Threading.Timer(_ => SaveSettings(), null, 400, System.Threading.Timeout.Infinite);
        }
    }

    private static void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) { Settings = new ThemeSettings(); return; }
            Settings = JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(SettingsPath), JsonOpt) ?? new ThemeSettings();
        }
        catch (Exception ex)
        {
            NextLog.Warn("[主题] 设置读取失败,回退默认: " + ex.Message);
            Settings = new ThemeSettings();
        }
    }

    /// <summary>原子保存主题设置(tmp → Flush → Move;加锁防防抖定时器并发写)</summary>
    public static void SaveSettings()
    {
        lock (SaveLock)
        {
            try
            {
                NextPaths.EnsureDir(NextPaths.Root);
                string tmp = SettingsPath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, Settings, JsonOpt);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, SettingsPath, overwrite: true);
            }
            catch (Exception ex) { NextLog.Error("[主题] 设置保存失败", ex); }
        }
    }

    /// <summary>切换主题并持久化(设置页入口)</summary>
    public static void SwitchAndPersist(string id)
    {
        if (!Apply(id)) return;
        Settings.ThemeId = id;
        SaveSettings();
    }

    // ==================== 背景 / 透明度解析 ====================

    /// <summary>生效背景图路径:用户自定义 > 主题自带(jm) > null(纯色)</summary>
    public static string? ResolveBackgroundPath()
    {
        if (!string.IsNullOrEmpty(Settings.CustomBackgroundPath) && File.Exists(Settings.CustomBackgroundPath))
            return Settings.CustomBackgroundPath;
        if (!string.IsNullOrEmpty(Current.BackgroundImage))
        {
            string p = Path.Combine(ImageAssets.ThemeDir, Current.BackgroundImage);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>生效窗口不透明度(用户覆盖优先,钳位 0.6~1.0)</summary>
    public static double ResolveOpacity()
        => Math.Clamp(Settings.WindowOpacity > 0 ? Settings.WindowOpacity : Current.WindowOpacity, 0.6, 1.0);
}
