// MainWindow.xaml.cs — 主窗口交互逻辑
// 可爱的芙芙 - 主窗口(底部 Dock 导航版)
//
// 职责:
// 1. 承载标题栏(自绘无边框窗口的最小化/最大化/关闭、拖动)
// 2. 底部 Dock 导航胶囊切换页面
// 3. 页面调度:页面实例缓存(ViewModel 为单例,页面不反复重建),
//    动作项(日志控制台/环境自检)走弹窗,不改变内容区
// 4. 启动时触发环境自检与静默更新检测
// 5. 承载视频/图片背景层

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using FufuLauncher.Services;
using FufuLauncher.ViewModels;
using FufuLauncher.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher;

public partial class MainWindow : Window
{
    private readonly ThemeService _themeService;
    private readonly EnvironmentCheckService _envCheckService;
    private readonly NavigationViewModel _navVm;

    /// <summary>页面实例缓存:导航跳转复用同一 Page,防止反复新建导致内存暴涨</summary>
    private readonly Dictionary<string, Page> _pageCache = new();

    /// <summary>供 XAML 绑定导航 VM</summary>
    public NavigationViewModel NavVm => _navVm;

    public MainWindow(MainViewModel mainViewModel,
                       ThemeService themeService,
                       EnvironmentCheckService envCheckService,
                       NavigationViewModel navVm)
    {
        InitializeComponent();
        DataContext = mainViewModel;
        _themeService = themeService;
        _envCheckService = envCheckService;
        _navVm = navVm;
        _navVm.NavigationRequested += OnNavigationRequested;
        ApplyBrandLogo();
    }

    /// <summary>
    /// 加载专属应用 Logo(tupian\tub.png)到标题栏与侧边栏。
    /// 成功时清空占位底色(避免透明 PNG 透出背景色);失败时保留原 LogoBrush 占位,不弹错不崩溃。
    /// </summary>
    private void ApplyBrandLogo()
    {
        try
        {
            var logo = BrandAssets.GetLogo();
            if (logo == null) return; // 沿用内置占位样式
            TitleLogoImage.Source = logo;
            SidebarLogoImage.Source = logo;
            // 透明背景正常渲染:去掉占位底色,防止 PNG 透明区域透出绿色块
            TitleLogoBox.Background = Brushes.Transparent;
            if (SidebarLogoImage.Parent is Border sidebarLogoBox)
                sidebarLogoBox.Background = Brushes.Transparent;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[品牌] 主窗口 Logo 应用失败,保留占位样式:{ex.Message}");
        }
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        // 窗口淡入动画(300ms,从透明到不透明)
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fadeIn);

        // 默认导航到主页
        NavigateToPage("Home");

        // 加载主题与背景
        _themeService.ApplyTheme();
        _themeService.Init(BackgroundLayer);

