using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;
using static Vanara.PInvoke.Kernel32;
using quantum_drive.Models;

namespace quantum_drive.Services;

/// <summary>
/// Per-vault Cloud Files API sync provider. Replaces WebDavHandler.
/// Creates NTFS placeholder files in a sync root folder and hydrates them
/// on demand by decrypting the corresponding .qd files from the vault.
/// </summary>
public sealed class CloudSyncProvider : IDisposable
{
    private const int TransferChunkSize = 64 * 1024; // 64KB — matches crypto chunk size
    private const int DebounceDelayMs = 500;

    private readonly string _syncRootPath;
    private readonly IStorageBackend _storageBackend;
    private readonly ICryptoService _cryptoService;
    private readonly IIdentityService _identityService;
    private readonly ConcurrentDictionary<string, QdFileEntry> _fileIndex = new(StringComparer.OrdinalIgnoreCase);

    // Prevent GC of callback delegates — must live as long as the connection
    private CF_CALLBACK? _fetchDataCallback;
    private CF_CALLBACK? _cancelFetchDataCallback;
    private CF_CALLBACK_REGISTRATION[]? _callbackTable;
    private CF_CONNECTION_KEY _connectionKey;
    private bool _connected;

    private FileSystemWatcher? _syncRootWatcher;
    private IDisposable? _backendWatchSubscription;

    // Suppress vault watcher events for files we just wrote
    private readonly ConcurrentDictionary<string, DateTime> _recentVaultWrites = new(StringComparer.OrdinalIgnoreCase);

    // Debounce timers for sync root changes
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceCts = new(StringComparer.OrdinalIgnoreCase);

    // Track files currently being hydrated to suppress sync root watcher
    private readonly ConcurrentDictionary<string, byte> _hydratingFiles = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public Action? OnFilesChanged { get; set; }

    public CloudSyncProvider(string syncRootPath, IStorageBackend storageBackend,
        ICryptoService cryptoService, IIdentityService identityService)
    {
        _syncRootPath = syncRootPath;
        _storageBackend = storageBackend;
        _cryptoService = cryptoService;
        _identityService = identityService;
    }

