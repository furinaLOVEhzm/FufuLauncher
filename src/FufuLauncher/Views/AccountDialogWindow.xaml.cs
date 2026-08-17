// AccountDialogWindow.xaml.cs — 账号管理弹窗(全新重写)
// 可爱的芙芙
//
// 仅从主页启动栏的账号入口唤起(导航栏无入口)。
// 功能:账号列表、切换使用、删除、添加微软账号、添加离线账号。
// 账号数据固化在 APP\mcGAME\accounts,与游戏版本数据严格隔离。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FufuLauncher.Interaction;
using FufuLauncher.Services;

namespace FufuLauncher.Views;

/// <summary>账号列表展示项(视图包装)</summary>
public class AccountCardItem
{
    public string Uuid { get; set; } = "";
    public string Username { get; set; } = "";
    public string UuidShort { get; set; } = "";
    public string TypeLabel { get; set; } = "";
    public Brush TypeBadgeBg { get; set; } = Brushes.Gray;
    public string Initial { get; set; } = "?";
    public Visibility IsCurrentVis { get; set; } = Visibility.Collapsed;
}

public partial class AccountDialogWindow : Window
{
    private readonly AccountService _accountService;
    private readonly AuthService _authService;

    public AccountDialogWindow(AccountService accountService, AuthService authService)
    {
        InitializeComponent();
        _accountService = accountService;
        _authService = authService;
        _accountService.AccountsChanged += RefreshList;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RefreshList();

    protected override void OnClosed(EventArgs e)
    {
        _accountService.AccountsChanged -= RefreshList;
        base.OnClosed(e);
    }

    /// <summary>刷新账号列表(当前账号置顶高亮)</summary>
    private void RefreshList()
    {
        try
        {
            var current = _accountService.CurrentAccount;
            var items = new List<AccountCardItem>();
            foreach (var acc in _accountService.Accounts
                         .OrderByDescending(a => a.Uuid == current?.Uuid)
                         .ThenByDescending(a => a.AddedAt))
            {
                bool isMs = acc.Type == AccountType.Microsoft;
                items.Add(new AccountCardItem
                {
                    Uuid = acc.Uuid,
                    Username = string.IsNullOrEmpty(acc.Username) ? "(未命名)" : acc.Username,
                    UuidShort = acc.Uuid.Length > 18 ? acc.Uuid[..8] + "…" + acc.Uuid[^4..] : acc.Uuid,
                    TypeLabel = isMs ? "微软正版" : "离线模式",
                    TypeBadgeBg = isMs ? new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF))
                                       : new SolidColorBrush(Color.FromRgb(0x8B, 0x90, 0xA5)),
                    Initial = string.IsNullOrEmpty(acc.Username) ? "?" : acc.Username[..1].ToUpperInvariant(),
                    IsCurrentVis = acc.Uuid == current?.Uuid ? Visibility.Visible : Visibility.Collapsed
                });
            }
            AccountList.ItemsSource = items;
            EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[账号弹窗] 刷新列表失败:{ex.Message}");
        }
    }

    #region 账号操作

    private void UseAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string uuid)
        {
            _accountService.SetCurrentAccount(uuid);
            var acc = _accountService.CurrentAccount;
            FufuMessage.Success(this, "切换账号", $"已切换到账号:{acc?.Username ?? "未知"}");
            RefreshList();
        }
    }

    private void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string uuid) return;
        var acc = _accountService.Accounts.FirstOrDefault(a => a.Uuid == uuid);
        string name = acc?.Username ?? uuid;
        if (!FufuMessage.Confirm(this, "删除账号", $"确定删除账号「{name}」吗?\n删除后需要重新登录。",
                                 okText: "删除", danger: true))
            return;
        _accountService.DeleteAccount(uuid);
        App.WriteAppLog($"[账号] 已删除账号:{name}");
        RefreshList();
    }

    /// <summary>微软登录(本地回调 OAuth,业务链路保留 AuthService 实现)</summary>
    private async void MsLogin_Click(object sender, RoutedEventArgs e)
    {
        BtnMsLogin.IsEnabled = false;
        BtnMsLogin.Content = "⏳ 等待浏览器授权…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var msToken = await _authService.LoginWithLocalCallbackAsync(cts.Token);
            if (msToken == null)
            {
                FufuMessage.Warn(this, "登录失败", "授权码无效或已过期,请重新尝试。");
                return;
            }

            var account = await _authService.CompleteLoginFromMsTokenAsync(msToken);
            if (account != null)
            {
                _accountService.SetCurrentAccount(account.Uuid);
                FufuMessage.Success(this, "登录成功", $"微软账号登录成功:{account.Username}\n已设为当前账号。");
                RefreshList();
            }
        }
        catch (AuthException ex)
        {
            App.WriteAppLog($"[账号弹窗] 微软登录失败({ex.Error}):{ex.Message}");
            FufuMessage.Error(this, "登录失败", ex.Message);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[账号弹窗] 登录异常:{ex}");
            FufuMessage.Error(this, "登录失败", "登录过程中出现异常,请查看日志了解详情。");
        }
        finally
        {
            BtnMsLogin.IsEnabled = true;
            BtnMsLogin.Content = "🔐 添加微软账号";
        }
    }

    /// <summary>离线账号添加(昵称输入弹窗)</summary>
    private void OfflineLogin_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialogWindow("添加离线账号", "请输入游戏内显示的玩家昵称:",
            null, isPassword: false,
            validate: s => !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= 16,
            watermark: "3~16 个字符")
        {
            Owner = this
        };
        if (dlg.ShowDialog() != true) return;

        string nickname = dlg.ResultText?.Trim() ?? "";
        if (string.IsNullOrEmpty(nickname)) return;

        try
        {
            var account = _authService.LoginOffline(nickname);
            _accountService.SetCurrentAccount(account.Uuid);
            App.WriteAppLog($"[账号] 添加离线账号:{nickname}");
            FufuMessage.Success(this, "添加成功", $"离线账号「{nickname}」已添加并设为当前账号。");
            RefreshList();
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[账号弹窗] 添加离线账号失败:{ex.Message}");
            FufuMessage.Error(this, "添加失败", ex.Message);
        }
    }

    #endregion

    #region 标题栏

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    #endregion
}
