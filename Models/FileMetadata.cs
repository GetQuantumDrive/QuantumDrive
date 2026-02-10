namespace quantum_drive.Models;

public class FileMetadata
{
    public string OriginalName { get; set; } = string.Empty;
    public long OriginalSize { get; set; }
    public long EncryptedSize { get; set; }
    public DateTime UploadedAt { get; set; }
    public string SHA256Hash { get; set; } = string.Empty;
}
