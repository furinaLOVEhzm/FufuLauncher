// FufuLauncher - 系统托盘图标(Shell_NotifyIcon)
// Copyright © FufuLauncher
//
// 「后台下载」的窗口侧配套:关闭主窗口时不退出进程,而是缩到托盘继续下载,
// 避免用户误关丢任务。
// 用 Win32 Shell_NotifyIcon + 原生弹出菜单直接实现,不引入 WinForms
// (工程保持 UseWindowsForms=false,发布体积与松散布局不变)。
//
// 约定:全部成员必须在 UI 线程调用——WndProc 与 TrackPopupMenu 都是 UI 线程消息。

using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FufuLauncher.Services;

/// <summary>托盘图标:显示/隐藏、悬停提示、右键菜单(显示主界面 / 退出启动器)。</summary>
public sealed class TrayIconService : IDisposable
{
    // ---- 自定义回调消息 ----
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 88;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;

    // ---- Shell_NotifyIcon ----
    private const int NIM_ADD = 0x0;
    private const int NIM_MODIFY = 0x1;
    private const int NIM_DELETE = 0x2;
    private const int NIF_MESSAGE = 0x1;
    private const int NIF_ICON = 0x2;
    private const int NIF_TIP = 0x4;
    private const int NIF_INFO = 0x10;
    private const int NIIF_INFO = 0x1;

    // ---- 弹出菜单 ----
    private const uint MF_STRING = 0x0;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;
    private const int ID_SHOW = 1;
    private const int ID_EXIT = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private readonly HwndSource _sink;      // 只收托盘回调消息的不可见窗口
    private readonly Action _onShowWindow;  // 托盘菜单「显示主界面」
    private readonly Action _onExitApp;     // 托盘菜单「退出启动器」
    private readonly IntPtr _icon;
    private readonly int _id = Environment.ProcessId & 0xFFFF;

    private string _tip = "FufuLauncher";
    private bool _visible;
    private bool _disposed;

    /// <summary>创建托盘图标(不显示)。onShowWindow / onExitApp 在 UI 线程回调。</summary>
    public TrayIconService(Action onShowWindow, Action onExitApp)
    {
        _onShowWindow = onShowWindow;
        _onExitApp = onExitApp;
        _icon = LoadAppIcon();
        // 无样式顶层窗口:不可见、不占任务栏,只作 Shell_NotifyIcon 的消息接收端
        _sink = new HwndSource(0, 0, 0, 0, 0, "FufuLauncherTraySink", IntPtr.Zero);
        _sink.AddHook(WndProc);
    }

    /// <summary>当前是否已显示图标</summary>
    public bool Visible => _visible;

    /// <summary>显示托盘图标</summary>
    public void Show()
    {
        if (_disposed || _visible) return;
        var data = BuildData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        if (Shell_NotifyIconW(NIM_ADD, ref data)) _visible = true;
    }

    /// <summary>隐藏托盘图标</summary>
    public void Hide()
    {
        if (_disposed || !_visible) return;
        var data = BuildData(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _visible = false;
    }

    /// <summary>更新悬停提示(下载进度等);图标未显示时不做事</summary>
    public void UpdateTip(string tip)
    {
        if (_disposed || string.IsNullOrEmpty(tip)) return;
        _tip = tip.Length > 120 ? tip[..120] : tip;
        if (!_visible) return;
        var data = BuildData(NIF_TIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>气泡提示(缩到托盘时告知用户「程序还在,下载继续」)</summary>
    public void ShowBalloon(string title, string text)
    {
        if (_disposed) return;
        var data = BuildData(NIF_INFO | NIF_ICON | NIF_TIP);
        data.szInfoTitle = title.Length > 60 ? title[..60] : title;
        data.szInfo = text.Length > 250 ? text[..250] : text;
        data.dwInfoFlags = NIIF_INFO;
        data.uTimeoutOrVersion = 8000;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA BuildData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _sink.Handle,
        uID = _id,
        uFlags = flags,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
        dwInfoFlags = 0,
        guidItem = Guid.Empty,
        hBalloonIcon = IntPtr.Zero
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAYICON) return IntPtr.Zero;
        int evt = lParam.ToInt32() & 0xFFFF;
        if (evt is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
        {
            handled = true;
            _onShowWindow();
        }
        else if (evt == WM_RBUTTONUP)
        {
            handled = true;
            ShowMenu();
        }
        return IntPtr.Zero;
    }

    /// <summary>原生右键菜单(TPM_RETURNCMD:直接拿到选中项,不用处理 WM_COMMAND)</summary>
    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, MF_STRING, new IntPtr(ID_SHOW), "显示主界面");
            AppendMenuW(menu, MF_STRING, new IntPtr(ID_EXIT), "退出启动器");
            GetCursorPos(out var pt);
            // 先抢前台:否则菜单点外部不会自动关闭(Windows 的经典要求)
            SetForegroundWindow(_sink.Handle);
            int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                pt.X, pt.Y, 0, _sink.Handle, IntPtr.Zero);
            PostMessageW(_sink.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd == ID_SHOW) _onShowWindow();
            else if (cmd == ID_EXIT) _onExitApp();
        }
        finally { DestroyMenu(menu); }
    }

    /// <summary>取 exe 主图标做托盘图标(与桌面/任务栏同源);失败退回系统默认图标</summary>
    private static IntPtr LoadAppIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) &&
                ExtractIconExW(exe, 0, out IntPtr large, out IntPtr small, 1) > 0)
            {
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero) return small;
            }
        }
        catch { /* 取图标失败不致命,走兜底 */ }
        return LoadIconW(IntPtr.Zero, new IntPtr(32512));   // IDI_APPLICATION
    }

    public void Dispose()
    {
        if (_disposed) return;
        // 必须先删图标再置 _disposed:Hide() 有 _disposed 守卫,顺序反了会留下「幽灵托盘图标」
        try
        {
            if (_visible)
            {
                var data = BuildData(0);
                Shell_NotifyIconW(NIM_DELETE, ref data);
                _visible = false;
            }
        }
        catch { /* ignore */ }
        _disposed = true;
        try { _sink.RemoveHook(WndProc); _sink.Dispose(); } catch { /* ignore */ }
        if (_icon != IntPtr.Zero) { try { DestroyIcon(_icon); } catch { /* ignore */ } }
    }
}