using Microsoft.UI.Xaml.Controls;

namespace quantum_drive.Services;

public interface INavigationService
{
    void NavigateTo<TPage>() where TPage : Page;
    void SetFrame(Frame frame);
}
