// Converters/BoolToVisConverter.cs — 布尔值转可见性
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FufuLauncher.Converters;

public class BoolToVisConverter : IValueConverter
{
    public static readonly BoolToVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}
