using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class SonyGamepadProvider : IBatteryProvider
{
    public string ProviderName => "Sony PlayStation HID";
    private const int SonyVid = 0x054C;

    public bool CanHandle(string vidPidKey) =>
        vidPidKey.Equals("054C:0CE6", StringComparison.OrdinalIgnoreCase) || // DualSense
        vidPidKey.Equals("054C:0DF2", StringComparison.OrdinalIgnoreCase) || // DualSense Edge
        vidPidKey.Equals("054C:05C4", StringComparison.OrdinalIgnoreCase) || // DualShock 4 v1
        vidPidKey.Equals("054C:09CC", StringComparison.OrdinalIgnoreCase) || // DualShock 4 v2
        vidPidKey.Equals("054C:0BA0", StringComparison.OrdinalIgnoreCase);   // DS4 USB Adapter

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();
        var seenPid = new HashSet<int>();

        foreach (HidDevice dev in DeviceList.Local.GetHidDevices(vendorID: SonyVid))
        {
            string key = $"{dev.VendorID:X4}:{dev.ProductID:X4}";
            if (!CanHandle(key)) continue;
            if (targetVidPid != null && !key.Equals(targetVidPid, StringComparison.OrdinalIgnoreCase)) continue;
            if (seenPid.Contains(dev.ProductID)) continue;

            int inLen;
            try { inLen = dev.GetMaxInputReportLength(); } catch { continue; }
            if (inLen < 10) continue;

            bool isDualSense = dev.ProductID is 0x0CE6 or 0x0DF2;

            try
            {
                if (!dev.TryOpen(out HidStream stream)) continue;
                using (stream)
                {
                    stream.ReadTimeout = 200;
                    byte[] buf = new byte[Math.Max(inLen, 80)];
                    int n = stream.Read(buf, 0, buf.Length);

                    // Если по Bluetooth геймпад шлёт базовый короткий репорт 0x01 — запрашиваем калибровочный Feature, чтобы включить полный репорт
                    if (n > 0 && n < 64 && buf[0] == 0x01)
                    {
                        try
                        {
                            byte[] feat = new byte[isDualSense ? 41 : 37];
                            feat[0] = (byte)(isDualSense ? 0x05 : 0x02);
                            stream.GetFeature(feat);
                            n = stream.Read(buf, 0, buf.Length);
                        }
                        catch { }
                    }

                    if (TryParseBattery(isDualSense, buf, n, out int pct, out bool charging, out bool isBt))
                    {
                        seenPid.Add(dev.ProductID);
                        string name = SafeName(dev, isDualSense);
                        result.Add(new DeviceBatteryInfo
                        {
                            Id = $"SONY:{dev.ProductID:X4}",
                            Name = name,
                            Kind = DeviceKind.Gamepad,
                            ProtocolName = ProviderName,
                            ConnectionType = isBt ? "Bluetooth (Sony)" : "USB (Sony)",
                            VidPid = key,
                            BatteryPercent = pct,
                            IsConnected = true,
                            IsCharging = charging,
                            StatusNote = charging ? "Заряжается" : "От батареи"
                        });
                    }
                }
            }
            catch { }
        }

        return result;
    }

    private static bool TryParseBattery(bool isDualSense, byte[] buf, int n, out int pct, out bool charging, out bool isBt)
    {
        pct = 0;
        charging = false;
        isBt = false;

        if (isDualSense)
        {
            byte? status = null;
            if (n >= 54 && buf[0] == 0x01) { status = buf[53]; isBt = false; }
            else if (n >= 55 && buf[0] == 0x31) { status = buf[54]; isBt = true; }

            if (!status.HasValue) return false;
            int lvl = status.Value & 0x0F;
            int chgState = (status.Value >> 4) & 0x0F;
            charging = chgState is 1 or 2;
            pct = chgState == 2 ? 100 : Math.Clamp(lvl * 10 + 5, 0, 100);
            if (lvl == 0 && !charging) pct = 5;
            return true;
        }
        else
        {
            byte? status = null;
            if (n >= 31 && buf[0] == 0x01) { status = buf[30]; isBt = false; }
            else if (n >= 33 && buf[0] == 0x11) { status = buf[32]; isBt = true; }

            if (!status.HasValue) return false;
            charging = ((status.Value >> 4) & 0x01) != 0;
            int lvl = status.Value & 0x0F;
            pct = lvl >= 10 ? 100 : Math.Clamp(lvl * 10 + 5, 0, 100);
            return true;
        }
    }

    private static string SafeName(HidDevice dev, bool isDualSense)
    {
        try
        {
            string? p = dev.GetProductName()?.Trim();
            if (!string.IsNullOrWhiteSpace(p) && !p.Equals("Wireless Controller", StringComparison.OrdinalIgnoreCase))
                return p;
        }
        catch { }

        return dev.ProductID switch
        {
            0x0DF2 => "DualSense Edge",
            0x0CE6 => "Sony DualSense",
            _ => isDualSense ? "Sony DualSense" : "Sony DualShock 4"
        };
    }
}
