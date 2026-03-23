using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace quantum_drive.Services;

public class VirtualDriveService : IVirtualDriveService
{
    // Use a normal user-profile folder for the sync root — same pattern as
    // OneDrive (C:\Users\bjorn\OneDrive) and Dropbox (C:\Users\bjorn\Dropbox).
    // This avoids MSIX VFS virtualization issues: kernel APIs bypass the VFS layer.
    private static readonly string SyncRootParent = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "QuantumDrive");

    private readonly IVaultRegistry _vaultRegistry;
    private readonly StorageBackendRegistry _backendRegistry;
    private readonly Dictionary<string, CloudSyncProvider> _providers = []; // vaultId → provider
    private readonly Dictionary<string, string> _syncRootPaths = []; // vaultId → sync root folder path
    private readonly SemaphoreSlim _providerLock = new(1, 1); // serialises all _providers/_syncRootPaths mutations

    private bool _isConnected;

    public string? SyncRootPath => _isConnected ? SyncRootParent : null;
    public bool IsEncryptedMode { get; private set; }
    public event Action? FilesChanged;
    public event Action<string>? VaultConnectFailed;

    public VirtualDriveService(IVaultRegistry vaultRegistry, StorageBackendRegistry backendRegistry)
    {
        _vaultRegistry = vaultRegistry;
        _backendRegistry = backendRegistry;
    }

    /// <summary>
    /// Removes stale registry entries and connections left behind if the app crashed while a drive was mounted.
    /// Also cleans up CFAPI sync root registrations that survive uninstall.
    /// </summary>
    public static void CleanupStaleEntries()
    {
        // 0. SECURITY: Remove any plaintext files left by a crash while vaults were unlocked.
        //    Hydrated placeholders contain decrypted data — must be purged before the user can
        //    access them without entering a password.
        CleanupStaleSyncRootFiles();

        // 1. Unregister any QuantumDrive CFAPI sync roots left over from a crash or prior uninstall.
        //    StorageProviderSyncRootManager registrations persist until explicitly removed.
        CleanupStaleSyncRoots();

        // 2. Clean up stale WebDAV connections (from old WebDAV-based virtual drive)
        CleanupStaleWebDavConnections();

        // 3. Clean up stale subst drive mappings (from previous app versions)
        CleanupStaleSubstDrives();

        // 4. Clean up stale CLSID / nav pane entries (from previous app versions)
        CleanupStaleNavPane();
    }

    private static void CleanupStaleSyncRoots()
    {
        try
        {
            var roots = Windows.Storage.Provider.StorageProviderSyncRootManager.GetCurrentSyncRoots();
            foreach (var root in roots)
            {
                if (!root.Id.StartsWith("QuantumDrive!", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    Windows.Storage.Provider.StorageProviderSyncRootManager.Unregister(root.Id);
                    Debug.WriteLine($"Cleaned up stale sync root: {root.Id}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to unregister stale sync root '{root.Id}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Sync root cleanup failed: {ex.Message}");
        }
    }

    private static readonly string[] LegacyLetters = ["Q", "Z", "Y", "X", "W", "V", "U", "T"];

    private static void CleanupStaleWebDavConnections()
    {
        try
        {
            // Check each preferred drive letter for stale WebDAV (network) connections
            foreach (var letter in LegacyLetters)
            {
                try
                {
                    var localName = $"{letter}:";
                    var sb = new StringBuilder(260);
                    int length = sb.Capacity;
                    int result = NativeMethods.WNetGetConnection(localName, sb, ref length);

                    if (result != 0) continue; // NO_ERROR = 0

                    var remoteName = sb.ToString();
                    if (remoteName.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
                        remoteName.Contains("DavWWWRoot", StringComparison.OrdinalIgnoreCase))
                    {
                        // This is a stale WebDAV connection — disconnect it
                        NativeMethods.WNetCancelConnection2(localName, 0, true);
                        Debug.WriteLine($"Cleaned up stale WebDAV connection: {localName} → {remoteName}");
                    }
                }
                catch { /* best effort per letter */ }
            }

            // Also clean up persisted WebDAV connections from HKCU\Network
            using var networkKey = Registry.CurrentUser.OpenSubKey("Network");
            if (networkKey is null) return;

            foreach (var subName in networkKey.GetSubKeyNames())
            {
                try
                {
                    using var letterKey = networkKey.OpenSubKey(subName);
                    var remotePath = letterKey?.GetValue("RemotePath")?.ToString();
                    if (remotePath is not null &&
                        remotePath.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
                        remotePath.Contains("DavWWWRoot", StringComparison.OrdinalIgnoreCase))
                    {
                        NativeMethods.WNetCancelConnection2($"{subName}:", 1, true); // CONNECT_UPDATE_PROFILE
                        Registry.CurrentUser.DeleteSubKeyTree($@"Network\{subName}", false);
                        Debug.WriteLine($"Cleaned up persisted WebDAV connection: {subName}: → {remotePath}");
                    }
                }
                catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebDAV cleanup failed: {ex.Message}");
        }
    }

    private static void CleanupStaleSubstDrives()
    {
        try
        {
            foreach (var letter in LegacyLetters)
            {
                try
                {
                    // Only clean up drives that have our "QuantumDrive" label in the registry
                    var driveIconsPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{letter}\DefaultLabel";
                    using var labelKey = Registry.CurrentUser.OpenSubKey(driveIconsPath);
                    if (labelKey?.GetValue("")?.ToString() != "QuantumDrive") continue;

                    // This drive has our label — remove the subst mapping and registry entry
                    NativeMethods.DefineDosDevice(0x2, $"{letter}:", null); // DDD_REMOVE_DEFINITION
                    Registry.CurrentUser.DeleteSubKeyTree(
                        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{letter}", false);
                    Debug.WriteLine($"Cleaned up stale subst drive: {letter}:");
                }
                catch { /* best effort per letter */ }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Subst cleanup failed: {ex.Message}");
        }
    }

    private static void CleanupStaleNavPane()
    {
        try
        {
            const string NavPaneClsid = "{E4A3F710-7B2C-4B9A-9C6D-8F1A2B3C4D5E}";

            using var clsidKey = Registry.CurrentUser.OpenSubKey(
                $@"SOFTWARE\Classes\CLSID\{NavPaneClsid}");
            if (clsidKey is null) return;

            Registry.CurrentUser.DeleteSubKeyTree(
                $@"SOFTWARE\Classes\CLSID\{NavPaneClsid}", false);
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{NavPaneClsid}", false);
            using var hideKey = Registry.CurrentUser.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", true);
            hideKey?.DeleteValue(NavPaneClsid, false);

            Debug.WriteLine("Cleaned up stale nav pane entries.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Nav pane cleanup failed: {ex.Message}");
        }
    }

    public async Task MountAsync()
    {
        await _providerLock.WaitAsync();
        try
        {
            if (_isConnected)
                throw new InvalidOperationException("QuantumDrive is already mounted.");

            Directory.CreateDirectory(SyncRootParent);

            // SECURITY: Clean up any stale plaintext files from a previous crash.
            // If the app crashed while vaults were unlocked, hydrated placeholders
            // with decrypted data may still be on disk.
            CleanupStaleSyncRootFiles();

            // Generate icon before registration so sync roots pick it up
            var iconPath = await EnsureDriveIconAsync();
            Debug.WriteLine($"Drive icon: {iconPath}");

            // Create sync providers for all unlocked vaults
            await Task.Run(() => ConnectAllVaults(iconPath));

            IsEncryptedMode = true;
            _isConnected = true;

            Debug.WriteLine($"CFAPI drive mounted: {SyncRootParent}");
        }
        finally
        {
            _providerLock.Release();
        }
    }

    public async Task RefreshVaultsAsync()
    {
        if (!_isConnected)
            return;

        var iconPath = await EnsureDriveIconAsync();

        await _providerLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                // Disconnect providers and unregister sync roots for vaults that are no longer unlocked
                var unlockedIds = _vaultRegistry.UnlockedVaults
                    .Select(v => v.Descriptor.Id).ToHashSet();

                var staleIds = _providers.Keys.Where(id => !unlockedIds.Contains(id)).ToList();

                foreach (var vaultId in staleIds)
                {
                    if (_providers.TryGetValue(vaultId, out var provider))
                    {
                        provider.Disconnect();
                        provider.Dispose();
                        _providers.Remove(vaultId);
                    }
                    SyncRootRegistrar.Unregister(vaultId);
                    RemoveSyncRootFolder(vaultId);
                }

                // Connect providers for newly unlocked vaults
                ConnectAllVaults(iconPath);
            });
        }
        finally
        {
            _providerLock.Release();
        }
    }

    public async Task UnmountAsync()
    {
        await UnmountInternalAsync(force: false);
    }

    public async Task ForceUnmountAsync()
    {
        await UnmountInternalAsync(force: true);
    }

    private async Task UnmountInternalAsync(bool force = false)
    {
        await _providerLock.WaitAsync();
        try
        {
            if (!_isConnected)
                return;

            // Disconnect all CFAPI providers
            await Task.Run(() => DisconnectAllProviders(force));

            _isConnected = false;
            IsEncryptedMode = false;

            Debug.WriteLine("CFAPI drive unmounted.");
        }
        finally
        {
            _providerLock.Release();
        }
    }

    /// <summary>
    /// Removes any leftover plaintext files from sync root subfolders.
    /// Called at startup (via CleanupStaleEntries) and on mount to handle
    /// cases where the app crashed without cleanup.
    /// </summary>
    private static void CleanupStaleSyncRootFiles()
    {
        try
        {
            if (!Directory.Exists(SyncRootParent)) return;

            foreach (var subDir in Directory.GetDirectories(SyncRootParent))
            {
                // Skip hidden config folders
                var dirName = Path.GetFileName(subDir);
                if (dirName.StartsWith('.')) continue;

                try
                {
                    // AllDirectories: hydration can create nested folder trees; purge everything.
                    foreach (var file in Directory.EnumerateFiles(subDir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var attrs = File.GetAttributes(file);
                            if ((attrs & (FileAttributes.ReadOnly | FileAttributes.System)) != 0)
                                File.SetAttributes(file, attrs & ~(FileAttributes.ReadOnly | FileAttributes.System));
                            File.Delete(file);
                        }
                        catch { /* best effort */ }
                    }
                }
                catch { /* best effort */ }
            }

            Debug.WriteLine("Cleaned up stale sync root files on mount.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Stale sync root cleanup failed: {ex.Message}");
        }
    }

    private void ConnectAllVaults(string iconPath)
    {
        foreach (var ctx in _vaultRegistry.UnlockedVaults)
        {
            var vaultId = ctx.Descriptor.Id;

            // Skip if already connected
            if (_providers.ContainsKey(vaultId))
                continue;

            // Use the vault ID (not display name) as the folder name to guarantee uniqueness
            // across vaults with the same name and to survive vault renames (F8, F9).
            var syncRootPath = Path.Combine(SyncRootParent, ctx.Descriptor.Id);
            Directory.CreateDirectory(syncRootPath);

            try
            {
                SyncRootRegistrar.RegisterAsync(
                    vaultId, ctx.Descriptor.Name, syncRootPath, iconPath)
                    .GetAwaiter().GetResult();

                var factory = _backendRegistry.GetFactory(ctx.Descriptor.BackendId)
                              ?? _backendRegistry.GetFactory("local")!;
                var backend = factory.CreateForVault(ctx.Descriptor);

                var provider = new CloudSyncProvider(
                    syncRootPath,
                    backend,
                    ctx.Crypto,
                    ctx.Identity);
                provider.OnFilesChanged = () => FilesChanged?.Invoke();
                provider.Connect();

                ctx.SyncProvider = provider;
                _providers[vaultId] = provider;
                _syncRootPaths[vaultId] = syncRootPath;

                Debug.WriteLine($"Vault connected: {ctx.Descriptor.Name}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to connect vault '{ctx.Descriptor.Name}': {ex} (HRESULT: 0x{ex.HResult:X8})");
                VaultConnectFailed?.Invoke(ctx.Descriptor.Name);
            }
        }
    }

    private void DisconnectAllProviders(bool force = false)
    {
        foreach (var (vaultId, provider) in _providers)
        {
            try
            {
                provider.Disconnect();
                provider.Dispose();
                SyncRootRegistrar.Unregister(vaultId);
                RemoveSyncRootFolder(vaultId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Provider disconnect error: {ex.Message}");
            }
        }
        _providers.Clear();
        _syncRootPaths.Clear();

        // Clear provider references from vault contexts
        foreach (var ctx in _vaultRegistry.UnlockedVaults)
            ctx.SyncProvider = null;

        // Force mode: nuke any CFAPI sync root registrations that survived the normal
        // disconnect (e.g. because a process held an open handle to a placeholder file).
        // CleanupStaleSyncRoots enumerates StorageProviderSyncRootManager and unregisters
        // anything with our prefix, so it catches registrations that Unregister() above missed.
        if (force)
            CleanupStaleSyncRoots();
    }

    private void RemoveSyncRootFolder(string vaultId)
    {
        if (!_syncRootPaths.TryGetValue(vaultId, out var path))
            return;
        _syncRootPaths.Remove(vaultId);

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            Debug.WriteLine($"Removed sync root folder: {path}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove sync root folder: {ex.Message}");
        }
    }

    #region Icon

    /// <summary>
    /// Copies the app logo PNG into an ICO file at a stable, non-MSIX-virtualized path.
    /// Uses %PROGRAMDATA%\QuantumDrive\ — the standard location for shared app data
    /// that must be visible outside the MSIX virtual filesystem.
    /// </summary>
    private static async Task<string> EnsureDriveIconAsync()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "QuantumDrive");
        Directory.CreateDirectory(appDataDir);

        // v2 suffix forces recreation if an old single-resolution icon is cached.
        var iconPath = Path.Combine(appDataDir, "drive-v2.ico");

        if (File.Exists(iconPath))
            return iconPath;

        // Use the app's own logo PNG — ensures identical branding everywhere.
        try
        {
            var packagePath = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
            var pngPath = Path.Combine(packagePath, "Assets", "Square150x150Logo.scale-200.png");
            if (File.Exists(pngPath))
            {
                await CreateMultiResolutionIcoAsync(iconPath, pngPath);
                return iconPath;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read app logo from package: {ex.Message}");
        }

        // Fallback for development: look for the logo in the source tree
        try
        {
            var devPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Square150x150Logo.scale-200.png");
            if (File.Exists(devPath))
            {
                await CreateMultiResolutionIcoAsync(iconPath, devPath);
                return iconPath;
            }
        }
        catch { /* best effort */ }

        // Last resort: use a system icon
        return "%SystemRoot%\\System32\\shell32.dll,-44";
    }

    /// <summary>
    /// Creates a proper multi-resolution ICO at standard sizes (16, 24, 32, 48, 256).
    /// Each size is pre-rendered using WinRT imaging so Explorer gets a sharp image
    /// at every display density instead of downscaling from a single large frame.
    /// </summary>
    private static async Task CreateMultiResolutionIcoAsync(string icoPath, string sourcePngPath)
    {
        int[] sizes = [16, 24, 32, 48, 256];
        var sourcePngData = await File.ReadAllBytesAsync(sourcePngPath);

        var pngs = new List<byte[]>(sizes.Length);
        foreach (var size in sizes)
            pngs.Add(await ResizePngAsync(sourcePngData, size));

        WriteMultiResIco(icoPath, sizes, pngs);
    }

    private static async Task<byte[]> ResizePngAsync(byte[] pngData, int size)
    {
        using var inputStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var writer = new Windows.Storage.Streams.DataWriter(inputStream.GetOutputStreamAt(0));
        writer.WriteBytes(pngData);
        await writer.StoreAsync();
        writer.DetachStream();
        inputStream.Seek(0);

        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(inputStream);

        using var outputStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateForTranscodingAsync(outputStream, decoder);
        encoder.BitmapTransform.ScaledWidth = (uint)size;
        encoder.BitmapTransform.ScaledHeight = (uint)size;
        encoder.BitmapTransform.InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant;
        await encoder.FlushAsync();

        outputStream.Seek(0);
        var resultBytes = new byte[outputStream.Size];
        var reader = new Windows.Storage.Streams.DataReader(outputStream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)outputStream.Size);
        reader.ReadBytes(resultBytes);
        reader.DetachStream();
        return resultBytes;
    }

    private static void WriteMultiResIco(string icoPath, int[] sizes, List<byte[]> pngs)
    {
        int count = sizes.Length;
        int headerSize = 6 + 16 * count;

        using var fs = File.Create(icoPath);
        using var bw = new BinaryWriter(fs);

        // ICO header
        bw.Write((short)0);       // Reserved
        bw.Write((short)1);       // Type = ICO
        bw.Write((short)count);   // Number of images

        // Directory entries
        int offset = headerSize;
        for (int i = 0; i < count; i++)
        {
            int sz = sizes[i];
            bw.Write((byte)(sz >= 256 ? 0 : sz)); // 0 means 256
            bw.Write((byte)(sz >= 256 ? 0 : sz));
            bw.Write((byte)0);     // Color count (0 = 32-bpp/PNG)
            bw.Write((byte)0);     // Reserved
            bw.Write((short)1);    // Color planes
            bw.Write((short)32);   // Bits per pixel
            bw.Write(pngs[i].Length);
            bw.Write(offset);
            offset += pngs[i].Length;
        }

        // Image data blocks
        foreach (var png in pngs)
            bw.Write(png);
    }

    #endregion

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool DefineDosDevice(
            int flags,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string deviceName,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string? targetPath);

        [System.Runtime.InteropServices.DllImport("mpr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int WNetCancelConnection2(
            string name, int flags, bool force);

        [System.Runtime.InteropServices.DllImport("mpr.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int WNetGetConnection(
            string localName, StringBuilder remoteName, ref int length);
    }
}
