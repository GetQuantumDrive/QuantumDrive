namespace quantum_drive.Services;

/// <summary>
/// Registry of all available storage backend factories.
/// Factories are registered at app startup; cloud provider factories are registered
/// alongside the local factory when the cloud provider assemblies are present.
/// </summary>
public sealed class StorageBackendRegistry
{
    private readonly Dictionary<string, IStorageBackendFactory> _factories = [];

    public void Register(IStorageBackendFactory factory)
        => _factories[factory.Id] = factory;

    public IStorageBackendFactory? GetFactory(string id)
        => _factories.GetValueOrDefault(id);

    public IReadOnlyCollection<IStorageBackendFactory> All
        => _factories.Values;
}
