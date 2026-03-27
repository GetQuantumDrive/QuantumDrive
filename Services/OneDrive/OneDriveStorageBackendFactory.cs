using System.Diagnostics;
using Microsoft.UI.Xaml;
using quantum_drive.Models;

namespace quantum_drive.Services.OneDrive;

/// <summary>
/// Factory for the OneDrive (Microsoft Graph) storage backend.
///
/// <para>
/// Implements <see cref="ICloudStorageBackendFactory"/> to provide the OAuth 2.0 PKCE
/// authorization flow via the Microsoft identity platform.  After
/// <see cref="AuthorizeAsync"/> completes, the returned config dictionary contains:
/// </para>
/// <list type="table">
/// <listheader><term>Key</term><description>Value</description></listheader>
/// <item><term><c>access_token</c></term><description>Short-lived Microsoft Graph bearer token.</description></item>
/// <item><term><c>refresh_token</c></term><description>Long-lived token for silent renewal.</description></item>
/// <item><term><c>token_expiry</c></term><description>ISO-8601 UTC expiry of the access token.</description></item>
/// <item><term><c>account_email</c></term><description>User principal name (UPN) of the signed-in account.</description></item>
/// <item><term><c>tenant_id</c></term><description>Azure AD tenant ID returned by the token endpoint.</description></item>
/// </list>
///
/// <para>
/// <b>Developer set-up:</b> register an application at
/// <see href="https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps"/>.
/// Choose <c>Accounts in any organizational directory and personal Microsoft accounts</c>
/// (multi-tenant + MSA).  Under <em>Authentication</em> add a mobile/desktop redirect URI:
/// <c>http://localhost</c>.  Under <em>API permissions</em> add
/// <c>Files.ReadWrite</c> (delegated, Microsoft Graph).  Replace <see cref="ClientId"/>
/// with the Application (client) ID.  No client secret is required for PKCE.
/// </para>
/// </summary>
public sealed class OneDriveStorageBackendFactory : ICloudStorageBackendFactory
{
    // ── OAuth credentials — replace with real values from portal.azure.com ────

    /// <summary>Application (client) ID from the Azure portal app registration.</summary>
    internal const string ClientId = "YOUR_AZURE_CLIENT_ID";

    /// <summary>Space-separated Microsoft Graph scopes requested during authorization.</summary>
    internal const string Scopes = "Files.ReadWrite offline_access User.Read";

    private const string AuthEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    // ── IStorageBackendFactory ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public string Id => "onedrive";

    /// <inheritdoc/>
    public string DisplayName => "OneDrive";

    /// <inheritdoc/>
    public IStorageBackend CreateForVault(VaultDescriptor vault)
        => new OneDriveStorageBackend(vault.Id, vault.BackendConfig);

    // ── ICloudStorageBackendFactory ────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Dictionary<string, string>> AuthorizeAsync(
        Window parentWindow, CancellationToken ct = default)
    {
        var port        = OAuthLoopbackHelper.GetFreePort();
        var redirectUri = OAuthLoopbackHelper.BuildRedirectUri(port);
        var (verifier, challenge) = OAuthLoopbackHelper.GeneratePkce();

        var authUrl = BuildAuthUrl(redirectUri, challenge);
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var code = await OAuthLoopbackHelper.WaitForAuthCodeAsync(redirectUri, ct);

        var tokenJson = await OAuthLoopbackHelper.ExchangeCodeForTokensAsync(
            TokenEndpoint,
            new Dictionary<string, string>
            {
                ["code"]          = code,
                ["client_id"]     = ClientId,
                ["redirect_uri"]  = redirectUri,
                ["grant_type"]    = "authorization_code",
                ["code_verifier"] = verifier,
                ["scope"]         = Scopes,
            }, ct);

        var config = ParseTokenResponse(tokenJson);

        // Fetch the user's display name / UPN via Microsoft Graph.
        var userJson = await OAuthLoopbackHelper.GetWithBearerAsync(
            "https://graph.microsoft.com/v1.0/me?$select=userPrincipalName,mail",
            config["access_token"], ct);

        var userRoot = OAuthLoopbackHelper.ParseJsonPublic(userJson);
        var email = userRoot.TryGetProperty("mail", out var mail) && mail.GetString() is { Length: > 0 } m
            ? m
            : userRoot.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() : null;

        config["account_email"] = email ?? "(unknown)";

        return config;
    }

    /// <inheritdoc/>
    public string? GetConnectedAccount(IReadOnlyDictionary<string, string> config)
        => config.TryGetValue("account_email", out var email) ? email : null;

    // ── Private helpers ────────────────────────────────────────────────────────

    private static string BuildAuthUrl(string redirectUri, string codeChallenge)
    {
        var qs = new Dictionary<string, string>
        {
            ["client_id"]             = ClientId,
            ["redirect_uri"]          = redirectUri,
            ["response_type"]         = "code",
            ["scope"]                 = Scopes,
            ["code_challenge"]        = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        return AuthEndpoint + "?" +
               string.Join("&", qs.Select(kv =>
                   $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    private static Dictionary<string, string> ParseTokenResponse(string json)
    {
        var config = new Dictionary<string, string>();
        var root = OAuthLoopbackHelper.ParseJsonPublic(json);

        if (root.TryGetProperty("access_token", out var at))
            config["access_token"] = at.GetString()!;
        if (root.TryGetProperty("refresh_token", out var rt))
            config["refresh_token"] = rt.GetString()!;
        if (root.TryGetProperty("expires_in", out var exp))
            config["token_expiry"] = DateTime.UtcNow.AddSeconds(exp.GetInt32()).ToString("O");
        if (root.TryGetProperty("tenant_id", out var tid))
            config["tenant_id"] = tid.GetString()!;

        return config;
    }
}
