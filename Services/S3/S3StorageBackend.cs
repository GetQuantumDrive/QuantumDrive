using System.Diagnostics;
using System.Xml.Linq;
using quantum_drive.Models;

namespace quantum_drive.Services.S3;

/// <summary>
/// <see cref="IStorageBackend"/> implementation backed by any S3-compatible object store
/// (Scaleway, Hetzner, Cloudflare R2, Wasabi, AWS S3, MinIO, …).
///
/// <para>
/// <b>URL style:</b> path-style only —
/// <c>https://{endpoint}/{bucket}/QuantumDrive/{vaultId}/{filename}.qd</c>.
/// Virtual-hosted-style (subdomain) URLs are not used because several providers
/// require explicit opt-in or do not support them for custom endpoints.
/// </para>
///
/// <para>
/// <b>BackendConfig keys consumed:</b>
/// <list type="table">
/// <item><term><c>access_key</c></term><description>S3 access key ID.</description></item>
/// <item><term><c>secret_key</c></term><description>S3 secret access key.</description></item>
/// <item><term><c>bucket</c></term><description>Bucket name.</description></item>
/// <item><term><c>region</c></term><description>Region string, e.g. <c>nl-ams</c>.</description></item>
/// <item><term><c>endpoint</c></term><description>Hostname without scheme, e.g. <c>s3.nl-ams.scw.cloud</c>.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Auth:</b> AWS Signature V4 via <see cref="S3Signer"/>. No tokens, no refresh —
/// access keys are permanent until revoked by the user.
/// </para>
///
/// <para>
/// <b>Watch:</b> 30-second polling (same strategy as
/// <c>CloudStorageBackendBase</c>, reproduced here because S3 does not use that base class).
/// </para>
/// </summary>
internal sealed class S3StorageBackend : IStorageBackend
{
    // S3 ListObjectsV2 XML namespace — required for XDocument element lookups.
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string _region;
    private readonly string _bucketUrl;  // https://{endpoint}/{bucket}
    private readonly string _keyPrefix;  // {vaultId}/

    public S3StorageBackend(string vaultId, Dictionary<string, string> config)
    {
        _accessKey = config["access_key"];
        _secretKey = config["secret_key"];
        _region    = config["region"];
        _bucketUrl = $"https://{config["endpoint"]}/{config["bucket"]}";
        _keyPrefix = $"{vaultId}/";
    }

    // ── IStorageBackend ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IEnumerable<string>> ListFilesAsync(
        string extension, CancellationToken ct = default)
    {
        var names = new List<string>();
        string? continuationToken = null;

        do
        {
            var url = $"{_bucketUrl}?list-type=2&prefix={Uri.EscapeDataString(_keyPrefix)}";
            if (continuationToken is not null)
                url += $"&continuation-token={Uri.EscapeDataString(continuationToken)}";

            using var http     = new HttpClient();
            using var request  = new HttpRequestMessage(HttpMethod.Get, url);
            S3Signer.Sign(request, _accessKey, _secretKey, _region, bodyBytes: []);

            var response = await http.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new IOException("S3: access denied — verify your access key and secret key.");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return names; // Bucket not found — treat as empty vault.

            response.EnsureSuccessStatusCode();

            var xml  = await response.Content.ReadAsStringAsync(ct);
            var doc  = XDocument.Parse(xml);

            foreach (var keyEl in doc.Descendants(S3Ns + "Key"))
            {
                var key = keyEl.Value;
                if (!key.StartsWith(_keyPrefix, StringComparison.Ordinal)) continue;
                var name = key[_keyPrefix.Length..]; // strip vault prefix
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }

            var isTruncated = doc.Descendants(S3Ns + "IsTruncated")
                                 .FirstOrDefault()?.Value;
            continuationToken = string.Equals(isTruncated, "true", StringComparison.OrdinalIgnoreCase)
                ? doc.Descendants(S3Ns + "NextContinuationToken").FirstOrDefault()?.Value
                : null;

        } while (continuationToken is not null);

        return names;
    }

    /// <inheritdoc/>
    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var url = ObjectUrl(path);
        using var http    = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        S3Signer.Sign(request, _accessKey, _secretKey, _region, bodyBytes: []);

        var response = await http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new FileNotFoundException($"S3 object not found: {_keyPrefix}{path}");
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new IOException($"S3: access denied reading {path}.");

        response.EnsureSuccessStatusCode();

        var ms = new MemoryStream();
        await response.Content.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string path, Stream content, CancellationToken ct = default)
    {
        var data = await ReadAllBytesAsync(content, ct);
        var url  = ObjectUrl(path);

        using var http    = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(data)
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        S3Signer.Sign(request, _accessKey, _secretKey, _region, bodyBytes: data);

        var response = await http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new IOException($"S3: access denied writing {path}.");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new IOException($"S3: bucket not found. Verify the bucket name.");

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    /// <remarks>S3 DELETE returns 204 even for keys that do not exist — no-op is free.</remarks>
    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var url = ObjectUrl(path);
        using var http    = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        S3Signer.Sign(request, _accessKey, _secretKey, _region, bodyBytes: []);

        var response = await http.SendAsync(request, ct);

        if (response.StatusCode is System.Net.HttpStatusCode.NoContent
                                or System.Net.HttpStatusCode.NotFound)
            return; // Both are success for delete.

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new IOException($"S3: access denied deleting {path}.");

        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var url = ObjectUrl(path);
        using var http    = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        S3Signer.Sign(request, _accessKey, _secretKey, _region, bodyBytes: []);

        var response = await http.SendAsync(request, ct);
        return response.StatusCode == System.Net.HttpStatusCode.OK;
    }

    // ── Path helpers ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string GetQdPath(string virtualName) => $"{virtualName}.qd";

    /// <inheritdoc/>
    public string GetQdPath(string virtualName, int counter) => $"{virtualName} ({counter}).qd";

    // ── Watch (30-second polling) ─────────────────────────────────────────────

    /// <inheritdoc/>
    public IDisposable Watch(Action<StorageBackendChangeEvent> onChange, Action? onError = null)
    {
        var cts = new CancellationTokenSource();
        _ = PollAsync(onChange, onError, cts.Token);
        return new CtsDisposable(cts);
    }

    private async Task PollAsync(
        Action<StorageBackendChangeEvent> onChange,
        Action? onError,
        CancellationToken ct)
    {
        try
        {
            var known = (await ListFilesAsync(".qd", ct)).ToHashSet(StringComparer.Ordinal);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                var current = (await ListFilesAsync(".qd", ct)).ToHashSet(StringComparer.Ordinal);

                foreach (var added in current.Except(known))
                    onChange(new StorageBackendChangeEvent(StorageChangeType.Created, added));
                foreach (var removed in known.Except(current))
                    onChange(new StorageBackendChangeEvent(StorageChangeType.Deleted, removed));

                known = current;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[S3StorageBackend] Watch error: {ex.Message}");
            onError?.Invoke();
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose() { }

    // ── Private helpers ────────────────────────────────────────────────────────

    private string ObjectUrl(string filename)
    {
        var key = _keyPrefix + filename;
        return $"{_bucketUrl}/{Uri.EscapeDataString(key)}";
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        if (stream is MemoryStream ms) return ms.ToArray();
        using var buf = new MemoryStream();
        await stream.CopyToAsync(buf, ct);
        return buf.ToArray();
    }

    private sealed class CtsDisposable(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose() => cts.Cancel();
    }
}
