using Microsoft.Win32;

namespace WirelessBatteryHub.Services;

public static class StartupManager
{
    private const string AppKeyName = "WirelessBatteryHub";
    private const string RunRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegPath, false);
        return key?.GetValue(AppKeyName) != null;
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegPath, true);
        if (key == null) return;

        if (enabled)
        {
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue(AppKeyName, $"\"{exePath}\" --minimized");
        }
        else
        {
            key.DeleteValue(AppKeyName, false);
        }
    }
}
