using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using quantum_drive.Helpers;
using Windows.Storage;

namespace quantum_drive.Services;

public class IdentityService : IIdentityService
{
    private const string VaultFileName = "vault.identity";
    private const int SaltSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Argon2MemorySize = 65536; // 64 MB
    private const int Argon2Iterations = 3;
    private const int Argon2Parallelism = 4;
    private const int RecoveryKeySize = 32; // 256-bit recovery key

    private readonly IPostQuantumCrypto _pqCrypto;

    private byte[]? _mlKemPrivateKey;
    private byte[]? _mlKemPublicKey;
    private string? _passwordHint;
    private string? _vaultSaltBase64;

    public bool IsVaultCreated => File.Exists(GetVaultPath());
    public byte[]? MlKemPrivateKey => _mlKemPrivateKey;
    public byte[]? MlKemPublicKey => _mlKemPublicKey;
    public string? PasswordHint => _passwordHint;
    public string? VaultSaltBase64 => _vaultSaltBase64;

    public IdentityService(IPostQuantumCrypto pqCrypto)
    {
        _pqCrypto = pqCrypto;
    }

    public async Task<(byte[] PublicKey, byte[] PrivateKey, string RecoveryKey)> CreateVaultAsync(string password, string? passwordHint = null)
    {
        var keyPair = _pqCrypto.GenerateKeyPair();

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] masterKey = await DeriveKeyAsync(password, salt);

        // Encrypt private key with password-derived master key
        byte[] encryptedBlob = EncryptAndCombine(keyPair.PrivateKey, masterKey, nonce);

        // Generate recovery key
        byte[] recoveryKeyBytes = RandomNumberGenerator.GetBytes(RecoveryKeySize);
        string recoveryKeyFormatted = Base32Encoder.EncodeFormatted(recoveryKeyBytes);

