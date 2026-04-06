using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using quantum_drive.Helpers;
using quantum_drive.Models;
using quantum_drive.Services;
using quantum_drive.Services.S3;
using quantum_drive.Views;

namespace quantum_drive.ViewModels;

public partial class SetupWizardViewModel : ObservableObject
{
    private readonly IVaultRegistry _vaultRegistry;
    private readonly INavigationService _navigationService;
    private readonly StorageBackendRegistry _backendRegistry;

    /// <summary>BackendConfig populated by <see cref="ConnectCloudAccountAsync"/> (OAuth
    /// providers) or assembled from S3 credential fields (S3 providers) just before vault
    /// creation. Passed to <see cref="IVaultRegistry.RegisterNewVaultAsync"/>.</summary>
    private Dictionary<string, string>? _pendingBackendConfig;

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

    /// <summary>ID of the selected storage backend (e.g. "local", "google-drive").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalBackend))]
    [NotifyPropertyChangedFor(nameof(IsGoogleDriveBackend))]
    [NotifyPropertyChangedFor(nameof(IsDropboxBackend))]
    [NotifyPropertyChangedFor(nameof(IsPCloudBackend))]
    [NotifyPropertyChangedFor(nameof(IsS3Backend))]
    [NotifyPropertyChangedFor(nameof(IsOAuthBackend))]
    [NotifyPropertyChangedFor(nameof(CanAdvanceFromStorageStep))]
    private string _selectedBackendId = "local";

    /// <summary>Display label shown after OAuth succeeds (e.g. "user@gmail.com").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdvanceFromStorageStep))]
    private string _connectedAccountLabel = string.Empty;

    /// <summary>True while an OAuth browser flow is in progress.</summary>
    [ObservableProperty]
    private bool _isAuthorizingCloud;

    /// <summary>Error message shown under the Connect button when auth fails.</summary>
    [ObservableProperty]
    private string _cloudAuthError = string.Empty;

