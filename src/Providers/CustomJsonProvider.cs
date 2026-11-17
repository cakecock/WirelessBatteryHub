using System.Text.Json;
using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class CustomJsonProvider : IBatteryProvider
{
    public string ProviderName => "Custom JSON (protocols.json)";

    private static readonly string RulesPath =
        Path.Combine(AppContext.BaseDirectory, "protocols.json");

    public static List<CustomProtocolRule> LoadRules()
    {
        try
        {
            if (!File.Exists(RulesPath))
            {
                var defaults = new List<CustomProtocolRule>
                {
                    new()
                    {
                        Name = "Beken OEM / Attack Shark X11 (Пример пассивного правила)",
                        Vid = "1D57",
                        Pid = "*",
                        Kind = "Mouse",
                        Mode = "PassiveInput",
                        ResponsePrefixHex = "03 55 40 01",
                        BatteryByteIndex = 4,
                        ChargingByteIndex = -1,
                        MaxScale = 100
                    }
                };
                SaveRules(defaults);
                return defaults;
            }

            string json = File.ReadAllText(RulesPath);
            return JsonSerializer.Deserialize<List<CustomProtocolRule>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static void SaveRules(List<CustomProtocolRule> rules)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(RulesPath, JsonSerializer.Serialize(rules, opts));
    }

    public static void AddOrUpdateRule(CustomProtocolRule rule)
    {
        List<CustomProtocolRule> rules = LoadRules();
        rules.RemoveAll(r =>
            r.Vid.Equals(rule.Vid, StringComparison.OrdinalIgnoreCase) &&
            r.Pid.Equals(rule.Pid, StringComparison.OrdinalIgnoreCase));
        rules.Add(rule);
        SaveRules(rules);
    }

    public bool CanHandle(string vidPidKey)
    {
        var parts = vidPidKey.Split(':');
        if (parts.Length != 2) return false;
        return LoadRules().Any(r => Matches(r, parts[0], parts[1]));
    }

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();
        List<CustomProtocolRule> activeRules = LoadRules()
            .Where(r => r.Mode is "OutputQuery" or "FeatureQuery")
            .ToList();

        if (activeRules.Count == 0) return result;

        foreach (CustomProtocolRule rule in activeRules)
        {
            byte[] req = ParseHex(rule.RequestHex);
            byte[] prefix = ParseHex(rule.ResponsePrefixHex);
            if (req.Length == 0) continue;

            foreach (HidDevice dev in DeviceList.Local.GetHidDevices())
            {
                string key = $"{dev.VendorID:X4}:{dev.ProductID:X4}";
                if (targetVidPid != null && !key.Equals(targetVidPid, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Matches(rule, $"{dev.VendorID:X4}", $"{dev.ProductID:X4}")) continue;
                if (!MatchesUsagePage(dev, rule.UsagePageHex)) continue;

                try
                {
                    if (!dev.TryOpen(out HidStream stream)) continue;
                    using (stream)
                    {
                        stream.ReadTimeout = 250;
                        byte[] resp;
                        int n;

                        if (rule.Mode == "FeatureQuery")
                        {
                            int fl = dev.GetMaxFeatureReportLength();
                            if (fl < req.Length) continue;
                            byte[] fReq = new byte[fl];
                            Array.Copy(req, fReq, req.Length);
                            stream.SetFeature(fReq);
                            Thread.Sleep(80);
                            resp = new byte[fl];
                            stream.GetFeature(resp);
                            n = resp.Length;
                        }
                        else
                        {
                            if (dev.GetMaxOutputReportLength() < req.Length) continue;
                            stream.Write(req);
                            resp = new byte[Math.Max(dev.GetMaxInputReportLength(), 64)];
                            n = stream.Read(resp, 0, resp.Length);
                        }

                        if (n <= rule.BatteryByteIndex) continue;
                        if (!StartsWithPrefix(resp, n, prefix)) continue;

                        int raw = resp[rule.BatteryByteIndex];
                        int pct = rule.MaxScale > 100
                            ? Math.Clamp((int)Math.Round(raw * 100.0 / rule.MaxScale), 0, 100)
                            : Math.Clamp(raw, 0, 100);

                        bool chg = rule.ChargingByteIndex >= 0 && n > rule.ChargingByteIndex && resp[rule.ChargingByteIndex] != 0;
                        Enum.TryParse(rule.Kind, true, out DeviceKind kind);

                        result.Add(new DeviceBatteryInfo
                        {
                            Id = $"JSON:{key}",
                            Name = string.IsNullOrWhiteSpace(rule.Name) ? $"Device ({key})" : rule.Name,
                            Kind = kind,
                            ProtocolName = ProviderName,
                            ConnectionType = "2.4 ГГц (Custom JSON)",
                            VidPid = key,
                            BatteryPercent = pct,
                            IsConnected = true,
                            IsCharging = chg,
                            StatusNote = chg ? "Заряжается" : "От батареи"
                        });
                        break;
                    }
                }
                catch { }
            }
        }

        return result;
    }

    public static bool Matches(CustomProtocolRule r, string vidHex, string pidHex)
    {
        bool vidOk = r.Vid == "*" || r.Vid.Equals(vidHex, StringComparison.OrdinalIgnoreCase);
        bool pidOk = r.Pid == "*" || r.Pid.Equals(pidHex, StringComparison.OrdinalIgnoreCase);
        return vidOk && pidOk;
    }

    public static bool MatchesUsagePage(HidDevice dev, string usagePageHex)
    {
        if (string.IsNullOrWhiteSpace(usagePageHex)) return true;
        if (!int.TryParse(usagePageHex, System.Globalization.NumberStyles.HexNumber, null, out int targetPage))
            return true;
        try
        {
            return dev.GetReportDescriptor().DeviceItems
                .SelectMany(x => x.Usages.GetAllValues())
                .Any(u => (u >> 16) == targetPage);
        }
        catch { return false; }
    }

    public static byte[] ParseHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        return hex.Split(new[] { ' ', '-', ',', ':' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Convert.ToByte(s, 16))
            .ToArray();
    }

    public static bool StartsWithPrefix(byte[] buf, int len, byte[] prefix)
    {
        if (prefix.Length == 0) return true;
        if (len < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
            if (buf[i] != prefix[i]) return false;
        return true;
    }
}
