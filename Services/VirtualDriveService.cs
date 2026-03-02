using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace quantum_drive.Services;

public class VirtualDriveService : IVirtualDriveService
{
    private static readonly string[] PreferredLetters = ["Q", "Z", "Y", "X", "W", "V", "U", "T"];

    // Use a normal user-profile folder for the sync root — same pattern as
    // OneDrive (C:\Users\bjorn\OneDrive) and Dropbox (C:\Users\bjorn\Dropbox).
    // This avoids MSIX VFS virtualization issues: kernel APIs bypass the VFS layer.
    private static readonly string SyncRootParent = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "QuantumDrive");

    private readonly IVaultRegistry _vaultRegistry;
    private readonly Dictionary<string, CloudSyncProvider> _providers = []; // vaultId → provider
    private readonly Dictionary<string, string> _syncRootPaths = []; // vaultId → sync root folder path

    public string? MountedDriveLetter { get; private set; }
    public bool IsEncryptedMode { get; private set; }
    public event Action? FilesChanged;

    public VirtualDriveService(IVaultRegistry vaultRegistry)
    {
        _vaultRegistry = vaultRegistry;
    }

    /// <summary>
    /// Removes stale registry entries and connections left behind if the app crashed while a drive was mounted.
    /// </summary>
    public static void CleanupStaleEntries()
    {
        // 1. Clean up stale WebDAV connections (from old WebDAV-based virtual drive)
        CleanupStaleWebDavConnections();

        // 2. Clean up stale subst drive mappings
        CleanupStaleSubstDrives();

        // 3. Clean up stale CLSID / nav pane entries
        CleanupStaleNavPane();
    }

    private static void CleanupStaleWebDavConnections()
    {
        try
        {
            // Check each preferred drive letter for stale WebDAV (network) connections
            foreach (var letter in PreferredLetters)
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
            foreach (var letter in PreferredLetters)
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
            using var clsidKey = Registry.CurrentUser.OpenSubKey(
                $@"SOFTWARE\Classes\CLSID\{NavPaneClsid}");
            if (clsidKey is null) return;

            // Check if any drive is still actively mounted with our label
            bool anyMounted = PreferredLetters.Any(l =>
            {
                try
                {
                    using var driveKey = Registry.CurrentUser.OpenSubKey(
                        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{l}\DefaultLabel");
                    if (driveKey?.GetValue("")?.ToString() == "QuantumDrive")
                        return Directory.Exists($@"{l}:\");
                    return false;
                }
                catch { return false; }
            });

            if (anyMounted) return;

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

    public async Task<string> MountAsync()
    {
        if (MountedDriveLetter is not null)
            throw new InvalidOperationException($"Drive {MountedDriveLetter}: is already mounted by QuantumDrive.");

        var letter = PreferredLetters.FirstOrDefault(l => !DriveExists(l))
            ?? throw new InvalidOperationException("No available drive letter found.");

        var syncRoot = SyncRootParent;
        Directory.CreateDirectory(syncRoot);

        // SECURITY: Clean up any stale plaintext files from a previous crash.
        // If the app crashed while vaults were unlocked, hydrated placeholders
        // with decrypted data may still be on disk.
        CleanupStaleSyncRootFiles();

        // Generate icon before registration so sync roots pick it up
        var iconPath = await EnsureDriveIconAsync();
        Debug.WriteLine($"Drive icon: {iconPath}");

        // Create sync providers for all unlocked vaults
        await Task.Run(() => ConnectAllVaults(iconPath));

        // Verify the folder actually exists on disk
        if (!Directory.Exists(syncRoot))
        {
            DisconnectAllProviders();
            throw new InvalidOperationException(
                $"Sync root folder does not exist at expected path: {syncRoot}");
        }

        // Map drive letter to sync root parent via subst (DefineDosDevice)
        var deviceName = $"{letter}:";
        var mapped = NativeMethods.DefineDosDevice(0, deviceName, syncRoot);
        if (!mapped)
        {
            DisconnectAllProviders();
            throw new InvalidOperationException(
                $"Failed to map drive {letter}: (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        IsEncryptedMode = true;
        SetDriveMetadata(letter, iconPath);
        MountedDriveLetter = letter;

        // Notify Explorer so it picks up our drive label and icon immediately
        NativeMethods.SHChangeNotify(0x00000100 /* SHCNE_DRIVEADD */, 0x0005 /* SHCNF_PATHW */,
            Marshal.StringToHGlobalUni($@"{letter}:\"), IntPtr.Zero);

        Debug.WriteLine($"CFAPI drive mounted: {letter}: → {syncRoot}");
        return letter;
    }

    public async Task RefreshVaultsAsync()
    {
        if (MountedDriveLetter is null)
            return;

        var iconPath = await EnsureDriveIconAsync();

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

    public async Task UnmountAsync()
    {
        await UnmountInternalAsync(force: false);
    }

    public async Task ForceUnmountAsync()
    {
        await UnmountInternalAsync(force: true);
    }

    private async Task UnmountInternalAsync(bool force)
    {
        if (MountedDriveLetter is null)
            return;

        var letter = MountedDriveLetter;
        ClearDriveMetadata(letter);

        // Disconnect all CFAPI providers
        await Task.Run(DisconnectAllProviders);

        // Remove drive mapping (DDD_REMOVE_DEFINITION = 0x2)
        var deviceName = $"{letter}:";
        var removed = NativeMethods.DefineDosDevice(0x2, deviceName, null);
        if (!removed)
            Debug.WriteLine($"DefineDosDevice remove failed for {letter}: (Win32 error {Marshal.GetLastWin32Error()})");

        MountedDriveLetter = null;
        IsEncryptedMode = false;

        Debug.WriteLine($"CFAPI drive unmounted: {letter}:");
    }

    /// <summary>
    /// Removes any leftover plaintext files from sync root subfolders.
    /// Called on mount to handle cases where the app crashed without cleanup.
    /// </summary>
    private void CleanupStaleSyncRootFiles()
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
                    foreach (var file in Directory.EnumerateFiles(subDir))
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

            var syncRootPath = Path.Combine(SyncRootParent, ctx.Descriptor.Name);
            Directory.CreateDirectory(syncRootPath);

            try
            {
                SyncRootRegistrar.RegisterAsync(
                    vaultId, ctx.Descriptor.Name, syncRootPath, iconPath)
                    .GetAwaiter().GetResult();

                var provider = new CloudSyncProvider(
                    syncRootPath,
                    ctx.Descriptor.FolderPath,
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
            }
        }
    }

    private void DisconnectAllProviders()
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

    private static bool DriveExists(string letter)
    {
        return Directory.Exists($@"{letter}:\");
    }

    // Fixed CLSID for QuantumDrive Explorer nav pane entry
    private const string NavPaneClsid = "{E4A3F710-7B2C-4B9A-9C6D-8F1A2B3C4D5E}";

    private static void SetDriveMetadata(string letter, string iconPath)
    {
        try
        {
            // Drive icon/label under This PC
            var driveIconsPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{letter}";
            using var labelKey = Registry.CurrentUser.CreateSubKey($@"{driveIconsPath}\DefaultLabel");
            labelKey.SetValue("", "QuantumDrive");
            using var iconKey = Registry.CurrentUser.CreateSubKey($@"{driveIconsPath}\DefaultIcon");
            iconKey.SetValue("", iconPath);

            // Register shell namespace folder (nav pane entry)
            var clsidPath = $@"SOFTWARE\Classes\CLSID\{NavPaneClsid}";
            using var clsidKey = Registry.CurrentUser.CreateSubKey(clsidPath);
            clsidKey.SetValue("", "QuantumDrive");
            clsidKey.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
            clsidKey.SetValue("SortOrderIndex", 0x42, RegistryValueKind.DWord);

            using var clsidIconKey = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\DefaultIcon");
            clsidIconKey.SetValue("", iconPath);

            using var inProcKey = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\InProcServer32");
            inProcKey.SetValue("", @"%SystemRoot%\System32\shell32.dll", RegistryValueKind.ExpandString);

            using var instanceKey = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\Instance");
            instanceKey.SetValue("CLSID", "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}");
            using var initBagKey = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\Instance\InitPropertyBag");
            initBagKey.SetValue("Attributes", 0x11, RegistryValueKind.DWord);
            initBagKey.SetValue("TargetFolderPath", $@"{letter}:\");

            using var shellFolderKey = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\ShellFolder");
            shellFolderKey.SetValue("Attributes", unchecked((int)0xF080004D), RegistryValueKind.DWord);
            shellFolderKey.SetValue("FolderValueFlags", 0x28, RegistryValueKind.DWord);

            using var nsKey = Registry.CurrentUser.CreateSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{NavPaneClsid}");
            nsKey.SetValue("", "QuantumDrive");

            using var hideKey = Registry.CurrentUser.CreateSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel");
            hideKey.SetValue(NavPaneClsid, 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set drive metadata: {ex.Message}");
        }
    }

    private static void ClearDriveMetadata(string letter)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{letter}", false);
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"SOFTWARE\Classes\CLSID\{NavPaneClsid}", false);
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{NavPaneClsid}", false);

            using var hideKey = Registry.CurrentUser.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", true);
            hideKey?.DeleteValue(NavPaneClsid, false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to clear drive metadata: {ex.Message}");
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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DefineDosDevice(
            int flags,
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            [MarshalAs(UnmanagedType.LPWStr)] string? targetPath);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        public static extern int WNetCancelConnection2(
            string name, int flags, bool force);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        public static extern int WNetGetConnection(
            string localName, StringBuilder remoteName, ref int length);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
