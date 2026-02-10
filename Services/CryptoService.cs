using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using quantum_drive.Models;

namespace quantum_drive.Services;

/// <summary>
/// Implements the QuantumDrive .qd encrypted file format.
///
/// QDRIVE11 File Layout:
/// ┌─────────────────────────────────────────────────┐
/// │ Magic Bytes: "QDRIVE11" (8 bytes)               │
/// │ Metadata Length: uint32 LE (4 bytes)             │
/// │ Metadata JSON: UTF-8 (variable)                 │
/// ├─────────────────────────────────────────────────┤
/// │ Nonce: 12 bytes (RANDOM, UNIQUE PER FILE)       │
/// │ ML-KEM Capsule: 1568 bytes                      │
/// │ Auth Tag: 16 bytes (AES-GCM authentication)     │
/// │ Ciphertext: AES-256-GCM encrypted file data     │
/// └─────────────────────────────────────────────────┘
/// </summary>
public class CryptoService : ICryptoService
{
    private const string Magic = "QDRIVE11";
    private const int MagicSize = 8;
    private const int NonceSize = 12;
    private const int CapsuleSize = 1568;
    private const int TagSize = 16;

    private readonly IPostQuantumCrypto _pqCrypto;

    public CryptoService(IPostQuantumCrypto pqCrypto)
    {
        _pqCrypto = pqCrypto;
    }

    public byte[] EncryptFile(byte[] data, byte[] mlKemPublicKey, FileMetadata? metadata = null)
    {
        // 1. Encapsulate to get shared secret (used as File Encryption Key)
        var encapResult = _pqCrypto.Encapsulate(mlKemPublicKey);
        byte[] fek = encapResult.SharedSecret;
        byte[] capsule = encapResult.Capsule;

        // 2. Generate a UNIQUE random nonce for THIS file
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);

        // 3. Encrypt file data with AES-256-GCM
        byte[] ciphertext = new byte[data.Length];
        byte[] tag = new byte[TagSize];
        using (var aes = new AesGcm(fek, TagSize))
        {
            aes.Encrypt(nonce, data, ciphertext, tag);
        }

        // 4. Construct .qd file (QDRIVE11 format)
        using var ms = new MemoryStream();

        // Write magic
        ms.Write(Encoding.ASCII.GetBytes(Magic));

        // Write metadata section
        byte[] metaBytes = metadata is not null
            ? JsonSerializer.SerializeToUtf8Bytes(metadata)
            : Array.Empty<byte>();
        ms.Write(BitConverter.GetBytes((uint)metaBytes.Length)); // 4 bytes LE
        if (metaBytes.Length > 0)
            ms.Write(metaBytes);

        // Write encrypted payload
        ms.Write(nonce);       // 12 bytes
        ms.Write(capsule);     // 1568 bytes
        ms.Write(tag);         // 16 bytes
        ms.Write(ciphertext);  // variable

        return ms.ToArray();
    }

    public (byte[] Data, FileMetadata? Metadata) DecryptFile(byte[] encryptedData, byte[] mlKemPrivateKey)
    {
        using var ms = new MemoryStream(encryptedData);
        using var br = new BinaryReader(ms);

        // 1. Read magic bytes
        string magic = Encoding.ASCII.GetString(br.ReadBytes(MagicSize));

        if (magic != Magic)
            throw new InvalidDataException($"Invalid file format. Expected '{Magic}', got '{magic}'.");

        FileMetadata? metadata = null;
        uint metaLength = br.ReadUInt32();
        if (metaLength > 0)
        {
            byte[] metaBytes = br.ReadBytes((int)metaLength);
            metadata = JsonSerializer.Deserialize<FileMetadata>(metaBytes);
        }

        // 2. Read nonce (unique to this file)
        byte[] nonce = br.ReadBytes(NonceSize);

        // 3. Read ML-KEM capsule
        byte[] capsule = br.ReadBytes(CapsuleSize);

        // 4. Decapsulate to recover File Encryption Key
        byte[] fek = _pqCrypto.Decapsulate(capsule, mlKemPrivateKey);

        // 5. Read auth tag and ciphertext
        byte[] tag = br.ReadBytes(TagSize);
        byte[] ciphertext = br.ReadBytes((int)(ms.Length - ms.Position));

        // 6. Decrypt with AES-256-GCM using the file's own nonce
        byte[] plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(fek, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return (plaintext, metadata);
    }

    /// <summary>
    /// Reads metadata from a .qd file without performing full decryption.
    /// </summary>
    public static FileMetadata? ReadMetadata(byte[] encryptedData)
    {
        if (encryptedData.Length < MagicSize + 4)
            return null;

        string magic = Encoding.ASCII.GetString(encryptedData, 0, MagicSize);
        if (magic != Magic)
            return null;

        uint metaLength = BitConverter.ToUInt32(encryptedData, MagicSize);
        if (metaLength == 0 || encryptedData.Length < MagicSize + 4 + metaLength)
            return null;

        var metaSpan = new ReadOnlySpan<byte>(encryptedData, MagicSize + 4, (int)metaLength);
        return JsonSerializer.Deserialize<FileMetadata>(metaSpan);
    }
}