        // 启动环境自检(异步,不阻塞 UI)
        try
        {
            await _envCheckService.RunEnvironmentCheckAsync();
            if (!IsLoaded) return;
            StatusBarText.Text = _envCheckService.GetStatusSummary();

            // 有失败项时弹出检测窗口(可一键跳转下载)
            if (!_envCheckService.LastResult.AllOk)
            {
                var dialog = new Views.EnvironmentCheckDialog(_envCheckService)
                {
                    Owner = this
                };
                dialog.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            if (IsLoaded)
            {
                StatusBarText.Text = $"环境自检失败:{ex.Message}";
            }
        }

        // 启动时可选自动检测新版本(静默,不打断用户;发现更新时仅在状态栏提示)
        try
        {
            var cfg = App.Services.GetRequiredService<ConfigService>();
            if (cfg.Config.AutoCheckUpdate && !string.IsNullOrWhiteSpace(cfg.Config.UpdateManifestUrl))
            {
                var upd = App.Services.GetRequiredService<UpdateService>();
                var r = await upd.CheckForUpdateAsync();
                if (IsLoaded && r.Ok && r.HasUpdate && r.Manifest != null)
                    StatusBarText.Text = $"发现新版本 v{r.Manifest.Version},可前往 设置→程序更新";
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[更新] 启动时自动检测更新异常:{ex.Message}");
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
            _themeService.PauseVideo();
        else
            _themeService.ResumeVideo();
    }

    protected override void OnClosed(EventArgs e)
    {
        _navVm.NavigationRequested -= OnNavigationRequested;
        _themeService.Shutdown();
        base.OnClosed(e);
    }

    #region 导航调度

    private void NavItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
            _navVm.RequestNavigate(key);
    }

    private void OnNavigationRequested(string key) => NavigateToPage(key);

    public void NavigateToPage(string pageName)
    {
        // 防御:窗口未完全初始化时(如 XAML 解析阶段)跳过导航
        if (!IsInitialized || MainFrame == null) return;

        // ===== 动作项:弹窗类,不切换右侧内容区 =====
        switch (pageName)
        {
            case "Logs":
                OpenLogConsole();
                _navVm.RestorePageSelection();
                return;
            case "EnvCheck":
                OpenEnvCheckAsync();
                _navVm.RestorePageSelection();
                return;
        }

        // ===== 页面项:切换 Frame 内容(不重建主窗口)=====
        // 离开设置页时执行其生命周期契约(停止防抖定时器 + 暂停内存监控)
        if (MainFrame.Content is SettingsPage leavingSettings && pageName != "Settings")
        {
            try { leavingSettings.Dispose(); } catch { /* 忽略释放异常 */ }
        }

        Page page;
        if (pageName == "Settings")
        {
            // 设置页维持既有生命周期契约(构造时恢复内存监控、Dispose 时暂停),不做缓存
            page = App.Services.GetRequiredService<SettingsPage>();
        }
        else
        {
            // 页面实例缓存:ViewModel 为单例,页面复用不反复新建
            if (!_pageCache.TryGetValue(pageName, out var cached))
            {
                cached = ResolvePage(pageName);
                _pageCache[pageName] = cached;
            }
            page = cached;
        }

        MainFrame.Content = page;

        // 页面切换动画:淡入 + 轻微上滑(220ms,CubicEase)
        page.Opacity = 0;
        var slideDuration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = slideDuration,
            EasingFunction = ease
        };

        var translate = new TranslateTransform(0, 8);
        page.RenderTransform = translate;
        var slideUp = new DoubleAnimation
        {
            From = 8,
            To = 0,
            Duration = slideDuration,
            EasingFunction = ease
        };

        page.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        translate.BeginAnimation(TranslateTransform.YProperty, slideUp);

        _navVm.SetSelected(pageName);

        if (StatusBarText != null)
            StatusBarText.Text = pageName switch
            {
                "Home" => "已切换到:主页",
                "Mods" => "已切换到:模组管理",
                "Market" => "已切换到:在线市场",
                "Java" => "已切换到:Java 管理",
                "Download" => "已切换到:下载中心",
                "Manage" => "已切换到:版本管理",
                "Settings" => "已切换到:设置",
                "About" => "已切换到:关于",
                _ => "已切换页面"
            };
    }

    /// <summary>从 DI 容器解析页面实例(页面 Transient,ViewModel/Service 单例自动注入)</summary>
    private static Page ResolvePage(string pageName)
    {
        var services = App.Services;
        return pageName switch
        {
            "Home" => services.GetRequiredService<HomePage>(),
            "Mods" => services.GetRequiredService<ModsPage>(),
            "Market" => services.GetRequiredService<MarketPage>(),
            "Java" => services.GetRequiredService<JavaRuntimePage>(),
            "Download" => services.GetRequiredService<DownloadPage>(),
            "Manage" => services.GetRequiredService<ManageVersionsPage>(),
            "About" => services.GetRequiredService<AboutPage>(),
            _ => services.GetRequiredService<HomePage>()
        };
    }

    /// <summary>日志控制台:弹出全新双栏日志查看器(应用日志/游戏日志),单例防堆叠</summary>
    private void OpenLogConsole()
    {
        try
        {
            var gameLog = App.Services.GetRequiredService<GameLogService>();
            LogViewerWindow.ShowSingle(gameLog, this);
            StatusBarText.Text = "已打开:日志控制台";
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[导航] 打开日志控制台失败:{ex.Message}");
            MessageBox.Show("打开日志控制台失败,请检查文件权限。", "可爱的芙芙",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>环境自检:重新执行检测并弹出结果窗口(警告/致命分级)</summary>
    private async void OpenEnvCheckAsync()
    {
        try
        {
            StatusBarText.Text = "环境自检中…";
            await _envCheckService.RunEnvironmentCheckAsync();
            if (!IsLoaded) return;

            var dialog = new EnvironmentCheckDialog(_envCheckService) { Owner = this };
            dialog.ShowDialog();
            StatusBarText.Text = _envCheckService.GetStatusSummary();
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[导航] 环境自检失败:{ex.Message}");
            if (IsLoaded)
            {
                StatusBarText.Text = "环境自检失败";
                MessageBox.Show("环境自检过程中出现异常,请查看日志了解详情。", "可爱的芙芙",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    #endregion

    #region 标题栏交互

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else
        {
            DragMove();
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        // 资源释放由应用退出时的 DI 容器统一处理,这里仅关闭窗口
        Close();
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Normal)
        {
            WindowState = WindowState.Maximized;
            BtnMaximize.Content = "❐";
        }
        else
        {
            WindowState = WindowState.Normal;
            BtnMaximize.Content = "▢";
        }
    }

    #endregion
}
