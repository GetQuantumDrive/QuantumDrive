using System.Diagnostics;
using Windows.Storage;
using Windows.Storage.Provider;

namespace quantum_drive.Services;

/// <summary>
/// Registers and unregisters Cloud Files API sync roots with Windows.
/// Sync roots appear as cloud provider entries in Explorer's navigation pane.
/// </summary>
public static class SyncRootRegistrar
{
    private const string ProviderId = "QuantumDrive";

    public static string GetSyncRootId(string vaultId) => $"{ProviderId}!{vaultId}";

    public static async Task RegisterAsync(string vaultId, string vaultName, string syncRootPath, string? iconPath = null)
    {
        Directory.CreateDirectory(syncRootPath);

        var storageFolder = await StorageFolder.GetFolderFromPathAsync(syncRootPath);

        var info = new StorageProviderSyncRootInfo
        {
            Id = GetSyncRootId(vaultId),
            Path = storageFolder,
            DisplayNameResource = vaultName,
            Version = "1.0",
            RecycleBinUri = null,
            AllowPinning = false,
            ShowSiblingsAsGroup = false,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy =
                StorageProviderInSyncPolicy.FileCreationTime |
                StorageProviderInSyncPolicy.DirectoryCreationTime,
            IconResource = iconPath != null && File.Exists(iconPath)
                ? iconPath
                : "%SystemRoot%\\System32\\shell32.dll,-44",
        };

        try
        {
            // Unregister first to clear any cached display name / icon
            try { StorageProviderSyncRootManager.Unregister(GetSyncRootId(vaultId)); }
            catch { /* may not exist yet */ }

            StorageProviderSyncRootManager.Register(info);
            Debug.WriteLine($"Sync root registered: {vaultName} at {syncRootPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Sync root registration failed for '{vaultName}': {ex} (HRESULT: 0x{ex.HResult:X8})");
            throw;
        }
    }

    public static void Unregister(string vaultId)
    {
        try
        {
            var syncRootId = GetSyncRootId(vaultId);
            StorageProviderSyncRootManager.Unregister(syncRootId);
            Debug.WriteLine($"Sync root unregistered: {vaultId}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Sync root unregister failed: {ex.Message}");
        }
    }

    public static bool IsRegistered(string vaultId)
    {
        try
        {
            var roots = StorageProviderSyncRootManager.GetCurrentSyncRoots();
            var syncRootId = GetSyncRootId(vaultId);
            foreach (var root in roots)
            {
                if (root.Id == syncRootId)
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
