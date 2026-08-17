// AccountService.cs — 账号管理服务
// 可爱的芙芙 - 阶段3 模块
//
// 多账号管理:列表、切换、删除、当前账号

using System;
using System.Collections.Generic;
using System.Linq;

namespace FufuLauncher.Services;

public class AccountService
{
    private readonly AuthService _authService;
    private readonly ConfigService _configService;
    private GameAccount? _currentAccount;

    public List<GameAccount> Accounts => _authService.Accounts;
    public GameAccount? CurrentAccount => _currentAccount;

    public event Action? AccountsChanged;

    public AccountService(AuthService authService, ConfigService configService)
    {
        _authService = authService;
        _configService = configService;
        _authService.LoadAccounts();

        // 恢复上次使用的账号(持久化到 Config);找不到时回退第一个账号,避免启动时因未选择账号而失败
        string lastUuid = _configService.Config.CurrentAccountUuid;
        _currentAccount = Accounts.FirstOrDefault(a => a.Uuid == lastUuid)
                          ?? Accounts.FirstOrDefault();
    }

    public void SetCurrentAccount(string uuid)
    {
        _currentAccount = Accounts.FirstOrDefault(a => a.Uuid == uuid);
        PersistCurrentAccount();
        AccountsChanged?.Invoke();
    }

    public void DeleteAccount(string uuid)
    {
        _authService.DeleteAccount(uuid);
        if (_currentAccount?.Uuid == uuid)
        {
            // 删的是当前账号:回退到剩余第一个账号并持久化,避免重启后又复活已删账号
            _currentAccount = Accounts.FirstOrDefault();
        }
        PersistCurrentAccount();
        AccountsChanged?.Invoke();
    }

    /// <summary>把当前账号选择写入 Config(重启后恢复同一账号)</summary>
    private void PersistCurrentAccount()
    {
        string uuid = _currentAccount?.Uuid ?? "";
        if (_configService.Config.CurrentAccountUuid == uuid) return;
        _configService.Config.CurrentAccountUuid = uuid;
        _configService.Save();
    }

    /// <summary>启动游戏前校验 token,若过期自动刷新</summary>
    public async System.Threading.Tasks.Task<bool> EnsureValidTokenAsync()
    {
        if (_currentAccount == null) return false;
        if (_currentAccount.Type == AccountType.Offline) return true;

        if (_authService.IsTokenExpired(_currentAccount))
        {
            return await _authService.RefreshTokenAsync(_currentAccount);
        }
        return true;
    }
}
