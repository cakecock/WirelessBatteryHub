using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using WirelessBatteryHub.Models;

namespace WirelessBatteryHub.Services;

public static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateBadgeIcon(
        int? percent,
        bool isCharging,
        DeviceKind kind,
        bool isSleeping = false,
        TrayIconStyle style = TrayIconStyle.Badge,
        bool darkTaskbar = true)
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

            switch (style)
            {
                case TrayIconStyle.CleanDigits:
                    DrawCleanDigits(g, w, h, percent, isCharging, kind, isSleeping, darkTaskbar);
                    break;
                case TrayIconStyle.BatteryIcon:
                    DrawBatteryShape(g, w, h, percent, isCharging, kind, isSleeping, darkTaskbar);
                    break;
                default:
                    DrawBadge(g, w, h, percent, isCharging, kind, isSleeping);
                    break;
            }
        }

        return BitmapToClonedIcon(bmp);
    }

    public static Icon CreateAppWindowIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(Color.FromArgb(30, 32, 40));
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 7);
            g.FillPath(bg, path);

            using var border = new Pen(Color.FromArgb(80, 200, 255), 2f);
            g.DrawPath(border, path);

            // Силуэт батарейки
            using var batPen = new Pen(Color.White, 2f);
            g.DrawRectangle(batPen, 7, 10, 15, 12);
            using var capBrush = new SolidBrush(Color.White);
            g.FillRectangle(capBrush, 22, 13, 3, 6);

            using var fillBrush = new SolidBrush(Color.FromArgb(70, 215, 115));
            g.FillRectangle(fillBrush, 9, 12, 11, 8);
        }

        return BitmapToClonedIcon(bmp);
    }

    private static void DrawBadge(Graphics g, int w, int h, int? percent, bool isCharging, DeviceKind kind, bool isSleeping)
    {
        using var bgBrush = new SolidBrush(Color.FromArgb(242, isSleeping ? 42 : 28, isSleeping ? 42 : 28, isSleeping ? 48 : 34));
        g.FillRectangle(bgBrush, 0, 0, w, h);

        Color kindColor = GetKindColor(kind);
        using (var topBar = new SolidBrush(isSleeping ? Color.FromArgb(120, kindColor) : kindColor))
            g.FillRectangle(topBar, 1, 0, w - 2, Math.Max(1, h / 10));

        Color accent = GetLevelColor(percent, isCharging, isSleeping, darkBg: true);
        int barH = Math.Max(2, h / 6);
        using (var trackBrush = new SolidBrush(Color.FromArgb(65, 65, 72)))
            g.FillRectangle(trackBrush, 1, h - barH - 1, w - 2, barH);

        if (percent.HasValue)
        {
            int fillW = (int)Math.Round((w - 2) * (percent.Value / 100.0));
            if (fillW < 1 && percent.Value > 0) fillW = 1;
            using var fillBrush = new SolidBrush(accent);
            g.FillRectangle(fillBrush, 1, h - barH - 1, fillW, barH);
        }

        string label = percent.HasValue ? percent.Value.ToString() : "--";
        float fontSize = label.Length >= 3 ? h * 0.40f : h * 0.48f;
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(isSleeping ? Color.FromArgb(190, 195, 205) : Color.White);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, textBrush, new RectangleF(-1, 1, w + 2, h - barH - 1), sf);
    }

    private static void DrawCleanDigits(Graphics g, int w, int h, int? percent, bool isCharging, DeviceKind kind, bool isSleeping, bool darkTaskbar)
    {
        string label = percent.HasValue ? percent.Value.ToString() : "--";
        Color fg = GetLevelColor(percent, isCharging, isSleeping, darkBg: darkTaskbar);
        float fontSize = label.Length >= 3 ? h * 0.48f : h * 0.62f;

        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        // Лёгкая тень для читаемости на любом фоне панели задач
        using var shadowBrush = new SolidBrush(darkTaskbar ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(140, 255, 255, 255));
        g.DrawString(label, font, shadowBrush, new RectangleF(0, 1, w + 2, h), sf);

        using var textBrush = new SolidBrush(fg);
        g.DrawString(label, font, textBrush, new RectangleF(-1, 0, w + 2, h), sf);

        // Маленькая точка типа устройства в правом нижнем углу
        int dot = Math.Max(3, w / 5);
        using var dotBrush = new SolidBrush(isSleeping ? Color.Gray : GetKindColor(kind));
        g.FillEllipse(dotBrush, w - dot - 1, h - dot - 1, dot, dot);
    }

    private static void DrawBatteryShape(Graphics g, int w, int h, int? percent, bool isCharging, DeviceKind kind, bool isSleeping, bool darkTaskbar)
    {
        Color frameColor = darkTaskbar ? Color.FromArgb(220, 225, 235) : Color.FromArgb(45, 48, 56);
        Color fillColor = GetLevelColor(percent, isCharging, isSleeping, darkBg: darkTaskbar);

        int capW = Math.Max(2, w / 8);
        int bodyW = w - capW - 2;
        int bodyH = Math.Max(10, (int)(h * 0.72));
        int topY = (h - bodyH) / 2;

        // Контур батарейки
        using var pen = new Pen(frameColor, Math.Max(1f, w / 16f));
        g.DrawRectangle(pen, 1, topY, bodyW, bodyH);

        // Контакт справа
        using var capBrush = new SolidBrush(frameColor);
        g.FillRectangle(capBrush, 1 + bodyW, topY + bodyH / 4, capW, Math.Max(3, bodyH / 2));

        // Заливка заряда
        if (percent.HasValue)
        {
            int innerMaxW = Math.Max(1, bodyW - 3);
            int fillW = (int)Math.Round(innerMaxW * (percent.Value / 100.0));
            if (fillW < 1 && percent.Value > 0) fillW = 1;
            using var fillBrush = new SolidBrush(fillColor);
            g.FillRectangle(fillBrush, 3, topY + 2, fillW, Math.Max(2, bodyH - 3));
        }

        // Тонкая метка типа устройства под батарейкой
        using var kindBrush = new SolidBrush(GetKindColor(kind));
        g.FillRectangle(kindBrush, 1, topY + bodyH + 2, bodyW, Math.Max(1, h / 12));
    }

    private static Color GetKindColor(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => Color.FromArgb(80, 200, 255),     // Бирюзовый
        DeviceKind.Keyboard => Color.FromArgb(185, 130, 255), // Фиолетовый
        DeviceKind.Gamepad => Color.FromArgb(255, 165, 55),   // Золотой
        DeviceKind.Headset => Color.FromArgb(245, 110, 175),  // Малиновый (Гарнитура)
        _ => Color.FromArgb(140, 140, 150)
    };

    private static Color GetLevelColor(int? percent, bool isCharging, bool isSleeping, bool darkBg)
    {
        if (!percent.HasValue || isSleeping)
            return darkBg ? Color.FromArgb(150, 155, 165) : Color.FromArgb(100, 105, 115);

        if (isCharging)
            return darkBg ? Color.FromArgb(75, 185, 255) : Color.FromArgb(0, 115, 215);

        int v = percent.Value;
        if (v <= 15) return darkBg ? Color.FromArgb(255, 85, 85) : Color.FromArgb(205, 25, 25);
        if (v <= 30) return darkBg ? Color.FromArgb(255, 195, 55) : Color.FromArgb(195, 120, 0);
        return darkBg ? Color.FromArgb(80, 225, 120) : Color.FromArgb(15, 145, 55);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Icon BitmapToClonedIcon(Bitmap bmp)
    {
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
