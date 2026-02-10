using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private void ConfirmPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmPassword = ConfirmPasswordBox.Password;
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
