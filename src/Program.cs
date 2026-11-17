using WirelessBatteryHub.Forms;

namespace WirelessBatteryHub;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool startMinimized = args.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-m", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, "WirelessBatteryHub_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(startMinimized));
    }
}
