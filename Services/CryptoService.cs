using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using quantum_drive.Models;

namespace quantum_drive.Services;

/// <summary>
/// Implements the QuantumDrive .qd encrypted file format (QDRIVE01).
///
/// QDRIVE01 File Layout:
/// ┌──────────────────────────────────────────────────────────┐
/// │ Magic: "QDRIVE01" (8 bytes)                              │
/// │ ML-KEM-1024 Capsule (1568 bytes)                         │
/// ├──────────────────────────────────────────────────────────┤
/// │ Meta Nonce (12 bytes, random)                            │
/// │ Meta Ciphertext Length (4 bytes, uint32 LE)               │
/// │ Meta Tag (16 bytes)                                      │
/// │ Meta Ciphertext (variable — encrypted FileMetadata JSON) │
/// ├──────────────────────────────────────────────────────────┤
/// │ Data Nonce Prefix (8 bytes, random)                      │
/// │ Chunk 0: Tag (16) + Ciphertext (up to 65536 bytes)       │
/// │ Chunk 1: Tag (16) + Ciphertext (up to 65536 bytes)       │
/// │ ...                                                      │
/// │ Last Chunk: Tag (16) + Ciphertext (1–65536 bytes)        │
/// └──────────────────────────────────────────────────────────┘
///
/// Key derivation:
///   shared_secret = ML-KEM-1024.Decapsulate(capsule, private_key)
///   file_key      = HKDF-SHA256(ikm=shared_secret, info="qdrive1-fek")
///
/// Metadata uses file_key with a random 12-byte nonce, capsule as AAD.
/// Data chunks use file_key with counter nonces (prefix‖uint32BE(i)), capsule as AAD.
/// Each chunk is independently authenticated — streaming with constant memory.
/// </summary>
public class CryptoService : ICryptoService
{
    private const string Magic = "QDRIVE01";
    private const int MagicSize = 8;
    private const int CapsuleSize = 1568;       // ML-KEM-1024
    private const int NonceSize = 12;           // AES-256-GCM nonce
    private const int TagSize = 16;             // AES-256-GCM auth tag
    private const int MetaLenSize = 4;          // uint32 for metadata ciphertext length
    private const int NoncePrefixSize = 8;      // Random prefix for counter nonces
    private const int ChunkSize = 65536;        // 64KB per data chunk

    // Magic(8) + Capsule(1568) + MetaNonce(12) + MetaLen(4) = 1592
    private const int FixedHeaderSize = MagicSize + CapsuleSize + NonceSize + MetaLenSize;

    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes(Magic);
    private static readonly byte[] HkdfInfo = "qdrive1-fek"u8.ToArray();

    private readonly IPostQuantumCrypto _pqCrypto;

    public CryptoService(IPostQuantumCrypto pqCrypto)
    {
        _pqCrypto = pqCrypto;
    }

    // ──────────────────────────────────────────────
    //  Byte-array convenience API (wraps stream API)
    // ──────────────────────────────────────────────

    public byte[] EncryptFile(byte[] data, byte[] mlKemPublicKey, FileMetadata? metadata = null)
    {
        var meta = metadata ?? new FileMetadata { UploadedAt = DateTime.UtcNow };
        meta.OriginalSize = data.Length;

        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        // MemoryStream async ops complete synchronously — safe to block
        EncryptToStreamAsync(input, output, mlKemPublicKey, meta).GetAwaiter().GetResult();
        return output.ToArray();
    }

    public (byte[] Data, FileMetadata? Metadata) DecryptFile(byte[] encryptedData, byte[] mlKemPrivateKey)
    {
        using var input = new MemoryStream(encryptedData, writable: false);
        using var output = new MemoryStream();
        var metadata = DecryptToStreamAsync(input, output, mlKemPrivateKey).GetAwaiter().GetResult();
        return (output.ToArray(), metadata);
    }

    // ──────────────────────────────────────────────
    //  Streaming API — constant memory usage
    // ──────────────────────────────────────────────

