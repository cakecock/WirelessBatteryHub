using System.Collections.Concurrent;
using HidSharp;
using WirelessBatteryHub.Models;
using WirelessBatteryHub.Providers;

namespace WirelessBatteryHub.Services;

public sealed class PassiveHidManager : IDisposable
{
    private readonly ConcurrentDictionary<string, DeviceBatteryInfo> _latest = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = new();
    private readonly HashSet<string> _openedPaths = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DeviceBatteryInfo> GetSnapshot() => _latest.Values.ToList();

    public void EnsureListenersStarted()
    {
        List<CustomProtocolRule> passiveRules = CustomJsonProvider.LoadRules()
            .Where(r => r.Mode.Equals("PassiveInput", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (HidDevice dev in DeviceList.Local.GetHidDevices())
        {
            if (_openedPaths.Contains(dev.DevicePath)) continue;

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

            _openedPaths.Add(dev.DevicePath);
            _workers.Add(Task.Run(() => ListenLoop(dev, vidPid, rule, _cts.Token)));
        }
    }

    private void ListenLoop(HidDevice dev, string vidPid, CustomProtocolRule rule, CancellationToken ct)
    {
        byte[] prefix = CustomJsonProvider.ParseHex(rule.ResponsePrefixHex);
        Enum.TryParse(rule.Kind, true, out DeviceKind kind);
        string name = SafeName(dev, rule.Name, vidPid);

        // Регистрируем устройство в статусе ожидания движения/активности
        _latest.TryAdd($"PASSIVE:{vidPid}", new DeviceBatteryInfo
        {
            Id = $"PASSIVE:{vidPid}",
            Name = name,
            Kind = kind,
            ProtocolName = "Passive HID",
            ConnectionType = "2.4 ГГц (Ожидание пакета)",
            VidPid = vidPid,
            BatteryPercent = null,
            IsConnected = true,
            StatusNote = "Пошевелите устройство"
        });

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!dev.TryOpen(out HidStream stream))
                {
                    Thread.Sleep(2000);
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

                            _latest[$"PASSIVE:{vidPid}"] = new DeviceBatteryInfo
                            {
                                Id = $"PASSIVE:{vidPid}",
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
            catch
            {
                Thread.Sleep(1500);
            }
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
        try { Task.WaitAll(_workers.ToArray(), 500); } catch { }
        _cts.Dispose();
    }
}
