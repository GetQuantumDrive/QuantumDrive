namespace quantum_drive.Models;

public class LicenseInfo
{
    public string Email { get; set; } = string.Empty;
    /// <summary>"pro_individual" or "pro_business"</summary>
    public string Tier { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    /// <summary>Base64-encoded Ed25519 signature over "{Email}|{IssuedAt:O}|{ExpiresAt:O or empty}"</summary>
    public string Sig { get; set; } = string.Empty;
}
