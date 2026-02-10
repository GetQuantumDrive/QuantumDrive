using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using quantum_drive.Helpers;
using quantum_drive.Services;
using quantum_drive.Views;
using Windows.Storage;

namespace quantum_drive.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const string AutoMountKey = "AutoMountOnUnlock";

    private readonly IIdentityService _identityService;
    private readonly ILicenseService _licenseService;
    private readonly INavigationService _navigationService;

    // Change Password fields
    [ObservableProperty]
    private string _oldPassword = string.Empty;

    [ObservableProperty]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    private string _confirmNewPassword = string.Empty;

    [ObservableProperty]
    private double _newPasswordEntropyBits;

    [ObservableProperty]
    private string _newPasswordStrengthLabel = string.Empty;

    [ObservableProperty]
    private bool _isChangingPassword;

    // License fields
    [ObservableProperty]
    private string _currentTier = string.Empty;

    [ObservableProperty]
    private string _licenseKey = string.Empty;

    // Notification
    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private bool _isNotificationOpen;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Controls.InfoBarSeverity _notificationSeverity;

    // Recovery export
    [ObservableProperty]
    private string _exportPassword = string.Empty;

    [ObservableProperty]
    private bool _isVerifyingExportPassword;

    // Auto-mount
    public bool IsAutoMountEnabled
    {
        get => ApplicationData.Current.LocalSettings.Values[AutoMountKey] is true;
        set
        {
            ApplicationData.Current.LocalSettings.Values[AutoMountKey] = value;
            OnPropertyChanged();
        }
    }

    public bool IsChangePasswordValid =>
        !string.IsNullOrEmpty(OldPassword)
        && NewPasswordEntropyBits >= 60
        && !string.IsNullOrEmpty(ConfirmNewPassword)
        && NewPassword == ConfirmNewPassword
        && NewPassword != OldPassword;

    public SettingsViewModel(
        IIdentityService identityService,
        ILicenseService licenseService,
        INavigationService navigationService)
    {
        _identityService = identityService;
        _licenseService = licenseService;
        _navigationService = navigationService;
        CurrentTier = $"Current Plan: {_licenseService.GetLicenseTier()}";
    }

    partial void OnNewPasswordChanged(string value)
    {
        NewPasswordEntropyBits = EntropyCalculator.CalculateBits(value);
        NewPasswordStrengthLabel = EntropyCalculator.GetStrengthLabel(NewPasswordEntropyBits);
        OnPropertyChanged(nameof(IsChangePasswordValid));
    }

    partial void OnOldPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(IsChangePasswordValid));
    }

    partial void OnConfirmNewPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(IsChangePasswordValid));
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        if (!IsChangePasswordValid || IsChangingPassword) return;

        IsChangingPassword = true;

        try
        {
            // Verify old password first
            bool verified = await _identityService.VerifyPasswordAsync(OldPassword);
            if (!verified)
            {
                ShowNotification("Current password is incorrect.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
                return;
            }

            await _identityService.ChangePasswordAsync(OldPassword, NewPassword);
            ShowNotification("Password changed successfully! Please log in again.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success);

            // Force logout
            _navigationService.NavigateTo<LockScreenPage>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Password change failed: {ex.Message}");
            ShowNotification("Failed to change password.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            IsChangingPassword = false;
            OldPassword = string.Empty;
            NewPassword = string.Empty;
            ConfirmNewPassword = string.Empty;
        }
    }

    [RelayCommand]
    private void ActivateLicense()
    {
        var key = LicenseKey.Trim();

        if (_licenseService.VerifyLicense(key))
        {
            CurrentTier = "Current Plan: Pro \u2713";
            ShowNotification("Pro license activated! Restart the app to unlock all features.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success);
        }
        else
        {
            ShowNotification("Invalid license key. Please check and try again.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task VerifyExportPasswordAsync()
    {
        if (string.IsNullOrEmpty(ExportPassword)) return;

        IsVerifyingExportPassword = true;

        try
        {
            bool verified = await _identityService.VerifyPasswordAsync(ExportPassword);
            if (!verified)
            {
                ShowNotification("Incorrect password.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
                return;
            }

            // Password verified — the View will handle showing the FileSavePicker
            // We generate the recovery kit text here
            var publicKey = _identityService.MlKemPublicKey;
            string fingerprint = "N/A";
            if (publicKey is not null)
            {
                string hex = Convert.ToHexString(publicKey.Take(16).ToArray());
                fingerprint = string.Join("-", Enumerable.Range(0, hex.Length / 4)
                    .Select(i => hex.Substring(i * 4, Math.Min(4, hex.Length - i * 4))));
            }

            string salt = _identityService.VaultSaltBase64 ?? "N/A";
            string hint = _identityService.PasswordHint ?? string.Empty;
            string? recoveryKey = await _identityService.GetRecoveryKeyAsync(ExportPassword);
            RecoveryKitText = SetupWizardViewModel.BuildRecoveryKitText(fingerprint, salt, hint, recoveryKey);
            ExportPasswordVerified = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Export password verification failed: {ex.Message}");
            ShowNotification("Verification failed.", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            IsVerifyingExportPassword = false;
            ExportPassword = string.Empty;
        }
    }

    [ObservableProperty]
    private string _recoveryKitText = string.Empty;

    [ObservableProperty]
    private bool _exportPasswordVerified;

    [RelayCommand]
    private void NavigateBack()
    {
        _navigationService.NavigateTo<DashboardPage>();
    }

    [RelayCommand]
    private void LogOut()
    {
        _navigationService.NavigateTo<LockScreenPage>();
    }

    private void ShowNotification(string message, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity)
    {
        NotificationMessage = message;
        NotificationSeverity = severity;
        IsNotificationOpen = true;
    }
}
