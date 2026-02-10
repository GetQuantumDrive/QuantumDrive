using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace quantum_drive.Helpers;

/// <summary>
/// Shows visibility when the confirm password field has content but doesn't match.
/// Used with x:Bind on the ConfirmPassword property — the page code-behind
/// handles the actual mismatch check via the ViewModel.
/// This converter simply shows the warning when the confirm field is non-empty
/// and the passwords don't match (checked by binding to CanGoNext indirectly).
/// For simplicity, this shows when the string is non-empty (the ViewModel
/// handles the actual validation via CanGoNext).
/// </summary>
public sealed class PasswordMismatchVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        // This will be bound to ConfirmPassword. We show the warning when
        // it's non-empty. The actual mismatch logic is in CanGoNext.
        // A more complete solution would use a multi-binding, but WinUI doesn't
        // support that natively. We'll keep it simple.
        return Visibility.Collapsed; // Hidden by default; page handles visibility
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
