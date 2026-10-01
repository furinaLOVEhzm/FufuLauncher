// FufuLauncher - UI 控件工厂
// Copyright © FufuLauncher
//
// 沉浸式科技美学设计语言:全屏背景 + 悬浮玻璃卡片(柔阴影/统一圆角/细亮边) + 克制的点缀色。
// 全部控件代码构建(规避 wpftmp XAML 编译风险),颜色一律走主题令牌:
//   T.Background T.Surface T.SurfaceAlt T.Foreground T.ForegroundDim
//   T.Primary T.PrimaryHover T.Border T.Success T.Warning T.Danger
//   T.Overlay T.PrimarySoft T.HoverFill T.PressedFill
//
// 模板实现要点(历史教训固化):
// - FrameworkElementFactory 命名用 .Name 属性(SetValue(NameProperty) 不进模板名表);
// - 含 Track 的模板(Slider/ScrollBar)走 XamlReader.Parse 字符串(Track 非 IAddChild);
// - ComboBox 选中项绑定公开的 SelectionBoxItem/SelectionBoxItemTemplate。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FufuLauncher.Next.UI;

public static class UIKit
{
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(130));

    // ==================== 视觉规格档位(2026-09-26 统一) ====================
    // 圆角只允许四档,新增控件一律引用档位常量,禁止再写裸数值:
    //   Chip  8   —— 小芯片/徽章/输入框/图标底/导航项/标签
    //   Panel 12  —— 列表行/面板/弹层/页签/侧栏悬浮层
    //   Card  16  —— 卡片/对话框
    //   Pill 999  —— 胶囊(WPF 自动收敛为较短边的一半):开关/滑块轨道/头像/状态胶囊/小标签
    public static class R
    {
        public const double Chip = 8;
        public const double Panel = 12;
        public const double Card = 16;
        public const double Pill = 999;
    }

    /// <summary>DynamicResource 简写</summary>
    public static DynamicResourceExtension DR(string key) => new(key);

    // ==================== 文本 ====================

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, string brushKey = "T.Foreground",
        bool wrap = false)
    {
        var tb = new TextBlock { Text = text, FontSize = size };
        if (wrap) tb.TextWrapping = TextWrapping.Wrap;
        if (weight.HasValue) tb.FontWeight = weight.Value;
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return tb;
    }

    /// <summary>辅助文字(暗色令牌)。默认开换行:状态/说明/错误/路径等长文本在有限宽容器内
    /// 自动换行,根治文本撑破卡片或溢出裁切;放水平 StackPanel 内测量无限宽不触发换行,行为不变。</summary>
    public static TextBlock Sub(string text, double size = 12)
    {
        var tb = Text(text, size, brushKey: "T.ForegroundDim", wrap: true);
        tb.VerticalAlignment = VerticalAlignment.Center;   // 2026-09-25:水平行内与输入框同基线,根治文字顶对齐错位
        return tb;
    }

    /// <summary>单行辅助文字(暗色令牌):水平行内过长省略号收尾,不换行不撑宽(版本信息/引擎徽章等)</summary>
    public static TextBlock SubOneLine(string text, double size = 12, double maxWidth = 420)
    {
        var tb = Text(text, size, brushKey: "T.ForegroundDim", wrap: false);
        tb.TextTrimming = TextTrimming.CharacterEllipsis;
        tb.MaxWidth = maxWidth;
        tb.VerticalAlignment = VerticalAlignment.Center;   // 同 Sub:横排与控件同基线
        return tb;
    }

    public static TextBlock PageTitle(string text) => Text(text, 20, FontWeights.Bold);

    // ==================== 容器 ====================

    /// <summary>标准柔阴影(悬浮感:向下 4px + 16px 晕染,性能模式渲染)</summary>
    public static DropShadowEffect SoftShadow(double opacity = 0.35)
        => new() { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = opacity, RenderingBias = RenderingBias.Performance };

    /// <summary>主卡片(双层结构:阴影独立层避免 Effect 栅格化文字 + 内容层大圆角细边)</summary>
    public static FrameworkElement Card(UIElement child, double pad = 22, double topGap = 16)
    {
        var shadowLayer = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Card),
            Effect = SoftShadow()
        };
        shadowLayer.SetResourceReference(Border.BackgroundProperty, "T.Surface");

        var content = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Card),
            Padding = new Thickness(pad),
            BorderThickness = new Thickness(1),
            Child = child
        };
        content.WithRef("T.Surface", "T.Border");

        return new Grid
        {
            Margin = new Thickness(0, topGap, 0, 0),
            Children = { shadowLayer, content }
        };
    }

    /// <summary>轻量面板(列表行底)</summary>
    public static Border Panel(UIElement child, double pad = 12)
    {
        var p = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Panel),
            Padding = new Thickness(pad),
            Child = child
        };
        p.WithRef("T.SurfaceAlt", "T.Border");
        return p;
    }

    /// <summary>页眉:标题 + 副标题 + 右侧操作按钮组</summary>
    public static Grid PageHeader(string title, string subtitle, params UIElement[] actions)
    {
        // 副标题在右侧按钮组过宽、左列被挤压时自动换行完整显示,
        // 避免尾部文字被按钮遮挡裁切(模组管理页 5 按钮挤窄左列即此因)。
        var subtitleTb = Sub(subtitle, 12);
        subtitleTb.TextWrapping = TextWrapping.Wrap;
        var left = V(PageTitle(title), subtitleTb.MarginTop(6));
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { left }
        };
        if (actions.Length > 0)
        {
            // 2026-09-26:按钮组改 WrapPanel 自动换行——工具栏按钮偏多(如模组管理页 5 个)时
            // 横向 StackPanel 会把右列挤爆/按钮被窗口裁切,换行后任何宽度都不溢出。
            var right = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var a in actions) right.Children.Add(a);
            grid.Children.Add(right.Col(1));
        }
        return grid;
    }

    /// <summary>空态提示(柔和虚线框)</summary>
    public static Border EmptyHint(string text)
    {
        var hint = Sub(text, 12.5);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.Margin = new Thickness(0, 10, 0, 10);
        var box = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Panel),
            BorderThickness = new Thickness(1.2),
            Padding = new Thickness(20, 18, 20, 18),   // 2026-09-26 收紧:空态提示更紧凑
            Margin = new Thickness(0, 8, 0, 4),
            Child = hint
        };
        box.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        box.SetResourceReference(Border.BackgroundProperty, "T.Overlay");
        return box;
    }

    /// <summary>小徽章(圆角胶囊,全局统一规格:12px 文字 + 舒展内边距,禁止页面私自缩小)</summary>
    public static Border Badge(string text, string brushKey)
    {
        // 内容超长(如加载器名+版本号)单行省略 + 悬停看全名,杜绝徽章把卡片撑破
        var tb = Text(text, 12, FontWeights.Medium, "T.Surface");
        tb.TextTrimming = TextTrimming.CharacterEllipsis;
        tb.MaxWidth = 220;
        if (!string.IsNullOrEmpty(text) && text.Length > 18) tb.ToolTip = text;
        var badge = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = tb
        };
        badge.SetResourceReference(Border.BackgroundProperty, brushKey);
        return badge;
    }

    /// <summary>警示徽章(T.Warning 琥珀底,白字 + ToolTip 说明)——空壳加载器版本等异常状态提示</summary>
    public static Border WarningBadge(string text, string tooltip)
    {
        var badge = Badge(text, "T.Warning");
        badge.ToolTip = tooltip;
        return badge;
    }

    // ==================== 按钮 ====================

    /// <summary>标准按钮:primary = 主色填充 + 悬浮投影,secondary = 玻璃面(半透明白雾 + 描边,悬停描边主色化)。
    /// 圆角随高度等比缩放(与主页启动大按钮同一比例),保证各尺寸都是“长方形 + 圆角”观感</summary>
    public static Button Button(string text, bool primary, Action? onClick = null, double height = 36,
        bool debounce = true)
    {
        double radius = Math.Clamp(height * 0.22, 6, 11);   // 2026-09-26 圆角回退:按钮缩小后更协调
        var label = Text(text, 13, FontWeights.Medium, primary ? "T.Surface" : "T.Foreground");   // 2026-09-26 文字回 13:缩小后不挤
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;

        var btnContent = new Grid();
        if (primary)
        {
            // 悬浮投影层(独立 Border 仅承载 Effect,不栅格化文字)
            var glow = new Border
            {
                CornerRadius = new CornerRadius(radius),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.35,
                    RenderingBias = RenderingBias.Performance
                }
            };
            glow.SetResourceReference(Border.BackgroundProperty, "T.Primary");
            btnContent.Children.Add(glow);
        }

        var baseLayer = new Border { Name = "baseLayer", CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(primary ? 0 : 1) };
        baseLayer.SetResourceReference(Border.BackgroundProperty, primary ? "T.Primary" : "T.HoverFill");
        if (!primary) baseLayer.SetResourceReference(Border.BorderBrushProperty, "T.Border");

        var hoverLayer = new Border { CornerRadius = new CornerRadius(radius), Opacity = 0, BorderThickness = new Thickness(primary ? 0 : 1) };
        hoverLayer.SetResourceReference(Border.BackgroundProperty, primary ? "T.PrimaryHover" : "T.PressedFill");
        if (!primary) hoverLayer.SetResourceReference(Border.BorderBrushProperty, "T.Primary");

        btnContent.Children.Add(baseLayer);
        btnContent.Children.Add(hoverLayer);
        btnContent.Children.Add(label);

        var btn = new Button
        {
            Content = btnContent,
            Height = height,
            Padding = new Thickness(14, 0, 14, 0),   // 2026-09-26 内边距 20→14:按钮紧凑缩小
            Template = GhostButtonTemplate(),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        // 点击反馈:按下时按钮整块轻微内缩(0.97),松开/移出弹回 1.0 —— 纯 RenderTransform,
        // 不改变布局占位,补上此前只有 hover、缺少「按下去了」手感的空白(挂在 btn 本体,
        // 故即使调用方替换了 Content(如发送键)也照样生效)
        var pressScale = new ScaleTransform(1, 1);
        btn.RenderTransformOrigin = new Point(0.5, 0.5);
        btn.RenderTransform = pressScale;
        void PressIn()
        {
            MotionKit.Anim(pressScale, ScaleTransform.ScaleXProperty, 0.97, MotionKit.Fast);
            MotionKit.Anim(pressScale, ScaleTransform.ScaleYProperty, 0.97, MotionKit.Fast);
        }
        void PressOut()
        {
            MotionKit.Anim(pressScale, ScaleTransform.ScaleXProperty, 1, MotionKit.Fast);
            MotionKit.Anim(pressScale, ScaleTransform.ScaleYProperty, 1, MotionKit.Fast);
        }
        btn.MouseEnter += (_, _) =>
        {
            hoverLayer.FadeTo(1, Fast);
            if (!primary) baseLayer.SetResourceReference(Border.BorderBrushProperty, "T.Primary");
        };
        btn.MouseLeave += (_, _) =>
        {
            hoverLayer.FadeTo(0, Fast);
            PressOut();   // 按下后又拖出按钮:一并弹回,避免卡在缩放态
            if (!primary) baseLayer.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        };
        btn.PreviewMouseLeftButtonDown += (_, _) => PressIn();
        btn.PreviewMouseLeftButtonUp += (_, _) => PressOut();
        if (onClick != null)
        {
            if (debounce)
            {
                // 双击/连点防抖:400ms 内忽略第二次点击。WPF Button 无内置防抖,
                // 双击启动/删除/安装会连触发两次(曾出现连开两个游戏进程/连弹两个窗)。
                long lastClick = 0;
                btn.Click += (_, _) =>
                {
                    long now = Environment.TickCount64;
                    if (now - lastClick < 400) return;
                    lastClick = now;
                    onClick();
                };
            }
            else
                btn.Click += (_, _) => onClick();
        }
        return btn;
    }

    /// <summary>改按钮文字(保留多层结构,仅替换文本)</summary>
    public static void SetButtonText(Button btn, string text)
    {
        if (btn.Content is Grid g && g.Children.Count >= 3 && g.Children[^1] is TextBlock tb)
            tb.Text = text;
    }

    /// <summary>透明底按钮(导航/图标按钮载体)</summary>
    public static Button GhostButton(UIElement content, Action onClick, double height = 40)
    {
        var btn = new Button
        {
            Content = content,
            Height = height,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = GhostButtonTemplate(),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        long lastClick = 0;
        btn.Click += (_, _) =>
        {
            long now = Environment.TickCount64;
            if (now - lastClick < 400) return;
            lastClick = now;
            onClick();
        };
        return btn;
    }

    /// <summary>透明按钮模板(背景全由 Content 自绘)</summary>
    public static ControlTemplate GhostButtonTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(ContentPresenter));
        factory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        factory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        return new ControlTemplate(typeof(Button)) { VisualTree = factory };
    }

    // ==================== 输入控件 ====================

    /// <summary>文本框(圆角 + 占位符水印;控件高度对齐 Ant Design 大号输入控件 40px)</summary>
    public static TextBox TextBox(string initial = "", string? placeholder = null)
    {
        var tb = new TextBox
        {
            Text = initial,
            FontSize = 13,
            Padding = new Thickness(12, 10, 12, 10),
            Template = TextBoxTemplate(placeholder)
        };
        tb.SetResourceReference(Control.BackgroundProperty, "T.SurfaceAlt");
        tb.SetResourceReference(Control.ForegroundProperty, "T.Foreground");
        tb.SetResourceReference(Control.BorderBrushProperty, "T.Border");
        tb.SetResourceReference(System.Windows.Controls.TextBox.CaretBrushProperty, "T.Primary");
        tb.SetResourceReference(System.Windows.Controls.TextBox.SelectionBrushProperty, "T.PrimarySoft");
        if (!string.IsNullOrEmpty(placeholder))
        {
            tb.ToolTip = placeholder;
            // 常规输入框行为:有内容时占位提示消失,清空后恢复(代码驱动,避开模板 Trigger 限制)
            TextBlock? wm = null;
            void SyncWatermark()
            {
                wm ??= tb.Template.FindName("watermark", tb) as TextBlock;
                if (wm != null)
                    wm.Visibility = string.IsNullOrEmpty(tb.Text) ? Visibility.Visible : Visibility.Collapsed;
            }
            tb.Loaded += (_, _) => { tb.ApplyTemplate(); SyncWatermark(); };
            tb.TextChanged += (_, _) => SyncWatermark();
        }
        return tb;
    }

    /// <summary>下拉框(全局统一规格:宽 320 × 高 38,字号 13,圆角 11;弹出面板与控件等宽)</summary>
    public static ComboBox ComboBox()
    {
        var cb = new ComboBox
        {
            FontSize = 13,
            Padding = new Thickness(12, 9, 12, 9),
            Height = 38,      // 全局统一规格:高 38
            Width = 320,      // 全局统一规格:宽 320(页面不再各自覆写)
            Template = ComboBoxTemplate(),
            ItemContainerStyle = ComboBoxItemStyle()
        };
        cb.SetResourceReference(Control.BackgroundProperty, "T.SurfaceAlt");
        cb.SetResourceReference(Control.ForegroundProperty, "T.Foreground");
        cb.SetResourceReference(Control.BorderBrushProperty, "T.Border");
        return cb;
    }

    /// <summary>勾选框(22px 圆角方块 + 白勾,多选/列表启用态场景;尺寸对齐 Fluent 2 触控标准)</summary>
    public static CheckBox CheckBox(string text, bool isChecked = false)
    {
        var chk = new CheckBox { Content = text, IsChecked = isChecked, FontSize = 13, Template = CheckBoxTemplate() };
        chk.SetResourceReference(Control.ForegroundProperty, "T.Foreground");
        return chk;
    }

    /// <summary>开关式切换(54×30 圆角矩形轨道 + 24px 滑块 + 主色轨道 + 跟随主题的滑块配色,形状按用户截图还原为圆角矩形非胶囊)。
    /// 派生自 CheckBox,Checked/Unchecked/IsChecked 契约不变,二值设置页直接用</summary>
    public sealed class ToggleSwitch : CheckBox
    {
        public ToggleSwitch(string text, bool isChecked = false)
        {
            Content = text;
            FontSize = 13;
            Template = SwitchTemplate();
            SetResourceReference(Control.ForegroundProperty, "T.Foreground");
            Cursor = System.Windows.Input.Cursors.Hand;
            // 点击轨道任意位置即翻转(CheckBox 默认已支持)

            // 选中态视觉(滑块位移 + 轨道主色 + 滑块跟随主题)全部走实例事件驱动:
            // 代码模板的名字注册 NameScope 不可靠,Storyboard/Trigger/FindName 均会失效或崩溃
            Loaded += (_, _) => ApplyVisual(animate: false);
            Checked += (_, _) => ApplyVisual(animate: true);
            Unchecked += (_, _) => ApplyVisual(animate: true);
            IsChecked = isChecked;
        }

        /// <summary>按当前选中态同步轨道/滑块视觉(animate=false 用于初始归位)</summary>
        private void ApplyVisual(bool animate)
        {
            var knob = FindKnob();
            if (knob == null) return;   // 模板尚未应用,Loaded 后补位
            var track = knob.Parent as Border;
            bool on = IsChecked == true;
            SetKnobX(knob, on ? SwitchKnobTravel : 0, animate);
            if (track != null)
            {
                // 开:整条主色实心胶囊;关:整条中性实心胶囊 —— 两态同一套构造。
                // 颜色全部走主题令牌(T.Primary/T.Border),主题设置里换主色时开关一起变(用户要求)。
                // 形状保持按用户截图逐像素还原的规格(54×30 胶囊轨道 + 24px 滑块 + 位移 24),不再改动。
                // 关态填色用 T.Border 而非 T.SurfaceAlt(2026-09-27 用户实测反馈):
                // SurfaceAlt 在深色主题下几乎与卡片面同色,填充看不出来,只剩 1px 描边,
                // 整颗开关呈「空心轮廓」而不是实心形状;Border 填满后两态都是「实心胶囊 + 对比色圆点」。
                track.SetResourceReference(Border.BackgroundProperty, on ? "T.Primary" : "T.Border");
                track.SetResourceReference(Border.BorderBrushProperty, on ? "T.Primary" : "T.Border");
            }
            // 滑块配色跟随主题:开态用 T.Surface(卡片面令牌,深色主题下叠在主色轨道上合成深色圆点);
            // 关态用次级前景做中性灰点。都是主题令牌,随主题切换即时生效(用户要求颜色跟主题一起变)
            knob.SetResourceReference(Ellipse.FillProperty, on ? "T.Surface" : "T.ForegroundDim");
        }

        private Ellipse? FindKnob()
        {
            // 代码模板的名字注册不可靠(FindName/Storyboard 均解析不到),
            // 直接下探可视树找唯一 Ellipse;模板未应用时子元素为空自然返回 null
            static Ellipse? Walk(DependencyObject o)
            {
                int n = VisualTreeHelper.GetChildrenCount(o);
                for (int i = 0; i < n; i++)
                {
                    var c = VisualTreeHelper.GetChild(o, i);
                    if (c is Ellipse e) return e;
                    var hit = Walk(c);
                    if (hit != null) return hit;
                }
                return null;
            }
            return Walk(this);
        }

        private static void SetKnobX(Ellipse knob, double x, bool animate)
        {
            // 工厂模板预置的 RenderTransform 会被冻结且全实例共享,不可写;
            // 这里按需为每个滑块新建独立的可写变换
            if (knob.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
            {
                tt = new TranslateTransform();
                knob.RenderTransform = tt;
            }
            if (!animate) { tt.X = x; return; }
            var anim = new DoubleAnimation(x, Fast) { EasingFunction = new QuadraticEase() };
            tt.BeginAnimation(TranslateTransform.XProperty, anim);
        }
    }

    // ---- 开关尺寸(2026-09-27 按截图逐像素实测还原):54×30 胶囊轨道 + 24px 滑块。
    //      实测依据:用户截图 140×75,125% DPI 下轨道 bbox = 67×38 物理像素
    //      → 67/1.25≈54、38/1.25≈30;滑块 30 物理 → 24 逻辑。
    //      位移固定 24(而非几何对称的 22):原版即如此,滑块贴右边 2px,
    //      与截图实测的右边距一致 —— 这次要的是「一模一样」,不擅自"修正"。
    private const double SwitchTrackW = 54;
    private const double SwitchTrackH = 30;
    private const double SwitchKnobD = 24;
    private const double SwitchKnobPad = 3;   // 滑块左侧留白(轨道 1px 描边之外)
    /// <summary>滑块开态位移(原版值,勿按几何对称改成 22/25)</summary>
    private const double SwitchKnobTravel = 24;
    /// <summary>轨道圆角:8px 小圆角(圆角矩形,不是胶囊椭圆)。2026-09-27 用户明确要求</summary>
    private const double SwitchTrackCorner = 14; // 更圆润的圆角矩形(8太方、15胶囊、14接近参考图,仍有水平段非椭圆)

    /// <summary>开关模板:圆角矩形轨道(8px 小圆角) + 圆形滑块,选中时滑块右移 + 轨道主色化(动画过渡)</summary>
    private static ControlTemplate SwitchTemplate()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var track = new FrameworkElementFactory(typeof(Border));
        track.Name = "track";
        track.SetValue(Border.WidthProperty, SwitchTrackW);
        track.SetValue(Border.HeightProperty, SwitchTrackH);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(SwitchTrackCorner)); // 圆角矩形(小圆角,非胶囊椭圆,用户要求)
        track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        track.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
        track.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("T.Border"));
        track.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("T.Border"));
        root.AppendChild(track);

        var knob = new FrameworkElementFactory(typeof(Ellipse));
        knob.Name = "knob";
        knob.SetValue(Ellipse.WidthProperty, SwitchKnobD);
        knob.SetValue(Ellipse.HeightProperty, SwitchKnobD);
        knob.SetValue(Ellipse.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        knob.SetValue(Ellipse.VerticalAlignmentProperty, VerticalAlignment.Center);
        knob.SetValue(Ellipse.MarginProperty, new Thickness(SwitchKnobPad, 0, 0, 0));
        knob.SetValue(Ellipse.FillProperty, new DynamicResourceExtension("T.ForegroundDim"));
        // 注意:不预置 RenderTransform——工厂共享值会被冻结,位移动画由 SetKnobX 按需创建
        track.AppendChild(knob);

        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(ContentPresenter.MarginProperty, new Thickness(10, 0, 0, 0));
        label.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        root.AppendChild(label);

        var tpl = new ControlTemplate(typeof(CheckBox)) { VisualTree = root };

        // 选中态视觉全部由代码驱动(名字注册在代码模板里解析不可靠,Trigger TargetName 一并废弃):
        // 轨道主色化/复原 + 滑块配色跟随主题/复原在 ToggleSwitch 实例事件里完成
        return tpl;
    }

    /// <summary>进度条(圆角胶囊;默认高度 8,圆角随高度自适应)</summary>
    public static ProgressBar ProgressBar(double value = 0)
    {
        var p = new ProgressBar { Value = value, Minimum = 0, Maximum = 100, Height = 8, Template = ProgressBarTemplate() };
        p.SetResourceReference(System.Windows.Controls.ProgressBar.ForegroundProperty, "T.Primary");
        p.SetResourceReference(System.Windows.Controls.ProgressBar.BackgroundProperty, "T.SurfaceAlt");
        return p;
    }

    /// <summary>滑块(圆钮 + 主色已选轨)</summary>
    public static Slider Slider() => new() { Template = SliderTemplate(), IsMoveToPointEnabled = true };

    /// <summary>进度条数值平滑过渡(默认 220ms 缓动):值变化时从当前位置滑到新值,
    /// 消除进度「一格一跳」的生硬感;高频刷新(下载进度)自动续接改道,不排队不抖动。
    /// 注意:过渡期间 Value 读取为动画中间值,业务判定请以数据源为准</summary>
    public static void SmoothSet(this ProgressBar bar, double value, TimeSpan? dur = null)
    {
        if (bar.IsIndeterminate) { bar.Value = value; return; }
        var a = new DoubleAnimation(value, dur ?? TimeSpan.FromMilliseconds(220)) { EasingFunction = MotionKit.Out() };
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, a);
    }

    /// <summary>进度条立即归位(清掉在途过渡动画后再赋值):新一轮任务开始/重试前复位用,
    /// 避免出现「先倒着退回去再重新涨」的怪异观感</summary>
    public static void ResetProgress(this ProgressBar bar, double value = 0)
    {
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        bar.Value = value;
    }

    /// <summary>色相专用滑块:透明轨道(光谱底由外部铺) + 白环取色钮,取色器行业标准形态</summary>
    public static Slider HueSlider() => new() { Template = HueSliderTemplate(), IsMoveToPointEnabled = true };

    // ==================== 布局 ====================

    public static StackPanel H(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < children.Length; i++)
        {
            var c = children[i];
            // 相邻按钮间统一 8px 呼吸间距(已有自定义 Margin 的不动)
            if (i > 0 && c is Button { Margin.Left: 0 } b)
                b.Margin = new Thickness(8, b.Margin.Top, b.Margin.Right, b.Margin.Bottom);
            sp.Children.Add(c);
        }
        return sp;
    }

    public static StackPanel V(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    /// <summary>设置行:左标题+说明,右控件,可选分隔线。
    /// 输入框类控件未显式定宽时限制上限,避免 Auto 列被期望宽度撑得过长</summary>
    public static Border SettingRow(string title, string hint, UIElement control, bool divider = true)
    {
        // 输入类控件未显式定宽时限制上限,避免 Auto 列被期望宽度撑得过长
        if (control is TextBox { Width: double.NaN } tb) tb.MaxWidth = 340;
        if (control is ComboBox { Width: double.NaN } cb) cb.MaxWidth = 320;
        var hintTb = Sub(hint, 12).MarginTop(4);
        hintTb.TextWrapping = TextWrapping.Wrap;   // 窄窗口下说明文字换行,杜绝裁切溢出
        var left = V(Text(title, 14, FontWeights.Medium), hintTb);
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.HorizontalAlignment = HorizontalAlignment.Right;   // 2026-09-25:右列控件右对齐,按钮/开关/输入框右缘统一贴右侧,杜绝参差
        }
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { left, control.Col(1) }
        };
        var wrap = new Border { Padding = new Thickness(2, 17, 2, 17), Child = grid };
        if (divider)
        {
            wrap.BorderThickness = new Thickness(0, 0, 0, 1);
            wrap.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        }
        return wrap;
    }

    public static Border SettingRowNoDivider(string title, string hint, UIElement control)
        => SettingRow(title, hint, control, divider: false);

    // ==================== 工具 ====================

    public static string FmtBytes(long b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024 / 1024:0.00} GB",
        >= 1024L * 1024 => $"{b / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{b / 1024.0:0} KB",
        _ => $"{b} B"
    };

    /// <summary>透明度过渡动画(悬停/指示条淡入淡出)。统一走全局动效中心的房子曲线 CubicEase.EaseOut:
    /// 即时起步、柔和收尾,与页面/面板过渡同一手感(原 QuadraticEase 默认 EaseInOut 起步偏慢,快速悬停略显迟滞)</summary>
    public static void FadeTo(this UIElement el, double target, Duration duration)
    {
        var anim = new DoubleAnimation(target, duration) { EasingFunction = MotionKit.Out() };
        el.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>Y 轴微位移动画(悬停上浮;未预置 TranslateTransform 时自动创建)。曲线同 FadeTo,与全局动效一致</summary>
    public static void Lift(this FrameworkElement el, double y)
    {
        if (el.RenderTransform is not TranslateTransform tt)
        {
            tt = new TranslateTransform();
            el.RenderTransform = tt;
        }
        var anim = new DoubleAnimation(y, Fast) { EasingFunction = MotionKit.Out() };
        tt.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    /// <summary>统一卡片悬停手感:指针移入轻微上浮、移出复原(默认 2px)。
    /// 纯 RenderTransform 不改变布局占位;用于列表卡片补齐「可交互」反馈,消除静态生硬感</summary>
    public static T HoverLift<T>(this T el, double y = -2) where T : FrameworkElement
    {
        el.MouseEnter += (_, _) => el.Lift(y);
        el.MouseLeave += (_, _) => el.Lift(0);
        return el;
    }

    /// <summary>空态/提示类元素的柔和显隐:显示时淡入 + 自下轻微漂移,隐藏时淡出收回,
    /// 替代硬切 Visibility(消除提示「啪一下蹦出来/不见」的生硬感)。可反复调用,动画自动续接</summary>
    public static void FadeToggle(this UIElement el, bool show, double fromY = 8)
    {
        if (show) MotionKit.Enter(el, fromY);
        else MotionKit.Exit(el, -fromY, MotionKit.Fast);
    }

    /// <summary>一次性绑定底色+描边令牌</summary>
    public static Border WithRef(this Border b, string bgKey, string borderKey)
    {
        b.SetResourceReference(Border.BackgroundProperty, bgKey);
        b.SetResourceReference(Border.BorderBrushProperty, borderKey);
        return b;
    }

    /// <summary>细滚动条全局化:向令牌字典注册隐式 ScrollBar 样式(含弹窗下拉)</summary>
    public static void InstallScrollBarChrome(FrameworkElement root)
    {
        var dict = Theme.ThemeManager.TokenDict;
        if (dict.Contains(typeof(System.Windows.Controls.Primitives.ScrollBar))) return;
        dict[typeof(System.Windows.Controls.Primitives.ScrollBar)] = ScrollBarImplicitStyle();
    }

    // ==================== 控件模板 ====================

    private static ControlTemplate TextBoxTemplate(string? placeholder)
    {
        const string xamlHead =
            "<ControlTemplate TargetType='TextBox' " +
            "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>";
        // 水印默认隐藏,由工厂代码订阅 TextChanged 同步可见性(不用模板 Trigger——
        // ControlTemplate.Triggers 的 Setter 不支持 TargetName,会抛 KeyNotFoundException)
        string watermark = string.IsNullOrEmpty(placeholder) ? "" :
            "<TextBlock x:Name='watermark' Text='" + placeholder + "' Opacity='0.45' FontSize='13' " +
            "Foreground='{DynamicResource T.ForegroundDim}' Margin='13,0,0,0' " +
            "VerticalAlignment='Center' IsHitTestVisible='False' Visibility='Collapsed'/>";
        string xaml = xamlHead +
            "<Border x:Name='bd' CornerRadius='11' Background='{TemplateBinding Background}' " +
            "BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'>" +
            "<Grid>" + watermark +
            "<ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='Center' Margin='2,0,0,0'/>" +
            "</Grid></Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='IsKeyboardFocusWithin' Value='True'>" +
            // Setter.Value 必须用元素语法承载 DynamicResource:属性语法 '{DynamicResource}'
            // 在 XamlReader.Parse 下会抛 XamlParseException(2026-08-26 线上崩溃实锤)
            "<Setter TargetName='bd' Property='BorderBrush'>" +
            "<Setter.Value><DynamicResource ResourceKey='T.Primary'/></Setter.Value>" +
            "</Setter>" +
            "</Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>";
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static ControlTemplate ComboBoxTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));

        var bd = new FrameworkElementFactory(typeof(Border));
        bd.Name = "boxBd";
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(UIKit.R.Chip));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        bd.SetValue(Border.PaddingProperty, new Thickness(12, 9, 30, 9));
        root.AppendChild(bd);

        // 选中项展示(SelectedContent DP 非公开,用公开的 SelectionBoxItem)
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.ContentProperty,
            new TemplateBindingExtension(System.Windows.Controls.ComboBox.SelectionBoxItemProperty));
        cp.SetValue(ContentPresenter.ContentTemplateProperty,
            new TemplateBindingExtension(System.Windows.Controls.ComboBox.SelectionBoxItemTemplateProperty));
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        cp.SetValue(ContentPresenter.IsHitTestVisibleProperty, false);
        bd.AppendChild(cp);

        // 右侧下拉箭头(不参与命中,点击由顶层 ToggleButton 统一接管)
        var arrow = new FrameworkElementFactory(typeof(Path));
        arrow.SetValue(Path.DataProperty, Geometry.Parse("M 0 0 L 4 4 L 8 0"));
        arrow.SetValue(Path.StrokeThicknessProperty, 1.5);
        arrow.SetValue(Path.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(Path.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(Path.MarginProperty, new Thickness(0, 0, 12, 0));
        arrow.SetValue(Path.StretchProperty, Stretch.None);
        arrow.SetValue(Shape.StrokeProperty, new DynamicResourceExtension("T.ForegroundDim"));
        arrow.SetValue(UIElement.IsHitTestVisibleProperty, false);
        root.AppendChild(arrow);

        // 下拉气泡(必须显式 Placement=Bottom + 定位到宿主控件,否则弹到屏幕绝对坐标 0,0 导致全部下拉不可用)
        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.StaysOpenProperty, false);
        popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.PlacementTargetProperty,
            new Binding { RelativeSource = RelativeSource.TemplatedParent });
        popup.SetValue(Popup.IsOpenProperty,
            new Binding { Path = new PropertyPath(System.Windows.Controls.ComboBox.IsDropDownOpenProperty), RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay });
        root.AppendChild(popup);

        var popBd = new FrameworkElementFactory(typeof(Border));
        popBd.SetValue(Border.CornerRadiusProperty, new CornerRadius(UIKit.R.Panel));
        popBd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        popBd.SetValue(Border.PaddingProperty, new Thickness(4));
        // 弹出面板宽度与下拉框控件宽度严格一致(全局统一规格)
        popBd.SetValue(Border.WidthProperty,
            new Binding { Path = new PropertyPath(FrameworkElement.ActualWidthProperty), RelativeSource = RelativeSource.TemplatedParent });
        popBd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("T.Surface"));
        popBd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("T.Border"));
        popBd.SetValue(FrameworkElement.StyleProperty, ComboBoxPopupStyle());   // 开合缩放动画(自控件底缘生长/回缩)
        popup.AppendChild(popBd);

        var sv = new FrameworkElementFactory(typeof(ScrollViewer));
        sv.SetValue(ScrollViewer.MaxHeightProperty,
            new Binding { Path = new PropertyPath(System.Windows.Controls.ComboBox.MaxDropDownHeightProperty), RelativeSource = RelativeSource.TemplatedParent });
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        // 宽度已钉死为控件宽度,禁用横向滚动避免内容撑宽弹层
        sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        popBd.AppendChild(sv);

        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        sv.AppendChild(items);

        // 全幅透明 ToggleButton(置顶命中层):标准模板正是靠它接收点击并切换展开态,
        // 缺失它时 ComboBox 本体不响应点击 —— 这就是之前全部下拉不可用的真正根因
        var toggle = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.ToggleButton));
        toggle.SetValue(FrameworkElement.FocusableProperty, false);
        toggle.SetValue(System.Windows.Controls.Primitives.ToggleButton.IsTabStopProperty, false);
        toggle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        toggle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        toggle.SetValue(System.Windows.Controls.Control.TemplateProperty, TransparentToggleTemplate());
        toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding
        {
            Path = new PropertyPath(System.Windows.Controls.ComboBox.IsDropDownOpenProperty),
            RelativeSource = RelativeSource.TemplatedParent,
            Mode = BindingMode.TwoWay
        });
        root.AppendChild(toggle);

        var tpl = new ControlTemplate(typeof(System.Windows.Controls.ComboBox)) { VisualTree = root };
        tpl.Triggers.Add(new Trigger
        {
            Property = System.Windows.Controls.ComboBox.IsMouseOverProperty,
            Value = true,
            Setters = { new Setter(Border.BorderBrushProperty, DR("T.Primary"), "boxBd") }
        });
        return tpl;
    }

    /// <summary>下拉弹层开合动画样式:顶部锚定缩放 —— 展开自控件底缘向下生长(轻微回弹),
    /// 收起向上回缩淡出,配合 Popup 窗口级渐隐,贴合「从哪展开,关闭就回到哪」。
    /// 动画一律不指定 From(从当前值续接)→ 快速开合可打断改道不闪跳;
    /// DataTrigger 不设 TargetName(Style 中合法,与模板 Trigger 不同)。</summary>
    private static Style ComboBoxPopupStyle()
    {
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(UIElement.RenderTransformOriginProperty, new Point(0.5, 0)));
        style.Setters.Add(new Setter(FrameworkElement.RenderTransformProperty, new ScaleTransform(1, 0.72)));

        // 展开:0.72 → 1 纵向生长 + 快速淡入(BackEase 轻微回弹增加"活"感,幅度克制)
        var openSb = new Storyboard();
        var openScale = new DoubleAnimation
        {
            To = 1, Duration = MotionKit.Normal,
            EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTargetProperty(openScale,
            new PropertyPath("(FrameworkElement.RenderTransform).(ScaleTransform.ScaleY)"));
        var openFade = new DoubleAnimation { To = 1, Duration = MotionKit.Fast, EasingFunction = MotionKit.Out() };
        Storyboard.SetTargetProperty(openFade, new PropertyPath(UIElement.OpacityProperty));
        openSb.Children.Add(openScale);
        openSb.Children.Add(openFade);

        // 收起:回缩至 0.72 + 淡出(窗口级 PopupAnimation.Fade 同步渐隐,双保险不闪)
        var closeSb = new Storyboard();
        var closeScale = new DoubleAnimation
        {
            To = 0.72, Duration = MotionKit.Fast,
            EasingFunction = MotionKit.Out()
        };
        Storyboard.SetTargetProperty(closeScale,
            new PropertyPath("(FrameworkElement.RenderTransform).(ScaleTransform.ScaleY)"));
        var closeFade = new DoubleAnimation { To = 0, Duration = MotionKit.Fast, EasingFunction = MotionKit.Out() };
        Storyboard.SetTargetProperty(closeFade, new PropertyPath(UIElement.OpacityProperty));
        closeSb.Children.Add(closeScale);
        closeSb.Children.Add(closeFade);

        // 单一 DataTrigger 承载开合两态:EnterActions=展开,ExitActions=收起
        var openState = new DataTrigger
        {
            Binding = new Binding
            {
                Path = new PropertyPath(System.Windows.Controls.ComboBox.IsDropDownOpenProperty),
                RelativeSource = RelativeSource.TemplatedParent
            },
            Value = true
        };
        openState.EnterActions.Add(new BeginStoryboard { Storyboard = openSb });
        openState.ExitActions.Add(new BeginStoryboard { Storyboard = closeSb });

        style.Triggers.Add(openState);
        return style;
    }

    /// <summary>透明 ToggleButton 模板(仅承载点击,不渲染任何视觉)</summary>
    private static ControlTemplate TransparentToggleTemplate()
    {
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        return new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton)) { VisualTree = f };
    }

    private static Style ComboBoxItemStyle()
    {
        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, DR("T.Foreground")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.TemplateProperty, ComboBoxItemTemplate()));
        // 长项限宽:下拉面板不会因最长项撑得超宽(甚至顶出窗口),超出部分省略
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 420d));
        // 注意:Style.Triggers 的 Setter 禁用 TargetName(仅模板 Trigger 可用),
        // 改为直接设控件 Background,由模板内 Border 的 TemplateBinding 承接
        style.Triggers.Add(new Trigger
        {
            Property = ComboBoxItem.IsHighlightedProperty,
            Value = true,
            Setters = { new Setter(Control.BackgroundProperty, DR("T.HoverFill")) }
        });
        style.Triggers.Add(new Trigger
        {
            Property = ComboBoxItem.IsSelectedProperty,
            Value = true,
            Setters = { new Setter(Control.BackgroundProperty, DR("T.PrimarySoft")) }
        });
        return style;
    }

    private static ControlTemplate ComboBoxItemTemplate()
    {
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.Name = "itemBd";
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(UIKit.R.Chip));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        return new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = bd };
    }

    private static ControlTemplate CheckBoxTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        // 左勾选框右文字
        var dock = new FrameworkElementFactory(typeof(StackPanel));
        dock.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        root.AppendChild(dock);

        var bd = new FrameworkElementFactory(typeof(Border));
        bd.Name = "boxBd";
        bd.SetValue(Border.WidthProperty, 22.0);
        bd.SetValue(Border.HeightProperty, 22.0);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(UIKit.R.Pill));
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1.8));
        bd.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("T.SurfaceAlt"));
        bd.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("T.Border"));
        dock.AppendChild(bd);

        var glyph = new FrameworkElementFactory(typeof(Path));
        glyph.Name = "glyph";
        glyph.SetValue(Path.DataProperty, Geometry.Parse("M 4.5 11 L 8.5 15 L 17 5.5"));
        glyph.SetValue(Path.StrokeProperty, Brushes.White);
        glyph.SetValue(Path.StrokeThicknessProperty, 2.2);
        glyph.SetValue(Path.StretchProperty, Stretch.None);
        glyph.SetValue(Path.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        glyph.SetValue(Path.VerticalAlignmentProperty, VerticalAlignment.Center);
        glyph.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        bd.AppendChild(glyph);

        var label = new FrameworkElementFactory(typeof(ContentPresenter));
        label.SetValue(ContentPresenter.MarginProperty, new Thickness(9, 0, 0, 0));
        label.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        dock.AppendChild(label);

        var tpl = new ControlTemplate(typeof(CheckBox)) { VisualTree = root };
        tpl.Triggers.Add(new Trigger
        {
            Property = System.Windows.Controls.CheckBox.IsCheckedProperty,
            Value = true,
            Setters =
            {
                new Setter(Border.BackgroundProperty, DR("T.Primary"), "boxBd"),
                new Setter(Border.BorderBrushProperty, DR("T.Primary"), "boxBd"),
                new Setter(UIElement.VisibilityProperty, Visibility.Visible, "glyph")
            }
        });
        tpl.Triggers.Add(new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true,
            Setters = { new Setter(Border.BorderBrushProperty, DR("T.Primary"), "boxBd") }
        });
        // 键盘焦点态:描边主色 + 加粗,键盘用户 Tab 导航可见(不改变控件尺寸)
        tpl.Triggers.Add(new Trigger
        {
            Property = UIElement.IsKeyboardFocusedProperty,
            Value = true,
            Setters =
            {
                new Setter(Border.BorderBrushProperty, DR("T.Primary"), "boxBd"),
                new Setter(Border.BorderThicknessProperty, new Thickness(2.2), "boxBd")
            }
        });
        return tpl;
    }

    private static ControlTemplate ProgressBarTemplate()
    {
        var track = new FrameworkElementFactory(typeof(Border));
        track.Name = "PART_Track";
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(UIKit.R.Pill));
        track.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.ProgressBar.BackgroundProperty));

        var indicator = new FrameworkElementFactory(typeof(Border));
        indicator.Name = "PART_Indicator";
        indicator.SetValue(Border.CornerRadiusProperty, new CornerRadius(UIKit.R.Pill));
        indicator.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        indicator.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        track.AppendChild(indicator);

        return new ControlTemplate(typeof(System.Windows.Controls.ProgressBar)) { VisualTree = track };
    }

    /// <summary>滑块模板:Track 非 IAddChild,必须走 XamlReader.Parse</summary>
    private static ControlTemplate SliderTemplate()
    {
        const string xaml =
            "<ControlTemplate TargetType='Slider' " +
            "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
            "<Grid Height='22'>" +
            "<Track x:Name='PART_Track' VerticalAlignment='Center'>" +
            "<Track.DecreaseRepeatButton>" +
            "<RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>" +
            "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>" +
            "<Border Height='6' CornerRadius='3' VerticalAlignment='Center' Background='{DynamicResource T.Primary}'/>" +
            "</ControlTemplate></RepeatButton.Template></RepeatButton>" +
            "</Track.DecreaseRepeatButton>" +
            "<Track.IncreaseRepeatButton>" +
            "<RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>" +
            "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>" +
            "<Border Height='6' CornerRadius='3' VerticalAlignment='Center' Background='{DynamicResource T.SurfaceAlt}'/>" +
            "</ControlTemplate></RepeatButton.Template></RepeatButton>" +
            "</Track.IncreaseRepeatButton>" +
            "<Track.Thumb>" +
            "<Thumb Focusable='False' Width='22' Height='22'>" +
            "<Thumb.Template><ControlTemplate TargetType='Thumb'>" +
            "<Grid Background='Transparent'>" +
            "<Border Width='18' Height='18' CornerRadius='9' BorderThickness='3' " +
            "HorizontalAlignment='Center' VerticalAlignment='Center' " +
            "Background='{DynamicResource T.Primary}' BorderBrush='{DynamicResource T.Surface}'/>" +
            "</Grid></ControlTemplate></Thumb.Template></Thumb>" +
            "</Track.Thumb></Track></Grid></ControlTemplate>";
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    /// <summary>色相滑块模板:轨道全透明(光谱渐变由宿主铺在滑块底下),滑块为白环取色钮</summary>
    private static ControlTemplate HueSliderTemplate()
    {
        const string xaml =
            "<ControlTemplate TargetType='Slider' " +
            "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
            "<Grid Height='22'>" +
            "<Track x:Name='PART_Track' VerticalAlignment='Center'>" +
            "<Track.DecreaseRepeatButton>" +
            "<RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>" +
            "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>" +
            "<Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton>" +
            "</Track.DecreaseRepeatButton>" +
            "<Track.IncreaseRepeatButton>" +
            "<RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>" +
            "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>" +
            "<Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton>" +
            "</Track.IncreaseRepeatButton>" +
            "<Track.Thumb>" +
            "<Thumb Focusable='False' Width='24' Height='24'>" +
            "<Thumb.Template><ControlTemplate TargetType='Thumb'>" +
            "<Grid Background='Transparent'>" +
            "<Border Width='20' Height='20' CornerRadius='10' BorderThickness='3.5' " +
            "HorizontalAlignment='Center' VerticalAlignment='Center' " +
            "Background='Transparent' BorderBrush='White'>" +
            "<Border.Effect><DropShadowEffect BlurRadius='6' ShadowDepth='0' Opacity='0.55' RenderingBias='Performance'/></Border.Effect>" +
            "</Border>" +
            "</Grid></ControlTemplate></Thumb.Template></Thumb>" +
            "</Track.Thumb></Track></Grid></ControlTemplate>";
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    // ==================== 细滚动条(隐式样式,全局生效) ====================

    private static Style ScrollBarImplicitStyle()
    {
        var style = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 8.0));
        style.Setters.Add(new Setter(Control.TemplateProperty, ScrollBarTemplate(vertical: true)));
        style.Triggers.Add(new Trigger
        {
            Property = System.Windows.Controls.Primitives.ScrollBar.OrientationProperty,
            Value = Orientation.Horizontal,
            Setters =
            {
                new Setter(FrameworkElement.WidthProperty, double.NaN),
                new Setter(FrameworkElement.HeightProperty, 8.0),
                new Setter(Control.TemplateProperty, ScrollBarTemplate(vertical: false))
            }
        });
        return style;
    }

    /// <summary>滚动条模板:Track 结构同样走 XamlReader.Parse</summary>
    private static ControlTemplate ScrollBarTemplate(bool vertical)
    {
        string decCmd = vertical ? "ScrollBar.PageUpCommand" : "ScrollBar.PageLeftCommand";
        string incCmd = vertical ? "ScrollBar.PageDownCommand" : "ScrollBar.PageRightCommand";
        string sizeAttr = vertical ? "Width='8'" : "Height='8'";
        string thumbMin = vertical ? "MinHeight='28'" : "MinWidth='28'";
        string xaml =
            "<ControlTemplate TargetType='ScrollBar' " +
            "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
            $"<Grid {sizeAttr} Background='Transparent'>" +
            "<Track x:Name='PART_Track' " + (vertical ? "IsDirectionReversed='True'" : "") + ">" +
            "<Track.DecreaseRepeatButton>" +
            $"<RepeatButton Command='{decCmd}' Focusable='False' Opacity='0'>" +
            "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>" +
            "<Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton>" +
            "</Track.DecreaseRepeatButton>" +
            "<Track.IncreaseRepeatButton>" +
            $"<RepeatButton Command='{incCmd}' Focusable='False' Opacity='0'>" +
            "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>" +
            "<Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton>" +
            "</Track.IncreaseRepeatButton>" +
            "<Track.Thumb>" +
            "<Thumb Focusable='False'>" +
            "<Thumb.Template><ControlTemplate TargetType='Thumb'>" +
            $"<Border {thumbMin} CornerRadius='4'>" +
            "<Border.Style><Style TargetType='Border'>" +
            // Setter.Value 用元素语法承载 DynamicResource:属性语法在 XamlReader.Parse 下
            // 抛 XamlParseException『Unexpected token Open』(2026-08-26 两次崩溃的根因)
            "<Setter Property='Background'>" +
            "<Setter.Value><DynamicResource ResourceKey='T.ScrollThumb'/></Setter.Value>" +
            "</Setter>" +
            "<Style.Triggers><Trigger Property='IsMouseOver' Value='True'>" +
            "<Setter Property='Background'>" +
            "<Setter.Value><DynamicResource ResourceKey='T.ScrollThumbHover'/></Setter.Value>" +
            "</Setter>" +
            "</Trigger></Style.Triggers></Style></Border.Style></Border>" +
            "</ControlTemplate></Thumb.Template></Thumb>" +
            "</Track.Thumb></Track></Grid></ControlTemplate>";
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }
}

/// <summary>布局定位扩展(减少样板)</summary>
internal static class LayoutExt
{
    public static T Row<T>(this T el, int row) where T : UIElement { Grid.SetRow(el, row); return el; }
    public static T Col<T>(this T el, int col) where T : UIElement { Grid.SetColumn(el, col); return el; }
    public static T MarginAll<T>(this T el, double l, double t, double r, double b) where T : FrameworkElement
    { el.Margin = new Thickness(l, t, r, b); return el; }
    public static T MarginTop<T>(this T el, double t) where T : FrameworkElement
    { el.Margin = new Thickness(el.Margin.Left, t, el.Margin.Right, el.Margin.Bottom); return el; }
}
