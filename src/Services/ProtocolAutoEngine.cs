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
        new CustomJsonProvider()
    };

    private readonly PassiveHidManager _passiveManager = new();
    private Dictionary<string, string> _vidPidCache;

    public ProtocolAutoEngine()
    {
        _vidPidCache = LoadCache();
    }

    public IReadOnlyList<DeviceBatteryInfo> ScanAll()
    {
        var results = new List<DeviceBatteryInfo>();

        // 1. Bluetooth (мыши, клавиатуры, геймпады)
        try { results.AddRange(_btProvider.ReadDevices()); } catch { }

        // 2. Пассивные слушатели (Beken / Attack Shark / Custom JSON)
        _passiveManager.SyncListeners();

        // 3. USB HID устройства
        var presentVidPids = DeviceList.Local.GetHidDevices()
            .Select(d => $"{d.VendorID:X4}:{d.ProductID:X4}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

        // 4. Пассивные устройства
        var activeVidPids = new HashSet<string>(
            results.Where(r => !string.IsNullOrEmpty(r.VidPid)).Select(r => r.VidPid!),
            StringComparer.OrdinalIgnoreCase);

        foreach (DeviceBatteryInfo p in _passiveManager.GetSnapshot())
        {
            if (p.VidPid == null || !activeVidPids.Contains(p.VidPid))
                results.Add(p);
        }

        // 5. XInput-геймпады (Xbox Wireless Adapter, 8BitDo, GameSir и др.)
        try
        {
            var xinputs = _xinputProvider.ReadDevices();
            bool hasXboxBt = results.Any(r =>
                r.IsConnected &&
                r.Kind == DeviceKind.Gamepad &&
                r.ProtocolName == "Bluetooth" &&
                r.Name.Contains("xbox", StringComparison.OrdinalIgnoreCase));

            bool hasXboxDongle = presentVidPids.Any(v =>
                v.Equals("045E:02FE", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("045E:02E6", StringComparison.OrdinalIgnoreCase));

            // Если Xbox геймпад подключён по Bluetooth и уже отдал точный % — не дублируем его грубым шагом XInput
            if (hasXboxBt && !hasXboxDongle && xinputs.Count == 1)
            {
                // Пропускаем дубль XInput
            }
            else
            {
                results.AddRange(xinputs);
            }
        }
        catch { }

        return results;
    }

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
