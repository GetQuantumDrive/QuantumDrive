using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    }
}