        // Encrypt a second copy of the private key with recovery-derived master key
        byte[] recoverySalt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] recoveryNonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] recoveryMasterKey = await DeriveKeyAsync(recoveryKeyBytes, recoverySalt);
        byte[] recoveryEncryptedBlob = EncryptAndCombine(keyPair.PrivateKey, recoveryMasterKey, recoveryNonce);

        // Encrypt the recovery key itself with the password master key (for re-export)
        byte[] recoveryKeyNonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] recoveryKeyEncryptedBlob = EncryptAndCombine(recoveryKeyBytes, masterKey, recoveryKeyNonce);

        var vault = new VaultFile
        {
            Version = "1.1",
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            EncryptedMlKemPrivateKey = Convert.ToBase64String(encryptedBlob),
            PasswordHint = passwordHint ?? string.Empty,
            MlKemPublicKey = Convert.ToBase64String(keyPair.PublicKey),
            RecoverySalt = Convert.ToBase64String(recoverySalt),
            RecoveryNonce = Convert.ToBase64String(recoveryNonce),
            RecoveryEncryptedMlKemPrivateKey = Convert.ToBase64String(recoveryEncryptedBlob),
            RecoveryKeyEncrypted = Convert.ToBase64String(recoveryKeyEncryptedBlob),
            RecoveryKeyNonce = Convert.ToBase64String(recoveryKeyNonce)
        };

        string json = JsonSerializer.Serialize(vault, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(GetVaultPath(), json);

        _mlKemPrivateKey = keyPair.PrivateKey;
        _mlKemPublicKey = keyPair.PublicKey;
        _passwordHint = passwordHint;
        _vaultSaltBase64 = vault.Salt;

        CryptographicOperations.ZeroMemory(masterKey);
        CryptographicOperations.ZeroMemory(recoveryMasterKey);
        CryptographicOperations.ZeroMemory(recoveryKeyBytes);

        Debug.WriteLine("Vault created successfully with recovery key.");
        return (keyPair.PublicKey, keyPair.PrivateKey, recoveryKeyFormatted);
    }

    public async Task<bool> UnlockAsync(string password)
    {
        try
        {
            string json = await File.ReadAllTextAsync(GetVaultPath());
            var vault = JsonSerializer.Deserialize<VaultFile>(json);
            if (vault is null) return false;

            byte[] salt = Convert.FromBase64String(vault.Salt);
            byte[] nonce = Convert.FromBase64String(vault.Nonce);
            byte[] encryptedBlob = Convert.FromBase64String(vault.EncryptedMlKemPrivateKey);

            byte[] masterKey = await DeriveKeyAsync(password, salt);

            var (ciphertext, tag) = SplitCiphertextAndTag(encryptedBlob);
            byte[] privateKey = DecryptAesGcm(ciphertext, masterKey, nonce, tag);

            _mlKemPrivateKey = privateKey;
            _passwordHint = vault.PasswordHint;
            _vaultSaltBase64 = vault.Salt;

            if (!string.IsNullOrEmpty(vault.MlKemPublicKey))
            {
                _mlKemPublicKey = Convert.FromBase64String(vault.MlKemPublicKey);
            }

            CryptographicOperations.ZeroMemory(masterKey);

            Debug.WriteLine("Vault unlocked successfully.");
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            Debug.WriteLine("Unlock failed: Invalid password or tampered data.");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unlock failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> VerifyPasswordAsync(string password)
    {
        try
        {
            string json = await File.ReadAllTextAsync(GetVaultPath());
            var vault = JsonSerializer.Deserialize<VaultFile>(json);
            if (vault is null) return false;

            byte[] salt = Convert.FromBase64String(vault.Salt);
            byte[] nonce = Convert.FromBase64String(vault.Nonce);
            byte[] encryptedBlob = Convert.FromBase64String(vault.EncryptedMlKemPrivateKey);

            byte[] masterKey = await DeriveKeyAsync(password, salt);

            var (ciphertext, tag) = SplitCiphertextAndTag(encryptedBlob);
            DecryptAesGcm(ciphertext, masterKey, nonce, tag);
            CryptographicOperations.ZeroMemory(masterKey);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task ChangePasswordAsync(string oldPassword, string newPassword)
    {
        string json = await File.ReadAllTextAsync(GetVaultPath());
        var vault = JsonSerializer.Deserialize<VaultFile>(json)
            ?? throw new InvalidOperationException("Vault file is corrupt.");

        byte[] oldSalt = Convert.FromBase64String(vault.Salt);
        byte[] oldNonce = Convert.FromBase64String(vault.Nonce);
        byte[] encryptedBlob = Convert.FromBase64String(vault.EncryptedMlKemPrivateKey);

        // Decrypt with old password
        byte[] oldMasterKey = await DeriveKeyAsync(oldPassword, oldSalt);
        var (ciphertext, oldTag) = SplitCiphertextAndTag(encryptedBlob);
        byte[] privateKey = DecryptAesGcm(ciphertext, oldMasterKey, oldNonce, oldTag);

        // Re-encrypt with new password, new salt, new nonce
        byte[] newSalt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] newNonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] newMasterKey = await DeriveKeyAsync(newPassword, newSalt);

        byte[] newBlob = EncryptAndCombine(privateKey, newMasterKey, newNonce);

        var newVault = new VaultFile
        {
            Version = vault.Version,
            Salt = Convert.ToBase64String(newSalt),
            Nonce = Convert.ToBase64String(newNonce),
            EncryptedMlKemPrivateKey = Convert.ToBase64String(newBlob),
            PasswordHint = vault.PasswordHint,
            MlKemPublicKey = vault.MlKemPublicKey,
            // Preserve recovery fields unchanged
            RecoverySalt = vault.RecoverySalt,
            RecoveryNonce = vault.RecoveryNonce,
            RecoveryEncryptedMlKemPrivateKey = vault.RecoveryEncryptedMlKemPrivateKey
        };

        // Re-encrypt the stored recovery key with the new password master key
        if (!string.IsNullOrEmpty(vault.RecoveryKeyEncrypted) && !string.IsNullOrEmpty(vault.RecoveryKeyNonce))
        {
            // Decrypt recovery key with old master key
            byte[] oldRecoveryKeyBlob = Convert.FromBase64String(vault.RecoveryKeyEncrypted);
            byte[] oldRecoveryKeyNonce = Convert.FromBase64String(vault.RecoveryKeyNonce);
            var (rkCiphertext, rkTag) = SplitCiphertextAndTag(oldRecoveryKeyBlob);
            byte[] recoveryKeyBytes = DecryptAesGcm(rkCiphertext, oldMasterKey, oldRecoveryKeyNonce, rkTag);

            // Re-encrypt with new master key
            byte[] newRecoveryKeyNonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] newRecoveryKeyBlob = EncryptAndCombine(recoveryKeyBytes, newMasterKey, newRecoveryKeyNonce);

            newVault.RecoveryKeyEncrypted = Convert.ToBase64String(newRecoveryKeyBlob);
            newVault.RecoveryKeyNonce = Convert.ToBase64String(newRecoveryKeyNonce);

            CryptographicOperations.ZeroMemory(recoveryKeyBytes);
        }

        string newJson = JsonSerializer.Serialize(newVault, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(GetVaultPath(), newJson);

        _mlKemPrivateKey = privateKey;
        _vaultSaltBase64 = newVault.Salt;

        CryptographicOperations.ZeroMemory(oldMasterKey);
        CryptographicOperations.ZeroMemory(newMasterKey);

        Debug.WriteLine("Password changed successfully.");
    }

    public async Task<bool> RecoverWithKeyAsync(string recoveryKey, string newPassword)
    {
        try
        {
            string json = await File.ReadAllTextAsync(GetVaultPath());
            var vault = JsonSerializer.Deserialize<VaultFile>(json);
            if (vault is null) return false;

            // v1.0 vaults have no recovery fields
            if (string.IsNullOrEmpty(vault.RecoverySalt) ||
                string.IsNullOrEmpty(vault.RecoveryNonce) ||
                string.IsNullOrEmpty(vault.RecoveryEncryptedMlKemPrivateKey))
                return false;

            // Decode recovery key from Base32
            byte[] recoveryKeyBytes = Base32Encoder.Decode(recoveryKey);
            if (recoveryKeyBytes.Length != RecoveryKeySize)
                return false;

            // Derive recovery master key
            byte[] recoverySalt = Convert.FromBase64String(vault.RecoverySalt);
            byte[] recoveryNonce = Convert.FromBase64String(vault.RecoveryNonce);
            byte[] recoveryBlob = Convert.FromBase64String(vault.RecoveryEncryptedMlKemPrivateKey);

            byte[] recoveryMasterKey = await DeriveKeyAsync(recoveryKeyBytes, recoverySalt);

            var (ciphertext, tag) = SplitCiphertextAndTag(recoveryBlob);
            byte[] privateKey = DecryptAesGcm(ciphertext, recoveryMasterKey, recoveryNonce, tag);

            // Re-encrypt vault with new password
            byte[] newSalt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] newNonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] newMasterKey = await DeriveKeyAsync(newPassword, newSalt);
            byte[] newBlob = EncryptAndCombine(privateKey, newMasterKey, newNonce);

            // Re-encrypt recovery key with new master key
            byte[] newRecoveryKeyNonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] newRecoveryKeyBlob = EncryptAndCombine(recoveryKeyBytes, newMasterKey, newRecoveryKeyNonce);

            var newVault = new VaultFile
            {
                Version = vault.Version,
                Salt = Convert.ToBase64String(newSalt),
                Nonce = Convert.ToBase64String(newNonce),
                EncryptedMlKemPrivateKey = Convert.ToBase64String(newBlob),
                PasswordHint = vault.PasswordHint,
                MlKemPublicKey = vault.MlKemPublicKey,
                // Preserve recovery-encrypted private key copy unchanged
                RecoverySalt = vault.RecoverySalt,
                RecoveryNonce = vault.RecoveryNonce,
                RecoveryEncryptedMlKemPrivateKey = vault.RecoveryEncryptedMlKemPrivateKey,
                RecoveryKeyEncrypted = Convert.ToBase64String(newRecoveryKeyBlob),
                RecoveryKeyNonce = Convert.ToBase64String(newRecoveryKeyNonce)
            };

            string newJson = JsonSerializer.Serialize(newVault, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(GetVaultPath(), newJson);

            _mlKemPrivateKey = privateKey;
            _vaultSaltBase64 = newVault.Salt;
            _passwordHint = vault.PasswordHint;

            if (!string.IsNullOrEmpty(vault.MlKemPublicKey))
                _mlKemPublicKey = Convert.FromBase64String(vault.MlKemPublicKey);

            CryptographicOperations.ZeroMemory(recoveryMasterKey);
            CryptographicOperations.ZeroMemory(newMasterKey);
            CryptographicOperations.ZeroMemory(recoveryKeyBytes);

            Debug.WriteLine("Vault recovered with recovery key successfully.");
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            Debug.WriteLine("Recovery failed: Invalid recovery key.");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Recovery failed: {ex.Message}");
            return false;
        }
    }

    public async Task<string?> GetRecoveryKeyAsync(string password)
    {
        try
        {
            string json = await File.ReadAllTextAsync(GetVaultPath());
            var vault = JsonSerializer.Deserialize<VaultFile>(json);
            if (vault is null) return null;

            // v1.0 vaults have no recovery key
            if (string.IsNullOrEmpty(vault.RecoveryKeyEncrypted) ||
                string.IsNullOrEmpty(vault.RecoveryKeyNonce))
                return null;

            byte[] salt = Convert.FromBase64String(vault.Salt);
            byte[] masterKey = await DeriveKeyAsync(password, salt);

            byte[] recoveryKeyBlob = Convert.FromBase64String(vault.RecoveryKeyEncrypted);
            byte[] recoveryKeyNonce = Convert.FromBase64String(vault.RecoveryKeyNonce);

            var (ciphertext, tag) = SplitCiphertextAndTag(recoveryKeyBlob);
            byte[] recoveryKeyBytes = DecryptAesGcm(ciphertext, masterKey, recoveryKeyNonce, tag);

            string formatted = Base32Encoder.EncodeFormatted(recoveryKeyBytes);

            CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(recoveryKeyBytes);

            return formatted;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] EncryptAndCombine(byte[] plaintext, byte[] key, byte[] nonce)
    {
        byte[] ciphertext = EncryptAesGcm(plaintext, key, nonce, out byte[] tag);
        byte[] blob = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, blob, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, blob, ciphertext.Length, tag.Length);
        return blob;
    }

    private static (byte[] Ciphertext, byte[] Tag) SplitCiphertextAndTag(byte[] blob)
    {
        byte[] ciphertext = new byte[blob.Length - TagSize];
        byte[] tag = new byte[TagSize];
        Buffer.BlockCopy(blob, 0, ciphertext, 0, ciphertext.Length);
        Buffer.BlockCopy(blob, ciphertext.Length, tag, 0, TagSize);
        return (ciphertext, tag);
    }

    private static async Task<byte[]> DeriveKeyAsync(string password, byte[] salt)
    {
        return await DeriveKeyAsync(Encoding.UTF8.GetBytes(password), salt);
    }

    private static async Task<byte[]> DeriveKeyAsync(byte[] keyMaterial, byte[] salt)
    {
        return await Task.Run(() =>
        {
            using var argon2 = new Argon2id(keyMaterial);
            argon2.Salt = salt;
            argon2.DegreeOfParallelism = Argon2Parallelism;
            argon2.MemorySize = Argon2MemorySize;
            argon2.Iterations = Argon2Iterations;
            return argon2.GetBytes(KeySize);
        });
    }

    private static byte[] EncryptAesGcm(byte[] plaintext, byte[] key, byte[] nonce, out byte[] tag)
    {
        byte[] ciphertext = new byte[plaintext.Length];
        tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return ciphertext;
    }

    private static byte[] DecryptAesGcm(byte[] ciphertext, byte[] key, byte[] nonce, byte[] tag)
    {
        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static string GetVaultPath()
    {
        string localFolder = ApplicationData.Current.LocalFolder.Path;
        return Path.Combine(localFolder, VaultFileName);
    }

    private sealed class VaultFile
    {
        public string Version { get; set; } = "1.1";
        public string Salt { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string EncryptedMlKemPrivateKey { get; set; } = string.Empty;
        public string PasswordHint { get; set; } = string.Empty;
        public string MlKemPublicKey { get; set; } = string.Empty;

        // Recovery key fields (v1.1+)
        public string? RecoverySalt { get; set; }
        public string? RecoveryNonce { get; set; }
        public string? RecoveryEncryptedMlKemPrivateKey { get; set; }
        public string? RecoveryKeyEncrypted { get; set; }
        public string? RecoveryKeyNonce { get; set; }
    }
}
