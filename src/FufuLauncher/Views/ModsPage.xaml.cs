// ModsPage.xaml.cs — 模组管理(全新重写)
// 可爱的芙芙
//
// 模组与游戏版本严格绑定:读取 APP\mcGAME\mods\{实例} 目录。
// 功能:启用/禁用(改名备份不删除)、删除、导入、拖拽导入、打开目录、搜索过滤。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FufuLauncher.Interaction;
using FufuLauncher.Services;
using Microsoft.Win32;

namespace FufuLauncher.Views;

/// <summary>模组卡片展示项</summary>
public class ModCardItem
{
    public ModInfo Info { get; set; } = null!;
    public string Display { get; set; } = "";
    public string StateText { get; set; } = "";
    public string MetaText { get; set; } = "";
    public string ToggleText { get; set; } = "";
    public double RowOpacity { get; set; } = 1.0;
}

/// <summary>实例下拉展示项</summary>
public class ModsInstanceItem
{
    public GameInstance Inst { get; set; } = null!;
    public string Display { get; set; } = "";
    public override string ToString() => Display;
}

public partial class ModsPage : Page
{
    private readonly InstanceService _instanceService;
    private readonly ModManagerService _modManager;
    private readonly ConfigService _configService;

    private bool _suppress;
    private List<ModCardItem> _allMods = new();

    public ModsPage(InstanceService instanceService, ModManagerService modManager,
                    ConfigService configService)
    {
        _instanceService = instanceService;
        _modManager = modManager;
        _configService = configService;
        InitializeComponent();
    }

    // ==================== 加载 ====================

    private void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        _instanceService.LoadInstances();
        var items = _instanceService.Instances.Select(i => new ModsInstanceItem
        {
            Inst = i,
            Display = $"{i.Name} · MC {i.VersionId}"
        }).ToList();
        CbInstance.ItemsSource = items;

        var last = items.FirstOrDefault(x => x.Inst.Id == _configService.Config.LastInstanceId)
                   ?? items.FirstOrDefault();
        CbInstance.SelectedItem = last;
        _suppress = false;

        if (last == null)
        {
            TxtInstanceInfo.Text = "还没有游戏版本,请先前往「下载中心」安装";
            ModList.ItemsSource = null;
            TxtEmpty.Visibility = Visibility.Visible;
            return;
        }
        SwitchToInstance(last.Inst);
    }

    private void CbInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (CbInstance.SelectedItem is not ModsInstanceItem item) return;
        _configService.Config.LastInstanceId = item.Inst.Id;
        _configService.Save();
        SwitchToInstance(item.Inst);
    }

    private void SwitchToInstance(GameInstance inst)
    {
        _modManager.SetCurrentInstance(inst.Id);
        string loader = string.IsNullOrEmpty(inst.ModLoader) ? "原版" : inst.ModLoader;
        TxtInstanceInfo.Text = $"当前版本:{inst.Name} · MC {inst.VersionId} · {loader}" +
                               $" · 模组目录:{_instanceService.GetModsDir(inst.Id)}";
        RefreshMods();
    }

    // ==================== 列表 ====================

    private void RefreshMods()
    {
        _allMods = _modManager.LoadMods().Select(m => new ModCardItem
        {
            Info = m,
            Display = string.IsNullOrEmpty(m.Name) ? m.FileName : m.Name,
            StateText = m.Enabled ? "✓ 已启用" : "已禁用",
            MetaText = string.Join(" · ", new[]
            {
                m.FileName,
                string.IsNullOrEmpty(m.Version) ? "" : $"v{m.Version}",
                string.IsNullOrEmpty(m.Author) ? "" : m.Author,
                FormatSize(m.Size)
            }.Where(s => !string.IsNullOrEmpty(s))),
            ToggleText = m.Enabled ? "禁用" : "启用",
            RowOpacity = m.Enabled ? 1.0 : 0.6
        }).ToList();

        ApplyFilter();
        int enabled = _allMods.Count(m => m.Info.Enabled);
        TxtSummary.Text = $"共 {_allMods.Count} 个模组({enabled} 个启用)";
        TxtEmpty.Visibility = _allMods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtEmpty.Text = "暂无模组\n可点击「导入模组」或前往「在线市场」下载安装";
    }

    private void ApplyFilter()
    {
        string keyword = TxtSearch?.Text.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(keyword)
            ? _allMods
            : _allMods.Where(m =>
                m.Display.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                m.Info.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        ModList.ItemsSource = filtered;
        if (_allMods.Count > 0 && filtered.Count == 0)
        {
            TxtEmpty.Visibility = Visibility.Visible;
            TxtEmpty.Text = "没有匹配的模组";
        }
        else if (_allMods.Count > 0)
        {
            TxtEmpty.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1024.0 / 1024:F1} MB";
    }

    // ==================== 操作 ====================

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_modManager.CurrentInstanceId == null) return;
        RefreshMods();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_modManager.CurrentInstanceId == null)
        {
            FufuMessage.Info(Window.GetWindow(this), "提示", "请先选择一个游戏版本。");
            return;
        }
        _modManager.OpenModsFolder();
    }

    private void ImportMod_Click(object sender, RoutedEventArgs e)
    {
        if (_modManager.CurrentInstanceId == null)
        {
            FufuMessage.Info(Window.GetWindow(this), "提示", "请先选择一个游戏版本。");
            return;
        }
        var ofd = new OpenFileDialog
        {
            Title = "选择模组文件(.jar)",
            Filter = "模组文件 (*.jar)|*.jar|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (ofd.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var f in ofd.FileNames) ImportOne(f);
        RefreshMods();
    }

    private void ImportOne(string path)
    {
        var (ok, err) = _modManager.ImportModFile(path);
        if (ok)
        {
            App.WriteAppLog($"[模组] 导入成功:{Path.GetFileName(path)}");
        }
        else
        {
            FufuMessage.Warn(Window.GetWindow(this), "导入失败",
                $"{Path.GetFileName(path)} 导入失败:\n{err ?? "未知原因"}");
        }
    }

    private void ToggleMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ModCardItem item) return;
        _modManager.ToggleMod(item.Info.FilePath, !item.Info.Enabled);
        RefreshMods();
    }

    private void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ModCardItem item) return;
        if (!FufuMessage.Confirm(Window.GetWindow(this), "删除模组",
                $"确定删除模组「{item.Display}」?\n文件将被彻底删除:{item.Info.FileName}",
                okText: "删除", danger: true))
            return;
        if (_modManager.DeleteMod(item.Info.FilePath))
        {
            RefreshMods();
        }
        else
        {
            FufuMessage.Error(Window.GetWindow(this), "删除失败", "文件删除失败,可能被占用,请稍后重试。");
        }
    }

    // ==================== 拖拽导入 ====================

    private void Page_OnDragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        bool ok = files != null && files.Any(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Page_OnDrop(object sender, DragEventArgs e)
    {
        if (_modManager.CurrentInstanceId == null)
        {
            FufuMessage.Info(Window.GetWindow(this), "提示", "请先选择一个游戏版本再导入模组。");
            return;
        }
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null) return;
        foreach (var f in files.Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
            ImportOne(f);
        RefreshMods();
    }
}

