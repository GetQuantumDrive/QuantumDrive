using System.Diagnostics;
using quantum_drive.Models;

namespace quantum_drive.Services;

/// <summary>
/// IStorageBackend implementation backed by a local directory on disk.
/// </summary>
public sealed class LocalStorageBackend : IStorageBackend
{
    private readonly string _vaultPath;

    public LocalStorageBackend(string vaultPath)
    {
        _vaultPath = vaultPath;
    }

    public Task<IEnumerable<string>> ListFilesAsync(string extension, CancellationToken ct = default)
    {
        if (!Directory.Exists(_vaultPath))
            return Task.FromResult(Enumerable.Empty<string>());

        return Task.FromResult<IEnumerable<string>>(
            Directory.EnumerateFiles(_vaultPath, $"*{extension}"));
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        => Task.FromResult<Stream>(File.OpenRead(path));

    public async Task WriteAsync(string path, Stream content, CancellationToken ct = default)
    {
        using var dest = File.Create(path);
        await content.CopyToAsync(dest, ct);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(File.Exists(path));

    public string GetQdPath(string virtualName)
        => Path.Combine(_vaultPath, $"{virtualName}.qd");

    public string GetQdPath(string virtualName, int counter)
        => Path.Combine(_vaultPath, $"{virtualName} ({counter}).qd");

    public IDisposable Watch(Action<StorageBackendChangeEvent> onChange, Action? onError = null)
    {
        if (!Directory.Exists(_vaultPath))
            return new NullDisposable();

        var watcher = new FileSystemWatcher(_vaultPath, "*.qd")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false,
        };

        watcher.Created += (_, e) =>
            onChange(new StorageBackendChangeEvent(StorageChangeType.Created, e.FullPath));
        watcher.Changed += (_, e) =>
            onChange(new StorageBackendChangeEvent(StorageChangeType.Changed, e.FullPath));
        watcher.Deleted += (_, e) =>
            onChange(new StorageBackendChangeEvent(StorageChangeType.Deleted, e.FullPath));
        watcher.Renamed += (_, e) =>
            onChange(new StorageBackendChangeEvent(StorageChangeType.Renamed, e.FullPath, e.OldFullPath));
        watcher.Error += (_, e) =>
        {
            Debug.WriteLine($"LocalStorageBackend watcher error: {e.GetException()?.Message}");
            onError?.Invoke();
        };

        watcher.EnableRaisingEvents = true;
        return new WatcherDisposable(watcher);
    }

    public void Dispose() { /* no persistent resources */ }

    private sealed class WatcherDisposable(FileSystemWatcher watcher) : IDisposable
    {
        public void Dispose()
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
