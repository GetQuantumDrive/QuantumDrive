using System;
using Microsoft.UI.Xaml.Data;

namespace quantum_drive.Helpers;

public sealed class BoolToMatchGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool matches)
            return matches ? "\uE73E" : "\uE7BA"; // Checkmark vs Warning
        return "\uE7BA";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
