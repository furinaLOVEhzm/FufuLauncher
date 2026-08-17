// MarketPage.xaml.cs — 在线市场(全新重写)
// 可爱的芙芙
//
// 基于 Modrinth 公开接口(服务端做多端点容错回退)。
// 模组/资源包/光影三类资源,按当前游戏版本与加载器筛选,
// 一键安装到 APP\mcGAME 对应目录(mods/resourcepacks/shaderpacks)。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FufuLauncher.Interaction;
using FufuLauncher.Services;

namespace FufuLauncher.Views;

/// <summary>市场搜索结果展示项</summary>
public class MarketItem : INotifyPropertyChanged
{
    public ModrinthProject Project { get; }
    public MarketItem(ModrinthProject p) { Project = p; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => string.IsNullOrEmpty(Project.Title) ? Project.Slug : Project.Title;
    public string Description => Project.Description;

    public string DownloadsDisplay => "⬇ " + Project.Downloads switch
    {
        >= 1_000_000 => $"{Project.Downloads / 1_000_000.0:F1}M",
        >= 1_000 => $"{Project.Downloads / 1_000.0:F1}K",
        _ => Project.Downloads.ToString()
    };

    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Project.Author)) parts.Add(Project.Author);
            if (Project.Loaders.Count > 0)
                parts.Add(string.Join("/", Project.Loaders.Select(l => char.ToUpperInvariant(l[0]) + l[1..])));
            return string.Join(" · ", parts);
        }
    }

    public bool CanInstall { get; set; } = true;

    private ImageSource? _icon;
    private bool _iconLoading;
    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && !_iconLoading && !string.IsNullOrEmpty(Project.IconUrl))
            {
                _iconLoading = true;
                _ = LoadIconAsync();
            }
            return _icon;
        }
        private set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    private async Task LoadIconAsync()
    {
        try
        {
            var bytes = await ModrinthService.DownloadIconBytesAsync(Project.IconUrl!);
            if (bytes == null || bytes.Length == 0) return;
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            Icon = bmp;
        }
        catch { /* 图标加载失败由占位图兜底 */ }
    }
}

public partial class MarketPage : Page
{
    private readonly ModrinthService _modrinth;
    private readonly InstanceService _instanceService;
    private readonly DownloadService _downloadService;
    private readonly ConfigService _configService;

    private CancellationTokenSource? _searchCts;
    private bool _busy;

    private string ProjectType =>
        RbRespack != null && RbRespack.IsChecked == true ? "resourcepack" :
        RbShader != null && RbShader.IsChecked == true ? "shader" : "mod";

    private GameInstance? CurrentInstance =>
        _instanceService.Instances.FirstOrDefault(i => i.Id == _configService.Config.LastInstanceId);

    public MarketPage(ModrinthService modrinth, InstanceService instanceService,
                      DownloadService downloadService, ConfigService configService)
    {
        _modrinth = modrinth;
        _instanceService = instanceService;
        _downloadService = downloadService;
        _configService = configService;
        InitializeComponent();
    }

    // ==================== 初始化 ====================

    private async void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        TxtApiInfo.Text = $"资源源:Modrinth 官方 API · 当前接入点:{ModrinthService.CurrentApiBase}";

        _instanceService.LoadInstances();
        var inst = CurrentInstance;
        if (inst != null)
        {
            TxtInstanceHint.Text = $"安装到:{inst.Name} · MC {inst.VersionId}";
            CbGameVersion.ItemsSource = new[] { inst.VersionId, "不限版本" };
            CbGameVersion.SelectedIndex = 0;
            string loader = string.IsNullOrEmpty(inst.ModLoader) ? "不限加载器" : inst.ModLoader.ToLowerInvariant();
            var loaders = new[] { "不限加载器", "fabric", "forge", "neoforge", "quilt" };
            CbLoader.ItemsSource = loaders;
            int idx = loaders.ToList().IndexOf(loader);
            CbLoader.SelectedIndex = idx >= 0 ? idx : 0;
        }
        else
        {
            TxtInstanceHint.Text = "未选择游戏版本(仍可浏览,安装需先选择版本)";
            CbGameVersion.ItemsSource = new[] { "不限版本" };
            CbGameVersion.SelectedIndex = 0;
            CbLoader.ItemsSource = new[] { "不限加载器", "fabric", "forge", "neoforge", "quilt" };
            CbLoader.SelectedIndex = 0;
        }

