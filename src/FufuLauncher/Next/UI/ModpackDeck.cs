// Copyright © FufuLauncher
//
// 整合包页(侧边栏一级菜单):默认按下载量列出热门整合包,可搜索/分类筛选,点"安装"直接下载装成游戏版本。
// 风格与版本管理页统一(同款圆角卡片、左图标 + 标题/信息 + 右侧操作按钮)。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FufuLauncher.Services;
using FufuLauncher.Theme;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

public sealed class ModpackDeck : UserControl
{
    private readonly ModrinthService _mr;
    private readonly ModPackImportService _packs;
    private readonly HttpClient _http = new();

    private readonly TextBox _query = UIKit.TextBox(placeholder: "搜索整合包,如 Cobblemon / 空岛 / 魔法");
    private readonly StackPanel _results = new();
    private readonly TextBlock _status = UIKit.Sub("正在加载热门整合包…", 12);

    private static readonly (string Label, string? Slug)[] _cats = new[]
    {
        ("全部", null),
        ("科技工业", "technology"),
        ("魔法", "magic"),
        ("冒险", "adventure"),
        ("优化", "optimization"),
        ("任务", "quests"),
        ("多人", "multiplayer"),
        ("轻量", "lightweight"),
    };
    private string? _curCat;
    private bool _installing;
    private readonly List<Button> _catButtons = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _zhCache = new();

    public ModpackDeck()
    {
        _mr = App.Services.GetRequiredService<ModrinthService>();
        _packs = App.Services.GetRequiredService<ModPackImportService>();
        _http.Timeout = TimeSpan.FromSeconds(30);

        var searchBtn = UIKit.Button("搜索", primary: true, onClick: () => _ = DoSearchAsync(), height: 38);
        searchBtn.Width = 96;
        var searchRow = new DockPanel();
        DockPanel.SetDock(searchBtn, Dock.Right);
        searchBtn.Margin = new Thickness(10, 0, 0, 0);
        searchRow.Children.Add(searchBtn);
        searchRow.Children.Add(_query);
        _query.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = DoSearchAsync(); };

