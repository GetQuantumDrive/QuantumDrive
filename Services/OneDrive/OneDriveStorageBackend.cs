using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using quantum_drive.Models;

namespace quantum_drive.Services.OneDrive;

/// <summary>
/// <see cref="IStorageBackend"/> implementation backed by the Microsoft Graph OneDrive API v1.0.
///
/// <para>
/// All <c>.qd</c> files are stored at
/// <c>/me/drive/root:/QuantumDrive/{vaultId}/{filename}.qd</c>.  Like Dropbox, OneDrive
/// supports path-based addressing, so no folder ID is required after the vault is
/// provisioned.  An in-memory <c>name → driveItemId</c> cache is populated by
/// <see cref="ListFilesAsync"/> for faster subsequent lookups.
/// </para>
///
/// <para>
/// Token refresh is handled by the base class (<see cref="CloudStorageBackendBase"/>).
/// </para>
/// </summary>
internal sealed class OneDriveStorageBackend : CloudStorageBackendBase
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    private readonly string _vaultPath; // e.g. QuantumDrive/a1b2c3d4

    public OneDriveStorageBackend(string vaultId, Dictionary<string, string> config)
        : base(vaultId, config)
    {
        _vaultPath = $"QuantumDrive/{vaultId}";
    }

    // ── IStorageBackend ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override async Task<IEnumerable<string>> ListFilesAsync(
        string extension, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        using var http = MakeClient();
        var url = $"{GraphBase}/me/drive/root:/{_vaultPath}:/children?$select=id,name";

        var response = await http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return Enumerable.Empty<string>(); // Folder not yet created.

        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"OneDrive list error: {json}");

        var root  = ParseJson(json);
        var names = new List<string>();

        NameToId.Clear();

        if (root.TryGetProperty("value", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? string.Empty;
                var id   = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";

                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                    if (id.Length > 0) NameToId[name] = id;
                }
            }
        }

        return names;
    }

    /// <inheritdoc/>
    public override async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        using var http = MakeClient();
        var url = $"{GraphBase}/me/drive/root:/{_vaultPath}/{path}:/content";

        var response = await http.GetAsync(url, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new FileNotFoundException($"Remote file not found: {path}");

        response.EnsureSuccessStatusCode();

        var ms = new MemoryStream();
        await response.Content.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    /// <inheritdoc/>
    public override async Task WriteAsync(
        string path, Stream content, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        var data = await ReadAllBytesAsync(content, ct);
        var url  = $"{GraphBase}/me/drive/root:/{_vaultPath}/{path}:/content";

        using var http = MakeClient();
        var response = await http.PutAsync(
            url,
            new ByteArrayContent(data) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
            ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"OneDrive write error: {body}");

        // Cache the returned item ID.
        var id = OAuthLoopbackHelper.ReadStringPublic(body, "id");
        if (id is not null) NameToId[path] = id;
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        string url;
        if (NameToId.TryGetValue(path, out var itemId))
        {
            url = $"{GraphBase}/me/drive/items/{itemId}";
        }
        else
        {
            url = $"{GraphBase}/me/drive/root:/{_vaultPath}/{path}";
        }

        using var http = MakeClient();
        var response = await http.DeleteAsync(url, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
        NameToId.TryRemove(path, out _);
    }

    /// <inheritdoc/>
    public override async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        if (NameToId.ContainsKey(path)) return true;

        using var http = MakeClient();
        var url = $"{GraphBase}/me/drive/root:/{_vaultPath}/{path}";
        var response = await http.GetAsync(url, ct);
        return response.IsSuccessStatusCode;
    }

    // ── Token refresh ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override async Task RefreshTokenAsync(CancellationToken ct)
    {
        if (!Config.TryGetValue("refresh_token", out var refreshToken) ||
            string.IsNullOrEmpty(refreshToken))
            throw new InvalidOperationException("No refresh_token in BackendConfig.");

        var tenantId = Config.TryGetValue("tenant_id", out var t) ? t : "common";
        var endpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

        var body = await OAuthLoopbackHelper.ExchangeCodeForTokensAsync(
            endpoint,
            new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"]     = OneDriveStorageBackendFactory.ClientId,
                ["scope"]         = OneDriveStorageBackendFactory.Scopes,
            }, ct);

        var root = ParseJson(body);
        if (root.TryGetProperty("access_token", out var at))
            Config["access_token"] = at.GetString()!;
        if (root.TryGetProperty("expires_in", out var exp))
            Config["token_expiry"] = DateTime.UtcNow.AddSeconds(exp.GetInt32()).ToString("O");
        if (root.TryGetProperty("refresh_token", out var rt))
            Config["refresh_token"] = rt.GetString()!;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private HttpClient MakeClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AccessToken);
        return http;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        if (stream is MemoryStream ms) return ms.ToArray();
        using var buf = new MemoryStream();
        await stream.CopyToAsync(buf, ct);
        return buf.ToArray();
    }
}
