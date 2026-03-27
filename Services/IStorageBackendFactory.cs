using quantum_drive.Models;

namespace quantum_drive.Services;

/// <summary>
/// Plugin entry point for a storage backend. Each backend (local disk, Google Drive,
/// Dropbox, OneDrive, S3 …) provides exactly one factory implementation.
///
/// <para>
/// <b>Registration:</b> register your factory in <c>App.xaml.cs</c> by calling
/// <c>backendRegistry.Register(new YourFactory())</c>. The <see cref="Id"/> must be
/// unique across all registered factories and must be stable across app versions — it
/// is stored in <see cref="VaultDescriptor.BackendId"/> and used to look up the correct
/// factory when the user reopens a vault.
/// </para>
///
/// <para>
/// <b>Cloud providers / OAuth:</b> if your backend requires user sign-in, implement
/// <see cref="ICloudStorageBackendFactory"/> instead. That interface adds
/// <c>AuthorizeAsync</c> (runs the OAuth flow) and <c>GetConnectedAccount</c> (returns
/// the signed-in email for display in the wizard).
/// </para>
///
/// <para>
/// <b>BackendConfig:</b> backend-specific settings (OAuth tokens, remote folder IDs,
/// server URLs, …) are stored in <see cref="VaultDescriptor.BackendConfig"/>
/// (<c>Dictionary&lt;string, string&gt;</c>). Document the keys your implementation
/// reads so contributors know what to expect in the dictionary.
/// </para>
///
/// <para>
/// <b>Implementing a new backend:</b> see <c>docs/contributing-providers.md</c> for a
/// step-by-step guide, the test checklist, and the PR checklist.
/// </para>
/// </summary>
public interface IStorageBackendFactory
{
    /// <summary>
    /// Stable, lowercase identifier for this backend. Stored in
    /// <see cref="VaultDescriptor.BackendId"/> and used to look up the factory on app
    /// restart. Must not change across versions once vaults have been created with it.
    ///
    /// <para>Examples: <c>"local"</c>, <c>"google-drive"</c>, <c>"dropbox"</c>, <c>"onedrive"</c>.</para>
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Human-readable name shown in the setup wizard's storage picker.
    /// <para>Examples: <c>"Local Folder"</c>, <c>"Google Drive"</c>.</para>
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Creates and returns a ready-to-use <see cref="IStorageBackend"/> for the given vault.
    ///
    /// <para>
    /// This method is called each time the vault is unlocked and the virtual drive is
    /// mounted. It must return quickly — do not perform network requests here. Store any
    /// credentials needed for lazy initialization in <see cref="VaultDescriptor.BackendConfig"/>.
    /// </para>
    ///
    /// <para>
    /// The <see cref="VaultDescriptor.BackendConfig"/> dictionary is the same
    /// <c>Dictionary&lt;string, string&gt;</c> instance held by the vault registry. Any
    /// in-memory updates to it (e.g. refreshed access tokens) will be visible to the rest
    /// of the app for the lifetime of the session, but will only be persisted to disk if
    /// the registry's <c>SaveVaultList</c> is triggered by another operation.
    /// </para>
    /// </summary>
    /// <param name="vault">The vault descriptor, including <c>BackendConfig</c> with credentials.</param>
    IStorageBackend CreateForVault(VaultDescriptor vault);
}
