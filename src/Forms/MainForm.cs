using WirelessBatteryHub.Models;
using WirelessBatteryHub.Services;
using WinTimer = System.Windows.Forms.Timer;

namespace WirelessBatteryHub.Forms;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly ProtocolAutoEngine _engine = new();
    private readonly DeviceArrivalWatcher _watcher;
    private readonly Dictionary<string, NotifyIcon> _trayIcons = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ContextMenuStrip _listContextMenu = new();
    private readonly RegisteredWaitHandle _showWaitHandle;
    private readonly WinTimer _rareRefreshTimer;

    private readonly HashSet<string> _lowNotified = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fullNotified = new(StringComparer.OrdinalIgnoreCase);

    private readonly ListView _deviceList;
    private readonly Label _lblHeadline;
    private readonly Label _lblSub;
    private readonly CheckBox _chkStartup;
    private readonly CheckBox _chkMinimizeToTray;
    private readonly CheckBox _chkSeparateTrayIcons;
    private readonly CheckBox _chkRareTimer;
    private readonly CheckBox _chkNotifyLow;
    private readonly NumericUpDown _numLowThreshold;
    private readonly CheckBox _chkNotifyFull;
    private readonly Button _btnRefresh;
    private readonly Button _btnAnalyze;

    private bool _allowShowWindow;
    private bool _isExiting;
    private bool _isScanning;
    private bool _suppressSettingsSave;
    private List<DeviceBatteryInfo> _devices = new();

    public MainForm(bool startMinimized, EventWaitHandle showWindowEvent)
    {
        _settings = AppSettings.Load();
        _allowShowWindow = !startMinimized;

        Text = "Wireless Battery Hub — Мыши, Клавиатуры, Геймпады";
        Size = new Size(710, 500);
        MinimumSize = new Size(600, 420);
        StartPosition = FormStartPosition.CenterScreen;

        _ = Handle;

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
            Text = "Событийный режим (0% фоновой нагрузки) · Правый клик по строке: переименовать или скрыть",
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
            MultiSelect = false,
            ContextMenuStrip = _listContextMenu
        };
        _deviceList.Columns.Add("Устройство", 225);
        _deviceList.Columns.Add("Тип", 95);
        _deviceList.Columns.Add("Протокол", 160);
        _deviceList.Columns.Add("Заряд", 70);
        _deviceList.Columns.Add("Статус", 125);
        _deviceList.SelectedIndexChanged += OnListSelectionChanged;
        _listContextMenu.Opening += OnListContextOpening;

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 162, Padding = new Padding(12, 6, 12, 8) };

        var settingsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = false
        };

        _suppressSettingsSave = true;

        _chkStartup = new CheckBox
        {
            Text = "Запускать вместе с Windows (свёрнуто в трей)",
            AutoSize = true,
            Checked = StartupManager.IsEnabled()
        };
        _chkStartup.CheckedChanged += (_, _) => StartupManager.SetEnabled(_chkStartup.Checked);

        _chkMinimizeToTray = new CheckBox
        {
            Text = "Сворачивать в трей при закрытии окна",
            AutoSize = true,
            Checked = _settings.MinimizeToTray
        };
        _chkMinimizeToTray.CheckedChanged += (_, _) => SaveUiSettings();

        _chkSeparateTrayIcons = new CheckBox
        {
            Text = "Отдельный значок в трее для каждого подключённого устройства",
            AutoSize = true,
            Checked = _settings.SeparateTrayIcons
        };
        _chkSeparateTrayIcons.CheckedChanged += (_, _) =>
        {
            SaveUiSettings();
            SyncTrayIcons();
        };

        _chkRareTimer = new CheckBox
        {
            Text = "Подтягивать % заряда раз в 2 минуты (выкл = 0% нагрузки)",
            AutoSize = true,
            Checked = _settings.EnableRareTimer
        };
        _chkRareTimer.CheckedChanged += (_, _) =>
        {
            if (_chkRareTimer.Checked) _rareRefreshTimer.Start();
            else _rareRefreshTimer.Stop();
            SaveUiSettings();
        };

        var notifyRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 0)
        };

        _chkNotifyLow = new CheckBox
        {
            Text = "Уведомлять при ≤",
            AutoSize = true,
            Checked = _settings.NotifyLowBattery,
            Margin = new Padding(3, 4, 2, 0)
        };
        _chkNotifyLow.CheckedChanged += (_, _) => SaveUiSettings();

        _numLowThreshold = new NumericUpDown
        {
            Minimum = 5,
            Maximum = 50,
            Width = 48,
            Value = Math.Clamp(_settings.LowBatteryThreshold, 5, 50),
            Margin = new Padding(2, 2, 2, 0)
        };
        _numLowThreshold.ValueChanged += (_, _) => SaveUiSettings();

        _chkNotifyFull = new CheckBox
        {
            Text = "% и при полной зарядке (100%)",
            AutoSize = true,
            Checked = _settings.NotifyFullCharge,
            Margin = new Padding(2, 4, 0, 0)
        };
        _chkNotifyFull.CheckedChanged += (_, _) => SaveUiSettings();

        notifyRow.Controls.AddRange(new Control[] { _chkNotifyLow, _numLowThreshold, _chkNotifyFull });
        settingsFlow.Controls.AddRange(new Control[]
        {
            _chkStartup, _chkMinimizeToTray, _chkSeparateTrayIcons, _chkRareTimer, notifyRow
        });

        var actionsPanel = new Panel { Dock = DockStyle.Right, Width = 165 };

        _btnRefresh = new Button
        {
            Text = "Обновить сейчас",
            Size = new Size(150, 34),
            Location = new Point(10, 14)
        };
        _btnRefresh.Click += async (_, _) => await ScanAsync();

        _btnAnalyze = new Button
        {
            Text = "HID-Анализатор",
            Size = new Size(150, 34),
            Location = new Point(10, 56)
        };
        _btnAnalyze.Click += (_, _) =>
        {
            using var dlg = new AnalyzerForm();
            dlg.ShowDialog(this);
            _ = ScanAsync();
        };

        actionsPanel.Controls.AddRange(new Control[] { _btnRefresh, _btnAnalyze });
        bottomPanel.Controls.Add(settingsFlow);
        bottomPanel.Controls.Add(actionsPanel);

        Controls.Add(_deviceList);
        Controls.Add(_lblSub);
        Controls.Add(_lblHeadline);
        Controls.Add(bottomPanel);

        _suppressSettingsSave = false;

        _rareRefreshTimer = new WinTimer { Interval = 120_000 };
        _rareRefreshTimer.Tick += async (_, _) => await ScanAsync();
        if (_settings.EnableRareTimer) _rareRefreshTimer.Start();

        _watcher = new DeviceArrivalWatcher(Handle, () => _ = ScanAsync());

        SyncTrayIcons();
        _ = ScanAsync();
    }

    private void SaveUiSettings()
    {
        if (_suppressSettingsSave) return;
        _settings.MinimizeToTray = _chkMinimizeToTray.Checked;
        _settings.SeparateTrayIcons = _chkSeparateTrayIcons.Checked;
        _settings.EnableRareTimer = _chkRareTimer.Checked;
        _settings.NotifyLowBattery = _chkNotifyLow.Checked;
        _settings.LowBatteryThreshold = (int)_numLowThreshold.Value;
        _settings.NotifyFullCharge = _chkNotifyFull.Checked;
        _settings.Save();
    }

    private string GetDisplayName(DeviceBatteryInfo d) =>
        _settings.CustomNames.TryGetValue(d.Id, out string? custom) && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : d.Name;

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
                .ThenBy(d => _settings.HiddenDeviceIds.Contains(d.Id))
                .ThenBy(d => d.Kind)
                .ThenBy(GetDisplayName)
                .ToList();

            UpdateUi();
            CheckBatteryNotifications();
        }
        finally
        {
            _isScanning = false;
            _btnRefresh.Enabled = true;
        }
    }

    private void UpdateUi()
    {
        List<DeviceBatteryInfo> visibleConnected = _devices
            .Where(d => d.IsConnected && !_settings.HiddenDeviceIds.Contains(d.Id))
            .ToList();

        if (visibleConnected.Count > 0 &&
            (_settings.PrimaryDeviceId == null || visibleConnected.All(d => d.Id != _settings.PrimaryDeviceId)))
        {
            _settings.PrimaryDeviceId = (visibleConnected.FirstOrDefault(d => d.Kind == DeviceKind.Mouse) ?? visibleConnected[0]).Id;
            _settings.Save();
        }

        _deviceList.BeginUpdate();
        _deviceList.Items.Clear();
        foreach (DeviceBatteryInfo d in _devices)
        {
            bool hidden = _settings.HiddenDeviceIds.Contains(d.Id);
            string displayName = GetDisplayName(d) + (hidden ? " (скрыто)" : "");
            var item = new ListViewItem(displayName) { Tag = d.Id };
            item.SubItems.Add(KindToRu(d.Kind));
            item.SubItems.Add(d.ConnectionType);
            item.SubItems.Add(d.BatteryPercent.HasValue ? $"{d.BatteryPercent}%" : "--");
            item.SubItems.Add(d.StatusNote);

            if (!d.IsConnected || hidden) item.ForeColor = Color.Gray;
            else if (d.IsSleeping) item.ForeColor = Color.DimGray;

            if (d.Id == _settings.PrimaryDeviceId) item.Selected = true;
            _deviceList.Items.Add(item);
        }
        _deviceList.EndUpdate();

        _lblHeadline.Text = visibleConnected.Count == 0
            ? "Подключённых беспроводных устройств нет"
            : string.Join("  |  ", visibleConnected.Take(3).Select(d =>
                $"{GetDisplayName(d)}: {(d.BatteryPercent.HasValue ? d.BatteryPercent + "%" : "--")}"));

        SyncTrayIcons();
    }

    private void SyncTrayIcons()
    {
        List<DeviceBatteryInfo> visibleConnected = _devices
            .Where(d => d.IsConnected && !_settings.HiddenDeviceIds.Contains(d.Id))
            .ToList();

        RebuildTrayMenu(visibleConnected);

        List<DeviceBatteryInfo> targets;
        if (visibleConnected.Count == 0)
        {
            targets = new List<DeviceBatteryInfo>();
        }
        else if (_chkSeparateTrayIcons.Checked)
        {
            targets = visibleConnected;
        }
        else
        {
            DeviceBatteryInfo primary = visibleConnected.FirstOrDefault(d => d.Id == _settings.PrimaryDeviceId)
                                     ?? visibleConnected[0];
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
            SetOrUpdateTrayIcon("__EMPTY__", "Нет подключённых устройств (клик — проверить)", null, false, DeviceKind.Other, false);
            return;
        }

        foreach (DeviceBatteryInfo dev in targets)
        {
            string name = GetDisplayName(dev);
            string tip = dev.BatteryPercent.HasValue
                ? $"{KindToRu(dev.Kind)} {name}: {dev.BatteryPercent}%" +
                  (dev.IsCharging ? " (зарядка)" : dev.IsSleeping ? " (спит)" : "")
                : $"{KindToRu(dev.Kind)} {name}: {dev.StatusNote}";

            SetOrUpdateTrayIcon(dev.Id, tip, dev.BatteryPercent, dev.IsCharging, dev.Kind, dev.IsSleeping);
        }
    }

    private void CheckBatteryNotifications()
    {
        NotifyIcon? anchor = _trayIcons.Values.FirstOrDefault();
        if (anchor == null) return;

        foreach (DeviceBatteryInfo d in _devices)
        {
            if (!d.IsConnected || d.IsSleeping || !d.BatteryPercent.HasValue) continue;
            if (_settings.HiddenDeviceIds.Contains(d.Id)) continue;

            int pct = d.BatteryPercent.Value;
            string name = GetDisplayName(d);

            // Низкий заряд
            if (_settings.NotifyLowBattery && !d.IsCharging && pct <= _settings.LowBatteryThreshold)
            {
                if (_lowNotified.Add(d.Id))
                {
                    anchor.ShowBalloonTip(
                        5000,
                        "Низкий заряд устройства",
                        $"{KindToRu(d.Kind)} «{name}»: осталось {pct}%. Пора поставить на зарядку.",
                        ToolTipIcon.Warning);
                }
            }
            else if (pct > _settings.LowBatteryThreshold + 5 || d.IsCharging)
            {
                _lowNotified.Remove(d.Id);
            }

            // Полная зарядка
            if (_settings.NotifyFullCharge && d.IsCharging && pct >= 100)
            {
                if (_fullNotified.Add(d.Id))
                {
                    anchor.ShowBalloonTip(
                        4000,
                        "Зарядка завершена",
                        $"{KindToRu(d.Kind)} «{name}» заряжен(а) на 100%.",
                        ToolTipIcon.Info);
                }
            }
            else if (pct < 95)
            {
                _fullNotified.Remove(d.Id);
            }
        }
    }

    private void SetOrUpdateTrayIcon(string key, string tooltip, int? pct, bool charging, DeviceKind kind, bool isSleeping)
    {
        if (!_trayIcons.TryGetValue(key, out NotifyIcon? ni))
        {
            ni = new NotifyIcon { Visible = true, ContextMenuStrip = _trayMenu };
            ni.DoubleClick += (_, _) => RestoreFromTray();
            ni.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _ = ScanAsync(); };
            _trayIcons[key] = ni;
        }

        ni.Text = tooltip.Length > 63 ? tooltip[..60] + "..." : tooltip;
        Icon newIcon = TrayIconRenderer.CreateBadgeIcon(pct, charging, kind, isSleeping);
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

    private void RebuildTrayMenu(List<DeviceBatteryInfo> visibleConnected)
    {
        _trayMenu.Items.Clear();

        if (visibleConnected.Count == 0)
        {
            _trayMenu.Items.Add(new ToolStripMenuItem("Нет подключённых устройств") { Enabled = false });
        }
        else
        {
            foreach (DeviceBatteryInfo d in visibleConnected)
            {
                string pct = d.BatteryPercent.HasValue ? $"{d.BatteryPercent}%" : "--";
                string suffix = d.IsSleeping ? " · спит" : d.IsCharging ? " · зарядка" : "";
                var item = new ToolStripMenuItem($"[{KindToRu(d.Kind)}] {GetDisplayName(d)}: {pct}{suffix}")
                {
                    Checked = !_chkSeparateTrayIcons.Checked && d.Id == _settings.PrimaryDeviceId
                };
                item.Click += (_, _) =>
                {
                    _settings.PrimaryDeviceId = d.Id;
                    _settings.Save();
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
            RebuildTrayMenu(visibleConnected);
        };
        _trayMenu.Items.Add(startupItem);

        _trayMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => ExitApplication();
        _trayMenu.Items.Add(exitItem);
    }

    private void OnListContextOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _listContextMenu.Items.Clear();
        if (_deviceList.SelectedItems.Count == 0 || _deviceList.SelectedItems[0].Tag is not string id)
        {
            e.Cancel = true;
            return;
        }

        DeviceBatteryInfo? dev = _devices.FirstOrDefault(d => d.Id == id);
        if (dev == null)
        {
            e.Cancel = true;
            return;
        }

        var renameItem = new ToolStripMenuItem("Переименовать...");
        renameItem.Click += (_, _) =>
        {
            string? input = PromptInput("Переименовать устройство",
                "Введите короткое имя (оставьте пустым для сброса):",
                GetDisplayName(dev));
            if (input == null) return;

            if (string.IsNullOrWhiteSpace(input))
          
