using System.Diagnostics;
using Microsoft.UI.Xaml;
using quantum_drive.Models;

namespace quantum_drive.Services.Dropbox;

/// <summary>
/// Factory for the Dropbox storage backend.
///
/// <para>
/// Implements <see cref="ICloudStorageBackendFactory"/> to provide the OAuth 2.0 PKCE
/// authorization flow via the Dropbox authorization endpoint.  After
/// <see cref="AuthorizeAsync"/> completes, the returned config dictionary contains:
/// </para>
/// <list type="table">
/// <listheader><term>Key</term><description>Value</description></listheader>
/// <item><term><c>access_token</c></term><description>Short-lived bearer token for Dropbox API calls.</description></item>
/// <item><term><c>refresh_token</c></term><description>Long-lived token for silent renewal (only returned when <c>token_access_type=offline</c>).</description></item>
/// <item><term><c>token_expiry</c></term><description>ISO-8601 UTC expiry of the access token.</description></item>
/// <item><term><c>account_email</c></term><description>Primary e-mail of the connected Dropbox account.</description></item>
/// </list>
///
/// <para>
/// <b>Developer set-up:</b> create an app at <see href="https://www.dropbox.com/developers/apps"/>
/// using the <c>Scoped access</c> type and <c>Full Dropbox</c> or <c>App folder</c>
/// permissions.  Enable <c>files.content.write</c>, <c>files.content.read</c>, and
/// <c>files.metadata.read</c> scopes.  Set the redirect URI to
/// <c>http://127.0.0.1</c> (loopback).  Replace <see cref="ClientId"/> with your App key.
/// </para>
/// </summary>
public sealed class DropboxStorageBackendFactory : ICloudStorageBackendFactory
{
    // ── OAuth credentials — replace with real values from dropbox.com/developers ──

    /// <summary>App key from the Dropbox App Console.</summary>
    internal const string ClientId = "3870e9q9zgmvhdb";

    private const string AuthEndpoint  = "https://www.dropbox.com/oauth2/authorize";
    private const string TokenEndpoint = "https://api.dropboxapi.com/oauth2/token";

    // ── IStorageBackendFactory ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public string Id => "dropbox";

    /// <inheritdoc/>
    public string DisplayName => "Dropbox";

    /// <inheritdoc/>
    public IStorageBackend CreateForVault(VaultDescriptor vault)
        => new DropboxStorageBackend(vault.Id, vault.BackendConfig);

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
            }, ct);

        var config = ParseTokenResponse(tokenJson);

        // Fetch account e-mail via /users/get_current_account.
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config["access_token"]);
        var accountResponse = await http.PostAsync(
            "https://api.dropboxapi.com/2/users/get_current_account",
            new System.Net.Http.StringContent("null", System.Text.Encoding.UTF8, "application/json"),
            ct);

        if (accountResponse.IsSuccessStatusCode)
        {
            var accountJson = await accountResponse.Content.ReadAsStringAsync(ct);
            var root = OAuthLoopbackHelper.ParseJsonPublic(accountJson);
            if (root.TryGetProperty("email", out var em))
                config["account_email"] = em.GetString() ?? "(unknown)";
        }

        config.TryAdd("account_email", "(unknown)");
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
            ["token_access_type"]     = "offline",
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

        return config;
    }
}
