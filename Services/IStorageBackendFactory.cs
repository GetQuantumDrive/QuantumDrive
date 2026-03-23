using quantum_drive.Models;

namespace quantum_drive.Services;

/// <summary>
/// Plugin entry point. Each storage backend (local, Google Drive, Dropbox, …) provides one factory.
/// </summary>
public interface IStorageBackendFactory
{
    string Id { get; }
    string DisplayName { get; }
    IStorageBackend CreateForVault(VaultDescriptor vault);
}
