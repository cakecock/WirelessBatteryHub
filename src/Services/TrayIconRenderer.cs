using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Services;

public static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateBadgeIcon(int? percent, bool isCharging, DeviceKind kind)
    {
        Size sz = SystemInformation.SmallIconSize;
        int w = Math.Max(sz.Width, 16);
        int h = Math.Max(sz.Height, 16);

        using var bmp = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            using var bgBrush = new SolidBrush(Color.FromArgb(242, 28, 28, 34));
            g.FillRectangle(bgBrush, 0, 0, w, h);

            // Тонкая верхняя метка типа устройства: бирюзовая для мыши, фиолетовая для клавиатуры
            Color kindColor = kind switch
            {
                DeviceKind.Mouse => Color.FromArgb(80, 200, 255),
                DeviceKind.Keyboard => Color.FromArgb(185, 130, 255),
                _ => Color.FromArgb(140, 140, 150)
            };
            using (var topBar = new SolidBrush(kindColor))
            {
                g.FillRectangle(topBar, 1, 0, w - 2, Math.Max(1, h / 10));
            }

            Color accent;
            string label;
            if (!percent.HasValue)
            {
                accent = Color.DimGray;
                label = "--";
            }
            else
            {
                int v = percent.Value;
                label = v.ToString();
                if (isCharging) accent = Color.FromArgb(70, 175, 255);
                else if (v <= 15) accent = Color.FromArgb(245, 75, 75);
                else if (v <= 30) accent = Color.FromArgb(245, 185, 50);
                else accent = Color.FromArgb(70, 210, 110);
            }

            int barH = Math.Max(2, h / 6);
            using (var trackBrush = new SolidBrush(Color.FromArgb(65, 65, 72)))
            {
                g.FillRectangle(trackBrush, 1, h - barH - 1, w - 2, barH);
            }

            if (percent.HasValue)
            {
                int fillW = (int)Math.Round((w - 2) * (percent.Value / 100.0));
                if (fillW < 1 && percent.Value > 0) fillW = 1;
                using var fillBrush = new SolidBrush(accent);
                g.FillRectangle(fillBrush, 1, h - barH - 1, fillW, barH);
            }

            float fontSize = label.Length >= 3 ? h * 0.40f : h * 0.48f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            g.DrawString(label, font, textBrush, new RectangleF(-1, 1, w + 2, h - barH - 1), sf);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using Icon tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
