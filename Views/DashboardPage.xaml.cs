using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using quantum_drive.Models;
using quantum_drive.Services;
using quantum_drive.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

// ReSharper disable AsyncVoidMethod

namespace quantum_drive.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel ViewModel { get; }

    public DashboardPage()
    {
        ViewModel = App.Services.GetRequiredService<DashboardViewModel>();
        InitializeComponent();
        Loaded += DashboardPage_Loaded;
    }

    private void DashboardPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshStats();
        ViewModel.TryAutoMount();
    }

    private async void CreateNewVault_Click(object sender, RoutedEventArgs e)
    {
        var registry = App.Services.GetRequiredService<IVaultRegistry>();
        if (registry.IsAtVaultLimit)
        {
            await ShowVaultLimitDialogAsync();
            return;
        }

        var nav = App.Services.GetRequiredService<INavigationService>();
        nav.NavigateTo<SetupWizardPage>();
    }

    private async void AddExistingVault_Click(object sender, RoutedEventArgs e)
    {
        var folderPicker = new FolderPicker();
        folderPicker.FileTypeFilter.Add("*");

        var window = App.CurrentWindow;
        if (window is not null)
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            InitializeWithWindow.Initialize(folderPicker, hwnd);
        }

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder is null) return;

        bool isExisting = System.IO.File.Exists(
            System.IO.Path.Combine(folder.Path, ".quantum_vault", "vault.identity"));

        if (!isExisting)
        {
            ViewModel.ShowNotification("No QuantumDrive vault found in this folder.", isError: true);
            return;
        }

        var registry = App.Services.GetRequiredService<IVaultRegistry>();
        await AddExistingVaultDialogAsync(folder, registry);
    }

    private async Task AddExistingVaultDialogAsync(Windows.Storage.StorageFolder folder, IVaultRegistry registry)
    {
        var nameBox = new TextBox { Header = "Vault Name", PlaceholderText = "e.g., Personal" };
        var passwordBox = new PasswordBox { Header = "Master Password", PlaceholderText = "Enter the vault password" };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "An existing QuantumDrive vault was found in this folder.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(nameBox);
        panel.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            Title = "Add Existing Vault",
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = panel
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var name = nameBox.Text.Trim();
        var password = passwordBox.Password;

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(password))
        {
            ViewModel.ShowNotification("Name and password are required.", isError: true);
            return;
        }

        try
        {
            var descriptor = await registry.RegisterExistingVaultAsync(name, folder.Path, password);
            await ViewModel.AddVaultAsync(descriptor);
            ViewModel.ShowNotification($"Vault '{name}' added.");
        }
        catch (VaultLimitReachedException)
        {
            await ShowVaultLimitDialogAsync();
        }
        catch (UnauthorizedAccessException)
        {
            ViewModel.ShowNotification("Invalid password for this vault.", isError: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to add existing vault: {ex.Message}");
            ViewModel.ShowNotification("Failed to add vault.", isError: true);
        }
    }

    private async Task ShowVaultLimitDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Upgrade to Pro",
            Content = "The free tier allows 1 vault. Upgrade to Pro for unlimited vaults and all cloud providers.",
            PrimaryButtonText = "Get Pro",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://quantumdrive.app/pro",
                UseShellExecute = true
            });
        }
    }

    private void VaultCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not VaultStatusItem item) return;
        if (item.IsUnlocked) return;

        _ = ShowUnlockDialogAsync(item.Id);
    }

    private async void UnlockVault_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string vaultId) return;

        await ShowUnlockDialogAsync(vaultId);
    }

    private async Task ShowUnlockDialogAsync(string vaultId)
    {
        var registry = App.Services.GetRequiredService<IVaultRegistry>();
        var context = registry.GetContext(vaultId);

        var panel = new StackPanel { Spacing = 12 };

        // Show password hint if available
        if (context is not null)
        {
            string? hint = await context.Identity.GetPasswordHintAsync();
            if (!string.IsNullOrEmpty(hint))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"Hint: {hint}",
                    FontSize = 13,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        var passwordBox = new PasswordBox { PlaceholderText = "Enter master password" };
        panel.Children.Add(passwordBox);

        var forgotLink = new HyperlinkButton
        {
            Content = "Forgot password?",
            Padding = new Thickness(0)
        };
        panel.Children.Add(forgotLink);

        var dialog = new ContentDialog
        {
            Title = "Unlock Vault",
            PrimaryButtonText = "Unlock",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = panel
        };

        // If user clicks "Forgot password?", close this dialog and open recovery
        bool openRecovery = false;
        forgotLink.Click += (_, _) =>
        {
            openRecovery = true;
            dialog.Hide();
        };

        var result = await dialog.ShowAsync();

        if (openRecovery)
        {
            await ShowRecoveryDialogAsync(vaultId);
            return;
        }

        if (result != ContentDialogResult.Primary) return;

        await ViewModel.UnlockVaultAsync(vaultId, passwordBox.Password);
    }

    private async Task ShowRecoveryDialogAsync(string vaultId)
    {
        var recoveryKeyBox = new TextBox
        {
            Header = "Recovery Key",
            PlaceholderText = "ABCD-EFGH-IJKL-MNOP-...",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var newPasswordBox = new PasswordBox
        {
            Header = "New Password",
            PlaceholderText = "Enter a new password"
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Enter your recovery key and set a new master password.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        });
        panel.Children.Add(recoveryKeyBox);
        panel.Children.Add(newPasswordBox);

        var dialog = new ContentDialog
        {
            Title = "Account Recovery",
            PrimaryButtonText = "Reset Password",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = panel
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var recoveryKey = recoveryKeyBox.Text.Trim();
        var newPassword = newPasswordBox.Password;

        if (string.IsNullOrEmpty(recoveryKey) || string.IsNullOrEmpty(newPassword))
        {
            ViewModel.ShowNotification("Recovery key and new password are required.", isError: true);
            return;
        }

        var registry = App.Services.GetRequiredService<IVaultRegistry>();
        var context = registry.GetContext(vaultId);
        if (context is null)
        {
            ViewModel.ShowNotification("Vault not found.", isError: true);
            return;
        }

        try
        {
            bool success = await context.Identity.RecoverWithKeyAsync(recoveryKey, newPassword);
            if (success)
            {
                ViewModel.RefreshStats();
                ViewModel.ShowNotification("Password reset successfully. Vault is now unlocked.");
            }
            else
            {
                ViewModel.ShowNotification("Invalid recovery key.", isError: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Recovery failed: {ex.Message}");
            ViewModel.ShowNotification("Recovery failed.", isError: true);
        }
    }

    private void OpenVaultFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton btn || btn.Tag is not string folderPath) return;
        if (!System.IO.Directory.Exists(folderPath)) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = folderPath,
            UseShellExecute = true
        });
    }

    private async void DisconnectVault_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string vaultId) return;

        await ViewModel.LockVaultAsync(vaultId);
    }

    private void UpgradeToPro_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://quantumdrive.app/pro",
            UseShellExecute = true
        });
    }

    private async void RemoveVault_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string vaultId) return;

        var dialog = new ContentDialog
        {
            Title = "Remove Vault",
            Content = "Remove this vault from QuantumDrive? Files on disk will not be deleted.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        await ViewModel.RemoveVaultAsync(vaultId);
    }
}
