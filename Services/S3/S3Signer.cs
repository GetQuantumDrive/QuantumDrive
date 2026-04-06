using System.Security.Cryptography;
using System.Text;

namespace quantum_drive.Services.S3;

/// <summary>
/// Stateless helper that signs <see cref="HttpRequestMessage"/> instances with
/// AWS Signature Version 4 (HMAC-SHA256). Compatible with any S3-compatible endpoint
/// (Scaleway, Hetzner, Cloudflare R2, Wasabi, AWS, MinIO, etc.).
///
/// <para>
/// Call <see cref="Sign"/> on a freshly constructed request <em>before</em> sending it.
/// The method mutates the request by adding three headers:
/// <c>Authorization</c>, <c>x-amz-date</c>, and <c>x-amz-content-sha256</c>.
/// </para>
///
/// <para>
/// Always uses path-style URLs (<c>https://{endpoint}/{bucket}/{key}</c>), which are
/// supported by all S3-compatible providers including those that do not support
/// virtual-hosted-style subdomain URLs.
/// </para>
/// </summary>
internal static class S3Signer
{
    private const string Algorithm  = "AWS4-HMAC-SHA256";
    private const string Service    = "s3";
    private const string Terminator = "aws4_request";

    // Signed headers in alphabetical order (required by SigV4 spec).
    private const string SignedHeaders = "host;x-amz-content-sha256;x-amz-date";

    /// <summary>
    /// Signs <paramref name="request"/> in-place with AWS Signature V4.
    /// </summary>
    /// <param name="request">
    /// Outgoing request. <c>RequestUri</c> must already be set and must use an absolute
    /// HTTPS URL. Do not set the body content before calling this method; pass the raw
    /// bytes via <paramref name="bodyBytes"/> instead so the hash is computed correctly.
    /// </param>
    /// <param name="accessKey">S3 access key ID.</param>
    /// <param name="secretKey">S3 secret access key.</param>
    /// <param name="region">Region string, e.g. <c>"nl-ams"</c> or <c>"eu-central-1"</c>.</param>
    /// <param name="bodyBytes">
    /// Raw request body bytes. Pass an empty array for GET / HEAD / DELETE requests.
    /// </param>
    public static void Sign(
        HttpRequestMessage request,
        string accessKey,
        string secretKey,
        string region,
        byte[] bodyBytes)
    {
        var now         = DateTime.UtcNow;
        var dateStamp   = now.ToString("yyyyMMdd");
        var amzDateTime = now.ToString("yyyyMMddTHHmmssZ");
        var bodyHash    = HexSha256(bodyBytes);
        var host        = request.RequestUri!.Host;

        // Add the two mandatory AWS headers before building the canonical request.
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDateTime);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", bodyHash);
        // HttpClient sets Host automatically on send, but we need its value now for
        // the canonical headers. TryAddWithoutValidation avoids InvalidOperationException.
        request.Headers.TryAddWithoutValidation("Host", host);

        // ── Step 1: Canonical request ─────────────────────────────────────────
        var canonicalUri = CanonicalUri(request.RequestUri);
        var canonicalQs  = CanonicalQueryString(request.RequestUri);
        var canonicalHeaders =
            $"host:{host}\n" +
            $"x-amz-content-sha256:{bodyHash}\n" +
            $"x-amz-date:{amzDateTime}\n";

        var canonicalRequest =
            $"{request.Method.Method}\n" +
            $"{canonicalUri}\n" +
            $"{canonicalQs}\n" +
            $"{canonicalHeaders}\n" +
            $"{SignedHeaders}\n" +
            $"{bodyHash}";

        // ── Step 2: String to sign ────────────────────────────────────────────
        var credentialScope = $"{dateStamp}/{region}/{Service}/{Terminator}";
        var stringToSign =
            $"{Algorithm}\n" +
            $"{amzDateTime}\n" +
            $"{credentialScope}\n" +
            $"{HexSha256(Encoding.UTF8.GetBytes(canonicalRequest))}";

        // ── Step 3: Signing key (four nested HMACs) ───────────────────────────
        var signingKey = DeriveSigningKey(secretKey, dateStamp, region);

        // ── Step 4: Signature ─────────────────────────────────────────────────
        var signature = HexHmac(signingKey, stringToSign);

        // ── Step 5: Authorization header ──────────────────────────────────────
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{Algorithm} Credential={accessKey}/{credentialScope}, " +
            $"SignedHeaders={SignedHeaders}, Signature={signature}");
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the URI path component with each segment percent-encoded per SigV4 rules
    /// (unreserved characters left as-is; everything else double-encoded if already encoded).
    /// A bare slash is never encoded.
    /// </summary>
    private static string CanonicalUri(Uri uri)
    {
        // Split on '/', encode each segment, rejoin.
        var segments = uri.AbsolutePath.Split('/');
        return string.Join("/", segments.Select(s =>
            Uri.EscapeDataString(Uri.UnescapeDataString(s))));
    }

    /// <summary>
    /// Returns the canonical query string: key=value pairs sorted by key then value,
    /// both percent-encoded, joined by '&amp;'. Returns an empty string for requests
    /// with no query parameters.
    /// </summary>
    private static string CanonicalQueryString(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query)) return string.Empty;

        var pairs = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var eq = p.IndexOf('=');
                var k  = eq >= 0 ? p[..eq]      : p;
                var v  = eq >= 0 ? p[(eq + 1)..] : string.Empty;
                return (K: Uri.EscapeDataString(Uri.UnescapeDataString(k)),
                        V: Uri.EscapeDataString(Uri.UnescapeDataString(v)));
            })
            .OrderBy(p => p.K, StringComparer.Ordinal)
            .ThenBy(p  => p.V, StringComparer.Ordinal);

        return string.Join("&", pairs.Select(p => $"{p.K}={p.V}"));
    }

    /// <summary>
    /// Derives the SigV4 signing key via four nested HMAC-SHA256 operations:
    /// <c>HMAC("AWS4" + secretKey, date) → HMAC(·, region) → HMAC(·, "s3") → HMAC(·, "aws4_request")</c>.
    /// </summary>
    private static byte[] DeriveSigningKey(string secretKey, string dateStamp, string region)
    {
        var kDate    = HmacBytes(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var kRegion  = HmacBytes(kDate,    region);
        var kService = HmacBytes(kRegion,  Service);
        return HmacBytes(kService, Terminator);
    }

    private static string HexSha256(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string HexHmac(byte[] key, string data)
        => Convert.ToHexString(HmacBytes(key, data)).ToLowerInvariant();

    private static byte[] HmacBytes(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}
