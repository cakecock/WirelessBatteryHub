using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class CompxNordicProvider : IBatteryProvider
{
    public string ProviderName => "Compx / Nordic 2.4G";

    private static readonly byte[] QueryFrame =
    {
        0x08, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x49
    };

    public bool CanHandle(string vidPidKey)
    {
        var parts = vidPidKey.Split(':');
        if (parts.Length != 2) return false;
        int vid = Convert.ToInt32(parts[0], 16);
        int pid = Convert.ToInt32(parts[1], 16);
        return DeviceList.Local.GetHidDevices(vid, pid).Any(IsCompxCollection);
    }

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();
        var seenPid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Приоритет коллекциям 0xFF02:0x0002 (требуется для донглов ATK / VXE / Pulsar)
        var candidates = DeviceList.Local.GetHidDevices()
            .Where(IsCompxCollection)
            .OrderByDescending(HasUsage0002);

        foreach (HidDevice dev in candidates)
        {
            string key = $"{dev.VendorID:X4}:{dev.ProductID:X4}";
            if (targetVidPid != null && !key.Equals(targetVidPid, StringComparison.OrdinalIgnoreCase))
                continue;
            if (seenPid.Contains(key)) continue;

            try
            {
                if (!dev.TryOpen(out HidStream stream)) continue;
                using (stream)
                {
                    stream.ReadTimeout = 200;
                    stream.WriteTimeout = 200;
                    stream.Write(QueryFrame);

                    byte[] resp = new byte[17];
                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(220);
                    while (DateTime.UtcNow < deadline)
                    {
                        int n = stream.Read(resp, 0, resp.Length);
                        if (n < 17 || resp[0] != 0x08 || resp[1] != 0x04) continue;

                        int sum = 0;
                        for (int i = 0; i < 17; i++) sum += resp[i];
                        if ((sum & 0xFF) != 0x55) continue;

                        int percent = resp[6];
                        if (percent > 100) continue;

                        bool charging = resp[7] != 0;
                        seenPid.Add(key);
                        string name = SafeProductName(dev, key);

                        result.Add(new DeviceBatteryInfo
                        {
                            Id = $"COMPX:{key}",
                            Name = name,
                            Kind = DeviceClassifier.GuessFromHidSibling(dev.VendorID, dev.ProductID, name),
                            ProtocolName = ProviderName,
                            ConnectionType = "2.4 ГГц (Compx/Nordic)",
                            VidPid = key,
                            BatteryPercent = percent,
                            IsConnected = true,
                            IsCharging = charging,
                            StatusNote = charging ? "Зарядка" : "От батареи"
                        });
                        break;
                    }
                }
            }
            catch { }
        }

        return result;
    }

    private static bool IsCompxCollection(HidDevice dev)
    {
        try
        {
            if (dev.GetMaxOutputReportLength() < 17 || dev.GetMaxInputReportLength() < 17) return false;
            return dev.GetReportDescriptor().DeviceItems
                .SelectMany(x => x.Usages.GetAllValues())
                .Any(u => (u >> 16) == 0xFF02);
        }
        catch { return false; }
    }

    private static bool HasUsage0002(HidDevice dev)
    {
        try
        {
            return dev.GetReportDescriptor().DeviceItems
                .SelectMany(x => x.Usages.GetAllValues())
                .Any(u => u == 0xFF020002);
        }
        catch { return false; }
    }

    private static string SafeProductName(HidDevice dev, string key)
    {
        try { return dev.GetProductName()?.Trim() ?? $"Wireless Device ({key})"; }
        catch { return $"Wireless Device ({key})"; }
    }
}
