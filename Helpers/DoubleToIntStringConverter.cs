using System;
using Microsoft.UI.Xaml.Data;

namespace quantum_drive.Helpers;

public sealed class DoubleToIntStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double d)
            return ((int)d).ToString();
        return "0";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
