namespace quantum_drive.Services;

public interface ILicenseService
{
    bool IsPro { get; }
    int FileLimit { get; }
    bool VerifyLicense(string licenseKey);
    string GetLicenseTier();
}
