using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows;
using AllNetworkManager.Core.Devices;

namespace AllNetworkManager.App.ViewModels;

public class ManufacturerToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string manufacturer)
        {
            return manufacturer.ToLowerInvariant() switch
            {
                var m when m.Contains("bosch") => "📷",
                var m when m.Contains("mikrotik") || m.Contains("router") => "🌐",
                var m when m.Contains("switch") => "🔀",
                var m when m.Contains("wisenet") || m.Contains("hanwha") => "📹",
                var m when m.Contains("axis") => "📷",
                var m when m.Contains("microsens") => "🔀",
                _ => "📦"
            };
        }
        return "📦";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DeviceStatus status)
        {
            return status switch
            {
                DeviceStatus.Online => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69)),
                DeviceStatus.Offline => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(108, 117, 125)),
                DeviceStatus.Unreachable => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 53, 69)),
                DeviceStatus.AuthRequired => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7)),
                DeviceStatus.Updating => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212)),
                _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(108, 117, 125))
            };
        }
        return System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        return false;
    }
}