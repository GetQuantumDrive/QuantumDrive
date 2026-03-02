using System;
using Microsoft.UI.Xaml.Data;

namespace quantum_drive.Helpers;

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool flag)
        {
            return flag ? 1.0 : 0.5;
        }
        return 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
