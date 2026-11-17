using System.Collections.Concurrent;
using HidSharp;
using WirelessBatteryHub.Models;
using WirelessBatteryHub.Providers;

namespace WirelessBatteryHub.Services;

public sealed class PassiveHidManager : IDisposable
{
    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _latest = new();
    private readonly ConcurrentDictionary<string, byte> _openedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();

    public IReadOnlyList<DeviceBatteryInfo> GetSnapshot() => _latest.Values.ToList();

    public void SyncListeners()
    {
        List<CustomProtocolRule> passiveRules = CustomJsonProvider.LoadRules()
            .Where(r => r.Mode.Equals("PassiveInput", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var presentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (HidDevice dev in DeviceList.Local.GetHidDevices())
        {
            presentPaths.Add(dev.DevicePath);
            if (_openedPaths.ContainsKey(dev.DevicePath)) continue;

            string vidHex = $"{dev.VendorID:X4}";
            string pidHex = $"{dev.ProductID:X4}";
            string vidPid = $"{vidHex}:{pidHex}";

            CustomProtocolRule? rule = passiveRules.FirstOrDefault(r =>
                CustomJsonProvider.Matches(r, vidHex, pidHex) &&
                CustomJsonProvider.MatchesUsagePage(dev, r.UsagePageHex));

            if (rule == null) continue;

            int inLen;
            try { inLen = dev.GetMaxInputReportLength(); } catch { continue; }
            if (inLen <= rule.BatteryByteIndex) continue;

            if (_openedPaths.TryAdd(dev.DevicePath, 1))
            {
                _ = Task.Run(() => ListenLoop(dev, vidPid, rule, _cts.Token));
            }
        }

        // Удаляем отключённые донглы из списка
        foreach (string trackedId in _latest.Keys.ToList())
        {
            if (_latest.TryGetValue(trackedId, out var info) && info.VidPid != null)
            {
                bool stillPresent = DeviceList.Local.GetHidDevices()
                    .Any(d => $"{d.VendorID:X4}:{d.ProductID:X4}".Equals(info.VidPid, StringComparison.OrdinalIgnoreCase));
                if (!stillPresent) _latest.TryRemove(trackedId, out _);
            }
        }
    }

    private void ListenLoop(HidDevice dev, string vidPid, CustomProtocolRule rule, CancellationToken ct)
    {
        byte[] prefix = CustomJsonProvider.ParseHex(rule.ResponsePrefixHex);
        Enum.TryParse(rule.Kind, true, out DeviceKind kind);
        string name = SafeName(dev, rule.Name, vidPid);
        string deviceKey = $"PASSIVE:{vidPid}";

        _latest.TryAdd(deviceKey, new DeviceBatteryInfo
        {
            Id = deviceKey,
            Name = name,
            Kind = kind,
            ProtocolName = "Passive HID",
            ConnectionType = "2.4 ГГц (Ожидание)",
            VidPid = vidPid,
            BatteryPercent = null,
            IsConnected = true,
            StatusNote = "Пошевелите устройство"
        });

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!dev.TryOpen(out HidStream stream))
                {
                    // Если устройство отключено — выходим
                    if (!DeviceList.Local.GetHidDevices().Any(d => d.DevicePath.Equals(dev.DevicePath, StringComparison.OrdinalIgnoreCase)))
                        break;
                    Thread.Sleep(1500);
                    continue;
                }

                using (stream)
                {
                    stream.ReadTimeout = 500;
                    byte[] buf = new byte[Math.Max(dev.GetMaxInputReportLength(), 64)];

                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            int n = stream.Read(buf, 0, buf.Length);
                            if (n <= rule.BatteryByteIndex) continue;
                            if (!CustomJsonProvider.StartsWithPrefix(buf, n, prefix)) continue;

                            int raw = buf[rule.BatteryByteIndex];
                            int pct = rule.MaxScale > 100
                                ? Math.Clamp((int)Math.Round(raw * 100.0 / rule.MaxScale), 0, 100)
                                : Math.Clamp(raw, 0, 100);

                            bool chg = rule.ChargingByteIndex >= 0 && n > rule.ChargingByteIndex && buf[rule.ChargingByteIndex] != 0;

                            _latest[deviceKey] = new DeviceBatteryInfo
                            {
                                Id = deviceKey,
                                Name = name,
                                Kind = kind,
                                ProtocolName = "Passive HID",
                                ConnectionType = "2.4 ГГц (Passive)",
                                VidPid = vidPid,
                                BatteryPercent = pct,
                                IsConnected = true,
                                IsCharging = chg,
                                StatusNote = chg ? "Заряжается" : "От батареи"
                            };
                        }
                        catch (TimeoutException) { }
                    }
                }
            }
        }
        catch { }
        finally
        {
            _openedPaths.TryRemove(dev.DevicePath, out _);
            _latest.TryRemove(deviceKey, out _);
        }
    }

    private static string SafeName(HidDevice dev, string ruleName, string vidPid)
    {
        try
        {
            string? p = dev.GetProductName();
            if (!string.IsNullOrWhiteSpace(p)) return p.Trim();
        }
        catch { }
        return string.IsNullOrWhiteSpace(ruleName) ? $"Wireless Device ({vidPid})" : ruleName;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
