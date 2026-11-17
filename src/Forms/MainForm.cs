using WirelessBatteryHub.Models;
using WirelessBatteryHub.Services;
using WinTimer = System.Windows.Forms.Timer;

namespace WirelessBatteryHub.Forms;

public sealed class MainForm : Form
{
    private readonly ProtocolAutoEngine _engine = new();
    private readonly DeviceArrivalWatcher _watcher;
    private readonly Dictionary<string, NotifyIcon> _trayIcons = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly RegisteredWaitHandle _showWaitHandle;
    private readonly WinTimer _rareRefreshTimer;

    private readonly ListView _deviceList;
    private readonly Label _lblHeadline;
    private readonly Label _lblSub;
    private readonly CheckBox _chkStartup;
    private readonly CheckBox _chkMinimizeToTray;
    private readonly CheckBox _chkSeparateTrayIcons;
    private readonly CheckBox _chkRareTimer;
    private readonly Button _btnRefresh;
    private readonly Button _btnAnalyze;

    private bool _allowShowWindow;
    private bool _isExiting;
    private bool _isScanning;
    private string? _primaryDeviceId;
    private List<DeviceBatteryInfo> _devices = new();

    public MainForm(bool startMinimized, EventWaitHandle showWindowEvent)
    {
        _allowShowWindow = !startMinimized;

        Text = "Wireless Battery Hub — Мыши, Клавиатуры, Геймпады";
        Size = new Size(680, 470);
        MinimumSize = new Size(580, 390);
        StartPosition = FormStartPosition.CenterScreen;

        _ = Handle; // Инициализируем HWND для WM_DEVICECHANGE даже при скрытом запуске

        _showWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            showWindowEvent,
            (_, _) => { if (!IsDisposed) BeginInvoke(RestoreFromTray); },
            null,
            Timeout.Infinite,
            false);

        _lblHeadline = new Label
        {
            Text = "Поиск подключённых устройств...",
            Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 36,
            Padding = new Padding(12, 8, 12, 0)
        };

        _lblSub = new Label
        {
            Text = "Режим без фоновой нагрузки: проверка при старте и подключении новых устройств (USB / Bluetooth)",
            ForeColor = Color.DimGray,
            Dock = DockStyle.Top,
            Height = 24,
            Padding = new Padding(12, 0, 12, 0)
        };

