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
    public void CryptoService_EncryptDecrypt_RoundTrip()
    {
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = RandomNumberGenerator.GetBytes(1024);

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey);
        var (decrypted, _) = _cryptoService.DecryptFile(encrypted, keyPair.PrivateKey);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void CryptoService_WrongKey_FailsDecrypt()
    {
        var keyPair1 = _pqCrypto.GenerateKeyPair();
        var keyPair2 = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = RandomNumberGenerator.GetBytes(512);

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair1.PublicKey);

        // AES-GCM authentication tag mismatch with wrong key
        Assert.ThrowsAny<CryptographicException>(
            () => _cryptoService.DecryptFile(encrypted, keyPair2.PrivateKey));
    }

    [Fact]
    public void CryptoService_MetadataPreserved()
    {
        var keyPair = _pqCrypto.GenerateKeyPair();
        byte[] plaintext = new byte[] { 1, 2, 3 };
        var metadata = new FileMetadata
        {
            OriginalName = "test.txt",
            OriginalSize = 3,
            EncryptedSize = 0,
            UploadedAt = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            SHA256Hash = "abc123"
        };

        byte[] encrypted = _cryptoService.EncryptFile(plaintext, keyPair.PublicKey, metadata);
        var (decrypted, recoveredMeta) = _cryptoService.DecryptFile(encrypted, keyPair.PrivateKey);

        Assert.Equal(plaintext, decrypted);
        Assert.NotNull(recoveredMeta);
        Assert.Equal("test.txt", recoveredMeta!.OriginalName);
        Assert.Equal(3, recoveredMeta.OriginalSize);
        Assert.Equal("abc123", recoveredMeta.SHA256Hash);
    }
}
