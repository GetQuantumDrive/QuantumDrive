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

    /// <summary>Fired when an individual vault fails to connect during mount or refresh.</summary>
    event Action<string>? VaultConnectFailed; // vault display name
}
