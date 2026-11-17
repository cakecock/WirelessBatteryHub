using HidSharp;
using WirelessBatteryHub.Models;
using WirelessBatteryHub.Providers;

namespace WirelessBatteryHub.Forms;

public sealed class AnalyzerForm : Form
{
    private readonly ComboBox _cmbInterfaces;
    private readonly ComboBox _cmbKind;
    private readonly NumericUpDown _numExpectedPct;
    private readonly Button _btnToggleListen;
    private readonly Button _btnSaveRule;
    private readonly TextBox _txtLog;

    private readonly List<HidDevice> _vendorDevices = new();
    private CancellationTokenSource? _cts;
    private CustomProtocolRule? _detectedDraft;

    public AnalyzerForm()
    {
        Text = "Анализатор неизвестной мыши / клавиатуры (HID)";
        Size = new Size(720, 500);
        StartPosition = FormStartPosition.CenterParent;

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 105, Padding = new Padding(10) };

        var lbl1 = new Label { Text = "Vendor-интерфейс (Usage >= FF00):", Location = new Point(10, 12), AutoSize = true };
        _cmbInterfaces = new ComboBox
        {
            Location = new Point(10, 32),
            Width = 470,
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        var lblKind = new Label { Text = "Тип:", Location = new Point(490, 12), AutoSize = true };
        _cmbKind = new ComboBox
        {
            Location = new Point(490, 32),
            Width = 110,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _cmbKind.Items.AddRange(new object[] { "Mouse", "Keyboard" });
        _cmbKind.SelectedIndex = 0;

        var lbl2 = new Label { Text = "Известный заряд % (0 = искать любое):", Location = new Point(10, 68), AutoSize = true };
        _numExpectedPct = new NumericUpDown { Location = new Point(235, 65), Width = 65, Minimum = 0, Maximum = 100, Value = 0 };

        _btnToggleListen = new Button { Text = "Слушать пакеты", Location = new Point(315, 63), Size = new Size(135, 28) };
        _btnToggleListen.Click += OnToggleListen;

        _btnSaveRule = new Button { Text = "Сохранить в protocols.json", Location = new Point(460, 63), Size = new Size(210, 28), Enabled = false };
        _btnSaveRule.Click += OnSaveRule;

        topPanel.Controls.AddRange(new Control[]
        {
            lbl1, _cmbInterfaces, lblKind, _cmbKind, lbl2, _numExpectedPct, _btnToggleListen, _btnSaveRule
        });

        _txtLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9.5f),
            Text = "1. Выберите Vendor-интерфейс вашего донгла.\r\n" +
                   "2. Если знаете текущий % из родного софта — укажите его.\r\n" +
                   "3. Нажмите «Слушать пакеты» и пошевелите мышкой / нажмите клавиши.\r\n\r\n"
        };

        Controls.Add(_txtLog);
        Controls.Add(topPanel);

        LoadInterfaces();
    }

    private void LoadInterfaces()
    {
        foreach (HidDevice dev in DeviceList.Local.GetHidDevices())
        {
            try
            {
                var usages = dev.GetReportDescriptor().DeviceItems
                    .SelectMany(x => x.Usages.GetAllValues())
                    .ToList();

                bool isVendor = usages.Any(u => (u >> 16) >= 0xFF00);
                if (!isVendor || dev.GetMaxInputReportLength() < 4) continue;

                string name = dev.GetProductName()?.Trim() ?? "HID Device";
                string usageStr = string.Join(",", usages.Select(u => $"{u >> 16:X4}:{u & 0xFFFF:X4}").Distinct());
                _vendorDevices.Add(dev);
                _cmbInterfaces.Items.Add($"[{dev.VendorID:X4}:{dev.ProductID:X4}] {name} | Usage:{usageStr} | In:{dev.GetMaxInputReportLength()}");
            }
            catch { }
        }

        if (_cmbInterfaces.Items.Count > 0) _cmbInterfaces.SelectedIndex = 0;
    }

    private void OnToggleListen(object? sender, EventArgs e)
    {
        if (_cts != null)
        {
            StopListening();
            return;
        }

        if (_cmbInterfaces.SelectedIndex < 0) return;
        HidDevice dev = _vendorDevices[_cmbInterfaces.SelectedIndex];
        int targetPct = (int)_numExpectedPct.Value;

        _cts = new CancellationTokenSource();
        _btnToggleListen.Text = "Остановить";
        _txtLog.AppendText($"--- Слушаем {dev.VendorID:X4}:{dev.ProductID:X4} ---\r\n");

        CancellationToken ct = _cts.Token;
        Task.Run(() => CaptureLoop(dev, targetPct, ct));
    }

    private void CaptureLoop(HidDevice dev, int targetPct, CancellationToken ct)
    {
        try
        {
            if (!dev.TryOpen(out HidStream stream))
            {
                AppendLog("Ошибка: не удалось открыть интерфейс (возможно, занят родным софтом).\r\n");
                return;
            }

            using (stream)
            {
                stream.ReadTimeout = 400;
                byte[] buf = new byte[Math.Max(dev.GetMaxInputReportLength(), 64)];

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        int n = stream.Read(buf, 0, buf.Length);
                        if (n <= 0) continue;

                        string hex = string.Join(" ", buf.Take(n).Select(b => b.ToString("X2")));
                        string matchNote = "";

                        if (targetPct > 0)
                        {
                            for (int i = 1; i < n; i++)
                            {
                                if (buf[i] == targetPct)
                                {
                                    matchNote = $"  <-- НАЙДЕН БАЙТ #{i} == {targetPct}%!";
                                    PrepareDraft(dev, buf, i);
                                    break;
                                }
                            }
                        }

                        AppendLog($"RX ({n}): {hex}{matchNote}\r\n");
                    }
                    catch (TimeoutException) { }
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка чтения: {ex.Message}\r\n");
        }
    }

    private void PrepareDraft(HidDevice dev, byte[] packet, int pctIndex)
    {
        int prefixLen = Math.Min(pctIndex, 4);
        string prefixHex = string.Join(" ", packet.Take(prefixLen).Select(b => b.ToString("X2")));
        string name = "Custom Device";
        try { name = dev.GetProductName()?.Trim() ?? name; } catch { }

        _detectedDraft = new CustomProtocolRule
        {
            Name = name,
            Vid = $"{dev.VendorID:X4}",
            Pid = $"{dev.ProductID:X4}",
            Kind = _cmbKind.SelectedItem?.ToString() ?? "Mouse",
            Mode = "PassiveInput",
            ResponsePrefixHex = prefixHex,
            BatteryByteIndex = pctIndex,
            MaxScale = 100
        };

        BeginInvoke(() => _btnSaveRule.Enabled = true);
    }

    private void OnSaveRule(object? sender, EventArgs e)
    {
        if (_detectedDraft == null) return;
        CustomJsonProvider.AddOrUpdateRule(_detectedDraft);
        MessageBox.Show($"Правило для {_detectedDraft.Name} сохранено в protocols.json!\nНажмите «Обновить» в главном окне.",
            "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void AppendLog(string msg)
    {
        if (IsDisposed) return;
        BeginInvoke(() => _txtLog.AppendText(msg));
    }

    private void StopListening()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _btnToggleListen.Text = "Слушать пакеты";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopListening();
        base.OnFormClosing(e);
    }
}
