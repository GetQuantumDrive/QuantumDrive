namespace quantum_drive.Services;

public interface IIdentityService
{
    bool IsVaultCreated { get; }
    byte[]? MlKemPrivateKey { get; }
    byte[]? MlKemPublicKey { get; }
    string? PasswordHint { get; }
    string? VaultSaltBase64 { get; }
    Task<(byte[] PublicKey, byte[] PrivateKey, string RecoveryKey)> CreateVaultAsync(string password, string? passwordHint = null);
    Task<bool> UnlockAsync(string password);
    Task<bool> VerifyPasswordAsync(string password);
    Task ChangePasswordAsync(string oldPassword, string newPassword);
    Task<bool> RecoverWithKeyAsync(string recoveryKey, string newPassword);
    Task<string?> GetRecoveryKeyAsync(string password);
}
