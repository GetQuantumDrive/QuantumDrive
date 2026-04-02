using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using quantum_drive.Models;

namespace quantum_drive.Services.Dropbox;

/// <summary>
/// <see cref="IStorageBackend"/> implementation backed by the Dropbox API v2.
///
/// <para>
/// All <c>.qd</c> files are stored at <c>/QuantumDrive/{vaultId}/</c> in the user's
/// Dropbox.  Dropbox uses full paths rather than opaque IDs for most operations, so no
/// remote folder ID is needed.  An in-memory <c>name → rev</c> cache is populated by
/// <see cref="ListFilesAsync"/> and used by <see cref="WriteAsync"/> (Dropbox requires the
/// current revision for updates) and <see cref="DeleteAsync"/>.
/// </para>
///
/// <para>
/// Token refresh is handled by the base class (<see cref="CloudStorageBackendBase"/>).
/// </para>
/// </summary>
internal sealed class DropboxStorageBackend : CloudStorageBackendBase
{
    private const string ContentBase = "https://content.dropboxapi.com/2";
    private const string ApiBase     = "https://api.dropboxapi.com/2";

    /// <summary>Maps file name (e.g. <c>doc.pdf.qd</c>) to its current Dropbox rev string.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>
        _nameToRev = new(StringComparer.Ordinal);

    private readonly string _vaultPath; // e.g. /QuantumDrive/a1b2c3d4

    public DropboxStorageBackend(string vaultId, Dictionary<string, string> config)
        : base(vaultId, config)
    {
        _vaultPath = $"/QuantumDrive/{vaultId}";
    }

    // ── IStorageBackend ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override async Task<IEnumerable<string>> ListFilesAsync(
        string extension, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        using var http = MakeClient();
        var body = JsonSerializer.Serialize(new
        {
            path = _vaultPath,
            recursive = false,
            include_media_info = false,
            include_deleted = false,
            include_has_explicit_shared_members = false,
        });

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(
                $"{ApiBase}/files/list_folder",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
        }
        catch
        {
            // Folder does not exist yet — return empty.
            return Enumerable.Empty<string>();
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict ||
            !response.IsSuccessStatusCode)
        {
            // 409 = path not found (folder not created yet) — treat as empty.
            var err = await response.Content.ReadAsStringAsync(ct);
            if (err.Contains("path/not_found", StringComparison.OrdinalIgnoreCase))
                return Enumerable.Empty<string>();
            throw new IOException($"Dropbox list_folder error: {err}");
        }

        var json  = await response.Content.ReadAsStringAsync(ct);
        var root  = ParseJson(json);
        var names = new List<string>();

        NameToId.Clear();
        _nameToRev.Clear();

        if (root.TryGetProperty("entries", out var entries))
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty(".tag", out var tag) ||
                    tag.GetString() != "file") continue;

                var name = entry.GetProperty("name").GetString() ?? string.Empty;
                var rev  = entry.TryGetProperty("rev", out var r) ? r.GetString() ?? "" : "";

                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                    _nameToRev[name] = rev;
                    // NameToId stores the full Dropbox path (used by some helpers).
                    NameToId[name] = $"{_vaultPath}/{name}";
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
        var dropboxPath = $"{_vaultPath}/{path}";

        // Pass the path via the Dropbox-API-Arg header (required for /download).
        var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentBase}/files/download");
        request.Headers.Add("Dropbox-API-Arg",
            JsonSerializer.Serialize(new { path = dropboxPath }));
        request.Content = new StringContent(string.Empty);

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict ||
            response.StatusCode == System.Net.HttpStatusCode.NotFound)
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

        var dropboxPath = $"{_vaultPath}/{path}";
        var data = await ReadAllBytesAsync(content, ct);

        // "add" creates; "overwrite" replaces — use overwrite mode always.
        var apiArg = new
        {
            path        = dropboxPath,
            mode        = "overwrite",
            autorename  = false,
            mute        = false,
        };

        using var http = MakeClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentBase}/files/upload");
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(apiArg));
        request.Content = new ByteArrayContent(data);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        // Update rev cache.
        var rev = OAuthLoopbackHelper.ReadStringPublic(body, "rev");
        if (rev is not null) _nameToRev[path] = rev;
        NameToId[path] = dropboxPath;
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        var dropboxPath = $"{_vaultPath}/{path}";
        using var http = MakeClient();

        var body = JsonSerializer.Serialize(new { path = dropboxPath });
        var response = await http.PostAsync(
            $"{ApiBase}/files/delete_v2",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            if (err.Contains("path_lookup/not_found", StringComparison.OrdinalIgnoreCase))
                return; // Already gone — no-op.
        }

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new IOException($"Dropbox delete error: {err}");
        }

        NameToId.TryRemove(path, out _);
        _nameToRev.TryRemove(path, out _);
    }

    /// <inheritdoc/>
    public override async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        if (NameToId.ContainsKey(path)) return true;

        var dropboxPath = $"{_vaultPath}/{path}";
        using var http = MakeClient();

        var body = JsonSerializer.Serialize(new { path = dropboxPath });
        var response = await http.PostAsync(
            $"{ApiBase}/files/get_metadata",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) return false;
        return response.IsSuccessStatusCode;
    }

    // ── Token refresh ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override async Task RefreshTokenAsync(CancellationToken ct)
    {
        if (!Config.TryGetValue("refresh_token", out var refreshToken) ||
            string.IsNullOrEmpty(refreshToken))
            throw new InvalidOperationException("No refresh_token in BackendConfig.");

        var body = await OAuthLoopbackHelper.ExchangeCodeForTokensAsync(
            "https://api.dropboxapi.com/oauth2/token",
            new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"]     = DropboxStorageBackendFactory.ClientId,
            }, ct);

        var root = ParseJson(body);
        if (root.TryGetProperty("access_token", out var at))
            Config["access_token"] = at.GetString()!;
        if (root.TryGetProperty("expires_in", out var exp))
            Config["token_expiry"] = DateTime.UtcNow.AddSeconds(exp.GetInt32()).ToString("O");
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
