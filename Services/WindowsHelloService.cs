using System.Diagnostics;
using Windows.Security.Credentials;

namespace quantum_drive.Services;

/// <summary>
/// Caches vault passwords in the Windows Credential Manager (encrypted via DPAPI / Windows Hello).
/// This is a convenience feature — free for all tiers.
/// </summary>
public sealed class WindowsHelloService
{
    private const string ResourceName = "QuantumDriveVault";

    /// <summary>Saves (or updates) the vault password in the Windows Credential Manager.</summary>
    public void SaveVaultPassword(string vaultId, string password)
    {
        try
        {
            var vault = new PasswordVault();
            // Remove any stale entry first to avoid duplicates
            TryRemove(vault, vaultId);
            vault.Add(new PasswordCredential(ResourceName, vaultId, password));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WindowsHello: SaveVaultPassword failed for {vaultId}: {ex.Message}");
        }
    }

    /// <summary>Returns the cached password for a vault, or null if not stored.</summary>
    public string? LoadVaultPassword(string vaultId)
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(ResourceName, vaultId);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes the cached password for a vault (call on vault deletion or password change).</summary>
    public void DeleteVaultPassword(string vaultId)
    {
        try
        {
            TryRemove(new PasswordVault(), vaultId);
        }
        catch { /* not stored — ignore */ }
    }

    private static void TryRemove(PasswordVault vault, string vaultId)
    {
        try
        {
            var existing = vault.Retrieve(ResourceName, vaultId);
            vault.Remove(existing);
        }
        catch { /* not present */ }
    }
}
