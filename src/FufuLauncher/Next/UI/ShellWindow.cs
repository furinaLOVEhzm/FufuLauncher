// FufuLauncher - 壳窗口
// Copyright © FufuLauncher
//
// 设计语言(借鉴沉浸式玻璃拟态启动器):
// 1. 无边框圆角窗口(18px 圆角裁切)+ 自绘标题栏(品牌 + 最小化/关闭 + 拖拽移动);
//    四周/四角透明缩放手柄(Win32 WM_NCLBUTTONDOWN)保留原生拖拽缩放;
// 2. 背景三层结构:主题背景图(可选) → 亮度压暗 → T.Overlay 遮罩,全部服从圆角裁切;
// 3. 玻璃悬浮侧栏(双层阴影结构),导航选中态主色点缀描边;
// 4. 底部浮动状态胶囊(替代全宽状态栏),轻盈低干扰;
// 5. 订阅 ThemeManager.ThemeChanged:背景/透明度即时刷新,免重启切换;
// 6. 账号卡绑定 AccountService(DI 单例),AccountsChanged 即时刷新;
// 7. 窗口图标:外部 tub\logo.png 优先,内嵌资源兜底(ImageAssets)。

using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FufuLauncher.Services;
using FufuLauncher.Theme;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class ShellWindow : Window
{
    /// <summary>页面键(七类 Deck,实例/设置含子视图)</summary>
    public enum DeckKey { Home, Ai, Instances, Mods, Modpacks, Downloads, Accounts, Settings }

    /// <summary>一级菜单定义(图标为中性线性符号,禁萌系)</summary>
    private static readonly (DeckKey Key, string Icon, string Label, string[] SubItems)[] NavDefs =
    {
        (DeckKey.Home,      "◇", "主页",     Array.Empty<string>()),
        (DeckKey.Ai,        "◈", "泡芙助理", Array.Empty<string>()),
        (DeckKey.Instances, "▤", "版本管理", new[] { "本地版本", "新装版本", "版本设置", "版本工具" }),
        (DeckKey.Mods,      "▣", "模组管理", new[] { "管理模组", "下载模组" }),
        (DeckKey.Modpacks,  "◲", "整合包",   Array.Empty<string>()),
        (DeckKey.Downloads, "⇓", "下载中心", Array.Empty<string>()),
        (DeckKey.Accounts,  "◉", "账号",     Array.Empty<string>()),
        (DeckKey.Settings,  "✦", "设置",     new[] { "游戏设置", "Java 运行时", "路径设置", "网络设置", "外观设置", "下载设置", "高级设置", "泡芙助理" }),
    };

    /// <summary>窗口圆角半径(统一视觉锚点)</summary>
    private const double Radius = 18;

    private readonly Rectangle _bgRect = new();
    private readonly ImageBrush _bgBrush = new() { AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
    private readonly Border _base = new();          // 窗口底色(无背景图时的兼底,防透明露底)
    private readonly Border _dim = new() { Background = Brushes.Black };   // 亮度压暗层
    private readonly Border _overlay = new();
    private readonly Grid _clipHost = new();        // 背景层容器(圆角几何裁切)
    private Grid? _root;                            // 窗口根容器(启动整体淡入载体)
    private bool _backdropPending = true;           // 背景图/模糊是否推迟到首帧之后再上(启动提速)
    private Border? _chrome;                        // 窗口描边(最大化时圆角归零)
    private Grid? _grips;                           // 四周缩放手柄(最大化时停用)
    private TextBlock? _maxGlyph;                   // 最大化按钮图标(随窗口状态切 □/❐)
    private readonly StackPanel _navPanel = new();
    private readonly Grid _deckArea = new();
    private readonly TextBlock _statusText = UIKit.Sub("就绪", 11);
    private readonly Ellipse _statusDot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private Grid? _statusPill;   // 右下角状态胶囊,主页隐藏(用户要求)
    private Border? _statusPillBody;   // 胶囊内层(状态刷新时的轻微弹入载体)
    private long _statusPulseAt;       // 上次弹入时刻(节流:状态高频刷新时不反复回跳)
    private readonly TextBlock _accountText = UIKit.Text("未登录", 13, FontWeights.Medium);
    private readonly Image _accountAvatar = new()   // 玩家 3D 方块头颅头像(仅正版账号,像素风最近邻缩放)
    {
        Width = 68,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 4, 0)
    };
    private string _avatarToken = "";   // 头像异步加载对应的账号键(防切号后旧结果覆盖新头像)

    private readonly Dictionary<DeckKey, UserControl> _decks = new();
    private readonly Dictionary<DeckKey, (Border Pill, Border IconChip, TextBlock IconGlyph, Border Accent)> _navVisuals = new();
    private readonly Dictionary<DeckKey, System.Collections.Generic.List<Border>> _subGuides = new();   // 二级子菜单左侧引导线
    private readonly Dictionary<DeckKey, TextBlock[]> _subTexts = new();   // 二级子菜单文字(选中高亮用)
    private readonly Dictionary<DeckKey, int> _subSelection = new();       // 各菜单当前选中子项索引
    private readonly Stack<DeckKey> _history = new();   // 导航历史(页面栈,Alt+← 回退)
    private readonly Stack<(DeckKey Key, int Sub)> _subHistory = new();   // 2026-09-26 批4:子分区历史(同页 Alt+← 先退子分区)
    private bool _restoringSub;   // 历史回退触发的 SelectSub 不再入栈(防自吞)
    private readonly AccountService? _accountService;
    private readonly Border _sidebarPanel = new();      // 毛玻璃侧栏面板(裁切 + 坐标对齐载体)
    private readonly VisualBrush _frostBrush = new();   // 实时采样窗口背景层(毛玻璃透视)
    private readonly Rectangle _tintRect = new();       // 白雾染色层(雾蒙蒙质感)
    private readonly BlurEffect _bgBlur = new() { RenderingBias = RenderingBias.Performance };   // 背景模糊实例复用(避免每次主题刷新重建 Effect)
    private string? _bgPathKey;                         // 背景图缓存键:路径不变时免重复磁盘读取解码
    private BitmapImage? _bgBmp;
    private DeckKey _current = DeckKey.Home;
    private bool _startupEntered;   // 启动入场动画是否已消费(用户抢先切页则跳过,避免叠加跳动)
    private DateTime _statusAt = DateTime.MinValue;   // 状态文本写入时刻(切页防冲刷判定用)
    private bool _statusSticky;                       // 重要状态(下载完成等)短暂驻留,切页不立即清成「就绪」
    private readonly DownloadService? _downloadService;
    private TrayIconService? _tray;      // 托盘图标(惰性创建:首窗关窗时才建,不占启动路径)
    private bool _trayExit;             // 托盘菜单「退出启动器」已请求退出(放行 Closing)
    private bool _trayNotified;         // 首次缩托盘已弹过气泡,避免反复打扰

    public ShellWindow()
    {
        // 窗口构造分段计时(2026-08-29 启动提速排查):定位 7s+ 构造耗时的具体构成,稳定后可删
        var cw = System.Diagnostics.Stopwatch.StartNew();
        long cwLast = 0;
        void Stage(string s)
        {
            long now = cw.ElapsedMilliseconds;
            App.WriteAppLog($"[窗口计时] {s} +{now - cwLast}ms 累计{now}ms");
            cwLast = now;
        }
        // 全局字体统一(中英文混排时避免系统默认字体回落导致的显示参差)
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI, sans-serif");
        Title = "FufuLauncher";
        Width = 1220; Height = 780; MinWidth = 1040; MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = ImageAssets.ResolveLogo() as ImageSource;   // tub logo 优先,内嵌兜底

        // 无边框透明窗口:圆角由内容层几何裁切实现
        WindowStyle = WindowStyle.None;
        // 可缩放(CanResize):之前设 NoResize 时,四周透明手柄发的 WM_NCLBUTTONDOWN + HT* 命中码
        // 会被系统直接忽略 —— 这就是「窗口无法调整大小」的真正原因;手柄逻辑本身不用动
        ResizeMode = ResizeMode.CanResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        var root = BuildLayout();
        Content = root;
        _root = root;
        BuildNav();
        Stage("布局+导航构建");

        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;   // 退订防泄漏

        // 账号卡:绑定真实账号服务(构造时已从 accounts 目录加载)
        _accountService = App.Services.GetService<AccountService>();
        if (_accountService != null)
        {
            _accountService.AccountsChanged += RefreshAccountCard;
            Closed += (_, _) => _accountService.AccountsChanged -= RefreshAccountCard;
        }
        RefreshAccountCard();
        Stage("账号卡刷新");

        // 后台下载(2026-09-27):关闭窗口时若还有未完成任务则缩到托盘继续下,不退出进程。
        // 托盘图标本身惰性创建(首次关窗才建),此处只订阅下载状态用于刷新托盘提示;
        // 未启用托盘时代价为零(_tray 为 null 直接返回)。
        _downloadService = App.Services.GetService<DownloadService>();
        if (_downloadService != null)
            _downloadService.TaskStatusChanged += OnTrayDownloadStatusChanged;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            if (_downloadService != null) _downloadService.TaskStatusChanged -= OnTrayDownloadStatusChanged;
            _tray?.Dispose();
            _tray = null;
        };

        ApplyThemeVisual(deferBackdrop: true);   // 首帧只上底色/窗口不透明度,背景图+模糊推迟(见 OnFirstFrameRendered)
        UIKit.InstallScrollBarChrome(this);   // 细滚动条主题化(全局含弹窗下拉)
        Stage("主题视觉+滚动条");

        // Alt+← 回退上一页(页面栈简化版)
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Left && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                GoBack();
                e.Handled = true;
            }
        };

        // 拖拽本地图片 → 自定义背景(设置页另有按钮选择入口)
        AllowDrop = true;
        Drop += OnBackgroundDrop;

        SizeChanged += (_, _) => UpdateRoundClip();
        StateChanged += (_, _) => { UpdateRoundClip(); UpdateMaxGlyph(); };   // 最大化/还原时圆角裁切与毛玻璃采样同步重算
        Loaded += (_, _) =>
        {
            UpdateSidebarFrost();   // 首次布局完成后对齐毛玻璃采样坐标
            PlayStartupEntrance();  // 启动入场:整体柔和浮现 + 首个页面漂移落位(连贯一体)
            PreCreateDecks();       // 空闲预建其余页面:首切不阻塞,快速切页不掉帧
            // 背景图解码 + 大面积模糊栅格化成本移出启动关键路径:低优先级排队,等首帧落地后再补齐
            Dispatcher.BeginInvoke(DispatcherPriority.Background, OnFirstFrameRendered);
        };

        Switch(DeckKey.Home);
        Stage("首屏切换(含 HomeDeck 构建)");

        // 启动入场预置隐藏态(窗口显示前生效,防先闪后跳):整体内容 + 首屏页面在 Loaded 后同步浮现
        if (_root != null) _root.Opacity = 0;
        if (_decks.TryGetValue(_current, out var boot)) { boot.Opacity = 0; MotionKit.EnsureShift(boot).Y = 18; }
    }

    // ==================== 启动入场与预热 ====================

    /// <summary>首帧之后补齐主题背景:解码背景图 + 上模糊,并让背景柔和淡入。
    /// 好处有二:① 启动期不再为整屏模糊买单,窗口更快出现;
    /// ② 背景不再「一开就满屏」,而是随入场一起渐显,观感更软</summary>
    private void OnFirstFrameRendered()
    {
        if (!_backdropPending) return;
        _backdropPending = false;
        ApplyThemeVisual();                                  // 全量:背景图 + 模糊 + 压暗/遮罩显隐
        if (_bgRect.Visibility == Visibility.Visible)
        {
            _bgRect.Opacity = 0;
            MotionKit.Anim(_bgRect, UIElement.OpacityProperty, 1, MotionKit.Slow);
        }
        UpdateSidebarFrost();                                // 背景就位后重采样毛玻璃
    }

    /// <summary>启动入场编排:窗口整体柔和浮现 + 首个页面自下漂移落位(同一节拍,连贯一体);
    /// 若用户在 Loaded 前已抢先切页,则跳过补播,避免两段动画叠加跳动</summary>
    private void PlayStartupEntrance()
    {
        if (_startupEntered) return;
        _startupEntered = true;
        // 整体淡入:标题栏/侧栏/内容一体浮现,消除「窗口啪一下整个出现」的硬切换
        if (_root != null) MotionKit.Anim(_root, UIElement.OpacityProperty, 1, MotionKit.Slow);
        if (_decks.TryGetValue(_current, out var deck))
            MotionKit.PageEnter(deck, fromY: 18);
    }

    /// <summary>空闲预建其余 Deck(低优先级分片,不阻塞交互):把页面构建成本从切换瞬间移走后。
    /// 首次切页不再同步构建掉帧;预建产物与懒创建同路径,无额外副作用</summary>
    private void PreCreateDecks()
    {
        var rest = new Queue<DeckKey>(NavDefs.Select(n => n.Key).Where(k => !_decks.ContainsKey(k)));
        void Next()
        {
            if (rest.Count == 0) return;
            var k = rest.Dequeue();
            if (!_decks.ContainsKey(k))
            {
                var d = CreateDeck(k);
                _decks[k] = d;
            }
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Next);
        }
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Next);
    }

    // ==================== Win32 缩放手柄 ====================

    private static class Win32
    {
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int WM_GETMINMAXINFO = 0x24;
        public const uint MONITOR_DEFAULTTONEAREST = 0x2;
        public const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                         HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved; public POINT ptMaxSize; public POINT ptMaxPosition;
            public POINT ptMinTrackSize; public POINT ptMaxTrackSize;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        }
    }

    /// <summary>透明缩放手柄按下 → 交给系统非客户区缩放逻辑(保留原生拖拽手感)</summary>
    private void StartResize(int hitCode)
    {
        if (WindowState == WindowState.Maximized) return;   // 最大化下不允许边缘拖拽(先还原再调)
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Win32.ReleaseCapture();
        Win32.SendMessage(hwnd, Win32.WM_NCLBUTTONDOWN, (IntPtr)hitCode, IntPtr.Zero);
    }

    // ==================== 最大化钳制(无边框窗口必修)====================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        src?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_GETMINMAXINFO)
        {
            ClampToWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>把最大化尺寸/位置钳制到所在显示器的工作区:WindowStyle=None 的窗口默认会铺满整屏盖掉任务栏,
    /// 同时把最小拖拽尺寸按 DPI 换算后交给系统(MinWidth/MinHeight 是设备无关单位)</summary>
    private void ClampToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<Win32.MINMAXINFO>(lParam);
        IntPtr monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (Win32.GetMonitorInfo(monitor, ref mi))
            {
                // 坐标以最近显示器左上角为原点(多显示器负坐标场景同样成立)
                mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
            }
        }
        double scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        mmi.ptMinTrackSize.X = (int)(MinWidth * scale);
        mmi.ptMinTrackSize.Y = (int)(MinHeight * scale);
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    /// <summary>最大化 / 还原切换(双击标题栏触发,与原生窗口手感一致)</summary>
    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private Border MakeResizeGrip(double width, double height, int hitCode, Cursor cursor,
        HorizontalAlignment hAlign, VerticalAlignment vAlign)
    {
        var grip = new Border
        {
            Width = width, Height = height,
            Background = Brushes.Transparent,
            Cursor = cursor,
            HorizontalAlignment = hAlign,
            VerticalAlignment = vAlign
        };
        grip.MouseLeftButtonDown += (_, e) => { StartResize(hitCode); e.Handled = true; };
        return grip;
    }

    /// <summary>窗口圆角裁切:背景层集合统一套圆角矩形几何(尺寸变化时重建)</summary>
    private void UpdateRoundClip()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        bool max = WindowState == WindowState.Maximized;
        // 最大化时圆角归零:窗口已铺满工作区,留 18px 圆角会在四角露出桌面缺口
        double r = max ? 0 : Radius;
        _clipHost.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), r, r);
        if (_chrome != null) _chrome.CornerRadius = new CornerRadius(r);
        // 缩放手柄同步停用:最大化下边缘拖拽无意义,光标也不该再显示缩放箭头
        if (_grips != null) _grips.IsHitTestVisible = !max;
        UpdateSidebarFrost();
    }

    // ==================== 布局 ====================

    private Grid BuildLayout()
    {
        _overlay.SetResourceReference(Border.BackgroundProperty, "T.Overlay");
        _base.SetResourceReference(Border.BackgroundProperty, "T.Background");
        _bgRect.Fill = _bgBrush;
        _clipHost.Children.Add(_base);
        _clipHost.Children.Add(_bgRect);
        _clipHost.Children.Add(_dim);
        _clipHost.Children.Add(_overlay);

        // ---- 自绘标题栏(品牌 + 窗口控制,空白区拖拽移动) ----
        var titleBar = BuildTitleBar();

        // ---- 玻璃悬浮侧栏(双层阴影结构) ----
        var sidebar = BuildSidebar();

        // ---- 主体:标题栏下,侧栏悬浮 + 内容区 ----
        var body = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            Children =
            {
                new Border { Child = sidebar, Margin = new Thickness(16, 10, 0, 16) },
                new Border { Child = _deckArea, Margin = new Thickness(26, 14, 26, 30) }.Col(1)
            }
        };

        var content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            },
            Children = { titleBar, body.Row(1) }
        };

        // ---- 底部浮动状态胶囊(轻盈低干扰,替代全宽状态栏) ----
        _statusDot.SetResourceReference(Ellipse.FillProperty, "T.Success");
        var verText = UIKit.Sub("v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "1.9.8.6"), 10.5);
        verText.Margin = new Thickness(10, 0, 0, 0);
        verText.VerticalAlignment = VerticalAlignment.Center;
        var pillShadow = new Border { CornerRadius = new CornerRadius(UIKit.R.Panel), Effect = UIKit.SoftShadow(0.28) };
        pillShadow.SetResourceReference(Border.BackgroundProperty, "T.Surface");
        var pill = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Panel),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 6, 14, 6),
            Child = UIKit.H(_statusDot, _statusText, verText)
        };
        pill.WithRef("T.Surface", "T.Border");
        // 状态文字长文本省略显示,悬停看完整内容(避免右下角胶囊被撑得过宽)
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
        _statusText.MaxWidth = 420;
        _statusText.ToolTip = _statusText.Text;
        _statusPill = new Grid { Children = { pillShadow, pill } };
        // 内层弹入载体:状态刷新时轻微放大回弹(挂在 pill 本体,与外壳的淡入/位移互不干扰)
        pill.RenderTransformOrigin = new Point(0.5, 0.5);
        pill.RenderTransform = new ScaleTransform(1, 1);
        _statusPillBody = pill;
        _statusPill!.HorizontalAlignment = HorizontalAlignment.Right;   // 锚定窗口右下角,缩放时跟随对齐
        _statusPill!.VerticalAlignment = VerticalAlignment.Bottom;
        _statusPill!.Margin = new Thickness(0, 0, 14, 14);

        // ---- 窗口描边(圆角细边,顶层不拦截输入) ----
        _chrome = new Border { CornerRadius = new CornerRadius(Radius), BorderThickness = new Thickness(1), IsHitTestVisible = false };
        _chrome.SetResourceReference(Border.BorderBrushProperty, "T.Border");

        // ---- 四周/四角透明缩放手柄 ----
        _grips = new Grid
        {
            Children =
            {
                MakeResizeGrip(6, double.NaN, Win32.HTLEFT, Cursors.SizeWE, HorizontalAlignment.Left, VerticalAlignment.Stretch),
                MakeResizeGrip(6, double.NaN, Win32.HTRIGHT, Cursors.SizeWE, HorizontalAlignment.Right, VerticalAlignment.Stretch),
                MakeResizeGrip(double.NaN, 6, Win32.HTTOP, Cursors.SizeNS, HorizontalAlignment.Stretch, VerticalAlignment.Top),
                MakeResizeGrip(double.NaN, 6, Win32.HTBOTTOM, Cursors.SizeNS, HorizontalAlignment.Stretch, VerticalAlignment.Bottom),
                MakeResizeGrip(12, 12, Win32.HTTOPLEFT, Cursors.SizeNWSE, HorizontalAlignment.Left, VerticalAlignment.Top),
                MakeResizeGrip(12, 12, Win32.HTTOPRIGHT, Cursors.SizeNESW, HorizontalAlignment.Right, VerticalAlignment.Top),
                MakeResizeGrip(12, 12, Win32.HTBOTTOMLEFT, Cursors.SizeNESW, HorizontalAlignment.Left, VerticalAlignment.Bottom),
                MakeResizeGrip(12, 12, Win32.HTBOTTOMRIGHT, Cursors.SizeNWSE, HorizontalAlignment.Right, VerticalAlignment.Bottom)
            }
        };

        var root = new Grid { Children = { _clipHost, content, _statusPill!, _chrome, _grips } };
        root.SetResourceReference(ForegroundProperty, "T.Foreground");
        return root;
    }

    /// <summary>自绘标题栏:左品牌,右窗口控制(最小化/最大化/关闭),整条可拖拽移动</summary>
    private UIElement BuildTitleBar()
    {
        var logo = new Image
        {
            Source = ImageAssets.ResolveLogo(),
            Width = 36, Height = 36,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        // 品牌主副标题纵向叠放,与放大后的 logo 对齐
        var sub = UIKit.Sub("MINECRAFT LAUNCHER", 9.5);
        sub.Margin = new Thickness(1, 2, 0, 0);
        var brandText = UIKit.V(UIKit.Text("FufuLauncher", 16.5, FontWeights.SemiBold), sub);
        brandText.Margin = new Thickness(10, 0, 0, 0);
        var brand = UIKit.H(logo, brandText);
        brand.HorizontalAlignment = HorizontalAlignment.Left;
        brand.VerticalAlignment = VerticalAlignment.Center;
        brand.Margin = new Thickness(20, 8, 0, 0);   // 整体下移,与窗口圆角拉开呼吸距离

        var minBtn = MakeCaptionButton("—", () => WindowState = WindowState.Minimized, dangerHover: false);
        var maxBtn = MakeCaptionButton(MaxGlyph(), ToggleMaximize, dangerHover: false, icon => _maxGlyph = icon);
        var closeBtn = MakeCaptionButton("✕", Close, dangerHover: true);
        var controls = UIKit.H(minBtn, maxBtn, closeBtn);
        controls.HorizontalAlignment = HorizontalAlignment.Right;
        controls.VerticalAlignment = VerticalAlignment.Center;
        controls.Margin = new Thickness(0, 0, 12, 0);

        var bar = new Grid { Height = 54, Children = { brand, controls } };
        // 空白区拖拽移动(按钮自身吞掉点击,不会误触发);双击则最大化/还原
        bar.Background = Brushes.Transparent;
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (e.ClickCount == 2) { ToggleMaximize(); return; }
            DragMove();
        };
        return bar;
    }

    /// <summary>最大化按钮当前应显示的图标:已最大化时给「还原」,否则给「最大化」</summary>
    private string MaxGlyph() => WindowState == WindowState.Maximized ? "❐" : "□";

    /// <summary>窗口状态变化后同步最大化按钮图标</summary>
    private void UpdateMaxGlyph()
    {
        if (_maxGlyph != null) _maxGlyph.Text = MaxGlyph();
    }

    // ==================== 后台下载:托盘收纳 ====================

    /// <summary>关窗拦截(2026-09-27 后台下载):还有未完成任务就缩到托盘继续下,不退出进程;
    /// 任务已跑完、用户主动「暂停全部」(队列与断点已落盘,下次启动原样恢复)、
    /// 或走托盘菜单退出时都正常放行。进程退出前 App.OnExit 会把队列断点刷盘。</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_trayExit) return;   // 托盘菜单退出:放行(Closed 里回收托盘)
        // 主动暂停视为「用户不想再跑」:按正常关闭处理,不把窗口困在托盘里
        if (_downloadService == null || _downloadService.IsPaused) return;
        int pending = _downloadService.PendingCount;
        if (pending == 0) return;   // 没有后台任务:按用户预期直接关闭退出

        e.Cancel = true;
        Hide();   // 隐藏而非关闭:引擎是 DI 单例,下载不受影响
        var tray = EnsureTray();
        tray.Show();
        tray.UpdateTip(TrayTip(pending));
        // 首次收纳才弹气泡:告诉用户「程序没关,下载在继续」,并指明怎么找回窗口
        if (!_trayNotified)
        {
            _trayNotified = true;
            tray.ShowBalloon("FufuLauncher 仍在后台下载",
                $"还有 {pending} 个任务未完成,已最小化到托盘继续下载。\n左键托盘图标可恢复窗口,右键可显示或退出。");
        }
        App.WriteAppLog($"[托盘] 主窗口已隐藏,后台继续下载(未完成 {pending} 个);退出请用托盘菜单");
    }

    /// <summary>惰性创建托盘图标:首次收纳才建,不占启动路径耗时。
    /// 两个回调都经 Dispatcher 排队执行 —— 托盘点击是在托盘消息钩子内部同步回调的,
    /// 若在钩子里直接 Close()(进而 Dispose 掉承载钩子的 HwndSource)会踩到「在自己回调里销毁自己」,
    /// 排队一拍后钩子已返回,再关窗/回收才安全。</summary>
    private TrayIconService EnsureTray() => _tray ??= new TrayIconService(
        () => Dispatcher.BeginInvoke(ShowFromTray),
        () => Dispatcher.BeginInvoke(ExitFromTray));

    /// <summary>从托盘恢复主窗口</summary>
    private void ShowFromTray()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ShowFromTray); return; }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // 抢前台:Windows 限制后台进程置顶,经典绕法是 Topmost 闪一下再还原
        Topmost = true;
        Topmost = false;
        RefreshTrayState();
    }

    /// <summary>托盘菜单「退出启动器」:置放行标志后走正常关闭流程(OnExit 会刷下载断点)</summary>
    private void ExitFromTray()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ExitFromTray); return; }
        _trayExit = true;
        Close();
    }

    /// <summary>下载任务状态变化 → 刷新托盘提示与图标可见性</summary>
    private void OnTrayDownloadStatusChanged(DownloadTaskItem task)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnTrayDownloadStatusChanged(task)); return; }
        if (_tray == null && IsVisible) return;   // 未收纳且窗口可见:无需托盘提示
        RefreshTrayState();
    }

    /// <summary>托盘图标显隐/提示刷新:有未完成任务、或窗口还收纳在托盘里 → 图标保持;
    /// 两者都不成立(窗口回来了且下载跑完)才收起图标,不留残余托盘图标。</summary>
    private void RefreshTrayState()
    {
        if (_tray == null) return;
        int pending = _downloadService?.PendingCount ?? 0;
        if (pending > 0 || !IsVisible)
        {
            _tray.Show();
            _tray.UpdateTip(TrayTip(pending));
        }
        else _tray.Hide();
    }

    private static string TrayTip(int pending)
        => pending > 0 ? $"FufuLauncher · 后台下载中(剩余 {pending} 个任务)" : "FufuLauncher";

    /// <summary>标题栏窗口控制按钮(悬停柔底;关闭键悬停转语义红)</summary>
    private Button MakeCaptionButton(string glyph, Action onClick, bool dangerHover, Action<TextBlock>? onIcon = null)
    {
        var icon = UIKit.Text(glyph, 11, brushKey: "T.ForegroundDim");
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var hover = new Border { CornerRadius = new CornerRadius(UIKit.R.Chip), Opacity = 0 };
        hover.SetResourceReference(Border.BackgroundProperty, dangerHover ? "T.Danger" : "T.HoverFill");
        var btn = UIKit.GhostButton(new Grid { Children = { hover, icon } }, onClick, height: 30);
        btn.Width = 34;
        btn.Margin = new Thickness(4, 0, 0, 0);
        btn.MouseEnter += (_, _) =>
        {
            hover.FadeTo(dangerHover ? 0.85 : 1, UIKit.Fast);
            if (dangerHover) icon.SetResourceReference(TextBlock.ForegroundProperty, "T.Surface");
        };
        btn.MouseLeave += (_, _) =>
        {
            hover.FadeTo(0, UIKit.Fast);
            icon.SetResourceReference(TextBlock.ForegroundProperty, "T.ForegroundDim");
        };
        onIcon?.Invoke(icon);   // 交由调用方持有图标引用(最大化按钮需随状态换字形)
        return btn;
    }

    /// <summary>苹果毛玻璃侧栏(从零重做):VisualBrush 实时采样窗口背景层 → 模糊 → 白雾染色,
    /// 真正的“透明雾蒙蒙”透视感;布局 = 导航 + 账号卡</summary>
    private UIElement BuildSidebar()
    {
        // 阴影剪影层(独立于毛玻璃内容,避免 Effect 栅格化文字)
        var shadowLayer = new Border { CornerRadius = new CornerRadius(Radius), Effect = UIKit.SoftShadow(0.25) };
        shadowLayer.SetResourceReference(Border.BackgroundProperty, "T.Surface");

        // 毛玻璃核心:采样 _clipHost(背景图+压暗+遮罩),BlurEffect 实现真磨砂透视
        _frostBrush.Visual = _clipHost;
        _frostBrush.Stretch = Stretch.Fill;
        _frostBrush.ViewboxUnits = BrushMappingMode.Absolute;
        _frostBrush.Viewbox = new Rect(0, 0, 100, 100);   // 位置/尺寸变化时由 UpdateSidebarFrost 同步
        _frostBrush.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
        _frostBrush.Viewport = new Rect(0, 0, 1, 1);
        var frostRect = new Rectangle
        {
            Fill = _frostBrush,
            Effect = new BlurEffect { Radius = 26, RenderingBias = RenderingBias.Performance },
            Margin = new Thickness(-40)   // 外扩隐藏模糊边缘衰减,面板 Clip 裁切
        };

        _sidebarPanel.CornerRadius = new CornerRadius(Radius);
        _sidebarPanel.BorderThickness = new Thickness(1);
        _sidebarPanel.Width = 222;
        _sidebarPanel.SetResourceReference(Border.BorderBrushProperty, "T.Border");

        // 账号卡(底部,点击跳转账号页)
        var accountCard = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Panel),
            Margin = new Thickness(12, 8, 12, 12),
            Padding = new Thickness(14, 14, 14, 14),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        accountCard.WithRef("T.SurfaceAlt", "T.Border");
        RenderOptions.SetBitmapScalingMode(_accountAvatar, BitmapScalingMode.NearestNeighbor);
        var accountInfo = UIKit.V(UIKit.Sub("当前账号", 10.5), _accountText);
        accountInfo.VerticalAlignment = VerticalAlignment.Center;
        accountCard.Child = UIKit.H(_accountAvatar, accountInfo);
        _accountText.Margin = new Thickness(0, 3, 0, 0);
        _accountText.TextTrimming = TextTrimming.CharacterEllipsis;
        _accountText.MaxWidth = 70;   // 侧栏宽有限,长昵称截断不溢卡
        accountCard.MouseLeftButtonUp += (_, _) => Switch(DeckKey.Accounts);

        var contentCol = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            },
            Children =
            {
                new ScrollViewer { Content = _navPanel, Margin = new Thickness(10, 12, 10, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                accountCard.Row(1)
            }
        };

        _sidebarPanel.Child = new Grid { Children = { frostRect, _tintRect, contentCol } };
        return new Grid { Children = { shadowLayer, _sidebarPanel } };
    }

    /// <summary>毛玻璃对齐:面板圆角裁切 + 采样窗口坐标同步(尺寸/位置变化时调用)</summary>
    private void UpdateSidebarFrost()
    {
        if (!_sidebarPanel.IsLoaded || _sidebarPanel.ActualWidth <= 0 || _sidebarPanel.ActualHeight <= 0) return;
        _sidebarPanel.Clip = new RectangleGeometry(
            new Rect(0, 0, _sidebarPanel.ActualWidth, _sidebarPanel.ActualHeight), Radius, Radius);
        try
        {
            var p = _sidebarPanel.TranslatePoint(new Point(0, 0), _clipHost);
            _frostBrush.Viewbox = new Rect(p.X, p.Y, _sidebarPanel.ActualWidth, _sidebarPanel.ActualHeight);
        }
        catch (InvalidOperationException) { /* 坐标系未就绪跳过本帧,下次 SizeChanged 补偿 */ }
    }

    // ==================== 导航(一级图标徽章 + 二级) ====================

    private void BuildNav()
    {
        foreach (var (key, icon, label, subDef) in NavDefs)
        {
            // 泡芙助理入口与设置子项常驻显示:程序不内置模型,改由用户在页面内拖入 .gguf,
            // 故不再按「模型文件是否存在」隐藏导航项/设置子菜单(否则用户永远看不到导入入口)
            string[] subItems = subDef;
            // 左侧点缀指示条(选中时显现,iOS 式高亮锚点)
            var accent = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(1.5),
                Opacity = 0,
                Margin = new Thickness(0, 5, 0, 5),
                VerticalAlignment = VerticalAlignment.Center
            };
            accent.SetResourceReference(Border.BackgroundProperty, "T.Primary");

            // 图标徽章:圆角小方块,选中时主色底
            var iconGlyph = UIKit.Text(icon, 13, FontWeights.Medium);
            iconGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            iconGlyph.VerticalAlignment = VerticalAlignment.Center;
            var iconChip = new Border
            {
                Width = 28, Height = 28,
                CornerRadius = new CornerRadius(UIKit.R.Chip),
                Margin = new Thickness(8, 0, 0, 0),
                Child = iconGlyph
            };
            iconChip.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");

            var text = UIKit.Text(label, 13, FontWeights.Medium);
            text.Margin = new Thickness(10, 0, 0, 0);
            text.VerticalAlignment = VerticalAlignment.Center;

            var row = UIKit.H(accent, iconChip, text);
            row.VerticalAlignment = VerticalAlignment.Center;

            var hover = new Border { CornerRadius = new CornerRadius(UIKit.R.Chip), Opacity = 0 };
            hover.SetResourceReference(Border.BackgroundProperty, "T.HoverFill");

            // 选中态 pill:主色柔底(毛玻璃面上不再用描边,改用指示条高亮)
            var pill = new Border { CornerRadius = new CornerRadius(UIKit.R.Chip), Padding = new Thickness(8, 6, 8, 6) };
            var btn = UIKit.GhostButton(new Grid { Children = { hover, pill, row } }, () => OnNavClick(key), height: 42);
            btn.Margin = new Thickness(0, 2, 0, 2);
            btn.MouseEnter += (_, _) => hover.FadeTo(1, UIKit.Fast);
            btn.MouseLeave += (_, _) => hover.FadeTo(0, UIKit.Fast);

            _navVisuals[key] = (pill, iconChip, iconGlyph, accent);
            _navPanel.Children.Add(btn);

            // 二级子菜单:左侧引导线 + 缩进文字(hover 渐入,点击区加高更易命中)
            if (subItems.Length > 0)
            {
                var guideList = new System.Collections.Generic.List<Border>();
                var subPanel = new StackPanel { Visibility = Visibility.Collapsed, Tag = key, Margin = new Thickness(24, 2, 0, 6) };
                var subTexts = new TextBlock[subItems.Length];
                for (int i = 0; i < subItems.Length; i++)
                {
                    // 过滤(如无模型时隐藏泡芙助理)会使位置与真实 section 索引错位,按标签回查原始定义取真实索引
                    int idx = Array.IndexOf(subDef, subItems[i]);
                    var subText = UIKit.Sub(subItems[i], 12.5);
                    subText.Margin = new Thickness(12, 0, 0, 0);
                    subText.VerticalAlignment = VerticalAlignment.Center;
                    subTexts[i] = subText;
                    var subHover = new Border { CornerRadius = new CornerRadius(UIKit.R.Chip), Opacity = 0 };
                    subHover.SetResourceReference(Border.BackgroundProperty, "T.HoverFill");
                    var guide = new Border { Width = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 7, 0, 7) };
                    guide.SetResourceReference(Border.BackgroundProperty, "T.Border");
                    guideList.Add(guide);
                    var subBtn = UIKit.GhostButton(new Grid { Children = { subHover, UIKit.H(guide, subText) } },
                        () =>
                        {
                            Switch(key); SelectSub(key, idx);
                        }, height: 34);
                    subBtn.Margin = new Thickness(0, 1, 0, 1);
                    subBtn.MouseEnter += (_, _) => subHover.FadeTo(1, UIKit.Fast);
                    subBtn.MouseLeave += (_, _) => subHover.FadeTo(0, UIKit.Fast);
                    subPanel.Children.Add(subBtn);
                }
                _navPanel.Children.Add(subPanel);
                _subGuides[key] = guideList;
                _subTexts[key] = subTexts;
            }
        }
    }

    private void OnNavClick(DeckKey key)
    {
        Switch(key);
        // 二级菜单展开/收起动画:自导航项下方展开,收起回到原位(代数守护防快速连点错乱)
        foreach (var child in _navPanel.Children)
        {
            if (child is not StackPanel { Tag: DeckKey tag } sp) continue;
            if (tag == key) MotionKit.Enter(sp, fromY: -8, MotionKit.Normal);
            else MotionKit.Exit(sp, toY: -8, MotionKit.Fast);
        }
    }

    /// <summary>选中一级菜单下的二级子视图(子项文字与引导线联动高亮)</summary>
    public void SelectSub(DeckKey key, int subIndex)
    {
        if (!_decks.TryGetValue(key, out var deck)) return;
        // 2026-09-26 批4:记录上一次子分区,支持 Alt+← 分区级回退(历史回退触发的不再入栈)
        int prevSub = _subSelection.TryGetValue(key, out var p) ? p : 0;
        if (!_restoringSub && prevSub != subIndex)
        {
            _subHistory.Push((key, prevSub));
            if (_subHistory.Count > 32) _subHistory.Clear();   // 上限保护
        }
        switch (key)
        {
            case DeckKey.Instances when deck is InstancesDeck v:
                // 四分区:0=本地版本 1=新装版本 2=版本设置 3=版本工具(页内分区,与模组管理同款,不跳设置页)
                v.ShowSection(subIndex);
                break;
            case DeckKey.Mods when deck is ModsDeck m: m.ShowSection(subIndex); break;
            case DeckKey.Settings when deck is SettingsDeck s: s.ShowSection(subIndex); break;
        }
        RefreshSubHighlight(key, subIndex);
    }

    /// <summary>刷新二级子菜单高亮:选中项文字主色半粗 + 引导线主色,其余恢复灰(用户偏好「选中发光」)</summary>
    private void RefreshSubHighlight(DeckKey key, int? subIndex = null)
    {
        if (!_subTexts.TryGetValue(key, out var arr)) return;
        int sel = subIndex ?? (_subSelection.TryGetValue(key, out var v) ? v : 0);
        _subSelection[key] = sel;
        for (int i = 0; i < arr.Length; i++)
        {
            bool active = i == sel;
            arr[i].SetResourceReference(TextBlock.ForegroundProperty, active ? "T.Primary" : "T.ForegroundDim");
            arr[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
            if (_subGuides.TryGetValue(key, out var guides) && i < guides.Count)
                guides[i].SetResourceReference(Border.BackgroundProperty, active ? "T.Primary" : "T.Border");
        }
    }

    /// <summary>跳到「版本管理 → 版本工具」分区并定位到指定页签与版本(供各面板的跨页跳转按钮调用)</summary>
    public void OpenInstanceTools(int tab, string instanceId)
    {
        // 2026-09-26 批2(E1 修复):原先只 Switch 不 SelectSub,侧栏二级高亮停在旧分区(与页内分区不一致)
        Navigate(DeckKey.Instances, 3);
        if (_decks.TryGetValue(DeckKey.Instances, out var deck) && deck is InstancesDeck v)
            v.OpenTools(tab, instanceId);
    }

    /// <summary>深链跳转:切到指定页并可直接选中二级分区(跨页按钮统一入口,2026-09-26 批2)</summary>
    public void Navigate(DeckKey key, int? subIndex = null)
    {
        Switch(key);
        if (subIndex is int i) SelectSub(key, i);
    }

    // ==================== Deck 切换 ====================

    /// <summary>切换页面(记入历史栈,供 Alt+← 回退)</summary>
    public void Switch(DeckKey key) => SwitchCore(key, recordHistory: true);

    /// <summary>回退:同页有子分区历史先退子分区,否则退回上一页(栈空忽略)</summary>
    public void GoBack()
    {
        // 2026-09-26 批4:分区级回退——停在同页时 Alt+← 先回到上一个二级分区,再退整页
        if (_subHistory.Count > 0 && _subHistory.Peek().Key == _current)
        {
            var (k, s) = _subHistory.Pop();
            _restoringSub = true;
            try { SelectSub(k, s); } finally { _restoringSub = false; }
            SetStatus("已回退上一分区");
            return;
        }
        if (_history.Count == 0) return;
        SwitchCore(_history.Pop(), recordHistory: false);
        SetStatus("已回退上一页");
    }

    /// <summary>切换页面:懒创建 + Visibility 保活 + 导航高亮联动 + ColorOS 式交叉过渡</summary>
    private void SwitchCore(DeckKey key, bool recordHistory)
    {
        if (recordHistory && key != _current)
        {
            _history.Push(_current);
            if (_history.Count > 32) { _history.Clear(); _subHistory.Clear(); }   // 上限保护(两级历史同清)
        }

        if (!_decks.TryGetValue(key, out var deck))
        {
            deck = CreateDeck(key);
            _decks[key] = deck;
        }
        if (deck.Parent == null) _deckArea.Children.Add(deck);   // 预建产物首次启用时挂载

        // 旧页退场 + 新页入场(叠放交叉过渡:新页抬到上层自下漂移浮现,旧页在其下向上收拢淡出;
        // 流畅度保障:①残留动画瞬收 → 同屏恒定「1 退场 + 1 入场」,快速连点不叠加不掉帧;
        // ②无 From 动画可打断改道;③退场 190ms / 入场 300ms 错开节拍,双页并存时间最短;
        // ④入场前先 UpdateLayout 把大页布局成本消化在动画之外。
        // 注:过渡期位图缓存已移除 —— 透明窗口下与渲染线程冲突致 UCEERR_RENDERTHREADFAILURE 崩溃)
        if (key != _current)
        {
            _startupEntered = true;   // 用户已抢先切页:启动入场不再补播,避免叠加跳动
            foreach (var kv in _decks)
            {
                if (kv.Key == key || kv.Key == _current) continue;
                MotionKit.SnapHide(kv.Value);   // 残留可见页瞬收,不叠加动画
            }
            if (_decks.TryGetValue(_current, out var oldDeck) && !ReferenceEquals(oldDeck, deck))
                MotionKit.PageExit(oldDeck);
            MotionKit.PageEnter(deck);
        }

        // 选中态:pill 主色柔底 + 左侧指示条显现,图标徽章主色化
        foreach (var kv in _navVisuals)
        {
            bool active = kv.Key == key;
            var (pill, chip, glyph, accent) = kv.Value;
            if (active)
            {
                pill.SetResourceReference(Border.BackgroundProperty, "T.PrimarySoft");
                chip.SetResourceReference(Border.BackgroundProperty, "T.Primary");
                glyph.SetResourceReference(TextBlock.ForegroundProperty, "T.Surface");
                if (accent.Opacity < 1) accent.FadeTo(1, UIKit.Fast);   // 指示条柔和淡入,消除切页时的硬闪
            }
            else
            {
                pill.Background = Brushes.Transparent;
                chip.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");
                glyph.SetResourceReference(TextBlock.ForegroundProperty, "T.ForegroundDim");
                if (accent.Opacity > 0) accent.FadeTo(0, UIKit.Fast);   // 指示条柔和淡出
            }
        }
        // 二级子菜单高亮联动:按当前选中子项刷新(选中项主色,其余灰)
        RefreshSubHighlight(key);
        _current = key;
        // 2026-09-26 批2(状态胶囊防冲刷):6 秒内刚播报过重要状态(如下载完成)则保留,不立刻清成「就绪」
        if (!_statusSticky || (DateTime.Now - _statusAt).TotalSeconds > 6)
            SetStatus("就绪");
        // 主页隐藏右下角状态胶囊,其他页显示(改用淡入淡出,消除切页时的「瞬间消失/出现」)
        if (_statusPill != null)
        {
            bool showPill = key != DeckKey.Home;
            if (IsLoaded)
            {
                if (showPill) MotionKit.Enter(_statusPill, fromY: 8, MotionKit.Normal);
                else MotionKit.Exit(_statusPill, toY: 8, MotionKit.Fast);
            }
            else
                _statusPill.Visibility = showPill ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private UserControl CreateDeck(DeckKey key)   // 2026-09-26 去 static:下载中心需要传入 Switch 导航回调
    {
        UserControl deck = key switch   // 显式声明基类:switch 表达式的各分支类型不同,靠 var 推不出公共类型
        {
            DeckKey.Home => new HomeDeck(),
            DeckKey.Ai => new AiDeck(),
            DeckKey.Instances => new InstancesDeck(),
            DeckKey.Mods => new ModsDeck(),
            DeckKey.Modpacks => new ModpackDeck(),
            DeckKey.Downloads => new DownloadDeck(navigate: Navigate),   // 2026-09-26 批2:空态快捷入口支持深链到二级分区
            DeckKey.Accounts => new AccountsDeck(),
            DeckKey.Settings => new SettingsDeck(),
            _ => throw new InvalidOperationException("未知 Deck")
        };
        // 新建即折叠,显示权统一交给 PageEnter。若保持默认的 Visible + Opacity 1,
        // PageEnter 会把它误判成「退场中途」而直接续接当前值 → 新页瞬间满屏,
        // 这正是切页闪现的成因之一(预建路径与懒创建路径共用本函数,一次堵住两处)
        deck.Visibility = Visibility.Collapsed;
        // 页面主滚动区挂惯性平滑滚动(纯 UI 行为,内容/业务不动):取消滚轮一格一跳的生硬跳滚
        if (deck.Content is ScrollViewer sv) SmoothScroll.Attach(sv);
        return deck;
    }

    // ==================== 主题 / 状态 / 账号 ====================

    private void OnThemeChanged() => Dispatcher.BeginInvoke(() => ApplyThemeVisual());

    /// <summary>应用主题视觉:背景图(填充/适应/平铺) + 模糊 + 亮度 + 遮罩 + 窗口透明度。
    /// 性能要点:背景图按路径缓存(色相/亮度等高频刷新免重复解码),BlurEffect 实例复用。
    /// deferBackdrop=true 用于启动首帧:只定窗口不透明度与底色,背景图/模糊延后到 OnFirstFrameRendered</summary>
    private void ApplyThemeVisual(bool deferBackdrop = false)
    {
        if (deferBackdrop)
        {
            _bgRect.Visibility = Visibility.Collapsed;
            _dim.Visibility = Visibility.Collapsed;
            _overlay.Visibility = Visibility.Collapsed;
            Opacity = ThemeManager.ResolveOpacity();
            return;
        }

        // 全量应用前清掉「背景淡入」遗留动画,避免主题切换时半途被旧动画按住不透明度
        _bgRect.BeginAnimation(UIElement.OpacityProperty, null);
        _bgRect.Opacity = 1;

        var bgPath = ThemeManager.ResolveBackgroundPath();
        BitmapImage? bmp;
        if (bgPath == null)
        {
            bmp = null; _bgPathKey = null; _bgBmp = null;
        }
        else if (bgPath == _bgPathKey && _bgBmp != null)
        {
            bmp = _bgBmp;   // 路径未变:复用已解码位图
        }
        else
        {
            bmp = ImageAssets.LoadFromFile(bgPath);
            _bgPathKey = bgPath; _bgBmp = bmp;
        }
        bool hasBg = bmp != null;

        // 显示模式:填充 / 适应 / 平铺(图片尺寸适配全部交给画刷,不写死比例)
        if (!ReferenceEquals(_bgBrush.ImageSource, bmp)) _bgBrush.ImageSource = bmp;
        string mode = ThemeManager.ResolveBackgroundMode();
        if (mode == "Tile" && bmp != null)
        {
            _bgBrush.TileMode = TileMode.Tile;
            _bgBrush.Stretch = Stretch.None;
            _bgBrush.ViewportUnits = BrushMappingMode.Absolute;
            _bgBrush.Viewport = new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight);
        }
        else
        {
            _bgBrush.TileMode = TileMode.None;
            _bgBrush.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
            _bgBrush.Stretch = mode == "Fit" ? Stretch.Uniform : Stretch.UniformToFill;
        }
        _bgRect.Visibility = hasBg ? Visibility.Visible : Visibility.Collapsed;

        // 模糊度(模糊时外扩绘制区,隐藏边缘透明锯齿;圆角裁切由 clipHost 兜底)
        double blur = ThemeManager.ResolveBlur();
        if (blur > 0 && hasBg)
        {
            _bgBlur.Radius = blur;
            if (!ReferenceEquals(_bgRect.Effect, _bgBlur)) _bgRect.Effect = _bgBlur;
            _bgRect.Margin = new Thickness(-70);
        }
        else
        {
            _bgRect.Effect = null;
            _bgRect.Margin = new Thickness(0);
        }

        // 亮度:压暗层(< 1 时叠加黑色透明层)
        _dim.Opacity = Math.Clamp(1 - ThemeManager.ResolveBrightness(), 0, 0.6);
        _dim.Visibility = hasBg ? Visibility.Visible : Visibility.Collapsed;

        _overlay.Visibility = hasBg ? Visibility.Visible : Visibility.Collapsed;
        // 毛玻璃侧栏白雾染色:深色主题薄白雾、浅色主题浓白雾(2026-09-26 批5 改为主题令牌,取值不变)
        _tintRect.SetResourceReference(Shape.FillProperty, "T.FrostTint");
        Opacity = ThemeManager.ResolveOpacity();
    }

    /// <summary>拖入文件 → 先走拖拽路由(zip 模组/资源包/光影包/整合包/存档自动识别导入),未命中再回落为自定义背景图片</summary>
    private void OnBackgroundDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            // ① 拖拽路由:自动分辨模组包 / 资源包 / 光影包 / 整合包 / 存档 zip 并导入(LauncherX-3)
            //    图片等非支持类型会返回 false,交回下面的背景逻辑
            if (DropRouter.Handle(this, files)) return;

            // ② 回落:图片文件设为自定义背景(仅接受常见位图格式)
            var hit = files.FirstOrDefault(f =>
                f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));
            if (hit == null) { SetStatus("可拖入 zip 模组/整合包,或 png/jpg/bmp 图片作为背景", true); return; }
            ThemeManager.SetCustomBackground(hit);
            SetStatus("自定义背景已应用");
        }
        catch (Exception ex) { SetStatus("拖入处理失败: " + ex.Message, true); }
    }

    /// <summary>状态胶囊播报(跨线程安全)。sticky=true 表示重要状态,切页时 6 秒内不被「就绪」冲刷(2026-09-26 批2)</summary>
    public void SetStatus(string text, bool warning = false, bool sticky = false)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetStatus(text, warning, sticky)); return; }
        bool changed = _statusText.Text != text;
        _statusText.Text = text;
        _statusText.ToolTip = text;   // 悬停气泡跟随最新状态文本
        _statusDot.SetResourceReference(Ellipse.FillProperty, warning ? "T.Warning" : "T.Success");
        _statusAt = DateTime.Now;
        _statusSticky = sticky;
        // 状态发生变化时胶囊轻微放大回弹(自 0.96 弹回 1),把「播报」做出可感知的反馈;
        // 400ms 节流:高频状态刷新(下载进度等)不反复回跳,只保留最后一次手感
        if (changed && _statusPillBody?.RenderTransform is ScaleTransform st
            && Environment.TickCount64 - _statusPulseAt > 400)
        {
            _statusPulseAt = Environment.TickCount64;
            st.ScaleX = 0.96; st.ScaleY = 0.96;
            MotionKit.Anim(st, ScaleTransform.ScaleXProperty, 1, MotionKit.Normal, MotionKit.OutBack());
            MotionKit.Anim(st, ScaleTransform.ScaleYProperty, 1, MotionKit.Normal, MotionKit.OutBack());
        }
    }

    private void RefreshAccountCard()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshAccountCard); return; }
        var acc = _accountService?.CurrentAccount;
        _accountText.Text = acc == null
            ? "未登录"
            : acc.Username + (acc.Type == AccountType.Microsoft ? "(微软)" : "(离线)");
        // 玩家头颅头像仅限正版(微软)账号生效,离线账号不启用此功能
        bool showAvatar = acc != null && acc.Type == AccountType.Microsoft;
        _accountAvatar.Visibility = showAvatar ? Visibility.Visible : Visibility.Collapsed;
        if (showAvatar) _ = LoadAccountAvatarAsync(acc!);
        else { _avatarToken = ""; _accountAvatar.Source = null; }
    }

    /// <summary>异步加载正版账号玩家头颅:官方皮肤 → 获取失败时按 UUID 随机生成游戏风格头颅</summary>
    private async Task LoadAccountAvatarAsync(GameAccount acc)
    {
        string token = acc.Uuid;
        _avatarToken = token;
        try
        {
            var img = await SkinHeadKit.GetHeadImageAsync(token, AccountType.Microsoft);
            if (_avatarToken != token) return;   // 加载期间账号已切换,丢弃过期头像
            _accountAvatar.Source = img;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[账号] 头像加载失败:{ex.Message}");
        }
    }
}
