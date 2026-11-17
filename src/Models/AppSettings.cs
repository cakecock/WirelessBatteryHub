using System.Text.Json;

namespace WirelessBatteryHub.Models;

public enum TrayIconStyle
{
    Badge = 0,
    CleanDigits = 1,
    BatteryIcon = 2
}

public enum AppThemeMode
{
    Auto = 0,
    Dark = 1,
    Light = 2
}

public sealed class AppSettings
{
    public bool MinimizeToTray { get; set; } = true;
    public bool SeparateTrayIcons { get; set; } = true;
    public bool EnableRareTimer { get; set; } = false;
    public bool NotifyLowBattery { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 15;
    public bool NotifyFullCharge { get; set; } = true;
    public TrayIconStyle TrayStyle { get; set; } = TrayIconStyle.Badge;
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.Auto;
    public string? PrimaryDeviceId { get; set; }
    public HashSet<string> HiddenDeviceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CustomNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string GetSettingsPath()
    {
        string portable = Path.Combine(AppContext.BaseDirectory, "settings.json");
        try
        {
            string probe = Path.Combine(AppContext.BaseDirectory, ".write_test");
            if (File.Exists(portable))
            {
                using FileStream fs = File.Open(portable, FileMode.Open, FileAccess.ReadWrite);
                return portable;
            }
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return portable;
        }
        catch
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WirelessBatteryHub");
            Directory.CreateDirectory(appData);
            return Path.Combine(appData, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            string path = GetSettingsPath();
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded != null)
                {
                    loaded.HiddenDeviceIds = new HashSet<string>(loaded.HiddenDeviceIds, StringComparer.OrdinalIgnoreCase);
                    loaded.CustomNames = new Dictionary<string, string>(loaded.CustomNames, StringComparer.OrdinalIgnoreCase);
                    loaded.LowBatteryThreshold = Math.Clamp(loaded.LowBatteryThreshold, 5, 50);
                    return loaded;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string path = GetSettingsPath();
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }
}
