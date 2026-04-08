using Microsoft.UI.Xaml;
using quantum_drive.Models;
using quantum_drive.Views.Dialogs;

namespace quantum_drive.Services.PCloud;

/// <summary>
/// Factory for the pCloud storage backend.
///
/// <para>
/// Implements <see cref="ICloudStorageBackendFactory"/> using the pCloud OAuth 2.0
/// <b>token (implicit) flow</b> (<c>response_type=token</c>).  The bearer token is
/// returned directly in the loopback redirect query string — no second API call is
/// required and no client secret is needed.  After <see cref="AuthorizeAsync"/>
/// completes, the returned config dictionary contains:
/// </para>
/// <list type="table">
/// <listheader><term>Key</term><description>Value</description></listheader>
/// <item><term><c>access_token</c></term><description>Long-lived pCloud bearer token.</description></item>
/// <item><term><c>token_expiry</c></term><description>Far-future ISO-8601 UTC sentinel (pCloud tokens do not expire).</description></item>
/// <item><term><c>account_email</c></term><description>Email address of the signed-in account.</description></item>
/// <item><term><c>api_host</c></term><description>API hostname returned by pCloud (e.g. <c>api.pcloud.com</c> or <c>eapi.pcloud.com</c>).</description></item>
/// </list>
///
/// <para>
/// <b>Developer set-up:</b> register an application at
/// <see href="https://docs.pcloud.com/oauth_2.0_authentication.html"/>.
/// Provide your app name and the redirect URI <c>http://localhost</c>.  Replace
/// <see cref="ClientId"/> with the issued client ID.  No client secret is required.
/// pCloud access tokens are long-lived and do not need periodic refresh.
/// </para>
/// </summary>
public sealed class PCloudStorageBackendFactory : ICloudStorageBackendFactory
{
    // ── OAuth credentials — replace with your app's client ID from the pCloud developer portal ──

    /// <summary>Client ID from the pCloud developer portal app registration.</summary>
    internal const string ClientId = "YOUR_PCLOUD_CLIENT_ID";

    private const string AuthEndpoint    = "https://my.pcloud.com/oauth2/authorize";
    internal const string DefaultApiBase = "https://api.pcloud.com";

    // ── IStorageBackendFactory ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public string Id => "pcloud";

    /// <inheritdoc/>
    public string DisplayName => "pCloud";

    /// <inheritdoc/>
    public IStorageBackend CreateForVault(VaultDescriptor vault)
        => new PCloudStorageBackend(vault.Id, vault.BackendConfig);

    // ── ICloudStorageBackendFactory ────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Dictionary<string, string>> AuthorizeAsync(
        Window parentWindow, CancellationToken ct = default)
    {
        var port        = OAuthLoopbackHelper.GetFreePort();
        var redirectUri = OAuthLoopbackHelper.BuildRedirectUri(port);

        // Token flow: pCloud returns the bearer token directly in the redirect
        // query string — no code exchange step needed.
        var authUrl  = BuildAuthUrl(redirectUri);
        var xamlRoot = parentWindow.Content?.XamlRoot
            ?? throw new InvalidOperationException("Window content is not available.");

        var redirectParams = await OAuthWebViewDialog.GetTokenParamsAsync(authUrl, redirectUri, xamlRoot, ct);

        var config = new Dictionary<string, string>
        {
            ["access_token"] = redirectParams["access_token"],
            // pCloud tokens are long-lived; set a far-future sentinel so the
            // base-class refresh logic stays dormant.
            ["token_expiry"] = DateTime.UtcNow.AddYears(10).ToString("O"),
        };

        // "api.pcloud.com" = global/US bucket; "eapi.pcloud.com" = EU bucket.
        if (redirectParams.TryGetValue("hostname", out var hostname) &&
            !string.IsNullOrEmpty(hostname))
            config["api_host"] = hostname;

        // Fetch the user's email from the pCloud userinfo endpoint.
        var apiBase  = config.TryGetValue("api_host", out var h) ? $"https://{h}" : DefaultApiBase;
        var userJson = await OAuthLoopbackHelper.GetWithBearerAsync(
            $"{apiBase}/userinfo", config["access_token"], ct);

        var userRoot = OAuthLoopbackHelper.ParseJsonPublic(userJson);
        var email    = userRoot.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
        config["account_email"] = email ?? "(unknown)";

        return config;
    }

    /// <inheritdoc/>
    public string? GetConnectedAccount(IReadOnlyDictionary<string, string> config)
        => config.TryGetValue("account_email", out var email) ? email : null;

    // ── Private helpers ────────────────────────────────────────────────────────

    private static string BuildAuthUrl(string redirectUri)
    {
        var qs = new Dictionary<string, string>
        {
            ["client_id"]     = ClientId,
            ["redirect_uri"]  = redirectUri,
            ["response_type"] = "token",
        };
        return AuthEndpoint + "?" +
               string.Join("&", qs.Select(kv =>
                   $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }
}
