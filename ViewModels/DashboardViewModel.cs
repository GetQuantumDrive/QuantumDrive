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
    private readonly IVaultRegistry _vaultRegistry;
    private readonly INavigationService _navigationService;
    private readonly IVirtualDriveService _virtualDriveService;

    [ObservableProperty]
    private bool _isDriveMounted;

    [ObservableProperty]
    private bool _isDriveMounting;

    [ObservableProperty]
    private string _driveLabel = "Virtual Drive";

    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private double _notificationOpacity;

    [ObservableProperty]
    private Microsoft.UI.Xaml.Media.Brush _notificationForeground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);

    public ObservableCollection<VaultStatusItem> VaultList { get; } = new();

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    public DashboardViewModel(
        IVaultRegistry vaultRegistry,
        INavigationService navigationService,
        IVirtualDriveService virtualDriveService)
    {
        _vaultRegistry = vaultRegistry;
        _navigationService = navigationService;
        _virtualDriveService = virtualDriveService;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _virtualDriveService.FilesChanged += OnDriveFilesChanged;
    }

    private void OnDriveFilesChanged()
    {
        _dispatcherQueue.TryEnqueue(RefreshStats);
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
                var letter = await _virtualDriveService.MountAsync();
                DriveLabel = $"Virtual Drive {letter}: (Encrypted)";
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
            ShowNotification($"Failed to {(mount ? "mount" : "unmount")} drive. {ex.Message}", isError: true);

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

    public void SyncDriveState()
    {
        bool mounted = _virtualDriveService.MountedDriveLetter is not null;
#pragma warning disable MVVMTK0034
        _isDriveMounted = mounted;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsDriveMounted));

        DriveLabel = mounted
            ? $"Virtual Drive {_virtualDriveService.MountedDriveLetter}: (Encrypted)"
            : "Virtual Drive";
    }

    public void TryAutoMount()
    {
        // Check service state first — if already mounted (e.g. navigated back from setup wizard),
        // just sync UI state instead of trying to mount again.
        if (_virtualDriveService.MountedDriveLetter is not null)
        {
            SyncDriveState();
            return;
        }

        if (ApplicationData.Current.LocalSettings.Values["AutoMountOnUnlock"] is true
            && !IsDriveMounted && !IsDriveMounting)
        {
            IsDriveMounted = true;
        }
    }

    public void RefreshStats()
    {
        VaultList.Clear();

        foreach (var vault in _vaultRegistry.Vaults)
        {
            var context = _vaultRegistry.GetContext(vault.Id);
            int fileCount = 0;
            long vaultSize = 0;

            if (Directory.Exists(vault.FolderPath))
            {
                try
                {
                    var qdFiles = Directory.GetFiles(vault.FolderPath, "*.qd");
                    fileCount = qdFiles.Length;
                    foreach (var f in qdFiles)
                        vaultSize += new FileInfo(f).Length;
                }
                catch { /* skip inaccessible */ }
            }

            VaultList.Add(new VaultStatusItem
            {
                Id = vault.Id,
                Name = vault.Name,
                IsUnlocked = context?.IsUnlocked ?? false,
                FileCount = fileCount,
                SizeLabel = FormatBytes(vaultSize),
                FolderPath = vault.FolderPath
            });
        }
    }

    public async Task AddVaultAsync(VaultDescriptor descriptor)
    {
        RefreshStats();

        if (_virtualDriveService.MountedDriveLetter is not null)
        {
            try
            {
                await _virtualDriveService.RefreshVaultsAsync();
                SyncDriveState();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh vaults after add: {ex.Message}");
            }
        }
    }

    public async Task RemoveVaultAsync(string vaultId)
    {
        await _vaultRegistry.RemoveVaultAsync(vaultId);
        RefreshStats();

        if (_virtualDriveService.MountedDriveLetter is not null)
        {
            try
            {
                await _virtualDriveService.RefreshVaultsAsync();
                SyncDriveState();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh vaults after remove: {ex.Message}");
            }
        }

        // If no vaults left, go to setup
        if (!_vaultRegistry.HasAnyVault)
        {
            _navigationService.NavigateTo<SetupWizardPage>();
        }
    }

    public async Task LockVaultAsync(string vaultId)
    {
        _vaultRegistry.LockVault(vaultId);
        RefreshStats();

        if (_virtualDriveService.MountedDriveLetter is not null)
        {
            await _virtualDriveService.RefreshVaultsAsync();
            SyncDriveState();
        }

        ShowNotification("Vault locked.");
    }

    public async Task UnlockVaultAsync(string vaultId, string password)
    {
        bool success = await _vaultRegistry.UnlockVaultAsync(vaultId, password);
        if (success)
        {
            RefreshStats();
            if (_virtualDriveService.MountedDriveLetter is not null)
            {
                await _virtualDriveService.RefreshVaultsAsync();
                SyncDriveState();
            }
            ShowNotification("Vault unlocked.");
        }
        else
        {
            ShowNotification("Invalid password.", isError: true);
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

    [RelayCommand]
    private void OpenDonatePage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://payment-links.mollie.com/payment/eFm5y2gJzzpzjC2VtpNsX",
            UseShellExecute = true
        });
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
