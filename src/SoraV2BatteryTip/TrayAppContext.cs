using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SoraV2BatteryTip;

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly Localizer _text;
    private readonly AlertSoundService _sound;
    private readonly BatteryHistoryStore _history;
    private readonly DiagnosticsExporter _diagnostics;
    private readonly BatteryCandidateCollector _candidateCollector;
    private readonly ProfileDraftImporter _draftImporter;
    private readonly KnownDeviceProfileProvider _profileProvider;
    private readonly BatteryProviderManager _providerManager;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly DeviceChangeWindow _deviceChangeWindow;
    private readonly Control _ui;
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    private AppSettings _settings;
    private BatteryReading? _lastReading;
    private IReadOnlyList<BatteryReading> _lastReadings = Array.Empty<BatteryReading>();
    private BatteryHistoryWindow? _historyWindow;
    private Icon? _dynamicIcon;
    private ToolStripMenuItem? _statusItem;
    private string _statusText = "SORA V2: not detected";
    private string? _lastTrayText;
    private string? _lastIconKey;
    private int? _lastBatteryPercentage;
    private int? _lastBatteryBucket;
    private bool _isDetected;
    private bool _isCableConnected;
    private bool _keepSoundMenuOpen;
    private DateTime _lastAlertUtc = DateTime.MinValue;
    private DateTime? _lastCheckLocal;
    private int? _lastAlertedBatteryLevel;
    private string _lastSource = "none";
    private string _lastFailureReason = "not_detected";

    public TrayAppContext()
    {
        _paths = new AppPaths();
        _paths.Ensure();
        _settingsStore = new SettingsStore(_paths);
        _settings = _settingsStore.Load();
        _text = new Localizer(() => _settings);
        _sound = new AlertSoundService(_paths, () => _settings);
        _history = new BatteryHistoryStore(_paths);
        _diagnostics = new DiagnosticsExporter(_paths, () => _settings, CreateDiagnosticsState);
        _profileProvider = new KnownDeviceProfileProvider(_paths);
        _candidateCollector = new BatteryCandidateCollector(_paths);
        _draftImporter = new ProfileDraftImporter(_paths, _profileProvider);
        _providerManager = new BatteryProviderManager(new IBatteryProvider[] { new NinjutsoSoraOfficialProvider(), new CompxBatteryProvider(), _profileProvider });

        if (_settings.StartupWithWindows)
            StartupManager.SetEnabled(true);

        _menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9F) };
        _menu.Closing += KeepSoundMenuOpenWhenPreviewing;
        BuildMenu();

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Sora V2 Battery Tip",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.MouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                await CheckNowAsync(clearOnMissing: true, updateUi: true);
            else if (e.Button == MouseButtons.Right)
                BuildMenu();
        };

        _pollTimer = new System.Windows.Forms.Timer();
        _pollTimer.Tick += async (_, _) => await CheckNowAsync(clearOnMissing: true, updateUi: true);

        _ui = new Control();
        _ui.CreateControl();
        _deviceChangeWindow = new DeviceChangeWindow(OnDeviceChanged);

        ApplyTimerInterval();
        _ = CheckNowAsync(clearOnMissing: true, updateUi: true);
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _statusItem = new ToolStripMenuItem(_statusText) { Enabled = false, ForeColor = _isCableConnected ? Color.ForestGreen : SystemColors.ControlText };
        _menu.Items.Add(_statusItem);
        if (_lastReadings.Count > 1)
        {
            foreach (var reading in _lastReadings)
                _menu.Items.Add(new ToolStripMenuItem(FormatDeviceReading(reading)) { Enabled = false, ForeColor = IsReadingCharging(reading) ? Color.ForestGreen : SystemColors.ControlText });
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(_text["CheckNow"], null, async (_, _) => await CheckNowAsync(clearOnMissing: true, updateUi: true)));
        if (!_isDetected)
            _menu.Items.Add(new ToolStripMenuItem(_text["AutoSetupUnknownMouse"], null, async (_, _) => await AutoSetupUnknownMouse()));
        _menu.Items.Add(new ToolStripMenuItem(_text["BatteryHistory"], null, (_, _) => ShowBatteryHistoryWindow()));
        _menu.Items.Add(new ToolStripMenuItem(_text["TestSound"], null, (_, _) => _sound.PlayCurrent()));
        _menu.Items.Add(BuildDeviceProfilesMenu());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem($"{_text["LastCheck"]}: {(_lastCheckLocal.HasValue ? _lastCheckLocal.Value.ToString("HH:mm:ss") : _text["Never"])}") { Enabled = false });
        _menu.Items.Add(new ToolStripMenuItem($"{_text["Source"]}: {_lastSource}") { Enabled = false });
        if (!string.IsNullOrWhiteSpace(_lastFailureReason))
            _menu.Items.Add(new ToolStripMenuItem($"{_text["FailureReason"]}: {LocalizeFailure(_lastFailureReason)}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(BuildNumberMenu(_text["Threshold"], _settings.AlertThreshold, new[] { 5, 10, 15, 20 }, "%", value => SaveSettings(settings => settings.AlertThreshold = value)));
        _menu.Items.Add(BuildNumberMenu(_text["Interval"], _settings.PollingIntervalMinutes, new[] { 5, 10, 15, 30 }, " min", value => SaveSettings(settings => settings.PollingIntervalMinutes = value)));
        _menu.Items.Add(BuildNumberMenu(_text["Cooldown"], _settings.AlertCooldownMinutes, new[] { 5, 10, 15, 30 }, " min", value => SaveSettings(settings => settings.AlertCooldownMinutes = value)));
        _menu.Items.Add(BuildSoundMenu());
        _menu.Items.Add(BuildLanguageMenu());

        var startup = new ToolStripMenuItem(_text["Startup"]) { Checked = _settings.StartupWithWindows };
        startup.Click += (_, _) =>
        {
            var enabled = !_settings.StartupWithWindows;
            SaveSettings(settings => settings.StartupWithWindows = enabled);
            StartupManager.SetEnabled(enabled);
        };
        _menu.Items.Add(startup);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(_text["Uninstall"], null, (_, _) => ConfirmUninstall()));
        _menu.Items.Add(new ToolStripMenuItem(_text["Exit"], null, (_, _) => ExitThread()));
    }

    private ToolStripMenuItem BuildNumberMenu(string label, int current, int[] values, string suffix, Action<int> setter)
    {
        var menu = new ToolStripMenuItem(label);
        foreach (var value in values)
        {
            var item = new ToolStripMenuItem($"{value}{suffix}") { Checked = value == current };
            item.Click += (_, _) => setter(value);
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private ToolStripMenuItem BuildDeviceProfilesMenu()
    {
        var statuses = _profileProvider.GetProfileStatus();
        var validCount = statuses.Count(status => status.IsValid);
        var invalidCount = statuses.Count(status => !status.IsValid);
        var menu = new ToolStripMenuItem(_text["DeviceProfiles"]);
        menu.DropDownItems.Add(new ToolStripMenuItem($"{_text["LoadedProfiles"]}: {validCount}") { Enabled = false });
        menu.DropDownItems.Add(new ToolStripMenuItem($"{_text["InvalidProfiles"]}: {invalidCount}") { Enabled = false });
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem(_text["OpenProfilesFolder"], null, (_, _) => _paths.OpenProfilesDirectory()));
        menu.DropDownItems.Add(new ToolStripMenuItem(_text["ReloadProfiles"], null, (_, _) => ReloadProfiles()));
        menu.DropDownItems.Add(new ToolStripMenuItem(_text["AutoSetupUnknownMouse"], null, async (_, _) => await AutoSetupUnknownMouse()));
        menu.DropDownItems.Add(new ToolStripMenuItem(_text["ImportLatestDrafts"], null, (_, _) => ImportLatestDrafts()));
        menu.DropDownItems.Add(new ToolStripMenuItem(_text["ExportDiagnostics"], null, (_, _) => ExportDiagnostics()));
        return menu;
    }

    private void ReloadProfiles()
    {
        _profileProvider.ReloadProfiles();
        try { _notifyIcon.ShowBalloonTip(1800, _text["DeviceProfiles"], _text["ProfilesReloaded"], ToolTipIcon.Info); }
        catch { }
        BuildMenu();
    }

    private void ImportLatestDrafts()
    {
        var result = _draftImporter.ImportLatestVerifiedDrafts();
        _profileProvider.ReloadProfiles();
        var message = result.Total == 0
            ? _text["NoDraftsFound"]
            : $"{_text[result.MessageKey]}: {result.Imported}/{result.Total}, rejected: {result.Rejected}";
        try { _notifyIcon.ShowBalloonTip(3000, _text["DeviceProfiles"], message, result.Imported > 0 ? ToolTipIcon.Info : ToolTipIcon.Warning); }
        catch { }
        BuildMenu();
    }

    private async Task AutoSetupUnknownMouse()
    {
        var officialBattery = PromptOfficialBatteryPercentage();
        if (!officialBattery.HasValue)
            return;

        string? dir = null;
        try
        {
            SetStatusText(_text["AutoSetupRunning"]);
            dir = _candidateCollector.Collect(officialBattery.Value);
            var result = _draftImporter.ImportVerifiedDraftsProgressive(dir);
            _profileProvider.ReloadProfiles();

            if (result.Imported > 0)
            {
                var readOk = await CheckNowAsync(clearOnMissing: true, updateUi: true);
                var message = readOk
                    ? $"{_text["AutoSetupSuccess"]}: {result.Imported}/{result.Total}, ±{result.ToleranceUsed}%"
                    : $"{_text["AutoSetupImportedButReadFailed"]}: {result.Imported}/{result.Total}, ±{result.ToleranceUsed}%";
                try { _notifyIcon.ShowBalloonTip(3000, _text["DeviceProfiles"], message, readOk ? ToolTipIcon.Info : ToolTipIcon.Warning); }
                catch { }
            }
            else
            {
                var message = $"{_text["AutoSetupFailed"]}: {result.Rejected}/{result.Total}";
                try { _notifyIcon.ShowBalloonTip(3500, _text["DeviceProfiles"], message, ToolTipIcon.Warning); }
                catch { }
                TryOpenDirectory(dir);
            }
        }
        catch
        {
            try { _notifyIcon.ShowBalloonTip(3500, _text["DeviceProfiles"], _text["AutoSetupFailed"], ToolTipIcon.Error); }
            catch { }
            if (!string.IsNullOrWhiteSpace(dir))
                TryOpenDirectory(dir);
        }
        finally
        {
            BuildMenu();
        }
    }

    private int? PromptOfficialBatteryPercentage()
    {
        using var form = new Form
        {
            Text = _text["AutoSetupUnknownMouse"],
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 128),
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        var label = new Label { Text = _text["OfficialBatteryPrompt"], AutoSize = false, Left = 14, Top = 16, Width = 390, Height = 24 };
        var input = new NumericUpDown { Left = 18, Top = 48, Width = 120, Minimum = 1, Maximum = 100, Value = Math.Clamp(_lastBatteryPercentage ?? 50, 1, 100) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 236, Top = 84, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 326, Top = 84, Width = 80 };
        form.Controls.Add(label);
        form.Controls.Add(input);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog() != DialogResult.OK)
            return null;

        return (int)input.Value;
    }

    private static void TryOpenDirectory(string directory)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true }); }
        catch { }
    }

    private ToolStripMenuItem BuildSoundMenu()
    {
        var menu = new ToolStripMenuItem(_text["Sound"]);
        menu.DropDown.Closing += KeepSoundMenuOpenWhenPreviewing;
        menu.DropDownItems.Add(BuildVolumeMenu());
        menu.DropDownItems.Add(new ToolStripSeparator());

        foreach (var sound in _sound.GetSounds())
        {
            var file = sound;
            var label = file.Equals("default.wav", StringComparison.OrdinalIgnoreCase) ? _text["Default"] : Path.GetFileNameWithoutExtension(file);
            var item = new ToolStripMenuItem(label) { Checked = file.Equals(_settings.AlertSoundFile, StringComparison.OrdinalIgnoreCase), Tag = file };
            item.Click += (_, _) =>
            {
                KeepSoundMenuOpenBriefly();
                SaveSettings(settings => settings.AlertSoundFile = file);
                RefreshSoundMenuChecks();
                _sound.PlayCurrent();
            };
            menu.DropDownItems.Add(item);
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem(_text["OpenFolder"], null, (_, _) => _sound.OpenFolder()));
        return menu;
    }

    private ToolStripMenuItem BuildVolumeMenu()
    {
        var menu = new ToolStripMenuItem($"{_text["Sound"]} {_settings.AlertVolume}%");
        menu.DropDown.Closing += KeepSoundMenuOpenWhenPreviewing;
        foreach (var volume in new[] { 15, 25, 50, 70, 85, 100 })
        {
            var value = volume;
            var item = new ToolStripMenuItem($"{value}%") { Checked = _settings.AlertVolume == value };
            item.Click += (_, _) =>
            {
                KeepSoundMenuOpenBriefly();
                SaveSettings(settings => settings.AlertVolume = value);
                RefreshSoundMenuChecks();
                _sound.PlayCurrent();
            };
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private ToolStripMenuItem BuildLanguageMenu()
    {
        var menu = new ToolStripMenuItem(_text["Language"]);
        foreach (var pair in new[] { ("Auto", "auto"), ("zh-CN", "zh-CN"), ("English", "en-US") })
        {
            var value = pair.Item2;
            var item = new ToolStripMenuItem(pair.Item1) { Checked = _settings.Language.Equals(value, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => SaveSettings(settings => settings.Language = value);
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private void SaveSettings(Action<AppSettings> update)
    {
        update(_settings);
        _settingsStore.Save(_settings);
        ApplyTimerInterval();
        RenderTrayState();
    }

    private void ShowBatteryHistoryWindow()
    {
        if (_historyWindow == null || _historyWindow.IsDisposed)
            _historyWindow = new BatteryHistoryWindow(_history, _text);
        else
            _historyWindow.Reload();

        _historyWindow.Show();
        _historyWindow.WindowState = FormWindowState.Normal;
        _historyWindow.Activate();
    }

    private void ExportDiagnostics()
    {
        var dir = _diagnostics.Export();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch { }
        try { _notifyIcon.ShowBalloonTip(2500, _text["DiagnosticsDone"], dir, ToolTipIcon.Info); }
        catch { }
    }

    private object CreateDiagnosticsState() => new
    {
        timestampLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        detected = _isDetected,
        cableConnected = _isCableConnected,
        batteryPercentage = _lastBatteryPercentage,
        batteryBucket = _lastBatteryBucket,
        lastCheckLocal = _lastCheckLocal?.ToString("yyyy-MM-dd HH:mm:ss"),
        source = _lastSource,
        failureReason = _lastFailureReason,
        builtInSoraV2Provider = "official-hid",
        providers = _providerManager.GetProviderStatus(),
        profiles = _profileProvider.GetProfileStatus(),
        readings = _lastReadings.Select(reading => new
        {
            reading.DeviceName,
            reading.VendorId,
            reading.ProductId,
            reading.BatteryPercentage,
            reading.IsCharging,
            reading.IsCableConnected,
            reading.Source
        }).ToArray(),
        processId = Environment.ProcessId
    };

    private async Task<bool> CheckNowAsync(bool clearOnMissing, bool updateUi)
    {
        if (!await _checkGate.WaitAsync(0))
            return false;

        try
        {
            if (updateUi)
                SetStatusText(_text["Checking"]);

            var result = await _providerManager.ReadAllAsync(CancellationToken.None);
            _lastCheckLocal = DateTime.Now;
            _lastSource = result.Source;
            _lastFailureReason = result.FailureReason;

            if (result.Readings.Count > 0)
            {
                ApplyReadings(result.Readings);
                foreach (var reading in result.Readings)
                    _history.Append("detected", reading);
                if (updateUi)
                    RenderTrayState();
                var alertReading = SelectAlertReading(result.Readings);
                if (alertReading != null)
                    MaybePlayAlert(alertReading);
                return true;
            }

            ApplyMissingReading(clearOnMissing);
            if (updateUi)
                RenderTrayState();
            return false;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private void ApplyReading(BatteryReading reading)
    {
        ApplyReadings(new[] { reading });
    }

    private void ApplyReadings(IReadOnlyList<BatteryReading> readings)
    {
        var validReadings = readings
            .Where(reading => reading.HasBatteryPercentage && reading.BatteryPercentage is >= 1 and <= 100)
            .OrderBy(reading => reading.BatteryPercentage)
            .ThenBy(reading => ShortDeviceName(reading))
            .ToArray();

        if (validReadings.Length == 0)
        {
            ApplyMissingReading(clearOnMissing: true);
            return;
        }

        var iconReading = SelectIconReading(validReadings);
        _lastReadings = validReadings;
        _lastReading = iconReading;
        _isDetected = true;
        _isCableConnected = validReadings.All(IsReadingCharging);
        _lastBatteryPercentage = iconReading.BatteryPercentage;
        _lastBatteryBucket = ToBatteryBucket(iconReading.BatteryPercentage);
    }

    private void ApplyMissingReading(bool clearOnMissing)
    {
        var connection = default(DeviceConnection);
        _isCableConnected = connection.IsCableConnected;
        _isDetected = connection.IsDetected || (!clearOnMissing && _lastBatteryBucket.HasValue);

        if (!_isDetected)
        {
            _lastReadings = Array.Empty<BatteryReading>();
            _lastReading = null;
            return;
        }

        if (_lastReading != null)
        {
            _lastReading = new BatteryReading
            {
                BatteryPercentage = _lastBatteryPercentage ?? _lastReading.BatteryPercentage,
                IsCharging = false,
                IsFullyCharged = false,
                IsOnline = connection.WirelessPresent,
                IsCableConnected = connection.IsCableConnected,
                Source = _lastReading.Source
            };
            _lastReadings = new[] { _lastReading };
        }
    }

    private void OnDeviceChanged(string devicePath, bool arrived)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return;

        if (devicePath.Contains("hid#", StringComparison.OrdinalIgnoreCase))
            _ = CheckNowAsync(clearOnMissing: true, updateUi: true);
    }

    private void RenderTrayState()
    {
        if (!_isDetected)
        {
            RenderNotDetected();
            return;
        }

        if (_lastFailureReason == "read_failed" && _lastBatteryPercentage.HasValue)
        {
            var text = $"SORA V2: {_lastBatteryPercentage.Value}% ({LocalizeFailure(_lastFailureReason)})";
            SetStatusText(text);
            SetTrayText(TrimTrayText(text));
            UpdateIcon(cableConnected: _isCableConnected, batteryBucket: _lastBatteryBucket ?? 100);
            return;
        }

        if (_lastReadings.Count > 1)
        {
            var text = $"{MouseCountLabel(_lastReadings.Count)}: {string.Join(" / ", _lastReadings.Select(FormatCompactReading))}";
            SetStatusText(text);
            SetTrayText(TrimTrayText(text));
            UpdateIcon(cableConnected: _isCableConnected, batteryBucket: _lastBatteryBucket ?? 100);
            return;
        }

        if (_isCableConnected)
        {
            var battery = _lastBatteryPercentage.HasValue ? $"{_lastBatteryPercentage.Value}% " : string.Empty;
            var text = $"SORA V2: {battery}{_text["Charging"]}";
            SetStatusText(text);
            SetTrayText(TrimTrayText(text));
            UpdateIcon(cableConnected: true, batteryBucket: _lastBatteryBucket ?? 100);
            return;
        }

        if (_lastBatteryBucket.HasValue)
        {
            var text = _lastBatteryPercentage.HasValue ? $"SORA V2: {_lastBatteryPercentage.Value}%" : "SORA V2";
            SetStatusText(text);
            SetTrayText(TrimTrayText(text));
            UpdateIcon(cableConnected: false, batteryBucket: _lastBatteryBucket.Value);
            return;
        }

        RenderNotDetected();
    }

    private void RenderNotDetected()
    {
        SetStatusText(_text["NotReady"]);
        SetTrayText("SORA V2: not detected");
        SetDefaultIcon();
    }

    private string LocalizeFailure(string reason)
    {
        var localized = _text[$"Failure_{reason}"];
        return localized == $"Failure_{reason}" ? reason : localized;
    }

    private void SetStatusText(string text)
    {
        _statusText = text;
        if (_statusItem is { IsDisposed: false })
        {
            _statusItem.Text = text;
            _statusItem.ForeColor = _isCableConnected ? Color.ForestGreen : SystemColors.ControlText;
        }
    }

    private void SetTrayText(string text)
    {
        if (string.Equals(_lastTrayText, text, StringComparison.Ordinal))
            return;
        _lastTrayText = text;
        _notifyIcon.Text = text;
    }

    private void PostRenderTrayState()
    {
        try
        {
            if (!_ui.IsDisposed && _ui.IsHandleCreated)
                _ui.BeginInvoke(RenderTrayState);
        }
        catch { }
    }

    private void MaybePlayAlert(BatteryReading reading)
    {
        if (!reading.HasBatteryPercentage || reading.BatteryPercentage <= 0)
            return;

        if (reading.IsCableConnected || reading.IsCharging || reading.IsFullyCharged || reading.BatteryPercentage > _settings.AlertThreshold)
        {
            _lastAlertedBatteryLevel = null;
            return;
        }

        var alertLevel = AlertLevelFor(reading.BatteryPercentage);
        if (_lastAlertedBatteryLevel.HasValue && alertLevel >= _lastAlertedBatteryLevel.Value)
            return;

        if (_lastAlertUtc != DateTime.MinValue && DateTime.UtcNow < _lastAlertUtc.AddMinutes(_settings.AlertCooldownMinutes))
            return;

        _lastAlertUtc = DateTime.UtcNow;
        _lastAlertedBatteryLevel = alertLevel;
        _sound.PlayCurrent();
    }

    private static int AlertLevelFor(int batteryPercentage) => Math.Clamp((batteryPercentage / 5) * 5, 0, 100);

    private void ApplyTimerInterval()
    {
        _pollTimer.Stop();
        _pollTimer.Interval = Math.Max(1, _settings.PollingIntervalMinutes) * 60 * 1000;
        _pollTimer.Start();
    }

    private void KeepSoundMenuOpenBriefly()
    {
        _keepSoundMenuOpen = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            try
            {
                if (!_ui.IsDisposed && _ui.IsHandleCreated)
                    _ui.BeginInvoke((MethodInvoker)(() => _keepSoundMenuOpen = false));
            }
            catch { }
        });
    }

    private void KeepSoundMenuOpenWhenPreviewing(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        if (_keepSoundMenuOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
            e.Cancel = true;
    }

    private void RefreshSoundMenuChecks()
    {
        foreach (ToolStripItem rootItem in _menu.Items)
        {
            if (rootItem is not ToolStripMenuItem root || root.Text != _text["Sound"])
                continue;

            foreach (ToolStripItem item in root.DropDownItems)
            {
                if (item is not ToolStripMenuItem child)
                    continue;

                if (child.DropDownItems.Count > 0)
                {
                    child.Text = $"{_text["Sound"]} {_settings.AlertVolume}%";
                    foreach (ToolStripItem volumeItem in child.DropDownItems)
                    {
                        if (volumeItem is ToolStripMenuItem volumeMenuItem && int.TryParse((volumeMenuItem.Text ?? string.Empty).TrimEnd('%'), out var volume))
                            volumeMenuItem.Checked = volume == _settings.AlertVolume;
                    }
                }
                else if (child.Tag is string soundFile)
                {
                    child.Checked = soundFile.Equals(_settings.AlertSoundFile, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    private void ConfirmUninstall()
    {
        var result = MessageBox.Show(_text["UninstallConfirm"], _text["UninstallTitle"], MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (result == DialogResult.Yes)
            BeginUninstall();
    }

    private void BeginUninstall()
    {
        try
        {
            _settings.StartupWithWindows = false;
            _settingsStore.Save(_settings);
            StartupManager.SetEnabled(false);
            CleanRegistryRecords();
            if (Directory.Exists(_paths.DataDirectory))
                Directory.Delete(_paths.DataDirectory, recursive: true);
        }
        catch { }

        try { _notifyIcon.ShowBalloonTip(2500, _text["UninstallStartedTitle"], _text["UninstallStarted"], ToolTipIcon.Info); }
        catch { }

        LaunchSelfCleanupAfterExit();
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            try
            {
                if (!_ui.IsDisposed && _ui.IsHandleCreated)
                    _ui.BeginInvoke((MethodInvoker)(ExitThread));
                else
                    ExitThread();
            }
            catch { ExitThread(); }
        });
    }

    private void LaunchSelfCleanupAfterExit()
    {
        var appDir = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!IsSafeAppDirectory(appDir))
            return;

        var script = "$ErrorActionPreference='SilentlyContinue';"
            + $"Wait-Process -Id {Environment.ProcessId} -Timeout 30;"
            + "Start-Sleep -Milliseconds 500;"
            + $"Remove-Item -LiteralPath '{appDir.Replace("'", "''")}' -Recurse -Force;";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        try
        {
            var needsElevation = IsUnderProgramFiles(appDir);
            var startInfo = new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = needsElevation,
                CreateNoWindow = !needsElevation,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            };
            if (needsElevation)
                startInfo.Verb = "runas";
            System.Diagnostics.Process.Start(startInfo);
        }
        catch { }
    }

    private static void CleanRegistryRecords()
    {
        DeleteRegistryValue(@"Software\Microsoft\Windows\CurrentVersion\Run", "SoraV2BatteryTip");
        DeleteRegistryValue(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "SoraV2BatteryTip");
        DeleteNotifyIconSettings();
    }

    private static void DeleteRegistryValue(string path, string valueName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch { }
    }

    private static void DeleteNotifyIconSettings()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (key == null)
                return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                var executablePath = subKey?.GetValue("ExecutablePath") as string ?? string.Empty;
                if (IsOurExecutablePath(executablePath))
                    key.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
            }
        }
        catch { }
    }

    private static bool IsOurExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;
        try
        {
            if (string.Equals(Path.GetFileName(executablePath), "SoraV2BatteryTip.exe", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        return executablePath.Contains("SoraV2BatteryTip", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeAppDirectory(string appDir)
    {
        if (string.IsNullOrWhiteSpace(appDir))
            return false;
        var root = Path.GetPathRoot(appDir);
        if (string.Equals(root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), appDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return false;
        return File.Exists(Path.Combine(appDir, "SoraV2BatteryTip.exe"));
    }

    private static bool IsUnderProgramFiles(string appDir)
    {
        return IsChildOf(appDir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
            || IsChildOf(appDir, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
    }

    private static bool IsChildOf(string child, string parent)
    {
        if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent))
            return false;
        var childFull = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var parentFull = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimTrayText(string text) => text.Length <= 63 ? text : text[..63];
    private static int ToBatteryBucket(int percentage) => Math.Clamp((percentage / 5) * 5, 5, 100);

    private BatteryReading SelectIconReading(IReadOnlyList<BatteryReading> readings)
    {
        return readings
            .Where(reading => !IsReadingCharging(reading))
            .OrderBy(reading => reading.BatteryPercentage)
            .FirstOrDefault()
            ?? readings.OrderBy(reading => reading.BatteryPercentage).First();
    }

    private static BatteryReading? SelectAlertReading(IReadOnlyList<BatteryReading> readings)
    {
        return readings
            .Where(reading => !IsReadingCharging(reading))
            .OrderBy(reading => reading.BatteryPercentage)
            .FirstOrDefault();
    }

    private static bool IsReadingCharging(BatteryReading reading)
    {
        return reading.IsCableConnected || reading.IsCharging || reading.IsFullyCharged;
    }

    private string MouseCountLabel(int count)
    {
        return _settings.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? $"{count} mice"
            : $"{count} 个鼠标";
    }

    private string FormatCompactReading(BatteryReading reading)
    {
        return IsReadingCharging(reading)
            ? $"{reading.BatteryPercentage}% {_text["Charging"]}"
            : $"{reading.BatteryPercentage}%";
    }

    private string FormatDeviceReading(BatteryReading reading)
    {
        return $"{ShortDeviceName(reading)}: {FormatCompactReading(reading)}";
    }

    private static string ShortDeviceName(BatteryReading reading)
    {
        var name = string.IsNullOrWhiteSpace(reading.DeviceName) ? reading.Source : reading.DeviceName;
        name = name
            .Replace("Wireless mouse", "Mouse", StringComparison.OrdinalIgnoreCase)
            .Replace("NANO dongle", "Dongle", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return name.Length <= 34 ? name : name[..34].TrimEnd();
    }

    private void UpdateIcon(bool cableConnected, int batteryBucket)
    {
        var key = cableConnected ? "plugged" : $"battery:{batteryBucket}";
        if (string.Equals(_lastIconKey, key, StringComparison.Ordinal))
            return;

        _lastIconKey = key;
        var icon = CreateBatteryIcon(cableConnected ? 100 : batteryBucket, cableConnected);
        var previous = _dynamicIcon;
        _dynamicIcon = icon;
        _notifyIcon.Icon = icon;
        previous?.Dispose();
    }

    private void SetDefaultIcon()
    {
        if (string.Equals(_lastIconKey, "default", StringComparison.Ordinal))
            return;

        _lastIconKey = "default";
        var icon = LoadAppIcon();
        var previous = _dynamicIcon;
        _dynamicIcon = icon;
        _notifyIcon.Icon = icon;
        previous?.Dispose();
    }

    private static Icon CreateBatteryIcon(int batteryBucket, bool cableConnected)
    {
        const int size = 64;
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var body = new Rectangle(8, 19, 43, 27);
        var tip = new Rectangle(51, 27, 5, 11);
        var inner = new Rectangle(13, 24, 33, 17);
        using var outline = new Pen(Color.White, 4f);
        using var shadow = new Pen(Color.FromArgb(150, 0, 0, 0), 5f);
        g.DrawRoundedRectangle(shadow, body, 6);
        g.DrawRoundedRectangle(outline, body, 6);
        g.DrawRectangle(outline, tip);

        var fillColor = cableConnected
            ? Color.FromArgb(34, 197, 94)
            : batteryBucket <= 10
                ? Color.FromArgb(239, 68, 68)
                : batteryBucket <= 20
                    ? Color.FromArgb(245, 158, 11)
                    : Color.FromArgb(235, 238, 242);
        var fillWidth = Math.Max(2, (int)Math.Round(inner.Width * Math.Clamp(batteryBucket / 100d, 0.03d, 1d)));
        using var fill = new SolidBrush(fillColor);
        g.FillRoundedRectangle(fill, new Rectangle(inner.X, inner.Y, fillWidth, inner.Height), 3);

        if (cableConnected)
            DrawBolt(g, fillColor);
        DrawBars(g, batteryBucket);
        return ToIcon(bitmap);
    }

    private static void DrawBolt(Graphics g, Color color)
    {
        var points = new[] { new Point(34, 13), new Point(24, 34), new Point(33, 34), new Point(28, 51), new Point(43, 28), new Point(34, 28) };
        using var white = new SolidBrush(Color.White);
        using var pen = new Pen(color, 1.5f);
        g.FillPolygon(white, points);
        g.DrawPolygon(pen, points);
    }

    private static void DrawBars(Graphics g, int percentage)
    {
        var bars = Math.Clamp((int)Math.Ceiling(percentage / 25d), 1, 4);
        using var brush = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
        for (var i = 0; i < bars; i++)
            g.FillRectangle(brush, 15 + i * 7, 48, 4, 4);
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static Icon LoadAppIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "SoraV2BatteryTip.ico");
        return File.Exists(path) ? new Icon(path) : SystemIcons.Application;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _deviceChangeWindow.Dispose();
            _ui.Dispose();
            _pollTimer.Dispose();
            _menu.Dispose();
            _historyWindow?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _dynamicIcon?.Dispose();
            _checkGate.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

internal sealed class DeviceChangeWindow : NativeWindow, IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private readonly Action<string, bool> _onChange;
    private IntPtr _notificationHandle;

    public DeviceChangeWindow(Action<string, bool> onChange)
    {
        _onChange = onChange;
        CreateHandle(new CreateParams());
        RegisterHidNotifications();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE)
        {
            var eventType = m.WParam.ToInt32();
            if ((eventType == DBT_DEVICEARRIVAL || eventType == DBT_DEVICEREMOVECOMPLETE) && m.LParam != IntPtr.Zero)
            {
                var header = Marshal.PtrToStructure<DevBroadcastHeader>(m.LParam);
                if (header.DeviceType == DBT_DEVTYP_DEVICEINTERFACE)
                {
                    var namePtr = IntPtr.Add(m.LParam, 28);
                    var path = Marshal.PtrToStringUni(namePtr) ?? string.Empty;
                    _onChange(path, eventType == DBT_DEVICEARRIVAL);
                }
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_notificationHandle != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_notificationHandle);
            _notificationHandle = IntPtr.Zero;
        }
        DestroyHandle();
    }

    private void RegisterHidNotifications()
    {
        var filter = new DevBroadcastDeviceInterface
        {
            Size = Marshal.SizeOf<DevBroadcastDeviceInterface>(),
            DeviceType = DBT_DEVTYP_DEVICEINTERFACE,
            Reserved = 0,
            ClassGuid = HidInterfaceGuid
        };
        _notificationHandle = RegisterDeviceNotification(Handle, ref filter, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastHeader
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastDeviceInterface
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DevBroadcastDeviceInterface notificationFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
}

internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangle(this Graphics g, Pen pen, Rectangle bounds, int radius)
    {
        using var path = Rounded(bounds, radius);
        g.DrawPath(pen, path);
    }

    public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle bounds, int radius)
    {
        using var path = Rounded(bounds, radius);
        g.FillPath(brush, path);
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
