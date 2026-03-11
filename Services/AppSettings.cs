using System.Text.Json;

namespace quantum_drive.Services;

internal static class AppSettings
{
    private static readonly string _settingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuantumDrive");

    private static readonly string _settingsPath = Path.Combine(_settingsDir, "settings.json");

    public static string LocalDataPath => _settingsDir;

    private static AppSettingsData _data = Load();

    public static string? RegisteredVaults
    {
        get => _data.RegisteredVaults;
        set { _data.RegisteredVaults = value; Save(); }
    }

    public static bool AutoMountOnUnlock
    {
        get => _data.AutoMountOnUnlock;
        set { _data.AutoMountOnUnlock = value; Save(); }
    }

    private static AppSettingsData Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettingsData>(json) ?? new();
            }
        }
        catch { /* corrupt settings — start fresh */ }
        return new();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(_settingsDir);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_data));
        }
        catch { /* best effort */ }
    }

    private sealed class AppSettingsData
    {
        public string? RegisteredVaults { get; set; }
        public bool AutoMountOnUnlock { get; set; }
    }
}
