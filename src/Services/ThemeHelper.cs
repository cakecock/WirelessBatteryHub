using System.Runtime.InteropServices;
using Microsoft.Win32;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Services;

public static class ThemeHelper
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static bool IsWindowsDarkApps()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false);
            object? val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 0;
        }
        catch { return false; }
    }

    public static bool IsWindowsDarkTaskbar()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false);
            object? val = key?.GetValue("SystemUsesLightTheme");
            return val is int i && i == 0;
        }
        catch { return true; }
    }

    public static bool ShouldUseDarkTheme(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Dark => true,
        AppThemeMode.Light => false,
        _ => IsWindowsDarkApps()
    };

    public static void ApplyTitleBarTheme(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        int val = dark ? 1 : 0;
        // 20 = Windows 11 / Win10 2004+, 19 = старые сборки Win10
        if (DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref val, sizeof(int));
    }

    public static void ApplyControlTreeTheme(Control root, bool dark)
    {
        Color bg = dark ? Color.FromArgb(28, 28, 34) : SystemColors.Control;
        Color surface = dark ? Color.FromArgb(38, 38, 46) : SystemColors.Window;
        Color btnBg = dark ? Color.FromArgb(50, 50, 60) : SystemColors.Control;
        Color fg = dark ? Color.FromArgb(236, 236, 242) : SystemColors.ControlText;
        Color border = dark ? Color.FromArgb(72, 72, 86) : SystemColors.ControlDark;

        root.BackColor = bg;
        root.ForeColor = fg;

        ApplyRecursive(root, bg, surface, btnBg, fg, border, dark);
    }

    private static void ApplyRecursive(Control parent, Color bg, Color surface, Color btnBg, Color fg, Color border, bool dark)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case ListView lv:
                    lv.BackColor = surface;
                    lv.ForeColor = fg;
                    lv.OwnerDraw = dark;
                    break;

                case TextBox tb:
                    tb.BackColor = surface;
                    tb.ForeColor = fg;
                    tb.BorderStyle = dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                    break;

                case ComboBox cb:
                    cb.BackColor = surface;
                    cb.ForeColor = fg;
                    cb.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                    break;

                case NumericUpDown num:
                    num.BackColor = surface;
                    num.ForeColor = fg;
                    break;

                case Button btn:
                    btn.BackColor = btnBg;
                    btn.ForeColor = fg;
                    btn.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                    if (dark)
                    {
                        btn.FlatAppearance.BorderColor = border;
                        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(65, 65, 78);
                    }
                    break;

                case Label lbl:
                    lbl.BackColor = Color.Transparent;
                    if (lbl.Tag as string != "muted") lbl.ForeColor = fg;
                    else lbl.ForeColor = dark ? Color.FromArgb(155, 158, 170) : Color.DimGray;
                    break;

                case Panel or FlowLayoutPanel or CheckBox:
                    c.BackColor = bg;
                    c.ForeColor = fg;
                    break;
            }

            if (c.HasChildren)
                ApplyRecursive(c, bg, surface, btnBg, fg, border, dark);
        }
    }
}
