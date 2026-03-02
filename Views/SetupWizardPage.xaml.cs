using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using quantum_drive.Services;
using quantum_drive.ViewModels;
using Windows.Storage.Pickers;
using Windows.Storage;
using WinRT.Interop;

namespace quantum_drive.Views;

public sealed partial class SetupWizardPage : Page
{
    public SetupWizardViewModel ViewModel { get; }

    public SetupWizardPage()
    {
        ViewModel = App.Services.GetRequiredService<SetupWizardViewModel>();
        InitializeComponent();
        Loaded += SetupWizardPage_Loaded;
    }

    private void SetupWizardPage_Loaded(object sender, RoutedEventArgs e)
    {
        KeyFadeInAnimation.Begin();
        KeyPulseAnimation.Begin();
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.Password = PasswordBox.Password;
        UpdateEntropyBarColor();
    }

    private void UpdateEntropyBarColor()
    {
        var bits = ViewModel.EntropyBits;
        if (bits < 50)
            EntropyBar.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
        else if (bits < 80)
            EntropyBar.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
        else
            EntropyBar.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
    }

    private async void BrowseVaultFolder_Click(object sender, RoutedEventArgs e)
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
        if (folder is not null)
        {
            ViewModel.SetVaultFolder(folder);
        }
    }

    private async void ImportExistingVault_Click(object sender, RoutedEventArgs e)
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
            var errorDialog = new ContentDialog
            {
                Title = "No vault found",
                Content = "The selected folder does not contain a QuantumDrive vault.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await errorDialog.ShowAsync();
            return;
        }

        var nameBox = new TextBox { Header = "Vault Name", PlaceholderText = "e.g., Personal" };
        var passwordBox = new PasswordBox { Header = "Master Password", PlaceholderText = "Enter the vault password" };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "A QuantumDrive vault was found. Enter a name and your password to add it.",
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
            return;

        try
        {
            var registry = App.Services.GetRequiredService<IVaultRegistry>();
            await registry.RegisterExistingVaultAsync(name, folder.Path, password, folder);

            var nav = App.Services.GetRequiredService<INavigationService>();
            nav.NavigateTo<DashboardPage>();
        }
        catch (UnauthorizedAccessException)
        {
            var failDialog = new ContentDialog
            {
                Title = "Invalid password",
                Content = "The password you entered is incorrect for this vault.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await failDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to import vault: {ex.Message}");
        }
    }

    private async void OnSaveRecoveryKitClick(object sender, RoutedEventArgs e)
    {
        var savePicker = new FileSavePicker();

        // Get the window handle for the picker
        var window = App.CurrentWindow;
        if (window is not null)
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            InitializeWithWindow.Initialize(savePicker, hwnd);
        }

        savePicker.SuggestedFileName = $"QuantumDrive-Recovery-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        savePicker.FileTypeChoices.Add("Text File", new List<string> { ".txt" });

        var file = await savePicker.PickSaveFileAsync();
        if (file is not null)
        {
            await FileIO.WriteTextAsync(file, ViewModel.RecoveryKitText);
            RecoveryNotification.Title = "Recovery kit saved successfully!";
            RecoveryNotification.Severity = InfoBarSeverity.Success;
            RecoveryNotification.IsOpen = true;
        }
    }
}
