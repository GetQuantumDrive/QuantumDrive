# Contributing a Storage Provider

This guide walks you through adding a new storage backend to QuantumDrive.
All built-in providers (Local Folder, Google Drive, Dropbox, OneDrive) follow
exactly this pattern.

---

## Overview

QuantumDrive's storage layer is a plugin system built on two interfaces:

| Interface | Purpose |
|-----------|---------|
| `IStorageBackendFactory` | Creates backend instances; provides `Id` and `DisplayName` |
| `IStorageBackend` | Per-vault I/O: list, read, write, delete, watch |
| `ICloudStorageBackendFactory` | Extends factory for OAuth providers (optional) |

---

## Step 1 — Implement `IStorageBackendFactory`

Create a class that implements `IStorageBackendFactory` in
`Services/<ProviderName>/<ProviderName>StorageBackendFactory.cs`:

```csharp
namespace quantum_drive.Services.MyProvider;

public sealed class MyProviderStorageBackendFactory : IStorageBackendFactory
{
    public string Id => "my-provider";           // stable, lowercase, never change after vaults exist
    public string DisplayName => "My Provider";  // shown in setup wizard

    public IStorageBackend CreateForVault(VaultDescriptor vault)
        => new MyProviderStorageBackend(vault.Id, vault.BackendConfig);
}
```

**`Id` rules:**
- Lowercase, hyphen-separated (e.g. `"my-provider"`)
- Must be unique across all registered factories
- **Never change** once users have created vaults with this backend — it is
  stored in `settings.json` and used to look up the factory on every app launch

---

## Step 2 — Implement `IStorageBackend`

Create `Services/<ProviderName>/<ProviderName>StorageBackend.cs`.

For cloud providers, inherit from `CloudStorageBackendBase` which gives you:
- Thread-safe token refresh (`EnsureFreshTokenAsync` / `RefreshTokenAsync`)
- 30-second polling watch loop
- `GetQdPath` implementations

For local/self-hosted providers, implement `IStorageBackend` directly (see
`LocalStorageBackend` as reference).

```csharp
internal sealed class MyProviderStorageBackend : CloudStorageBackendBase
{
    public MyProviderStorageBackend(string vaultId, Dictionary<string, string> config)
        : base(vaultId, config) { }

    public override Task<IEnumerable<string>> ListFilesAsync(string extension, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        // Call your provider's list API, filtering by extension.
        // Populate NameToId cache: NameToId[filename] = remoteId;
        // Return file names (not full paths) — GetQdPath returns just the filename for cloud.
    }

    public override Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        // Download file content. Buffer in MemoryStream so HTTP connection is released.
        // Throw FileNotFoundException if file does not exist.
    }

    public override Task WriteAsync(string path, Stream content, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        // Upload content. Overwrite if file already exists (use NameToId to check).
        // Update NameToId cache after successful upload.
    }

    public override Task DeleteAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        // Delete file. Must be a no-op (not throw) if file does not exist.
        // Remove from NameToId cache.
    }

    public override Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        await EnsureFreshTokenAsync(ct);
        // Check NameToId first (fast path). Fall back to API call.
        // Never throw for a missing file — return false instead.
    }

    protected override Task RefreshTokenAsync(CancellationToken ct)
    {
        // POST to token endpoint with grant_type=refresh_token.
        // Update Config["access_token"] and Config["token_expiry"] in-memory.
    }
}
```

### `BackendConfig` keys

Document every key your backend reads from / writes to `BackendConfig`:

| Key | Description |
|-----|-------------|
| `access_token` | Short-lived bearer token |
| `refresh_token` | Long-lived refresh token |
| `token_expiry` | ISO-8601 UTC expiry (`DateTime.UtcNow.AddSeconds(n).ToString("O")`) |
| `account_email` | User's email (for wizard display) |
| *(provider-specific)* | e.g. `remote_folder_id`, `tenant_id` |

---

## Step 3 — OAuth providers: implement `ICloudStorageBackendFactory`

If your backend requires user sign-in, implement `ICloudStorageBackendFactory`
instead of just `IStorageBackendFactory`:

