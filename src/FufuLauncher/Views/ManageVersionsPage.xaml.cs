// ManageVersionsPage.xaml.cs — 管理游戏版本页
// 可爱的芙芙
//
// 功能:列出全部游戏实例,支持卸载指定游戏版本。
// 卸载硬约束(逐条落实):
// 1. 卸载只删除:版本本体(共享且无其它实例引用时) + 该版本的模组 + 版本配置文件
// 2. ⚠ 严禁删除 saves 存档文件夹(服务层先拆联接再删目录,物理存档目录全程不触碰)
// 3. 绝不触碰 runtimes 下的 Java 运行时(Java 卸载只允许在 Java 管理页执行)
// 4. 禁止卸载正在运行中的游戏版本
// 5. 删除后台异步执行(Task.Run),不阻塞 UI
// 6. 卸载前弹出二次确认弹窗(危险操作样式)

using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FufuLauncher.Interaction;
using FufuLauncher.Services;

namespace FufuLauncher.Views;

/// <summary>管理页实例展示项</summary>
public class ManageInstanceItem : INotifyPropertyChanged
{
    public GameInstance Instance { get; }
    public ManageInstanceItem(GameInstance inst) { Instance = inst; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName => string.IsNullOrEmpty(Instance.Name) ? Instance.Id : Instance.Name;

    public string VersionText
    {
        get
        {
            string text = $"MC {Instance.VersionId}";
            if (!string.IsNullOrEmpty(Instance.ModLoader))
                text += $" · {Instance.ModLoader}";
            return text;
        }
    }

    public string MetaText
    {
        get
        {
            string last = Instance.LastPlayedAt == default
                ? "从未游玩"
                : $"上次游玩 {Instance.LastPlayedAt:yyyy-MM-dd HH:mm}";
            var ts = TimeSpan.FromSeconds(Instance.TotalPlayTimeSeconds);
            string played = ts.TotalHours >= 1
                ? $"累计游玩 {ts.TotalHours:F1} 小时"
                : $"累计游玩 {ts.Minutes} 分钟";
            return $"{last} · {played} · 创建于 {Instance.CreatedAt:yyyy-MM-dd}";
        }
    }

    public Visibility RunningVis { get; set; } = Visibility.Collapsed;

    private bool _canUninstall = true;
    /// <summary>卸载按钮可用性(运行中/卸载中为 false)</summary>
    public bool CanUninstall
    {
        get => _canUninstall;
        set
        {
            _canUninstall = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUninstall)));
        }
    }
}

public partial class ManageVersionsPage : Page
{
    private readonly InstanceService _instanceService;
    private readonly GameLaunchService _gameLaunchService;
    private readonly ConfigService _configService;
    private bool _uninstalling;

    public ManageVersionsPage(InstanceService instanceService,
                              GameLaunchService gameLaunchService,
                              ConfigService configService)
    {
        _instanceService = instanceService;
        _gameLaunchService = gameLaunchService;
        _configService = configService;
        InitializeComponent();
    }

    private void Page_OnLoaded(object sender, RoutedEventArgs e) => Reload();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    /// <summary>重新扫描实例并刷新列表(页面每次切入都会执行,运行状态始终最新)</summary>
    private void Reload()
    {
        _instanceService.LoadInstances();
        var items = _instanceService.Instances
            .OrderByDescending(i => i.LastPlayedAt)
            .Select(i =>
            {
                bool running = _gameLaunchService.IsInstanceRunning(i.Id);
                return new ManageInstanceItem(i)
                {
                    RunningVis = running ? Visibility.Visible : Visibility.Collapsed,
                    // 运行中的版本禁止卸载(硬性约束)
                    CanUninstall = !running && !_uninstalling
                };
            })
            .ToList();
        InstanceList.ItemsSource = items;
        TxtSummary.Text = items.Count == 0
            ? "暂无已安装的游戏版本,可前往下载中心安装"
            : $"共 {items.Count} 个游戏版本";
        TxtStatus.Text = "就绪 · 卸载不会影响存档与 Java 运行时";
    }

    // ==================== 卸载 ====================

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ManageInstanceItem item) return;
        if (_uninstalling) return;

        var inst = item.Instance;

        // 硬约束1:禁止卸载正在运行中的游戏版本(点击瞬间再次实时校验)
        if (_gameLaunchService.IsInstanceRunning(inst.Id))
        {
            FufuMessage.Warn(Window.GetWindow(this), "无法卸载",
                "该游戏版本正在运行中,禁止卸载。\n请先退出游戏,再回来执行卸载。");
            return;
        }

        // 硬约束2:卸载前弹出二次确认弹窗(危险操作样式,明确告知删除范围与保护范围)
        bool confirmed = FufuMessage.Confirm(Window.GetWindow(this), "确认卸载游戏版本",
            $"确定要卸载「{inst.Name}」(MC {inst.VersionId})吗?\n\n" +
            "将删除:\n" +
            "  · 该版本本体(若其它版本共用则保留)\n" +
            "  · 该版本对应的模组(mods)\n" +
            "  · 该版本的配置文件\n\n" +
            "不会删除:\n" +
            "  · 🛡 saves 存档文件夹(完整保留)\n" +
            "  · ☕ Java 运行时(请在 Java 管理页操作)\n\n" +
            "此操作不可撤销,请确认后继续。",
            okText: "确认卸载", cancelText: "取消", danger: true);
        if (!confirmed)
        {
            TxtStatus.Text = "已取消卸载";
            return;
        }

        // 硬约束3:删除后台异步执行(Task.Run),不阻塞 UI
        _uninstalling = true;
        item.CanUninstall = false;
        TxtStatus.Text = $"正在后台卸载 {inst.Name}…";
        App.WriteAppLog($"[卸载] 开始卸载实例 {inst.Name}({inst.Id})");

        try
        {
            var (ok, error) = await _instanceService.UninstallInstanceAsync(inst.Id);
            if (!IsLoaded) return;

            if (ok)
            {
                // 卸载的是当前选中版本时,清空默认选中,避免主页引用失效实例
                if (_configService.Config.LastInstanceId == inst.Id)
                {
                    _configService.Config.LastInstanceId = "";
                    _configService.Save();
                }
                TxtStatus.Text = $"✓ 已卸载 {inst.Name}(存档已完整保留)";
                FufuMessage.Success(Window.GetWindow(this), "卸载完成",
                    $"「{inst.Name}」已卸载。\n版本本体、模组与版本配置已删除,存档文件夹完整保留。");
                Reload();
            }
            else
            {
                TxtStatus.Text = "卸载失败";
                FufuMessage.Error(Window.GetWindow(this), "卸载失败",
                    "卸载过程中出现问题:\n" + error + "\n\n请稍后重试,或查看日志了解详情。");
                item.CanUninstall = true;
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[卸载] 异常:{ex}");
            if (IsLoaded)
            {
                TxtStatus.Text = "卸载失败";
                FufuMessage.Error(Window.GetWindow(this), "卸载失败", "卸载时发生异常:\n" + ex.Message);
                item.CanUninstall = true;
            }
        }
        finally
        {
            _uninstalling = false;
        }
    }
}

