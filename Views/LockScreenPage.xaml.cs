using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using quantum_drive.ViewModels;

namespace quantum_drive.Views;

public sealed partial class LockScreenPage : Page
{
    public LockScreenViewModel ViewModel { get; }

    public LockScreenPage()
    {
        ViewModel = App.Services.GetRequiredService<LockScreenViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += (_, _) => LogoBreathAnimation.Begin();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LockScreenViewModel.ShouldShake) && ViewModel.ShouldShake)
        {
            if (ViewModel.IsRecoveryMode)
                PlayShakeAnimation(RecoveryShakeTransform);
            else
                PlayShakeAnimation(ShakeTransform);

            ViewModel.ShouldShake = false;
        }
    }

    private void PasswordInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ViewModel.Password = PasswordInput.Password;
            ViewModel.UnlockCommand.Execute(null);
        }
    }

    private void RecoveryKeyInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.RecoveryKey = RecoveryKeyInput.Text;
    }

    private void RecoveryNewPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.NewPassword = RecoveryNewPasswordInput.Password;
    }

    private void RecoveryConfirmPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmNewPassword = RecoveryConfirmPasswordInput.Password;
    }

    private static void PlayShakeAnimation(Microsoft.UI.Xaml.Media.TranslateTransform target)
    {
        var storyboard = new Storyboard();
        var animation = new DoubleAnimationUsingKeyFrames();
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "X");

        animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(0.1), Value = -10 });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(0.2), Value = 10 });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(0.3), Value = -10 });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(0.4), Value = 10 });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(0.5), Value = 0 });

        storyboard.Children.Add(animation);
        storyboard.Begin();
    }
}
