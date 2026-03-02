using System.Security.Cryptography;
using quantum_drive.Models;
using quantum_drive.Services;
using Xunit;

namespace quantum_drive.Tests;

public class CryptoServiceTests
{
    private readonly PostQuantumCrypto _pqCrypto = new();
    private readonly CryptoService _cryptoService;

    public CryptoServiceTests()
    {
        _cryptoService = new CryptoService(_pqCrypto);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_SmallFile()
    {
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = RandomNumberGenerator.GetBytes(1024);

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey);
        var (decrypted, _) = _cryptoService.DecryptFile(encrypted, keyPair.PrivateKey);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_MultiChunk()
    {
        // 200KB — spans multiple 64KB chunks
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = RandomNumberGenerator.GetBytes(200_000);

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey);
        var (decrypted, _) = _cryptoService.DecryptFile(encrypted, keyPair.PrivateKey);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_ExactChunkBoundary()
    {
        // Exactly 64KB — one full chunk, no partial
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = RandomNumberGenerator.GetBytes(65536);

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey);
        var (decrypted, _) = _cryptoService.DecryptFile(encrypted, keyPair.PrivateKey);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_EmptyFile()
    {
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = Array.Empty<byte>();

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey);
        var (decrypted, _) = _cryptoService.DecryptFile(encrypted, keyPair.PrivateKey);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_SingleByte()
    {
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = new byte[] { 0x42 };

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey);
        var (decrypted, _) = _cryptoService.DecryptFile(encrypted, keyPair.PrivateKey);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void WrongKey_FailsDecrypt()
    {
        var keyPair1 = _pqCrypto.GenerateKeyPair();
        var keyPair2 = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = RandomNumberGenerator.GetBytes(512);

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair1.PublicKey);

        // AES-GCM auth tag mismatch with wrong key
        Assert.ThrowsAny<CryptographicException>(
            () => _cryptoService.DecryptFile(encrypted, keyPair2.PrivateKey));
    }

    [Fact]
    public void MetadataPreserved()
    {
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = new byte[] { 1, 2, 3 };
        var metadata = new FileMetadata
        {
            OriginalName = "test.txt",
            OriginalSize = 3,
            UploadedAt = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc)
        };

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey, metadata);
        var (decrypted, recoveredMeta) = _cryptoService.DecryptFile(encrypted, keyPair.PrivateKey);

        Assert.Equal(plaintext, decrypted);
        Assert.NotNull(recoveredMeta);
        Assert.Equal("test.txt", recoveredMeta!.OriginalName);
        Assert.Equal(3, recoveredMeta.OriginalSize);
        Assert.Equal(new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc), recoveredMeta.UploadedAt);
    }

    [Fact]
    public void Magic_VerifiedOnDecrypt()
    {
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = new byte[] { 1, 2, 3 };

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey);

        // Corrupt the magic bytes
        encrypted[0] = (byte)'X';

        Assert.Throws<InvalidDataException>(
            () => _cryptoService.DecryptFile(encrypted, keyPair.PrivateKey));
    }

    [Fact]
    public void TamperedCiphertext_FailsAuth()
    {
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = RandomNumberGenerator.GetBytes(1024);

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey);

        // Flip a byte near the end (in the data section)
        encrypted[^10] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(
            () => _cryptoService.DecryptFile(encrypted, keyPair.PrivateKey));
    }

    [Fact]
    public async Task StreamingEncryptDecrypt_RoundTrip()
    {
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = RandomNumberGenerator.GetBytes(150_000);

        var metadata = new FileMetadata
        {
            OriginalName = "stream-test.bin",
            OriginalSize = plaintext.Length,
            UploadedAt = DateTime.UtcNow
        };

        using var inputEnc = new MemoryStream(plaintext);
        using var encrypted = new MemoryStream();
        await _cryptoService.EncryptToStreamAsync(inputEnc, encrypted, keyPair.PublicKey, metadata);

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        var recoveredMeta = await _cryptoService.DecryptToStreamAsync(encrypted, decrypted, keyPair.PrivateKey);

        Assert.Equal(plaintext, decrypted.ToArray());
        Assert.NotNull(recoveredMeta);
        Assert.Equal("stream-test.bin", recoveredMeta!.OriginalName);
    }

    [Fact]
    public async Task ReadMetadata_WithoutDecryptingData()
    {
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = RandomNumberGenerator.GetBytes(100_000);

        var metadata = new FileMetadata
        {
            OriginalName = "metadata-only.txt",
            OriginalSize = plaintext.Length,
            UploadedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey, metadata);

        using var stream = new MemoryStream(encrypted);
        var readMeta = await _cryptoService.ReadMetadataAsync(stream, keyPair.PrivateKey);

        Assert.NotNull(readMeta);
        Assert.Equal("metadata-only.txt", readMeta!.OriginalName);
        Assert.Equal(plaintext.Length, readMeta.OriginalSize);
    }

    [Fact]
    public async Task RewriteMetadata_PreservesData()
    {
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = RandomNumberGenerator.GetBytes(80_000);

        var originalMeta = new FileMetadata
        {
            OriginalName = "original.txt",
            OriginalSize = plaintext.Length,
            UploadedAt = DateTime.UtcNow
        };

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey, originalMeta);

        // Rewrite metadata (simulates MOVE/rename)
        var newMeta = new FileMetadata
        {
            OriginalName = "renamed.txt",
            OriginalSize = plaintext.Length,
            UploadedAt = originalMeta.UploadedAt
        };

        using var source = new MemoryStream(encrypted);
        using var destination = new MemoryStream();
        await _cryptoService.RewriteMetadataAsync(source, destination, keyPair.PrivateKey, newMeta);

        // Decrypt the rewritten file — data should be intact, metadata should be updated
        destination.Position = 0;
        using var decrypted = new MemoryStream();
        var recoveredMeta = await _cryptoService.DecryptToStreamAsync(destination, decrypted, keyPair.PrivateKey);

        Assert.Equal(plaintext, decrypted.ToArray());
        Assert.NotNull(recoveredMeta);
        Assert.Equal("renamed.txt", recoveredMeta!.OriginalName);
    }

    [Fact]
    public void DifferentEncryptions_ProduceDifferentCiphertext()
    {
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = RandomNumberGenerator.GetBytes(256);

        byte[] encrypted1 = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey);
        byte[] encrypted2 = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey);

        // Different capsules + nonces → different ciphertext (IND-CPA)
        Assert.NotEqual(encrypted1, encrypted2);
    }
}
