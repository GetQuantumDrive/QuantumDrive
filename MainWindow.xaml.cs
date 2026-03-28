using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using quantum_drive.Services;

namespace quantum_drive;

public sealed partial class MainWindow : Window
{
    public Frame NavigationFrame => RootFrame;

    /// <summary>Set to true before calling Close() to perform a real exit instead of minimizing to tray.</summary>
    internal bool IsExiting;

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
