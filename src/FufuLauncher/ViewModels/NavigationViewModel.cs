// ViewModels/NavigationViewModel.cs — 侧边导航视图模型
// 可爱的芙芙 - 精简侧边栏导航
//
// 职责:
// 1. 维护全部导航项清单(页面项 + 动作项),单一集合供侧边栏绑定
// 2. 维护选中态(IsSelected)
// 3. 通过 NavigationRequested 事件向 MainWindow 发出跳转请求,
//    页面调度逻辑全部由 MainWindow 统一处理,VM 不直接触碰视图

using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace FufuLauncher.ViewModels;

/// <summary>单个导航项</summary>
public class NavItem : ViewModelBase
{
    public NavItem(string key, string icon, string label, bool isPage = true)
    {
        Key = key;
        Icon = icon;
        Label = label;
        IsPage = isPage;
    }

    /// <summary>路由键(页面项对应 Page 缓存键;动作项对应特殊处理)</summary>
    public string Key { get; }
    /// <summary>图标(emoji)</summary>
    public string Icon { get; }
    /// <summary>显示文字</summary>
    public string Label { get; }
    /// <summary>true=切换内容区页面;false=动作项(弹窗类,不改变内容区)</summary>
    public bool IsPage { get; }

    private bool _isSelected;
    /// <summary>是否高亮选中</summary>
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>侧边导航视图模型</summary>
public class NavigationViewModel : ViewModelBase
{
    /// <summary>主导航项(侧边栏上部)</summary>
    public ObservableCollection<NavItem> MainItems { get; }

    /// <summary>底部固定项(设置/关于)</summary>
    public ObservableCollection<NavItem> BottomItems { get; }

    /// <summary>当前内容区对应的页面键(动作项不改变此值)</summary>
    public string CurrentPageKey { get; private set; } = "Home";

    /// <summary>跳转请求:key=导航项 Key</summary>
    public event Action<string>? NavigationRequested;

    public NavigationViewModel()
    {
        MainItems = new ObservableCollection<NavItem>
        {
            new("Home",      "🏠", "主页"),
            new("Download",  "⬇",  "下载中心"),
            new("Mods",      "🧩", "模组管理"),
            new("Market",    "🛍", "在线市场"),
            new("Java",      "☕", "Java 管理"),
            new("Manage",    "🗑", "版本管理"),
        };
        BottomItems = new ObservableCollection<NavItem>
        {
            new("Logs",      "📜", "日志控制台", isPage: false),
            new("EnvCheck",  "🩺", "环境自检",   isPage: false),
            new("Settings",  "⚙",  "设置"),
            new("About",     "ℹ",  "关于"),
        };
        // 注:账号管理不再作为导航项,仅通过主页启动栏的账号入口弹窗唤起。
    }

    /// <summary>由导航按钮点击调用:发出跳转请求</summary>
    public void RequestNavigate(string key) => NavigationRequested?.Invoke(key);

    /// <summary>设置选中高亮;页面项同时更新 CurrentPageKey</summary>
    public void SetSelected(string key)
    {
        foreach (var item in MainItems.Concat(BottomItems))
            item.IsSelected = item.Key == key;

        var selected = MainItems.Concat(BottomItems).FirstOrDefault(i => i.Key == key);
        if (selected != null && selected.IsPage)
            CurrentPageKey = key;
    }

    /// <summary>动作项执行完毕后,恢复高亮到当前内容页</summary>
    public void RestorePageSelection() => SetSelected(CurrentPageKey);
}
