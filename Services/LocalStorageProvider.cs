using System.Security.Cryptography;
using System.Text.Json;
using quantum_drive.Models;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Streams;

namespace quantum_drive.Services;

public class LocalStorageProvider : ICloudStorageService
{
    private const string VaultFolderName = ".quantum_vault";
    private const string VaultFolderTokenKey = "QuantumDriveVaultFolderToken";

    private readonly ICryptoService _cryptoService;
    private readonly IIdentityService _identityService;
    private StorageFolder? _vaultFolder;

    public LocalStorageProvider(ICryptoService cryptoService, IIdentityService identityService)
    {
        _cryptoService = cryptoService;
        _identityService = identityService;
    }

    private async Task EnsureVaultFolderAsync()
    {
        if (_vaultFolder is not null) return;

        // Check for custom folder token in LocalSettings
        var localSettings = ApplicationData.Current.LocalSettings;
        var token = localSettings.Values[VaultFolderTokenKey] as string;

        if (!string.IsNullOrEmpty(token) && StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
        {
            try
            {
                var customFolder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token);
                _vaultFolder = await customFolder.CreateFolderAsync(
                    VaultFolderName,
                    CreationCollisionOption.OpenIfExists);
                return;
            }
            catch
            {
                // Custom folder no longer accessible, fall back to default
                localSettings.Values.Remove(VaultFolderTokenKey);
            }
        }

        // Default: AppData local folder
        var localFolder = ApplicationData.Current.LocalFolder;
        _vaultFolder = await localFolder.CreateFolderAsync(
            VaultFolderName,
            CreationCollisionOption.OpenIfExists);
    }

