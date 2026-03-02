using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace quantum_drive.Helpers;

public sealed class BoolToLockColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool unlocked)
            return unlocked
                ? new SolidColorBrush(Microsoft.UI.Colors.Green)
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
