using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;
using quantum_drive.Models;

namespace quantum_drive.Services;

public class WebDavHandler : IDisposable
{
    private readonly string _vaultPath;
    private readonly ICryptoService _cryptoService;
    private readonly IIdentityService _identityService;
    private readonly ConcurrentDictionary<string, QdFileEntry> _fileIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher? _fileWatcher;

    public int FileLimit { get; set; } = int.MaxValue;

    public WebDavHandler(string vaultPath, ICryptoService cryptoService, IIdentityService identityService)
    {
        _vaultPath = vaultPath;
        _cryptoService = cryptoService;
        _identityService = identityService;
        RebuildIndex();

        // Initialize FileSystemWatcher for real-time index updates
        if (Directory.Exists(_vaultPath))
        {
            _fileWatcher = new FileSystemWatcher(_vaultPath, "*.qd")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = false
            };

            _fileWatcher.Created += OnFileChanged;
            _fileWatcher.Changed += OnFileChanged;
            _fileWatcher.Deleted += OnFileDeleted;
            _fileWatcher.Renamed += OnFileRenamed;
            _fileWatcher.Error += (sender, e) =>
            {
                Debug.WriteLine($"FileSystemWatcher error: {e.GetException()?.Message}");
                RebuildIndex(); // Full rebuild on buffer overflow
            };

            _fileWatcher.EnableRaisingEvents = true;
        }
    }

    private void RebuildIndex()
    {
        _fileIndex.Clear();
        if (!Directory.Exists(_vaultPath))
            return;

        foreach (var qdPath in Directory.EnumerateFiles(_vaultPath, "*.qd"))
        {
            try
            {
                var bytes = File.ReadAllBytes(qdPath);
                var metadata = CryptoService.ReadMetadata(bytes);
                if (metadata is null || string.IsNullOrEmpty(metadata.OriginalName))
                    continue;

                _fileIndex[metadata.OriginalName] = new QdFileEntry
                {
                    QdFilePath = qdPath,
                    Metadata = metadata,
                    EncryptedSize = bytes.Length
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Skipping {qdPath}: {ex.Message}");
            }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        Debug.WriteLine($"FileWatcher: {e.ChangeType} detected for {e.Name}");

        // Debounce: delay to ensure file write is complete
        Task.Delay(100).ContinueWith(_ =>
        {
            try
            {
                UpdateIndexEntry(e.FullPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update index for {e.Name}: {ex.Message}");
            }
        });
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        Debug.WriteLine($"FileWatcher: Deleted {e.Name}");

        var toRemove = _fileIndex
            .Where(kvp => kvp.Value.QdFilePath.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _fileIndex.TryRemove(key, out _);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        Debug.WriteLine($"FileWatcher: Renamed {e.OldName} → {e.Name}");

        // Remove old entry
        var oldKeys = _fileIndex
            .Where(kvp => kvp.Value.QdFilePath.Equals(e.OldFullPath, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in oldKeys)
        {
            _fileIndex.TryRemove(key, out _);
        }

        // Add new entry
        UpdateIndexEntry(e.FullPath);
    }

    private void UpdateIndexEntry(string qdPath)
    {
        try
        {
            if (!File.Exists(qdPath) || !qdPath.EndsWith(".qd", StringComparison.OrdinalIgnoreCase))
                return;

            var bytes = File.ReadAllBytes(qdPath);
            var metadata = CryptoService.ReadMetadata(bytes);

            if (metadata is null || string.IsNullOrEmpty(metadata.OriginalName))
            {
                Debug.WriteLine($"Skipping {qdPath}: invalid metadata");
                return;
            }

            _fileIndex[metadata.OriginalName] = new QdFileEntry
            {
                QdFilePath = qdPath,
                Metadata = metadata,
                EncryptedSize = bytes.Length
            };

            Debug.WriteLine($"Index updated: {metadata.OriginalName} → {qdPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateIndexEntry failed for {qdPath}: {ex.Message}");
        }
    }

    public async Task HandleRequestAsync(HttpContext context)
    {
        var method = context.Request.Method.ToUpperInvariant();
        Debug.WriteLine($"WebDAV {method} {context.Request.Path}");

        try
        {
            switch (method)
            {
                case "OPTIONS":
                    HandleOptions(context);
                    break;
                case "PROPFIND":
                    await HandlePropfindAsync(context);
                    break;
                case "PROPPATCH":
                    await HandleProppatchAsync(context);
                    break;
                case "GET":
                    await HandleGetAsync(context, includeBody: true);
                    break;
                case "HEAD":
                    await HandleGetAsync(context, includeBody: false);
                    break;
                case "PUT":
                    await HandlePutAsync(context);
                    break;
                case "DELETE":
                    HandleDelete(context);
                    break;
                case "MOVE":
                    HandleMove(context);
                    break;
                case "LOCK":
                    await HandleLockAsync(context);
                    break;
                case "UNLOCK":
                    context.Response.StatusCode = 204;
                    break;
                case "MKCOL":
                    // We don't support subdirectories — root collection already exists
                    var mkcolPath = GetVirtualPath(context.Request.Path);
                    context.Response.StatusCode = string.IsNullOrEmpty(mkcolPath) ? 405 : 403;
                    break;
                default:
                    context.Response.StatusCode = 405;
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebDAV {method} error: {ex.Message}");
            context.Response.StatusCode = 500;
        }
    }

    private static string GetVirtualPath(PathString requestPath)
    {
        var path = Uri.UnescapeDataString(requestPath.Value ?? "/");
        path = path.TrimStart('/');

        // Strip the QuantumDrive share prefix added by the WebDAV mount path
        const string prefix = "QuantumDrive/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            path = path[prefix.Length..];
        else if (path.Equals("QuantumDrive", StringComparison.OrdinalIgnoreCase))
            path = "";

        return path;
    }

    private void HandleOptions(HttpContext context)
    {
        context.Response.StatusCode = 200;
        context.Response.Headers["DAV"] = "1";
        context.Response.Headers["Allow"] = "OPTIONS, PROPFIND, PROPPATCH, GET, HEAD, PUT, DELETE, MOVE, LOCK, UNLOCK, MKCOL";
        context.Response.Headers["MS-Author-Via"] = "DAV";
        context.Response.ContentLength = 0;
    }

    private async Task HandlePropfindAsync(HttpContext context)
    {
        var virtualPath = GetVirtualPath(context.Request.Path);
        var isRoot = string.IsNullOrEmpty(virtualPath);

        // Depth header: "0" = just the resource, "1" = resource + children
        var depth = context.Request.Headers["Depth"].FirstOrDefault() ?? "1";

        if (!isRoot && !_fileIndex.ContainsKey(virtualPath))
        {
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.StatusCode = 207;
        context.Response.ContentType = "application/xml; charset=utf-8";

        // Add cache control headers
        context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.Headers["Expires"] = "0";

        if (isRoot && depth != "0")
        {
            context.Response.Headers["ETag"] = GenerateCollectionETag(_fileIndex.Values);
        }

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false
        }))
        {
            writer.WriteStartElement("D", "multistatus", "DAV:");

            if (isRoot)
            {
                // Root collection
                WriteCollectionResponse(writer, "/");

                if (depth != "0")
                {
                    foreach (var kvp in _fileIndex)
                    {
                        WriteFileResponse(writer, kvp.Key, kvp.Value);
                    }
                }
            }
            else
            {
                // Single file
                if (_fileIndex.TryGetValue(virtualPath, out var entry))
                {
                    WriteFileResponse(writer, virtualPath, entry);
                }
            }

            writer.WriteEndElement(); // multistatus
        }

        var xml = ms.ToArray();
        context.Response.ContentLength = xml.Length;
        await context.Response.Body.WriteAsync(xml);
    }

    private static void WriteCollectionResponse(XmlWriter writer, string href)
    {
        writer.WriteStartElement("D", "response", "DAV:");
        writer.WriteElementString("D", "href", "DAV:", href);
        writer.WriteStartElement("D", "propstat", "DAV:");
        writer.WriteStartElement("D", "prop", "DAV:");

        writer.WriteStartElement("D", "resourcetype", "DAV:");
        writer.WriteStartElement("D", "collection", "DAV:");
        writer.WriteEndElement(); // collection
        writer.WriteEndElement(); // resourcetype

        writer.WriteElementString("D", "getlastmodified", "DAV:",
            DateTime.UtcNow.ToString("R"));

        writer.WriteEndElement(); // prop
        writer.WriteElementString("D", "status", "DAV:", "HTTP/1.1 200 OK");
        writer.WriteEndElement(); // propstat
        writer.WriteEndElement(); // response
    }

    private static void WriteFileResponse(XmlWriter writer, string virtualName, QdFileEntry entry)
    {
        writer.WriteStartElement("D", "response", "DAV:");
        writer.WriteElementString("D", "href", "DAV:", "/" + Uri.EscapeDataString(virtualName));
        writer.WriteStartElement("D", "propstat", "DAV:");
        writer.WriteStartElement("D", "prop", "DAV:");

        // Empty resourcetype = not a collection
        writer.WriteStartElement("D", "resourcetype", "DAV:");
        writer.WriteEndElement();

        writer.WriteElementString("D", "getcontentlength", "DAV:",
            entry.Metadata.OriginalSize.ToString());
        writer.WriteElementString("D", "getlastmodified", "DAV:",
            entry.Metadata.UploadedAt.ToUniversalTime().ToString("R"));
        writer.WriteElementString("D", "getcontenttype", "DAV:",
            GetContentType(virtualName));
        writer.WriteElementString("D", "getetag", "DAV:",
            GenerateETag(entry.Metadata));

        writer.WriteEndElement(); // prop
        writer.WriteElementString("D", "status", "DAV:", "HTTP/1.1 200 OK");
        writer.WriteEndElement(); // propstat
        writer.WriteEndElement(); // response
    }

    private async Task HandleProppatchAsync(HttpContext context)
    {
        var virtualPath = GetVirtualPath(context.Request.Path);

        if (string.IsNullOrEmpty(virtualPath) || !_fileIndex.ContainsKey(virtualPath))
        {
            context.Response.StatusCode = string.IsNullOrEmpty(virtualPath) ? 405 : 404;
            return;
        }

        // Windows Explorer sends PROPPATCH after PUT to set timestamps.
        // Accept and return 207 Multi-Status success (properties are not persisted
        // because our metadata is set at encryption time).
        context.Response.StatusCode = 207;
        context.Response.ContentType = "application/xml; charset=utf-8";

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false
        }))
        {
            writer.WriteStartElement("D", "multistatus", "DAV:");
            writer.WriteStartElement("D", "response", "DAV:");
            writer.WriteElementString("D", "href", "DAV:", context.Request.Path.Value ?? "/");
            writer.WriteStartElement("D", "propstat", "DAV:");

            writer.WriteStartElement("D", "prop", "DAV:");
            // Echo back a generic property acknowledgement
            writer.WriteStartElement("Z", "Win32LastModifiedTime", "urn:schemas-microsoft-com:");
            writer.WriteEndElement();
            writer.WriteStartElement("Z", "Win32CreationTime", "urn:schemas-microsoft-com:");
            writer.WriteEndElement();
            writer.WriteStartElement("Z", "Win32LastAccessTime", "urn:schemas-microsoft-com:");
            writer.WriteEndElement();
            writer.WriteEndElement(); // prop

            writer.WriteElementString("D", "status", "DAV:", "HTTP/1.1 200 OK");
            writer.WriteEndElement(); // propstat
            writer.WriteEndElement(); // response
            writer.WriteEndElement(); // multistatus
        }

        var xml = ms.ToArray();
        context.Response.ContentLength = xml.Length;
        await context.Response.Body.WriteAsync(xml);
    }

    private async Task HandleGetAsync(HttpContext context, bool includeBody)
    {
        var virtualPath = GetVirtualPath(context.Request.Path);

        if (string.IsNullOrEmpty(virtualPath))
        {
            context.Response.StatusCode = 405;
            return;
        }

        if (!_fileIndex.TryGetValue(virtualPath, out var entry))
        {
            Debug.WriteLine($"GET 404: '{virtualPath}' not in index. Keys: [{string.Join(", ", _fileIndex.Keys)}]");
            context.Response.StatusCode = 404;
            return;
        }

        var privateKey = _identityService.MlKemPrivateKey;
        if (privateKey is null)
        {
            context.Response.StatusCode = 403;
            return;
        }

        byte[] plaintext;
        try
        {
            var encrypted = File.ReadAllBytes(entry.QdFilePath);
            var (data, _) = _cryptoService.DecryptFile(encrypted, privateKey);
            plaintext = data;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Decrypt failed for {virtualPath}: {ex.Message}");
            context.Response.StatusCode = 500;
            return;
        }

        Debug.WriteLine($"GET '{virtualPath}' → {plaintext.Length} bytes decrypted");

        context.Response.StatusCode = 200;
        context.Response.ContentType = GetContentType(virtualPath);
        context.Response.ContentLength = plaintext.Length;

        // Add cache control headers
        context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
        context.Response.Headers["ETag"] = GenerateETag(entry.Metadata);
        context.Response.Headers["Last-Modified"] = entry.Metadata.UploadedAt.ToUniversalTime().ToString("R");

        if (includeBody)
        {
            await context.Response.Body.WriteAsync(plaintext);
            await context.Response.Body.FlushAsync();
        }
    }

    private static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".doc" or ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" or ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };
    }

    private static string GenerateETag(FileMetadata metadata)
    {
        return $"\"{metadata.SHA256Hash}-{metadata.UploadedAt.Ticks}\"";
    }

    private static string GenerateCollectionETag(IEnumerable<QdFileEntry> entries)
    {
        var combined = string.Join("|", entries
            .OrderBy(e => e.Metadata.OriginalName)
            .Select(e => $"{e.Metadata.OriginalName}:{e.Metadata.UploadedAt.Ticks}"));

        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(combined)));
        return $"\"{hash}\"";
    }

    private async Task HandlePutAsync(HttpContext context)
    {
        var virtualPath = GetVirtualPath(context.Request.Path);

        if (string.IsNullOrEmpty(virtualPath))
        {
            context.Response.StatusCode = 405;
            return;
        }

        var publicKey = _identityService.MlKemPublicKey;
        if (publicKey is null)
        {
            context.Response.StatusCode = 403;
            return;
        }

        // Enforce file limit for new files (updates to existing files are always allowed)
        if (!_fileIndex.ContainsKey(virtualPath) && _fileIndex.Count >= FileLimit)
        {
            context.Response.StatusCode = 507; // Insufficient Storage
            return;
        }

        using var ms = new MemoryStream();
        await context.Request.Body.CopyToAsync(ms);
        var plaintext = ms.ToArray();

        var metadata = new FileMetadata
        {
            OriginalName = virtualPath,
            OriginalSize = plaintext.Length,
            UploadedAt = DateTime.UtcNow,
            SHA256Hash = Convert.ToBase64String(SHA256.HashData(plaintext))
        };

        var encrypted = _cryptoService.EncryptFile(plaintext, publicKey, metadata);
        metadata.EncryptedSize = encrypted.Length;

        // Determine .qd file path
        string qdPath;
        if (_fileIndex.TryGetValue(virtualPath, out var existing))
        {
            qdPath = existing.QdFilePath;
        }
        else
        {
            qdPath = Path.Combine(_vaultPath, $"{virtualPath}.qd");
            var counter = 1;
            while (File.Exists(qdPath) && !_fileIndex.ContainsKey(virtualPath))
            {
                qdPath = Path.Combine(_vaultPath, $"{virtualPath} ({counter}).qd");
                counter++;
            }
        }

        await File.WriteAllBytesAsync(qdPath, encrypted);

        _fileIndex[virtualPath] = new QdFileEntry
        {
            QdFilePath = qdPath,
            Metadata = metadata,
            EncryptedSize = encrypted.Length
        };

        context.Response.StatusCode = existing is not null ? 204 : 201;
    }

    private void HandleDelete(HttpContext context)
    {
        var virtualPath = GetVirtualPath(context.Request.Path);

        if (string.IsNullOrEmpty(virtualPath))
        {
            context.Response.StatusCode = 405;
            return;
        }

        if (!_fileIndex.TryRemove(virtualPath, out var entry))
        {
            context.Response.StatusCode = 404;
            return;
        }

        try
        {
            File.Delete(entry.QdFilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Delete failed for {entry.QdFilePath}: {ex.Message}");
        }

        context.Response.StatusCode = 204;
    }

    private void HandleMove(HttpContext context)
    {
        var sourcePath = GetVirtualPath(context.Request.Path);
        var destinationHeader = context.Request.Headers["Destination"].FirstOrDefault();

        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(destinationHeader))
        {
            context.Response.StatusCode = 400;
            return;
        }

        // Parse destination URI → virtual path
        string destPath;
        if (Uri.TryCreate(destinationHeader, UriKind.Absolute, out var destUri))
        {
            destPath = Uri.UnescapeDataString(destUri.AbsolutePath).TrimStart('/');
        }
        else
        {
            destPath = Uri.UnescapeDataString(destinationHeader).TrimStart('/');
        }

        if (string.IsNullOrEmpty(destPath))
        {
            context.Response.StatusCode = 400;
            return;
        }

        if (!_fileIndex.TryGetValue(sourcePath, out var entry))
        {
            context.Response.StatusCode = 404;
            return;
        }

        var overwrite = context.Request.Headers["Overwrite"].FirstOrDefault() != "F";
        if (!overwrite && _fileIndex.ContainsKey(destPath))
        {
            context.Response.StatusCode = 412; // Precondition Failed
            return;
        }

        var privateKey = _identityService.MlKemPrivateKey;
        var publicKey = _identityService.MlKemPublicKey;
        if (privateKey is null || publicKey is null)
        {
            context.Response.StatusCode = 403;
            return;
        }

        try
        {
            var encrypted = File.ReadAllBytes(entry.QdFilePath);
            var (plaintext, _) = _cryptoService.DecryptFile(encrypted, privateKey);

            var metadata = new FileMetadata
            {
                OriginalName = destPath,
                OriginalSize = plaintext.Length,
                UploadedAt = entry.Metadata.UploadedAt,
                SHA256Hash = Convert.ToBase64String(SHA256.HashData(plaintext))
            };

            var newEncrypted = _cryptoService.EncryptFile(plaintext, publicKey, metadata);
            metadata.EncryptedSize = newEncrypted.Length;

            var newQdPath = Path.Combine(_vaultPath, $"{destPath}.qd");
            File.WriteAllBytes(newQdPath, newEncrypted);

            // Remove source from index
            _fileIndex.TryRemove(sourcePath, out _);
            if (entry.QdFilePath != newQdPath)
                File.Delete(entry.QdFilePath);

            _fileIndex[destPath] = new QdFileEntry
            {
                QdFilePath = newQdPath,
                Metadata = metadata,
                EncryptedSize = newEncrypted.Length
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Move failed: {ex.Message}");
            context.Response.StatusCode = 500;
            return;
        }

        context.Response.StatusCode = _fileIndex.ContainsKey(destPath) ? 204 : 201;
    }

    private async Task HandleLockAsync(HttpContext context)
    {
        // Return a fake lock token — needed by Windows Office apps
        var token = $"opaquelocktoken:{Guid.NewGuid()}";
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/xml; charset=utf-8";

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false
        }))
        {
            writer.WriteStartElement("D", "prop", "DAV:");
            writer.WriteStartElement("D", "lockdiscovery", "DAV:");
            writer.WriteStartElement("D", "activelock", "DAV:");

            writer.WriteStartElement("D", "locktype", "DAV:");
            writer.WriteStartElement("D", "write", "DAV:");
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("D", "lockscope", "DAV:");
            writer.WriteStartElement("D", "exclusive", "DAV:");
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteElementString("D", "depth", "DAV:", "0");

            writer.WriteStartElement("D", "locktoken", "DAV:");
            writer.WriteElementString("D", "href", "DAV:", token);
            writer.WriteEndElement();

            writer.WriteElementString("D", "timeout", "DAV:", "Second-3600");

            writer.WriteEndElement(); // activelock
            writer.WriteEndElement(); // lockdiscovery
            writer.WriteEndElement(); // prop
        }

        var xml = ms.ToArray();
        context.Response.ContentLength = xml.Length;
        context.Response.Headers["Lock-Token"] = $"<{token}>";
        await context.Response.Body.WriteAsync(xml);
    }

    public void Dispose()
    {
        if (_fileWatcher is not null)
        {
            _fileWatcher.EnableRaisingEvents = false;
            _fileWatcher.Created -= OnFileChanged;
            _fileWatcher.Changed -= OnFileChanged;
            _fileWatcher.Deleted -= OnFileDeleted;
            _fileWatcher.Renamed -= OnFileRenamed;
            _fileWatcher.Dispose();
        }
    }

    private class QdFileEntry
    {
        public required string QdFilePath { get; set; }
        public required FileMetadata Metadata { get; set; }
        public long EncryptedSize { get; set; }
    }
}
