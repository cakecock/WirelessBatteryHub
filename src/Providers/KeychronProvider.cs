using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class KeychronProvider : IBatteryProvider
{
    public string ProviderName => "Keychron 2.4G";
    private const int KeychronVid = 0x3434;

    public bool CanHandle(string vidPidKey) =>
        vidPidKey.StartsWith("3434:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();
        var seenPid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (HidDevice dev in DeviceList.Local.GetHidDevices(vendorID: KeychronVid))
        {
            string key = $"{dev.VendorID:X4}:{dev.ProductID:X4}";
            if (targetVidPid != null && !key.Equals(targetVidPid, StringComparison.OrdinalIgnoreCase)) continue;
            if (seenPid.Contains(key)) continue;

            int featLen;
            int inLen;
            try
            {
                featLen = dev.GetMaxFeatureReportLength();
                inLen = dev.GetMaxInputReportLength();
            }
            catch { continue; }

            if (featLen < 64 || inLen < 64) continue;

            try
            {
                if (!dev.TryOpen(out HidStream stream)) continue;
                using (stream)
                {
                    stream.ReadTimeout = 220;
                    int offset = featLen >= 65 ? 1 : 0;
                    byte[] req = new byte[featLen];
                    req[offset + 0] = 0xB3;
                    req[offset + 1] = 0x06;
                    stream.SetFeature(req);

                    byte[] resp = new byte[inLen];
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        int n = stream.Read(resp, 0, resp.Length);
                        int rOff = (n >= 65 && resp[0] == 0x00) ? 1 : 0;
                        if (n >= rOff + 21 && resp[rOff] == 0xB4 && resp[rOff + 1] == 0x06)
                        {
                            int pct = resp[rOff + 20];
                            if (pct <= 100)
                            {
                                seenPid.Add(key);
                                string name = SafeProductName(dev, key);
                                result.Add(new DeviceBatteryInfo
                                {
                                    Id = $"KEYCHRON:{key}",
                                    Name = name,
                                    Kind = DeviceClassifier.GuessFromHidSibling(dev.VendorID, dev.ProductID, name),
                                    ProtocolName = ProviderName,
                                    ConnectionType = "2.4 ГГц (Keychron)",
                                    VidPid = key,
                                    BatteryPercent = pct,
                                    IsConnected = true,
                                    StatusNote = "От батареи"
                                });
                                break;
                            }
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
        try { return dev.GetProductName()?.Trim() ?? $"Keychron ({key})"; }
        catch { return $"Keychron ({key})"; }
    }
}
