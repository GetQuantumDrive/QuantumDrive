using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using quantum_drive.Helpers;
using quantum_drive.Models;
using quantum_drive.Services;
using quantum_drive.Views;
using Windows.Storage;

namespace quantum_drive.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const string AutoMountKey = "AutoMountOnUnlock";

    private readonly IVaultRegistry _vaultRegistry;
    private readonly INavigationService _navigationService;
    private readonly IVirtualDriveService _virtualDriveService;

    // Vault selector
    public ObservableCollection<VaultDescriptor> VaultList { get; } = new();

    [ObservableProperty]
    private VaultDescriptor? _selectedVault;

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

    // Notification
    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private double _notificationOpacity;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Media.Brush _notificationForeground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);

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
        SelectedVault is not null
        && !string.IsNullOrEmpty(OldPassword)
        && !string.IsNullOrEmpty(NewPassword)
        && !string.IsNullOrEmpty(ConfirmNewPassword)
        && NewPassword == ConfirmNewPassword
        && NewPassword != OldPassword;

    // Delete vault
    [ObservableProperty]
    private string _deletePassword = string.Empty;

    [ObservableProperty]
    private bool _isDeleteConfirmed;

    [ObservableProperty]
    private bool _isDeletingVault;

    public bool IsDeleteVaultValid =>
        SelectedVault is not null
        && !string.IsNullOrEmpty(DeletePassword)
        && IsDeleteConfirmed;

    public SettingsViewModel(
        IVaultRegistry vaultRegistry,
        INavigationService navigationService,
        IVirtualDriveService virtualDriveService)
    {
        _vaultRegistry = vaultRegistry;
        _navigationService = navigationService;
        _virtualDriveService = virtualDriveService;

        foreach (var v in _vaultRegistry.Vaults)
            VaultList.Add(v);

        if (VaultList.Count > 0)
            SelectedVault = VaultList[0];
    }

    partial void OnSelectedVaultChanged(VaultDescriptor? value)
    {
        OnPropertyChanged(nameof(IsChangePasswordValid));
        OnPropertyChanged(nameof(IsDeleteVaultValid));
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

    partial void OnDeletePasswordChanged(string value)
    {
        OnPropertyChanged(nameof(IsDeleteVaultValid));
    }

    partial void OnIsDeleteConfirmedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDeleteVaultValid));
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        if (!IsChangePasswordValid || IsChangingPassword || SelectedVault is null) return;

        IsChangingPassword = true;

        try
        {
            var context = _vaultRegistry.GetContext(SelectedVault.Id);
            if (context is null)
            {
                ShowNotification("Vault not found.", isError: true);
                return;
            }

            bool verified = await context.Identity.VerifyPasswordAsync(OldPassword);
            if (!verified)
            {
                ShowNotification("Current password is incorrect.", isError: true);
                return;
            }

            await context.Identity.ChangePasswordAsync(OldPassword, NewPassword);
            ShowNotification("Password changed successfully.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Password change failed: {ex.Message}");
            ShowNotification("Failed to change password.", isError: true);
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
    private async Task VerifyExportPasswordAsync()
    {
        if (string.IsNullOrEmpty(ExportPassword) || SelectedVault is null) return;

        IsVerifyingExportPassword = true;

        try
        {
            var context = _vaultRegistry.GetContext(SelectedVault.Id);
            if (context is null)
            {
                ShowNotification("Vault not found.", isError: true);
                return;
            }

            bool verified = await context.Identity.VerifyPasswordAsync(ExportPassword);
            if (!verified)
            {
                ShowNotification("Incorrect password.", isError: true);
                return;
            }

            var publicKey = context.Identity.MlKemPublicKey;
            string fingerprint = "N/A";
            if (publicKey is not null)
            {
                string hex = Convert.ToHexString(publicKey.Take(16).ToArray());
                fingerprint = string.Join("-", Enumerable.Range(0, hex.Length / 4)
                    .Select(i => hex.Substring(i * 4, Math.Min(4, hex.Length - i * 4))));
            }

            string salt = context.Identity.VaultSaltBase64 ?? "N/A";
            string hint = context.Identity.PasswordHint ?? string.Empty;
            string? recoveryKey = await context.Identity.GetRecoveryKeyAsync(ExportPassword);
            RecoveryKitText = SetupWizardViewModel.BuildRecoveryKitText(fingerprint, salt, hint, recoveryKey);
            ExportPasswordVerified = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Export password verification failed: {ex.Message}");
            ShowNotification("Verification failed.", isError: true);
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
        _vaultRegistry.LockAll();
        _navigationService.NavigateTo<DashboardPage>();
    }

    [RelayCommand]
    private async Task DeleteVaultAsync()
    {
        if (!IsDeleteVaultValid || IsDeletingVault || SelectedVault is null) return;

        IsDeletingVault = true;

        try
        {
            var context = _vaultRegistry.GetContext(SelectedVault.Id);
            if (context is null)
            {
                ShowNotification("Vault not found.", isError: true);
                return;
            }

            bool verified = await context.Identity.VerifyPasswordAsync(DeletePassword);
            if (!verified)
            {
                ShowNotification("Incorrect password.", isError: true);
                return;
            }

            // Unmount drive if mounted
            if (_virtualDriveService.MountedDriveLetter is not null)
                await _virtualDriveService.UnmountAsync();

            // Delete encrypted vault files
            string vaultDir = SelectedVault.FolderPath;
            if (Directory.Exists(vaultDir))
                Directory.Delete(vaultDir, true);

            // Delete vault identity and remove from registry
            await context.Identity.DeleteVaultAsync();
            await _vaultRegistry.RemoveVaultAsync(SelectedVault.Id);

            if (_vaultRegistry.HasAnyVault)
            {
                _navigationService.NavigateTo<DashboardPage>();
            }
            else
            {
                _navigationService.NavigateTo<SetupWizardPage>();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Vault deletion failed: {ex.Message}");
            ShowNotification("Failed to delete vault.", isError: true);
        }
        finally
        {
            IsDeletingVault = false;
            DeletePassword = string.Empty;
            IsDeleteConfirmed = false;
        }
    }

    private CancellationTokenSource? _notificationCts;

    public void ShowNotification(string message, bool isError = false)
    {
        _notificationCts?.Cancel();
        NotificationMessage = message;
        NotificationForeground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            isError ? Microsoft.UI.Colors.IndianRed : Microsoft.UI.ColorHelper.FromArgb(255, 160, 160, 170));
        NotificationOpacity = 1;

        _notificationCts = new CancellationTokenSource();
        _ = AutoDismissAsync(_notificationCts.Token, isError ? 5000 : 2500);
    }

    private async Task AutoDismissAsync(CancellationToken token, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs, token);
            NotificationOpacity = 0;
        }
        catch (OperationCanceledException) { }
    }
}