    public async Task EncryptToStreamAsync(Stream input, Stream output, byte[] mlKemPublicKey, FileMetadata metadata)
    {
        // 1. Encapsulate and derive file key
        var encapResult = _pqCrypto.Encapsulate(mlKemPublicKey);
        byte[] capsule = encapResult.Capsule;
        byte[] fileKey = DeriveFileKey(encapResult.SharedSecret);
        CryptographicOperations.ZeroMemory(encapResult.SharedSecret);

        try
        {
            // 2. Write header
            await output.WriteAsync(MagicBytes);
            await output.WriteAsync(capsule);

            // 3. Encrypt and write metadata
            byte[] metaPlaintext = JsonSerializer.SerializeToUtf8Bytes(metadata);
            byte[] metaNonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] metaCiphertext = new byte[metaPlaintext.Length];
            byte[] metaTag = new byte[TagSize];

            using (var aes = new AesGcm(fileKey, TagSize))
            {
                aes.Encrypt(metaNonce, metaPlaintext, metaCiphertext, metaTag, capsule);
            }

            await output.WriteAsync(metaNonce);
            byte[] metaLenBytes = new byte[MetaLenSize];
            BinaryPrimitives.WriteUInt32LittleEndian(metaLenBytes, (uint)metaCiphertext.Length);
            await output.WriteAsync(metaLenBytes);
            await output.WriteAsync(metaTag);
            await output.WriteAsync(metaCiphertext);

            // 4. Write data nonce prefix and stream-encrypt chunks
            byte[] noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
            await output.WriteAsync(noncePrefix);

            byte[] readBuffer = new byte[ChunkSize];
            byte[] cipherBuffer = new byte[ChunkSize];
            byte[] chunkTag = new byte[TagSize];
            byte[] chunkNonce = new byte[NonceSize];
            Buffer.BlockCopy(noncePrefix, 0, chunkNonce, 0, NoncePrefixSize);

            using var aesData = new AesGcm(fileKey, TagSize);

            long remaining = metadata.OriginalSize;
            uint chunkIndex = 0;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(ChunkSize, remaining);
                int bytesRead = await ReadFullAsync(input, readBuffer, toRead);
                if (bytesRead == 0) break;

                BinaryPrimitives.WriteUInt32BigEndian(chunkNonce.AsSpan(NoncePrefixSize), chunkIndex);

                aesData.Encrypt(chunkNonce,
                    readBuffer.AsSpan(0, bytesRead),
                    cipherBuffer.AsSpan(0, bytesRead),
                    chunkTag,
                    capsule);

                await output.WriteAsync(chunkTag);
                await output.WriteAsync(cipherBuffer.AsMemory(0, bytesRead));

                remaining -= bytesRead;
                chunkIndex++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    public async Task<FileMetadata?> DecryptToStreamAsync(Stream input, Stream output, byte[] mlKemPrivateKey)
    {
        // 1. Read and verify magic
        byte[] magicBuf = new byte[MagicSize];
        await input.ReadExactlyAsync(magicBuf);
        if (!magicBuf.AsSpan().SequenceEqual(MagicBytes))
            throw new InvalidDataException($"Invalid file format — expected '{Magic}'.");

        // 2. Read capsule and derive file key
        byte[] capsule = new byte[CapsuleSize];
        await input.ReadExactlyAsync(capsule);

        byte[] sharedSecret = _pqCrypto.Decapsulate(capsule, mlKemPrivateKey);
        byte[] fileKey = DeriveFileKey(sharedSecret);
        CryptographicOperations.ZeroMemory(sharedSecret);

        try
        {
            // 3. Decrypt metadata
            byte[] metaNonce = new byte[NonceSize];
            await input.ReadExactlyAsync(metaNonce);

            byte[] metaLenBuf = new byte[MetaLenSize];
            await input.ReadExactlyAsync(metaLenBuf);
            uint metaLen = BinaryPrimitives.ReadUInt32LittleEndian(metaLenBuf);

            byte[] metaTag = new byte[TagSize];
            await input.ReadExactlyAsync(metaTag);

            byte[] metaCiphertext = new byte[metaLen];
            await input.ReadExactlyAsync(metaCiphertext);

            FileMetadata? metadata = null;
            if (metaLen > 0)
            {
                byte[] metaPlaintext = new byte[metaLen];
                using (var aes = new AesGcm(fileKey, TagSize))
                {
                    aes.Decrypt(metaNonce, metaCiphertext, metaTag, metaPlaintext, capsule);
                }
                metadata = JsonSerializer.Deserialize<FileMetadata>(metaPlaintext);
            }

            if (metadata is null)
                throw new InvalidDataException("Missing or invalid metadata in encrypted file.");

            // 4. Read data nonce prefix and stream-decrypt chunks
            byte[] noncePrefix = new byte[NoncePrefixSize];
            await input.ReadExactlyAsync(noncePrefix);

            byte[] chunkNonce = new byte[NonceSize];
            Buffer.BlockCopy(noncePrefix, 0, chunkNonce, 0, NoncePrefixSize);

            byte[] cipherBuffer = new byte[ChunkSize];
            byte[] plainBuffer = new byte[ChunkSize];
            byte[] chunkTag = new byte[TagSize];

            using var aesData = new AesGcm(fileKey, TagSize);

            long remaining = metadata.OriginalSize;
            uint chunkIndex = 0;

            while (remaining > 0)
            {
                int chunkDataSize = (int)Math.Min(ChunkSize, remaining);

                await input.ReadExactlyAsync(chunkTag);
                await input.ReadExactlyAsync(cipherBuffer.AsMemory(0, chunkDataSize));

                BinaryPrimitives.WriteUInt32BigEndian(chunkNonce.AsSpan(NoncePrefixSize), chunkIndex);

                aesData.Decrypt(chunkNonce,
                    cipherBuffer.AsSpan(0, chunkDataSize),
                    chunkTag,
                    plainBuffer.AsSpan(0, chunkDataSize),
                    capsule);

                await output.WriteAsync(plainBuffer.AsMemory(0, chunkDataSize));

                remaining -= chunkDataSize;
                chunkIndex++;
            }

            return metadata;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    // ──────────────────────────────────────────────
    //  Metadata-only operations
    // ──────────────────────────────────────────────

    public async Task<FileMetadata?> ReadMetadataAsync(Stream fileStream, byte[] mlKemPrivateKey)
    {
        // Read the fixed header prefix
        byte[] prefix = new byte[FixedHeaderSize];
        int read = await ReadAtLeastAsync(fileStream, prefix, FixedHeaderSize).ConfigureAwait(false);
        if (read < FixedHeaderSize) return null;

        // Verify magic
        if (!prefix.AsSpan(0, MagicSize).SequenceEqual(MagicBytes))
            return null;

        uint metaLen = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(FixedHeaderSize - MetaLenSize));
        if (metaLen == 0) return null;

        // Read meta tag + ciphertext
        int metaSectionSize = TagSize + (int)metaLen;
        byte[] metaSection = new byte[metaSectionSize];
        read = await ReadAtLeastAsync(fileStream, metaSection, metaSectionSize).ConfigureAwait(false);
        if (read < metaSectionSize) return null;

        // Extract capsule
        byte[] capsule = new byte[CapsuleSize];
        Buffer.BlockCopy(prefix, MagicSize, capsule, 0, CapsuleSize);

        // Extract meta nonce
        byte[] metaNonce = new byte[NonceSize];
        Buffer.BlockCopy(prefix, MagicSize + CapsuleSize, metaNonce, 0, NonceSize);

        // Extract meta tag + ciphertext
        byte[] metaTag = new byte[TagSize];
        Buffer.BlockCopy(metaSection, 0, metaTag, 0, TagSize);

        byte[] metaCiphertext = new byte[metaLen];
        Buffer.BlockCopy(metaSection, TagSize, metaCiphertext, 0, (int)metaLen);

        // Decapsulate and derive key
        byte[] sharedSecret = _pqCrypto.Decapsulate(capsule, mlKemPrivateKey);
        byte[] fileKey = DeriveFileKey(sharedSecret);
        CryptographicOperations.ZeroMemory(sharedSecret);

        try
        {
            byte[] metaPlaintext = new byte[metaLen];
            using var aes = new AesGcm(fileKey, TagSize);
            aes.Decrypt(metaNonce, metaCiphertext, metaTag, metaPlaintext, capsule);
            return JsonSerializer.Deserialize<FileMetadata>(metaPlaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    public async Task RewriteMetadataAsync(Stream source, Stream destination, byte[] mlKemPrivateKey, FileMetadata newMetadata)
    {
        // 1. Read source header
        byte[] prefix = new byte[FixedHeaderSize];
        await source.ReadExactlyAsync(prefix).ConfigureAwait(false);

        if (!prefix.AsSpan(0, MagicSize).SequenceEqual(MagicBytes))
            throw new InvalidDataException($"Invalid file format — expected '{Magic}'.");

        // 2. Extract capsule and derive file key
        byte[] capsule = new byte[CapsuleSize];
        Buffer.BlockCopy(prefix, MagicSize, capsule, 0, CapsuleSize);

        byte[] sharedSecret = _pqCrypto.Decapsulate(capsule, mlKemPrivateKey);
        byte[] fileKey = DeriveFileKey(sharedSecret);
        CryptographicOperations.ZeroMemory(sharedSecret);

        // 3. Skip old metadata section in source
        uint oldMetaLen = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(FixedHeaderSize - MetaLenSize));
        source.Seek(TagSize + oldMetaLen, SeekOrigin.Current);

        // 4. Encrypt new metadata with a fresh nonce
        byte[] metaPlaintext = JsonSerializer.SerializeToUtf8Bytes(newMetadata);
        byte[] metaNonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] metaCiphertext = new byte[metaPlaintext.Length];
        byte[] metaTag = new byte[TagSize];

        using (var aes = new AesGcm(fileKey, TagSize))
        {
            aes.Encrypt(metaNonce, metaPlaintext, metaCiphertext, metaTag, capsule);
        }
        CryptographicOperations.ZeroMemory(fileKey);

        // 5. Write new file: header + new metadata + original data section (byte-copied)
        await destination.WriteAsync(MagicBytes).ConfigureAwait(false);
        await destination.WriteAsync(capsule).ConfigureAwait(false);
        await destination.WriteAsync(metaNonce).ConfigureAwait(false);

        byte[] metaLenBytes = new byte[MetaLenSize];
        BinaryPrimitives.WriteUInt32LittleEndian(metaLenBytes, (uint)metaCiphertext.Length);
        await destination.WriteAsync(metaLenBytes).ConfigureAwait(false);
        await destination.WriteAsync(metaTag).ConfigureAwait(false);
        await destination.WriteAsync(metaCiphertext).ConfigureAwait(false);

        // 6. Copy data section as-is (nonce prefix + all chunks — no crypto needed)
        await source.CopyToAsync(destination).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────
    //  Internal helpers
    // ──────────────────────────────────────────────

    private static byte[] DeriveFileKey(byte[] sharedSecret)
    {
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            outputLength: 32,
            salt: Array.Empty<byte>(),
            info: HkdfInfo);
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes, returning less only at EOF.</summary>
    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset)).ConfigureAwait(false);
            if (n == 0) break;
            offset += n;
        }
        return offset;
    }

    /// <summary>Reads at least <paramref name="count"/> bytes into <paramref name="buffer"/>, returning actual count.</summary>
    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, count - offset)).ConfigureAwait(false);
            if (n == 0) break;
            offset += n;
        }
        return offset;
    }
}
