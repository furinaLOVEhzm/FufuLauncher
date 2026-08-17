// JavaRuntimePage.xaml.cs — Java 管理(全新重写)
// 可爱的芙芙
//
// 读取 APP\mcGAME\runtimes 目录;支持:
//   · 新增本地 Java(以目录联接挂入 runtimes,不复制不占额外空间)
//   · 删除 Java(联接只删链接,实体目录确认后删除)
//   · 设为默认(写全局 config.JavaPath,启动游戏直接读取)
//   · 刷新列表

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FufuLauncher.Interaction;
using FufuLauncher.Services;
using FufuLauncher.ViewModels;
using Microsoft.Win32;

namespace FufuLauncher.Views;

/// <summary>Java 卡片展示项</summary>
public class JavaCardItem
{
    public InstalledJavaEntry Entry { get; set; } = null!;
    public string DisplayName { get; set; } = "";
    public string MetaText { get; set; } = "";
    public string StatusText { get; set; } = "";
    public Visibility DefaultVis { get; set; } = Visibility.Collapsed;
    public bool CanSetDefault { get; set; }
    public bool IsJunction { get; set; }
}

public partial class JavaRuntimePage : Page
{
    private readonly JavaRuntimeService _javaRuntime;
    private readonly ConfigService _configService;
    private readonly NavigationViewModel _nav;

    public JavaRuntimePage(JavaRuntimeService javaRuntime, ConfigService configService,
                           NavigationViewModel nav)
    {
        _javaRuntime = javaRuntime;
        _configService = configService;
        _nav = nav;
        InitializeComponent();
    }

