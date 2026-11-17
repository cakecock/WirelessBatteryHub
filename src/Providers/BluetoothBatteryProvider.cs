using System.Runtime.InteropServices;
using System.Text;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class BluetoothBatteryProvider : IBatteryProvider
{
    public string ProviderName => "Bluetooth / Windows HID";
    public bool CanHandle(string vidPidKey) => false;

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, ref DEVPROPKEY PropertyKey,
        out uint PropertyType, byte[] PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize, uint Flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        StringBuilder DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    private static DEVPROPKEY DEVPKEY_Battery = new() { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
    private static DEVPROPKEY DEVPKEY_FriendlyName = new() { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
    private static DEVPROPKEY DEVPKEY_DeviceDesc = new() { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 2 };
    private static DEVPROPKEY DEVPKEY_IsConnected = new() { fmtid = new Guid("83DA6326-97A6-4088-9453-A1923F573B29"), pid = 15 };

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IntPtr devSet = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (devSet == INVALID_HANDLE_VALUE) return result;

        try
        {
            var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };

            for (uint i = 0; SetupDiEnumDeviceInfo(devSet, i, ref devInfo); i++)
            {
                byte? bat = GetByteProperty(devSet, ref devInfo, DEVPKEY_Battery);
                if (!bat.HasValue || bat.Value > 100) continue;

                string? instanceId = GetInstanceId(devSet, ref devInfo);
                if (string.IsNullOrEmpty(instanceId) || !seen.Add(instanceId)) continue;

                string? name = GetStringProperty(devSet, ref devInfo, DEVPKEY_FriendlyName)
                            ?? GetStringProperty(devSet, ref devInfo, DEVPKEY_DeviceDesc)
                            ?? instanceId;

                byte? conn = GetByteProperty(devSet, ref devInfo, DEVPKEY_IsConnected);
                bool isConnected = !conn.HasValue || conn.Value != 0;
                bool isBt = instanceId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);

                result.Add(new DeviceBatteryInfo
                {
                    Id = "BT:" + instanceId,
                    Name = name,
                    Kind = DeviceClassifier.GuessByName(name, DeviceKind.Other),
                    ProtocolName = isBt ? "Bluetooth" : "Windows HID / PnP",
                    ConnectionType = isBt ? "Bluetooth" : "Windows HID / PnP",
                    BatteryPercent = isConnected ? bat.Value : null,
                    IsConnected = isConnected,
                    StatusNote = isConnected ? "Активно" : "Отключено"
                });
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devSet);
        }

        return result;
    }

    private static byte? GetByteProperty(IntPtr devSet, ref SP_DEVINFO_DATA devInfo, DEVPROPKEY key)
    {
        byte[] buf = new byte[4];
        if (SetupDiGetDeviceProperty(devSet, ref devInfo, ref key, out _, buf, (uint)buf.Length, out uint reqSize, 0) && reqSize >= 1)
            return buf[0];
        return null;
    }

    private static string? GetStringProperty(IntPtr devSet, ref SP_DEVINFO_DATA devInfo, DEVPROPKEY key)
    {
        byte[] buf = new byte[512];
        if (SetupDiGetDeviceProperty(devSet, ref devInfo, ref key, out _, buf, (uint)buf.Length, out uint reqSize, 0) && reqSize >= 2)
        {
            string s = Encoding.Unicode.GetString(buf, 0, Math.Min((int)reqSize, buf.Length)).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
    }

    private static string? GetInstanceId(IntPtr devSet, ref SP_DEVINFO_DATA devInfo)
    {
        var sb = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(devSet, ref devInfo, sb, (uint)sb.Capacity, out _) ? sb.ToString() : null;
    }
}
