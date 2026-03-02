using System;
using Microsoft.UI.Xaml.Data;

namespace quantum_drive.Helpers;

public sealed class BoolToLockGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool unlocked)
            return unlocked ? "\uE785" : "\uE72E"; // Unlock vs Lock
        return "\uE72E";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
