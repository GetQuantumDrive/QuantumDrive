using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using quantum_drive.Models;

namespace quantum_drive.Services;

public sealed class LicenseService : ILicenseService
{
    // Ed25519 public key (32 bytes, base64).
    // Replace with the actual signing key before release.
    // Generate with: openssl genpkey -algorithm ed25519 | openssl pkey -pubout -outform DER | tail -c 32 | base64
    private static readonly byte[] PublicKey = Convert.FromBase64String(
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

    private static readonly string LicensePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuantumDrive", "license.qdlic");

    public bool IsProLicenseActive { get; private set; }
    public bool IsCommercialLicense { get; private set; }

    public void Load()
    {
        IsProLicenseActive = false;
        IsCommercialLicense = false;

        if (!File.Exists(LicensePath))
            return;

        try
        {
            var json = File.ReadAllText(LicensePath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var info = JsonSerializer.Deserialize<LicenseInfo>(json, options);
            if (info is null) return;

            // Check expiry
            if (info.ExpiresAt.HasValue && info.ExpiresAt.Value < DateTime.UtcNow)
            {
                Debug.WriteLine("License expired.");
                return;
            }

            // Verify Ed25519 signature
            var expiresStr = info.ExpiresAt?.ToString("O") ?? string.Empty;
            var payload = $"{info.Email}|{info.IssuedAt:O}|{expiresStr}";
            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            var sigBytes = Convert.FromBase64String(info.Sig);

            var publicKeyParams = new Ed25519PublicKeyParameters(PublicKey, 0);
            var signer = new Ed25519Signer();
            signer.Init(false, publicKeyParams);
            signer.BlockUpdate(payloadBytes, 0, payloadBytes.Length);

            if (!signer.VerifySignature(sigBytes))
            {
                Debug.WriteLine("License signature invalid.");
                return;
            }

            IsProLicenseActive = true;
            IsCommercialLicense = string.Equals(
                info.Tier, "pro_business", StringComparison.OrdinalIgnoreCase);

            Debug.WriteLine($"License loaded: {info.Tier} for {info.Email}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"License load failed: {ex.Message}");
        }
    }
}
