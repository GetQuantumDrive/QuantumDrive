using Microsoft.UI.Xaml.Controls;

namespace quantum_drive.Services;

public class NavigationService : INavigationService
{
    private Frame? _frame;

    public void SetFrame(Frame frame)
    {
        _frame = frame;
    }

    public void NavigateTo<TPage>() where TPage : Page
    {
        _frame?.Navigate(typeof(TPage));
    }
}
