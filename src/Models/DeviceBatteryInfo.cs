namespace WirelessBatteryHub.Models;

public enum DeviceKind
{
    Mouse,
    Keyboard,
    Gamepad,
    Other
}

public sealed class DeviceBatteryInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DeviceKind Kind { get; init; }
    public required string ProtocolName { get; init; }
    public required string ConnectionType { get; init; }
    public string? VidPid { get; init; }
    public int? BatteryPercent { get; init; }
    public bool IsConnected { get; init; }
    public bool IsCharging { get; init; }
    public bool IsSleeping { get; init; }
    public string StatusNote { get; init; } = string.Empty;
}

public sealed class CustomProtocolRule
{
    public string Name { get; set; } = "Custom Device";
    public string Vid { get; set; } = "";
    public string Pid { get; set; } = "";
    public string Kind { get; set; } = "Mouse";
    public string Mode { get; set; } = "PassiveInput";
    public string UsagePageHex { get; set; } = "";
    public string RequestHex { get; set; } = "";
    public string ResponsePrefixHex { get; set; } = "";
    public int BatteryByteIndex { get; set; } = 4;
    public int ChargingByteIndex { get; set; } = -1;
    public int MaxScale { get; set; } = 100;
}
