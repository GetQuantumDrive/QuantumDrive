using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using quantum_drive.Models;
using quantum_drive.Services;
using quantum_drive.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

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
        ViewModel.RefreshVaultPath();
        ViewModel.TryAutoMount();
    }

    private void OpenProviderStorage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CloudProviderItem provider })
        {
            ViewModel.OpenProviderStorage(provider);
        }
    }

    private async void ProviderList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CloudProviderItem provider)
        {
            if (provider.IsLocked)
            {
                var dialog = new ContentDialog
                {
                    Title = "Upgrade to QuantumDrive Pro",
                    PrimaryButtonText = "Learn More",
                    SecondaryButtonText = "Not Now",
                    XamlRoot = XamlRoot,
                    Content = CreateUpgradeContent()
                };

                await dialog.ShowAsync();
            }
            else if (provider.Name == "Local Storage")
            {
                await ShowLocalStorageConfigDialogAsync();
            }
            else
            {
                var dialog = new ContentDialog
                {
                    Title = provider.Name,
                    PrimaryButtonText = "OK",
                    XamlRoot = XamlRoot,
                    Content = new TextBlock
                    {
                        Text = $"{provider.Name} integration coming soon!",
                        TextWrapping = TextWrapping.Wrap
                    }
                };

                await dialog.ShowAsync();
            }
        }
    }

    private async Task ShowLocalStorageConfigDialogAsync()
    {
        var provider = App.Services.GetRequiredService<ICloudStorageService>() as LocalStorageProvider;
        string currentPath = provider?.GetVaultPath() ?? "Default";

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Current vault folder:",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = currentPath,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        });

        var dialog = new ContentDialog
        {
            Title = "Local Storage Configuration",
            PrimaryButtonText = "Change Folder",
            SecondaryButtonText = "Reset to Default",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            Content = panel
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            await PickAndSetVaultFolderAsync();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await ResetVaultFolderToDefaultAsync();
        }
    }

    private async Task PickAndSetVaultFolderAsync()
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

        // Ask about migration
        var migrateDialog = new ContentDialog
        {
            Title = "Migrate Files?",
            Content = "Would you like to copy your existing encrypted files to the new location?",
            PrimaryButtonText = "Yes, migrate",
            SecondaryButtonText = "No, start fresh",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };

        var migrateResult = await migrateDialog.ShowAsync();
        if (migrateResult == ContentDialogResult.None) return;

        bool migrate = migrateResult == ContentDialogResult.Primary;

        try
        {
            var provider = App.Services.GetRequiredService<ICloudStorageService>() as LocalStorageProvider;
            if (provider is not null)
            {
                await provider.SetCustomVaultFolderAsync(folder, migrate);
                ViewModel.RefreshVaultPath();
                ViewModel.ShowNotification("Vault folder updated.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set vault folder: {ex.Message}");
            ViewModel.ShowNotification("Failed to change vault folder.", InfoBarSeverity.Error);
        }
    }

    private async Task ResetVaultFolderToDefaultAsync()
    {
        var confirmDialog = new ContentDialog
        {
            Title = "Reset to Default",
            Content = "Move encrypted files back to the default location?",
            PrimaryButtonText = "Yes, migrate",
            SecondaryButtonText = "No, just reset",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };

        var result = await confirmDialog.ShowAsync();
        if (result == ContentDialogResult.None) return;

        bool migrate = result == ContentDialogResult.Primary;

        try
        {
            var provider = App.Services.GetRequiredService<ICloudStorageService>() as LocalStorageProvider;
            if (provider is not null)
            {
                await provider.ResetToDefaultFolderAsync(migrate);
                ViewModel.RefreshVaultPath();
                ViewModel.ShowNotification("Vault folder reset to default.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to reset vault folder: {ex.Message}");
            ViewModel.ShowNotification("Failed to reset vault folder.", InfoBarSeverity.Error);
        }
    }

    private static StackPanel CreateUpgradeContent()
    {
        var panel = new StackPanel { Spacing = 16 };

        panel.Children.Add(new TextBlock
        {
            Text = "Go beyond local storage with cloud sync and unlimited encrypted files.",
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Pro Features:",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        var features = new StackPanel { Spacing = 8 };
        features.Children.Add(new TextBlock { Text = "\u2022 Unlimited encrypted files" });
        features.Children.Add(new TextBlock { Text = "\u2022 Google Drive, OneDrive & Dropbox sync" });
        features.Children.Add(new TextBlock { Text = "\u2022 Priority support" });
        panel.Children.Add(features);

        return panel;
    }
}
