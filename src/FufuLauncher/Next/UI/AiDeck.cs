// AiDeck.cs — 泡芙助理(本地自包含 AI 助理)· 纯任务形态
//
// 设计要点:
//   - 页面只处理我的世界任务:页头(标题 + 引擎状态徽章)→ MC 任务开关行 → 消息流 → 底部输入框;
//   - 日常闲聊已移除:非 MC 指令统一拒答;本地模型只负责把 MC 指令解析成结构化意图;
//   - 输入框为原生 TextBox(不走 UIKit XAML 字符串模板),保证焦点/中文输入法/多行输入;
//   - Enter 发送,Shift+Enter 换行;任务执行期间发送键变「停止」,进度条整合进输入坞。

using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class AiDeck : UserControl
{
    private readonly AiAssistantService _assistant;
    private readonly AiEngineService _engine;
    private readonly ConfigService _config;

    // MC 任务开关(关闭后不执行任何 MC 自动化;日常闲聊已移除,无需第二个开关)
    private readonly UIKit.ToggleSwitch _mcTaskSw = new("处理 MC 任务");

    // 消息流
    private readonly StackPanel _chatPanel = new() { Margin = new Thickness(2, 0, 2, 0) };
    private readonly ScrollViewer _chatScroll;

    // 输入坞
    private readonly TextBox _inputBox;
    private readonly Border _inputBorder;
    private readonly TextBlock _inputWatermark;
    private readonly Button _sendBtn;
    private readonly TextBlock _sendBtnLabel;
    private readonly ProgressBar _progressBar = UIKit.ProgressBar(0);
    private readonly TextBlock _progressText = UIKit.Sub("", 12);
    private readonly Grid _busyRow;
    private readonly TextBlock _idleHint;
    private readonly TextBlock _engineChip;
    private readonly TextBlock _modelNameChip;
    private readonly Grid _mainView = new();
    private readonly Grid _settingsView = new();

    // 模型设置区(用户自维护模型:下载链接 + 拖入投放 + 列表切换/删除)
    private AiModelLibraryService _library = null!;
    private Border _dropZone = null!;
    private TextBlock _dropHint = null!;
    private Border _modelsWrap = null!;
    private TextBlock _modelCountChip = null!;   // BuildModelCard() 中初始化
    private readonly StackPanel _modelsPanel = new();
    private bool _importing;

    private CancellationTokenSource? _cts;
    private bool _busy;
    private int _preloadGen;

    public AiDeck()
    {
        _assistant = App.Services.GetRequiredService<AiAssistantService>();
        _engine = App.Services.GetRequiredService<AiEngineService>();
        _config = App.Services.GetRequiredService<ConfigService>();

        // ==================== 页头(标题 + 引擎状态徽章) ====================
        _engineChip = UIKit.SubOneLine("模型加载中…", 12, 280);
        // 2026-09-25:模型名 chip 改用 SubOneLine(省略号+限宽)——长模型名不再把页头右侧撑宽挤压标题列
        _modelNameChip = UIKit.SubOneLine("未加载模型", 11, 220);
        _modelNameChip.Margin = new Thickness(0, 2, 0, 0);
        _modelNameChip.ToolTip = "当前加载的模型文件(悬停看完整名称)";
        var chipBox = new Border
        {
            Padding = new Thickness(12, 7, 12, 7),
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            VerticalAlignment = VerticalAlignment.Center,
            Child = UIKit.V(_engineChip, _modelNameChip)
        };
        chipBox.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        var title = UIKit.PageHeader("泡芙助理", "只处理我的世界任务 · 本地模型离线运行");
        Grid.SetColumn(title, 0);
        Grid.SetColumn(chipBox, 1);
        header.Children.Add(title);
        header.Children.Add(chipBox);

        // ==================== MC 任务开关行(即时生效并持久化) ====================
        _mcTaskSw.IsChecked = _config.Config.AiMcTasksEnabled;
        _mcTaskSw.ToolTip = "开启后识别我的世界指令:安装版本、做整合包、装模组等自动化;关闭后不执行任何 MC 操作";
        _mcTaskSw.Checked += (_, _) => { _config.Config.AiMcTasksEnabled = true; _config.Save(); };
        _mcTaskSw.Unchecked += (_, _) => { _config.Config.AiMcTasksEnabled = false; _config.Save(); };

        var switchHint = UIKit.Sub("关闭后不执行任何我的世界操作", 12);
        switchHint.Margin = new Thickness(12, 3, 0, 0);
        switchHint.VerticalAlignment = VerticalAlignment.Center;
        var switchRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 12, 2, 0),
            Children = { _mcTaskSw, switchHint }
        };

        // ==================== 模型设置区(放到子视图) ====================
        _library = App.Services.GetRequiredService<AiModelLibraryService>();
        var modelCard = BuildModelCard();

        // 「模型设置」小胶囊入口(轻量,不要大块灰按钮)
        var openSettingsBtn = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(2, 12, 0, 0),
            Cursor = Cursors.Hand,
            Child = UIKit.V(
                UIKit.Text("模型设置", 13, FontWeights.Medium),
                UIKit.Text("选择 / 下载 / 管理本地 AI 模型", 10.5, null, "T.ForegroundDim"))
        };
        openSettingsBtn.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");
        openSettingsBtn.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        openSettingsBtn.BorderThickness = new Thickness(1);
        openSettingsBtn.MouseLeftButtonUp += (_, _) =>
        {
            _mainView.Visibility = Visibility.Collapsed;
            _settingsView.Visibility = Visibility.Visible;
        };
        var settingsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { openSettingsBtn }
        };

        var headerBlock = UIKit.V(header, switchRow, settingsRow);

        // ==================== 消息流 ====================
        _chatScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 14, 0, 14),
            Content = _chatPanel
        };

        // ==================== 输入框(原生 TextBox,无模板,保证可输入) ====================
        _inputBox = new TextBox
        {
            FontSize = 14,
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 72,
            MaxHeight = 150,
            Padding = new Thickness(14, 12, 14, 12),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent
        };
        _inputBox.SetResourceReference(Control.ForegroundProperty, "T.Foreground");
        _inputBox.SetResourceReference(TextBox.CaretBrushProperty, "T.Primary");
        _inputBox.SetResourceReference(TextBox.SelectionBrushProperty, "T.PrimarySoft");

        _inputWatermark = UIKit.Sub("只处理我的世界任务,例如:帮我安装 1.12.2、换个深色主题、做个离线账号、帮我打开游戏", 13);
        _inputWatermark.Margin = new Thickness(16, 14, 0, 0);
        _inputWatermark.IsHitTestVisible = false;

        _inputBorder = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Panel),
            BorderThickness = new Thickness(1.5),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new Grid
            {
                Children = { _inputBox, _inputWatermark }
            }
        };
        _inputBorder.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");
        _inputBorder.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        _inputBox.GotFocus += (_, _) => _inputBorder.SetResourceReference(Border.BorderBrushProperty, "T.Primary");
        _inputBox.LostFocus += (_, _) => _inputBorder.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        _inputBox.TextChanged += (_, _) => SyncWatermark();
        _inputBox.PreviewKeyDown += OnInputKey;

        // ==================== 发送/停止(关键 CTA 46px) ====================
        _sendBtnLabel = UIKit.Text("发送", 14, FontWeights.SemiBold, "T.Surface");
        _sendBtn = UIKit.Button("", primary: true, height: 46);
        _sendBtn.Content = _sendBtnLabel;
        _sendBtn.MinWidth = 108;
        _sendBtn.Focusable = false;   // 点击按钮不抢输入框焦点
        _sendBtn.Click += (_, _) => { if (_busy) OnStop(); else OnSend(); };

        // ==================== 底部状态行:左(空闲提示 / 构建进度) + 右(发送按钮) ====================
        _idleHint = UIKit.Sub("Enter 发送 · Shift+Enter 换行", 12);
        _idleHint.VerticalAlignment = VerticalAlignment.Center;

        _progressBar.Height = 6;
        _progressBar.VerticalAlignment = VerticalAlignment.Center;
        _progressText.VerticalAlignment = VerticalAlignment.Center;
        _progressText.Margin = new Thickness(12, 0, 0, 0);
        _busyRow = new Grid
        {
            Visibility = Visibility.Collapsed,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        Grid.SetColumn(_progressBar, 0);
        Grid.SetColumn(_progressText, 1);
        _busyRow.Children.Add(_progressBar);
        _busyRow.Children.Add(_progressText);

        var statusRow = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        var statusLeft = new Grid { Children = { _idleHint, _busyRow } };
        Grid.SetColumn(statusLeft, 0);
        Grid.SetColumn(_sendBtn, 1);
        _sendBtn.VerticalAlignment = VerticalAlignment.Center;
        statusRow.Children.Add(statusLeft);
        statusRow.Children.Add(_sendBtn);

        // ==================== 输入坞卡片(整体容器,与消息流明确分区) ====================
        var dock = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Panel),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16, 18, 16),
            Child = UIKit.V(_inputBorder, statusRow)
        };
        dock.SetResourceReference(Border.BackgroundProperty, "T.Surface");
        dock.SetResourceReference(Border.BorderBrushProperty, "T.Border");

        // ==================== 主视图:页头 / 开关行 / 设置按钮 / 消息流 / 输入坞 ====================
        _mainView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _mainView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _mainView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(headerBlock, 0);
        Grid.SetRow(_chatScroll, 1);
        Grid.SetRow(dock, 2);
        _mainView.Children.Add(headerBlock);
        _mainView.Children.Add(_chatScroll);
        _mainView.Children.Add(dock);
        _mainView.Margin = new Thickness(26, 20, 26, 16);

        // ==================== 模型设置子视图:返回按钮 + 模型卡片 ====================
        // 返回:轻量文字链接,不要大块灰按钮
        var backLink = UIKit.Text("← 返回泡芙助理", 13, FontWeights.Medium, "T.Primary");
        backLink.Cursor = Cursors.Hand;
        backLink.Margin = new Thickness(0, 4, 0, 0);
        backLink.MouseLeftButtonUp += (_, _) =>
        {
            _settingsView.Visibility = Visibility.Collapsed;
            _mainView.Visibility = Visibility.Visible;
        };
        var subHeader = UIKit.V(
            UIKit.PageHeader("模型设置", "选择、下载、管理本地 AI 模型"),
            backLink);
        var settingsRoot = UIKit.V(subHeader, modelCard);
        settingsRoot.Margin = new Thickness(26, 20, 26, 16);
        settingsRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
        settingsRoot.VerticalAlignment = VerticalAlignment.Top;
        _settingsView.Children.Add(settingsRoot);
        _settingsView.Visibility = Visibility.Collapsed;

        // ==================== 根:两视图叠放 ====================
        var root = new Grid();
        root.Children.Add(_mainView);
        root.Children.Add(_settingsView);
        Content = root;

        // 欢迎语:一句话说明能力范围(只处理 MC 任务:内容任务 + 启动器本地功能)
        AddBubble("你好呀,我是泡芙助理。我只处理我的世界相关任务:可以帮你安装版本、做整合包、查模组,也能直接操作启动器——比如:换个深色主题、做一个离线账号名字叫芙芙、帮我打开游戏。", user: false);

        Loaded += (_, _) =>
        {
            PreloadEngine();
            _inputBox.Focus();
        };
        // 模型库变化(导入/删除/切换可能在其它线程触发)→ 刷新列表;页面每次可见时刷新状态并按需重载
        _library.Changed += () => Dispatcher.BeginInvoke(RenderModelList);
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) PreloadEngine(); };
        RenderModelList();
    }

    private void SyncWatermark() =>
        _inputWatermark.Visibility = string.IsNullOrEmpty(_inputBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        // Enter 发送;Shift+Enter 换行(AcceptsReturn 生效)
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            if (!_busy) OnSend();
        }
    }

    // ==================== 模型预加载 ====================

    private async void PreloadEngine()
    {
        int gen = ++_preloadGen;
        RenderModelList();
        if (!_library.HasAnyModel)
        {
            _engineChip.Text = "未导入模型";
            _modelNameChip.Text = "未加载模型";
            return;   // 无模型:不尝试加载,拖入区引导用户导入
        }
        _engineChip.Text = _engine.State == AiEngineService.EngineState.Ready
            ? _engine.StatusDetail
            : "模型加载中…";
        _modelNameChip.Text = string.IsNullOrEmpty(_engine.LoadedModelName) ? "未加载模型" : "当前模型: " + _engine.LoadedModelName;
        try
        {
            await _engine.EnsureLoadedAsync();
            if (gen != _preloadGen) return;
            _engineChip.Text = _engine.StatusDetail;
            _modelNameChip.Text = string.IsNullOrEmpty(_engine.LoadedModelName) ? "未加载模型" : "当前模型: " + _engine.LoadedModelName;
            _modelNameChip.ToolTip = _engine.LoadedModelName;
            RenderModelList();
        }
        catch (Exception ex)
        {
            if (gen != _preloadGen) return;
            _engineChip.Text = "模型加载异常:" + ex.Message;
            RenderModelList();
        }
    }

    // ==================== 模型设置区 ====================

    /// <summary>构建「模型设置」卡片:下载来源链接 + 拖入投放区 + 已导入模型列表</summary>
    private FrameworkElement BuildModelCard()
    {
        // 下载来源:程序不内置、不联网下载模型,给出官方来源让用户自行获取后拖入
        var dlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        dlRow.Children.Add(UIKit.Sub("模型下载:", 12));
        for (int i = 0; i < AiModelLibraryService.DownloadSources.Length; i++)
        {
            if (i > 0) dlRow.Children.Add(UIKit.Sub("   ·   ", 12));
            var (label, url) = AiModelLibraryService.DownloadSources[i];
            dlRow.Children.Add(MakeLink(label, url));
        }
        var dlHint = UIKit.Sub($"  推荐 {AiModelLibraryService.RecommendedModelName},下载后拖入下方区域即可", 11.5);
        dlHint.Margin = new Thickness(0, 0, 0, 12);

        // 拖入投放区:接受 .gguf 文件或整个模型文件夹;亦可点击选择文件
        _dropHint = UIKit.Text("请将模型拖入此处", 15, FontWeights.SemiBold, "T.ForegroundDim");
        _dropHint.HorizontalAlignment = HorizontalAlignment.Center;
        var dropSub = UIKit.Sub("支持 .gguf 模型文件,或整个模型文件夹 · 也可点击此处选择文件", 11.5);
        dropSub.HorizontalAlignment = HorizontalAlignment.Center;
        dropSub.Margin = new Thickness(0, 4, 0, 0);
        var dropStack = UIKit.V(_dropHint, dropSub);
        dropStack.HorizontalAlignment = HorizontalAlignment.Center;
        _dropZone = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Panel),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(16, 20, 16, 20),
            Margin = new Thickness(0, 0, 0, 12),
            Cursor = Cursors.Hand,
            AllowDrop = true,
            Child = dropStack
        };
        _dropZone.SetResourceReference(Border.BackgroundProperty, "T.SurfaceAlt");
        _dropZone.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        _dropZone.DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) HighlightDrop(true); };
        _dropZone.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; HighlightDrop(true); }
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        };
        _dropZone.DragLeave += (_, _) => HighlightDrop(false);
        _dropZone.Drop += OnModelDrop;
        _dropZone.MouseLeftButtonUp += (_, _) => BrowseModels();

        // 已导入模型列表(限高可滚动;无模型时整体隐藏)
        var modelsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 176, Content = _modelsPanel };
        _modelsWrap = new Border { Visibility = Visibility.Collapsed, Child = modelsScroll };

        // 已导入模型计数(原为 null! 从未赋值:计数提示永不显示,旧版还因此空引用崩溃)
        _modelCountChip = UIKit.Sub("尚未导入模型", 11.5);
        _modelCountChip.Margin = new Thickness(0, 0, 0, 6);
        _modelCountChip.SetResourceReference(TextBlock.ForegroundProperty, "T.ForegroundDim");

        var modelStack = UIKit.V(_modelCountChip, dlRow, dlHint, _dropZone, _modelsWrap);
        var card = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Panel),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16, 18, 16),
            Margin = new Thickness(0, 12, 0, 0),
            Child = modelStack
        };
        card.SetResourceReference(Border.BackgroundProperty, "T.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        return card;
    }

    private static TextBlock MakeLink(string label, string url)
    {
        var tb = UIKit.Text(label, 12, FontWeights.Medium, "T.Primary");
        tb.Cursor = Cursors.Hand;
        tb.TextDecorations = TextDecorations.Underline;
        tb.VerticalAlignment = VerticalAlignment.Center;
        tb.MouseLeftButtonUp += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { DialogKit.Error("打开链接失败:" + ex.Message + "\n可手动访问:" + url); }
        };
        return tb;
    }

    private void HighlightDrop(bool on)
    {
        _dropZone.SetResourceReference(Border.BorderBrushProperty, on ? "T.Primary" : "T.Border");
        _dropZone.SetResourceReference(Border.BackgroundProperty, on ? "T.PrimarySoft" : "T.SurfaceAlt");
        _dropHint.SetResourceReference(TextBlock.ForegroundProperty, on ? "T.Primary" : "T.ForegroundDim");
    }

    private void OnModelDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;   // 阻止冒泡到 ShellWindow.OnBackgroundDrop(避免被当成背景图/整合包处理)
        HighlightDrop(false);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        ImportModels(files);
    }

    private void BrowseModels()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 GGUF 模型文件",
            Filter = "GGUF 模型 (*.gguf)|*.gguf|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            ImportModels(dlg.FileNames);
    }

    /// <summary>导入一批路径(文件/文件夹混合):后台复制并登记,完成后按需自动加载</summary>
    private void ImportModels(IReadOnlyList<string> paths)
    {
        if (_importing) return;
        if (paths.Count == 0) return;
        _importing = true;
        AiModelImportOutcome outcome;
        try
        {
            outcome = DialogKit.RunWithProgress("导入模型",
                report => _library.ImportPathsAsync(paths, new Progress<string>(report)),
                Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            _importing = false;
            DialogKit.Error("导入模型失败:" + ex.Message);
            return;
        }
        _importing = false;
        RenderModelList();

        if (outcome.Imported == 0)
        {
            DialogKit.Warn(outcome.Notes.Count > 0
                ? "没有导入任何模型:\n" + string.Join("\n", outcome.Notes)
                : "没有可导入的 .gguf 模型文件。请确认拖入的是 GGUF 格式模型。");
            return;
        }
        DialogKit.Success(outcome.Summary);
        _ = AutoLoadAfterImportAsync();
    }

    /// <summary>导入后:若当前没有已加载的模型,自动加载活跃模型(通常即刚导入的那个)</summary>
    private async Task AutoLoadAfterImportAsync()
    {
        if (_engine.State == AiEngineService.EngineState.Ready) { RenderModelList(); return; }
        int gen = ++_preloadGen;
        _engineChip.Text = "模型加载中…";
        RenderModelList();
        try { await _engine.EnsureLoadedAsync(); }
        catch (Exception ex) { App.WriteAppLog($"[泡芙助理] 导入后加载异常:{ex.Message}"); }
        if (gen != _preloadGen) return;
        _engineChip.Text = _engine.StatusDetail;
        RenderModelList();
    }

    /// <summary>按模型库当前状态重建列表(跨线程安全)</summary>
    private void RenderModelList()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RenderModelList); return; }
        var models = _library.Models;
        string? activeId = _library.Active?.Id;
        _modelsPanel.Children.Clear();
        if (_modelCountChip != null)
            _modelCountChip.Text = models.Count == 0 ? "尚未导入模型" : $"已导入 {models.Count} 个模型";
        _modelsWrap.Visibility = models.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var m in models)
            _modelsPanel.Children.Add(MakeModelRow(m, activeId == m.Id));
    }

    private FrameworkElement MakeModelRow(AiModelInfo m, bool isActive)
    {
        var name = UIKit.Text(m.DisplayName, 13, FontWeights.Medium);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var meta = UIKit.Sub($"{UIKit.FmtBytes(m.SizeBytes)} · 导入于 {m.ImportedAt:yyyy-MM-dd HH:mm}", 11.5);
        var left = UIKit.V(name, meta);
        left.VerticalAlignment = VerticalAlignment.Center;

        string statusText, statusKey;
        if (isActive)
        {
            if (_engine.State == AiEngineService.EngineState.Ready) { statusText = "已加载"; statusKey = "T.Success"; }
            else if (_engine.State == AiEngineService.EngineState.Loading) { statusText = "加载中"; statusKey = "T.Warning"; }
            else if (_engine.State == AiEngineService.EngineState.Error) { statusText = "加载失败"; statusKey = "T.Danger"; }
            else { statusText = "当前 · 未加载"; statusKey = "T.Primary"; }
        }
        else { statusText = "未使用"; statusKey = "T.Border"; }
        var badge = UIKit.Badge(statusText, statusKey);
        badge.VerticalAlignment = VerticalAlignment.Center;
        badge.Margin = new Thickness(0, 0, 10, 0);

        var switchBtn = UIKit.Button(isActive ? "使用中" : "切换", primary: false, onClick: () => _ = SwitchToModelAsync(m.Id), height: 28);
        switchBtn.IsEnabled = !isActive && !_busy;
        switchBtn.MinWidth = 64;
        var delBtn = UIKit.Button("删除", primary: false, onClick: () => DeleteModel(m), height: 28);
        delBtn.MinWidth = 56;

        var right = UIKit.H(badge, switchBtn, delBtn);
        right.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { left, right.Col(1) }
        };

        var row = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 4, 0, 4),
            Child = grid
        };
        row.SetResourceReference(Border.BackgroundProperty, isActive ? "T.PrimarySoft" : "T.SurfaceAlt");
        return row;
    }

    /// <summary>切换到指定模型:卸载当前 → 加载目标(处理任务时禁止切换,避免推理中释放模型)</summary>
    private async Task SwitchToModelAsync(string id)
    {
        if (_busy) { DialogKit.Warn("泡芙助理正在处理任务,请稍候再切换模型。"); return; }
        int gen = ++_preloadGen;
        _engineChip.Text = "正在切换模型…";
        RenderModelList();
        try
        {
            bool ok = await _engine.SwitchModelAsync(id);
            if (gen != _preloadGen) return;
            _engineChip.Text = _engine.StatusDetail;
            if (!ok) AddBubble("模型切换后加载失败:" + _engine.StatusDetail, user: false);
        }
        catch (Exception ex)
        {
            if (gen != _preloadGen) return;
            _engineChip.Text = "切换异常:" + ex.Message;
        }
        RenderModelList();
    }

    /// <summary>删除模型:确认后移除文件与登记;若为已加载的活跃模型,先卸载引擎释放文件句柄</summary>
    private async void DeleteModel(AiModelInfo m)
    {
        if (_busy) { DialogKit.Warn("泡芙助理正在处理任务,请稍候再删除模型。"); return; }
        bool isActive = _library.Active?.Id == m.Id;
        if (!DialogKit.Confirm($"确定删除模型「{m.DisplayName}」?\n文件将从 models 目录移除,此操作不可撤销。",
                "删除模型", "删除", true, Window.GetWindow(this)))
            return;
        if (isActive && (_engine.State == AiEngineService.EngineState.Ready || _engine.State == AiEngineService.EngineState.Loading))
        {
            try { await _engine.UnloadAsync("删除当前模型前卸载"); }
            catch (Exception ex) { App.WriteAppLog($"[泡芙助理] 删除前卸载异常:{ex.Message}"); }
        }
        bool fileGone = _library.Delete(m.Id);
        _engineChip.Text = _library.HasAnyModel
            ? (_engine.State == AiEngineService.EngineState.Ready ? _engine.StatusDetail : "未加载")
            : "未导入模型";
        RenderModelList();
        if (!fileGone)
            DialogKit.Warn("模型已从列表移除,但文件正被占用未能删除,可稍后手动删除 models 目录下的该文件。");
    }

    // ==================== 对话流 ====================

    private async void OnSend()
    {
        if (_busy) return;
        string text = (_inputBox.Text ?? "").Trim();
        if (text.Length == 0) return;
        _inputBox.Text = "";
        SyncWatermark();

        AddBubble(text, user: true);
        var replyText = AddBubble("…", user: false);

        _busy = true;
        _sendBtnLabel.Text = "停止";
        _idleHint.Visibility = Visibility.Collapsed;
        _busyRow.Visibility = Visibility.Visible;
        _progressBar.ResetProgress();
        _progressText.Text = "泡芙助理正在理解你的需求…";

        bool streamed = false;
        _cts = new CancellationTokenSource();
        // 流式输出节流:模型逐 token 回调后台线程,原实现每 token 塞一个 UI 任务,
        // 输出快时 Dispatcher 队列积压 → 界面卡顿/内存堆积。改为无锁入队 +
        // 50ms DispatcherTimer 批量上屏,进度回调低频保持直接调度。
        var tokenQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var pendingSb = new System.Text.StringBuilder();
        var flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        flushTimer.Tick += (_, _) =>
        {
            while (tokenQueue.TryDequeue(out var tk)) pendingSb.Append(tk);
            if (pendingSb.Length == 0) return;
            if (replyText.Text == "…") replyText.Text = "";
            replyText.Text += pendingSb;
            pendingSb.Clear();
            ScrollBottom();
        };
        flushTimer.Start();
        try
        {
            string final = await _assistant.HandleMessageAsync(
                text,
                token => { streamed = true; tokenQueue.Enqueue(token); },
                (p, msg) => Dispatcher.BeginInvoke(() =>
                {
                    _progressBar.SmoothSet(Math.Clamp(p, 0, 1) * 100);
                    _progressText.Text = msg;
                }),
                _cts.Token);

            flushTimer.Stop();
            // 停表后把残余 token 一次上屏,避免最后一段被吞
            while (tokenQueue.TryDequeue(out var tk)) pendingSb.Append(tk);
            if (pendingSb.Length > 0)
            {
                if (replyText.Text == "…") replyText.Text = "";
                replyText.Text += pendingSb;
                pendingSb.Clear();
                ScrollBottom();
            }
            if (!streamed && !string.IsNullOrWhiteSpace(final))
                replyText.Text = final;
        }
        catch (OperationCanceledException)
        {
            replyText.Text = streamed ? replyText.Text + "\n(已停止)" : "好的,已停止。";
            _progressText.Text = "已停止";
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[泡芙助理] 处理异常:{ex}");
            replyText.Text = "处理时出错了:" + ex.Message;
        }
        finally
        {
            flushTimer.Stop();
            _busy = false;
            _sendBtnLabel.Text = "发送";
            _idleHint.Visibility = Visibility.Visible;
            _busyRow.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
            _inputBox.Focus();
            ScrollBottom();
        }
    }

    private void OnStop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    // ==================== 气泡 ====================

    private TextBlock AddBubble(string text, bool user)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 21
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "T.Foreground");

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(UIKit.R.Chip),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 4, 0, 4),
            MaxWidth = 680,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = tb
        };
        bubble.SetResourceReference(Border.BackgroundProperty, user ? "T.PrimarySoft" : "T.SurfaceAlt");

        _chatPanel.Children.Add(bubble);
        ScrollBottom();
        return tb;
    }

    private void ScrollBottom() => Dispatcher.BeginInvoke(() => _chatScroll.ScrollToEnd());
}
