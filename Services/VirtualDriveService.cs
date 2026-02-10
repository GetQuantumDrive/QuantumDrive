using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace quantum_drive.Services;

public class VirtualDriveService : IVirtualDriveService
{
    private static readonly string[] PreferredLetters = ["Q", "Z", "Y", "X", "W", "V", "U", "T"];

    private readonly ICryptoService _cryptoService;
    private readonly IIdentityService _identityService;
    private readonly ILicenseService _licenseService;

    private WebApplication? _webApp;
    private int _port;
    private WebDavHandler? _webDavHandler;

    public string? MountedDriveLetter { get; private set; }
    public bool IsEncryptedMode { get; private set; }

    public VirtualDriveService(ICryptoService cryptoService, IIdentityService identityService, ILicenseService licenseService)
    {
        _cryptoService = cryptoService;
        _identityService = identityService;
        _licenseService = licenseService;
    }

    public async Task<string> MountAsync(string path)
    {
        if (MountedDriveLetter is not null)
            throw new InvalidOperationException($"Drive {MountedDriveLetter}: is already mounted by QuantumDrive.");

        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);

        var letter = PreferredLetters.FirstOrDefault(l => !DriveExists(l))
            ?? throw new InvalidOperationException("No available drive letter found.");

        // Always attempt WebDAV first
        try
        {
            await MountWithWebDavAsync(path, letter);
            IsEncryptedMode = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebDAV mount failed ({ex.Message}) — falling back to subst mode.");
            // DefineDosDevice is the kernel API behind subst — works from MSIX
            var deviceLetter = $"{letter}:";
            var devicePath = path;
            var success = await Task.Run(() => NativeMethods.DefineDosDevice(0, deviceLetter, devicePath));
            if (!success)
            {
                var win32Error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"DefineDosDevice failed for {letter}: (Win32 error {win32Error}).");
            }
            IsEncryptedMode = false;
        }

        SetDriveMetadata(letter);
        MountedDriveLetter = letter;
        return letter;
    }

    public async Task UnmountAsync()
    {
        if (MountedDriveLetter is null)
            return;

        var letter = MountedDriveLetter;
        ClearDriveMetadata(letter);

        if (IsEncryptedMode)
        {
            // Disconnect the mapped WebDAV drive
            try
            {
                var cancelName = $@"{letter}:";
                var result = await Task.Run(() => NativeMethods.WNetCancelConnection2(cancelName, 0, true));
                if (result != 0)
                    Debug.WriteLine($"WNetCancelConnection2 failed: Win32 error {result}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Drive disconnect error: {ex.Message}");
            }

            // Stop Kestrel
            try
            {
                if (_webApp is not null)
                {
                    await _webApp.StopAsync();
                    await _webApp.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Kestrel stop error: {ex.Message}");
            }

            _webApp = null;

            // Dispose WebDavHandler to clean up FileSystemWatcher
            _webDavHandler?.Dispose();
            _webDavHandler = null;
        }
        else
        {
            // DDD_REMOVE_DEFINITION (0x2) removes the mapping
            var removeName = $"{letter}:";
            var removed = await Task.Run(() => NativeMethods.DefineDosDevice(0x2, removeName, null));
            if (!removed)
                Debug.WriteLine($"DefineDosDevice remove failed for {letter}: (Win32 error {Marshal.GetLastWin32Error()})");
        }

        MountedDriveLetter = null;
        IsEncryptedMode = false;
    }

    private async Task MountWithWebDavAsync(string vaultPath, string letter)
    {
        _port = GetAvailablePort();

        _webDavHandler = new WebDavHandler(vaultPath, _cryptoService, _identityService);
        _webDavHandler.FileLimit = _licenseService.FileLimit;
        var handler = _webDavHandler;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, _port);
        });
        builder.Logging.ClearProviders();

        _webApp = builder.Build();
        _webApp.Run(handler.HandleRequestAsync);

        await _webApp.StartAsync();
        Debug.WriteLine($"WebDAV server listening on http://localhost:{_port}");

        // Poll until Kestrel is accepting connections (typically <50ms)
        using var cts = new CancellationTokenSource(2000);
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, _port, cts.Token);
                break;
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                await Task.Delay(30, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        // Map drive using WNet API (handles WebClient service auto-start)
        var localName = $"{letter}:";
        var remotePath = $@"\\localhost@{_port}\QuantumDrive";

        // Clear any stale mapping on this letter (from previous failed attempts)
        await Task.Run(() => NativeMethods.WNetCancelConnection2(localName, 0, true));

        var resource = new NativeMethods.NETRESOURCE
        {
            dwType = 1, // RESOURCETYPE_DISK
            lpLocalName = localName,
            lpRemoteName = remotePath
        };

        var result = await Task.Run(() => NativeMethods.WNetAddConnection2(ref resource, null, null, 0));

        if (result != 0)
        {
            Debug.WriteLine($"WNetAddConnection2 attempt 1 failed: Win32 error {result}");

            // Retry once after a brief delay — WebClient service may need time to start
            await Task.Delay(500);
            result = await Task.Run(() => NativeMethods.WNetAddConnection2(ref resource, null, null, 0));
        }

        if (result != 0)
        {
            Debug.WriteLine($"WNetAddConnection2 attempt 2 failed: Win32 error {result}");
            await _webApp.StopAsync();
            await _webApp.DisposeAsync();
            _webApp = null;
            throw new InvalidOperationException($"WebDAV drive mapping failed (Win32 error {result}).");
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool DriveExists(string letter)
    {
        return Directory.Exists($@"{letter}:\");
    }

    // Fixed CLSID for QuantumDrive Explorer nav pane entry
    private const string NavPaneClsid = "{E4A3F710-7B2C-4B9A-9C6D-8F1A2B3C4D5E}";

    private static void SetDriveMetadata(string letter)
    {
        try
        {
            var iconPath = EnsureDriveIcon();

            // Drive icon/label under This PC
            var driveIconsPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{letter}";
            using var labelKey = Registry.CurrentUser.CreateSubKey($@"{driveIconsPath}\DefaultLabel");
            labelKey.SetValue("", "QuantumDrive");
            using var iconKey = Registry.CurrentUser.CreateSubKey($@"{driveIconsPath}\DefaultIcon");
            iconKey.SetValue("", iconPath);

            // Register shell namespace folder (Proton Drive-style nav pane entry)
            var clsidPath = $@"SOFTWARE\Classes\CLSID\{NavPaneClsid}";
            using var clsidKey = Registry.CurrentUser.CreateSubKey(clsidPath);
            clsidKey.SetValue("", "QuantumDrive");
            clsidKey.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
            clsidKey.SetValue("SortOrderIndex", 0x42, RegistryValueKind.DWord);

            using var clsidIconKey = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\DefaultIcon");
            clsidIconKey.SetValue("", iconPath);

            // InProcServer32 — use shell32.dll for folder behavior
            using var inProcKey = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\InProcServer32");
            inProcKey.SetValue("", @"%SystemRoot%\System32\shell32.dll", RegistryValueKind.ExpandString);

            // Instance — point to the drive letter as a folder
            using var instanceKey = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\Instance");
            instanceKey.SetValue("CLSID", "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}");
            using var initBagKey = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\Instance\InitPropertyBag");
            initBagKey.SetValue("Attributes", 0x11, RegistryValueKind.DWord);
            initBagKey.SetValue("TargetFolderPath", $@"{letter}:\");

            // ShellFolder attributes — browsable folder
            using var shellFolderKey = Registry.CurrentUser.CreateSubKey($@"{clsidPath}\ShellFolder");
            shellFolderKey.SetValue("Attributes", unchecked((int)0xF080004D), RegistryValueKind.DWord);
            shellFolderKey.SetValue("FolderValueFlags", 0x28, RegistryValueKind.DWord);

            // Register in desktop namespace (makes it appear in nav pane)
            using var nsKey = Registry.CurrentUser.CreateSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{NavPaneClsid}");
            nsKey.SetValue("", "QuantumDrive");

            // Hide from desktop
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
            // Remove drive icon/label
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{letter}", false);

            // Remove nav pane CLSID
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"SOFTWARE\Classes\CLSID\{NavPaneClsid}", false);

            // Remove desktop namespace entry
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{NavPaneClsid}", false);

            // Remove desktop icon hide entry
            using var hideKey = Registry.CurrentUser.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", true);
            hideKey?.DeleteValue(NavPaneClsid, false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to clear drive metadata: {ex.Message}");
        }
    }

    private static string EnsureDriveIcon()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuantumDrive");
        var iconPath = Path.Combine(dir, "drive.ico");

        if (File.Exists(iconPath))
            return iconPath;

        Directory.CreateDirectory(dir);
        GenerateDriveIcon(iconPath);
        return iconPath;
    }

    private static void GenerateDriveIcon(string path)
    {
        // Generate a 32x32 icon: violet circle with white shield outline
        const int size = 32;
        const float cx = 15.5f, cy = 15.5f, radius = 14.5f;
        const byte vR = 0x7C, vG = 0x3A, vB = 0xED; // #7C3AED QuantumViolet

        var pixels = new byte[size * size * 4];
        var andMask = new byte[size * (size / 8)];
        for (int i = 0; i < andMask.Length; i++) andMask[i] = 0xFF;

        for (int y = 0; y < size; y++)
        {
            int bmpY = size - 1 - y; // BMP rows are bottom-up
            for (int x = 0; x < size; x++)
            {
                float dx = x - cx, dy = y - cy;
                float dist = MathF.Sqrt(dx * dx + dy * dy);

                if (dist > radius + 0.5f) continue;

                float circleAlpha = Math.Clamp(radius + 0.5f - dist, 0f, 1f);
                bool shield = IsShieldOutline(x, y);

                byte r = shield ? (byte)255 : vR;
                byte g = shield ? (byte)255 : vG;
                byte b = shield ? (byte)255 : vB;
                byte a = (byte)(circleAlpha * 255);

                int idx = (bmpY * size + x) * 4;
                pixels[idx + 0] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = a;

                if (a > 127)
                {
                    int mi = bmpY * (size / 8) + x / 8;
                    andMask[mi] &= (byte)~(0x80 >> (x % 8));
                }
            }
        }

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        // ICO header
        bw.Write((short)0);  // Reserved
        bw.Write((short)1);  // Type: icon
        bw.Write((short)1);  // Image count

        // Directory entry
        bw.Write((byte)size);
        bw.Write((byte)size);
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((short)1);  // Planes
        bw.Write((short)32); // BPP
        int dataSize = 40 + pixels.Length + andMask.Length;
        bw.Write(dataSize);
        bw.Write(22);        // Offset (6 header + 16 directory)

        // BITMAPINFOHEADER
        bw.Write(40);
        bw.Write(size);
        bw.Write(size * 2);  // Height doubled (XOR + AND)
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(0);
        bw.Write(pixels.Length + andMask.Length);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

        bw.Write(pixels);
        bw.Write(andMask);
    }

    private static bool IsShieldOutline(int x, int y)
    {
        return IsInsideShield(x, y, 0f) && !IsInsideShield(x, y, 1.8f);
    }

    private static bool IsInsideShield(int x, int y, float inset)
    {
        float top = 8 + inset;
        float bottom = 25 - inset;
        if (y < top || y > bottom) return false;

        const float center = 16f;
        float halfWidth;

        if (y <= 10)
        {
            // Top curve: widen from narrow to full width
            float t = (y - top) / (10 - top);
            halfWidth = MathF.Max((7f - inset) * Math.Clamp(t, 0f, 1f), 0);
        }
        else if (y <= 17)
        {
            // Straight sides
            halfWidth = MathF.Max(7f - inset, 0);
        }
        else
        {
            // Bottom taper to point
            float t = (y - 17f) / (bottom - 17f);
            halfWidth = MathF.Max((7f - inset) * (1f - t), 0);
        }

        return x >= center - halfWidth && x <= center + halfWidth;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NETRESOURCE
        {
            public int dwScope;
            public int dwType;
            public int dwDisplayType;
            public int dwUsage;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpLocalName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpRemoteName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpComment;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpProvider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        public static extern int WNetAddConnection2(
            ref NETRESOURCE netResource,
            [MarshalAs(UnmanagedType.LPWStr)] string? password,
            [MarshalAs(UnmanagedType.LPWStr)] string? username,
            int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        public static extern int WNetCancelConnection2(
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            int flags,
            [MarshalAs(UnmanagedType.Bool)] bool force);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DefineDosDevice(
            int flags,
            [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
            [MarshalAs(UnmanagedType.LPWStr)] string? targetPath);
    }
}