```csharp
public sealed class MyProviderStorageBackendFactory : ICloudStorageBackendFactory
{
    internal const string ClientId = "YOUR_CLIENT_ID"; // fill from your developer portal

    public async Task<Dictionary<string, string>> AuthorizeAsync(
        Window parentWindow, CancellationToken ct = default)
    {
        // 1. Generate PKCE
        var (verifier, challenge) = OAuthLoopbackHelper.GeneratePkce();
        var port = OAuthLoopbackHelper.GetFreePort();
        var redirectUri = OAuthLoopbackHelper.BuildRedirectUri(port);

        // 2. Open browser
        Process.Start(new ProcessStartInfo(BuildAuthUrl(redirectUri, challenge)) { UseShellExecute = true });

        // 3. Wait for redirect code
        var code = await OAuthLoopbackHelper.WaitForAuthCodeAsync(redirectUri, ct);

        // 4. Exchange code for tokens
        var tokenJson = await OAuthLoopbackHelper.ExchangeCodeForTokensAsync(
            "https://your-provider.com/oauth2/token",
            new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = ClientId,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier,
            }, ct);

        // 5. Parse tokens and fetch account email
        var config = ParseTokenResponse(tokenJson);
        config["account_email"] = await FetchEmailAsync(config["access_token"], ct);

        // 6. Create remote folder if needed
        // config["remote_folder_id"] = await EnsureRemoteFolderAsync(...);

        return config;
    }

    public string? GetConnectedAccount(IReadOnlyDictionary<string, string> config)
        => config.TryGetValue("account_email", out var e) ? e : null;
}
```

The setup wizard detects `ICloudStorageBackendFactory` automatically and shows
a "Connect Account" button that calls `AuthorizeAsync` before allowing the user
to proceed to vault setup.

---

## Step 4 — Register in `App.xaml.cs`

Add one line after the existing registrations:

```csharp
backendRegistry.Register(new LocalStorageBackendFactory());
backendRegistry.Register(new GoogleDriveStorageBackendFactory());
backendRegistry.Register(new DropboxStorageBackendFactory());
backendRegistry.Register(new OneDriveStorageBackendFactory());
backendRegistry.Register(new MyProviderStorageBackendFactory()); // ← add this
```

---

## Step 5 — Test checklist

Run through these scenarios manually (or write unit tests):

- [ ] `ListFilesAsync` returns empty when vault folder does not exist yet
- [ ] `ListFilesAsync` returns all `.qd` files after writing some
- [ ] `WriteAsync` creates a new file
- [ ] `WriteAsync` overwrites an existing file (content replaced, not appended)
- [ ] `OpenReadAsync` returns the correct content after `WriteAsync`
- [ ] `OpenReadAsync` throws `FileNotFoundException` for a non-existent path
- [ ] `DeleteAsync` removes the file
- [ ] `DeleteAsync` is a no-op when the file does not exist (does not throw)
- [ ] `ExistsAsync` returns `true` after write, `false` after delete
- [ ] `Watch` fires `StorageChangeType.Created` when a file appears externally
- [ ] `Watch` fires `StorageChangeType.Deleted` when a file is removed externally
- [ ] Token refresh: set `token_expiry` to a past time in `settings.json`,
      then access a file — the token should be refreshed transparently
- [ ] App restart: close and reopen QuantumDrive with an existing cloud vault —
      vault should be listed and unlockable without re-authorizing
- [ ] Vault creation does not regress existing local vault flow

---

## PR checklist

Before opening a pull request:

- [ ] `IStorageBackendFactory.Id` is lowercase, hyphen-separated, and unique
- [ ] `Id` is documented in a comment noting it must never change
- [ ] All `BackendConfig` keys are documented in the factory's XML summary
- [ ] `DeleteAsync` is a no-op for missing files
- [ ] `ExistsAsync` never throws for a missing file
- [ ] Token refresh uses `SemaphoreSlim` (provided by `CloudStorageBackendBase`)
- [ ] No credentials (real client IDs/secrets) are committed — use placeholder
      strings like `"YOUR_CLIENT_ID"` with a comment linking to the developer portal
- [ ] OAuth scopes are minimal (principle of least privilege)
- [ ] Added to the factory registration list in `App.xaml.cs`
