using System.Runtime.InteropServices;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public sealed class XInputGamepadProvider : IBatteryProvider
{
    public string ProviderName => "XInput Gamepad";
    public bool CanHandle(string vidPidKey) => false; // Опрашивается напрямую через XInput API

    private const byte BATTERY_DEVTYPE_GAMEPAD = 0x00;
    private const byte BATTERY_TYPE_DISCONNECTED = 0x00;
    private const byte BATTERY_TYPE_WIRED = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern uint XInputGetBatteryInformation(
        uint dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);

    public IReadOnlyList<DeviceBatteryInfo> ReadDevices(string? targetVidPid = null)
    {
        var result = new List<DeviceBatteryInfo>();

        for (uint slot = 0; slot < 4; slot++)
        {
            try
            {
                uint status = XInputGetBatteryInformation(slot, BATTERY_DEVTYPE_GAMEPAD, out var info);
                if (status != 0) continue;
                if (info.BatteryType == BATTERY_TYPE_DISCONNECTED) continue;

                bool isWired = info.BatteryType == BATTERY_TYPE_WIRED;
                int pct = info.BatteryLevel switch
                {
                    0 => 10,   // EMPTY
                    1 => 30,   // LOW
                    2 => 65,   // MEDIUM
                    _ => 100   // FULL
                };

                result.Add(new DeviceBatteryInfo
                {
                    Id = $"XINPUT:{slot}",
                    Name = $"Геймпад Xbox / XInput #{slot + 1}",
                    Kind = DeviceKind.Gamepad,
                    ProtocolName = ProviderName,
                    ConnectionType = isWired ? "USB (XInput)" : "Беспроводной (XInput)",
                    BatteryPercent = isWired ? 100 : pct,
                    IsConnected = true,
                    IsCharging = isWired,
                    StatusNote = isWired ? "По кабелю" : "От батареи"
                });
            }
            catch
            {
                break;
            }
        }

        return result;
    }
}
