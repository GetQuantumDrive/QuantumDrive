using quantum_drive.Services;

namespace quantum_drive.Models;

public class VaultContext : IDisposable
{
    public required VaultDescriptor Descriptor { get; init; }
    public required IdentityService Identity { get; init; }
    public required CryptoService Crypto { get; init; }
    public CloudSyncProvider? SyncProvider { get; set; }
    public bool IsUnlocked => Identity.MlKemPrivateKey is not null;

    public void Dispose()
    {
        SyncProvider?.Dispose();
    }
}
