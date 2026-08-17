// EnvironmentCheckDialog.xaml.cs — 环境自检弹窗
// 可爱的芙芙
//
// 展示所有检测项,失败项可一键跳转官方下载页

using System.Windows;
using System.Windows.Input;
using FufuLauncher.Services;

namespace FufuLauncher.Views;

public partial class EnvironmentCheckDialog : Window
{
    private readonly EnvironmentCheckService _envCheck;

    public EnvironmentCheckDialog(EnvironmentCheckService envCheck)
    {
        InitializeComponent();
        _envCheck = envCheck;
        BindItems();
    }

    private void BindItems()
    {
        ItemsList.ItemsSource = _envCheck.LastResult.Items;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var result = _envCheck.LastResult;
        int total = result.Items.Count;
        int passed = result.Items.FindAll(i => i.Ok).Count;
        int failed = total - passed;
        int critical = result.Items.FindAll(i => !i.Ok && i.Severity == SeverityLevel.Critical).Count;
        int warning = failed - critical;

        if (failed == 0)
        {
            SummaryText.Text = $"全部通过 ({total}/{total})";
            SummaryText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x66, 0xBB, 0x66));
        }
        else if (critical > 0)
        {
            SummaryText.Text = $"致命 {critical} 项" + (warning > 0 ? $"、警告 {warning} 项" : "") + $" ({passed}/{total} 通过)";
            SummaryText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55));
        }
        else
        {
            SummaryText.Text = $"警告 {warning} 项需要关注 ({passed}/{total} 通过)";
            SummaryText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0x47));
        }
    }

    private void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string url && !string.IsNullOrEmpty(url))
        {
            EnvironmentCheckService.OpenDownloadUrl(url);
        }
    }

    private async void BtnRescan_Click(object sender, RoutedEventArgs e)
    {
        var btn = (System.Windows.Controls.Button)sender;
        btn.Content = "检测中...";
        btn.IsEnabled = false;
        try
        {
            await _envCheck.RunEnvironmentCheckAsync();
            BindItems();
        }
        finally
        {
            btn.Content = "重新检测";
            btn.IsEnabled = true;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }
}
