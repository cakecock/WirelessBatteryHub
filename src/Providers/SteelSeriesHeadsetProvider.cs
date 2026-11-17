using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class SteelSeriesHeadsetProvider : IBatteryProvider
{
    public string ProviderName => "SteelSeries Headset";
    private const int SteelSeriesVid = 0x1038;

    private static readonly HashSet<int> ArctisLegacyPids = new() { 0x1260, 0x12AD, 0x1252 };
    private static readonly HashSet<int> ArctisNovaPids = new()
    {
        0x2202, 0x2206, 0x22A4, 0x223A, 0x227A, 0x22A1, 0x22A5, 0x22A7, 0x2253
    };

    public bool CanHandle(string vidPidKey)
    {
        if (!vidPidKey.StartsWith("1038:", StringComparison.OrdinalIgnoreCase)) return false;
        int pid = Convert.ToInt32(vidPidKey.Split(':')[1], 16);
        return ArctisLegacyPids.Contains(pid) || ArctisNovaPids.Contains(pid);
    }

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();
        var seenPid = new HashSet<int>();

        foreach (HidDevice dev in DeviceList.Local.GetHidDevices(vendorID: SteelSeriesVid))
        {
            string key = $"{dev.VendorID:X4}:{dev.ProductID:X4}";
            if (!CanHandle(key)) continue;
            if (targetVidPid != null && !key.Equals(targetVidPid, StringComparison.OrdinalIgnoreCase)) continue;
            if (seenPid.Contains(dev.ProductID) || !IsVendorInterface(dev)) continue;

            try
            {
                if (!dev.TryOpen(out HidStream stream)) continue;
                using (stream)
                {
                    stream.ReadTimeout = 250;
                    stream.WriteTimeout = 250;

                    int outLen = Math.Max(dev.GetMaxOutputReportLength(), 2);
                    int inLen = Math.Max(dev.GetMaxInputReportLength(), 64);
                    byte[] req = new byte[outLen];
                    byte[] resp = new byte[inLen];

                    if (ArctisLegacyPids.Contains(dev.ProductID))
                    {
                        req[0] = 0x06;
                        req[1] = 0x18;
                        stream.Write(req);

                        int n = stream.Read(resp, 0, resp.Length);
                        if (n >= 3 && resp[0] == 0x06 && resp[1] == 0x18 && resp[2] <= 100)
                        {
                            seenPid.Add(dev.ProductID);
                            result.Add(MakeInfo(dev, key, resp[2], false));
                        }
                    }
                    else
                    {
                        req[0] = 0x00;
                        req[1] = 0xB0;
                        stream.Write(req);

                        int n = stream.Read(resp, 0, resp.Length);
                        int rOff = (n >= 5 && resp[0] == 0x00 && resp[1] == 0xB0) ? 1 : 0;
                        if (n >= rOff + 4 && resp[rOff] == 0xB0)
                        {
                            // 0x03 — гарнитура подключена; если выключена — пропускаем
                            if (resp[rOff + 1] != 0x03) continue;

                            int pct;
                            if (dev.ProductID == 0x2253) // Nova 5X
                                pct = Math.Clamp((int)resp[rOff + 3], 0, 100);
                            else
                                pct = resp[rOff + 2] <= 4 ? resp[rOff + 2] * 25 : Math.Clamp((int)resp[rOff + 2], 0, 100);

                            seenPid.Add(dev.ProductID);
                            result.Add(MakeInfo(dev, key, pct, false));
                        }
                    }
                }
            }
            catch { }
        }

        return result;
    }

    private DeviceBatteryInfo MakeInfo(HidDevice dev, string key, int pct, bool charging)
    {
        string name = $"SteelSeries Arctis ({key})";
        try { name = dev.GetProductName()?.Trim() ?? name; } catch { }

        return new DeviceBatteryInfo
        {
            Id = $"SSHS:{key}",
            Name = name,
            Kind = DeviceKind.Headset,
            ProtocolName = ProviderName,
            ConnectionType = "2.4 ГГц (SteelSeries)",
            VidPid = key,
            BatteryPercent = pct,
            IsConnected = true,
            IsCharging = charging,
            StatusNote = charging ? "Заряжается" : "От батареи"
        };
    }

    private static bool IsVendorInterface(HidDevice dev)
    {
        try
        {
            if (dev.GetMaxOutputReportLength() < 2 || dev.GetMaxInputReportLength() < 4) return false;
            return dev.GetReportDescriptor().DeviceItems
                .SelectMany(x => x.Usages.GetAllValues())
                .Any(u => (u >> 16) > 1);
        }
        catch { return false; }
    }
}
