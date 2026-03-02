namespace quantum_drive.Models;

public class FileMetadata
{
    public string OriginalName { get; set; } = string.Empty;
    public long OriginalSize { get; set; }
    public DateTime UploadedAt { get; set; }
}
