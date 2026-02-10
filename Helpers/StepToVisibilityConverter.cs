using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace quantum_drive.Helpers;

public sealed class StepToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int currentStep && parameter is string stepStr && int.TryParse(stepStr, out int targetStep))
        {
            return currentStep == targetStep ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
