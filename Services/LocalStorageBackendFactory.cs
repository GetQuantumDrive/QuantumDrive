using quantum_drive.Models;

namespace quantum_drive.Services;

public sealed class LocalStorageBackendFactory : IStorageBackendFactory
{
    public string Id => "local";
    public string DisplayName => "Local Storage";

    public IStorageBackend CreateForVault(VaultDescriptor vault)
        => new LocalStorageBackend(vault.FolderPath);
}
