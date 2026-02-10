namespace quantum_drive.Models;

public class CloudProviderItem
{
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public bool IsLocked { get; set; }
    public string Tier { get; set; } = "Free";
    public string Description { get; set; } = string.Empty;
}
