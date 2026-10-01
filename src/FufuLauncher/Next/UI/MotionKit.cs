// FufuLauncher - 全局动效中心(MotionKit)
// Copyright © FufuLauncher
//
// 动效规范(对标 ColorOS 质感:柔和、连贯、可打断):
// 1. 统一节拍:Fast 160ms(下拉/悬停)/ Normal 240ms(分区/面板)/ Slow 320ms(页面/弹窗);
// 2. 统一缓动:通用 CubicEase.EaseOut,入场带 BackEase(Amplitude 0.3)轻微回弹,不花哨;
// 3. 方向逻辑「从哪展开,关闭就回到哪」:入场自下而上漂移出现,退场向上淡回收拢;
//    弹窗自中心放大入场、回缩退场;下拉自控件下方展开;
// 4. 可打断安全:
//    a) DoubleAnimation 一律不指定 From —— 永远从当前值续接,中途新操作自动平滑改道;
//    b) 代数令牌(Generation):退场完成回调若已被新的入场打断,则不再执行折叠,
//       杜绝"画面已重新展开却被旧回调收起"的错乱。

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FufuLauncher.Next.UI;

internal static class MotionKit
{
    // ---- 统一节拍 ----
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(160);
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(240);
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(320);

    /// <summary>页面级过渡节拍:入场 300ms / 退场 190ms。
    /// 退场刻意比入场短 —— 交叉期约 190ms 内旧页已收干净,后 110ms 只剩新页落位,
    /// 同屏双页并存时间压到最短,既保住衔接感又少一层绘制开销(掉帧主因是双页阴影同时重绘)</summary>
    public static readonly TimeSpan PageIn = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan PageOut = TimeSpan.FromMilliseconds(190);

