namespace quantum_drive.Models;

public enum StorageChangeType { Created, Changed, Deleted, Renamed }

public record StorageBackendChangeEvent(
    StorageChangeType ChangeType,
    string Path,
    string? OldPath = null);
