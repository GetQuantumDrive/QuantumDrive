namespace quantum_drive.Models;

public class VaultDescriptor
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Storage backend identifier (e.g. "local", "google-drive"). Defaults to "local".</summary>
    public string BackendId { get; set; } = "local";

    /// <summary>Backend-specific configuration (OAuth tokens, remote folder IDs, etc.).</summary>
    public Dictionary<string, string> BackendConfig { get; set; } = [];
}
