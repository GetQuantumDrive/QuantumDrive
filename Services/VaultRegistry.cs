using System.Diagnostics;
using System.Text.Json;
using quantum_drive.Models;
using Windows.Storage;
using Windows.Storage.AccessCache;

namespace quantum_drive.Services;

public class VaultRegistry : IVaultRegistry
{
    private const string SettingsKey = "RegisteredVaults";

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

    public async Task<VaultDescriptor> RegisterNewVaultAsync(string name, string folderPath, string password, string? hint = null, StorageFolder? pickedFolder = null)
    {
        var vaultDir = Path.Combine(folderPath, ".quantum_vault");
        Directory.CreateDirectory(vaultDir);

        var descriptor = new VaultDescriptor
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = name,
            FolderPath = vaultDir
        };

        // Save the picked folder to FutureAccessList for permission persistence
        SaveFutureAccessToken(descriptor, pickedFolder);

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

    public async Task<VaultDescriptor> RegisterExistingVaultAsync(string name, string folderPath, string password, StorageFolder? pickedFolder = null)
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

        // Save the picked folder to FutureAccessList for permission persistence
        SaveFutureAccessToken(descriptor, pickedFolder);

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

        // Remove FutureAccessList token if present
        if (!string.IsNullOrEmpty(descriptor.FutureAccessToken))
        {
            try
            {
                if (StorageApplicationPermissions.FutureAccessList.ContainsItem(descriptor.FutureAccessToken))
                    StorageApplicationPermissions.FutureAccessList.Remove(descriptor.FutureAccessToken);
            }
            catch { /* best effort */ }
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

    private static void SaveFutureAccessToken(VaultDescriptor descriptor, StorageFolder? folder)
    {
        if (folder is null) return;

        try
        {
            var token = StorageApplicationPermissions.FutureAccessList.Add(folder, descriptor.Id);
            descriptor.FutureAccessToken = token;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save FutureAccessList token: {ex.Message}");
        }
    }

    private void LoadVaultList()
    {
        _vaults.Clear();

        var json = ApplicationData.Current.LocalSettings.Values[SettingsKey] as string;
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
        var json = JsonSerializer.Serialize(_vaults);
        ApplicationData.Current.LocalSettings.Values[SettingsKey] = json;
    }
}
