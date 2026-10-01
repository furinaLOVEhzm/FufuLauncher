// FufuLauncher - 统一弹窗体系
// Copyright © FufuLauncher
//
// 全部弹窗自绘并严格服从主题令牌(T.* DynamicResource):
// Info / Error / Confirm / Input 四类,视觉与主界面完全一致 ——
// 大圆角卡片 + 标题左侧语义色竖条 + 柔阴影,不出现系统 MessageBox 的割裂风格。

using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using FufuLauncher.Next.Foundation;

namespace FufuLauncher.Next.UI;

public static class DialogKit
{
    /// <summary>信息提示</summary>
    public static void Info(string message, string title = "提示", Window? owner = null)
        => ShowMessage(title, message, "T.Primary", "ℹ", owner);

    /// <summary>错误提示(同步写日志便于回溯)</summary>
    public static void Error(string message, string title = "出错了", Window? owner = null)
    {
        NextLog.Warn("[弹窗] " + message);
        ShowMessage(title, message, "T.Danger", "✕", owner);
    }

    /// <summary>成功提示(绿色语义竖条)</summary>
    public static void Success(string message, string title = "完成", Window? owner = null)
        => ShowMessage(title, message, "T.Success", "✓", owner);

    /// <summary>警告提示(黄色语义竖条)</summary>
    public static void Warn(string message, string title = "注意", Window? owner = null)
        => ShowMessage(title, message, "T.Warning", "⚠", owner);

    /// <summary>确认对话框(是/否)</summary>
    public static bool Confirm(string message, string title = "请确认", Window? owner = null)
        => ConfirmCore(message, title, "确定", "取消", danger: false, owner);

    /// <summary>确认对话框(自定义按钮文案;danger 用红色警示竖条,否则主色)</summary>
    public static bool Confirm(string message, string title, string okText, bool danger, Window? owner = null)
        => ConfirmCore(message, title, okText, "取消", danger, owner);

    private static bool ConfirmCore(string message, string title, string okText, string cancelText, bool danger, Window? owner)
    {
        bool result = false;
        var win = CreateShell(title, danger ? "T.Danger" : "T.Primary", owner, out var body);

        var msg = UIKit.Text(message, 13);
        msg.TextWrapping = TextWrapping.Wrap;
        msg.MaxWidth = 420;

        var yes = DialogButton(okText, true, () => { result = true; CloseWin(win, true); });
        var no = DialogButton(cancelText, false, () => CloseWin(win, false));
        var btnRow = UIKit.H(no, yes);
        btnRow.HorizontalAlignment = HorizontalAlignment.Right;
        btnRow.Margin = new Thickness(0, 24, 0, 0);

        body.Child = UIKit.V(msg, btnRow);
        try { win.ShowDialog(); } catch { /* 宿主关闭等极端情况按取消处理 */ }
        return result;
    }

    /// <summary>输入对话框:返回用户输入(null = 取消)</summary>
    public static string? Input(string prompt, string title, string placeholder = "", Window? owner = null)
    {
        string? result = null;
        var win = CreateShell(title, "T.Primary", owner, out var body);

        var msg = UIKit.Sub(prompt, 12.5);
        msg.TextWrapping = TextWrapping.Wrap;
        msg.MaxWidth = 420;

        var box = UIKit.TextBox(placeholder: placeholder);
        box.MinWidth = 300;
        box.Margin = new Thickness(0, 14, 0, 0);

        var ok = DialogButton("确定", true, () => { result = box.Text.Trim(); CloseWin(win, true); });
        var cancel = DialogButton("取消", false, () => CloseWin(win, false));
        var btnRow = UIKit.H(cancel, ok);
        btnRow.HorizontalAlignment = HorizontalAlignment.Right;
        btnRow.Margin = new Thickness(0, 20, 0, 0);

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { result = box.Text.Trim(); CloseWin(win, true); }
            if (e.Key == Key.Escape) CloseWin(win, false);
        };

