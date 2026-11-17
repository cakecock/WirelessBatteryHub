using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class SteelSeriesMouseProvider : IBatteryProvider
{
    public string ProviderName => "SteelSeries Mouse";
    private const int SteelSeriesVid = 0x1038;

    private static readonly HashSet<int> AeroxLegacyPids = new() { 0x1838, 0x1842, 0x1848, 0x184E };
    private static readonly HashSet<int> AeroxGen2Pids = new() { 0x1852, 0x185C, 0x1860, 0x1858, 0x1874, 0x1878 };
    private static readonly HashSet<int> Rival3Pids = new() { 0x1830 };

    public bool CanHandle(string vidPidKey)
    {
        if (!vidPidKey.StartsWith("1038:", StringComparison.OrdinalIgnoreCase)) return false;
        int pid = Convert.ToInt32(vidPidKey.Split(':')[1], 16);
        return AeroxLegacyPids.Contains(pid) || AeroxGen2Pids.Contains(pid) || Rival3Pids.Contains(pid);
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
            if (seenPid.Contains(dev.ProductID) || !IsVendorCollection(dev)) continue;

            try
            {
                if (!dev.TryOpen(out HidStream stream)) continue;
                using (stream)
                {
                    stream.ReadTimeout = 220;
                    stream.WriteTimeout = 220;

                    int outLen = Math.Max(dev.GetMaxOutputReportLength(), 64);
                    int inLen = Math.Max(dev.GetMaxInputReportLength(), 64);
                    byte[] req = new byte[outLen];
                    byte[] resp = new byte[inLen];

                    if (Rival3Pids.Contains(dev.ProductID))
                    {
                        req[0] = 0x00;
                        req[1] = 0xAA;
                        req[2] = 0x01;
                        stream.Write(req);

                        int n = stream.Read(resp, 0, resp.Length);
                        int rOff = (n >= 4 && resp[0] == 0x00) ? 1 : 0;

                        if (n >= rOff + 4 && resp[rOff] == 0xAA && resp[rOff + 1] <= 100)
                        {
                            seenPid.Add(dev.ProductID);
                            result.Add(MakeInfo(dev, key, resp[rOff + 1], resp[rOff + 3] != 0));
                        }
                        else if (n >= rOff + 3 && resp[rOff] <= 100 && resp[rOff + 2] is 0 or 1)
                        {
                            seenPid.Add(dev.ProductID);
                            result.Add(MakeInfo(dev, key, resp[rOff], resp[rOff + 2] == 1));
                        }
                    }
                    else if (AeroxLegacyPids.Contains(dev.ProductID))
                    {
                        req[0] = 0x00;
                        req[1] = 0xD2;
                        stream.Write(req);

                        int n = stream.Read(resp, 0, resp.Length);
                        int rOff = (n >= 3 && resp[0] == 0x00) ? 1 : 0;
                        if (n >= rOff + 2 && resp[rOff] == 0xD2)
                        {
                            byte raw = resp[rOff + 1];
                            bool chg = (raw & 0x80) != 0;
                            int val = raw & 0x7F;
                            if (val == 0) continue; // Мышь спит

                            int pct = val <= 21 ? Math.Clamp((val - 1) * 5, 0, 100) : Math.Clamp(val, 0, 100);
                            seenPid.Add(dev.ProductID);
                            result.Add(MakeInfo(dev, key, pct, chg));
                        }
                    }
                    else // Aerox 5 / 9 / Gen 2
                    {
                        req[0] = 0x00;
                        req[1] = 0x92;
                        stream.Write(req);

                        int n = stream.Read(resp, 0, resp.Length);
                        int rOff = (n >= 4 && resp[0] == 0x00) ? 1 : 0;
                        if (n >= rOff + 3 && resp[rOff] is 0x92 or 0xD2)
                        {
                            // 40 FF = спит
                            if (resp[rOff + 1] == 0x40 && resp[rOff + 2] == 0xFF) continue;

                            if (resp[rOff + 1] is 0 or 1 or 2 && resp[rOff + 2] <= 100)
                            {
                                bool chg = resp[rOff + 1] is 1 or 2;
                                seenPid.Add(dev.ProductID);
                                result.Add(MakeInfo(dev, key, resp[rOff + 2], chg));
                            }
                            else
                            {
                                byte raw = resp[rOff + 1];
                                bool chg = (raw & 0x80) != 0;
                                int val = raw & 0x7F;
                                if (val == 0) continue;
                                int pct = val <= 21 ? Math.Clamp((val - 1) * 5, 0, 100) : Math.Clamp(val, 0, 100);
                                seenPid.Add(dev.ProductID);
                                result.Add(MakeInfo(dev, key, pct, chg));
                            }
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
        string name = $"SteelSeries Mouse ({key})";
        try { name = dev.GetProductName()?.Trim() ?? name; } catch { }

        return new DeviceBatteryInfo
        {
            Id = $"SSMOUSE:{key}",
            Name = name,
            Kind = DeviceKind.Mouse,
            ProtocolName = ProviderName,
            ConnectionType = "2.4 ГГц (SteelSeries)",
            VidPid = key,
            BatteryPercent = pct,
            IsConnected = true,
            IsCharging = charging,
            StatusNote = charging ? "Заряжается" : "От батареи"
        };
    }

    private static bool IsVendorCollection(HidDevice dev)
    {
        try
        {
            if (dev.GetMaxOutputReportLength() < 2 || dev.GetMaxInputReportLength() < 3) return false;
            return dev.GetReportDescriptor().DeviceItems
                .SelectMany(x => x.Usages.GetAllValues())
                .Any(u => (u >> 16) >= 0xFF00);
        }
        catch { return false; }
    }
}
