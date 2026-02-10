namespace quantum_drive.Services;

public interface IVirtualDriveService
{
    string? MountedDriveLetter { get; }
    bool IsEncryptedMode { get; }
    Task<string> MountAsync(string path);
    Task UnmountAsync();
}