    private void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        TxtRuntimesDir.Text = $"统一目录:{JavaRuntimeService.RuntimesDir} · 主页选择的 Java 全局生效";
        RefreshList();
    }

    // ==================== 列表 ====================

    private void RefreshList()
    {
        var entries = _javaRuntime.ListInstalledRuntimes();
        string defaultJava = _configService.Config.JavaPath ?? "";

        var items = entries
            .OrderByDescending(r => string.Equals(r.JavaExe, defaultJava, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(r => r.Status == "已就绪")
            .Select(r => new JavaCardItem
            {
                Entry = r,
                DisplayName = $"{(string.IsNullOrEmpty(r.MajorVersion) ? "Java" : r.MajorVersion)} · {r.Name}",
                MetaText = $"{r.Kind}{(string.IsNullOrEmpty(r.Architecture) ? "" : " · " + r.Architecture)} · {r.JavaExe}",
                StatusText = r.Status,
                DefaultVis = string.Equals(r.JavaExe, defaultJava, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed,
                CanSetDefault = r.Status == "已就绪",
                IsJunction = JunctionHelper.IsJunction(r.Path)
            }).ToList();

        JavaList.ItemsSource = items;
        TxtEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int ready = items.Count(i => i.Entry.Status == "已就绪");
        TxtSummary.Text = $"共 {items.Count} 个 Java({ready} 个就绪)";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void GoDownload_Click(object sender, RoutedEventArgs e) => _nav.RequestNavigate("Download");

    // ==================== 新增本地 Java ====================

    private void AddLocalJava_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "选择 java.exe",
            Filter = "java.exe|java.exe|可执行文件 (*.exe)|*.exe",
            FileName = "java.exe"
        };
        if (ofd.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            string javaExe = ofd.FileName;
            if (!File.Exists(javaExe))
            {
                FufuMessage.Error(Window.GetWindow(this), "添加失败", "所选文件不存在。");
                return;
            }

            // java.exe 位于 {JAVA_HOME}\bin\java.exe,反推 JAVA_HOME
            string binDir = Path.GetDirectoryName(javaExe) ?? "";
            string javaHome = Path.GetDirectoryName(binDir) ?? "";
            if (string.IsNullOrEmpty(javaHome) ||
                !Path.GetFileName(binDir).Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                FufuMessage.Error(Window.GetWindow(this), "添加失败",
                    "目录结构不符合 Java 规范(应为 JAVA_HOME\\bin\\java.exe)。");
                return;
            }

            // 已在 runtimes 内则无需添加
            if (javaHome.StartsWith(JavaRuntimeService.RuntimesDir, StringComparison.OrdinalIgnoreCase))
            {
                FufuMessage.Info(Window.GetWindow(this), "提示", "该 Java 已位于 runtimes 目录内,无需重复添加。");
                RefreshList();
                return;
            }

            if (!JavaRuntimeService.VerifyJavaIntegrity(javaExe))
            {
                FufuMessage.Error(Window.GetWindow(this), "添加失败",
                    "该 Java 无法正常运行(java -version 校验未通过),请换一个可用的 Java。");
                return;
            }

            // 以目录联接挂入 runtimes:local-{目录名}(不复制文件,不占额外空间)
            string baseName = "local-" + SanitizeName(Path.GetFileName(javaHome.TrimEnd('\\', '/')));
            string linkDir = Path.Combine(JavaRuntimeService.RuntimesDir, baseName);
            int n = 2;
            while (Directory.Exists(linkDir))
                linkDir = Path.Combine(JavaRuntimeService.RuntimesDir, $"{baseName}-{n++}");

            if (!JunctionHelper.CreateJunction(linkDir, javaHome))
            {
                FufuMessage.Error(Window.GetWindow(this), "添加失败",
                    "创建目录联接失败(需要与 runtimes 同一磁盘分区)。");
                return;
            }

            App.WriteAppLog($"[Java] 新增本地 Java:{linkDir} → {javaHome}");
            FufuMessage.Success(Window.GetWindow(this), "添加成功",
                $"本地 Java 已挂入 runtimes 目录:\n{linkDir}\n\n可点击「设为默认」让启动游戏直接使用。");
            RefreshList();
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[Java] 新增本地 Java 异常:{ex}");
            FufuMessage.Error(Window.GetWindow(this), "添加失败", ex.Message);
        }
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? "java" : s;
    }

    // ==================== 设为默认 ====================

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not JavaCardItem item) return;
        _configService.Config.JavaPath = item.Entry.JavaExe;
        _configService.Save();
        App.WriteAppLog($"[Java] 设为默认:{item.Entry.JavaExe}");
        FufuMessage.Success(Window.GetWindow(this), "设为默认",
            $"已将该 Java 设为全局默认:\n{item.Entry.JavaExe}\n\n启动游戏将直接使用它。");
        RefreshList();
    }

    // ==================== 删除 ====================

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not JavaCardItem item) return;
        var owner = Window.GetWindow(this);

        bool ok;
        if (item.IsJunction)
        {
            if (!FufuMessage.Confirm(owner, "移除 Java 链接",
                    $"「{item.DisplayName}」是指向外部 Java 的链接,\n移除后原 Java 文件不会被删除。\n\n确定移除?",
                    okText: "移除", danger: true))
                return;
            ok = JunctionHelper.DeleteJunctionOnly(item.Entry.Path);
        }
        else
        {
            if (!FufuMessage.Confirm(owner, "删除 Java",
                    $"确定彻底删除「{item.DisplayName}」?\n该目录下的 Java 文件将被删除:\n{item.Entry.Path}",
                    okText: "删除", danger: true))
                return;
            ok = JavaRuntimeService.RemoveRuntimeDir(item.Entry.Path);
        }

        if (!ok)
        {
            FufuMessage.Error(owner, "删除失败", "删除失败,请确认没有程序正在使用该 Java 后重试。");
            return;
        }

        // 被删除的若是当前全局默认,清空全局选择
        if (string.Equals(_configService.Config.JavaPath, item.Entry.JavaExe, StringComparison.OrdinalIgnoreCase))
        {
            _configService.Config.JavaPath = "";
            _configService.Save();
        }
        App.WriteAppLog($"[Java] 已删除:{item.Entry.Path}");
        RefreshList();
    }
}

