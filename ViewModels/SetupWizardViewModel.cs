using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using quantum_drive.Helpers;
using quantum_drive.Services;
using quantum_drive.Views;

namespace quantum_drive.ViewModels;

public partial class SetupWizardViewModel : ObservableObject
{
    private readonly IIdentityService _identityService;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private int _currentStep;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _confirmPassword = string.Empty;

    [ObservableProperty]
    private double _entropyBits;

    [ObservableProperty]
    private string _strengthLabel = string.Empty;

    [ObservableProperty]
    private string _timeToCrack = string.Empty;

    [ObservableProperty]
    private string _publicKeyFingerprint = string.Empty;

    [ObservableProperty]
    private string _recoveryKitText = string.Empty;

    [ObservableProperty]
    private string _passwordHint = string.Empty;

    [ObservableProperty]
    private bool _hasAcknowledgedRisk;

    [ObservableProperty]
    private string _recoveryKey = string.Empty;

    [ObservableProperty]
    private bool _isCreatingVault;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _passwordsMatch;

    public bool IsPasswordValid =>
        EntropyBits >= 60
        && !string.IsNullOrEmpty(ConfirmPassword)
        && Password == ConfirmPassword;

    public bool CanFinish => HasAcknowledgedRisk;

    public int TotalSteps => 3;

    public SetupWizardViewModel(IIdentityService identityService, INavigationService navigationService)
    {
        _identityService = identityService;
        _navigationService = navigationService;
    }

    partial void OnPasswordChanged(string value)
    {
        EntropyBits = EntropyCalculator.CalculateBits(value);
        StrengthLabel = EntropyCalculator.GetStrengthLabel(EntropyBits);
        TimeToCrack = EntropyCalculator.GetTimeToCrack(EntropyBits);
        PasswordsMatch = !string.IsNullOrEmpty(ConfirmPassword) && value == ConfirmPassword;
        OnPropertyChanged(nameof(IsPasswordValid));
    }

    partial void OnConfirmPasswordChanged(string value)
    {
        PasswordsMatch = !string.IsNullOrEmpty(value) && Password == value;
        OnPropertyChanged(nameof(IsPasswordValid));
    }

    partial void OnHasAcknowledgedRiskChanged(bool value)
    {
        OnPropertyChanged(nameof(CanFinish));
    }

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep < TotalSteps - 1)
        {
            CurrentStep++;
        }
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep > 0)
        {
            CurrentStep--;
        }
    }

    [RelayCommand]
    private async Task CreateVaultAsync()
    {
        if (IsCreatingVault || !IsPasswordValid) return;

        IsCreatingVault = true;
        ErrorMessage = string.Empty;

        try
        {
            var (publicKey, _, recoveryKey) = await _identityService.CreateVaultAsync(Password, PasswordHint);

            RecoveryKey = recoveryKey;

            // Generate fingerprint from public key (first 16 bytes as hex, formatted XXXX-XXXX-...)
            string hex = Convert.ToHexString(publicKey.Take(16).ToArray());
            PublicKeyFingerprint = string.Join("-", Enumerable.Range(0, hex.Length / 4)
                .Select(i => hex.Substring(i * 4, Math.Min(4, hex.Length - i * 4))));

            // Build recovery kit text
            string salt = _identityService.VaultSaltBase64 ?? "N/A";
            RecoveryKitText = BuildRecoveryKitText(PublicKeyFingerprint, salt, PasswordHint, recoveryKey);

            Debug.WriteLine($"Vault created. Fingerprint: {PublicKeyFingerprint}");

            // Move to recovery kit step
            CurrentStep = 2;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Vault creation failed: {ex.Message}");
            ErrorMessage = "Failed to create vault. Please try again.";
        }
        finally
        {
            IsCreatingVault = false;
        }
    }

    [RelayCommand]
    private void CopyRecoveryKit()
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(RecoveryKitText);
        Clipboard.SetContent(dataPackage);
    }

    [RelayCommand]
    private void FinishSetup()
    {
        Debug.WriteLine("Setup complete — navigating to Dashboard.");
        // User is already authenticated after vault creation, go directly to Dashboard
        _navigationService.NavigateTo<DashboardPage>();
    }

    public static string BuildRecoveryKitText(string fingerprint, string salt, string? hint, string? recoveryKey = null)
    {
        string hintLine = string.IsNullOrEmpty(hint) ? "[None provided]" : hint;
        string recoveryKeySection = string.IsNullOrEmpty(recoveryKey)
            ? ""
            : $"""

            Recovery Key:
              {recoveryKey}

            """;
        return $"""
            ════════════════════════════════════════
                    QuantumDrive Recovery Kit
            ════════════════════════════════════════
            Created: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
            Vault ID: {fingerprint}

            Password Hint: {hintLine}
            {recoveryKeySection}
            Technical Details:
              Salt: {salt}
              Public Key Fingerprint: {fingerprint}

            ════════════════════════════════════════
              Store this document securely.
              Never share your master password
              or recovery key.
            ════════════════════════════════════════
            """;
    }
}
