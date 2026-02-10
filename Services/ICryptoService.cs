using quantum_drive.Models;

namespace quantum_drive.Services;

public interface ICryptoService
{
    byte[] EncryptFile(byte[] data, byte[] mlKemPublicKey, FileMetadata? metadata = null);
    (byte[] Data, FileMetadata? Metadata) DecryptFile(byte[] encryptedData, byte[] mlKemPrivateKey);
}
