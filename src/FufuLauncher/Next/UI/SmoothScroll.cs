// FufuLauncher - 平滑滚动(惯性缓动)
// Copyright © FufuLauncher
//
// WPF 原生 ScrollViewer 的滚轮是「一格一跳」的离散偏移,观感生硬。
// 本文件只在 UI 层拦截滚轮与翻页键,把目标偏移交给逐帧指数平滑逼近,内容与业务一概不碰:
// 1. 帧率无关:按真实经过时间做 exp 衰减逼近,60Hz / 120Hz 观感一致;
// 2. 可打断:滚动途中继续滚动只改「目标值」,不重启动画、不跳变、不回弹;
// 3. 零常驻开销:仅在有列表正在滚动时订阅 CompositionTarget.Rendering,到位立即退订;
// 4. 与外部滚动共存:每帧以 ScrollViewer.VerticalOffset 为基准续算,拖滚动条 / 代码
//    ScrollToVerticalOffset 造成的偏移天然被吸收,不会出现「动画跟人抢滚动条」;
// 5. 输入框优先:焦点在 TextBox 内时不拦截 Home/End/PageUp/PageDown,保留文本编辑语义。

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FufuLauncher.Next.UI;

internal static class SmoothScroll
{
    /// <summary>一格滚轮(±120)的位移量,约 3 行文字</summary>
    private const double WheelStep = 110;
    /// <summary>单次滚轮事件位移上限:高分辨率滚轮 / 触控板惯性事件防暴冲</summary>
    private const double MaxWheelJump = 420;
    /// <summary>翻页键位移占可视高度的比例(留 14% 上下文,对齐系统手感)</summary>
    private const double PageRatio = 0.86;
    /// <summary>指数平滑时间常数(秒):越小越跟手,越大越绵柔</summary>
    private const double Tau = 0.055;
    /// <summary>到位阈值(像素):残差小于该值直接吸附并停表</summary>
    private const double Epsilon = 0.4;

    private sealed class State
    {
        public double Target;      // 目标偏移
        public bool Running;       // 是否正在惯性滚动中
        public long LastMs;        // 上一帧时间戳(Environment.TickCount64)
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();
    private static readonly List<(ScrollViewer Sv, State St)> Active = new();
    private static bool _hooked;   // CompositionTarget.Rendering 是否已订阅

    /// <summary>为 ScrollViewer 挂上惯性平滑滚动(重复调用安全,同一实例只挂一次)</summary>
    public static void Attach(ScrollViewer sv)
    {
        if (States.TryGetValue(sv, out _)) return;
        var st = States.GetValue(sv, _ => new State());
        st.Target = sv.VerticalOffset;

        sv.PreviewMouseWheel += (_, e) => OnWheel(sv, st, e);
        sv.PreviewKeyDown += (_, e) => OnKey(sv, st, e);
        // 非惯性期间的外部滚动(拖滚动条 / 代码 ScrollTo)把目标对齐到真实位置,下次滚轮不跳
        sv.ScrollChanged += (_, _) => { if (!st.Running) st.Target = sv.VerticalOffset; };
    }

    // ==================== 输入拦截 ====================

    private static void OnWheel(ScrollViewer sv, State st, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (sv.ScrollableHeight <= 0) return;                       // 无可滚动内容:交回上层,不吞事件
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return;   // Ctrl+滚轮留给缩放类语义

        double delta = Math.Clamp(-(e.Delta / 120.0) * WheelStep, -MaxWheelJump, MaxWheelJump);
        double from = st.Running ? st.Target : sv.VerticalOffset;    // 滚动中续接目标值(可打断)
        st.Target = Math.Clamp(from + delta, 0, sv.ScrollableHeight);
        e.Handled = true;
        Start(sv, st);
    }

    private static void OnKey(ScrollViewer sv, State st, KeyEventArgs e)
    {
        if (e.Handled || sv.ScrollableHeight <= 0) return;
        if (Keyboard.FocusedElement is TextBoxBase) return;          // 输入框内的编辑键不拦截

        double from = st.Running ? st.Target : sv.VerticalOffset;
        double to = e.Key switch
        {
            Key.PageDown => from + sv.ViewportHeight * PageRatio,
            Key.PageUp => from - sv.ViewportHeight * PageRatio,
            Key.Home => 0,
            Key.End => sv.ScrollableHeight,
            _ => double.NaN
        };
        if (double.IsNaN(to)) return;

        st.Target = Math.Clamp(to, 0, sv.ScrollableHeight);
        e.Handled = true;
        Start(sv, st);
    }

    // ==================== 逐帧逼近 ====================

    private static void Start(ScrollViewer sv, State st)
    {
        if (Math.Abs(st.Target - sv.VerticalOffset) < Epsilon) { st.Target = sv.VerticalOffset; return; }
        st.Running = true;
        st.LastMs = Environment.TickCount64;
        if (!Active.Any(a => ReferenceEquals(a.Sv, sv))) Active.Add((sv, st));
        if (_hooked) return;
        CompositionTarget.Rendering += OnRendering;
        _hooked = true;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var (sv, st) = Active[i];
            double dt = Math.Clamp((now - st.LastMs) / 1000.0, 0.001, 0.1);   // 卡顿帧限幅,防一次跳到底
            st.LastMs = now;

            if (!sv.IsLoaded || sv.ScrollableHeight <= 0) { Stop(i); continue; }

            double cur = sv.VerticalOffset;                        // 以真实位置续算 → 外部滚动自动吸收
            double next = cur + (st.Target - cur) * (1 - Math.Exp(-dt / Tau));
            if (Math.Abs(st.Target - next) < Epsilon) next = st.Target;
            sv.ScrollToVerticalOffset(next);
            if (next == st.Target) Stop(i);
        }
        if (Active.Count == 0 && _hooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    private static void Stop(int index)
    {
        Active[index].St.Running = false;
        Active.RemoveAt(index);
    }
}
