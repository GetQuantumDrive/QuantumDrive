using Microsoft.UI.Xaml;
using quantum_drive.Models;

namespace quantum_drive.Services;

/// <summary>
/// Extended factory interface for storage backends that require OAuth user sign-in before
/// a vault can be created (Google Drive, Dropbox, OneDrive, etc.).
///
/// <para>
/// Implement this on top of <see cref="IStorageBackendFactory"/> when your backend needs
/// the user to authorize access to their account. The setup wizard detects this interface
/// and shows a "Connect" button that calls <see cref="AuthorizeAsync"/> before proceeding.
/// </para>
///
/// <para>
/// Backends that do not require sign-in (local disk, self-hosted WebDAV with a static
/// credential, etc.) should implement only <see cref="IStorageBackendFactory"/>.
/// </para>
///
/// <para>
/// <b>BackendConfig keys your implementation must populate in <see cref="AuthorizeAsync"/>:</b>
/// <list type="table">
/// <listheader><term>Key</term><description>Description</description></listheader>
/// <item><term><c>access_token</c></term><description>Short-lived bearer token for API calls.</description></item>
/// <item><term><c>refresh_token</c></term><description>Long-lived token used to obtain a new access token when the current one expires.</description></item>
/// <item><term><c>token_expiry</c></term><description>ISO-8601 UTC timestamp of when <c>access_token</c> expires (e.g. <c>DateTime.UtcNow.AddSeconds(3600).ToString("O")</c>).</description></item>
/// <item><term><c>account_email</c></term><description>The signed-in user's email address, shown in the wizard after a successful connect.</description></item>
/// </list>
/// Add any additional provider-specific keys you need (e.g. <c>root_folder_id</c>).
/// All keys are stored in <see cref="VaultDescriptor.BackendConfig"/> and persisted to
/// <c>%LOCALAPPDATA%\QuantumDrive\settings.json</c>.
/// </para>
///
/// <para>
/// <b>Implementing a new cloud backend:</b> see <c>docs/contributing-providers.md</c> for
/// a step-by-step guide with an OAuth walkthrough, the test checklist, and the PR checklist.
/// </para>
/// </summary>
public interface ICloudStorageBackendFactory : IStorageBackendFactory
{
    /// <summary>
    /// Opens an OAuth 2.0 authorization flow (PKCE + localhost loopback redirect) and
    /// returns a populated <c>BackendConfig</c> dictionary that can be stored in
    /// <see cref="VaultDescriptor.BackendConfig"/> and later passed to
    /// <see cref="IStorageBackendFactory.CreateForVault"/>.
    ///
    /// <para>
    /// This method must open the user's default browser at the provider's authorization
    /// URL and listen for the redirect on a localhost port. Use
    /// <see cref="OAuthLoopbackHelper"/> for PKCE generation and the loopback listener.
    /// After the user approves the request, exchange the code for tokens, fetch the
    /// user's email, and return the populated dictionary.
    /// </para>
    ///
    /// <para>
    /// This method is called on the UI thread. The browser launch and loopback listening
    /// must be done asynchronously so the UI remains responsive during the flow.
    /// </para>
    ///
    /// <para>
    /// Throw <see cref="OperationCanceledException"/> if <paramref name="ct"/> is
    /// cancelled (e.g. the user closed the wizard). Throw <see cref="InvalidOperationException"/>
    /// with a user-facing message for other authorization failures.
    /// </para>
    ///
    /// <para>
    /// At minimum, populate: <c>access_token</c>, <c>refresh_token</c>,
    /// <c>token_expiry</c>, <c>account_email</c>. Add any provider-specific keys
    /// your <see cref="IStorageBackend"/> implementation needs (e.g. <c>root_folder_id</c>).
    /// </para>
    /// </summary>
    /// <param name="parentWindow">The application window, for centering any native dialogs.</param>
    /// <param name="ct">Cancellation token. Cancel if the user dismisses the wizard.</param>
    /// <returns>
    /// A <c>BackendConfig</c> dictionary ready to be stored in
    /// <see cref="VaultDescriptor.BackendConfig"/>.
    /// </returns>
    Task<Dictionary<string, string>> AuthorizeAsync(Window parentWindow, CancellationToken ct = default);

    /// <summary>
    /// Returns a human-readable account identifier (typically the email address) from a
    /// <c>BackendConfig</c> that was previously produced by <see cref="AuthorizeAsync"/>.
    /// Displayed in the setup wizard after a successful connect: "✓ Connected as user@example.com".
    ///
    /// <para>Returns <see langword="null"/> if the expected key is missing from <paramref name="config"/>.</para>
    /// </summary>
    /// <param name="config">
    /// A <c>BackendConfig</c> dictionary produced by <see cref="AuthorizeAsync"/>.
    /// </param>
    string? GetConnectedAccount(IReadOnlyDictionary<string, string> config);
}
