using quantum_drive.Models;

namespace quantum_drive.Services;

public interface IVaultRegistry
{
    IReadOnlyList<VaultDescriptor> Vaults { get; }
    IReadOnlyList<VaultContext> UnlockedVaults { get; }
    bool HasAnyVault { get; }

    Task<VaultDescriptor> RegisterNewVaultAsync(string name, string folderPath, string password, string? hint = null);
    Task<VaultDescriptor> RegisterExistingVaultAsync(string name, string folderPath, string password);
    Task RemoveVaultAsync(string vaultId);

    Task<bool> UnlockVaultAsync(string vaultId, string password);
    void LockVault(string vaultId);
    void LockAll();

    VaultContext? GetContext(string vaultId);
    VaultContext? GetContextByName(string name);
}
