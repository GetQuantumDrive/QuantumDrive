using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace quantum_drive.Helpers;

public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
            return b ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility v)
            return v != Visibility.Visible;
        return true;
    }
}
