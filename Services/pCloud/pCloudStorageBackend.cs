using System.Net.Http.Headers;
using quantum_drive.Models;

namespace quantum_drive.Services.PCloud;

/// <summary>
/// <see cref="IStorageBackend"/> implementation backed by the pCloud API v1.
///
/// <para>
/// All <c>.qd</c> files are stored at <c>/QuantumDrive/{vaultId}/{filename}.qd</c>
/// in the user's pCloud drive.  An in-memory <c>name → fileId</c> cache is populated
/// by <see cref="ListFilesAsync"/> for faster subsequent lookups.
/// </para>
///
/// <para>
/// pCloud access tokens are long-lived and do not expire, so token refresh is a no-op.
/// The API host (<c>api.pcloud.com</c> for global/US, <c>eapi.pcloud.com</c> for EU)
/// is determined during authorization and stored in <c>BackendConfig["api_host"]</c>.
/// </para>
/// </summary>
internal sealed class PCloudStorageBackend : CloudStorageBackendBase
{
    private readonly string _apiBase;
    private readonly string _vaultPath;  // e.g. /QuantumDrive/a1b2c3d4

    public PCloudStorageBackend(string vaultId, Dictionary<string, string> config)
        : base(vaultId, config)
    {
        _apiBase   = config.TryGetValue("api_host", out var host) && !string.IsNullOrEmpty(host)
            ? $"https://{host}"
            : PCloudStorageBackendFactory.DefaultApiBase;
        _vaultPath = $"/QuantumDrive/{vaultId}";
    }

    // ── IStorageBackend ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override async Task<IEnumerable<string>> ListFilesAsync(
        string extension, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        using var http = MakeClient();
        var url = $"{_apiBase}/listfolder?path={Uri.EscapeDataString(_vaultPath)}&nofiles=0";

        var response = await http.GetAsync(url, ct);
        var json     = await response.Content.ReadAsStringAsync(ct);
        var root     = ParseJson(json);

        if (root.TryGetProperty("result", out var resultEl))
        {
            var code = resultEl.GetInt32();
            if (code == 2005) return Enumerable.Empty<string>(); // Folder not found
            if (code != 0)   throw new IOException($"pCloud list error: {json}");
        }

        var names = new List<string>();
        NameToId.Clear();

        if (root.TryGetProperty("metadata", out var metadata) &&
            metadata.TryGetProperty("contents", out var contents))
        {
            foreach (var item in contents.EnumerateArray())
            {
                if (item.TryGetProperty("isdir", out var isDir) && isDir.GetBoolean()) continue;

                var name = item.GetProperty("name").GetString() ?? string.Empty;
                if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;

                names.Add(name);
                if (item.TryGetProperty("fileid", out var fidEl))
                    NameToId[name] = fidEl.GetInt64().ToString();
            }
        }

        return names;
    }

    /// <inheritdoc/>
    public override async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        // Step 1: resolve a short-lived download link.
        string linkUrl = NameToId.TryGetValue(path, out var fileId) && fileId.Length > 0
            ? $"{_apiBase}/getfilelink?fileid={fileId}"
            : $"{_apiBase}/getfilelink?path={Uri.EscapeDataString($"{_vaultPath}/{path}")}";

        using var http     = MakeClient();
        var linkResp       = await http.GetAsync(linkUrl, ct);
        var linkJson       = await linkResp.Content.ReadAsStringAsync(ct);
        var linkRoot       = ParseJson(linkJson);

        if (linkRoot.TryGetProperty("result", out var r) && r.GetInt32() == 2009)
            throw new FileNotFoundException($"Remote file not found: {path}");

        var dlHost = linkRoot.GetProperty("hosts").EnumerateArray().First().GetString()!;
        var dlPath = linkRoot.GetProperty("path").GetString()!;

        // Step 2: download the content from the resolved URL (no auth header needed).
        using var dlHttp = new HttpClient();
        var dlResp       = await dlHttp.GetAsync($"https://{dlHost}{dlPath}", ct);
        dlResp.EnsureSuccessStatusCode();

        var ms = new MemoryStream();
        await dlResp.Content.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    /// <inheritdoc/>
    public override async Task WriteAsync(
        string path, Stream content, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        var data = await ReadAllBytesAsync(content, ct);
        var url  = $"{_apiBase}/uploadfile" +
                   $"?path={Uri.EscapeDataString(_vaultPath)}" +
                   $"&filename={Uri.EscapeDataString(path)}" +
                   $"&nopartial=1";

        using var http      = MakeClient();
        using var formData  = new MultipartFormDataContent();
        formData.Add(new ByteArrayContent(data), "file", path);

        var response = await http.PostAsync(url, formData, ct);
        var body     = await response.Content.ReadAsStringAsync(ct);
        var root     = ParseJson(body);

        if (root.TryGetProperty("result", out var r) && r.GetInt32() != 0)
            throw new IOException($"pCloud write error: {body}");

        // Cache the returned file ID.
        if (root.TryGetProperty("fileids", out var fileids) && fileids.GetArrayLength() > 0)
            NameToId[path] = fileids[0].GetInt64().ToString();
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        string url = NameToId.TryGetValue(path, out var fileId) && fileId.Length > 0
            ? $"{_apiBase}/deletefile?fileid={fileId}"
            : $"{_apiBase}/deletefile?path={Uri.EscapeDataString($"{_vaultPath}/{path}")}";

        using var http = MakeClient();
        var response   = await http.GetAsync(url, ct);
        var body       = await response.Content.ReadAsStringAsync(ct);
        var root       = ParseJson(body);

        if (root.TryGetProperty("result", out var r))
        {
            var code = r.GetInt32();
            if (code == 2009) return; // Already gone
            if (code != 0)   throw new IOException($"pCloud delete error: {body}");
        }

        NameToId.TryRemove(path, out _);
    }

    /// <inheritdoc/>
    public override async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);

        if (NameToId.ContainsKey(path)) return true;

        using var http = MakeClient();
        var url        = $"{_apiBase}/stat?path={Uri.EscapeDataString($"{_vaultPath}/{path}")}";
        var response   = await http.GetAsync(url, ct);
        var body       = await response.Content.ReadAsStringAsync(ct);
        var root       = ParseJson(body);

        return root.TryGetProperty("result", out var r) && r.GetInt32() == 0;
    }

    // ── Token refresh ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override Task RefreshTokenAsync(CancellationToken ct)
    {
        // pCloud access tokens are long-lived and do not expire.
        // Reset the sentinel so EnsureFreshTokenAsync stays dormant.
        Config["token_expiry"] = DateTime.UtcNow.AddYears(10).ToString("O");
        return Task.CompletedTask;
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
