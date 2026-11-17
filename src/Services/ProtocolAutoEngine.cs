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
    private readonly IBatteryProvider[] _hidProviders =
    {
        new LogitechHidppProvider(),
        new RazerHidProvider(),
        new CompxNordicProvider(),
        new WLMouseProvider(),
        new AsusRogProvider(),
        new KeychronProvider(),
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

        // 1. Всегда читаем Bluetooth (мыши, клавиатуры, наушники и др.)
        try { results.AddRange(_btProvider.ReadDevices()); } catch { }

        // 2. Запускаем пассивные слушатели (Beken / Attack Shark и правила protocols.json)
        _passiveManager.EnsureListenersStarted();

        // 3. Собираем все подключённые USB VID:PID
        var presentVidPids = DeviceList.Local.GetHidDevices()
            .Select(d => $"{d.VendorID:X4}:{d.ProductID:X4}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool cacheDirty = false;

        foreach (string vidPid in presentVidPids)
        {
            // Если уже есть закэшированный провайдер — сначала пробуем его
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

            // Иначе автоподбор среди провайдеров, подходящих под отпечаток VID:PID / дескриптора
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

        // 4. Добавляем данные от пассивных слушателей (если активный опрос уже не нашёл это VID:PID)
        var activeVidPids = new HashSet<string>(
            results.Where(r => !string.IsNullOrEmpty(r.VidPid)).Select(r => r.VidPid!),
            StringComparer.OrdinalIgnoreCase);

        foreach (DeviceBatteryInfo p in _passiveManager.GetSnapshot())
        {
            if (p.VidPid == null || !activeVidPids.Contains(p.VidPid))
                results.Add(p);
        }

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
