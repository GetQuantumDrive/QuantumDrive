using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using quantum_drive.Models;

namespace quantum_drive.Services.GoogleDrive;

/// <summary>
/// <see cref="IStorageBackend"/> implementation backed by the Google Drive REST API v3.
///
/// <para>
/// All <c>.qd</c> files are stored inside a single Drive folder whose ID is held in
/// <c>BackendConfig["remote_folder_id"]</c>.  An in-memory <c>name → fileId</c> cache is
/// populated by <see cref="ListFilesAsync"/> and kept up-to-date by
/// <see cref="WriteAsync"/> and <see cref="DeleteAsync"/> so that repeated reads do not
/// incur extra API round-trips.
/// </para>
///
/// <para>
/// Token refresh is handled by the base class (<see cref="CloudStorageBackendBase"/>).
/// Each public method calls <c>EnsureFreshTokenAsync</c> before issuing HTTP requests.
/// </para>
/// </summary>
internal sealed class GoogleDriveStorageBackend : CloudStorageBackendBase
{
    private const string FilesEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string UploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files";

    private readonly string _folderId;

    public GoogleDriveStorageBackend(string vaultId, Dictionary<string, string> config)
        : base(vaultId, config)
    {
        _folderId = config.TryGetValue("remote_folder_id", out var id) ? id
            : throw new InvalidOperationException("BackendConfig is missing 'remote_folder_id'.");
    }

    // ── IStorageBackend ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override async Task<IEnumerable<string>> ListFilesAsync(
        string extension, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        // Google Drive query: files in our folder whose name ends with the extension.
        var query = Uri.EscapeDataString(
            $"'{_folderId}' in parents and trashed = false and mimeType != 'application/vnd.google-apps.folder'");
        var fields = Uri.EscapeDataString("files(id,name)");
        var url = $"{FilesEndpoint}?q={query}&fields={fields}&pageSize=1000";

        using var http = MakeClient();
        var json = await GetStringAsync(http, url, ct);
        var root = ParseJson(json);

        NameToId.Clear();
        var names = new List<string>();

        if (root.TryGetProperty("files", out var filesEl))
        {
            foreach (var file in filesEl.EnumerateArray())
            {
                var name = file.GetProperty("name").GetString() ?? string.Empty;
                var id   = file.GetProperty("id").GetString() ?? string.Empty;
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && id.Length > 0)
                {
                    NameToId[name] = id;
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <inheritdoc/>
    public override async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        var fileId = await ResolveIdAsync(path, ct);

        using var http = MakeClient();
        var response = await http.GetAsync(
            $"{FilesEndpoint}/{fileId}?alt=media", ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new FileNotFoundException($"Remote file not found: {path}");

        response.EnsureSuccessStatusCode();

        // Buffer in memory so the HTTP connection is released promptly.
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

        // Read content first (we need it twice: once for the upload, once for the metadata JSON).
        var data = await ReadAllBytesAsync(content, ct);
        var mimeType = "application/octet-stream";

        if (NameToId.TryGetValue(path, out var existingId))
        {
            // Update existing file — PATCH to the upload endpoint.
            await PatchFileAsync(existingId, data, mimeType, ct);
        }
        else
        {
            // Create new file with the given name inside _folderId.
            var newId = await CreateFileAsync(path, data, mimeType, ct);
            NameToId[path] = newId;
        }
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        if (!NameToId.TryGetValue(path, out var fileId))
        {
            // Try to resolve by listing; no-op if still not found.
            try { fileId = await ResolveIdAsync(path, ct); }
            catch { return; }
        }

        using var http = MakeClient();
        var response = await http.DeleteAsync($"{FilesEndpoint}/{fileId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
        NameToId.TryRemove(path, out _);
    }

    /// <inheritdoc/>
    public override async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        if (NameToId.ContainsKey(path)) return true;

        try { await ResolveIdAsync(path, ct); return true; }
        catch { return false; }
    }

    // ── Token refresh ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override async Task RefreshTokenAsync(CancellationToken ct)
    {
        const string tokenEndpoint = "https://oauth2.googleapis.com/token";

        if (!Config.TryGetValue("refresh_token", out var refreshToken) ||
            string.IsNullOrEmpty(refreshToken))
            throw new InvalidOperationException("No refresh_token in BackendConfig.");

        var clientId     = Config.TryGetValue("client_id", out var cid) ? cid : GoogleDriveStorageBackendFactory.ClientId;
        var clientSecret = Config.TryGetValue("client_secret", out var cs)  ? cs  : GoogleDriveStorageBackendFactory.ClientSecret;

        var body = await OAuthLoopbackHelper.ExchangeCodeForTokensAsync(tokenEndpoint,
            new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"]     = clientId,
                ["client_secret"] = clientSecret,
            }, ct);

        ApplyTokenResponse(body);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private HttpClient MakeClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AccessToken);
        return http;
    }

