namespace quantum_drive.Services;

public interface ILicenseService
{
    /// <summary>True for any paid tier (pro_individual or pro_business). Gates vault count.</summary>
    bool IsProLicenseActive { get; }

    /// <summary>True only for pro_business tier. Indicates commercial use license.</summary>
    bool IsCommercialLicense { get; }

    /// <summary>Loads and verifies the license file from disk. Call once at app startup.</summary>
    void Load();
}