        body.Child = UIKit.V(msg, box, btnRow);
        try { win.ShowDialog(); box.Focus(); } catch { /* 同上 */ }
        return result;
    }

    /// <summary>多选一对话框:返回选中项下标(-1 = 取消)</summary>
    public static int Choice(string message, string[] options, string title = "请选择", Window? owner = null)
    {
        int result = -1;
        var win = CreateShell(title, "T.Primary", owner, out var body);

        var msg = UIKit.Sub(message, 12.5);
        msg.TextWrapping = TextWrapping.Wrap;
        msg.MaxWidth = 420;

        var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        for (int i = 0; i < options.Length; i++)
        {
            int idx = i;
            var b = DialogButton(options[i], primary: false, () => { result = idx; CloseWin(win, true); });
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (i > 0) b.Margin = new Thickness(0, 8, 0, 0);
            list.Children.Add(b);
        }

        var cancel = DialogButton("取消", false, () => CloseWin(win, false));
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Margin = new Thickness(0, 16, 0, 0);

        body.Child = UIKit.V(msg, list, cancel);
        try { win.ShowDialog(); } catch { /* 同上 */ }
        return result;
    }

    /// <summary>列表选择对话框(版本手动指定等长列表场景):返回选中项(null = 取消)</summary>
    public static string? PickFromList(string message, System.Collections.Generic.IReadOnlyList<string> items,
                                       string title = "请选择", Window? owner = null)
    {
        string? result = null;
        var win = CreateShell(title, "T.Primary", owner, out var body);

        var msg = UIKit.Sub(message, 12.5);
        msg.TextWrapping = TextWrapping.Wrap;
        msg.MaxWidth = 420;

        var lb = new ListBox
        {
            MinWidth = 380,
            MaxHeight = 320,
            Margin = new Thickness(0, 14, 0, 0),
            BorderThickness = new Thickness(1)
        };
        lb.SetResourceReference(ListBox.BackgroundProperty, "T.SurfaceAlt");
        lb.SetResourceReference(ListBox.BorderBrushProperty, "T.Border");
        lb.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "T.Foreground");
        // 列表项令牌化样式:悬停柔底 + 选中主色柔底(对齐下拉框列表项规格)
        var itemStyle = new System.Windows.Style(typeof(System.Windows.Controls.ListBoxItem));
        itemStyle.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, UIKit.DR("T.Foreground")));
        itemStyle.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        itemStyle.Triggers.Add(new System.Windows.Trigger
        {
            Property = System.Windows.Controls.ListBoxItem.IsMouseOverProperty,
            Value = true,
            Setters = { new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, UIKit.DR("T.HoverFill")) }
        });
        itemStyle.Triggers.Add(new System.Windows.Trigger
        {
            Property = System.Windows.Controls.ListBoxItem.IsSelectedProperty,
            Value = true,
            Setters = { new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, UIKit.DR("T.PrimarySoft")) }
        });
        lb.ItemContainerStyle = itemStyle;

        foreach (var item in items)
            lb.Items.Add(new ListBoxItem { Content = item, Padding = new Thickness(10, 6, 10, 6) });
        lb.MouseDoubleClick += (_, _) =>
        {
            if (lb.SelectedItem is ListBoxItem { Content: string s }) { result = s; CloseWin(win, true); }
        };

        var ok = DialogButton("确定", true, () =>
        {
            if (lb.SelectedItem is ListBoxItem { Content: string s }) { result = s; CloseWin(win, true); }
        });
        var cancel = DialogButton("取消", false, () => CloseWin(win, false));
        var btnRow = UIKit.H(cancel, ok);
        btnRow.HorizontalAlignment = HorizontalAlignment.Right;
        btnRow.Margin = new Thickness(0, 16, 0, 0);

        body.Child = UIKit.V(msg, lb, btnRow);
        try { win.ShowDialog(); } catch { /* 同上 */ }
        return result;
    }

    /// <summary>进度窗:异步任务执行期间展示状态文案 + 进度动画(模态)。
    /// 任务内异常在窗口关闭后原样抛出,由调用方统一中文提示;关闭按钮仅提前隐藏窗口,任务继续走完并如实返回结果</summary>
    public static T RunWithProgress<T>(string title, Func<Action<string>, Task<T>> work, Window? owner = null)
    {
        var win = CreateShell(title, "T.Primary", owner, out var body);

        var status = UIKit.Sub("准备中…", 12.5);
        status.TextWrapping = TextWrapping.Wrap;
        status.MaxWidth = 420;

        var bar = new ProgressBar
        {
            Height = 6,
            IsIndeterminate = true,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 18, 0, 4)
        };
        bar.SetResourceReference(ProgressBar.ForegroundProperty, "T.Primary");
        bar.SetResourceReference(ProgressBar.BackgroundProperty, "T.SurfaceAlt");

        var tip = UIKit.Sub("导入过程中请勿关闭启动器", 11);

        body.Child = UIKit.V(status, bar, tip);

        T result = default!;
        Exception? error = null;
        var task = Task.Run(async () =>
        {
            try
            {
                result = await work(s => win.Dispatcher.BeginInvoke(() => status.Text = s));
            }
            catch (Exception ex) { error = ex; }
        });
        // 任务完成 → 自动关窗(ShowDialog 返回)
        _ = task.ContinueWith(_ => win.Dispatcher.BeginInvoke(() =>
        {
            try { win.DialogResult = true; } catch { /* 窗口已被用户关闭 */ }
        }));

        try { win.ShowDialog(); } catch { /* 同上 */ }
        // 2026-09-25:用户提前关窗后不再 task.Wait() 硬阻塞 UI 线程(长任务期间界面会完全冻结)。
        // 改为 DispatcherFrame 消息泵:等待任务的同时持续处理 UI 消息,窗口保持可拖动/可响应
        while (!task.IsCompleted)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult(); // 统一取回异常,语义与 task.Wait() 等价
        if (error != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    // ==================== 内部:消息弹窗与窗口壳 ====================

    /// <summary>单按钮消息弹窗(Info/Error/Success/Warn 共用,统一语义图标徽标)</summary>
    private static void ShowMessage(string title, string message, string accentKey, string glyph, Window? owner)
    {
        var win = CreateShell(title, accentKey, owner, out var body);

        var msg = UIKit.Text(message, 13);
        msg.TextWrapping = TextWrapping.Wrap;
        msg.MaxWidth = 420;

        var ok = DialogButton("知道了", true, () => CloseWin(win, true));
        ok.Margin = new Thickness(0, 24, 0, 0);
        ok.HorizontalAlignment = HorizontalAlignment.Right;

        body.Child = UIKit.V(BuildMsgRow(glyph, accentKey, msg), ok);
        try { win.ShowDialog(); } catch (Exception ex) { NextLog.Warn("[弹窗] 弹窗异常降级: " + ex.Message); }
    }

    /// <summary>语义图标徽标(圆形软底 + 符号),四类弹窗统一视觉语言</summary>
    private static FrameworkElement BuildMsgRow(string glyph, string accentKey, TextBlock msg)
    {
        var chip = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(UIKit.R.Card),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        chip.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");
        if (chip.Child is TextBlock gt) gt.SetResourceReference(TextBlock.ForegroundProperty, accentKey);
        msg.VerticalAlignment = VerticalAlignment.Top;
        return UIKit.H(chip, msg);
    }

    /// <summary>弹窗统一按钮尺寸(高 36 / 宽 ≥ 88),四类弹窗按钮完全一致</summary>
    private static FrameworkElement DialogButton(string text, bool primary, Action onClick)
    {
        var b = UIKit.Button(text, primary: primary, onClick: onClick, height: 36);
        b.MinWidth = 88;
        return b;
    }

    /// <summary>弹窗关闭统一走退场动画(回缩+淡出后才置 DialogResult),打断安全</summary>
    private static void CloseWin(Window win, bool result)
    {
        if (win.Tag is FrameworkElement root)
            MotionKit.DialogExit(root, () => win.DialogResult = result);
        else
            win.DialogResult = result;
    }

    /// <summary>统一弹窗壳:无边框大圆角 + 令牌底 + 柔阴影 + 标题语义色竖条 + 关闭按钮;
    /// 动效:入场中心放大淡入,退场回缩淡出(从哪展开回哪去)</summary>
    private static Window CreateShell(string title, string accentKey, Window? owner, out Border body)
    {
        var win = new Window
        {
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI, sans-serif"),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false
        };
        win.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "T.Foreground");

        // 双层卡片结构:阴影独立层(避免 Effect 栅格化文字) + 内容层大圆角细边
        var shadowLayer = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Card),
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 0, Opacity = 0.4, RenderingBias = RenderingBias.Performance }
        };
        shadowLayer.SetResourceReference(Border.BackgroundProperty, "T.Surface");

        var card = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Card),
            BorderThickness = new Thickness(1)
        };
        card.WithRef("T.Surface", "T.Border");

        // 标题行:语义色竖条 + 标题 + 关闭按钮
        var accentBar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2, 10, 2) };
        accentBar.SetResourceReference(Border.BackgroundProperty, accentKey);
        var titleText = UIKit.Text(title, 15, FontWeights.SemiBold);
        titleText.VerticalAlignment = VerticalAlignment.Center;

        var closeGlyph = UIKit.Text("✕", 12, brushKey: "T.ForegroundDim");
        closeGlyph.VerticalAlignment = VerticalAlignment.Center;
        closeGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        var closeHover = new Border { CornerRadius = new CornerRadius(UIKit.R.Chip), Opacity = 0 };
        closeHover.SetResourceReference(Border.BackgroundProperty, "T.HoverFill");
        var closeBtn = UIKit.GhostButton(new Grid { Children = { closeHover, closeGlyph } }, () => CloseWin(win, false), height: 28);
        closeBtn.Width = 28;
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.MouseEnter += (_, _) => closeHover.FadeTo(1, UIKit.Fast);
        closeBtn.MouseLeave += (_, _) => closeHover.FadeTo(0, UIKit.Fast);

        var header = new Grid
        {
            Margin = new Thickness(0, 0, 0, 12),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { accentBar, titleText, closeBtn }
        };
        Grid.SetColumn(titleText, 1);
        Grid.SetColumn(closeBtn, 2);

        body = new Border { Padding = new Thickness(2, 0, 0, 2) };
        card.Child = new Border
        {
            Padding = new Thickness(26, 22, 26, 24),
            Child = UIKit.V(header, body)
        };

        // 拖动支持(无边框窗口)
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };

        var root = new Grid { Children = { shadowLayer, card } };
        win.Content = root;
        win.Tag = root;   // CloseWin 退场动画宿主

        // 入场动画:显示前预置隐藏态防闪,Loaded 后中心放大 + 淡入(轻微回弹)
        root.Opacity = 0;
        MotionKit.EnsureScale(root).ScaleX = MotionKit.EnsureScale(root).ScaleY = 0.94;
        win.Loaded += (_, _) => MotionKit.DialogEnter(root);
        return win;
    }
}
