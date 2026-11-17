using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class HyperXHeadsetProvider : IBatteryProvider
{
    public string ProviderName => "HyperX Headset";

    private static readonly HashSet<string> CloudLegacyIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "03F0:0D93", // Cloud Stinger 2 Wireless
        "03F0:018B", // Cloud II Wireless (HP)
        "0951:1718"  // Cloud II Wireless (Kingston)
    };

    public bool CanHandle(string vidPidKey) =>
        vidPidKey.Equals("03F0:05B7", StringComparison.OrdinalIgnoreCase) || // Cloud III Wireless
        CloudLegacyIds.Contains(vidPidKey);

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (HidDevice dev in DeviceList.Local.GetHidDevices())
        {
            string key = $"{dev.VendorID:X4}:{dev.ProductID:X4}";
            if (!CanHandle(key)) continue;
            if (targetVidPid != null && !key.Equals(targetVidPid, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Contains(key)) continue;

            try
            {
                if (key.Equals("03F0:05B7", StringComparison.OrdinalIgnoreCase))
                {
                    if (!HasUsagePage(dev, 0xFF13)) continue;
                    if (!dev.TryOpen(out HidStream stream)) continue;
                    using (stream)
                    {
                        stream.ReadTimeout = 250;
                        int outLen = Math.Max(dev.GetMaxOutputReportLength(), 52);
                        int off = outLen >= 53 ? 1 : 0;
                        byte[] req = new byte[outLen];
                        req[off + 0] = 0x66;
                        req[off + 1] = 0x89;
                        stream.Write(req);

                        byte[] resp = new byte[Math.Max(dev.GetMaxInputReportLength(), 52)];
                        int n = stream.Read(resp, 0, resp.Length);
                        int rOff = (n >= 5 && resp[0] == 0x00) ? 1 : 0;
                        if (n >= rOff + 5 && resp[rOff + 4] <= 100)
                        {
                            seen.Add(key);
                            result.Add(MakeInfo(dev, key, resp[rOff + 4]));
                        }
                    }
                }
                else
                {
                    if (dev.GetMaxOutputReportLength() < 4 || dev.GetMaxInputReportLength() < 8) continue;
                    if (!dev.TryOpen(out HidStream stream)) continue;
                    using (stream)
                    {
                        stream.ReadTimeout = 250;
                        byte[] req = new byte[Math.Max(dev.GetMaxOutputReportLength(), 8)];
                        req[0] = 0x06;
                        req[1] = 0xFF;
                        req[2] = 0xBB;
                        req[3] = 0x02;
                        stream.Write(req);

                        byte[] resp = new byte[Math.Max(dev.GetMaxInputReportLength(), 16)];
                        int n = stream.Read(resp, 0, resp.Length);
                        if (n >= 8 && resp[7] <= 100 && resp[7] > 0)
                        {
                            seen.Add(key);
                            result.Add(MakeInfo(dev, key, resp[7]));
                        }
                    }
                }
            }
            catch { }
        }

        return result;
    }

    private DeviceBatteryInfo MakeInfo(HidDevice dev, string key, int pct)
    {
        string name = $"HyperX Cloud ({key})";
        try { name = dev.GetProductName()?.Trim() ?? name; } catch { }

        return new DeviceBatteryInfo
        {
            Id = $"HYPERX:{key}",
            Name = name,
            Kind = DeviceKind.Headset,
            ProtocolName = ProviderName,
            ConnectionType = "2.4 ГГц (HyperX)",
            VidPid = key,
            BatteryPercent = pct,
            IsConnected = true,
            StatusNote = "От батареи"
        };
    }

    private static bool HasUsagePage(HidDevice dev, int page)
    {
        try
        {
            return dev.GetReportDescriptor().DeviceItems
                .SelectMany(x => x.Usages.GetAllValues())
                .Any(u => (u >> 16) == page);
        }
        catch { return false; }
    }
}
