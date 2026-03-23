using quantum_drive.Models;

namespace quantum_drive.Services;

/// <summary>
/// Per-vault storage abstraction. Local and cloud backends implement this interface.
/// All path parameters are backend-native paths returned by <see cref="GetQdPath"/>.
/// </summary>
public interface IStorageBackend : IDisposable
{
    /// <summary>Lists all files with the given extension (e.g. ".qd") in the vault storage location.</summary>
    Task<IEnumerable<string>> ListFilesAsync(string extension, CancellationToken ct = default);

    /// <summary>Opens a readable stream for the given path. Caller is responsible for disposing.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);

    /// <summary>Writes the full content of <paramref name="content"/> to <paramref name="path"/>, replacing any existing file.</summary>
    Task WriteAsync(string path, Stream content, CancellationToken ct = default);

    /// <summary>Deletes the file at <paramref name="path"/>. No-op if not found.</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>Returns true if a file exists at <paramref name="path"/>.</summary>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>Returns the storage path for a new .qd file named after <paramref name="virtualName"/>.</summary>
    string GetQdPath(string virtualName);

    /// <summary>Returns a collision-resolved storage path (e.g. "file.txt (2).qd").</summary>
    string GetQdPath(string virtualName, int counter);

    /// <summary>
    /// Subscribes to external changes in the vault storage (files added/changed/deleted/renamed
    /// by an outside process, e.g. a cloud sync client). Returns a disposable subscription.
    /// </summary>
    IDisposable Watch(Action<StorageBackendChangeEvent> onChange, Action? onError = null);
}
