// FufuLauncher - 主题数据模型(全量重构版)
// Copyright © FufuLauncher
//
// ThemeDefinition:一套主题的完整定义(配色令牌 + 背景图 + 透明度)。
// ThemeSettings:用户主题选择的持久化模型(theme.settings.json,原子写)。
// System.Text.Json 序列化,缺字段走默认值,旧文件向后兼容。

using System.Text.Json.Serialization;

namespace FufuLauncher.Theme;

/// <summary>主题配色令牌集(键与 ThemeManager 写入资源字典的 T.* 一一对应)</summary>
public sealed class ThemeColors
{
    public string Background { get; set; } = "#101418";      // 窗口底色
    public string Surface { get; set; } = "#1A2027";         // 卡片面
    public string SurfaceAlt { get; set; } = "#232B35";      // 悬浮面/输入框底
    public string Foreground { get; set; } = "#E8ECF1";      // 主文字
    public string ForegroundDim { get; set; } = "#8A93A0";   // 辅助文字
    public string Primary { get; set; } = "#4C8DFF";         // 主色
    public string PrimaryHover { get; set; } = "#6BA1FF";    // 主色悬停
    public string Border { get; set; } = "#2A323D";          // 描边
    public string Success { get; set; } = "#3FB27F";
    public string Warning { get; set; } = "#E0A93E";
    public string Danger { get; set; } = "#E25D5D";
    public string Overlay { get; set; } = "#990B0E12";       // 背景图遮罩(保证前景可读)
}

/// <summary>主题定义(内置预设与外部 tupian\jm\*.json 共用此模型)</summary>
public sealed class ThemeDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDark { get; set; } = true;
    public ThemeColors Colors { get; set; } = new();
    /// <summary>背景图文件名(相对 tupian\jm);空 = 纯色背景</summary>
    public string BackgroundImage { get; set; } = "";
    /// <summary>窗口不透明度 0.6~1.0</summary>
    public double WindowOpacity { get; set; } = 1.0;
    /// <summary>主题默认背景模糊度(0~24,0 = 不模糊)</summary>
    public double BackgroundBlur { get; set; }
    /// <summary>主题默认卡片透明度 0.6~1.0(1 = 不透明)</summary>
    public double CardOpacity { get; set; } = 1.0;

    /// <summary>是否来自外部自定义文件(预设恒为 false)</summary>
    [JsonIgnore]
    public bool IsCustom { get; set; }
}

/// <summary>主题设置持久化模型(theme.settings.json)</summary>
public sealed class ThemeSettings
{
    /// <summary>当前主题 ID(预设或自定义,默认沉浸式暖橙旗舰主题)</summary>
    public string ThemeId { get; set; } = "aura";
    /// <summary>窗口不透明度用户覆盖(0 = 跟随主题定义)</summary>
    public double WindowOpacity { get; set; }
    /// <summary>用户自定义背景图绝对路径(空 = 跟随主题背景)</summary>
    public string CustomBackgroundPath { get; set; } = "";
    /// <summary>自定义主色色相 0~360(-1 = 跟随预设主题主色)</summary>
    public double PrimaryHue { get; set; } = -1;
    /// <summary>背景模糊度用户覆盖(-1 = 跟随主题默认值)</summary>
    public double BackgroundBlur { get; set; } = -1;
    /// <summary>卡片透明度用户覆盖(-1 = 跟随主题默认值,其余 0.6~1.0)</summary>
    public double CardOpacity { get; set; } = -1;
    /// <summary>背景显示模式:Fill(填充) / Fit(适应) / Tile(平铺)</summary>
    public string BackgroundMode { get; set; } = "Fill";
    /// <summary>背景亮度 0.4~1.0(1 = 原亮度)</summary>
    public double BackgroundBrightness { get; set; } = 1.0;
}
