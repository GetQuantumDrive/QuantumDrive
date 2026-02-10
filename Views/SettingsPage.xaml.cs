using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using quantum_drive.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace quantum_drive.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    private void OldPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.OldPassword = OldPasswordBox.Password;
    }

    private void NewPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.NewPassword = NewPasswordBox.Password;
        UpdateNewPasswordEntropyBarColor();
    }

    private void ConfirmNewPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmNewPassword = ConfirmNewPasswordBox.Password;
    }

    private void ExportPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.ExportPassword = ExportPasswordBox.Password;
    }

    private void UpdateNewPasswordEntropyBarColor()
    {
        var bits = ViewModel.NewPasswordEntropyBits;
        if (bits < 50)
            NewPasswordEntropyBar.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
        else if (bits < 80)
            NewPasswordEntropyBar.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
        else
            NewPasswordEntropyBar.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
    }

    private async void OnExportRecoveryKitClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.VerifyExportPasswordCommand.ExecuteAsync(null);
    }

    private async void OnSaveExportedRecoveryKit(object sender, RoutedEventArgs e)
    {
        var savePicker = new FileSavePicker();

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
            ViewModel.IsNotificationOpen = true;
        }
    }
}
