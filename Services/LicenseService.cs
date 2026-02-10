using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Windows.Storage;

namespace quantum_drive.Services;

public class LicenseService : ILicenseService
{
    // Ed25519 public key (only the generator holds the private key)
    private static readonly byte[] Ed25519PublicKey = Convert.FromBase64String(
        "EGFJVitzqFPxeOK6hCNyf9+BZTX40BpoM1EEhqft9qo=");

    private bool _isPro;

    public bool IsPro => _isPro;

    public int FileLimit => _isPro ? int.MaxValue : 25;

    public LicenseService()
    {
        // Restore saved license on startup
        var settings = ApplicationData.Current.LocalSettings;
        if (settings.Values["LicenseKey"] is string savedKey)
            _isPro = ValidateKey(savedKey);
    }

    public bool VerifyLicense(string licenseKey)
    {
        if (!ValidateKey(licenseKey))
            return false;

        _isPro = true;

        // Persist activation
        var settings = ApplicationData.Current.LocalSettings;
        settings.Values["LicenseKey"] = licenseKey;
        return true;
    }

    public string GetLicenseTier() => _isPro ? "Pro" : "Free";

    private static bool ValidateKey(string key)
    {
        // Format: QDPRO-XXXXX-XXXXX-...-XXXXX (base32-encoded 4-byte serial + 64-byte Ed25519 signature)
        if (string.IsNullOrEmpty(key))
            return false;

        try
        {
            // Strip prefix and dashes
            var stripped = key.Replace("-", "");
            if (!stripped.StartsWith("QDPRO"))
                return false;

            stripped = stripped["QDPRO".Length..];
            var rawBytes = Base32Decode(stripped);

            if (rawBytes.Length != 68) // 4 serial + 64 signature
                return false;

            var serial = rawBytes[..4];
            var signature = rawBytes[4..];

            // Verify Ed25519 signature over the serial bytes
            var pubKeyParams = new Ed25519PublicKeyParameters(Ed25519PublicKey);
            var verifier = new Ed25519Signer();
            verifier.Init(false, pubKeyParams);
            verifier.BlockUpdate(serial, 0, serial.Length);
            return verifier.VerifySignature(signature);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').ToUpperInvariant();

        int bitBuffer = 0, bitsInBuffer = 0;
        var output = new List<byte>();

        foreach (var c in input)
        {
            int val = alphabet.IndexOf(c);
            if (val < 0) continue;

            bitBuffer = (bitBuffer << 5) | val;
            bitsInBuffer += 5;

            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                output.Add((byte)(bitBuffer >> bitsInBuffer));
                bitBuffer &= (1 << bitsInBuffer) - 1;
            }
        }

        return output.ToArray();
    }
}
