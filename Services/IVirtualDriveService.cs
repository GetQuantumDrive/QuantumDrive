namespace quantum_drive.Services;

public interface IVirtualDriveService
{
    string? MountedDriveLetter { get; }
    bool IsEncryptedMode { get; }
    Task<string> MountAsync();
    Task UnmountAsync();
    Task ForceUnmountAsync();
    Task RefreshVaultsAsync();
    event Action? FilesChanged;
}
