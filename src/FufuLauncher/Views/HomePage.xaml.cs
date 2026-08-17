// HomePage.xaml.cs — 主页(全新重写)
// 可爱的芙芙
//
// 职责:
//   · 展示当前游戏版本(大字标题 + 版本切换)
//   · 账号卡片(点击唤起账号管理弹窗,导航栏无账号入口)
//   · Java 运行时全局选择(写入 config.JavaPath,启动时直接读取)
//   · 完整性校验闸门:校验不通过禁止启动,强制启动可跳过
//   · 启动游戏 / 强制启动

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FufuLauncher.Interaction;
using FufuLauncher.Services;
using FufuLauncher.ViewModels;

namespace FufuLauncher.Views;

/// <summary>版本下拉展示项</summary>
public class InstanceItem
{
    public GameInstance Inst { get; set; } = null!;
    public string Display { get; set; } = "";
    public override string ToString() => Display;
}

/// <summary>Java 下拉展示项(全局)</summary>
public class JavaSelectionItem
{
    public string Label { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public override string ToString() => Label;
}

public partial class HomePage : Page
{
    private readonly InstanceService _instanceService;
    private readonly AccountService _accountService;
    private readonly AuthService _authService;
    private readonly ConfigService _configService;
    private readonly JavaRuntimeService _javaRuntime;
    private readonly GameLaunchService _gameLaunch;
    private readonly GameInstallService _gameInstall;
    private readonly NavigationViewModel _nav;
    private readonly GameMemoryWatchService _memoryWatch;

    private bool _suppressEvents;
    private bool _integrityPassed;
    private bool _busy;

    // 会话级校验通过缓存:同一实例校验通过后不重复校验(切换实例/重装后重置)
    private readonly HashSet<string> _verifiedInstances = new(StringComparer.OrdinalIgnoreCase);

