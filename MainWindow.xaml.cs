using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using quantum_drive.Services;

namespace quantum_drive;

public sealed partial class MainWindow : Window
{
    public Frame NavigationFrame => RootFrame;

    public MainWindow()
    {
        InitializeComponent();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        AppWindow.Closing += OnAppWindowClosing;
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        var tray = App.Services.GetRequiredService<TrayIconService>();
        if (AppSettings.MinimizeToTray && !tray.IsReallyClosing)
        {
            args.Cancel = true;
            AppWindow.Hide();
        }
    }
}
