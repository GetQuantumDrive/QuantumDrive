using quantum_drive.Models;

namespace quantum_drive.Services;

public interface ICryptoService
{
    /// <summary>Convenience API — encrypts data in memory. For large files, use the stream API.</summary>
    byte[] EncryptFile(byte[] data, byte[] mlKemPublicKey, FileMetadata? metadata = null);

    /// <summary>Convenience API — decrypts data in memory. For large files, use the stream API.</summary>
    (byte[] Data, FileMetadata? Metadata) DecryptFile(byte[] encryptedData, byte[] mlKemPrivateKey);

    /// <summary>
    /// Streaming encryption with chunked AEAD. Memory usage is ~128KB regardless of file size.
    /// metadata.OriginalSize must be set to the exact number of bytes in the input stream.
    /// </summary>
    Task EncryptToStreamAsync(Stream input, Stream output, byte[] mlKemPublicKey, FileMetadata metadata);

    /// <summary>
    /// Streaming decryption with chunked AEAD. Memory usage is ~128KB regardless of file size.
    /// Returns the decrypted metadata, writes decrypted data to the output stream.
    /// </summary>
    Task<FileMetadata?> DecryptToStreamAsync(Stream input, Stream output, byte[] mlKemPrivateKey);

    /// <summary>
    /// Reads only the encrypted metadata from a file stream without decrypting any data chunks.
    /// Reads the minimum bytes needed from the stream (~2KB header + metadata).
    /// </summary>
    Task<FileMetadata?> ReadMetadataAsync(Stream fileStream, byte[] mlKemPrivateKey);

    /// <summary>
    /// Rewrites only the metadata section of an encrypted file, copying data chunks byte-for-byte.
    /// Used by MOVE/rename — a 1GB file rename takes ~2ms instead of seconds.
    /// </summary>
    Task RewriteMetadataAsync(Stream source, Stream destination, byte[] mlKemPrivateKey, FileMetadata newMetadata);
}
