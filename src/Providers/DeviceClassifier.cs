using HidSharp;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Providers;

public static class DeviceClassifier
{
    private static readonly string[] HeadsetKeywords =
    {
        "headset", "headphone", "arctis", "hyperx", "cloud ", "g733", "g533", "g935",
        "g435", "virtuoso", "hs80", "hs70", "void", "barracuda", "blackshark",
        "kraken", "airpods", "buds", "wh-1000", "wf-1000", "наушник", "гарнитур"
    };

    private static readonly string[] GamepadKeywords =
    {
        "gamepad", "controller", "xbox", "dualsense", "dualshock", "playstation",
        "8bitdo", "switch pro", "joy-con", "gamesir", "flydigi", "gulikit",
        "vader", "apex 4", "scuf", "геймпад", "джойстик"
    };

    private static readonly string[] KeyboardKeywords =
    {
        "keyboard", "клавиатур", "keychron", "mx keys", "g915", "g715", "g613",
        "blackwidow", "deathstalker", "huntsman", "aula", "lofree",
        "nuphy", "akko", "drunkdeer", "wooting", "rainy", "tkl", "65%", "75%"
    };

    private static readonly string[] MouseKeywords =
    {
        "mouse", "мышь", "viper", "deathadder", "basilisk", "cobra", "naga",
        "superlight", "g pro", "g305", "g502", "g703", "g903", "mx master",
        "mx anywhere", "aerox", "rival", "beast", "pulsar", "atk", "vxe", "vgn",
        "zaopin", "scyrox", "kysona", "attack shark", "darmoshark", "lamzu",
        "g-wolves", "mchose", "gladius", "keris", "chakram", "harpe"
    };

    public static DeviceKind GuessByName(string name, DeviceKind fallback = DeviceKind.Mouse)
    {
        string lower = name.ToLowerInvariant();
        if (HeadsetKeywords.Any(k => lower.Contains(k))) return DeviceKind.Headset;
        if (GamepadKeywords.Any(k => lower.Contains(k))) return DeviceKind.Gamepad;
        if (KeyboardKeywords.Any(k => lower.Contains(k))) return DeviceKind.Keyboard;
        if (MouseKeywords.Any(k => lower.Contains(k))) return DeviceKind.Mouse;
        return fallback;
    }

    public static DeviceKind GuessFromHidSibling(int vid, int pid, string name)
    {
        DeviceKind byName = GuessByName(name, DeviceKind.Other);
        if (byName != DeviceKind.Other) return byName;

        bool hasMouse = false, hasKeyboard = false, hasGamepad = false, hasTelephony = false;

        try
        {
            foreach (HidDevice dev in DeviceList.Local.GetHidDevices(vid, pid))
            {
                foreach (var item in dev.GetReportDescriptor().DeviceItems)
                {
                    foreach (uint u in item.Usages.GetAllValues())
                    {
                        if (u == 0x00010002) hasMouse = true;
                        if (u == 0x00010006) hasKeyboard = true;
                        if (u is 0x00010004 or 0x00010005) hasGamepad = true;
                        if ((u >> 16) == 0x000B) hasTelephony = true;
                    }
                }
            }
        }
        catch { }

        if (hasGamepad) return DeviceKind.Gamepad;
        if (hasTelephony && !hasMouse && !hasKeyboard) return DeviceKind.Headset;
        if (hasMouse && !hasKeyboard) return DeviceKind.Mouse;
        if (hasKeyboard && !hasMouse) return DeviceKind.Keyboard;
        return DeviceKind.Mouse;
    }
}
