using System.Diagnostics;
using Microsoft.UI.Xaml;
using quantum_drive.Models;

namespace quantum_drive.Services.GoogleDrive;

/// <summary>
/// Factory for the Google Drive storage backend.
///
/// <para>
/// Implements <see cref="ICloudStorageBackendFactory"/> to provide the OAuth 2.0 PKCE
/// authorization flow.  After <see cref="AuthorizeAsync"/> completes, the returned config
/// dictionary contains:
/// </para>
/// <list type="table">
/// <listheader><term>Key</term><description>Value</description></listheader>
/// <item><term><c>access_token</c></term><description>Short-lived Drive bearer token.</description></item>
/// <item><term><c>refresh_token</c></term><description>Long-lived token for silent renewal.</description></item>
/// <item><term><c>token_expiry</c></term><description>ISO-8601 UTC expiry of the access token.</description></item>
/// <item><term><c>account_email</c></term><description>Signed-in Google account e-mail.</description></item>
/// <item><term><c>remote_folder_id</c></term><description>Drive folder ID of <c>QuantumDrive/{vaultId}</c>.</description></item>
/// </list>
///
/// <para>
/// <b>Developer set-up:</b> create an OAuth 2.0 Desktop application credential at
/// <see href="https://console.cloud.google.com"/>, enable the Google Drive API, and
/// replace <see cref="ClientId"/> / <see cref="ClientSecret"/> with your values.
/// Redirect URI: <c>http://127.0.0.1</c> (any port — the loopback scheme).
/// Scope required: <c>https://www.googleapis.com/auth/drive.file</c>.
/// </para>
/// </summary>
public sealed class GoogleDriveStorageBackendFactory : ICloudStorageBackendFactory
{
    // ── OAuth credentials — replace with real values from console.cloud.google.com ──

    /// <summary>OAuth 2.0 client ID from the Google Cloud Console.</summary>
    internal const string ClientId = "YOUR_GOOGLE_CLIENT_ID";

    /// <summary>OAuth 2.0 client secret from the Google Cloud Console.</summary>
    internal const string ClientSecret = "YOUR_GOOGLE_CLIENT_SECRET";

    private const string AuthEndpoint  = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoUrl   = "https://www.googleapis.com/oauth2/v3/userinfo";
    private const string DriveScope    = "https://www.googleapis.com/auth/drive.file";

    // ── IStorageBackendFactory ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public string Id => "google-drive";

    /// <inheritdoc/>
    public string DisplayName => "Google Drive";

    /// <inheritdoc/>
    public IStorageBackend CreateForVault(VaultDescriptor vault)
        => new GoogleDriveStorageBackend(vault.Id, vault.BackendConfig);

    // ── ICloudStorageBackendFactory ────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Dictionary<string, string>> AuthorizeAsync(
        Window parentWindow, CancellationToken ct = default)
    {
        var port        = OAuthLoopbackHelper.GetFreePort();
        var redirectUri = OAuthLoopbackHelper.BuildRedirectUri(port);
        var (verifier, challenge) = OAuthLoopbackHelper.GeneratePkce();

        // Build authorization URL and open the browser
        var authUrl = BuildAuthUrl(redirectUri, challenge);
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        // Wait for the redirect code on the loopback port
        var code = await OAuthLoopbackHelper.WaitForAuthCodeAsync(redirectUri, ct);

        // Exchange code for tokens
        var tokenJson = await OAuthLoopbackHelper.ExchangeCodeForTokensAsync(
            TokenEndpoint,
            new Dictionary<string, string>
            {
                ["code"]          = code,
                ["client_id"]     = ClientId,
                ["client_secret"] = ClientSecret,
                ["redirect_uri"]  = redirectUri,
                ["grant_type"]    = "authorization_code",
                ["code_verifier"] = verifier,
            }, ct);

        var config = ParseTokenResponse(tokenJson);

        // Fetch user email
        var userJson = await OAuthLoopbackHelper.GetWithBearerAsync(
            UserInfoUrl, config["access_token"], ct);
        config["account_email"] = OAuthLoopbackHelper.ReadStringPublic(userJson, "email")
                                  ?? "(unknown)";

        // Create / locate the QuantumDrive root folder on Drive (stored for all vaults)
        var rootFolderId = await EnsureRootFolderAsync(config["access_token"], ct);
        config["remote_folder_id"] = rootFolderId;

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
            ["scope"]                 = DriveScope,
            ["access_type"]           = "offline",
            ["prompt"]                = "consent",
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

    /// <summary>
    /// Creates the <c>QuantumDrive</c> folder in the user's Drive root if it doesn't exist
    /// yet and returns its file ID.
    /// </summary>
    private static async Task<string> EnsureRootFolderAsync(string accessToken, CancellationToken ct)
    {
        const string filesUrl = "https://www.googleapis.com/drive/v3/files";
        const string folderMime = "application/vnd.google-apps.folder";

        // Check whether a folder named QuantumDrive already exists at root.
        var query  = Uri.EscapeDataString("name = 'QuantumDrive' and mimeType = 'application/vnd.google-apps.folder' and trashed = false and 'root' in parents");
        var fields = Uri.EscapeDataString("files(id)");
        var listJson = await OAuthLoopbackHelper.GetWithBearerAsync(
            $"{filesUrl}?q={query}&fields={fields}", accessToken, ct);

        var root = OAuthLoopbackHelper.ParseJsonPublic(listJson);
        if (root.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
        {
            return files[0].GetProperty("id").GetString()!;
        }

        // Create the folder.
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            name     = "QuantumDrive",
            mimeType = folderMime,
        });
        var response = await http.PostAsync(
            $"{filesUrl}?fields=id",
            new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        return OAuthLoopbackHelper.ReadStringPublic(responseBody, "id")
               ?? throw new InvalidOperationException("Google Drive did not return a folder ID.");
    }
}
