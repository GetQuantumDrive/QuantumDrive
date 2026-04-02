using System;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace quantum_drive.Services;

internal sealed class TrayIconService : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private Window? _window;

    public bool IsReallyClosing { get; private set; }

    public void Initialize(Window window)
    {
        _window = window;

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "QuantumDrive",
            IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri("ms-appx:///Assets/app.ico")),
            ContextMenuMode = ContextMenuMode.PopupMenu,
            DoubleClickCommand = new RelayCommand(ShowWindow),
        };

        var menu = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "Open QuantumDrive" };
        openItem.Click += (_, _) => ShowWindow();

        var separator = new MenuFlyoutSeparator();

        var quitItem = new MenuFlyoutItem { Text = "Quit" };
        quitItem.Click += (_, _) =>
        {
            IsReallyClosing = true;
            Application.Current.Exit();
        };

        menu.Items.Add(openItem);
        menu.Items.Add(separator);
        menu.Items.Add(quitItem);

        _trayIcon.ContextFlyout = menu;

        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.AppWindow.Show();
        _window.Activate();
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
