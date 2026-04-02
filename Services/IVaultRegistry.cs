using quantum_drive.Models;

namespace quantum_drive.Services;

public interface IVaultRegistry
{
    IReadOnlyList<VaultDescriptor> Vaults { get; }
    IReadOnlyList<VaultContext> UnlockedVaults { get; }
    bool HasAnyVault { get; }
    /// <summary>True when the free-tier vault limit has been reached.</summary>
    bool IsAtVaultLimit { get; }

    Task<VaultDescriptor> RegisterNewVaultAsync(
        string name,
        string folderPath,
        string password,
        string? hint = null,
        string backendId = "local",
        Dictionary<string, string>? backendConfig = null);
    Task<VaultDescriptor> RegisterExistingVaultAsync(string name, string folderPath, string password);
    Task RemoveVaultAsync(string vaultId);

    Task<bool> UnlockVaultAsync(string vaultId, string password);
    void LockVault(string vaultId);
    void LockAll();

    VaultContext? GetContext(string vaultId);
    VaultContext? GetContextByName(string name);

    /// <summary>
    /// Attempts to unlock all registered vaults using passwords cached in the Windows Credential Manager.
    /// Call on app startup before auto-mounting.
    /// </summary>
    Task TryAutoUnlockAllAsync();
}
