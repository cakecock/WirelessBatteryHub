using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class RazerHidProvider : IBatteryProvider
{
    public string ProviderName => "Razer HID";
    private const int RazerVid = 0x1532;
    private static readonly byte[] TransactionIds = { 0x1F, 0x3F };

    public bool CanHandle(string vidPidKey) =>
        vidPidKey.StartsWith("1532:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();
        var seenPid = new HashSet<int>();

        foreach (HidDevice dev in DeviceList.Local.GetHidDevices(vendorID: RazerVid))
        {
            string key = $"{dev.VendorID:X4}:{dev.ProductID:X4}";
            if (targetVidPid != null && !key.Equals(targetVidPid, StringComparison.OrdinalIgnoreCase))
                continue;

            int featLen;
            try { featLen = dev.GetMaxFeatureReportLength(); } catch { continue; }
            if (featLen < 90 || featLen > 92 || seenPid.Contains(dev.ProductID)) continue;

            try
            {
                if (!dev.TryOpen(out HidStream stream)) continue;
                using (stream)
                {
                    int offset = featLen >= 91 ? 1 : 0;
                    foreach (byte txId in TransactionIds)
                    {
                        byte[]? batResp = QueryWithRetry(stream, featLen, offset, txId, 0x07, 0x80);
                        if (batResp == null) continue;

                        int percent = Math.Clamp((int)Math.Round(batResp[offset + 9] * 100.0 / 255.0), 0, 100);
                        bool charging = false;
                        byte[]? chgResp = QueryWithRetry(stream, featLen, offset, txId, 0x07, 0x84);
                        if (chgResp != null) charging = chgResp[offset + 9] != 0;

                        seenPid.Add(dev.ProductID);
                        string name = SafeProductName(dev);

                        result.Add(new DeviceBatteryInfo
                        {
                            Id = $"RAZER:{dev.ProductID:X4}",
                            Name = name,
                            Kind = DeviceClassifier.GuessFromHidSibling(dev.VendorID, dev.ProductID, name),
                            ProtocolName = ProviderName,
                            ConnectionType = "2.4 ГГц (Razer)",
                            VidPid = key,
                            BatteryPercent = percent,
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

    private static byte[]? QueryWithRetry(HidStream stream, int bufLen, int offset, byte txId, byte cmdClass, byte cmdId)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte[] req = new byte[bufLen];
            req[offset + 1] = txId;
            req[offset + 5] = 0x02;
            req[offset + 6] = cmdClass;
            req[offset + 7] = cmdId;

            byte crc = 0;
            for (int i = 2; i < 88; i++) crc ^= req[offset + i];
            req[offset + 88] = crc;

            stream.SetFeature(req);
            Thread.Sleep(60);

            byte[] resp = new byte[bufLen];
            stream.GetFeature(resp);

            if (resp[offset + 0] == 0x02 && resp[offset + 6] == cmdClass && resp[offset + 7] == cmdId)
                return resp;

            // Если 0x01 (занято) или ответ на чужую команду Synapse — повторяем чтение/запрос
            if (resp[offset + 0] == 0x04 || resp[offset + 0] == 0x05)
                return null;
        }
        return null;
    }

    private static string SafeProductName(HidDevice dev)
    {
        try { return dev.GetProductName()?.Trim() ?? $"Razer ({dev.ProductID:X4})"; }
        catch { return $"Razer ({dev.ProductID:X4})"; }
    }
}