    // 头像加载:独立 HttpClient + 内存缓存,失败回退占位
    private static readonly HttpClient _avatarHttp = CreateAvatarHttp();
    private readonly Dictionary<string, ImageSource?> _avatarCache = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateAvatarHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) FufuLauncher/1.0");
        return client;
    }

    public HomePage(InstanceService instanceService, AccountService accountService,
                    AuthService authService, ConfigService configService,
                    JavaRuntimeService javaRuntime, GameLaunchService gameLaunch,
                    GameInstallService gameInstall, NavigationViewModel nav,
                    GameMemoryWatchService memoryWatch)
    {
        _instanceService = instanceService;
        _accountService = accountService;
        _authService = authService;
        _configService = configService;
        _javaRuntime = javaRuntime;
        _gameLaunch = gameLaunch;
        _gameInstall = gameInstall;
        _nav = nav;
        _memoryWatch = memoryWatch;
        InitializeComponent();
        _accountService.AccountsChanged += OnAccountsChanged;
        // 游戏进程内存监控:堆 + 堆外拆分展示 + 持续暴涨预警
        _memoryWatch.Updated += OnGameMemoryUpdated;
        _memoryWatch.OffHeapGrowthWarning += OnOffHeapGrowthWarning;
        _gameLaunch.GameExited += OnGameExited;
    }

    private void OnAccountsChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshAccountDisplay); return; }
        RefreshAccountDisplay();
    }

    // ==================== 页面加载 ====================

    private void HomePage_OnLoaded(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        try
        {
            LoadInstances();
            LoadJavaOptions();
        }
        finally { _suppressEvents = false; }

        RefreshAccountDisplay();
        ResetIntegrityGate();
        UpdateLaunchEnabled();
        // 进入页面时若游戏已在运行,恢复内存监控行(文本在 2s 内由采样事件刷新)
        TxtGameMemory.Visibility = _memoryWatch.IsWatching ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== 游戏进程内存监控(堆 + 堆外拆分) ====================

    private void OnGameMemoryUpdated(GameMemorySnapshot s)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => OnGameMemoryUpdated(s))); return; }
        TxtGameMemory.Text = $"🧠 进程总 {s.TotalMb / 1024.0:F2} GB · Java 堆 {s.HeapCapMb} MB · 堆外 +{s.OffHeapMb} MB";
        TxtGameMemory.Visibility = Visibility.Visible;
    }

    private void OnOffHeapGrowthWarning(long offHeapMb)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => OnOffHeapGrowthWarning(offHeapMb))); return; }
        FufuMessage.Warn(Window.GetWindow(this), "⚠ 堆外内存持续暴涨",
            $"检测到游戏堆外内存(DirectBuffer/Native/GPU 资源)持续快速上涨,当前已达 {offHeapMb} MB。\n\n" +
            "常见于 Iris 光影 / Sodium 渲染器的长时间游玩场景。\n" +
            "建议:适时保存进度并重启游戏,避免堆外耗尽导致崩溃;\n" +
            "若持续出现,可尝试降低光影画质或更换光影包。");
    }

    private void OnGameExited()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(OnGameExited)); return; }
        TxtGameMemory.Visibility = Visibility.Collapsed;
        TxtStatus.Text = "游戏已退出,启动器就绪";
        UpdateLaunchEnabled();
    }

    private void LoadInstances()
    {
        _instanceService.LoadInstances();
        var items = _instanceService.Instances.Select(i => new InstanceItem
        {
            Inst = i,
            Display = $"{i.Name}  ·  MC {i.VersionId}" +
                      (string.IsNullOrEmpty(i.ModLoader) ? "" : $" · {i.ModLoader}")
        }).ToList();

        CbInstance.ItemsSource = items;
        if (items.Count > 0)
        {
            var last = items.FirstOrDefault(x => x.Inst.Id == _configService.Config.LastInstanceId);
            CbInstance.SelectedItem = last ?? items[0];
        }
        else
        {
            CbInstance.SelectedItem = null;
        }
        ApplyCurrentInstance();
    }

    private GameInstance? CurrentInstance =>
        (CbInstance.SelectedItem as InstanceItem)?.Inst;

    /// <summary>把当前选中实例的信息刷到大字展示区</summary>
    private void ApplyCurrentInstance()
    {
        var inst = CurrentInstance;
        if (inst == null)
        {
            TxtInstanceName.Text = "未选择版本";
            TxtMcVersion.Text = "Minecraft -";
            LoaderChip.Visibility = Visibility.Collapsed;
            TxtLastPlayed.Text = "请先前往下载中心安装游戏版本";
            return;
        }

        TxtInstanceName.Text = string.IsNullOrEmpty(inst.Name) ? inst.VersionId : inst.Name;
        TxtMcVersion.Text = $"Minecraft {inst.VersionId}";
        if (string.IsNullOrEmpty(inst.ModLoader))
        {
            LoaderChip.Visibility = Visibility.Collapsed;
        }
        else
        {
            LoaderChip.Visibility = Visibility.Visible;
            TxtLoader.Text = $"{inst.ModLoader} {inst.ModLoaderVersion}".Trim();
        }
        TxtLastPlayed.Text = inst.LastPlayedAt > DateTime.MinValue
            ? $"上次游玩:{inst.LastPlayedAt:yyyy-MM-dd HH:mm}"
            : "还没有游玩记录";
    }

    // ==================== 版本切换 ====================

    private void CbInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var inst = CurrentInstance;
        if (inst == null) return;

        _configService.Config.LastInstanceId = inst.Id;
        _configService.Save();
        ApplyCurrentInstance();
        ResetIntegrityGate();
        UpdateLaunchEnabled();
    }

    private void GoDownload_Click(object sender, RoutedEventArgs e) => _nav.RequestNavigate("Download");

    // ==================== Java 全局选择 ====================

    private void LoadJavaOptions()
    {
        var items = new List<JavaSelectionItem>();
        try
        {
            foreach (var rt in _javaRuntime.ListInstalledRuntimes())
            {
                if (string.IsNullOrEmpty(rt.JavaExe) || !File.Exists(rt.JavaExe)) continue;
                string arch = string.IsNullOrEmpty(rt.Architecture) ? "" : $" {rt.Architecture}";
                // rt.MajorVersion 已是 "Java 17" 格式,禁止再拼 "Java " 前缀(曾导致 "Java Java 17" 显示错乱)
                string ver = string.IsNullOrEmpty(rt.MajorVersion) ? "Java" : rt.MajorVersion;
                items.Add(new JavaSelectionItem
                {
                    Label = $"{rt.Name} ({ver}{arch})",
                    JavaPath = rt.JavaExe
                });
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[主页] 扫描 runtimes Java 失败:{ex.Message}");
        }

        CbJava.ItemsSource = items;
        string global = _configService.Config.JavaPath ?? "";
        var current = items.FirstOrDefault(x =>
            string.Equals(x.JavaPath, global, StringComparison.OrdinalIgnoreCase));
        if (current == null && items.Count > 0)
        {
            current = items[0];
            _configService.Config.JavaPath = current.JavaPath;
            _configService.Save();
        }
        CbJava.SelectedItem = current;
    }

    private void CbJava_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (CbJava.SelectedItem is not JavaSelectionItem sel) return;
        if (string.Equals(_configService.Config.JavaPath, sel.JavaPath, StringComparison.OrdinalIgnoreCase)) return;

        _configService.Config.JavaPath = sel.JavaPath;
        _configService.Save();
        TxtStatus.Text = $"已全局切换 Java:{sel.Label}";
        App.WriteAppLog($"[主页] 全局 Java 已切换:{sel.JavaPath}");
    }

    // ==================== 完整性校验闸门 ====================

    private void ResetIntegrityGate()
    {
        _integrityPassed = false;
        UpdateLaunchEnabled();
    }

    private void UpdateLaunchEnabled()
    {
        // 完整性闸门在点击「启动游戏」时自动执行(校验不通过才禁止启动),
        // 按钮保持可用,避免用户感觉「普通启动按钮无效」
        BtnLaunch.IsEnabled = CurrentInstance != null && !_busy;
        BtnVerify.IsEnabled = CurrentInstance != null && !_busy;
        BtnForceLaunch.IsEnabled = CurrentInstance != null && !_busy;
    }

    private async void BtnVerify_Click(object sender, RoutedEventArgs e)
    {
        var inst = CurrentInstance;
        if (inst == null)
        {
            FufuMessage.Warn(Window.GetWindow(this), "提示", "请先选择或下载一个游戏版本。");
            return;
        }

        SetBusy(true, "正在校验游戏版本完整性…");
        try
        {
            var result = await _gameInstall.VerifyInstanceIntegrityAsync(inst.Id);
            _integrityPassed = result.Passed;

            if (result.Passed)
            {
                _verifiedInstances.Add(inst.Id);
                TxtStatus.Text = "✓ " + result.Summary;
                FufuMessage.Success(Window.GetWindow(this), "完整性校验",
                    $"✓ 校验通过\n{result.Summary}\n\n现在可以启动游戏了。");
            }
            else
            {
                TxtStatus.Text = "✗ " + result.Summary;
                string detail = "";
                if (result.MissingFiles.Count > 0)
                    detail += "\n\n缺失文件:\n  - " + string.Join("\n  - ", result.MissingFiles.Take(15));
                if (result.CorruptFiles.Count > 0)
                    detail += "\n\n损坏文件:\n  - " + string.Join("\n  - ", result.CorruptFiles.Take(15));
                FufuMessage.Warn(Window.GetWindow(this), "完整性校验未通过",
                    $"{result.Summary}{detail}\n\n已禁止启动,请前往「下载中心」重新安装此版本以修复。");
            }
        }
        catch (Exception ex)
        {
            _integrityPassed = false;
            TxtStatus.Text = "校验失败:" + ex.Message;
            FufuMessage.Error(Window.GetWindow(this), "校验失败", "完整性校验过程出错:\n" + ex.Message);
            App.WriteAppLog($"[主页] 完整性校验异常 {inst.Id}:{ex}");
        }
        finally
        {
            SetBusy(false);
            UpdateLaunchEnabled();
        }
    }

    // ==================== 启动游戏 ====================

    /// <summary>启动前检查账号:无账号时唤起账号管理弹窗,微软账号确保令牌有效</summary>
    private async Task<bool> EnsureAccountReadyAsync()
    {
        if (_accountService.CurrentAccount == null)
        {
            FufuMessage.Info(Window.GetWindow(this), "需要账号",
                "还没有可用的游戏账号,请在账号管理中添加微软账号或离线账号。");
            OpenAccountDialog();
            return _accountService.CurrentAccount != null;
        }

        if (_accountService.CurrentAccount.Type == AccountType.Microsoft)
        {
            TxtStatus.Text = "正在校验账号令牌…";
            bool ok = await _accountService.EnsureValidTokenAsync();
            if (!ok)
            {
                FufuMessage.Warn(Window.GetWindow(this), "账号令牌失效",
                    "微软账号令牌已失效且自动刷新失败,请重新登录该账号。");
                OpenAccountDialog();
                return false;
            }
        }
        return true;
    }

    private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        var inst = CurrentInstance;
        if (inst == null)
        {
            FufuMessage.Warn(Window.GetWindow(this), "提示", "请先选择或下载一个游戏版本。");
            return;
        }
        if (_gameLaunch.IsGameRunning)
        {
            FufuMessage.Info(Window.GetWindow(this), "提示", "已有游戏进程在运行。");
            return;
        }
        SetBusy(true, "正在校验游戏完整性…");
        try
        {
            // 完整性闸门(参考 PCL2:一键启动自动完成校验):校验不通过禁止启动
            if (!_verifiedInstances.Contains(inst.Id))
            {
                var check = await _gameInstall.VerifyInstanceIntegrityAsync(inst.Id);
                if (!check.Passed)
                {
                    _integrityPassed = false;
                    string detail = "";
                    if (check.MissingFiles.Count > 0)
                        detail += "\n\n缺失文件:\n  - " + string.Join("\n  - ", check.MissingFiles.Take(10));
                    FufuMessage.Warn(Window.GetWindow(this), "禁止启动",
                        $"完整性校验未通过:{check.Summary}{detail}\n\n请前往「下载中心」重新安装此版本以修复,或使用「强制启动」跳过校验。");
                    return;
                }
                _integrityPassed = true;
                _verifiedInstances.Add(inst.Id);
                App.WriteAppLog($"[主页] 启动前自动校验通过:{inst.VersionId}");
            }

            if (string.IsNullOrEmpty(_configService.Config.JavaPath))
            {
                FufuMessage.Warn(Window.GetWindow(this), "缺少 Java",
                    "尚未选择 Java 运行时,请先在下方选择 Java,或前往「Java 管理」下载。");
                return;
            }

            if (!await EnsureAccountReadyAsync()) return;

            TxtStatus.Text = "正在启动游戏…";
            var result = await _gameLaunch.LaunchAsync(inst.Id);
            if (result.Success)
            {
                TxtStatus.Text = "✓ 游戏已启动";
                FufuMessage.Success(Window.GetWindow(this), "启动成功", "游戏进程已成功拉起,祝游玩愉快~");
            }
            else
            {
                TxtStatus.Text = "启动失败:" + result.ErrorMessage;
                FufuMessage.Error(Window.GetWindow(this), "启动失败", result.ErrorMessage ?? "未知错误");
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "启动异常:" + ex.Message;
            FufuMessage.Error(Window.GetWindow(this), "启动异常", ex.Message);
            App.WriteAppLog($"[主页] 启动异常:{ex}");
        }
        finally
        {
            SetBusy(false);
            UpdateLaunchEnabled();
        }
    }

    private async void BtnForceLaunch_Click(object sender, RoutedEventArgs e)
    {
        var inst = CurrentInstance;
        if (inst == null)
        {
            FufuMessage.Warn(Window.GetWindow(this), "提示", "请先选择或下载一个游戏版本。");
            return;
        }
        if (_gameLaunch.IsGameRunning)
        {
            FufuMessage.Info(Window.GetWindow(this), "提示", "已有游戏进程在运行。");
            return;
        }
        if (!FufuMessage.Confirm(Window.GetWindow(this), "⚡ 强制启动确认",
                "强制启动将跳过完整性校验与令牌校验:\n" +
                "· 跳过完整性校验\n· 跳过账号令牌校验\n\n" +
                "游戏可能因文件缺失而崩溃,确定继续?",
                okText: "强制启动", danger: true))
            return;

        SetBusy(true, "⚡ 强制启动中…");
        try
        {
            var result = await _gameLaunch.LaunchAsync(inst.Id, forceLaunch: true);
            if (result.Success)
            {
                TxtStatus.Text = "⚡ 游戏已强制启动";
            }
            else
            {
                TxtStatus.Text = "⚡ 强制启动失败:" + result.ErrorMessage;
                FufuMessage.Error(Window.GetWindow(this), "强制启动失败", result.ErrorMessage ?? "未知错误");
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "启动异常:" + ex.Message;
            App.WriteAppLog($"[主页] 强制启动异常:{ex}");
        }
        finally
        {
            SetBusy(false);
            UpdateLaunchEnabled();
        }
    }

    private void SetBusy(bool busy, string? statusText = null)
    {
        _busy = busy;
        LaunchProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (statusText != null) TxtStatus.Text = statusText;
        UpdateLaunchEnabled();
        if (busy)
        {
            BtnVerify.IsEnabled = false;
            BtnLaunch.IsEnabled = false;
            BtnForceLaunch.IsEnabled = false;
        }
    }

    // ==================== 账号卡片 ====================

    private void OpenAccountDialog_Click(object sender, RoutedEventArgs e) => OpenAccountDialog();

    private void OpenAccountDialog()
    {
        var owner = Window.GetWindow(this);
        var dlg = new AccountDialogWindow(_accountService, _authService) { Owner = owner };
        dlg.ShowDialog();
        RefreshAccountDisplay();
        ResetIntegrityGate();
    }

    private void RefreshAccountDisplay()
    {
        var current = _accountService.CurrentAccount;
        if (current == null)
        {
            TxtAccountName.Text = "未登录";
            TxtAccountType.Text = "点击管理账号";
        }
        else
        {
            TxtAccountName.Text = string.IsNullOrEmpty(current.Username) ? "(未命名)" : current.Username;
            TxtAccountType.Text = current.Type == AccountType.Offline ? "离线账号 · 点击管理" : "微软正版 · 点击管理";
        }
        _ = RefreshAvatarAsync(current);
    }

    private async Task RefreshAvatarAsync(GameAccount? acc)
    {
        ImgAvatar.Source = null;
        ImgAvatar.Visibility = Visibility.Collapsed;
        TxtAvatarPlaceholder.Visibility = Visibility.Visible;
        if (acc == null) return;

        try
        {
            var src = await TryLoadAvatarAsync(acc);
            if (src != null && _accountService.CurrentAccount?.Uuid == acc.Uuid)
            {
                ImgAvatar.Source = src;
                ImgAvatar.Visibility = Visibility.Visible;
                TxtAvatarPlaceholder.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[主页] 头像显示异常:{ex.Message}");
        }
    }

    private async Task<ImageSource?> TryLoadAvatarAsync(GameAccount acc)
    {
        if (acc.Type == AccountType.Offline) return null;
        if (_avatarCache.TryGetValue(acc.Uuid, out var cached)) return cached;

        // 取 16px 小图,UI 上 NearestNeighbor 整数倍放大(16×3=48),呈现清晰的像素风方块
        string[] urls =
        {
            $"https://crafatar.com/avatars/{Uri.EscapeDataString(acc.Uuid)}?size=16&overlay",
            $"https://mc-heads.net/avatar/{Uri.EscapeDataString(acc.Uuid)}/16",
            $"https://visage.surgeplay.com/face/16/{Uri.EscapeDataString(acc.Uuid)}"
        };
        foreach (var url in urls)
        {
            try
            {
                var bytes = await _avatarHttp.GetByteArrayAsync(url);
                if (bytes.Length == 0) continue;
                var img = CreateImageFromBytes(bytes);
                _avatarCache[acc.Uuid] = img;
                return img;
            }
            catch (Exception ex)
            {
                App.WriteAppLog($"[主页] 头像源失败({acc.Username}) {url}: {ex.Message}");
            }
        }
        return null;
    }

    private static ImageSource CreateImageFromBytes(byte[] bytes)
    {
        var bi = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
