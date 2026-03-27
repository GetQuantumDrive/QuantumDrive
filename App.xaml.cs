using System;
using System.Diagnostics;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using quantum_drive.Services;
using quantum_drive.Services.Dropbox;
using quantum_drive.Services.GoogleDrive;
using quantum_drive.Services.OneDrive;
using quantum_drive.ViewModels;
using quantum_drive.Views;

namespace quantum_drive;

public partial class App : Application
{
    private Window? _window;
    private TaskbarIcon? _trayIcon;
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
        backendRegistry.Register(new GoogleDriveStorageBackendFactory());
        backendRegistry.Register(new DropboxStorageBackendFactory());
        backendRegistry.Register(new OneDriveStorageBackendFactory());

        _window = new MainWindow();
        CurrentWindow = _window;
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

        SetupTrayIcon();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "QuantumDrive",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.targetsize-24_altform-unplated.png")),
        };

        _trayIcon.TrayLeftMouseDoubleClick += (_, _) => ShowWindow();

        var contextMenu = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "Open QuantumDrive" };
        openItem.Click += (_, _) => ShowWindow();
        contextMenu.Items.Add(openItem);

        contextMenu.Items.Add(new MenuFlyoutSeparator());

        var quitItem = new MenuFlyoutItem { Text = "Quit" };
        quitItem.Click += (_, _) => QuitApp();
        contextMenu.Items.Add(quitItem);

        _trayIcon.ContextFlyout = contextMenu;
    }

    private void ShowWindow()
    {
        (_window as MainWindow)?.ShowAndActivate();
    }

    private void QuitApp()
    {
        if (_window is MainWindow mainWindow)
            mainWindow.IsExiting = true;

        _trayIcon?.Dispose();
        _trayIcon = null;

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

        _window?.Close();
    }
}
