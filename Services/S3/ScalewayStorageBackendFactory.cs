namespace quantum_drive.Services.S3;

/// <summary>
/// Storage backend factory for Scaleway Object Storage.
///
/// <para>
/// Scaleway is a French cloud provider (Iliad Group) with EU-based data centers.
/// Object Storage is available in Amsterdam (<c>nl-ams</c>), Paris (<c>fr-par</c>),
/// and Warsaw (<c>pl-waw</c>).
/// </para>
///
/// <para>
/// <b>Getting credentials:</b> log in to console.scaleway.com → IAM → API Keys →
/// Generate API Key. Assign the <c>ObjectStorageObjectsAll</c> permission set.
/// Create a bucket in the same region via Object Storage → Buckets.
/// </para>
///
/// <para>
/// Endpoint format: <c>s3.{region}.scw.cloud</c>.
/// Path-style URL: <c>https://s3.{region}.scw.cloud/{bucket}/{key}</c>.
/// </para>
/// </summary>
public sealed class ScalewayStorageBackendFactory : S3StorageBackendFactoryBase
{
    public override string Id          => "scaleway-s3";
    public override string DisplayName => "Scaleway";

    public override IReadOnlyList<S3RegionItem> AvailableRegions =>
    [
        new("nl-ams", "Amsterdam (nl-ams)"),
        new("fr-par", "Paris (fr-par)"),
        new("pl-waw", "Warsaw (pl-waw)"),
    ];

    public override string GetEndpoint(string regionId) => $"s3.{regionId}.scw.cloud";
}
