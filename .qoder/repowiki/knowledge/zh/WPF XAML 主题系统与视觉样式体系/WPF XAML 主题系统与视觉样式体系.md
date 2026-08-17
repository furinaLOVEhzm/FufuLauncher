---
kind: frontend_style
name: WPF XAML 主题系统与视觉样式体系
category: frontend_style
scope:
    - '**'
source_files:
    - src/FufuLauncher/Themes/Theme.xaml
    - src/FufuLauncher/App.xaml
    - src/FufuLauncher/Services/ThemeService.cs
    - src/FufuLauncher/MainWindow.xaml
---

本项目采用 WPF + XAML 作为前端 UI 框架，通过集中式资源字典与运行时主题服务实现深浅双主题的完整视觉系统。

**1. 使用的系统与工具**
- WPF（Windows Presentation Foundation）XAML 作为 UI 声明语言
- XAML ResourceDictionary 作为样式与资源的组织方式
- C# 代码-behind 配合 MVVM 模式（ViewModels/Views/Services 分层）
- 无外部 CSS/SCSS/Tailwind 等 Web 技术栈，纯原生 Windows 桌面样式方案

**2. 核心文件与包**
- `src/FufuLauncher/Themes/Theme.xaml`：全局主题资源字典，定义所有控件样式、颜色变量、文本样式、卡片样式、按钮系列、导航栏、输入控件、进度条、列表项、滚动条、标签页、弹窗容器等
- `src/FufuLauncher/App.xaml`：应用入口，合并 Theme.xaml 到 Application.Resources
- `src/FufuLauncher/Services/ThemeService.cs`：主题切换服务，运行时覆盖 AppBackground/AppForeground/CardBackground/CardBorder/InputBorder/OverlayTint/StatusTextBrush 等 Key 的值以切换 Light/Dark 主题
- `src/FufuLauncher/MainWindow.xaml`：主窗口布局，使用 `{DynamicResource}` 引用主题资源，支持透明背景、圆角裁剪、自定义标题栏

**3. 架构与约定**
- **设计令牌（Design Tokens）**：在 Theme.xaml 中统一定义 PrimaryColor/AccentColor/DangerColor/SuccessColor/WarningColor 等基础色，再派生出对应 Brush；Light*/Dark* 色板分别定义浅色/深色两套背景、前景、卡片、边框、遮罩色
- **动态资源绑定**：UI 层统一使用 `{DynamicResource}` 引用主题 Key（如 AppBackground、AppForeground、CardBackground），而非硬编码颜色值，确保主题切换即时生效
- **控件样式命名规范**：所有自定义控件样式以 `Fufu` 前缀命名（FufuTextBox、FufuComboBox、FufuCheckBox、FufuRadioButton、FufuSlider、FufuProgressBar、FufuScrollBar、FufuScrollViewer、FufuTabItem），并通过隐式样式（TargetType 未指定 x:Key）自动应用到所有同类型控件
- **按钮体系**：PrimaryButton（主按钮，蓝色填充+hover缩放动画）、SecondaryButton（次按钮，透明描边）、DangerButton（危险按钮，红色）均基于同一模板，共享 hover/禁用状态逻辑
- **主题切换机制**：ThemeService.ApplyTheme() 根据配置中的 Theme 字段（"Light"/"Dark"）动态替换 Application.Current.Resources 中的 Key 值，无需重建 UI
- **背景层架构**：MainWindow 采用三层结构——BackgroundLayer（图片/视频背景，由 ThemeService 注入）、OverlayMaskBorder（半透明蒙版，透明度随背景透明度联动）、主内容层（Frame 承载各页面），整体窗口透明化并带圆角阴影
- **MVVM 集成**：App.xaml.cs 通过 DI 容器注册所有 ViewModel、View、Service，MainWindow 由 DI 创建，视图与数据通过 Binding 解耦

**4. 约定与约束**
- 所有颜色必须通过 `{DynamicResource}` 引用主题 Key，禁止在 XAML 中直接写死颜色值（Theme.xaml 注释明确要求“所有引用统一 DynamicResource”）
- 新增控件样式需遵循 `Fufu` 前缀命名约定，并提供对应的隐式样式（TargetType 自动应用）
- 主题仅支持 "Light" 和 "Dark" 两种模式，ThemeService.SetTheme() 会校验并持久化到 ConfigService
- 背景仅支持单层（图片或视频二选一），不支持多层叠加
- 窗口样式为无边框自定义标题栏（WindowStyle="None"），最小化/最大化/关闭按钮通过 Style="TitleBarButton"/"TitleBarCloseButton" 统一外观
- 所有交互反馈（hover、选中、禁用）通过 ControlTemplate.Triggers 实现，保持样式一致性