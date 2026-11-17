using System.Text.Json;
using HidSharp;
using WirelessBatteryHub.Models;
using WirelessBatteryHub.Providers;

namespace WirelessBatteryHub.Services;

public sealed class ProtocolAutoEngine : IDisposable
{
    private static readonly string CachePath =
        Path.Combine(AppContext.BaseDirectory, "protocol_cache.json");

    private readonly BluetoothBatteryProvider _btProvider = new();
    private readonly XInputGamepadProvider _xinputProvider = new();
    private readonly IBatteryProvider[] _hidProviders =
    {
        new LogitechHidppProvider(),
        new RazerHidProvider(),
        new CompxNordicProvider(),
        new WLMouseProvider(),
        new AsusRogProvider(),
        new KeychronProvider(),
        new SonyGamepadProvider(),
        new NintendoGamepadProvider(),
        new SteelSeriesMouseProvider(),
        new SteelSeriesHeadsetProvider(),
        new HyperXHeadsetProvider(),
        new CustomJsonProvider()
    };

    private readonly PassiveHidManager _passiveManager = new();
    private readonly Dictionary<string, DeviceBatteryInfo> _lastKnownUsb = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _vidPidCache;

    public ProtocolAutoEngine()
    {
        _vidPidCache = LoadCache();
    }

    public IReadOnlyList<DeviceBatteryInfo> ScanAll()
    {
        var results = new List<DeviceBatteryInfo>();

        try { results.AddRange(_btProvider.ReadDevices()); } catch { }

        _passiveManager.SyncListeners();

        var presentVidPids = DeviceList.Local.GetHidDevices()
            .Select(d => $"{d.VendorID:X4}:{d.ProductID:X4}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool cacheDirty = false;

        foreach (string vidPid in presentVidPids)
        {
            if (_vidPidCache.TryGetValue(vidPid, out string? cachedName))
            {
                IBatteryProvider? cachedProv = _hidProviders.FirstOrDefault(p => p.ProviderName == cachedName);
                if (cachedProv != null)
                {
                    var found = SafeRead(cachedProv, vidPid);
                    if (found.Count > 0)
                    {
                        results.AddRange(found);
                        continue;
                    }
                }
            }

            foreach (IBatteryProvider prov in _hidProviders)
            {
                if (!SafeCanHandle(prov, vidPid)) continue;
                var found = SafeRead(prov, vidPid);
                if (found.Count > 0)
                {
                    results.AddRange(found);
                    _vidPidCache[vidPid] = prov.ProviderName;
                    cacheDirty = true;
                    break;
                }
            }
        }

        if (cacheDirty) SaveCache(_vidPidCache);

        var activeVidPids = new HashSet<string>(
            results.Where(r => !string.IsNullOrEmpty(r.VidPid)).Select(r => r.VidPid!),
            StringComparer.OrdinalIgnoreCase);

        foreach (DeviceBatteryInfo p in _passiveManager.GetSnapshot())
        {
            if (p.VidPid == null || !activeVidPids.Contains(p.VidPid))
                results.Add(p);
        }

        try
        {
            var xinputs = _xinputProvider.ReadDevices();
            bool hasXboxBt = results.Any(r =>
                r.IsConnected &&
                r.Kind == DeviceKind.Gamepad &&
                r.ProtocolName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) &&
                r.Name.Contains("xbox", StringComparison.OrdinalIgnoreCase));

            bool hasXboxDongle = presentVidPids.Contains("045E:02FE") || presentVidPids.Contains("045E:02E6");

            if (!(hasXboxBt && !hasXboxDongle && xinputs.Count == 1))
                results.AddRange(xinputs);
        }
        catch { }

        for (int i = 0; i < results.Count; i++)
        {
            DeviceBatteryInfo d = results[i];
            if (string.IsNullOrEmpty(d.VidPid)) continue;

            if (d.BatteryPercent.HasValue)
            {
                _lastKnownUsb[d.Id] = d;
            }
            else if (_lastKnownUsb.TryGetValue(d.Id, out DeviceBatteryInfo? prev) && prev.BatteryPercent.HasValue)
            {
                results[i] = CloneAsSleeping(prev);
            }
        }

        var currentIds = results.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string cachedId in _lastKnownUsb.Keys.ToList())
        {
            if (currentIds.Contains(cachedId)) continue;

            DeviceBatteryInfo prev = _lastKnownUsb[cachedId];
            if (prev.VidPid != null && presentVidPids.Contains(prev.VidPid))
                results.Add(CloneAsSleeping(prev));
            else
                _lastKnownUsb.Remove(cachedId);
        }

        return results;
    }

    private static DeviceBatteryInfo CloneAsSleeping(DeviceBatteryInfo prev) => new()
    {
        Id = prev.Id,
        Name = prev.Name,
        Kind = prev.Kind,
        ProtocolName = prev.ProtocolName,
        ConnectionType = prev.ConnectionType,
        VidPid = prev.VidPid,
        BatteryPercent = prev.BatteryPercent,
        IsConnected = true,
        IsCharging = false,
        IsSleeping = true,
        StatusNote = prev.BatteryPercent.HasValue ? $"Спит (было {prev.BatteryPercent}%)" : "Спит"
    };

    private static bool SafeCanHandle(IBatteryProvider p, string vidPid)
    {
        try { return p.CanHandle(vidPid); } catch { return false; }
    }

    private static IReadOnlyList<DeviceBatteryInfo> SafeRead(IBatteryProvider p, string vidPid)
    {
        try { return p.ReadDevices(vidPid); } catch { return Array.Empty<DeviceBatteryInfo>(); }
    }

    private static Dictionary<string, string> LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath))
                       ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveCache(Dictionary<string, string> cache)
    {
        try
        {
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void Dispose() => _passiveManager.Dispose();
}
