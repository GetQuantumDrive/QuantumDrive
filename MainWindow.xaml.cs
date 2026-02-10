using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace quantum_drive;

public sealed partial class MainWindow : Window
{
    public Frame NavigationFrame => RootFrame;

    public MainWindow()
    {
        InitializeComponent();
    }
}
