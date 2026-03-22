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
        // Clean up stale registry entries from previous crashed sessions
        VirtualDriveService.CleanupStaleEntries();

        var services = new ServiceCollection();

        // Services
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IPostQuantumCrypto, PostQuantumCrypto>();
        services.AddSingleton<ILicenseService, LicenseService>();
        services.AddSingleton<WindowsHelloService>();
        services.AddSingleton<StorageBackendRegistry>();
        services.AddSingleton<IVaultRegistry, VaultRegistry>();
        services.AddSingleton<IVirtualDriveService, VirtualDriveService>();

        // ViewModels
        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<SettingsViewModel>();

        _services = services.BuildServiceProvider();

        // Load license and register storage backends before UI starts
        _services.GetRequiredService<ILicenseService>().Load();

        var backendRegistry = _services.GetRequiredService<StorageBackendRegistry>();
        backendRegistry.Register(new LocalStorageBackendFactory());

        _window = new MainWindow();
        CurrentWindow = _window;
        _window.Closed += OnWindowClosed;
        _window.Activate();

        // Set up navigation
        var mainWindow = (MainWindow)_window;
        var navigationService = (NavigationService)_services.GetRequiredService<INavigationService>();
        navigationService.SetFrame(mainWindow.NavigationFrame);

        // Route to appropriate page
        var vaultRegistry = _services.GetRequiredService<IVaultRegistry>();
        if (vaultRegistry.HasAnyVault)
        {
            navigationService.NavigateTo<DashboardPage>();
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
            if (driveService?.SyncRootPath is not null)
            {
                // Force unmount with a timeout to avoid hanging the close
                var unmountTask = driveService.ForceUnmountAsync();
                if (!unmountTask.Wait(TimeSpan.FromSeconds(3)))
                {
                    Debug.WriteLine("Unmount timed out on window close — proceeding with shutdown.");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to unmount drive on shutdown: {ex.Message}");
        }
    }
}
