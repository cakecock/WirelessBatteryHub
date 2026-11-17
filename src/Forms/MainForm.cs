using Microsoft.Win32;
using WirelessBatteryHub.Models;
using WirelessBatteryHub.Services;
using WinTimer = System.Windows.Forms.Timer;

namespace WirelessBatteryHub.Forms;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly ProtocolAutoEngine _engine = new();
    private readonly BatteryEstimator _estimator = new();
    private readonly Dictionary<string, string?> _estimates = new(StringComparer.OrdinalIgnoreCase);
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
    private readonly ComboBox _cmbTrayStyle;
    private readonly ComboBox _cmbThemeMode;
    private readonly Button _btnRefresh;
    private readonly Button _btnAnalyze;

    private bool _allowShowWindow;
    private bool _isExiting;
    private bool _isScanning;
    private bool _suppressSettingsSave;
    private bool _isDarkUi;
    private List<DeviceBatteryInfo> _devices = new();

    public MainForm(bool startMinimized, EventWaitHandle showWindowEvent)
    {
        _settings = AppSettings.Load();
        _allowShowWindow = !startMinimized;

        Text = "Wireless Battery Hub — Мыши, Клавиатуры, Геймпады, Гарнитуры";
        Size = new Size(730, 535);
        MinimumSize = new Size(630, 440);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = TrayIconRenderer.CreateAppWindowIcon();

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
            Tag = "muted",
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
        _deviceList.Columns.Add("Устройство", 220);
        _deviceList.Columns.Add("Тип", 95);
        _deviceList.Columns.Add("Протокол", 160);
        _deviceList.Columns.Add("Заряд", 65);
        _deviceList.Columns.Add("Статус", 150);
        _deviceList.SelectedIndexChanged += OnListSelectionChanged;
        _deviceList.DrawColumnHeader += OnDrawListColumnHeader;
        _deviceList.DrawItem += (_, e) => e.DrawDefault = true;
        _deviceList.DrawSubItem += (_, e) => e.DrawDefault = true;
        _listContextMenu.Opening += OnListContextOpening;

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 196, Padding = new Padding(12, 6, 12, 8) };

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

        var appearanceRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0)
        };

        var lblStyle = new Label { Text = "Значок:", AutoSize = true, Margin = new Padding(3, 6, 4, 0) };
        _cmbTrayStyle = new ComboBox { Width = 148, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbTrayStyle.Items.AddRange(new object[] { "Бейдж (фон + полосы)", "Крупные цифры", "Батарейка" });
        _cmbTrayStyle.SelectedIndex = Math.Clamp((int)_settings.TrayStyle, 0, 2);
        _cmbTrayStyle.SelectedIndexChanged += (_, _) =>
        {
            SaveUiSettings();
            SyncTrayIcons();
        };

        var lblTheme = new Label { Text = "Тема:", AutoSize = true, Margin = new Padding(12, 6, 4, 0) };
        _cmbThemeMode = new ComboBox { Width = 128, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbThemeMode.Items.AddRange(new object[] { "Как в Windows", "Тёмная", "Светлая" });
        _cmbThemeMode.SelectedIndex = Math.Clamp((int)_settings.ThemeMode, 0, 2);
        _cmbThemeMode.SelectedIndexChanged += (_, _) =>
        {
            SaveUiSettings();
            ApplyTheme();
        };

        appearanceRow.Controls.AddRange(new Control[] { lblStyle, _cmbTrayStyle, lblTheme, _cmbThemeMode });

        settingsFlow.Controls.AddRange(new Control[]
        {
            _chkStartup, _chkMinimizeToTray, _chkSeparateTrayIcons, _chkRareTimer, notifyRow, appearanceRow
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
            using var dlg = new AnalyzerForm(_isDarkUi);
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
        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;

        ApplyTheme();
        SyncTrayIcons();
        _ = ScanAsync();
    }

    private void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            ApplyTheme();
            SyncTrayIcons();
        });
    }

    private void ApplyTheme()
    {
        _isDarkUi = ThemeHelper.ShouldUseDarkTheme(_settings.ThemeMode);
        ThemeHelper.ApplyTitleBarTheme(Handle, _isDarkUi);
        ThemeHelper.ApplyControlTreeTheme(this, _isDarkUi);
        UpdateListColors();
    }

    private void OnDrawListColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bg = new SolidBrush(Color.FromArgb(42, 42, 52));
        using var border = new Pen(Color.FromArgb(65, 65, 80));
        using var textBrush = new SolidBrush(Color.FromArgb(230, 232, 240));
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };

        e.Graphics.FillRectangle(bg, e.Bounds);
        e.Graphics.DrawLine(border, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
        e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

        var rect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 10), e.Bounds.Height);
        e.Graphics.DrawString(e.Header?.Text ?? "", _deviceList.Font, textBrush, rect, sf);
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
        _settings.TrayStyle = (TrayIconStyle)Math.Clamp(_cmbTrayStyle.SelectedIndex, 0, 2);
        _settings.ThemeMode = (AppThemeMode)Math.Clamp(_cmbThemeMode.SelectedIndex, 0, 2);
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
        SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
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

            foreach (DeviceBatteryInfo d in _devices)
                _estimates[d.Id] = _estimator.RecordAndEstimate(d);

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

            string statusText = _estimates.TryGetValue(d.Id, out string? est) && est != null
                ? $"{d.StatusNote} ({est})"
                : d.StatusNote;
            item.SubItems.Add(statusText);

            if (d.Id == _settings.PrimaryDeviceId) item.Selected = true;
            _deviceList.Items.Add(item);
        }
        UpdateListColors();
        _deviceList.EndUpdate();

        _lblHeadline.Text = visibleConnected.Count == 0
            ? "Подключённых беспроводных устройств нет"
            : string.Join("  |  ", visibleConnected.Take(3).Select(d =>
                $"{GetDisplayName(d)}: {(d.BatteryPercent.HasValue ? d.BatteryPercent + "%" : "--")}"));

        SyncTrayIcons();
    }

    private void UpdateListColors()
    {
        foreach (ListViewItem item in _deviceList.Items)
        {
            if (item.Tag is not string id) continue;
            DeviceBatteryInfo? d = _devices.FirstOrDefault(x => x.Id == id);
            if (d == null) continue;

            bool hidden = _settings.HiddenDeviceIds.Contains(d.Id);
            if (!d.IsConnected || hidden)
                item.ForeColor = _isDarkUi ? Color.FromArgb(120, 122, 132) : Color.Gray;
            else if (d.IsSleeping)
                item.ForeColor = _isDarkUi ? Color.FromArgb(160, 165, 175) : Color.DimGray;
            else
                item.ForeColor = _isDarkUi ? Color.FromArgb(236, 236, 242) : SystemColors.WindowText;
        }
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
            string estPart = _estimates.TryGetValue(dev.Id, out string? est) && est != null ? $" · {est}" : "";
            string tip = dev.BatteryPercent.HasValue
                ? $"{KindToRu(dev.Kind)} {name}: {dev.BatteryPercent}%{estPart}" +
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
        bool darkTaskbar = ThemeHelper.IsWindowsDarkTaskbar();
        Icon newIcon = TrayIconRenderer.CreateBadgeIcon(pct, charging, kind, isSleeping, _settings.TrayStyle, darkTaskbar);
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
                GetDisplayName(dev),
                _isDarkUi);
            if (input == null) return;

            if (string.IsNullOrWhiteSpace(input))
                _settings.CustomNames.Remove(dev.Id);
            else
                _settings.CustomNames[dev.Id] = input.Trim();

            _settings.Save();
            UpdateUi();
        };

        bool isHidden = _settings.HiddenDeviceIds.Contains(dev.Id);
        var hideItem = new ToolStripMenuItem(isHidden ? "Показывать в трее" : "Скрыть из трея");
        hideItem.Click += (_, _) =>
        {
            if (isHidden) _settings.HiddenDeviceIds.Remove(dev.Id);
            else _settings.HiddenDeviceIds.Add(dev.Id);
            _settings.Save();
            UpdateUi();
        };

        _listContextMenu.Items.Add(renameItem);
        _listContextMenu.Items.Add(hideItem);
    }

    private void OnListSelectionChanged(object? sender, EventArgs e)
    {
        if (_deviceList.SelectedItems.Count == 0) return;
        if (_deviceList.SelectedItems[0].Tag is string id && id != _settings.PrimaryDeviceId)
        {
            _settings.PrimaryDeviceId = id;
            _settings.Save();
            if (!_chkSeparateTrayIcons.Checked) SyncTrayIcons();
        }
    }

    private static string? PromptInput(string title, string prompt, string defaultVal, bool dark)
    {
        using var form = new Form
        {
            Width = 380,
            Height = 155,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var lbl = new Label { Left = 14, Top = 14, Width = 335, Text = prompt };
        var txt = new TextBox { Left = 14, Top = 40, Width = 335, Text = defaultVal };
        var btnOk = new Button { Text = "ОК", Left = 190, Width = 75, Top = 76, DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "Отмена", Left = 274, Width = 75, Top = 76, DialogResult = DialogResult.Cancel };

        form.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        ThemeHelper.ApplyTitleBarTheme(form.Handle, dark);
        ThemeHelper.ApplyControlTreeTheme(form, dark);

        return form.ShowDialog() == DialogResult.OK ? txt.Text : null;
    }

    private static string KindToRu(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "Мышь",
        DeviceKind.Keyboard => "Клавиатура",
        DeviceKind.Gamepad => "Геймпад",
        DeviceKind.Headset => "Гарнитура",
        _ => "Устройство"
    };
}