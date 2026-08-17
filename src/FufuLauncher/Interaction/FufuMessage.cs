// Interaction/FufuMessage.cs — 统一消息/确认弹窗
// 可爱的芙芙
//
// 全部二级提示、确认弹窗统一走这里,抛弃系统 MessageBox,视觉与主程序一致。
// 无边框圆角 + 渐变标题栏 + DialogRoot 卡片壳。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FufuLauncher.Interaction;

public enum FufuMessageKind { Info, Success, Warn, Error, Question }

public static class FufuMessage
{
    /// <summary>普通信息/结果提示</summary>
    public static void Show(Window? owner, string title, string message,
        FufuMessageKind kind = FufuMessageKind.Info)
        => BuildAndShow(owner, title, message, kind, confirmOnly: true);

    public static void Info(Window? owner, string title, string message)
        => Show(owner, title, message, FufuMessageKind.Info);

    public static void Success(Window? owner, string title, string message)
        => Show(owner, title, message, FufuMessageKind.Success);

    public static void Warn(Window? owner, string title, string message)
        => Show(owner, title, message, FufuMessageKind.Warn);

    public static void Error(Window? owner, string title, string message)
        => Show(owner, title, message, FufuMessageKind.Error);

    /// <summary>确认弹窗:返回 true=用户点击了确认,false=取消/关闭</summary>
    public static bool Confirm(Window? owner, string title, string message,
        string okText = "确定", string cancelText = "取消", bool danger = false)
        => BuildAndShow(owner, title, message, FufuMessageKind.Question,
                        confirmOnly: false, okText: okText, cancelText: cancelText, danger: danger);

    private static bool BuildAndShow(Window? owner, string title, string message, FufuMessageKind kind,
        bool confirmOnly, string okText = "确定", string cancelText = "取消", bool danger = false)
    {
        bool result = false;

        var win = new Window
        {
            Title = "可爱的芙芙",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner ?? Application.Current?.MainWindow,
            MaxWidth = 480,
            ShowInTaskbar = false
        };

        var (icon, iconColor) = kind switch
        {
            FufuMessageKind.Success => ("✅", "#FF4CC38A"),
            FufuMessageKind.Warn => ("⚠", "#FFF5A524"),
            FufuMessageKind.Error => ("⛔", "#FFF0524D"),
            FufuMessageKind.Question => ("❔", "#FF5B8DEF"),
            _ => ("💬", "#FF5B8DEF")
        };

        // 渐变标题栏(可拖动 + 关闭按钮)
        var titleBar = new Border { Style = (Style)win.FindResource("DialogTitleBar") };
        var titleGrid = new Grid { Margin = new Thickness(16, 0, 16, 0) };
        var titleText = new TextBlock
        {
            Text = $"{icon}  {title}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        var closeBtn = new Button
        {
            Content = "✕",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
            Style = (Style)win.FindResource("GhostButton"),
            Width = 28,
            Height = 28
        };
        closeBtn.Click += (_, _) => win.DialogResult = false;
        titleGrid.Children.Add(titleText);
        titleGrid.Children.Add(closeBtn);
        titleBar.Child = titleGrid;
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) win.DragMove(); };

        // 内容区:图标 + 消息文本
        var body = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var msgBox = new TextBlock
        {
            Text = message,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 400,
            LineHeight = 21
        };
        msgBox.SetResourceReference(TextBlock.ForegroundProperty, "AppForeground");
        body.Children.Add(msgBox);

        // 按钮区
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var okBtn = new Button
        {
            Content = okText,
            MinWidth = 88,
            Margin = confirmOnly ? new Thickness(0) : new Thickness(0, 0, 10, 0),
            IsDefault = true,
            Style = danger
                ? (Style)win.FindResource("DangerButton")
                : (Style)win.FindResource("PrimaryButton")
        };
        okBtn.Click += (_, _) => { result = true; win.DialogResult = true; };
        btnPanel.Children.Add(okBtn);

        if (!confirmOnly)
        {
            var cancelBtn = new Button
            {
                Content = cancelText,
                MinWidth = 88,
                IsCancel = true,
                Style = (Style)win.FindResource("SecondaryButton")
            };
            cancelBtn.Click += (_, _) => win.DialogResult = false;
            btnPanel.Children.Add(cancelBtn);
        }
        body.Children.Add(btnPanel);

        var root = new Border
        {
            Style = (Style)win.FindResource("DialogRoot"),
            Padding = new Thickness(0),
            MinWidth = 340
        };
        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(body, 1);
        outer.Children.Add(titleBar);
        outer.Children.Add(body);
        // 内容区四周留白(标题栏全宽渐变)
        body.Margin = new Thickness(20, 18, 20, 20);
        root.Child = outer;

        var shell = new Border { Margin = new Thickness(10), Child = root };
        win.Content = shell;

        try { win.ShowDialog(); }
        catch { /* 父窗口状态异常时忽略 */ }
        return result;
    }
}
