namespace quantum_drive.Services;

public interface IVirtualDriveService
{
    string? SyncRootPath { get; }
    bool IsEncryptedMode { get; }
    Task MountAsync();
    Task UnmountAsync();
    Task ForceUnmountAsync();
    Task RefreshVaultsAsync();
    event Action? FilesChanged;
}
