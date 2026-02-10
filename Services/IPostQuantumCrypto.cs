namespace quantum_drive.Services;

public record MlKemKeyPair(byte[] PublicKey, byte[] PrivateKey);
public record MlKemEncapsulationResult(byte[] Capsule, byte[] SharedSecret);

public interface IPostQuantumCrypto
{
    MlKemKeyPair GenerateKeyPair();
    MlKemEncapsulationResult Encapsulate(byte[] publicKey);
    byte[] Decapsulate(byte[] capsule, byte[] privateKey);
}