    // ── S3 credential fields ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdvanceFromStorageStep))]
    private string _s3AccessKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdvanceFromStorageStep))]
    private string _s3SecretKey = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdvanceFromStorageStep))]
    private string _s3Bucket = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdvanceFromStorageStep))]
    private string _s3Region = string.Empty;

    /// <summary>Region options for the currently selected S3 provider. Bound to the
    /// ComboBox in the credential form.</summary>
    [ObservableProperty]
    private IReadOnlyList<S3RegionItem> _s3AvailableRegions = [];

    // ── Computed properties ────────────────────────────────────────────────────

    /// <summary>True when the local storage option is selected.</summary>
    public bool IsLocalBackend => SelectedBackendId == "local";

    /// <summary>True when Google Drive is the selected backend.</summary>
    public bool IsGoogleDriveBackend => SelectedBackendId == "google-drive";

    /// <summary>True when Dropbox is the selected backend.</summary>
    public bool IsDropboxBackend => SelectedBackendId == "dropbox";

    /// <summary>True when pCloud is the selected backend.</summary>
    public bool IsPCloudBackend => SelectedBackendId == "pcloud";

    /// <summary>True when an S3-compatible backend is selected. Controls visibility of
    /// the credential-input form.</summary>
    public bool IsS3Backend =>
        _backendRegistry.GetFactory(SelectedBackendId) is S3StorageBackendFactoryBase;

    /// <summary>True when the selected backend uses OAuth (Google Drive, Dropbox, pCloud).
    /// Controls visibility of the "Connect Account" section.</summary>
    public bool IsOAuthBackend => !IsLocalBackend && !IsS3Backend;

    /// <summary>True when the user may advance past the storage selection step.</summary>
    public bool CanAdvanceFromStorageStep =>
        IsLocalBackend
        || (IsOAuthBackend  && !string.IsNullOrEmpty(ConnectedAccountLabel))
        || (IsS3Backend     && IsS3ConfigValid());

    public bool CanGetStarted   => HasAcceptedTerms;
    public bool IsPasswordValid => !string.IsNullOrEmpty(Password) && !string.IsNullOrEmpty(VaultName);
    public bool CanFinish       => HasAcknowledgedRisk;
    public int  TotalSteps      => 4;

    private bool IsS3ConfigValid() =>
        !string.IsNullOrEmpty(S3AccessKey) &&
        !string.IsNullOrEmpty(S3SecretKey) &&
        !string.IsNullOrEmpty(S3Bucket)    &&
        !string.IsNullOrEmpty(S3Region);

    public SetupWizardViewModel(
        IVaultRegistry vaultRegistry,
        INavigationService navigationService,
        StorageBackendRegistry backendRegistry)
    {
        _vaultRegistry     = vaultRegistry;
        _navigationService = navigationService;
        _backendRegistry   = backendRegistry;

        _vaultFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    // ── Property-changed side-effects ──────────────────────────────────────────

    partial void OnPasswordChanged(string value)
    {
        EntropyBits   = EntropyCalculator.CalculateBits(value);
        StrengthLabel = EntropyCalculator.GetStrengthLabel(EntropyBits);
        TimeToCrack   = EntropyCalculator.GetTimeToCrack(EntropyBits);
        OnPropertyChanged(nameof(IsPasswordValid));
    }

    partial void OnVaultNameChanged(string value)    => OnPropertyChanged(nameof(IsPasswordValid));
    partial void OnHasAcceptedTermsChanged(bool value)  => OnPropertyChanged(nameof(CanGetStarted));
    partial void OnHasAcknowledgedRiskChanged(bool value) => OnPropertyChanged(nameof(CanFinish));

    // ── Commands ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep < TotalSteps - 1)
            CurrentStep++;
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep > 0)
            CurrentStep--;
    }

    /// <summary>
    /// Selects a storage backend. For OAuth providers the user must still call
    /// <see cref="ConnectCloudAccountAsync"/>; for S3 providers the credential fields
    /// are shown immediately.
    /// </summary>
    [RelayCommand]
    private void SelectBackend(string backendId)
    {
        if (SelectedBackendId == backendId) return;
        SelectedBackendId     = backendId;
        ConnectedAccountLabel = string.Empty;
        CloudAuthError        = string.Empty;
        _pendingBackendConfig = null;

        // Reset S3 fields whenever switching backends.
        S3AccessKey = string.Empty;
        S3SecretKey = string.Empty;
        S3Bucket    = string.Empty;

        // Load region list when an S3 factory is selected.
        if (_backendRegistry.GetFactory(backendId) is S3StorageBackendFactoryBase s3Factory)
        {
            S3AvailableRegions = s3Factory.AvailableRegions;
            S3Region = S3AvailableRegions.Count > 0 ? S3AvailableRegions[0].RegionId : string.Empty;
        }
        else
        {
            S3AvailableRegions = [];
            S3Region           = string.Empty;
        }
    }

    /// <summary>
    /// Launches the OAuth authorization flow for the currently selected cloud backend.
    /// Not called for S3 backends (guarded at the top of the method).
    /// </summary>
    [RelayCommand]
    private async Task ConnectCloudAccountAsync()
    {
        if (IsLocalBackend || IsS3Backend) return;

        var factory = _backendRegistry.GetFactory(SelectedBackendId) as ICloudStorageBackendFactory;
        if (factory is null)
        {
            CloudAuthError = $"Provider '{SelectedBackendId}' is not registered.";
            return;
        }

        IsAuthorizingCloud    = true;
        CloudAuthError        = string.Empty;
        ConnectedAccountLabel = string.Empty;

        try
        {
            var window = App.CurrentWindow
                ?? throw new InvalidOperationException("No active window.");

            _pendingBackendConfig = await factory.AuthorizeAsync(window);
            ConnectedAccountLabel = factory.GetConnectedAccount(_pendingBackendConfig)
                                    ?? "(connected)";
        }
        catch (OperationCanceledException)
        {
            CloudAuthError = "Authorization was cancelled.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cloud auth failed: {ex.Message}");
            CloudAuthError = "Authorization failed. Please try again.";
        }
        finally
        {
            IsAuthorizingCloud = false;
        }
    }

    public void SetVaultFolder(StorageFolder folder)
    {
        VaultFolderPath = folder.Path;
    }

    [RelayCommand]
    private async Task CreateVaultAsync()
    {
        if (IsCreatingVault || !IsPasswordValid) return;

        IsCreatingVault = true;
        ErrorMessage    = string.Empty;

        try
        {
            // For S3 backends, assemble BackendConfig from the credential fields.
            if (IsS3Backend)
            {
                var s3Factory = _backendRegistry.GetFactory(SelectedBackendId) as S3StorageBackendFactoryBase;
                if (s3Factory is null)
                {
                    ErrorMessage = "S3 provider is not registered.";
                    return;
                }
                _pendingBackendConfig = s3Factory.BuildConfig(S3AccessKey, S3SecretKey, S3Bucket, S3Region);
            }

            // Cloud backends store data remotely; give them a local temp directory for CFAPI.
            var folderPath = IsLocalBackend
                ? VaultFolderPath
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QuantumDrive", "CloudVaults", Guid.NewGuid().ToString("N")[..8]);

            var descriptor = await _vaultRegistry.RegisterNewVaultAsync(
                VaultName.Trim(), folderPath, Password, PasswordHint,
                SelectedBackendId, _pendingBackendConfig);

            var context = _vaultRegistry.GetContext(descriptor.Id);
            var publicKey = context?.Identity.MlKemPublicKey;
            var recoveryKeyStr = context is not null
                ? await context.Identity.GetRecoveryKeyAsync(Password)
                : null;

            RecoveryKey = recoveryKeyStr ?? string.Empty;

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

            CurrentStep = 3;
        }
        catch (VaultLimitReachedException)
        {
            ErrorMessage = "Free tier allows 1 vault. Upgrade to Pro for unlimited vaults.";
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
    private void CancelSetup() => _navigationService.NavigateTo<DashboardPage>();

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
