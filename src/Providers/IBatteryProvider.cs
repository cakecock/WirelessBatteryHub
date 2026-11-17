using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public interface IBatteryProvider
{
    string ProviderName { get; }
    bool CanHandle(string vidPidKey);
    IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null);
}
