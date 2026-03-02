using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using quantum_drive.Helpers;
using quantum_drive.Services;
using quantum_drive.Views;

namespace quantum_drive.ViewModels;

public partial class SetupWizardViewModel : ObservableObject
{
    private readonly IVaultRegistry _vaultRegistry;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private int _currentStep;

    [ObservableProperty]
    private string _vaultName = "My Vault";

    [ObservableProperty]
    private string _vaultFolderPath = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

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
    private bool _hasAcceptedTerms;

    private StorageFolder? _pickedFolder;

    public bool CanGetStarted => HasAcceptedTerms;

    public bool IsPasswordValid =>
        !string.IsNullOrEmpty(Password)
        && !string.IsNullOrEmpty(VaultName);

    public bool CanFinish => HasAcknowledgedRisk;

    public int TotalSteps => 3;

    public SetupWizardViewModel(IVaultRegistry vaultRegistry, INavigationService navigationService)
    {
        _vaultRegistry = vaultRegistry;
        _navigationService = navigationService;

        // Default folder path
        _vaultFolderPath = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
    }

    partial void OnPasswordChanged(string value)
    {
        EntropyBits = EntropyCalculator.CalculateBits(value);
        StrengthLabel = EntropyCalculator.GetStrengthLabel(EntropyBits);
        TimeToCrack = EntropyCalculator.GetTimeToCrack(EntropyBits);
        OnPropertyChanged(nameof(IsPasswordValid));
    }

    partial void OnVaultNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsPasswordValid));
    }

    partial void OnHasAcceptedTermsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGetStarted));
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

    public void SetVaultFolder(StorageFolder folder)
    {
        _pickedFolder = folder;
        VaultFolderPath = folder.Path;
    }

    [RelayCommand]
    private async Task CreateVaultAsync()
    {
        if (IsCreatingVault || !IsPasswordValid) return;

        IsCreatingVault = true;
        ErrorMessage = string.Empty;

        try
        {
            var descriptor = await _vaultRegistry.RegisterNewVaultAsync(
                VaultName.Trim(), VaultFolderPath, Password, PasswordHint, _pickedFolder);

            var context = _vaultRegistry.GetContext(descriptor.Id);
            var publicKey = context?.Identity.MlKemPublicKey;
            var recoveryKeyStr = context is not null
                ? await context.Identity.GetRecoveryKeyAsync(Password)
                : null;

            RecoveryKey = recoveryKeyStr ?? string.Empty;

            // Generate fingerprint from public key
            string fingerprint = "N/A";
            if (publicKey is not null)
            {
                string hex = Convert.ToHexString(publicKey.Take(16).ToArray());
                fingerprint = string.Join("-", Enumerable.Range(0, hex.Length / 4)
                    .Select(i => hex.Substring(i * 4, Math.Min(4, hex.Length - i * 4))));
            }
            PublicKeyFingerprint = fingerprint;

            string salt = context?.Identity.VaultSaltBase64 ?? "N/A";
            RecoveryKitText = BuildRecoveryKitText(PublicKeyFingerprint, salt, PasswordHint, recoveryKeyStr);

            Debug.WriteLine($"Vault created. Fingerprint: {PublicKeyFingerprint}");

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
