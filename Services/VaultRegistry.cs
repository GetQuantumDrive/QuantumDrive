using System.Diagnostics;
using System.Text.Json;
using quantum_drive.Models;

namespace quantum_drive.Services;

public class VaultRegistry : IVaultRegistry
{
    private readonly IPostQuantumCrypto _pqCrypto;
    private readonly List<VaultDescriptor> _vaults = new();
    private readonly Dictionary<string, VaultContext> _contexts = new();

    public IReadOnlyList<VaultDescriptor> Vaults => _vaults.AsReadOnly();

    public IReadOnlyList<VaultContext> UnlockedVaults =>
        _contexts.Values.Where(c => c.IsUnlocked).ToList().AsReadOnly();

    public bool HasAnyVault => _vaults.Count > 0;

    public VaultRegistry(IPostQuantumCrypto pqCrypto)
    {
        _pqCrypto = pqCrypto;
        LoadVaultList();
    }

    public async Task<VaultDescriptor> RegisterNewVaultAsync(string name, string folderPath, string password, string? hint = null)
    {
        var vaultDir = Path.Combine(folderPath, ".quantum_vault");
        Directory.CreateDirectory(vaultDir);

        var descriptor = new VaultDescriptor
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            FolderPath = vaultDir
        };

        var identity = new IdentityService(_pqCrypto, vaultDir);
        var crypto = new CryptoService(_pqCrypto);

        await identity.CreateVaultAsync(password, hint);

        var context = new VaultContext
        {
            Descriptor = descriptor,
            Identity = identity,
            Crypto = crypto
        };

        _vaults.Add(descriptor);
        _contexts[descriptor.Id] = context;
        SaveVaultList();

        return descriptor;
    }

    public async Task<VaultDescriptor> RegisterExistingVaultAsync(string name, string folderPath, string password)
    {
        var vaultDir = Path.Combine(folderPath, ".quantum_vault");
        if (!File.Exists(Path.Combine(vaultDir, "vault.identity")))
            throw new FileNotFoundException("No vault.identity found in the selected folder.");

        var identity = new IdentityService(_pqCrypto, vaultDir);
        bool unlocked = await identity.UnlockAsync(password);
        if (!unlocked)
            throw new UnauthorizedAccessException("Invalid password for the existing vault.");

        var descriptor = new VaultDescriptor
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            FolderPath = vaultDir
        };

        var crypto = new CryptoService(_pqCrypto);
        var context = new VaultContext
        {
            Descriptor = descriptor,
            Identity = identity,
            Crypto = crypto
        };

        _vaults.Add(descriptor);
        _contexts[descriptor.Id] = context;
        SaveVaultList();

        return descriptor;
    }

    public Task RemoveVaultAsync(string vaultId)
    {
        var descriptor = _vaults.FirstOrDefault(v => v.Id == vaultId);
        if (descriptor is null) return Task.CompletedTask;

        if (_contexts.TryGetValue(vaultId, out var context))
        {
            context.Dispose();
            _contexts.Remove(vaultId);
        }

        _vaults.Remove(descriptor);
        SaveVaultList();

        return Task.CompletedTask;
    }

    public async Task<bool> UnlockVaultAsync(string vaultId, string password)
    {
        var context = EnsureContext(vaultId);
        return await context.Identity.UnlockAsync(password);
    }

    public void LockVault(string vaultId)
    {
        if (_contexts.TryGetValue(vaultId, out var context))
        {
            context.Dispose();
            _contexts.Remove(vaultId);
        }
    }

    public void LockAll()
    {
        foreach (var context in _contexts.Values)
        {
            context.Dispose();
        }
        _contexts.Clear();
    }

    public VaultContext? GetContext(string vaultId)
    {
        return EnsureContext(vaultId);
    }

    public VaultContext? GetContextByName(string name)
    {
        var descriptor = _vaults.FirstOrDefault(v =>
            v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null) return null;
        return EnsureContext(descriptor.Id);
    }

    private VaultContext EnsureContext(string vaultId)
    {
        if (_contexts.TryGetValue(vaultId, out var existing))
            return existing;

        var descriptor = _vaults.FirstOrDefault(v => v.Id == vaultId);
        if (descriptor is null)
            throw new KeyNotFoundException($"No vault registered with id '{vaultId}'.");

        var identity = new IdentityService(_pqCrypto, descriptor.FolderPath);
        var crypto = new CryptoService(_pqCrypto);

        var context = new VaultContext
        {
            Descriptor = descriptor,
            Identity = identity,
            Crypto = crypto
        };

        _contexts[vaultId] = context;
        return context;
    }

    private void LoadVaultList()
    {
        _vaults.Clear();

        var json = AppSettings.RegisteredVaults;
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            var descriptors = JsonSerializer.Deserialize<List<VaultDescriptor>>(json);
            if (descriptors is not null)
                _vaults.AddRange(descriptors);
        }
        catch
        {
            // Corrupt settings — start fresh
        }
    }

    private void SaveVaultList()
    {
        AppSettings.RegisteredVaults = JsonSerializer.Serialize(_vaults);
    }
}
