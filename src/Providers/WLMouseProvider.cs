using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class WLMouseProvider : IBatteryProvider
{
    public string ProviderName => "WLMouse / G-Wolves";

    public bool CanHandle(string vidPidKey) =>
        vidPidKey.StartsWith("36A7:", StringComparison.OrdinalIgnoreCase) ||
        vidPidKey.StartsWith("33E4:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();
        var seenPid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (HidDevice dev in DeviceList.Local.GetHidDevices())
        {
            string key = $"{dev.VendorID:X4}:{dev.ProductID:X4}";
            if (!CanHandle(key)) continue;
            if (targetVidPid != null && !key.Equals(targetVidPid, StringComparison.OrdinalIgnoreCase)) continue;
            if (seenPid.Contains(key)) continue;

            int featLen;
            try { featLen = dev.GetMaxFeatureReportLength(); } catch { continue; }
            if (featLen < 64 || featLen > 66) continue;

            try
            {
                if (!dev.TryOpen(out HidStream stream)) continue;
                using (stream)
                {
                    int offset = featLen >= 65 ? 1 : 0;
                    byte[] req = new byte[featLen];
                    req[offset + 2] = 0x02;
                    req[offset + 3] = 0x02;
                    req[offset + 5] = 0x83;

                    stream.SetFeature(req);
                    Thread.Sleep(120);

                    byte[] resp = new byte[featLen];
                    stream.GetFeature(resp);

                    // Ответ: 0xA1 ... 0x83 <charging> <percent>
                    int idx83 = Array.IndexOf(resp, (byte)0x83, offset);
                    if (resp[offset] == 0xA1 && idx83 >= 0 && idx83 + 2 < resp.Length)
                    {
                        bool charging = resp[idx83 + 1] != 0;
                        int pct = resp[idx83 + 2];
                        if (pct <= 100)
                        {
                            seenPid.Add(key);
                            string name = SafeProductName(dev, key);
                            result.Add(new DeviceBatteryInfo
                            {
                                Id = $"WLM:{key}",
                                Name = name,
                                Kind = DeviceKind.Mouse,
                                ProtocolName = ProviderName,
                                ConnectionType = "2.4 ГГц (WLMouse/G-Wolves)",
                                VidPid = key,
                                BatteryPercent = pct,
                                IsConnected = true,
                                IsCharging = charging,
                                StatusNote = charging ? "Заряжается" : "От батареи"
                            });
                        }
                    }
                }
            }
            catch { }
        }

        return result;
    }

    private static string SafeProductName(HidDevice dev, string key)
    {
        try { return dev.GetProductName()?.Trim() ?? $"WLMouse ({key})"; }
        catch { return $"WLMouse ({key})"; }
    }
}