    public string GetVaultPath()
    {
        if (_vaultFolder is not null)
            return _vaultFolder.Path;

        var localSettings = ApplicationData.Current.LocalSettings;
        var token = localSettings.Values[VaultFolderTokenKey] as string;

        if (!string.IsNullOrEmpty(token))
            return "(Custom folder - loading...)";

        return System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, VaultFolderName);
    }

    public async Task SetCustomVaultFolderAsync(StorageFolder folder, bool migrateFiles)
    {
        var oldVaultFolder = _vaultFolder;
        await EnsureVaultFolderAsync();
        oldVaultFolder ??= _vaultFolder;

        // Create vault subfolder in the chosen location
        var newVaultFolder = await folder.CreateFolderAsync(
            VaultFolderName,
            CreationCollisionOption.OpenIfExists);

        // Migrate files if requested
        if (migrateFiles && oldVaultFolder is not null)
        {
            var files = await oldVaultFolder.GetFilesAsync();
            foreach (var file in files)
            {
                await file.CopyAsync(newVaultFolder, file.Name, NameCollisionOption.ReplaceExisting);
            }
        }

        // Store token in FutureAccessList and LocalSettings
        var token = StorageApplicationPermissions.FutureAccessList.Add(folder);
        var localSettings = ApplicationData.Current.LocalSettings;
        localSettings.Values[VaultFolderTokenKey] = token;

        _vaultFolder = newVaultFolder;
    }

    public async Task ResetToDefaultFolderAsync(bool migrateFiles)
    {
        var oldVaultFolder = _vaultFolder;
        await EnsureVaultFolderAsync();
        oldVaultFolder ??= _vaultFolder;

        // Default folder
        var localFolder = ApplicationData.Current.LocalFolder;
        var defaultVaultFolder = await localFolder.CreateFolderAsync(
            VaultFolderName,
            CreationCollisionOption.OpenIfExists);

        // Migrate files if requested
        if (migrateFiles && oldVaultFolder is not null && oldVaultFolder.Path != defaultVaultFolder.Path)
        {
            var files = await oldVaultFolder.GetFilesAsync();
            foreach (var file in files)
            {
                await file.CopyAsync(defaultVaultFolder, file.Name, NameCollisionOption.ReplaceExisting);
            }
        }

        // Remove custom token
        var localSettings = ApplicationData.Current.LocalSettings;
        var token = localSettings.Values[VaultFolderTokenKey] as string;
        if (!string.IsNullOrEmpty(token) && StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
        {
            StorageApplicationPermissions.FutureAccessList.Remove(token);
        }
        localSettings.Values.Remove(VaultFolderTokenKey);

        _vaultFolder = defaultVaultFolder;
    }

    public async Task UploadFileAsync(StorageFile file)
    {
        await EnsureVaultFolderAsync();

        // 1. Read original file bytes
        var buffer = await FileIO.ReadBufferAsync(file);
        byte[] data = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(data);
        }

        // 2. Compute SHA-256 hash for integrity verification
        byte[] hash = SHA256.HashData(data);

        // 3. Build metadata (embedded in .qd header)
        var metadata = new FileMetadata
        {
            OriginalName = file.Name,
            OriginalSize = data.Length,
            UploadedAt = DateTime.UtcNow,
            SHA256Hash = Convert.ToBase64String(hash)
        };

        // 4. Get ML-KEM public key for encryption
        var publicKey = _identityService.MlKemPublicKey
            ?? throw new InvalidOperationException("Vault not unlocked — cannot encrypt.");

        // 5. Encrypt using CryptoService (QDRIVE11: metadata embedded in header)
        var encryptedData = _cryptoService.EncryptFile(data, publicKey, metadata);

        // Update EncryptedSize now that we know it
        metadata.EncryptedSize = encryptedData.Length;

        // 6. Write encrypted file (handle duplicate names)
        var encryptedFileName = $"{file.Name}.qd";
        var encryptedFile = await _vaultFolder!.CreateFileAsync(
            encryptedFileName,
            CreationCollisionOption.GenerateUniqueName);

        await FileIO.WriteBytesAsync(encryptedFile, encryptedData);
    }

    public async Task<List<FileMetadata>> ListFilesAsync()
    {
        await EnsureVaultFolderAsync();

        var files = await _vaultFolder!.GetFilesAsync();
        var qdFiles = files.Where(f => f.Name.EndsWith(".qd"));

        var results = new List<FileMetadata>();

        foreach (var qdFile in qdFiles)
        {
            try
            {
                // Try reading embedded metadata from QDRIVE11 header
                var buffer = await FileIO.ReadBufferAsync(qdFile);
                byte[] fileData = new byte[buffer.Length];
                using (var reader = DataReader.FromBuffer(buffer))
                {
                    reader.ReadBytes(fileData);
                }

                var metadata = CryptoService.ReadMetadata(fileData);
                if (metadata is not null)
                    results.Add(metadata);
            }
            catch
            {
                // Skip corrupt files
            }
        }

        return results.OrderByDescending(f => f.UploadedAt).ToList();
    }

    public async Task<byte[]> DownloadFileAsync(string fileName)
    {
        await EnsureVaultFolderAsync();

        // 1. Find encrypted file
        var encryptedFileName = $"{fileName}.qd";
        var encryptedFile = await _vaultFolder!.GetFileAsync(encryptedFileName);

        // 2. Read encrypted data
        var buffer = await FileIO.ReadBufferAsync(encryptedFile);
        byte[] encryptedData = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(encryptedData);
        }

        // 3. Get ML-KEM private key for decryption
        var privateKey = _identityService.MlKemPrivateKey
            ?? throw new InvalidOperationException("Vault not unlocked — cannot decrypt.");

        // 4. Decrypt
        var (decryptedData, metadata) = _cryptoService.DecryptFile(encryptedData, privateKey);

        // 5. Verify integrity via SHA-256
        if (metadata?.SHA256Hash is string expectedHash)
        {
            var actualHash = Convert.ToBase64String(SHA256.HashData(decryptedData));
            if (actualHash != expectedHash)
            {
                throw new InvalidDataException(
                    "File integrity check failed! The file may be corrupted or tampered with.");
            }
        }

        return decryptedData;
    }

    public async Task DeleteFileAsync(string fileName)
    {
        await EnsureVaultFolderAsync();

        var encryptedFileName = $"{fileName}.qd";

        try
        {
            var encryptedFile = await _vaultFolder!.GetFileAsync(encryptedFileName);
            await encryptedFile.DeleteAsync();
        }
        catch (FileNotFoundException) { }
    }
}
