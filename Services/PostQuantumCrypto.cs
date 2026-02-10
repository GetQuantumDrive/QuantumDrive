using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace quantum_drive.Services;

/// <summary>
/// Real ML-KEM-1024 post-quantum cryptography implementation using BouncyCastle.
/// Keys are stored in ASN.1 (SubjectPublicKeyInfo / PrivateKeyInfo) format
/// so they can be reconstructed from raw bytes.
/// </summary>
public class PostQuantumCrypto : IPostQuantumCrypto
{
    public MlKemKeyPair GenerateKeyPair()
    {
        var generator = new MLKemKeyPairGenerator();
        generator.Init(new MLKemKeyGenerationParameters(new SecureRandom(), MLKemParameters.ml_kem_1024));

        AsymmetricCipherKeyPair keyPair = generator.GenerateKeyPair();

        byte[] publicKey = SubjectPublicKeyInfoFactory
            .CreateSubjectPublicKeyInfo(keyPair.Public).GetEncoded();
        byte[] privateKey = PrivateKeyInfoFactory
            .CreatePrivateKeyInfo(keyPair.Private).GetEncoded();

        return new MlKemKeyPair(publicKey, privateKey);
    }

    public MlKemEncapsulationResult Encapsulate(byte[] publicKey)
    {
        var publicKeyParams = (MLKemPublicKeyParameters)PublicKeyFactory.CreateKey(publicKey);

        var encapsulator = new MLKemEncapsulator(MLKemParameters.ml_kem_1024);
        encapsulator.Init(publicKeyParams);

        byte[] capsule = new byte[encapsulator.EncapsulationLength];
        byte[] sharedSecret = new byte[encapsulator.SecretLength];
        encapsulator.Encapsulate(capsule, 0, capsule.Length, sharedSecret, 0, sharedSecret.Length);

        return new MlKemEncapsulationResult(capsule, sharedSecret);
    }

    public byte[] Decapsulate(byte[] capsule, byte[] privateKey)
    {
        var privateKeyParams = (MLKemPrivateKeyParameters)PrivateKeyFactory.CreateKey(privateKey);

        var decapsulator = new MLKemDecapsulator(MLKemParameters.ml_kem_1024);
        decapsulator.Init(privateKeyParams);

        byte[] sharedSecret = new byte[decapsulator.SecretLength];
        decapsulator.Decapsulate(capsule, 0, capsule.Length, sharedSecret, 0, sharedSecret.Length);

        return sharedSecret;
    }
}
