using System.Text;
using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class LogitechHidppProvider : IBatteryProvider
{
    public string ProviderName => "Logitech HID++";
    private const int LogitechVid = 0x046D;
    private const byte SwId = 0x0A;

    public bool CanHandle(string vidPidKey) =>
        vidPidKey.StartsWith("046D:", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (HidDevice dev in DeviceList.Local.GetHidDevices(vendorID: LogitechVid))
        {
            string key = $"{dev.VendorID:X4}:{dev.ProductID:X4}";
            if (targetVidPid != null && !key.Equals(targetVidPid, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsHidppCollection(dev) || !seenPaths.Add(dev.DevicePath))
                continue;

            try
            {
                if (!dev.TryOpen(out HidStream stream)) continue;
                using (stream)
                {
                    stream.ReadTimeout = 250;
                    stream.WriteTimeout = 250;

                    string fallbackName = SafeProductName(dev);
                    bool isReceiver = fallbackName.Contains("Receiver", StringComparison.OrdinalIgnoreCase)
                                   || dev.ProductID is 0xC52B or 0xC534 or 0xC539 or 0xC547 or 0xC548;

                    byte[] slots = isReceiver ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 0xFF, 1 };

                    foreach (byte slot in slots)
                    {
                        DeviceBatteryInfo? info = QuerySlot(stream, dev, key, slot, fallbackName);
                        if (info != null)
                        {
                            result.Add(info);
                            if (!isReceiver) break;
                        }
                    }
                }
            }
            catch { }
        }

        return result;
    }

    private DeviceBatteryInfo? QuerySlot(HidStream stream, HidDevice dev, string vidPid, byte slot, string fallbackName)
    {
        int? pct = null;
        bool charging = false;
        string note = "От батареи";

        // 1) Unified Battery (0x1004)
        byte? f1004 = GetFeatureIndex(stream, slot, 0x1004);
        if (f1004.HasValue)
        {
            byte[]? r = SendShort(stream, slot, f1004.Value, 0x10, 0, 0, 0);
            if (r != null && r.Length >= 7)
            {
                pct = Math.Clamp((int)r[4], 0, 100);
                charging = r[6] is 1 or 2 or 3 or 4;
                if (charging) note = r[6] == 3 ? "Заряжена" : "Заряжается";
            }
        }

        // 2) Legacy Battery Level Status (0x1000)
        if (!pct.HasValue)
        {
            byte? f1000 = GetFeatureIndex(stream, slot, 0x1000);
            if (f1000.HasValue)
            {
                byte[]? r = SendShort(stream, slot, f1000.Value, 0x00, 0, 0, 0);
                if (r != null && r.Length >= 7)
                {
                    pct = Math.Clamp((int)r[4], 0, 100);
                    charging = r[6] is 1 or 2 or 3 or 4;
                    if (charging) note = "Заряжается";
                }
            }
        }

        // 3) Battery Voltage (0x1001) — напр. G502 Lightspeed / часть клавиатур
        if (!pct.HasValue)
        {
            byte? f1001 = GetFeatureIndex(stream, slot, 0x1001);
            if (f1001.HasValue)
            {
                byte[]? r = SendShort(stream, slot, f1001.Value, 0x00, 0, 0, 0);
                if (r != null && r.Length >= 7)
                {
                    int mv = (r[4] << 8) | r[5];
                    pct = VoltageToPercent(mv);
                    charging = (r[6] & 0x80) != 0;
                    if (charging) note = "Заряжается";
                }
            }
        }

        if (!pct.HasValue) return null;

        string name = ReadDeviceName(stream, slot) ?? fallbackName;
        DeviceKind kind = ReadDeviceKind(stream, slot) ?? DeviceClassifier.GuessByName(name, DeviceKind.Mouse);

        return new DeviceBatteryInfo
        {
            Id = $"LOGI:{dev.ProductID:X4}:{slot}",
            Name = name,
            Kind = kind,
            ProtocolName = ProviderName,
            ConnectionType = "2.4 ГГц (Logitech)",
            VidPid = vidPid,
            BatteryPercent = pct.Value,
            IsConnected = true,
            IsCharging = charging,
            StatusNote = note
        };
    }

    private static DeviceKind? ReadDeviceKind(HidStream stream, byte slot)
    {
        byte? f0005 = GetFeatureIndex(stream, slot, 0x0005);
        if (!f0005.HasValue) return null;
        byte[]? r = SendShort(stream, slot, f0005.Value, 0x20, 0, 0, 0);
        if (r == null || r.Length < 5) return null;

        // HID++ DeviceType: 0=Keyboard, 3=Mouse
        return r[4] switch
        {
            0 => DeviceKind.Keyboard,
            3 => DeviceKind.Mouse,
            8 => DeviceKind.Headset,
            _ => null
        };
    }

    private static byte? GetFeatureIndex(HidStream stream, byte slot, ushort featureId)
    {
        byte[]? r = SendShort(stream, slot, 0x00, 0x00, (byte)(featureId >> 8), (byte)(featureId & 0xFF), 0x00);
        return (r != null && r.Length >= 5 && r[4] != 0) ? r[4] : null;
    }

    private static string? ReadDeviceName(HidStream stream, byte slot)
    {
        byte? idx = GetFeatureIndex(stream, slot, 0x0005);
        if (!idx.HasValue) return null;

        byte[]? countResp = SendShort(stream, slot, idx.Value, 0x00, 0, 0, 0);
        if (countResp == null || countResp.Length < 5) return null;

        int totalLen = countResp[4];
        if (totalLen <= 0 || totalLen > 64) return null;

        var bytes = new List<byte>(totalLen);
        for (int offset = 0; offset < totalLen; offset += 16)
        {
            byte[]? chunk = SendShort(stream, slot, idx.Value, 0x10, (byte)offset, 0, 0);
            if (chunk == null || chunk.Length < 5) break;
            int take = Math.Min(16, Math.Min(totalLen - offset, chunk.Length - 4));
            for (int i = 0; i < take; i++) bytes.Add(chunk[4 + i]);
        }

        string name = Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static byte[]? SendShort(HidStream stream, byte slot, byte featureIdx, byte func, byte p0, byte p1, byte p2)
    {
        byte[] req = { 0x10, slot, featureIdx, (byte)((func & 0xF0) | SwId), p0, p1, p2 };
        try { stream.Write(req); } catch { return null; }

        byte[] buf = new byte[20];
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(280);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n < 5 || (buf[0] != 0x10 && buf[0] != 0x11) || buf[1] != slot) continue;
                if (buf[2] == 0xFF || buf[2] == 0x8F) return null;
                if (buf[2] == featureIdx && (buf[3] & 0x0F) == SwId) return buf;
            }
            catch { break; }
        }
        return null;
    }

    private static int VoltageToPercent(int mv)
    {
        // Кривая Li-ion (используется в Solaar)
        ReadOnlySpan<(int mv, int pct)> table = stackalloc[]
        {
            (4186, 100), (4100, 90), (4020, 80), (3950, 70), (3880, 60),
            (3820, 50), (3770, 40), (3730, 30), (3680, 20), (3620, 10), (3500, 0)
        };
        if (mv >= table[0].mv) return 100;
        if (mv <= table[^1].mv) return 0;
        for (int i = 0; i < table.Length - 1; i++)
        {
            if (mv >= table[i + 1].mv)
            {
                double t = (double)(mv - table[i + 1].mv) / (table[i].mv - table[i + 1].mv);
                return (int)Math.Round(table[i + 1].pct + t * (table[i].pct - table[i + 1].pct));
            }
        }
        return 0;
    }

    private static bool IsHidppCollection(HidDevice dev)
    {
        try
        {
            if (dev.GetMaxOutputReportLength() < 7 || dev.GetMaxInputReportLength() < 7) return false;
            return dev.GetReportDescriptor().DeviceItems
                .SelectMany(x => x.Usages.GetAllValues())
                .Any(u => (u >> 16) == 0xFF00);
        }
        catch { return false; }
    }

    private static string SafeProductName(HidDevice dev)
    {
        try { return dev.GetProductName()?.Trim() ?? $"Logitech ({dev.ProductID:X4})"; }
        catch { return $"Logitech ({dev.ProductID:X4})"; }
    }
}
