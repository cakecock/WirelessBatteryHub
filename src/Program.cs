using WirelessBatteryHub.Forms;

namespace WirelessBatteryHub;

internal static class Program
{
    public const string ShowWindowEventName = "WirelessBatteryHub_ShowWindow_Event";

    [STAThread]
    private static void Main(string[] args)
    {
        bool startMinimized = args.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-m", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, "WirelessBatteryHub_SingleInstance_Mutex", out bool createdNew);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);

        if (!createdNew)
        {
            if (!startMinimized) showEvent.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(startMinimized, showEvent));
    }
}
