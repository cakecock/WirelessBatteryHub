using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class NintendoGamepadProvider : IBatteryProvider
{
    public string ProviderName => "Nintendo HID";

    public bool CanHandle(string vidPidKey) =>
        vidPidKey.Equals("057E:2009", StringComparison.OrdinalIgnoreCase) || // Switch Pro
        vidPidKey.Equals("057E:2006", StringComparison.OrdinalIgnoreCase) || // Joy-Con (L)
        vidPidKey.Equals("057E:2007", StringComparison.OrdinalIgnoreCase) || // Joy-Con (R)
        vidPidKey.Equals("057E:200E", StringComparison.OrdinalIgnoreCase);   // Charging Grip

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();
        var seenPid = new HashSet<int>();

        foreach (HidDevice dev in DeviceList.Local.GetHidDevices(vendorID: 0x057E))
        {
            string key = $"{dev.VendorID:X4}:{dev.ProductID:X4}";
            if (!CanHandle(key)) continue;
            if (targetVidPid != null && !key.Equals(targetVidPid, StringComparison.OrdinalIgnoreCase)) continue;
            if (seenPid.Contains(dev.ProductID)) continue;

            try
            {
                if (!dev.TryOpen(out HidStream stream)) continue;
                using (stream)
                {
                    stream.ReadTimeout = 200;
                    byte[] buf = new byte[Math.Max(dev.GetMaxInputReportLength(), 64)];

                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        int n = stream.Read(buf, 0, buf.Length);
                        if (n < 3) continue;
                        if (buf[0] is not (0x30 or 0x21 or 0x31)) continue;

                        byte batConn = buf[2];
                        bool charging = (batConn & 0x10) != 0;
                        int lvl = (batConn & 0xE0) >> 5;
                        int pct = lvl switch
                        {
                            >= 4 => 100,
                            3 => 75,
                            2 => 50,
                            1 => 25,
                            _ => 5
                        };

                        seenPid.Add(dev.ProductID);
                        string name = dev.ProductID switch
                        {
                            0x2006 => "Nintendo Joy-Con (L)",
                            0x2007 => "Nintendo Joy-Con (R)",
                            _ => "Nintendo Switch Pro"
                        };

                        result.Add(new DeviceBatteryInfo
                        {
                            Id = $"NINT:{dev.ProductID:X4}",
                            Name = name,
                            Kind = DeviceKind.Gamepad,
                            ProtocolName = ProviderName,
                            ConnectionType = "Bluetooth / USB (Nintendo)",
                            VidPid = key,
                            BatteryPercent = pct,
                            IsConnected = true,
                            IsCharging = charging,
                            StatusNote = charging ? "Заряжается" : "От батареи"
                        });
                        break;
                    }
                }
            }
            catch { }
        }

        return result;
    }
}
