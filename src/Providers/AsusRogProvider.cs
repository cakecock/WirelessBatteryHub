using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class AsusRogProvider : IBatteryProvider
{
    public string ProviderName => "ASUS ROG / TUF";
    private const int AsusVid = 0x0B05;

    public bool CanHandle(string vidPidKey) =>
        vidPidKey.StartsWith("0B05:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();
        var seenPid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (HidDevice dev in DeviceList.Local.GetHidDevices(vendorID: AsusVid))
        {
            string key = $"{dev.VendorID:X4}:{dev.ProductID:X4}";
            if (targetVidPid != null && !key.Equals(targetVidPid, StringComparison.OrdinalIgnoreCase)) continue;
            if (seenPid.Contains(key) || !IsVendorCollection(dev)) continue;

            try
            {
                if (!dev.TryOpen(out HidStream stream)) continue;
                using (stream)
                {
                    stream.ReadTimeout = 200;
                    stream.WriteTimeout = 200;

                    int outLen = Math.Max(dev.GetMaxOutputReportLength(), 65);
                    byte[] req = new byte[outLen];
                    req[0] = 0x00;
                    req[1] = 0x12;
                    req[2] = 0x07;
                    stream.Write(req);

                    byte[] resp = new byte[Math.Max(dev.GetMaxInputReportLength(), 65)];
                    int n = stream.Read(resp, 0, resp.Length);
                    if (n >= 11 && resp[1] == 0x12 && resp[2] == 0x07)
                    {
                        int raw = resp[5];
                        bool charging = resp[10] != 0;
                        if (raw == 0 && !charging) continue; // Сон / режим ожидания

                        int pct = raw <= 4 ? raw * 25 : Math.Clamp(raw, 0, 100);
                        seenPid.Add(key);
                        string name = SafeProductName(dev, key);

                        result.Add(new DeviceBatteryInfo
                        {
                            Id = $"ASUS:{key}",
                            Name = name,
                            Kind = DeviceClassifier.GuessFromHidSibling(dev.VendorID, dev.ProductID, name),
                            ProtocolName = ProviderName,
                            ConnectionType = "2.4 ГГц (ASUS ROG)",
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

    private static bool IsVendorCollection(HidDevice dev)
    {
        try
        {
            if (dev.GetMaxOutputReportLength() < 16 || dev.GetMaxInputReportLength() < 16) return false;
            return dev.GetReportDescriptor().DeviceItems
                .SelectMany(x => x.Usages.GetAllValues())
                .Any(u => (u >> 16) >= 0xFF00);
        }
        catch { return false; }
    }

    private static string SafeProductName(HidDevice dev, string key)
    {
        try { return dev.GetProductName()?.Trim() ?? $"ASUS ROG ({key})"; }
        catch { return $"ASUS ROG ({key})"; }
    }
}
