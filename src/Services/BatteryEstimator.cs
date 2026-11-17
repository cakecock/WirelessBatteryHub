using System.Text.Json;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Services;

public sealed class BatterySample
{
    public DateTime Utc { get; set; }
    public int Percent { get; set; }
}

public sealed class BatteryEstimator
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<BatterySample>> _history;
    private readonly string _historyPath;

    public BatteryEstimator()
    {
        _historyPath = ResolveHistoryPath();
        _history = Load(_historyPath);
    }

    public string? RecordAndEstimate(DeviceBatteryInfo d)
    {
        if (!d.IsConnected || d.IsSleeping || !d.BatteryPercent.HasValue)
            return null;

        int pct = d.BatteryPercent.Value;

        lock (_sync)
        {
            if (d.IsCharging)
            {
                if (_history.Remove(d.Id)) Save();
                return null;
            }

            if (!_history.TryGetValue(d.Id, out List<BatterySample>? list))
            {
                list = new List<BatterySample>();
                _history[d.Id] = list;
            }

            DateTime now = DateTime.UtcNow;

            if (list.Count > 0)
            {
                BatterySample last = list[^1];
                // Если заряд заметно вырос — устройство заряжали, начинаем замер заново
                if (pct > last.Percent + 3)
                {
                    list.Clear();
                }
                else if (pct != last.Percent || (now - last.Utc).TotalMinutes >= 15)
                {
                    list.Add(new BatterySample { Utc = now, Percent = pct });
                    if (list.Count > 30) list.RemoveAt(0);
                    Save();
                }
            }

            if (list.Count == 0)
            {
                list.Add(new BatterySample { Utc = now, Percent = pct });
                Save();
                return null;
            }

            BatterySample first = list[0];
            BatterySample latest = list[^1];
            double hours = (latest.Utc - first.Utc).TotalHours;
            int dropped = first.Percent - latest.Percent;

            if (hours < 0.5 || dropped < 2) return null;

            double dropPerHour = dropped / hours;
            if (dropPerHour <= 0.05) return null;

            double remainingHours = pct / dropPerHour;
            if (remainingHours is < 0.15 or > 500) return null;

            return remainingHours >= 1.5
                ? $"~{Math.Round(remainingHours)} ч"
                : $"~{Math.Max(5, Math.Round(remainingHours * 60))} мин";
        }
    }

    private static string ResolveHistoryPath()
    {
        string portable = Path.Combine(AppContext.BaseDirectory, "battery_history.json");
        try
        {
            string probe = Path.Combine(AppContext.BaseDirectory, ".hist_test");
            if (File.Exists(portable))
            {
                using FileStream fs = File.Open(portable, FileMode.Open, FileAccess.ReadWrite);
                return portable;
            }
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return portable;
        }
        catch
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WirelessBatteryHub");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "battery_history.json");
        }
    }

    private static Dictionary<string, List<BatterySample>> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, List<BatterySample>>>(File.ReadAllText(path))
                       ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_historyPath, JsonSerializer.Serialize(_history));
        }
        catch { }
    }
}
