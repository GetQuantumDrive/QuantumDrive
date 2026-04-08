using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using quantum_drive.Services;
using quantum_drive.Services.Dropbox;
using quantum_drive.Services.GoogleDrive;
using quantum_drive.Services.S3;
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
        services.AddSingleton<TrayIconService>();

        // ViewModels
        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<SettingsViewModel>();

        _services = services.BuildServiceProvider();

        // Load license and register storage backends before UI starts
        _services.GetRequiredService<ILicenseService>().Load();

        var backendRegistry = _services.GetRequiredService<StorageBackendRegistry>();
        backendRegistry.Register(new LocalStorageBackendFactory());
        backendRegistry.Register(new GoogleDriveStorageBackendFactory());
        backendRegistry.Register(new DropboxStorageBackendFactory());
backendRegistry.Register(new ScalewayStorageBackendFactory());

        _window = new MainWindow();
        CurrentWindow = _window;
        _window.Closed += OnWindowClosed;

        var trayService = _services.GetRequiredService<TrayIconService>();
        trayService.Initialize(_window);

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
        var trayService = _services?.GetService<TrayIconService>();
        if (trayService is not null && !trayService.IsReallyClosing)
        {
            // Window was hidden to tray — keep app alive, don't unmount
            return;
        }

        try
        {
            var driveService = _services?.GetService<IVirtualDriveService>();
            if (driveService?.SyncRootPath is not null)
            {
                var unmountTask = driveService.ForceUnmountAsync();
                if (!unmountTask.Wait(TimeSpan.FromSeconds(3)))
                {
                    Debug.WriteLine("Unmount timed out on quit — proceeding with shutdown.");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to unmount drive on quit: {ex.Message}");
        }
        finally
        {
            trayService?.Dispose();
        }
    }
}
