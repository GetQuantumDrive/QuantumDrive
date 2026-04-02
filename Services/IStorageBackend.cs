using quantum_drive.Models;

namespace quantum_drive.Services;

/// <summary>
/// Per-vault storage abstraction. Implement this interface to add a new storage backend
/// (local disk, Google Drive, Dropbox, S3, etc.).
///
/// <para>
/// <b>Path semantics:</b> all <c>path</c> parameters accepted by the read/write/delete/exists
/// methods are <em>backend-native paths</em> produced by <see cref="GetQdPath"/>. Never
/// construct paths manually — always call <see cref="GetQdPath"/> first and pass its return
/// value to subsequent operations. For cloud backends the "path" is often just a file name;
/// for local backends it is a fully-qualified file system path.
/// </para>
///
/// <para>
/// <b>Threading:</b> all methods may be called from any thread. Implementations must be
/// thread-safe. Use locks or <see cref="System.Threading.SemaphoreSlim"/> where needed.
/// </para>
///
/// <para>
/// <b>Error handling:</b> throw <see cref="IOException"/> (or a subclass) for I/O failures.
/// Throw <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
/// Do <em>not</em> swallow exceptions silently — the CFAPI layer needs to propagate errors
/// back to the user.
/// </para>
///
/// <para>
/// <b>Implementing a new backend:</b> see <c>docs/contributing-providers.md</c> for a
/// step-by-step guide, the test checklist, and the PR checklist.
/// </para>
/// </summary>
public interface IStorageBackend : IDisposable
{
    /// <summary>
    /// Returns the backend-native paths of all files whose name ends with
    /// <paramref name="extension"/> (e.g. <c>".qd"</c>) in the vault's storage location.
    ///
    /// <para>
    /// The returned paths are passed back verbatim to <see cref="OpenReadAsync"/>,
    /// <see cref="WriteAsync"/>, <see cref="DeleteAsync"/>, and <see cref="ExistsAsync"/>.
    /// Returning an empty enumerable is valid when the vault is empty; never throw when
    /// there are simply no matching files.
    /// </para>
    /// </summary>
    /// <param name="extension">File extension filter, including the leading dot (e.g. <c>".qd"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<string>> ListFilesAsync(string extension, CancellationToken ct = default);

    /// <summary>
    /// Opens a readable, forward-only stream for the file at <paramref name="path"/>.
    /// The caller is responsible for disposing the returned stream.
    ///
    /// <para>
    /// Throw <see cref="FileNotFoundException"/> (or equivalent) when the file does not
    /// exist. Do not return an empty stream.
    /// </para>
    /// </summary>
    /// <param name="path">A backend-native path returned by <see cref="GetQdPath"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Writes the entire content of <paramref name="content"/> to <paramref name="path"/>,
    /// creating the file if it does not exist or replacing it if it does.
    ///
    /// <para>
    /// The stream is read exactly once, from its current position to the end. The
    /// implementation must not seek the stream. The caller does not dispose the stream
    /// before this method returns.
    /// </para>
    ///
    /// <para>
    /// For cloud backends: prefer an atomic upload (upload to a temp name, then rename)
    /// to avoid leaving partially-written files visible.
    /// </para>
    /// </summary>
    /// <param name="path">A backend-native path returned by <see cref="GetQdPath"/>.</param>
    /// <param name="content">The full content to write. Read sequentially; do not seek.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteAsync(string path, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Deletes the file at <paramref name="path"/>. Must be a no-op (not throw) if the
    /// file does not exist.
    /// </summary>
    /// <param name="path">A backend-native path returned by <see cref="GetQdPath"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Returns <see langword="true"/> if a file exists at <paramref name="path"/>,
    /// <see langword="false"/> otherwise. Never throws for a missing file.
    /// </summary>
    /// <param name="path">A backend-native path returned by <see cref="GetQdPath"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Returns the backend-native path for a new encrypted file named after
    /// <paramref name="virtualName"/> (the decrypted display name of the file, without
    /// extension). The returned value is passed back to the write/read/delete/exists methods.
    ///
    /// <para>
    /// For local backends: <c>Path.Combine(vaultDir, $"{virtualName}.qd")</c>.
    /// For cloud backends: typically just <c>$"{virtualName}.qd"</c> — the remote folder is
    /// implicit in the backend instance.
    /// </para>
    /// </summary>
    /// <param name="virtualName">The decrypted display name of the file (no extension).</param>
    string GetQdPath(string virtualName);

    /// <summary>
    /// Returns a collision-resolved backend-native path when a file named
    /// <c>{virtualName}.qd</c> already exists. Implementations must produce a unique
    /// name; the conventional format is <c>{virtualName} ({counter}).qd</c>.
    /// </summary>
    /// <param name="virtualName">The decrypted display name of the file (no extension).</param>
    /// <param name="counter">Collision counter, starting at 2.</param>
    string GetQdPath(string virtualName, int counter);

    /// <summary>
    /// Subscribes to external changes in the vault's storage location — files added,
    /// changed, or deleted by a process other than QuantumDrive (e.g. the cloud sync client
    /// on another machine, or the OS after a background sync).
    ///
    /// <para>
    /// <b>Local backends:</b> use <see cref="System.IO.FileSystemWatcher"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Cloud backends:</b> poll every 30 seconds and diff the file list. Return a
    /// <see cref="IDisposable"/> that cancels the polling loop when disposed.
    /// Push-based notifications (Google Drive change tokens, Dropbox longpoll, Graph delta)
    /// are welcome as improvements but not required for a first implementation.
    /// </para>
    ///
    /// <para>
    /// <b>Threading:</b> <paramref name="onChange"/> is called on a background thread.
    /// Implementations are responsible for any required thread marshalling.
    /// </para>
    /// </summary>
    /// <param name="onChange">
    /// Callback invoked for each detected change. May be called multiple times in quick
    /// succession during large sync operations.
    /// </param>
    /// <param name="onError">
    /// Optional callback invoked when the watcher encounters a fatal error (e.g. network
    /// loss) and can no longer deliver change events. The caller may attempt to re-subscribe.
    /// </param>
    /// <returns>
    /// A disposable subscription. Disposing it stops all change notifications. The caller
    /// always disposes this before the backend itself is disposed.
    /// </returns>
    IDisposable Watch(Action<StorageBackendChangeEvent> onChange, Action? onError = null);
}
