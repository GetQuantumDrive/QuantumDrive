using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using quantum_drive.Services;
using quantum_drive.Views;

namespace quantum_drive.ViewModels;

public partial class LockScreenViewModel : ObservableObject
{
    private readonly IIdentityService _identityService;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isUnlocking;

    [ObservableProperty]
    private bool _shouldShake;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // Recovery mode fields
    [ObservableProperty]
    private bool _isRecoveryMode;

    [ObservableProperty]
    private string _recoveryKey = string.Empty;

    [ObservableProperty]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    private string _confirmNewPassword = string.Empty;

    [ObservableProperty]
    private bool _isRecovering;

    public bool IsRecoveryPasswordValid =>
        !string.IsNullOrEmpty(RecoveryKey)
        && !string.IsNullOrEmpty(NewPassword)
        && NewPassword.Length >= 8
        && NewPassword == ConfirmNewPassword;

    public LockScreenViewModel(IIdentityService identityService, INavigationService navigationService)
    {
        _identityService = identityService;
        _navigationService = navigationService;
    }

    partial void OnRecoveryKeyChanged(string value) => OnPropertyChanged(nameof(IsRecoveryPasswordValid));
    partial void OnNewPasswordChanged(string value) => OnPropertyChanged(nameof(IsRecoveryPasswordValid));
    partial void OnConfirmNewPasswordChanged(string value) => OnPropertyChanged(nameof(IsRecoveryPasswordValid));

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (string.IsNullOrEmpty(Password) || IsUnlocking)
            return;

        IsUnlocking = true;
        ErrorMessage = string.Empty;
        ShouldShake = false;

        try
        {
            bool success = await _identityService.UnlockAsync(Password);

            if (success)
            {
                Debug.WriteLine("Unlock successful — navigating to Dashboard.");
                _navigationService.NavigateTo<DashboardPage>();
            }
            else
            {
                Debug.WriteLine("Unlock failed — triggering shake.");
                ErrorMessage = "Invalid password";
                ShouldShake = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unlock error: {ex.Message}");
            ErrorMessage = "An error occurred";
            ShouldShake = true;
        }
        finally
        {
            IsUnlocking = false;
            Password = string.Empty;
        }
    }

    [RelayCommand]
    private void EnterRecoveryMode()
    {
        IsRecoveryMode = true;
        ErrorMessage = string.Empty;
        ShouldShake = false;
    }

    [RelayCommand]
    private void ExitRecoveryMode()
    {
        IsRecoveryMode = false;
        RecoveryKey = string.Empty;
        NewPassword = string.Empty;
        ConfirmNewPassword = string.Empty;
        ErrorMessage = string.Empty;
        ShouldShake = false;
    }

    [RelayCommand]
    private async Task RecoverAsync()
    {
        if (!IsRecoveryPasswordValid || IsRecovering) return;

        IsRecovering = true;
        ErrorMessage = string.Empty;
        ShouldShake = false;

        try
        {
            bool success = await _identityService.RecoverWithKeyAsync(RecoveryKey, NewPassword);

            if (success)
            {
                Debug.WriteLine("Recovery successful — navigating to Dashboard.");
                _navigationService.NavigateTo<DashboardPage>();
            }
            else
            {
                Debug.WriteLine("Recovery failed — invalid recovery key.");
                ErrorMessage = "Invalid recovery key";
                ShouldShake = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Recovery error: {ex.Message}");
            ErrorMessage = "Recovery failed";
            ShouldShake = true;
        }
        finally
        {
            IsRecovering = false;
        }
    }
}