    /// <summary>Builds index, connects to sync root, starts watchers.</summary>
    public void Connect()
    {
        Directory.CreateDirectory(_syncRootPath);
        RebuildIndex();

        // Store delegates as fields to prevent GC
        _fetchDataCallback = new CF_CALLBACK(OnFetchData);
        _cancelFetchDataCallback = new CF_CALLBACK(OnCancelFetchData);

        // AlwaysFull policy: placeholders are created proactively via CfCreatePlaceholders,
        // so we don't need a FETCH_PLACEHOLDERS callback. Registering one that returns
        // PlaceholderCount=0 would cause Explorer to show an empty folder.
        _callbackTable =
        [
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = _fetchDataCallback },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA, Callback = _cancelFetchDataCallback },
            CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
        ];

        CfConnectSyncRoot(_syncRootPath, _callbackTable, IntPtr.Zero,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out _connectionKey)
            .ThrowIfFailed();

        _connected = true;
        Debug.WriteLine($"CFAPI connected: {_syncRootPath}");

        // Proactively create placeholders for all indexed files (AlwaysFull policy)
        CreateAllPlaceholders();

        // Notify Explorer to refresh the sync root folder so newly created
        // placeholders appear immediately without requiring a manual F5.
        NativeMethods.SHChangeNotify(0x00000008 /* SHCNE_UPDATEDIR */,
            0x0005 /* SHCNF_PATHW */, _syncRootPath, null);

        StartWatchers();
    }

    /// <summary>Stops watchers, disconnects from sync root, and cleans up plaintext files.</summary>
    public void Disconnect()
    {
        StopWatchers();

        if (_connected)
        {
            try
            {
                CfDisconnectSyncRoot(_connectionKey);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CfDisconnectSyncRoot failed: {ex.Message}");
            }
            _connected = false;
            Debug.WriteLine($"CFAPI disconnected: {_syncRootPath}");
        }

        // SECURITY: Remove all files from the sync root folder.
        // Hydrated placeholders contain decrypted plaintext on disk — leaving them
        // would allow access to vault files without entering the password.
        CleanupSyncRootFiles();
    }

    /// <summary>
    /// Deletes all files from the sync root folder so no plaintext data
    /// persists on disk after the vault is locked.
    /// </summary>
    private void CleanupSyncRootFiles()
    {
        try
        {
            if (!Directory.Exists(_syncRootPath)) return;

            foreach (var file in Directory.EnumerateFiles(_syncRootPath))
            {
                try
                {
                    // Clear read-only/system attributes that CFAPI may have set
                    var attrs = File.GetAttributes(file);
                    if ((attrs & (FileAttributes.ReadOnly | FileAttributes.System)) != 0)
                        File.SetAttributes(file, attrs & ~(FileAttributes.ReadOnly | FileAttributes.System));

                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to clean up sync root file '{Path.GetFileName(file)}': {ex.Message}");
                }
            }

            Debug.WriteLine($"Sync root cleaned: {_syncRootPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Sync root cleanup failed: {ex.Message}");
        }
    }

    #region Index Management

    public void RebuildIndex()
    {
        _fileIndex.Clear();

        var privateKey = _identityService.MlKemPrivateKey;
        if (privateKey is null) return;

        var qdPaths = _storageBackend.ListFilesAsync(".qd").GetAwaiter().GetResult();

        foreach (var qdPath in qdPaths)
        {
            try
            {
                using var stream = _storageBackend.OpenReadAsync(qdPath).GetAwaiter().GetResult();
                var metadata = _cryptoService.ReadMetadataAsync(stream, privateKey)
                    .GetAwaiter().GetResult();

                if (metadata is null || string.IsNullOrEmpty(metadata.OriginalName))
                    continue;

                _fileIndex[metadata.OriginalName] = new QdFileEntry
                {
                    QdFilePath = qdPath,
                    Metadata = metadata,
                    EncryptedSize = stream.Length
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Skipping {qdPath}: {ex.Message}");
            }
        }
    }

    #endregion

    #region Placeholder Management

    private void CreateAllPlaceholders()
    {
        foreach (var (virtualName, entry) in _fileIndex)
        {
            CreateSinglePlaceholder(virtualName, entry.Metadata);
        }
    }

    private void CreateSinglePlaceholder(string virtualName, FileMetadata metadata)
    {
        var nameBytes = Encoding.Unicode.GetBytes(virtualName);
        var handle = GCHandle.Alloc(nameBytes, GCHandleType.Pinned);
        try
        {
            var uploadedAtUtc = DateTime.SpecifyKind(metadata.UploadedAt, DateTimeKind.Utc);
            var ft = ToFileTime(uploadedAtUtc);

            var placeholders = new CF_PLACEHOLDER_CREATE_INFO[]
            {
                new()
                {
                    RelativeFileName = virtualName,
                    FileIdentity = handle.AddrOfPinnedObject(),
                    FileIdentityLength = (uint)nameBytes.Length,
                    Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
                          | CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_SUPERSEDE,
                    FsMetadata = new CF_FS_METADATA
                    {
                        FileSize = metadata.OriginalSize,
                        BasicInfo = new FILE_BASIC_INFO
                        {
                            FileAttributes = FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                            CreationTime = ft,
                            LastWriteTime = ft,
                            LastAccessTime = ft,
                            ChangeTime = ft,
                        }
                    }
                }
            };

            var hr = CfCreatePlaceholders(_syncRootPath, placeholders, 1,
                CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out _);

            if (hr.Failed)
                Debug.WriteLine($"Placeholder FAILED for '{virtualName}': HRESULT=0x{(uint)hr:X8}");
            else
                Debug.WriteLine($"Placeholder OK: {virtualName} ({metadata.OriginalSize} bytes, result=0x{(uint)placeholders[0].Result:X8})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Placeholder exception for '{virtualName}': {ex}");
        }
        finally
        {
            handle.Free();
        }
    }

    private void RemovePlaceholder(string virtualName)
    {
        var path = Path.Combine(_syncRootPath, virtualName);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to remove placeholder '{virtualName}': {ex.Message}");
        }
    }

    #endregion

    #region CFAPI Callbacks

    private void OnFetchData(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        // Extract the virtual file name from the identity blob we stored
        string? virtualName = null;
        try
        {
            if (info.FileIdentity != IntPtr.Zero && info.FileIdentityLength > 0)
                virtualName = Marshal.PtrToStringUni(info.FileIdentity, (int)(info.FileIdentityLength / sizeof(char)));
        }
        catch
        {
            // Fall back to normalized path
        }

        if (string.IsNullOrEmpty(virtualName))
        {
            // Try extracting from NormalizedPath (e.g. "\SyncRoot\filename.docx")
            try
            {
                var normalized = info.NormalizedPath?.ToString();
                if (!string.IsNullOrEmpty(normalized))
                    virtualName = Path.GetFileName(normalized);
            }
            catch { /* best effort */ }
        }

        Debug.WriteLine($"FETCH_DATA: {virtualName ?? "(unknown)"}");

        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = info.ConnectionKey,
            TransferKey = info.TransferKey,
            RequestKey = info.RequestKey,
        };

        if (string.IsNullOrEmpty(virtualName) || !_fileIndex.TryGetValue(virtualName, out var entry))
        {
            Debug.WriteLine($"FETCH_DATA: file not in index: {virtualName}");
            TransferError(ref opInfo, parameters.FetchData.RequiredFileOffset, parameters.FetchData.RequiredLength);
            return;
        }

        // Track that we're hydrating this file (suppress sync root watcher)
        _hydratingFiles[virtualName] = 0;

        try
        {
            var privateKey = _identityService.MlKemPrivateKey;
            if (privateKey is null)
            {
                TransferError(ref opInfo, parameters.FetchData.RequiredFileOffset, parameters.FetchData.RequiredLength);
                return;
            }

            // Decrypt entire file to memory (Full hydration policy = whole file)
            using var decrypted = new MemoryStream();
            using (var qdStream = _storageBackend.OpenReadAsync(entry.QdFilePath).GetAwaiter().GetResult())
            {
                _cryptoService.DecryptToStreamAsync(qdStream, decrypted, privateKey)
                    .GetAwaiter().GetResult();
            }

            var data = decrypted.GetBuffer();
            var dataLength = (int)decrypted.Length;

            // Transfer in 64KB chunks, 4KB-aligned
            long offset = parameters.FetchData.RequiredFileOffset;
            long end = offset + parameters.FetchData.RequiredLength;
            if (end > dataLength) end = dataLength;

            while (offset < end)
            {
                var chunkLen = (int)Math.Min(TransferChunkSize, end - offset);

                // Pin the relevant portion of the buffer
                var chunk = new byte[chunkLen];
                Buffer.BlockCopy(data, (int)offset, chunk, 0, chunkLen);

                var gcHandle = GCHandle.Alloc(chunk, GCHandleType.Pinned);
                try
                {
                    var opParams = CF_OPERATION_PARAMETERS.Create(
                        new CF_OPERATION_PARAMETERS.TRANSFERDATA
                        {
                            CompletionStatus = NTStatus.STATUS_SUCCESS,
                            Buffer = gcHandle.AddrOfPinnedObject(),
                            Offset = offset,
                            Length = chunkLen,
                        });

                    CfExecute(opInfo, ref opParams).ThrowIfFailed();
                }
                finally
                {
                    gcHandle.Free();
                }

                offset += chunkLen;
            }

            Debug.WriteLine($"FETCH_DATA complete: {virtualName} ({dataLength} bytes)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FETCH_DATA failed for {virtualName}: {ex.Message}");
            TransferError(ref opInfo, parameters.FetchData.RequiredFileOffset, parameters.FetchData.RequiredLength);
        }
        finally
        {
            _hydratingFiles.TryRemove(virtualName, out _);
        }
    }

    private static void TransferError(ref CF_OPERATION_INFO opInfo, long offset, long length)
    {
        try
        {
            var opParams = CF_OPERATION_PARAMETERS.Create(
                new CF_OPERATION_PARAMETERS.TRANSFERDATA
                {
                    CompletionStatus = NTStatus.STATUS_UNSUCCESSFUL,
                    Buffer = IntPtr.Zero,
                    Offset = offset,
                    Length = length,
                });
            CfExecute(opInfo, ref opParams);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TransferError CfExecute failed: {ex.Message}");
        }
    }

    private void OnCancelFetchData(in CF_CALLBACK_INFO info, in CF_CALLBACK_PARAMETERS parameters)
    {
        Debug.WriteLine("CANCEL_FETCH_DATA received");
        // Informational — we don't support cancellation mid-decrypt
    }

    #endregion

    #region File System Watchers

    private void StartWatchers()
    {
        // Watch sync root for user edits (saves, new files, deletes, renames)
        if (Directory.Exists(_syncRootPath))
        {
            _syncRootWatcher = new FileSystemWatcher(_syncRootPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = false,
            };

            _syncRootWatcher.Created += OnSyncRootCreatedOrChanged;
            _syncRootWatcher.Changed += OnSyncRootCreatedOrChanged;
            _syncRootWatcher.Deleted += OnSyncRootDeleted;
            _syncRootWatcher.Renamed += OnSyncRootRenamed;
            _syncRootWatcher.Error += (_, e) => Debug.WriteLine($"SyncRoot watcher error: {e.GetException()?.Message}");
            _syncRootWatcher.EnableRaisingEvents = true;
        }

        // Watch storage backend for external .qd changes (e.g. cloud sync from another device)
        _backendWatchSubscription = _storageBackend.Watch(OnBackendChanged, RebuildIndex);
    }

    private void StopWatchers()
    {
        if (_syncRootWatcher is not null)
        {
            _syncRootWatcher.EnableRaisingEvents = false;
            _syncRootWatcher.Dispose();
            _syncRootWatcher = null;
        }

        _backendWatchSubscription?.Dispose();
        _backendWatchSubscription = null;

        // Cancel all pending debounce timers
        foreach (var cts in _debounceCts.Values)
            cts.Cancel();
        _debounceCts.Clear();
    }

    #endregion

    #region Sync Root Watcher Handlers

    private void OnSyncRootCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        var fileName = e.Name;
        if (string.IsNullOrEmpty(fileName)) return;

        // Skip hidden/system files and temp files
        if (fileName.StartsWith('.') || fileName.StartsWith('~') || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            return;

        // Skip if we're currently hydrating this file
        if (_hydratingFiles.ContainsKey(fileName))
        {
            Debug.WriteLine($"SyncRoot watcher: skipping hydrating file {fileName}");
            return;
        }

        // Debounce: cancel previous timer, start new one
        DebouncedAction(fileName, () => EncryptSyncRootFileAsync(e.FullPath, fileName));
    }

    private void OnSyncRootDeleted(object sender, FileSystemEventArgs e)
    {
        var fileName = e.Name;
        if (string.IsNullOrEmpty(fileName)) return;

        Debug.WriteLine($"SyncRoot watcher: deleted {fileName}");

        if (_fileIndex.TryRemove(fileName, out var entry))
        {
            // Delete the corresponding .qd file via the backend (fire-and-forget)
            _recentVaultWrites[entry.QdFilePath] = DateTime.UtcNow;
            _ = _storageBackend.DeleteAsync(entry.QdFilePath);
            Debug.WriteLine($"Deleting .qd file: {Path.GetFileName(entry.QdFilePath)}");

            OnFilesChanged?.Invoke();
        }
    }

    private void OnSyncRootRenamed(object sender, RenamedEventArgs e)
    {
        var oldName = e.OldName;
        var newName = e.Name;
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return;

        Debug.WriteLine($"SyncRoot watcher: renamed {oldName} → {newName}");

        // Use debounce for the rename operation
        DebouncedAction(newName, () => RenameSyncRootFileAsync(oldName, newName));
    }

    private async Task EncryptSyncRootFileAsync(string localPath, string virtualName, int retryCount = 0)
    {
        try
        {
            if (!File.Exists(localPath)) return;

            var fileInfo = new FileInfo(localPath);

            // Skip if file hasn't actually changed (hydration writes don't update LastWriteTime)
            if (_fileIndex.TryGetValue(virtualName, out var existing))
            {
                var existingTime = DateTime.SpecifyKind(existing.Metadata.UploadedAt, DateTimeKind.Utc);
                if (fileInfo.LastWriteTimeUtc <= existingTime)
                {
                    Debug.WriteLine($"SyncRoot: skipping unchanged file {virtualName}");
                    return;
                }
            }

            var publicKey = _identityService.MlKemPublicKey;
            if (publicKey is null)
            {
                Debug.WriteLine("Cannot encrypt: no public key available");
                return;
            }

            var metadata = new FileMetadata
            {
                OriginalName = virtualName,
                OriginalSize = fileInfo.Length,
                UploadedAt = DateTime.UtcNow
            };

            // Determine .qd path
            string qdPath;
            if (existing is not null)
            {
                qdPath = existing.QdFilePath;
            }
            else
            {
                qdPath = _storageBackend.GetQdPath(virtualName);
                var counter = 1;
                while (await _storageBackend.ExistsAsync(qdPath) &&
                       !_fileIndex.Values.Any(e => e.QdFilePath.Equals(qdPath, StringComparison.OrdinalIgnoreCase)))
                {
                    qdPath = _storageBackend.GetQdPath(virtualName, counter);
                    counter++;
                }
            }

            _recentVaultWrites[qdPath] = DateTime.UtcNow;

            // Encrypt to a temp file, then push to the backend
            var tempPath = Path.GetTempFileName();
            long encryptedSize;
            try
            {
                using (var inputFs = File.OpenRead(localPath))
                using (var tempOut = File.Create(tempPath))
                {
                    await _cryptoService.EncryptToStreamAsync(inputFs, tempOut, publicKey, metadata);
                }
                encryptedSize = new FileInfo(tempPath).Length;
                using var tempFs = File.OpenRead(tempPath);
                await _storageBackend.WriteAsync(qdPath, tempFs);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }

            _fileIndex[virtualName] = new QdFileEntry
            {
                QdFilePath = qdPath,
                Metadata = metadata,
                EncryptedSize = encryptedSize
            };

            Debug.WriteLine($"Encrypted: {virtualName} → {Path.GetFileName(qdPath)}");
            OnFilesChanged?.Invoke();
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020) && retryCount < 3) // ERROR_SHARING_VIOLATION
        {
            Debug.WriteLine($"File in use, will retry ({retryCount + 1}/3): {virtualName}");
            await Task.Delay(1000);
            await EncryptSyncRootFileAsync(localPath, virtualName, retryCount + 1);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Encrypt failed for {virtualName}: {ex.Message}");
        }
    }

    private async Task RenameSyncRootFileAsync(string oldName, string newName)
    {
        try
        {
            if (!_fileIndex.TryGetValue(oldName, out var entry))
            {
                Debug.WriteLine($"Rename: old name '{oldName}' not in index, treating as new file");
                var newPath = Path.Combine(_syncRootPath, newName);
                await EncryptSyncRootFileAsync(newPath, newName);
                return;
            }

            var privateKey = _identityService.MlKemPrivateKey;
            if (privateKey is null) return;

            var newMetadata = new FileMetadata
            {
                OriginalName = newName,
                OriginalSize = entry.Metadata.OriginalSize,
                UploadedAt = entry.Metadata.UploadedAt
            };

            var newQdPath = _storageBackend.GetQdPath(newName);
            _recentVaultWrites[newQdPath] = DateTime.UtcNow;
            _recentVaultWrites[entry.QdFilePath] = DateTime.UtcNow;

            var tempPath = Path.GetTempFileName();
            long encryptedSize;
            try
            {
                using (var source = await _storageBackend.OpenReadAsync(entry.QdFilePath))
                using (var tempOut = File.Create(tempPath))
                {
                    await _cryptoService.RewriteMetadataAsync(source, tempOut, privateKey, newMetadata);
                }
                encryptedSize = new FileInfo(tempPath).Length;
                using var tempFs = File.OpenRead(tempPath);
                await _storageBackend.WriteAsync(newQdPath, tempFs);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }

            // Clean up old entry
            _fileIndex.TryRemove(oldName, out _);
            if (!entry.QdFilePath.Equals(newQdPath, StringComparison.OrdinalIgnoreCase))
                await _storageBackend.DeleteAsync(entry.QdFilePath);

            _fileIndex[newName] = new QdFileEntry
            {
                QdFilePath = newQdPath,
                Metadata = newMetadata,
                EncryptedSize = encryptedSize
            };

            Debug.WriteLine($"Renamed .qd: {oldName} → {newName}");
            OnFilesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Rename failed ({oldName} → {newName}): {ex.Message}");
        }
    }

    #endregion

    #region Backend Change Handler

    private void OnBackendChanged(StorageBackendChangeEvent evt)
    {
        // Suppress events triggered by our own writes
        if (_recentVaultWrites.TryGetValue(evt.Path, out var writeTime) &&
            (DateTime.UtcNow - writeTime).TotalSeconds < 5)
            return;

        Debug.WriteLine($"Backend: {evt.ChangeType} {Path.GetFileName(evt.Path)}");

        switch (evt.ChangeType)
        {
            case StorageChangeType.Created:
            case StorageChangeType.Changed:
                _ = UpdateIndexFromVaultAsync(evt.Path);
                break;

            case StorageChangeType.Deleted:
                var toRemove = _fileIndex
                    .Where(kvp => kvp.Value.QdFilePath.Equals(evt.Path, StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var key in toRemove)
                {
                    _fileIndex.TryRemove(key, out _);
                    RemovePlaceholder(key);
                }
                if (toRemove.Count > 0)
                    OnFilesChanged?.Invoke();
                break;

            case StorageChangeType.Renamed when evt.OldPath is not null:
                // Also suppress if we wrote the old path
                if (_recentVaultWrites.TryGetValue(evt.OldPath, out var oldWriteTime) &&
                    (DateTime.UtcNow - oldWriteTime).TotalSeconds < 5)
                    return;
                _ = UpdateIndexFromVaultAsync(evt.Path);
                break;
        }
    }

    private async Task UpdateIndexFromVaultAsync(string qdPath)
    {
        // Retry with delay — file may still be locked by the writing process
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await Task.Delay(attempt == 0 ? 200 : 500);
            try
            {
                var privateKey = _identityService.MlKemPrivateKey;
                if (privateKey is null) return;

                using var stream = await _storageBackend.OpenReadAsync(qdPath);
                var metadata = await _cryptoService.ReadMetadataAsync(stream, privateKey);

                if (metadata is null || string.IsNullOrEmpty(metadata.OriginalName))
                    return;

                _fileIndex[metadata.OriginalName] = new QdFileEntry
                {
                    QdFilePath = qdPath,
                    Metadata = metadata,
                    EncryptedSize = stream.Length
                };

                // Create placeholder if it doesn't exist yet
                CreateSinglePlaceholder(metadata.OriginalName, metadata);
                OnFilesChanged?.Invoke();
                return;
            }
            catch (IOException) when (attempt < 3) { /* retry */ }
            catch (Exception ex)
            {
                Debug.WriteLine($"Vault index update failed for {qdPath}: {ex.Message}");
                return;
            }
        }
    }

    #endregion

    #region Helpers

    private static FILETIME ToFileTime(DateTime utcDateTime)
    {
        long ticks = utcDateTime.ToFileTimeUtc();
        return new FILETIME
        {
            dwLowDateTime = (int)(ticks & 0xFFFFFFFF),
            dwHighDateTime = (int)(ticks >> 32),
        };
    }

    private void DebouncedAction(string key, Func<Task> action)
    {
        // Cancel any existing debounce for this key
        if (_debounceCts.TryRemove(key, out var oldCts))
            oldCts.Cancel();

        var cts = new CancellationTokenSource();
        _debounceCts[key] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelayMs, cts.Token);
                _debounceCts.TryRemove(key, out _);
                await action();
            }
            catch (OperationCanceledException) { /* debounce cancelled — newer event took over */ }
            catch (Exception ex)
            {
                Debug.WriteLine($"Debounced action failed for {key}: {ex.Message}");
            }
        });
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Disconnect();
        _storageBackend.Dispose();

        // Clean up suppression cache
        _recentVaultWrites.Clear();
        _hydratingFiles.Clear();
    }

    internal class QdFileEntry
    {
        public required string QdFilePath { get; set; }
        public required FileMetadata Metadata { get; set; }
        public long EncryptedSize { get; set; }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern void SHChangeNotify(int wEventId, int uFlags, string dwItem1, string? dwItem2);
    }
}
