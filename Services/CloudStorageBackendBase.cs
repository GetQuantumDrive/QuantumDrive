using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using quantum_drive.Models;

namespace quantum_drive.Services;

/// <summary>
/// Shared base class for cloud storage backends (Google Drive, Dropbox, OneDrive).
/// Handles:
/// <list type="bullet">
/// <item>Token refresh with double-checked locking.</item>
/// <item>30-second polling for external changes (<see cref="Watch"/>).</item>
/// <item>Standard <see cref="GetQdPath"/> implementations.</item>
/// </list>
/// </summary>
internal abstract class CloudStorageBackendBase : IStorageBackend
{
    /// <summary>
    /// Live reference to <see cref="VaultDescriptor.BackendConfig"/>. In-memory updates
    /// (e.g. refreshed tokens) are visible to the rest of the app immediately and may be
    /// persisted if the registry saves the vault list for another reason during the session.
    /// </summary>
    protected readonly Dictionary<string, string> Config;

    /// <summary>8-character vault ID used as the remote subfolder name.</summary>
    protected readonly string VaultId;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <summary>
    /// In-memory cache: filename (e.g. <c>doc.pdf.qd</c>) → provider file ID.
    /// Populated by <see cref="ListFilesAsync"/>; updated on write and delete.
    /// Rebuilt from scratch by the next <see cref="ListFilesAsync"/> call after a
    /// restart.
    /// </summary>
    protected readonly ConcurrentDictionary<string, string> NameToId = new(StringComparer.Ordinal);

    protected CloudStorageBackendBase(string vaultId, Dictionary<string, string> config)
    {
        VaultId = vaultId;
        Config = config;
    }

    // ── Token management ──────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether the stored access token is within 5 minutes of expiry and, if so,
    /// calls <see cref="RefreshTokenAsync"/> under a lock to obtain a fresh one.
    /// </summary>
    protected async Task EnsureFreshTokenAsync(CancellationToken ct)
    {
        if (IsTokenFresh()) return;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (IsTokenFresh()) return; // double-check after acquiring lock
            await RefreshTokenAsync(ct);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private bool IsTokenFresh()
    {
        if (!Config.TryGetValue("token_expiry", out var s)) return false;
        if (!DateTime.TryParse(s, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var expiry)) return false;
        return DateTime.UtcNow < expiry.AddMinutes(-5);
    }

    /// <summary>
    /// Called by <see cref="EnsureFreshTokenAsync"/> when a token refresh is needed.
    /// Must update <c>Config["access_token"]</c> and <c>Config["token_expiry"]</c>
    /// in-memory before returning.
    /// </summary>
    protected abstract Task RefreshTokenAsync(CancellationToken ct);

    /// <summary>Current access token from <see cref="Config"/>.</summary>
    protected string AccessToken =>
        Config.TryGetValue("access_token", out var t) ? t : string.Empty;

    // ── IStorageBackend abstract operations ───────────────────────────────────

    public abstract Task<IEnumerable<string>> ListFilesAsync(string extension, CancellationToken ct = default);
    public abstract Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);
    public abstract Task WriteAsync(string path, Stream content, CancellationToken ct = default);
    public abstract Task DeleteAsync(string path, CancellationToken ct = default);
    public abstract Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    // ── Path helpers ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string GetQdPath(string virtualName) => $"{virtualName}.qd";

    /// <inheritdoc/>
    public string GetQdPath(string virtualName, int counter) => $"{virtualName} ({counter}).qd";

    // ── Watch (30-second polling) ─────────────────────────────────────────────

    /// <inheritdoc/>
    public IDisposable Watch(Action<StorageBackendChangeEvent> onChange, Action? onError = null)
    {
        var cts = new CancellationTokenSource();
        _ = PollAsync(onChange, onError, cts.Token);
        return new CtsDisposable(cts);
    }

    private async Task PollAsync(
        Action<StorageBackendChangeEvent> onChange,
        Action? onError,
        CancellationToken ct)
    {
        try
        {
            var known = (await ListFilesAsync(".qd", ct)).ToHashSet(StringComparer.Ordinal);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                var current = (await ListFilesAsync(".qd", ct)).ToHashSet(StringComparer.Ordinal);

                foreach (var added in current.Except(known))
                    onChange(new StorageBackendChangeEvent(StorageChangeType.Created, added));
                foreach (var removed in known.Except(current))
                    onChange(new StorageBackendChangeEvent(StorageChangeType.Deleted, removed));

                known = current;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{GetType().Name}] Watch error: {ex.Message}");
            onError?.Invoke();
        }
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    protected static string? ReadString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(property, out var el))
                return el.GetString();
        }
        catch { }
        return null;
    }

    protected static JsonElement ParseJson(string json)
    {
        // Returns a cloned element so the underlying document can be disposed.
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public virtual void Dispose() => _tokenLock.Dispose();

    // ── Private types ─────────────────────────────────────────────────────────

    private sealed class CtsDisposable(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose() => cts.Cancel();
    }
}