    /// <summary>通用缓动:三次方收尾(柔和减速)</summary>
    public static IEasingFunction Out() => new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>入场缓动:收尾轻微回弹(幅度克制,只增加"活"感不夸张)</summary>
    public static IEasingFunction OutBack() => new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };

    // ---- 代数令牌:打断安全 ----
    private sealed class GenBox { public int Gen; }
    private static readonly ConditionalWeakTable<UIElement, GenBox> Gens = new();

    private static int NextGen(UIElement el)
    {
        var b = Gens.GetValue(el, _ => new GenBox());
        return ++b.Gen;
    }

    private static bool IsCurrent(UIElement el, int gen) =>
        Gens.TryGetValue(el, out var b) && b.Gen == gen;

    // ---- 基础原语 ----

    /// <summary>数值动画原语:不指定 From,从当前值续接 → 打断自然平滑</summary>
    public static void Anim(Animatable host, DependencyProperty dp, double to, TimeSpan dur,
        IEasingFunction? ease = null, Action? done = null)
    {
        var a = new DoubleAnimation(to, dur) { EasingFunction = ease ?? Out() };
        if (done != null) a.Completed += (_, _) => done();
        host.BeginAnimation(dp, a);
    }

    /// <summary>同上:UIElement 重载(UIElement 不继承 Animatable,是平行动画宿主体系)</summary>
    public static void Anim(UIElement host, DependencyProperty dp, double to, TimeSpan dur,
        IEasingFunction? ease = null, Action? done = null)
    {
        var a = new DoubleAnimation(to, dur) { EasingFunction = ease ?? Out() };
        if (done != null) a.Completed += (_, _) => done();
        host.BeginAnimation(dp, a);
    }

    /// <summary>确保元素挂 TranslateTransform(复用已有实例,不覆盖其它变换需求者自行隔离)</summary>
    public static TranslateTransform EnsureShift(FrameworkElement el)
    {
        if (el.RenderTransform is TranslateTransform t) return t;
        var nt = new TranslateTransform();
        el.RenderTransform = nt;
        return nt;
    }

    /// <summary>确保元素挂中心缩放变换(弹窗入场/退场用)</summary>
    public static ScaleTransform EnsureScale(FrameworkElement el)
    {
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        if (el.RenderTransform is ScaleTransform s) return s;
        var ns = new ScaleTransform();
        el.RenderTransform = ns;
        return ns;
    }

    // ---- 组合动作 ----

    /// <summary>入场:淡入 + 自 fromY 漂移归位;若正处于退场中途(可见且未全透明),直接续接改道不回跳;
    /// done 仅在动画自然播完时触发(被打断不触发,用于过渡后清理缓存等)</summary>
    public static void Enter(UIElement el, double fromY = 16, TimeSpan? dur = null, Action? done = null)
    {
        NextGen(el);
        var d = dur ?? Normal;
        bool retarget = el.Visibility == Visibility.Visible && el.Opacity > 0.05;
        el.Visibility = Visibility.Visible;
        if (!retarget) el.Opacity = 0;
        Anim(el, UIElement.OpacityProperty, 1, d, Out(), done);   // 透明度禁用回弹缓动:BackEase 会越过 1 被钳位,
                                                                  // 结果是前 40% 时间就冲满不透明 → 观感即「闪现」
        if (el is FrameworkElement fe)
        {
            var tt = EnsureShift(fe);
            if (!retarget) tt.Y = fromY;
            Anim(tt, TranslateTransform.YProperty, 0, d, Out());
        }
    }

    /// <summary>退场:淡出 + 向 toY 收拢,完成后折叠(代数守护:被打断则旧回调不生效)</summary>
    public static void Exit(UIElement el, double toY = -10, TimeSpan? dur = null, Action? done = null)
    {
        if (el.Visibility != Visibility.Visible) { done?.Invoke(); return; }
        int gen = NextGen(el);
        var d = dur ?? Fast;
        Anim(el, UIElement.OpacityProperty, 0, d, Out(), () =>
        {
            if (!IsCurrent(el, gen)) return;   // 已被新入场打断:保留现状
            el.Visibility = Visibility.Collapsed;
            done?.Invoke();
        });
        if (el is FrameworkElement fe)
            Anim(EnsureShift(fe), TranslateTransform.YProperty, toY, d, Out());
    }

    /// <summary>立即折叠(不等动画):作废在途退场回调 + 置 Collapsed。
    /// 用于快速连点时收敛残留动画,把同屏过渡恒定限制为「1 退场 + 1 入场」</summary>
    public static void SnapHide(UIElement el)
    {
        NextGen(el);
        if (el.Visibility != Visibility.Collapsed) el.Visibility = Visibility.Collapsed;
    }

    /// <summary>叠放容器(Grid)分区切换:新者入场 + 旧者退场,交叉过渡高度连贯。
    /// 退场刻意比入场短(与页面级 PageIn/PageOut 同理):旧分区尽快收干净,压缩两分区
    /// 同时重绘的重叠窗口,减少大分区(如版本列表/模组列表)切换时的同屏双份绘制掉帧;
    /// 入场保持原节拍,衔接感与原来一致</summary>
    public static void SwapOverlay(IList<UIElement> items, int idx,
        double enterFromY = 16, double exitToY = -10, TimeSpan? dur = null)
    {
        var d = dur ?? Normal;
        var dOut = dur ?? Fast;
        for (int i = 0; i < items.Count; i++)
        {
            if (i == idx) Enter(items[i], enterFromY, d);
            else Exit(items[i], exitToY, dOut);
        }
    }

    /// <summary>堆叠容器(StackPanel)分区切换:旧者立即折叠(避免布局重叠),新者按方向入场(dir≥0 前进自下,否则自上)</summary>
    public static void SwapStacked(IList<UIElement> items, int idx, int dir)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i == idx) continue;
            if (items[i].Visibility == Visibility.Visible)
            {
                NextGen(items[i]);   // 作废其上任何在途退场回调
                items[i].Visibility = Visibility.Collapsed;
            }
        }
        Enter(items[idx], dir >= 0 ? 18 : -18, Normal);
    }

    // ---- 页面级过渡(壳窗口 Deck 切换专用)----

    /// <summary>层级令牌:每过一次页面递增,保证新页恒在退场页之上(叠放 Grid 中后挂载不一定在上层,
    /// 先建的页反而盖住新页时,交叉过渡会看起来像「旧页闪一下才换」)</summary>
    private static int _topZ;

    /// <summary>页面入场:抬层级 + 先布局后开播 + 淡入 + 自 fromY 漂移归位。
    /// 两处防掉帧关键:① UpdateLayout 先把大页面的 measure/arrange 成本消化在动画开始之前,
    /// 否则首帧边算边画必卡;② 透明度用三次方收尾(无回弹),不会出现提前冲满的「闪现」。
    /// 正处于退场中途时直接续接改道(不重置起点),快速连点也不回跳</summary>
    public static void PageEnter(FrameworkElement el, double fromY = 14, TimeSpan? dur = null)
    {
        NextGen(el);
        var d = dur ?? PageIn;
        bool retarget = el.Visibility == Visibility.Visible && el.Opacity > 0.05;
        System.Windows.Controls.Panel.SetZIndex(el, ++_topZ);
        el.Visibility = Visibility.Visible;
        if (!retarget) el.Opacity = 0;
        var tt = EnsureShift(el);
        if (!retarget) tt.Y = fromY;
        el.UpdateLayout();   // 开播前完成一次布局,避免首帧与动画争 UI 线程
        Anim(el, UIElement.OpacityProperty, 1, d, Out());
        Anim(tt, TranslateTransform.YProperty, 0, d, Out());
    }

    /// <summary>页面退场:淡出 + 向上轻微收拢,完成后折叠(代数守护:被新入场打断则旧回调不生效);
    /// 退场在新页之下进行,因此只保留小幅位移,避免两页同时大幅移动相互干扰</summary>
    public static void PageExit(FrameworkElement el, double toY = -10, TimeSpan? dur = null)
    {
        if (el.Visibility != Visibility.Visible) return;
        int gen = NextGen(el);
        var d = dur ?? PageOut;
        Anim(el, UIElement.OpacityProperty, 0, d, Out(), () =>
        {
            if (!IsCurrent(el, gen)) return;   // 已被新入场打断:保留现状
            el.Visibility = Visibility.Collapsed;
        });
        Anim(EnsureShift(el), TranslateTransform.YProperty, toY, d, Out());
    }

    // ---- 弹窗专用 ----

    /// <summary>弹窗入场:0.94 倍中心放大 + 淡入(轻微回弹),初始态由调用方在显示前预置防闪</summary>
    public static void DialogEnter(FrameworkElement root)
    {
        NextGen(root);
        var sc = EnsureScale(root);
        root.Opacity = 0;
        sc.ScaleX = 0.94; sc.ScaleY = 0.94;
        Anim(root, UIElement.OpacityProperty, 1, Slow, OutBack());
        Anim(sc, ScaleTransform.ScaleXProperty, 1, Slow, OutBack());
        Anim(sc, ScaleTransform.ScaleYProperty, 1, Slow, OutBack());
    }

    /// <summary>弹窗退场:回缩至 0.96 + 淡出,完成后回调(关窗/置 DialogResult);打断安全</summary>
    public static void DialogExit(FrameworkElement root, Action done)
    {
        int gen = NextGen(root);
        var sc = EnsureScale(root);
        Anim(root, UIElement.OpacityProperty, 0, Fast, Out());
        Anim(sc, ScaleTransform.ScaleXProperty, 0.96, Fast, Out());
        Anim(sc, ScaleTransform.ScaleYProperty, 0.96, Fast, Out(), () =>
        {
            if (!IsCurrent(root, gen)) return;
            done();
        });
    }
}
