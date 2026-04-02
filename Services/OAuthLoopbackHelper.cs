using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace quantum_drive.Services;

/// <summary>
/// Shared helper for OAuth 2.0 PKCE + localhost loopback flows.
/// Used by all three built-in cloud providers (Google Drive, Dropbox, OneDrive).
/// Third-party provider implementations may use this class freely.
/// </summary>
public static class OAuthLoopbackHelper
{
    /// <summary>
    /// Generates a cryptographically random PKCE <c>code_verifier</c> (64 bytes, base64url-
    /// encoded to 86 characters) and its SHA-256 <c>code_challenge</c> (base64url-encoded).
    /// Use the verifier in the token exchange and the challenge in the authorization URL.
    /// </summary>
    public static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(64);
        var verifier = Base64UrlEncode(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return (verifier, Base64UrlEncode(challengeBytes));
    }

    /// <summary>
    /// Finds a free TCP port on 127.0.0.1. Use this to build the redirect URI before
    /// starting the loopback listener.
    /// </summary>
    public static int GetFreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Returns the redirect URI string for the given loopback port.</summary>
    public static string BuildRedirectUri(int port) => $"http://127.0.0.1:{port}/";

    /// <summary>
    /// Starts an <see cref="HttpListener"/> on <paramref name="redirectUri"/> and waits for
    /// the OAuth provider to redirect back with an authorization code. Responds with a
    /// friendly "Authorization complete" page so the user knows they can return to the app.
    /// </summary>
    /// <param name="redirectUri">
    /// The redirect URI registered with the OAuth provider (e.g. <c>http://127.0.0.1:PORT/</c>).
    /// Must end with <c>/</c>.
    /// </param>
    /// <param name="ct">Cancel to abort the wait (e.g. user closed the wizard).</param>
    /// <returns>The raw authorization code from the provider's query string (<c>?code=…</c>).</returns>
    /// <exception cref="InvalidOperationException">Thrown when the provider returns an error or no code.</exception>
    public static async Task<string> WaitForAuthCodeAsync(string redirectUri, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri.TrimEnd('/') + "/");
        listener.Start();

        HttpListenerContext context;
        try
        {
            // Race: either we get the redirect or the token is cancelled
            var contextTask = listener.GetContextAsync();
            await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, ct));
            ct.ThrowIfCancellationRequested();
            context = await contextTask;
        }
        finally
        {
            listener.Stop();
        }

        var error = context.Request.QueryString["error"];
        if (!string.IsNullOrEmpty(error))
            throw new InvalidOperationException($"Authorization failed: {error}");

        var code = context.Request.QueryString["code"]
            ?? throw new InvalidOperationException("Authorization redirect did not include a code.");

        // Write a user-visible success page
        const string html = """
            <html><head><title>QuantumDrive</title>
            <style>body{font-family:system-ui;display:flex;justify-content:center;align-items:center;height:100vh;margin:0}
            .box{text-align:center;padding:2rem}h2{margin-bottom:.5rem}</style></head>
            <body><div class="box"><h2>✓ Authorization complete</h2>
            <p>You can close this tab and return to QuantumDrive.</p></div></body></html>
            """;
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, CancellationToken.None);
        context.Response.Close();

        return code;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Exchanges an authorization code for access + refresh tokens via a POST to
    /// <paramref name="tokenEndpoint"/>. Returns the raw JSON response body.
    /// </summary>
    public static async Task<string> ExchangeCodeForTokensAsync(
        string tokenEndpoint,
        Dictionary<string, string> fields,
        CancellationToken ct)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("Accept", "application/json");
        var response = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(fields), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token exchange failed ({(int)response.StatusCode}): {body}");
        return body;
    }

    /// <summary>
    /// Sends a GET request to <paramref name="url"/> with a Bearer token and returns the
    /// response body as a string. Used to fetch user info after authorization.
    /// </summary>
    public static async Task<string> GetWithBearerAsync(
        string url, string accessToken, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");
        return body;
    }

    /// <summary>
    /// Reads the string value of <paramref name="property"/> from a JSON object string.
    /// Returns <see langword="null"/> if the property is absent or the JSON is invalid.
    /// </summary>
    public static string? ReadStringPublic(string json, string property)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(property, out var el))
                return el.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Parses a JSON string and returns a cloned <see cref="System.Text.Json.JsonElement"/>
    /// so the underlying document can be disposed immediately.
    /// </summary>
    public static System.Text.Json.JsonElement ParseJsonPublic(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    internal static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
