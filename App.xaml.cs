using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using quantum_drive.Services;
using quantum_drive.ViewModels;
using quantum_drive.Views;

namespace quantum_drive;

public partial class App : Application
{
    private Window? _window;
    private static IServiceProvider? _services;

    public static Window? CurrentWindow { get; private set; }

    public static IServiceProvider Services => _services
        ?? throw new InvalidOperationException("Service provider not initialized.");

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = new ServiceCollection();

        // Services
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IPostQuantumCrypto, PostQuantumCrypto>();
        services.AddSingleton<IIdentityService, IdentityService>();
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<ICloudStorageService, LocalStorageProvider>();
        services.AddSingleton<ILicenseService, LicenseService>();
        services.AddSingleton<IVirtualDriveService, VirtualDriveService>();

        // ViewModels
        services.AddTransient<LockScreenViewModel>();
        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<SettingsViewModel>();

        _services = services.BuildServiceProvider();

        _window = new MainWindow();
        CurrentWindow = _window;
        _window.Closed += OnWindowClosed;
        _window.Activate();

        // Set up navigation
        var mainWindow = (MainWindow)_window;
        var navigationService = (NavigationService)_services.GetRequiredService<INavigationService>();
        navigationService.SetFrame(mainWindow.NavigationFrame);

        // Route to appropriate page
        var identityService = _services.GetRequiredService<IIdentityService>();
        if (identityService.IsVaultCreated)
        {
            navigationService.NavigateTo<LockScreenPage>();
        }
        else
        {
            navigationService.NavigateTo<SetupWizardPage>();
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            var driveService = _services?.GetService<IVirtualDriveService>();
            if (driveService?.MountedDriveLetter is not null)
            {
                driveService.UnmountAsync().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to unmount drive on shutdown: {ex.Message}");
        }
    }
}
