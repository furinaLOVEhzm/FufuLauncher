// ViewModels/MainViewModel.cs — 主视图模型
using System;
using System.Reflection;
using FufuLauncher.Services;

namespace FufuLauncher.ViewModels;

public class MainViewModel : ViewModelBase
{
    private string _statusText = "就绪";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    /// <summary>启动器版本号(从程序集读取,避免硬编码)</summary>
    public string AppVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : "v1.0.5.1";
        }
    }

    public MainViewModel() { }
}
