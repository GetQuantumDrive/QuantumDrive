using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace quantum_drive.Helpers;

public sealed class BoolToMatchColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool matches)
            return matches
                ? new SolidColorBrush(Microsoft.UI.Colors.Green)
                : new SolidColorBrush(Microsoft.UI.Colors.Red);
        return new SolidColorBrush(Microsoft.UI.Colors.Red);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
