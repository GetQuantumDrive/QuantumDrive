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

        const int W = 900, H = 680;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(W, H));
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var work = display.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            work.X + (work.Width - W) / 2,
            work.Y + (work.Height - H) / 2));

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
