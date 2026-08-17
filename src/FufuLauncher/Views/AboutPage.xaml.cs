// AboutPage.xaml.cs — 关于页面(纯展示,无业务逻辑)
// 可爱的芙芙 - 侧边栏重构新增入口

using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FufuLauncher.Services;

namespace FufuLauncher.Views;

public partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
    }

    private void AboutPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = v != null ? $"版本 v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : "版本 v1.0";
            TxtCopyright.Text = $"© {DateTime.Now.Year} 可爱的芙芙 FufuLauncher · Minecraft Java 版第三方启动器";
        }
        catch
        {
            // 版本信息读取失败不影响页面展示
        }

        // 应用专属 Logo(tupian\tub.png):加载成功时清空占位底色,失败保留内置色块
        try
        {
            var logo = BrandAssets.GetLogo();
            if (logo != null)
            {
                AboutLogoImage.Source = logo;
                AboutLogoBox.Background = Brushes.Transparent;
            }
        }
        catch { /* Logo 加载异常不影响关于页展示 */ }
    }

    /// <summary>点击复制作者 QQ 号(剪贴板偶发占用失败时重试一次)</summary>
    private void TxtQQ_Copy(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            try { Clipboard.SetText("2703706747"); }
            catch { System.Threading.Thread.Sleep(150); Clipboard.SetText("2703706747"); }
            MessageBox.Show("已复制 QQ 号:2703706747", "可爱的芙芙",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { /* 剪贴板不可用时静默忽略 */ }
    }
}