    private static async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Google Drive API error ({(int)response.StatusCode}): {body}");
        return body;
    }

    /// <summary>
    /// Returns the Drive file ID for <paramref name="name"/>, searching the remote folder if
    /// not cached. Throws <see cref="FileNotFoundException"/> when the file does not exist.
    /// </summary>
    private async Task<string> ResolveIdAsync(string name, CancellationToken ct)
    {
        if (NameToId.TryGetValue(name, out var cached)) return cached;

        var query  = Uri.EscapeDataString($"'{_folderId}' in parents and name = '{name}' and trashed = false");
        var fields = Uri.EscapeDataString("files(id)");
        using var http = MakeClient();
        var json   = await GetStringAsync(http, $"{FilesEndpoint}?q={query}&fields={fields}", ct);
        var root   = ParseJson(json);

        if (root.TryGetProperty("files", out var files) &&
            files.GetArrayLength() > 0 &&
            files[0].TryGetProperty("id", out var idEl))
        {
            var id = idEl.GetString()!;
            NameToId[name] = id;
            return id;
        }

        throw new FileNotFoundException($"Remote file not found: {name}");
    }

    /// <summary>Multipart upload — creates a new file in the vault folder.</summary>
    private async Task<string> CreateFileAsync(
        string name, byte[] data, string mimeType, CancellationToken ct)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            name,
            parents = new[] { _folderId }
        });

        using var http = MakeClient();
        using var form = BuildMultipartContent(metadata, data, mimeType);
        var response = await http.PostAsync($"{UploadEndpoint}?uploadType=multipart&fields=id", form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        return ReadString(body, "id")
               ?? throw new IOException("Google Drive did not return a file ID after upload.");
    }

    /// <summary>Multipart PATCH — replaces the content of an existing file.</summary>
    private async Task PatchFileAsync(
        string fileId, byte[] data, string mimeType, CancellationToken ct)
    {
        using var http = MakeClient();
        using var form = BuildMultipartContent("{}", data, mimeType);
        var response = await http.PatchAsync(
            $"{UploadEndpoint}/{fileId}?uploadType=multipart", form, ct);
        response.EnsureSuccessStatusCode();
    }

    private static MultipartContent BuildMultipartContent(
        string metadataJson, byte[] data, string mimeType)
    {
        var form = new MultipartContent("related");
        form.Add(new StringContent(metadataJson, Encoding.UTF8, "application/json"));
        var binaryPart = new ByteArrayContent(data);
        binaryPart.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(binaryPart);
        return form;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        if (stream is MemoryStream ms) return ms.ToArray();
        using var buf = new MemoryStream();
        await stream.CopyToAsync(buf, ct);
        return buf.ToArray();
    }

    private void ApplyTokenResponse(string json)
    {
        var root = ParseJson(json);

        if (root.TryGetProperty("access_token", out var at))
            Config["access_token"] = at.GetString()!;

        if (root.TryGetProperty("expires_in", out var exp))
            Config["token_expiry"] = DateTime.UtcNow.AddSeconds(exp.GetInt32()).ToString("O");

        // refresh_token is only returned on first authorization; keep the existing one if absent.
        if (root.TryGetProperty("refresh_token", out var rt))
            Config["refresh_token"] = rt.GetString()!;
    }
}
