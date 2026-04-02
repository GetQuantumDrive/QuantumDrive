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
    public Microsoft.UI.Xaml.Media.Brush? IconBrush { get; set; }
}
