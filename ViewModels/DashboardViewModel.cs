using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using quantum_drive.Models;
using quantum_drive.Services;
using quantum_drive.Views;
using Windows.Storage;

namespace quantum_drive.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly ILicenseService _licenseService;
    private readonly ICloudStorageService _cloudStorageService;
    private readonly INavigationService _navigationService;
    private readonly IVirtualDriveService _virtualDriveService;
    private bool _limitNotificationShown;

    [ObservableProperty]
    private bool _isDriveMounted;

    [ObservableProperty]
    private bool _isDriveMounting;

    [ObservableProperty]
    private string _driveLabel = "Virtual Drive Q:";

    [ObservableProperty]
    private string _vaultSizeLabel = "—";

    [ObservableProperty]
    private string _vaultFileCountLabel = "";

    [ObservableProperty]
    private string _diskFreeLabel = "—";

    [ObservableProperty]
    private string _diskFreeDetail = "";

    [ObservableProperty]
    private double _fileUsagePercent;

    [ObservableProperty]
    private bool _isFreeTier = true;

    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private bool _isNotificationOpen;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Controls.InfoBarSeverity _notificationSeverity;

    public ObservableCollection<CloudProviderItem> Providers { get; } = new()
    {
        new()
        {
            Name = "Local Storage",
            Icon = "\uE7B8",
            IsLocked = false,
            Tier = "Free",
            Description = "Encrypt files on this computer"
        },
        new()
        {
            Name = "Google Drive",
            Icon = "\uE753",
            IsLocked = true,
            Tier = "Pro",
            Description = "Sync with Google Drive"
        },
        new()
        {
            Name = "OneDrive",
            Icon = "\uE753",
            IsLocked = true,
            Tier = "Pro",
            Description = "Sync with Microsoft OneDrive"
        },
        new()
        {
            Name = "Dropbox",
            Icon = "\uE753",
            IsLocked = true,
            Tier = "Pro",
            Description = "Sync with Dropbox"
        }
    };

    public DashboardViewModel(
        ILicenseService licenseService,
        ICloudStorageService cloudStorageService,
        INavigationService navigationService,
        IVirtualDriveService virtualDriveService)
    {
        _licenseService = licenseService;
        _cloudStorageService = cloudStorageService;
        _navigationService = navigationService;
        _virtualDriveService = virtualDriveService;
    }

    async partial void OnIsDriveMountedChanged(bool value)
    {
        await ToggleDriveMountAsync(value);
    }

    private async Task ToggleDriveMountAsync(bool mount)
    {
        IsDriveMounting = true;
        try
        {
            if (mount)
            {
                var provider = _cloudStorageService as LocalStorageProvider;
                string path = provider?.GetVaultPath()
                    ?? Windows.Storage.ApplicationData.Current.LocalFolder.Path;

                var letter = await _virtualDriveService.MountAsync(path);
                var modeSuffix = _virtualDriveService.IsEncryptedMode ? "(Encrypted)" : "(Basic)";
                DriveLabel = $"Virtual Drive {letter}: {modeSuffix}";
            }
            else
            {
                await _virtualDriveService.UnmountAsync();
                DriveLabel = "Virtual Drive";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Drive mount/unmount failed: {ex.Message}");
            ShowNotification($"Failed to {(mount ? "mount" : "unmount")} drive. {ex.Message}",
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);

            // Reset toggle without re-triggering the partial method
#pragma warning disable MVVMTK0034
            _isDriveMounted = !mount;
#pragma warning restore MVVMTK0034
            OnPropertyChanged(nameof(IsDriveMounted));
        }
        finally
        {
            IsDriveMounting = false;
        }
    }

    public void TryAutoMount()
    {
        if (ApplicationData.Current.LocalSettings.Values["AutoMountOnUnlock"] is true
            && !IsDriveMounted && !IsDriveMounting)
        {
            IsDriveMounted = true;
        }
    }

    public void RefreshVaultPath()
    {
        RefreshStats();
    }

    public void RefreshStats()
    {
        try
        {
            IsFreeTier = !_licenseService.IsPro;
            var fileLimit = _licenseService.FileLimit;

            var provider = _cloudStorageService as LocalStorageProvider;
            var vaultDir = provider?.GetVaultPath();
            if (vaultDir is null || !Directory.Exists(vaultDir))
            {
                VaultSizeLabel = IsFreeTier ? "0 of 25" : "0 files";
                VaultFileCountLabel = "";
                FileUsagePercent = 0;
                DiskFreeLabel = "—";
                DiskFreeDetail = "";
                return;
            }

            var qdFiles = Directory.GetFiles(vaultDir, "*.qd");
            int count = qdFiles.Length;
            long totalSize = 0;
            foreach (var f in qdFiles)
                totalSize += new FileInfo(f).Length;

            VaultSizeLabel = IsFreeTier ? $"{count} of {fileLimit}" : $"{count} files";
            VaultFileCountLabel = FormatBytes(totalSize) + " encrypted";
            FileUsagePercent = fileLimit == int.MaxValue ? 0 : (double)count / fileLimit * 100;

            if (IsFreeTier && count >= fileLimit - 2 && !_limitNotificationShown)
            {
                _limitNotificationShown = true;
                var remaining = fileLimit - count;
                var msg = remaining <= 0
                    ? "You've reached your file limit. Upgrade to Pro for unlimited files and cloud sync."
                    : $"You're almost at your file limit ({count}/{fileLimit}). Upgrade to Pro for unlimited files and cloud sync.";
                ShowNotification(msg, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational);
            }

            var driveRoot = Path.GetPathRoot(vaultDir);
            if (driveRoot is not null)
            {
                var driveInfo = new DriveInfo(driveRoot);
                DiskFreeLabel = FormatBytes(driveInfo.AvailableFreeSpace);
                DiskFreeDetail = $"of {FormatBytes(driveInfo.TotalSize)} total";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to refresh stats: {ex.Message}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024L => $"{bytes} B",
            < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
        };
    }

    public void OpenProviderStorage(CloudProviderItem provider)
    {
        if (provider.IsLocked) return;

        if (provider.Name == "Local Storage")
        {
            string? path = _virtualDriveService.MountedDriveLetter is { } letter
                ? $"{letter}:\\"
                : (_cloudStorageService as LocalStorageProvider)?.GetVaultPath();

            if (path is not null && Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = path,
                    UseShellExecute = true
                });
            }
        }
    }

    [RelayCommand]
    private void OpenVirtualDrive()
    {
        if (_virtualDriveService.MountedDriveLetter is not { } letter) return;

        var path = $"{letter}:\\";
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = path,
                UseShellExecute = true
            });
        }
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        _navigationService.NavigateTo<SettingsPage>();
    }

    public void ShowNotification(string message, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity)
    {
        NotificationMessage = message;
        NotificationSeverity = severity;
        IsNotificationOpen = true;
    }
}
