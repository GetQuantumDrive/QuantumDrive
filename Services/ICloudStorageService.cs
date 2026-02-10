using quantum_drive.Models;
using Windows.Storage;

namespace quantum_drive.Services;

public interface ICloudStorageService
{
    Task UploadFileAsync(StorageFile file);
    Task<List<FileMetadata>> ListFilesAsync();
    Task<byte[]> DownloadFileAsync(string fileName);
    Task DeleteFileAsync(string fileName);
}
