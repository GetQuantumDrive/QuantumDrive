using Microsoft.UI.Xaml.Media;

namespace quantum_drive.Models;

public class VaultStatusItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsUnlocked { get; set; }
    public int FileCount { get; set; }
    public string SizeLabel { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    /// <summary>Per-vault accent color used for the icon background and action buttons.</summary>
    public SolidColorBrush VaultColor { get; set; } = new(Microsoft.UI.Colors.Gray);
}
