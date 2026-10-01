// Copyright © FufuLauncher
//
// 新增功能面板共用的小工具:页签芯片、区块标题、紧凑按钮、目录打开。
// 全部基于 UIKit 既有控件工厂与 T.* 主题令牌拼装,不引入任何新的视觉样式,
// 保证新增面板与原有页面长得一模一样(圆角/间距/字号沿用既有规格)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FufuLauncher.Services;

namespace FufuLauncher.Next.UI;

internal static class PanelKit
{
    /// <summary>面板内按钮统一档位:32px 高度(与版本卡 / 模组卡行内按钮同规格)</summary>
    public const int BtnHeight = 32;

    /// <summary>面板内按钮工厂</summary>
    public static Button Btn(string text, bool primary, Action? onClick = null, double minWidth = 96)
    {
        var b = UIKit.Button(text, primary, onClick, height: BtnHeight);
        b.MinWidth = minWidth;
        return b;
    }

    /// <summary>页签芯片(下划线指示选中态),与版本设置面板同款形态</summary>
    public static (Border Chip, TextBlock Label) TabChip(string text)
    {
        var label = UIKit.Text(text, 13, FontWeights.SemiBold);
        var underline = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(2, 4, 2, 0)
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Child = UIKit.V(label, underline)
        };
        chip.Tag = underline;
        return (chip, label);
    }

    /// <summary>页签选中态:主色下划线 + 正常字色;未选中:透明下划线 + 暗字色</summary>
    public static void PaintChip(Border chip, bool active)
    {
        if (chip.Tag is Border underline)
        {
            if (active) underline.SetResourceReference(Border.BackgroundProperty, "T.Primary");
            else underline.Background = Brushes.Transparent;
        }
        if (chip.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock label)
            label.SetResourceReference(TextBlock.ForegroundProperty, active ? "T.Foreground" : "T.ForegroundDim");
    }

    /// <summary>区块小标题(14px 半粗) + 浅色说明</summary>
    public static StackPanel SectionHead(string title, string hint)
    {
        var h = UIKit.Sub(hint, 11.5);
        h.TextWrapping = TextWrapping.Wrap;
        h.Margin = new Thickness(0, 3, 0, 0);
        return UIKit.V(UIKit.Text(title, 14, FontWeights.Medium), h);
    }

    /// <summary>列表行底板(轻量面板,与模组行 / 下载任务行同款)</summary>
    public static Border Row(UIElement child, double pad = 12)
    {
        var b = UIKit.Panel(child, pad: pad);
        b.BorderThickness = new Thickness(1);
        b.Margin = new Thickness(0, 0, 0, 8);
        return b;
    }

    /// <summary>把行底板描边换成指定令牌(用于高亮:警示 / 危险 / 主色),传空则恢复默认</summary>
    public static void Tint(Border row, string? borderKey)
    {
        if (string.IsNullOrEmpty(borderKey))
        {
            row.BorderThickness = new Thickness(1);
            row.SetResourceReference(Border.BorderBrushProperty, "T.Border");
            return;
        }
        row.BorderThickness = new Thickness(1.4);
        row.SetResourceReference(Border.BorderBrushProperty, borderKey);
    }

    /// <summary>多行等宽文本块(日志 / 报告展示用),Consolas 便于对齐</summary>
    public static TextBox ReadOnlyBox(string text, double minHeight = 160)
    {
        var tb = UIKit.TextBox(text);
        tb.IsReadOnly = true;
        tb.AcceptsReturn = true;
        tb.TextWrapping = TextWrapping.NoWrap;
        tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        tb.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        tb.MinHeight = minHeight;
        tb.MaxHeight = 520;
        tb.FontFamily = new FontFamily("Consolas");
        tb.FontSize = 11.5;
        return tb;
    }

    /// <summary>打开文件夹(不存在时先建),失败只返回 false 不弹窗,由调用方决定提示</summary>
    public static bool OpenFolder(string dir)
    {
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[面板] 打开目录失败:{dir} → {ex.Message}");
            return false;
        }
    }

    /// <summary>侧边面板外壳:定宽 + 主卡片底色描边 + 内部可滚动</summary>
    public static Border SideShell(UIElement content, double width = 340)
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content
        };
        var shell = new Border
        {
            Width = width,
            CornerRadius = new CornerRadius(UIKit.R.Card),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Margin = new Thickness(14, 16, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = scroll
        };
        shell.WithRef("T.Surface", "T.Border");
        return shell;
    }

    /// <summary>侧边面板标题行:标题 + 收起按钮</summary>
    public static Grid SideHead(string title, Action onClose)
    {
        var t = UIKit.Text(title, 14, FontWeights.SemiBold);
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.VerticalAlignment = VerticalAlignment.Center;
        var close = UIKit.GhostButton(UIKit.Text("收起", 12, FontWeights.Medium, "T.ForegroundDim"), onClose, height: 28);
        close.Cursor = Cursors.Hand;
        close.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { t, close.Col(1) }
        };
        grid.Margin = new Thickness(0, 0, 0, 10);
        return grid;
    }

    /// <summary>键值说明行:左标签右值,超长自动截断并挂 ToolTip</summary>
    public static Grid KvRow(string key, string value, string? tone = null)
    {
        var k = UIKit.Sub(key, 11.5);
        k.VerticalAlignment = VerticalAlignment.Center;
        var v = UIKit.Text(value, 12);
        v.TextTrimming = TextTrimming.CharacterEllipsis;
        v.ToolTip = value;
        v.VerticalAlignment = VerticalAlignment.Center;
        if (!string.IsNullOrEmpty(tone)) v.SetResourceReference(TextBlock.ForegroundProperty, tone);
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(96) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            Margin = new Thickness(0, 3, 0, 3),
            Children = { k, v.Col(1) }
        };
        return grid;
    }

    /// <summary>分隔细线</summary>
    public static Border Divider(double top = 10, double bottom = 6)
    {
        var d = new Border
        {
            Height = 1,
            Margin = new Thickness(0, top, 0, bottom)
        };
        d.SetResourceReference(Border.BackgroundProperty, "T.Border");
        return d;
    }

    /// <summary>取当前实例的下拉选中项(未选返回 null)</summary>
    public static GameInstance? Selected(ComboBox box) => box.SelectedItem as GameInstance;
}