        var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (label, slug) in _cats)
        {
            var b = UIKit.Button(label, primary: false, onClick: () =>
            {
                _curCat = slug;
                PaintChips();
                _ = DoSearchAsync();
            }, height: 32);
            b.Width = 92;
            b.Margin = new Thickness(0, 0, 8, 6);
            b.Tag = slug;
            _catButtons.Add(b);
            chips.Children.Add(b);
        }
        PaintChips();

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = UIKit.V(
                UIKit.PageHeader("整合包", "在线浏览 Modrinth 整合包,点「安装」直接下载成游戏版本"),
                UIKit.Card(UIKit.V(searchRow, chips), pad: 20, topGap: 14),
                _results,
                _status)
        };

        Loaded += async (_, _) => await DoSearchAsync();
    }

    // 选中分类主色发光,其余恢复灰底(2026-09-25:原硬编码橙/深灰死色,浅色主题下很难看,改走主题资源)
    private void PaintChips()
    {
        foreach (var b in _catButtons)
        {
            bool sel = Equals(b.Tag as string, _curCat);
            b.SetResourceReference(Control.BackgroundProperty, sel ? "T.PrimarySoft" : "T.SurfaceAlt");
            b.SetResourceReference(Control.ForegroundProperty, sel ? "T.Primary" : "T.ForegroundDim");
        }
    }

    private async Task DoSearchAsync()
    {
        string q = (_query.Text ?? "").Trim();
        // 空关键词 → 按下载量列热门;有词 → 按相关度搜
        string sort = q.Length == 0 ? "downloads" : "relevance";
        _status.Text = q.Length == 0 ? "加载热门整合包…" : $"搜索「{q}」…";
        _results.Children.Clear();
        try
        {
            var res = await _mr.SearchAsync(q, limit: 30, gameVersion: null, loader: null,
                projectType: "modpack", sort: sort, category: _curCat);
            var hits = res?.Hits ?? new List<ModrinthProject>();
            if (hits.Count == 0)
            {
                _status.Text = "没搜到整合包,换个关键词或分类试试。";
                return;
            }
            _status.Text = $"找到 {hits.Count} 个整合包,点「安装」直接装";
            foreach (var h in hits)
                _results.Children.Add(BuildRow(h));
        }
        catch (Exception ex)
        {
            _status.Text = "搜索失败:" + ex.Message;
        }
    }

    private Border BuildRow(ModrinthProject p)
    {
        // 左:整合包图标
        var img = new Image { Width = 48, Height = 48, Stretch = Stretch.UniformToFill };
        var iconBox = new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(UIKit.R.Chip),
            Background = (Brush?)TryFindResource("T.SurfaceAlt") ?? Brushes.DimGray,
            Child = img,
            Margin = new Thickness(0, 0, 16, 0)
        };
        _ = LoadIconAsync(p.IconUrl, img);

        // 中:标题 + 信息
        var title = UIKit.Text(p.Title, 15, FontWeights.Bold);
        var meta = UIKit.Sub($"{(p.Author.Length > 0 ? p.Author + " · " : "")}{FormatDownloads(p.Downloads)} 次下载", 11.5);
        var desc = UIKit.Sub(p.Description, 12.5);
        desc.TextWrapping = TextWrapping.Wrap;
        var textCol = UIKit.V(title, meta, desc);
        _ = TranslateDescAsync(p.Description, desc);

        // 右:安装按钮(钉右边,文字不被裁)
        var installBtn = UIKit.Button("安装", primary: true, onClick: () => InstallPack(p), height: 36);
        installBtn.Width = 84;
        var rightCol = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rightCol.Children.Add(installBtn);

        var row = new DockPanel();
        DockPanel.SetDock(rightCol, Dock.Right);
        DockPanel.SetDock(iconBox, Dock.Left);
        row.Children.Add(rightCol);
        row.Children.Add(iconBox);
        row.Children.Add(textCol);
        var card = UIKit.Panel(row, pad: 16);
        card.Margin = new Thickness(0, 0, 0, 12);
        return card.HoverLift();   // 列表卡片补悬停上浮,消除整排静止的僵硬感
    }

    private async void InstallPack(ModrinthProject p)
    {
        if (_installing) { Shell()?.SetStatus("正在安装别的整合包,请稍等", warning: true); return; }
        _installing = true;
        try
        {
            Shell()?.SetStatus($"正在安装 {p.Title}…");
            var result = DialogKit.RunWithProgress("安装整合包", async report =>
            {
                report("获取最新版本…");
                var versions = await _mr.GetProjectVersionsAsync(p.ProjectId, null, null);
                var latest = versions.Where(v => v.GetPrimaryFile() != null)
                    .OrderByDescending(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                var file = latest?.GetPrimaryFile();
                if (file == null) throw new Exception("该整合包没有可下载文件");

                string tmp = Path.Combine(Path.GetTempPath(), file.Filename);
                report($"下载 {file.Filename}…");
                var bytes = await _http.GetByteArrayAsync(file.Url);
                await File.WriteAllBytesAsync(tmp, bytes);

                report("开始安装…");
                return await _packs.ImportAsync(tmp, p.Title, report);
            }, Window.GetWindow(this));

            if (result?.Ok == true)
                Shell()?.SetStatus($"整合包「{p.Title}」安装完成,可到版本管理启动");
            else
                Shell()?.SetStatus("整合包安装失败:" + (result?.Message ?? "未知错误"), warning: true);
        }
        catch (Exception ex)
        {
            Shell()?.SetStatus("整合包安装异常:" + ex.Message, warning: true);
        }
        finally { _installing = false; }
    }

    // 英文简介 → 中文(本地词典 → MyMemory → Google 免费端点三级兜底,失败保留原文)
    private async Task TranslateDescAsync(string en, TextBlock desc)
    {
        if (string.IsNullOrWhiteSpace(en)) return;
        // 本地词典:整句内出现社区共识术语时原位替换,离线秒翻
        if (ZhDict.TryLookup(en, out var dictZh))
        {
            _ = Dispatcher.BeginInvoke(new Action(() => desc.Text = dictZh));
            return;
        }
        try
        {
            if (_zhCache.TryGetValue(en, out var cached))
            {
            _ =     Dispatcher.BeginInvoke(new Action(() => desc.Text = cached));
                return;
            }
            string zh = "";
            try
            {
                var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(en) + "&langpair=en|zh-CN";
                var json = await _http.GetStringAsync(url);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var zh0 = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
                zh = System.Text.RegularExpressions.Regex.Replace(zh0, "<[^>]+>", "").Replace("★", "").Trim();   // 剥离 MyMemory 的 HTML 标签与 ★ 标记
            }
            catch
            {
                // MyMemory 免费接口易限流,失败换 Google 免费端点兜底
            }
            if (zh.Length == 0 || zh.Equals(en, StringComparison.OrdinalIgnoreCase))
                zh = await ZhDict.TranslateViaGoogleAsync(_http, en) ?? "";
            if (zh.Length > 0)
            {
                _zhCache[en] = zh;
        _ =         Dispatcher.BeginInvoke(new Action(() => desc.Text = zh));
            }
        }
        catch { /* 翻译失败保留英文 */ }
    }

    private async Task LoadIconAsync(string? url, Image img)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var bytes = await ModrinthService.DownloadIconBytesAsync(url);
            if (bytes == null || bytes.Length == 0) return;
            var bmp = ImageAssets.DecodeBytes(bytes);
            if (bmp == null) return;   // 解码失败(含 WebP 降级失败)留占位
            _ = Dispatcher.BeginInvoke(new Action(() => img.Source = bmp));
        }
        catch { /* 图标失败留占位 */ }
    }

    private static string FormatDownloads(long n)
    {
        if (n >= 1_000_000) return (n / 1_000_000.0).ToString("0.#") + "M";
        if (n >= 1_000) return (n / 1_000.0).ToString("0.#") + "K";
        return n.ToString();
    }

    private ShellWindow? Shell() => Window.GetWindow(this) as ShellWindow;
}
