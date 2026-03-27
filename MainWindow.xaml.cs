using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!IsExiting)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    public void ShowAndActivate()
    {
        AppWindow.Show(true);
        Activate();
    }
}
