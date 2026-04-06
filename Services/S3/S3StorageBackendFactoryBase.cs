using quantum_drive.Models;

namespace quantum_drive.Services.S3;

/// <summary>
/// Abstract base class for all S3-compatible storage backend factories.
///
/// <para>
/// Implements <see cref="IStorageBackendFactory"/> only — not
/// <see cref="ICloudStorageBackendFactory"/> — because S3 uses static access-key
/// authentication rather than OAuth. The setup wizard detects S3 factories by
/// casting to this type and shows a credential-input form instead of a browser
/// OAuth button.
/// </para>
///
/// <para>
/// <b>BackendConfig keys assembled by <see cref="BuildConfig"/>:</b>
/// <list type="table">
/// <item><term><c>access_key</c></term><description>User's S3 access key ID.</description></item>
/// <item><term><c>secret_key</c></term><description>User's S3 secret access key.</description></item>
/// <item><term><c>bucket</c></term><description>Bucket name.</description></item>
/// <item><term><c>region</c></term><description>Region ID, e.g. <c>nl-ams</c>.</description></item>
/// <item><term><c>endpoint</c></term><description>API hostname resolved from region, e.g. <c>s3.nl-ams.scw.cloud</c>.</description></item>
/// <item><term><c>account_label</c></term><description>Display string shown on the dashboard, e.g. <c>my-bucket (nl-ams)</c>.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Adding a new S3-compatible provider</b> requires only a new subclass (~15 lines)
/// that overrides <see cref="Id"/>, <see cref="DisplayName"/>,
/// <see cref="AvailableRegions"/>, and <see cref="GetEndpoint"/>, plus a single
/// <c>backendRegistry.Register(new MyFactory())</c> line in <c>App.xaml.cs</c>.
/// No changes to <see cref="S3Signer"/> or <see cref="S3StorageBackend"/> are needed.
/// </para>
/// </summary>
public abstract class S3StorageBackendFactoryBase : IStorageBackendFactory
{
    // ── Subclass contract ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public abstract string Id { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <summary>
    /// Region options presented in the wizard ComboBox.
    /// Each item is a <see cref="S3RegionItem"/> with a stable <c>RegionId</c>
    /// (used in the BackendConfig) and a human-readable <c>RegionName</c> (shown in the UI).
    /// </summary>
    public abstract IReadOnlyList<S3RegionItem> AvailableRegions { get; }

    /// <summary>
    /// Returns the S3 endpoint hostname (without scheme) for the given
    /// <paramref name="regionId"/>.
    /// <para>Example for Scaleway: <c>$"s3.{regionId}.scw.cloud"</c></para>
    /// </summary>
    public abstract string GetEndpoint(string regionId);

    // ── IStorageBackendFactory ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public IStorageBackend CreateForVault(VaultDescriptor vault)
        => new S3StorageBackend(vault.Id, vault.BackendConfig);

    // ── Credential helpers (called by ViewModel) ───────────────────────────────

    /// <summary>
    /// Builds the <c>BackendConfig</c> dictionary from the credentials the user
    /// entered in the wizard. The endpoint is derived automatically from
    /// <paramref name="regionId"/> via <see cref="GetEndpoint"/>.
    /// </summary>
    public Dictionary<string, string> BuildConfig(
        string accessKey, string secretKey, string bucket, string regionId)
        => new()
        {
            ["access_key"]    = accessKey,
            ["secret_key"]    = secretKey,
            ["bucket"]        = bucket,
            ["region"]        = regionId,
            ["endpoint"]      = GetEndpoint(regionId),
            ["account_label"] = $"{bucket} ({regionId})",
        };

    /// <summary>
    /// Returns the display label stored in <paramref name="config"/> (e.g.
    /// <c>"my-bucket (nl-ams)"</c>), or <see langword="null"/> if the key is absent.
    /// </summary>
    public string? GetConfiguredLabel(IReadOnlyDictionary<string, string> config)
        => config.TryGetValue("account_label", out var label) ? label : null;
}

/// <summary>
/// A region option for an S3-compatible provider, used as a ComboBox item in the
/// setup wizard.
/// </summary>
/// <param name="RegionId">
/// The stable region identifier stored in <c>BackendConfig["region"]</c>,
/// e.g. <c>"nl-ams"</c>.
/// </param>
/// <param name="RegionName">
/// Human-readable region label shown in the UI, e.g. <c>"Amsterdam (nl-ams)"</c>.
/// </param>
public sealed record S3RegionItem(string RegionId, string RegionName);
