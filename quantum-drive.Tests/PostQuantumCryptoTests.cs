using quantum_drive.Services;
using Xunit;

namespace quantum_drive.Tests;

public class PostQuantumCryptoTests
{
    private readonly PostQuantumCrypto _crypto = new();

    [Fact]
    public void GenerateKeyPair_ProducesCorrectSizes()
    {
        var keyPair = _crypto.GenerateKeyPair();

        // ASN.1-wrapped ML-KEM-1024 key sizes
        Assert.Equal(1590, keyPair.PublicKey.Length);
        Assert.Equal(3266, keyPair.PrivateKey.Length);
    }

    [Fact]
    public void EncapsulateDecapsulate_RoundTrip()
    {
        var keyPair = _crypto.GenerateKeyPair();

        var encapResult = _crypto.Encapsulate(keyPair.PublicKey);
        byte[] decapsulatedSecret = _crypto.Decapsulate(encapResult.Capsule, keyPair.PrivateKey);

        Assert.Equal(encapResult.SharedSecret, decapsulatedSecret);
    }

    [Fact]
    public void Encapsulate_ProducesCorrectCapsuleSize()
    {
        var keyPair = _crypto.GenerateKeyPair();

        var encapResult = _crypto.Encapsulate(keyPair.PublicKey);

        Assert.Equal(1568, encapResult.Capsule.Length);
        Assert.Equal(32, encapResult.SharedSecret.Length);
    }

    [Fact]
    public void DifferentKeyPairs_ProduceDifferentKeys()
    {
        var keyPair1 = _crypto.GenerateKeyPair();
        var keyPair2 = _crypto.GenerateKeyPair();

        Assert.NotEqual(keyPair1.PublicKey, keyPair2.PublicKey);
        Assert.NotEqual(keyPair1.PrivateKey, keyPair2.PrivateKey);
    }

    [Fact]
    public void WrongPrivateKey_ProducesDifferentSecret()
    {
        var keyPair1 = _crypto.GenerateKeyPair();
        var keyPair2 = _crypto.GenerateKeyPair();

        var encapResult = _crypto.Encapsulate(keyPair1.PublicKey);

        // Decapsulate with wrong private key — ML-KEM returns an implicit reject
        // (a deterministic but wrong secret), not an exception
        byte[] wrongSecret = _crypto.Decapsulate(encapResult.Capsule, keyPair2.PrivateKey);

        Assert.NotEqual(encapResult.SharedSecret, wrongSecret);
    }
}