        await SearchAsync();
    }

    // ==================== 搜索 ====================

    private void Category_Checked(object sender, RoutedEventArgs e)
    {
        if (ResultList == null) return;
        _ = SearchAsync();
    }

    private void TxtQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchAsync();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void Hot_Click(object sender, RoutedEventArgs e)
    {
        TxtQuery.Text = "";
        await SearchAsync();
    }

    private string? FilterGameVersion =>
        CbGameVersion?.SelectedItem is string s && s != "不限版本" ? s : null;

    private string? FilterLoader =>
        CbLoader?.SelectedItem is string s && s != "不限加载器" ? s : null;

    private async Task SearchAsync()
    {
        if (_busy) return;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        _busy = true;
        string query = TxtQuery?.Text.Trim() ?? "";
        string catName = ProjectType == "mod" ? "模组" : ProjectType == "resourcepack" ? "资源包" : "光影";
        TxtStatus.Text = $"正在搜索{catName}…";

        try
        {
            // 关键修复:加载器(loader)筛选只对模组(mod)有意义;
            // 光影/资源包没有加载器分类,传 loader facet 会导致 0 命中(列表空白)
            var result = await _modrinth.SearchAsync(
                query, offset: 0, limit: 30,
                gameVersion: FilterGameVersion,
                loader: ProjectType == "mod" ? FilterLoader : null,
                projectType: ProjectType,
                sort: string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance",
                ct: ct);
            if (ct.IsCancellationRequested) return;

            if (_modrinth.LastError != null)
            {
                TxtStatus.Text = _modrinth.LastError;
                FufuMessage.Warn(Window.GetWindow(this), "加载失败",
                    _modrinth.LastError + "\n\n已尝试全部可用接入点,请检查网络后重试。");
                return;
            }

            var items = result.Hits.Select(h => new MarketItem(h)
            {
                CanInstall = CurrentInstance != null
            }).ToList();
            ResultList.ItemsSource = items;
            TxtStatus.Text = result.TotalHits > 0
                ? $"找到 {result.TotalHits} 个{catName},已加载 {items.Count} 个"
                : $"未找到匹配的{catName},请尝试其他关键词或放宽筛选";
            TxtApiInfo.Text = $"资源源:Modrinth 官方 API · 当前接入点:{ModrinthService.CurrentApiBase}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TxtStatus.Text = "搜索失败";
            App.WriteAppLog($"[市场] 搜索异常:{ex}");
            FufuMessage.Error(Window.GetWindow(this), "搜索失败", "网络异常,无法访问资源接口:\n" + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    // ==================== 安装 ====================

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not MarketItem item) return;
        var inst = CurrentInstance;
        if (inst == null)
        {
            FufuMessage.Warn(Window.GetWindow(this), "需要游戏版本",
                "请先在主页选择(或到下载中心安装)一个游戏版本,再安装资源。");
            return;
        }
        if (_busy) return;
        _busy = true;

        string targetDir = ProjectType switch
        {
            "resourcepack" => _instanceService.GetResourcePacksDir(inst.Id),
            "shader" => _instanceService.GetShaderPacksDir(inst.Id),
            _ => _instanceService.GetModsDir(inst.Id)
        };

        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.IsIndeterminate = true;
        TxtStatus.Text = $"正在获取 {item.Title} 的下载信息…";

        // 进度订阅(命名方法便于退订)
        void OnProgress(DownloadProgressInfo info)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (info.TotalBytes > 0)
                {
                    InstallProgress.IsIndeterminate = false;
                    InstallProgress.Value = info.Progress * 100;
                }
            });
        }
        _downloadService.OverallProgressChanged += OnProgress;

        try
        {
            var versions = await _modrinth.GetProjectVersionsAsync(
                item.Project.ProjectId,
                gameVersion: FilterGameVersion,
                loader: ProjectType == "mod" ? FilterLoader : null);
            var main = versions.FirstOrDefault();
            if (main == null)
            {
                string msg = _modrinth.LastError
                    ?? $"未找到兼容 {FilterGameVersion ?? "当前版本"} 的文件,请调整筛选条件。";
                TxtStatus.Text = msg;
                FufuMessage.Warn(Window.GetWindow(this), "无兼容版本", msg);
                return;
            }

            // 模组依赖提示(仅检测必需前置,不自动下载)
            if (ProjectType == "mod")
            {
                var deps = new List<string>();
                foreach (var dep in main.Dependencies
                             .Where(d => d.DependencyType == "required" && !string.IsNullOrEmpty(d.ProjectId))
                             .Take(8))
                {
                    var proj = await _modrinth.GetProjectAsync(dep.ProjectId!);
                    deps.Add(proj?.Title ?? dep.ProjectId!);
                }
                if (deps.Count > 0)
                {
                    FufuMessage.Info(Window.GetWindow(this), "依赖提示",
                        $"该模组需要以下前置,请确认已安装:\n  - {string.Join("\n  - ", deps)}");
                }
            }

            TxtStatus.Text = $"正在下载 {main.Name}…";
            bool ok = await _modrinth.DownloadModVersionAsync(main, targetDir);
            if (ok)
            {
                TxtStatus.Text = $"✓ 已安装到 {targetDir}";
                FufuMessage.Success(Window.GetWindow(this), "安装完成",
                    $"{item.Title} 已安装到:\n{targetDir}");
            }
            else
            {
                TxtStatus.Text = "下载失败";
                FufuMessage.Error(Window.GetWindow(this), "安装失败",
                    "下载失败,请检查网络后重试(已自动尝试重试与镜像切换)。");
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "安装失败";
            App.WriteAppLog($"[市场] 安装异常:{ex}");
            FufuMessage.Error(Window.GetWindow(this), "安装失败", ex.Message);
        }
        finally
        {
            _downloadService.OverallProgressChanged -= OnProgress;
            InstallProgress.IsIndeterminate = false;
            InstallProgress.Visibility = Visibility.Collapsed;
            _busy = false;
        }
    }
}
