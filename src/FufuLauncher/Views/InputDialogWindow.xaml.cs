// InputDialogWindow.xaml.cs — 通用输入对话框(独立窗口)
// 可爱的芙芙
// 视图层:仅负责文本输入与确认/取消交互;校验回调由调用方注入,不在此实现业务逻辑。
using System;
using System.Windows;
using System.Windows.Input;

namespace FufuLauncher.Views;

public partial class InputDialogWindow : Window
{
    private readonly bool _isPassword;
    private readonly Func<string?, bool>? _validate;

    public InputDialogWindow(string title, string prompt, string? defaultValue, bool isPassword,
        Func<string?, bool>? validate, string? watermark)
    {
        _isPassword = isPassword;
        _validate = validate;
        InitializeComponent();
        Title = title;
        TxtTitle.Text = title;
        TxtPrompt.Text = prompt;
        if (isPassword)
        {
            PwdInput.Visibility = Visibility.Visible;
            TxtInput.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrEmpty(defaultValue)) PwdInput.Password = defaultValue;
            if (!string.IsNullOrEmpty(watermark)) PwdInput.Tag = watermark;
        }
        else
        {
            TxtInput.Visibility = Visibility.Visible;
            PwdInput.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrEmpty(defaultValue)) TxtInput.Text = defaultValue;
            if (!string.IsNullOrEmpty(watermark)) TxtInput.ToolTip = watermark;
        }
    }

    public string? ResultText => _isPassword ? PwdInput.Password : TxtInput.Text;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isPassword) PwdInput.Focus(); else TxtInput.Focus();
    }

    private void TxtInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryConfirm();
        else if (e.Key == Key.Escape) DialogResult = false;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => TryConfirm();

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void TryConfirm()
    {
        var text = ResultText;
        if (_validate != null)
        {
            try
            {
                if (!_validate(text))
                {
                    TxtError.Text = "输入不合法,请检查后重试。";
                    return;
                }
            }
            catch (Exception ex)
            {
                TxtError.Text = $"校验失败:{ex.Message}";
                return;
            }
        }
        DialogResult = true;
    }
}