        _deviceList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false
        };
        _deviceList.Columns.Add("Устройство", 220);
        _deviceList.Columns.Add("Тип", 95);
        _deviceList.Columns.Add("Протокол", 160);
        _deviceList.Columns.Add("Заряд", 70);
        _deviceList.Columns.Add("Статус", 100);
        _deviceList.SelectedIndexChanged += OnListSelectionChanged;

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 132, Padding = new Padding(12, 8, 12, 8) };

        _chkStartup = new CheckBox
        {
            Text = "Запускать вместе с Windows (свёрнуто в трей)",
            AutoSize = true,
            Location = new Point(12, 8),
            Checked = StartupManager.IsEnabled()
        };
        _chkStartup.CheckedChanged += (_, _) => StartupManager.SetEnabled(_chkStartup.Checked);

        _chkMinimizeToTray = new CheckBox
        {
            Text = "Сворачивать в трей при закрытии окна",
            AutoSize = true,
            Location = new Point(12, 34),
            Checked = true
        };

        _chkSeparateTrayIcons = new CheckBox
        {
            Text = "Отдельный значок в трее для каждого подключённого устройства",
            AutoSize = true,
            Location = new Point(12, 60),
            Checked = true
        };
        _chkSeparateTrayIcons.CheckedChanged += (_, _) => SyncTrayIcons();

        _chkRareTimer = new CheckBox
        {
            Text = "Подтягивать % заряда раз в 2 минуты (по умолчанию выкл — 0% нагрузки)",
            AutoSize = true,
            Location = new Point(12, 86),
            Checked = false
        };
        _chkRareTimer.CheckedChanged += (_, _) =>
        {
            if (_chkRareTimer.Checked) _rareRefreshTimer.Start();
            else _rareRefreshTimer.Stop();
        };

        _btnRefresh = new Button
        {
            Text = "Обновить",
            Size = new Size(100, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(bottomPanel.Width - 270, 36)
        };
        _btnRefresh.Click += async (_, _) => await ScanAsync();

        _btnAnalyze = new Button
        {
            Text = "HID-Анализатор",
            Size = new Size(145, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(bottomPanel.Width - 160, 36)
        };
        _btnAnalyze.Click += (_, _) =>
        {
            using var dlg = new AnalyzerForm();
            dlg.ShowDialog(this);
            _ = ScanAsync();
        };

        bottomPanel.Controls.AddRange(new Control[]
        {
            _chkStartup, _chkMinimizeToTray, _chkSeparateTrayIcons, _chkRareTimer, _btnRefresh, _btnAnalyze
        });

        Controls.Add(_deviceList);
        Controls.Add(_lblSub);
        Controls.Add(_lblHeadline);
        Controls.Add(bottomPanel);

        // Таймер по умолчанию выключен — работает только при включении галочки
        _rareRefreshTimer = new WinTimer { Interval = 120_000 };
        _rareRefreshTimer.Tick += async (_, _) => await ScanAsync();

        // Слушаем события Windows о подключении/отключении USB и Bluetooth
        _watcher = new DeviceArrivalWatcher(Handle, () => _ = ScanAsync());

        SyncTrayIcons();
        _ = ScanAsync();
    }

    protected override void WndProc(ref Message m)
    {
        _watcher?.HandleWndProc(ref m);
        base.WndProc(ref m);
    }

    protected override void SetVisibleCore(bool value) =>
        base.SetVisibleCore(_allowShowWindow && value);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_isExiting && _chkMinimizeToTray.Checked && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _chkMinimizeToTray.Checked)
            HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        _allowShowWindow = true;
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _rareRefreshTimer.Stop();
        _rareRefreshTimer.Dispose();
        _showWaitHandle.Unregister(null);
        _watcher.Dispose();
        ClearTrayIcons();
        _engine.Dispose();
        Application.Exit();
    }

    private async Task ScanAsync()
    {
        if (_isScanning) return;
        _isScanning = true;
        _btnRefresh.Enabled = false;

        try
        {
            IReadOnlyList<DeviceBatteryInfo> found = await Task.Run(() => _engine.ScanAll());
            _devices = found
                .OrderByDescending(d => d.IsConnected)
                .ThenBy(d => d.Kind)
                .ThenBy(d => d.Name)
                .ToList();
            UpdateUi();
        }
        finally
        {
            _isScanning = false;
            _btnRefresh.Enabled = true;
        }
    }

    private void UpdateUi()
    {
        List<DeviceBatteryInfo> connected = _devices.Where(d => d.IsConnected).ToList();

        if (connected.Count > 0 && (_primaryDeviceId == null || connected.All(d => d.Id != _primaryDeviceId)))
        {
            _primaryDeviceId = (connected.FirstOrDefault(d => d.Kind == DeviceKind.Mouse) ?? connected[0]).Id;
        }

        _deviceList.BeginUpdate();
        _deviceList.Items.Clear();
        foreach (DeviceBatteryInfo d in _devices)
        {
            var item = new ListViewItem(d.Name) { Tag = d.Id };
            item.SubItems.Add(KindToRu(d.Kind));
            item.SubItems.Add(d.ConnectionType);
            item.SubItems.Add(d.BatteryPercent.HasValue ? $"{d.BatteryPercent}%" : "--");
            item.SubItems.Add(d.StatusNote);
            if (!d.IsConnected) item.ForeColor = Color.Gray;
            if (d.Id == _primaryDeviceId) item.Selected = true;
            _deviceList.Items.Add(item);
        }
        _deviceList.EndUpdate();

        _lblHeadline.Text = connected.Count == 0
            ? "Подключённых беспроводных устройств нет"
            : string.Join("  |  ", connected.Take(3).Select(d =>
                $"{d.Name}: {(d.BatteryPercent.HasValue ? d.BatteryPercent + "%" : "--")}"));

        SyncTrayIcons();
    }

    private void SyncTrayIcons()
    {
        // В трей попадают ТОЛЬКО подключённые устройства (IsConnected == true)
        List<DeviceBatteryInfo> connected = _devices.Where(d => d.IsConnected).ToList();
        RebuildTrayMenu(connected);

        List<DeviceBatteryInfo> targets;
        if (connected.Count == 0)
        {
            targets = new List<DeviceBatteryInfo>();
        }
        else if (_chkSeparateTrayIcons.Checked)
        {
            targets = connected;
        }
        else
        {
            DeviceBatteryInfo primary = connected.FirstOrDefault(d => d.Id == _primaryDeviceId) ?? connected[0];
            targets = new List<DeviceBatteryInfo> { primary };
        }

        var wantedKeys = targets.Count == 0
            ? new HashSet<string> { "__EMPTY__" }
            : targets.Select(t => t.Id).ToHashSet();

        foreach (string existingKey in _trayIcons.Keys.Where(k => !wantedKeys.Contains(k)).ToList())
        {
            _trayIcons[existingKey].Visible = false;
            _trayIcons[existingKey].Icon?.Dispose();
            _trayIcons[existingKey].Dispose();
            _trayIcons.Remove(existingKey);
        }

        if (targets.Count == 0)
        {
            // Одна дежурная иконка, чтобы не терять доступ к меню, когда всё выключено
            SetOrUpdateTrayIcon("__EMPTY__", "Нет подключённых устройств (клик — проверить)", null, false, DeviceKind.Other);
            return;
        }

        foreach (DeviceBatteryInfo dev in targets)
        {
            string tip = dev.BatteryPercent.HasValue
                ? $"{KindToRu(dev.Kind)} {dev.Name}: {dev.BatteryPercent}%{(dev.IsCharging ? " (зарядка)" : "")}"
                : $"{KindToRu(dev.Kind)} {dev.Name}: {dev.StatusNote}";
            SetOrUpdateTrayIcon(dev.Id, tip, dev.BatteryPercent, dev.IsCharging, dev.Kind);
        }
    }

    private void SetOrUpdateTrayIcon(string key, string tooltip, int? pct, bool charging, DeviceKind kind)
    {
        if (!_trayIcons.TryGetValue(key, out NotifyIcon? ni))
        {
            ni = new NotifyIcon { Visible = true, ContextMenuStrip = _trayMenu };
            ni.DoubleClick += (_, _) => RestoreFromTray();
            ni.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _ = ScanAsync(); };
            _trayIcons[key] = ni;
        }

        ni.Text = tooltip.Length > 63 ? tooltip[..60] + "..." : tooltip;
        Icon newIcon = TrayIconRenderer.CreateBadgeIcon(pct, charging, kind);
        Icon? oldIcon = ni.Icon;
        ni.Icon = newIcon;
        oldIcon?.Dispose();
    }

    private void ClearTrayIcons()
    {
        foreach (NotifyIcon ni in _trayIcons.Values)
        {
            ni.Visible = false;
            ni.Icon?.Dispose();
            ni.Dispose();
        }
        _trayIcons.Clear();
    }

    private void RebuildTrayMenu(List<DeviceBatteryInfo> connected)
    {
        _trayMenu.Items.Clear();

        if (connected.Count == 0)
        {
            _trayMenu.Items.Add(new ToolStripMenuItem("Нет подключённых устройств") { Enabled = false });
        }
        else
        {
            foreach (DeviceBatteryInfo d in connected)
            {
                string pct = d.BatteryPercent.HasValue ? $"{d.BatteryPercent}%" : "--";
                var item = new ToolStripMenuItem($"[{KindToRu(d.Kind)}] {d.Name}: {pct}")
                {
                    Checked = !_chkSeparateTrayIcons.Checked && d.Id == _primaryDeviceId
                };
                item.Click += (_, _) =>
                {
                    _primaryDeviceId = d.Id;
                    SyncTrayIcons();
                };
                _trayMenu.Items.Add(item);
            }
        }

        _trayMenu.Items.Add(new ToolStripSeparator());

        var openItem = new ToolStripMenuItem("Открыть окно");
        openItem.Click += (_, _) => RestoreFromTray();
        _trayMenu.Items.Add(openItem);

        var refreshItem = new ToolStripMenuItem("Обновить сейчас");
        refreshItem.Click += async (_, _) => await ScanAsync();
        _trayMenu.Items.Add(refreshItem);

        var startupItem = new ToolStripMenuItem("Запускать с Windows") { Checked = StartupManager.IsEnabled() };
        startupItem.Click += (_, _) =>
        {
            bool next = !StartupManager.IsEnabled();
            StartupManager.SetEnabled(next);
            _chkStartup.Checked = next;
            RebuildTrayMenu(connected);
        };
        _trayMenu.Items.Add(startupItem);

        _trayMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => ExitApplication();
        _trayMenu.Items.Add(exitItem);
    }

    private void OnListSelectionChanged(object? sender, EventArgs e)
    {
        if (_deviceList.SelectedItems.Count == 0) return;
        if (_deviceList.SelectedItems[0].Tag is string id && id != _primaryDeviceId)
        {
            _primaryDeviceId = id;
            if (!_chkSeparateTrayIcons.Checked) SyncTrayIcons();
        }
    }

    private static string KindToRu(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "Мышь",
        DeviceKind.Keyboard => "Клавиатура",
        DeviceKind.Gamepad => "Геймпад",
        _ => "Устройство"
    };
}
