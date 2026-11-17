using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WirelessBatteryHub.Services;

public sealed class DeviceArrivalWatcher : IDisposable
{
    public const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private const int DBT_DEVTYP_DEVICEINTERFACE = 5;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    private const uint DEVICE_NOTIFY_ALL_INTERFACE_CLASSES = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public short dbcc_name;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    private IntPtr _notifyHandle;
    private readonly System.Windows.Forms.Timer _debounceTimer;
    private readonly Action _onHardwareChanged;

    public DeviceArrivalWatcher(IntPtr windowHandle, Action onHardwareChanged)
    {
        _onHardwareChanged = onHardwareChanged;
        _debounceTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _onHardwareChanged();
        };

        Register(windowHandle);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void Register(IntPtr hwnd)
    {
        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE
        };

        IntPtr buf = Marshal.AllocHGlobal(filter.dbcc_size);
        try
        {
            Marshal.StructureToPtr(filter, buf, true);
            _notifyHandle = RegisterDeviceNotification(
                hwnd, buf, DEVICE_NOTIFY_WINDOW_HANDLE | DEVICE_NOTIFY_ALL_INTERFACE_CLASSES);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public void HandleWndProc(ref Message m)
    {
        if (m.Msg != WM_DEVICECHANGE) return;
        int evt = m.WParam.ToInt32();
        if (evt is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE or DBT_DEVNODES_CHANGED)
        {
            // Группируем серию PnP-событий при вставке донгла в 1 сканирование
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _debounceTimer.Stop();
        _debounceTimer.Dispose();
        if (_notifyHandle != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_notifyHandle);
            _notifyHandle = IntPtr.Zero;
        }
    }
}
