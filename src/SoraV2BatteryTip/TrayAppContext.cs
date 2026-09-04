using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SoraV2BatteryTip;

internal sealed class TrayAppContext : ApplicationContext
{
    private const int ChargingPollingIntervalMilliseconds = 10_000;
    private const int SteadyChargingPollingIntervalMilliseconds = 60_000;
    private const int FullChargePollingIntervalMilliseconds = 5 * 60 * 1000;
    private static readonly TimeSpan ChargingFastWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DeviceChangeQuietPeriod = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DeviceChangeMaximumCoalesce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan[] DeviceReadRetryDelays =
    {
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1)
    };
    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly Localizer _text;
    private readonly AlertSoundService _sound;
    private readonly BatteryHistoryStore _history;
    private readonly DiagnosticsExporter _diagnostics;
    private readonly BatteryCandidateCollector _candidateCollector;
    private readonly ProfileDraftImporter _draftImporter;
    private readonly HidDeviceInventory _hidInventory;
    private readonly DeviceBatteryStateStore _deviceStates = new();
    private readonly KnownDeviceProfileProvider _profileProvider;
    private readonly BatteryProviderManager _providerManager;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly DeviceChangeWindow _deviceChangeWindow;
    private readonly Control _ui;
    private readonly Icon _appIcon;
    private readonly Dictionary<string, Icon> _iconCache = new(StringComparer.Ordinal);
    private readonly object _deviceRefreshSync = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly Dictionary<string, int> _lastAlertedBatteryLevels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastAlertUtcByDevice = new(StringComparer.OrdinalIgnoreCase);

    private AppSettings _settings;
    private BatteryReading? _lastReading;
    private IReadOnlyList<BatteryReading> _lastReadings = Array.Empty<BatteryReading>();
    private BatteryHistoryWindow? _historyWindow;
    private CancellationTokenSource? _deviceRefreshCts;
    private ToolStripMenuItem? _statusItem;
    private ToolStripMenuItem? _lastCheckItem;
    private string _statusText = "Mouse Battery Reminder: not detected";
    private string? _lastTrayText;
    private string? _lastIconKey;
    private int? _lastBatteryPercentage;
    private int? _lastBatteryBucket;
    private int _activePollingIntervalMilliseconds;
    private int _queuedCheck;
    private bool _isDetected;
    private bool _isCableConnected;
    private bool _keepSoundMenuOpen;
    private bool _displayActive = true;
    private DateTime? _lastCheckLocal;
    private DateTime _chargingFastUntilUtc = DateTime.MinValue;
    private DateTime _deviceRefreshBurstStartedUtc = DateTime.MinValue;
    private string _lastHidPresenceSignature = "";
    private string _lastSource = "none";
    private string _lastFailureReason = "not_detected";
    private bool _deviceRefreshForced;
    private bool _deviceRefreshTouchesTrackedDevice;

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
        _hidInventory = new HidDeviceInventory();
        _profileProvider = new KnownDeviceProfileProvider(_paths, _hidInventory);
        _candidateCollector = new BatteryCandidateCollector(_paths);
        _draftImporter = new ProfileDraftImporter(_paths, _profileProvider);
        _providerManager = new BatteryProviderManager(_hidInventory, new IBatteryProvider[] { new NinjutsoSoraOfficialProvider(), new CompxBatteryProvider(), _profileProvider });

        if (_settings.StartupWithWindows)
            StartupManager.SetEnabled(true);

        _menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9F) };
        _menu.Closing += KeepSoundMenuOpenWhenPreviewing;
        _menu.Opening += (_, _) => BuildMenu();
        _menu.Closed += (_, _) =>
        {
            _keepSoundMenuOpen = false;
        };

        _appIcon = LoadAppIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = _text["AppName"],
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.MouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                await CheckNowAsync(clearOnMissing: true, updateUi: true);
        };

        _pollTimer = new System.Windows.Forms.Timer();
        _pollTimer.Tick += async (_, _) => await CheckNowAsync(clearOnMissing: true, updateUi: true);

        _ui = new Control();
        _ui.CreateControl();
        _deviceChangeWindow = new DeviceChangeWindow(OnDeviceChanged, OnPowerEvent);
        var initialInventory = _hidInventory.GetSnapshot();
        if (initialInventory.IsReliable)
            _lastHidPresenceSignature = initialInventory.CreatePresenceSignature();

        ApplyTimerInterval();
        _ = CheckNowAsync(clearOnMissing: true, updateUi: true);
    }

    private void BuildMenu()
    {
        if (_menu.IsDisposed || _menu.Visible)
            return;

        _keepSoundMenuOpen = false;
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
        _lastCheckItem = new ToolStripMenuItem($"{_text["LastCheck"]}: {(_lastCheckLocal.HasValue ? _lastCheckLocal.Value.ToString("HH:mm:ss") : _text["Never"])}") { Enabled = false };
        _menu.Items.Add(_lastCheckItem);
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
        RequestMenuRebuild();
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
        RequestMenuRebuild();
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
            var setup = await Task.Run(() =>
            {
                var candidateDirectory = _candidateCollector.Collect(officialBattery.Value);
                var importResult = _draftImporter.ImportVerifiedDraftsProgressive(candidateDirectory);
                return (candidateDirectory, importResult);
            });
            dir = setup.candidateDirectory;
            var result = setup.importResult;
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
            RequestMenuRebuild();
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
        RequestMenuRebuild();
    }

    private void RequestMenuRebuild()
    {
        if (!_menu.IsDisposed && _menu.Visible)
            _menu.Invalidate();
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

    private async Task<bool> CheckNowAsync(
        bool clearOnMissing,
        bool updateUi,
        CancellationToken cancellationToken = default,
        bool preserveStateOnFailure = false,
        bool waitForGate = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (waitForGate)
        {
            await _checkGate.WaitAsync(cancellationToken);
        }
        else if (!await _checkGate.WaitAsync(0, cancellationToken))
        {
            Interlocked.Exchange(ref _queuedCheck, 1);
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previousStateKey = CreateReadingStateKey(_lastReadings, _lastSource, _lastFailureReason);
            if (updateUi && !preserveStateOnFailure)
                SetStatusText(_text["Checking"]);

            var result = await _providerManager.ReadAllAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (preserveStateOnFailure
                && (result.Readings.Count == 0 || !PreviousReadingsCoveredBy(_lastReadings, result.Readings)))
                return false;

            _lastCheckLocal = DateTime.Now;
            if (_lastCheckItem is { IsDisposed: false })
                _lastCheckItem.Text = $"{_text["LastCheck"]}: {_lastCheckLocal.Value:HH:mm:ss}";

            var stateUpdate = _deviceStates.Apply(result, DateTime.UtcNow, CurrentStaleThreshold());
            foreach (var reading in stateUpdate.OfflineReadings)
                _history.MarkOffline(reading);

            if (stateUpdate.CurrentReadings.Count > 0)
            {
                _lastSource = string.Join(", ", stateUpdate.CurrentReadings.Select(reading => reading.Source).Distinct(StringComparer.OrdinalIgnoreCase));
                _lastFailureReason = stateUpdate.HasFreshSamples ? "" : "read_failed";
                ApplyReadings(stateUpdate.CurrentReadings);
                ApplyTimerInterval();
                foreach (var reading in stateUpdate.FreshReadings)
                    _history.Append(reading);
                if (updateUi)
                    RenderTrayState();
                var alertReading = SelectAlertReading(stateUpdate.FreshReadings);
                if (alertReading != null)
                    MaybePlayAlert(alertReading);
                if (!string.Equals(previousStateKey, CreateReadingStateKey(_lastReadings, _lastSource, _lastFailureReason), StringComparison.Ordinal))
                    RequestMenuRebuild();
                return stateUpdate.HasFreshSamples;
            }

            _lastSource = result.Source;
            _lastFailureReason = result.FailureReason;
            ApplyMissingReading(clearOnMissing);
            ApplyTimerInterval();
            if (updateUi)
                RenderTrayState();
            if (!string.Equals(previousStateKey, CreateReadingStateKey(_lastReadings, _lastSource, _lastFailureReason), StringComparison.Ordinal))
                RequestMenuRebuild();
            return false;
        }
        finally
        {
            _checkGate.Release();
            if (Interlocked.Exchange(ref _queuedCheck, 0) == 1)
                _ = CheckNowAsync(clearOnMissing: true, updateUi: true);
        }
    }

    private void ApplyReadings(IReadOnlyList<BatteryReading> readings)
    {
        var validReadings = readings
            .Where(reading => reading.HasBatteryPercentage
                && reading.BatteryPercentage is >= 1 and <= 100
                && reading.IsOnline
                && reading.PowerState != DevicePowerState.Offline)
            .OrderBy(reading => reading.BatteryPercentage)
            .ThenBy(reading => ShortDeviceName(reading))
            .ToArray();

        if (validReadings.Length == 0)
        {
            ApplyMissingReading(clearOnMissing: true);
            return;
        }

        var wasCharging = _isCableConnected;
        var iconReading = SelectIconReading(validReadings);
        _lastReadings = validReadings;
        _lastReading = iconReading;
        _isDetected = true;
        _isCableConnected = validReadings.All(IsReadingCharging);
        if (!wasCharging && _isCableConnected)
            _chargingFastUntilUtc = DateTime.UtcNow.Add(ChargingFastWindow);
        else if (wasCharging && !_isCableConnected)
            _chargingFastUntilUtc = DateTime.MinValue;
        _lastBatteryPercentage = iconReading.BatteryPercentage;
        _lastBatteryBucket = ToBatteryBucket(iconReading.BatteryPercentage);
    }

    private void ApplyMissingReading(bool clearOnMissing)
    {
        if (_lastFailureReason == "read_failed" && _lastReading != null)
        {
            _isDetected = true;
            _isCableConnected = IsReadingCharging(_lastReading);
            if (_lastReadings.Count == 0)
                _lastReadings = new[] { _lastReading };
            return;
        }

        if (!clearOnMissing && _lastReading != null)
        {
            _isDetected = true;
            return;
        }

        _isDetected = false;
        _isCableConnected = false;
        _lastReadings = Array.Empty<BatteryReading>();
        _lastReading = null;
        _lastBatteryPercentage = null;
        _lastBatteryBucket = null;
        _chargingFastUntilUtc = DateTime.MinValue;
    }

    private void OnDeviceChanged(string devicePath, bool arrived)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return;

        if (devicePath.Contains("hid#", StringComparison.OrdinalIgnoreCase))
        {
            _hidInventory.Invalidate();
            ScheduleRefreshAfterChange(devicePath);
        }
    }

    private void ScheduleRefreshAfterChange(
        string? devicePath = null,
        bool forceRefresh = false,
        TimeSpan? quietPeriod = null)
    {
        CancellationTokenSource current;
        TimeSpan delay;
        bool touchesTrackedDevice;
        bool forced;
        lock (_deviceRefreshSync)
        {
            var nowUtc = DateTime.UtcNow;
            if (_deviceRefreshCts == null || _deviceRefreshBurstStartedUtc == DateTime.MinValue)
                _deviceRefreshBurstStartedUtc = nowUtc;

            _deviceRefreshForced |= forceRefresh;
            _deviceRefreshTouchesTrackedDevice |= !string.IsNullOrWhiteSpace(devicePath) && IsTrackedDevicePath(devicePath);

            var requestedDelay = quietPeriod ?? DeviceChangeQuietPeriod;
            var remaining = DeviceChangeMaximumCoalesce - (nowUtc - _deviceRefreshBurstStartedUtc);
            delay = remaining <= TimeSpan.Zero
                ? TimeSpan.Zero
                : requestedDelay <= remaining
                    ? requestedDelay
                    : remaining;

            _deviceRefreshCts?.Cancel();
            _deviceRefreshCts = new CancellationTokenSource();
            current = _deviceRefreshCts;
            touchesTrackedDevice = _deviceRefreshTouchesTrackedDevice;
            forced = _deviceRefreshForced;
        }
        _ = RefreshAfterDeviceChangeAsync(delay, current, forced, touchesTrackedDevice);
    }

    private async Task RefreshAfterDeviceChangeAsync(
        TimeSpan delay,
        CancellationTokenSource owner,
        bool forceRefresh,
        bool touchesTrackedDevice)
    {
        try
        {
            await Task.Delay(delay, owner.Token).ConfigureAwait(false);
            owner.Token.ThrowIfCancellationRequested();

            var snapshot = _hidInventory.GetSnapshot();
            var signature = snapshot.IsReliable ? snapshot.CreatePresenceSignature() : string.Empty;
            string previousSignature;
            lock (_deviceRefreshSync)
                previousSignature = _lastHidPresenceSignature;

            var signatureChanged = !snapshot.IsReliable
                || !string.Equals(previousSignature, signature, StringComparison.OrdinalIgnoreCase);
            if (!forceRefresh && !touchesTrackedDevice && !signatureChanged)
                return;

            for (var attempt = 0; attempt < DeviceReadRetryDelays.Length; attempt++)
            {
                var retryDelay = DeviceReadRetryDelays[attempt];
                if (retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(retryDelay, owner.Token).ConfigureAwait(false);
                    _hidInventory.Invalidate();
                    snapshot = _hidInventory.GetSnapshot();
                    if (snapshot.IsReliable)
                        signature = snapshot.CreatePresenceSignature();
                }

                owner.Token.ThrowIfCancellationRequested();
                var finalAttempt = attempt == DeviceReadRetryDelays.Length - 1;
                var succeeded = await InvokeDeviceCheckAsync(finalAttempt, owner.Token).ConfigureAwait(false);
                if (succeeded)
                {
                    CommitHidPresenceSignature(snapshot, signature);
                    return;
                }
            }

            CommitHidPresenceSignature(snapshot, signature);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            lock (_deviceRefreshSync)
            {
                if (ReferenceEquals(_deviceRefreshCts, owner))
                {
                    _deviceRefreshCts = null;
                    _deviceRefreshBurstStartedUtc = DateTime.MinValue;
                    _deviceRefreshForced = false;
                    _deviceRefreshTouchesTrackedDevice = false;
                }
            }
            owner.Dispose();
        }
    }

    private Task<bool> InvokeDeviceCheckAsync(bool finalAttempt, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (token.IsCancellationRequested)
        {
            completion.TrySetCanceled(token);
            return completion.Task;
        }
        if (_ui.IsDisposed || !_ui.IsHandleCreated)
        {
            completion.TrySetResult(false);
            return completion.Task;
        }

        try
        {
            _ui.BeginInvoke((MethodInvoker)(async () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var result = await CheckNowAsync(
                        clearOnMissing: finalAttempt,
                        updateUi: true,
                        cancellationToken: token,
                        preserveStateOnFailure: !finalAttempt,
                        waitForGate: true);
                    completion.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled(token);
                }
                catch
                {
                    completion.TrySetResult(false);
                }
            }));
        }
        catch
        {
            completion.TrySetResult(false);
        }

        return completion.Task;
    }

    private void CommitHidPresenceSignature(HidInventorySnapshot snapshot, string signature)
    {
        if (!snapshot.IsReliable)
            return;

        lock (_deviceRefreshSync)
            _lastHidPresenceSignature = signature;
    }

    private bool IsTrackedDevicePath(string devicePath)
    {
        foreach (var reading in _lastReadings)
        {
            if (!string.IsNullOrWhiteSpace(reading.DeviceId)
                && string.Equals(reading.DeviceId, devicePath, StringComparison.OrdinalIgnoreCase))
                return true;

            var vendorId = NormalizeUsbIdentifier(reading.VendorId);
            var productId = NormalizeUsbIdentifier(reading.ProductId);
            if (vendorId != null
                && productId != null
                && devicePath.Contains($"vid_{vendorId}", StringComparison.OrdinalIgnoreCase)
                && devicePath.Contains($"pid_{productId}", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? NormalizeUsbIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        if (!int.TryParse(normalized, System.Globalization.NumberStyles.HexNumber, null, out var parsed))
            return null;
        return parsed.ToString("X4");
    }

    private static bool PreviousReadingsCoveredBy(
        IReadOnlyList<BatteryReading> previousReadings,
        IReadOnlyList<BatteryReading> currentReadings)
    {
        if (previousReadings.Count == 0)
            return currentReadings.Count > 0;

        return previousReadings.All(previous => currentReadings.Any(current => IsSamePhysicalDevice(previous, current)));
    }

    private static bool IsSamePhysicalDevice(BatteryReading previous, BatteryReading current)
    {
        if (!string.IsNullOrWhiteSpace(previous.DeviceId)
            && string.Equals(previous.DeviceId, current.DeviceId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.Equals(previous.VendorId, current.VendorId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previous.Source, current.Source, StringComparison.OrdinalIgnoreCase))
            return false;

        var previousSerial = NormalizeDeviceSerial(previous.DeviceSerial);
        var currentSerial = NormalizeDeviceSerial(current.DeviceSerial);
        if (previousSerial != null && currentSerial != null)
            return string.Equals(previousSerial, currentSerial, StringComparison.OrdinalIgnoreCase);

        return !string.IsNullOrWhiteSpace(previous.DeviceName)
            && string.Equals(previous.DeviceName.Trim(), current.DeviceName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeDeviceSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return null;

        var normalized = serial.Trim();
        return normalized.All(character => character is '0' or '-' or ' ')
            ? null
            : normalized;
    }

    private void CancelPendingDeviceRefresh()
    {
        lock (_deviceRefreshSync)
        {
            _deviceRefreshCts?.Cancel();
            _deviceRefreshCts = null;
            _deviceRefreshBurstStartedUtc = DateTime.MinValue;
            _deviceRefreshForced = false;
            _deviceRefreshTouchesTrackedDevice = false;
        }
    }

    private void OnPowerEvent(PowerBroadcastEvent powerEvent)
    {
        switch (powerEvent)
        {
            case PowerBroadcastEvent.Suspend:
                _pollTimer.Stop();
                CancelPendingDeviceRefresh();
                break;
            case PowerBroadcastEvent.Resume:
                _displayActive = true;
                _hidInventory.Invalidate();
                ScheduleRefreshAfterChange(forceRefresh: true, quietPeriod: TimeSpan.FromMilliseconds(300));
                break;
            case PowerBroadcastEvent.DisplayOff:
                _displayActive = false;
                ApplyTimerInterval();
                break;
            case PowerBroadcastEvent.DisplayOn:
                var wasInactive = !_displayActive;
                _displayActive = true;
                ApplyTimerInterval();
                if (wasInactive)
                    ScheduleRefreshAfterChange(forceRefresh: true, quietPeriod: DeviceChangeQuietPeriod);
                break;
        }
    }

    private void RenderTrayState()
    {
        if (!_isDetected)
        {
            RenderNotDetected();
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

        if (_lastFailureReason == "read_failed" && _lastBatteryPercentage.HasValue)
        {
            var text = $"{_text["AppName"]}: {_lastBatteryPercentage.Value}% ({LocalizeFailure(_lastFailureReason)})";
            SetStatusText(text);
            SetTrayText(TrimTrayText(text));
            UpdateIcon(cableConnected: _isCableConnected, batteryBucket: _lastBatteryBucket ?? 100);
            return;
        }

        if (_isCableConnected)
        {
            var battery = _lastBatteryPercentage.HasValue ? $"{_lastBatteryPercentage.Value}% " : string.Empty;
            var text = $"{_text["AppName"]}: {battery}{_text["Charging"]}";
            SetStatusText(text);
            SetTrayText(TrimTrayText(text));
            UpdateIcon(cableConnected: true, batteryBucket: _lastBatteryBucket ?? 100);
            return;
        }

        if (_lastBatteryBucket.HasValue)
        {
            var text = _lastBatteryPercentage.HasValue ? $"{_text["AppName"]}: {_lastBatteryPercentage.Value}%" : _text["AppName"];
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
        SetTrayText(_text["NotReady"]);
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

    private void MaybePlayAlert(BatteryReading reading)
    {
        if (!reading.HasBatteryPercentage || reading.BatteryPercentage <= 0)
            return;

        var deviceKey = string.IsNullOrWhiteSpace(reading.DeviceId)
            ? $"{reading.VendorId}:{reading.ProductId}:{reading.Source}"
            : reading.DeviceId;

        if (reading.IsCableConnected || reading.IsCharging || reading.IsFullyCharged || reading.BatteryPercentage > _settings.AlertThreshold)
        {
            _lastAlertedBatteryLevels.Remove(deviceKey);
            _lastAlertUtcByDevice.Remove(deviceKey);
            return;
        }

        var alertLevel = AlertLevelFor(reading.BatteryPercentage);
        if (_lastAlertedBatteryLevels.TryGetValue(deviceKey, out var lastAlertedLevel) && alertLevel >= lastAlertedLevel)
            return;

        if (_lastAlertUtcByDevice.TryGetValue(deviceKey, out var lastAlertUtc)
            && DateTime.UtcNow < lastAlertUtc.AddMinutes(_settings.AlertCooldownMinutes))
            return;

        _lastAlertUtcByDevice[deviceKey] = DateTime.UtcNow;
        _lastAlertedBatteryLevels[deviceKey] = alertLevel;
        _sound.PlayCurrent();
    }

    private static int AlertLevelFor(int batteryPercentage) => Math.Clamp((batteryPercentage / 5) * 5, 0, 100);

    private void ApplyTimerInterval()
    {
        var chargingReadings = _lastReadings.Where(IsReadingCharging).ToArray();
        var intervalMilliseconds = chargingReadings.Length == 0
            ? Math.Max(1, _settings.PollingIntervalMinutes) * 60 * 1000
            : chargingReadings.All(IsReadingFullyCharged)
                ? FullChargePollingIntervalMilliseconds
                : !_displayActive
                    ? SteadyChargingPollingIntervalMilliseconds
                    : DateTime.UtcNow < _chargingFastUntilUtc
                        ? ChargingPollingIntervalMilliseconds
                        : SteadyChargingPollingIntervalMilliseconds;
        if (_pollTimer.Enabled && _activePollingIntervalMilliseconds == intervalMilliseconds)
            return;

        _pollTimer.Stop();
        _activePollingIntervalMilliseconds = intervalMilliseconds;
        _pollTimer.Interval = intervalMilliseconds;
        _pollTimer.Start();
    }

    private static string CreateReadingStateKey(IReadOnlyList<BatteryReading> readings, string source, string failureReason)
    {
        var devices = readings
            .OrderBy(reading => reading.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(reading => $"{reading.DeviceId}:{reading.BatteryPercentage}:{reading.PowerState}:{reading.ExternalPowerConnected}:{reading.Freshness}:{reading.Source}");
        return $"{source}|{failureReason}|{string.Join(";", devices)}";
    }

    private TimeSpan CurrentStaleThreshold()
    {
        var milliseconds = _activePollingIntervalMilliseconds > 0
            ? _activePollingIntervalMilliseconds * 2L
            : Math.Max(1, _settings.PollingIntervalMinutes) * 120_000L;
        return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 30_000L, 30 * 60_000L));
    }

    private void KeepSoundMenuOpenBriefly()
    {
        _keepSoundMenuOpen = true;
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
        return reading.PowerState is DevicePowerState.Charging or DevicePowerState.FullyCharged or DevicePowerState.PendingCharge
            || reading.ExternalPowerConnected == true
            || reading.IsCableConnected
            || reading.IsCharging
            || reading.IsFullyCharged;
    }

    private static bool IsReadingFullyCharged(BatteryReading reading)
    {
        return reading.PowerState == DevicePowerState.FullyCharged
            || reading.IsFullyCharged
            || (IsReadingCharging(reading) && reading.BatteryPercentage >= 100);
    }

    private string MouseCountLabel(int count)
    {
        return _settings.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? $"{count} mice"
            : $"{count} 个鼠标";
    }

    private string FormatCompactReading(BatteryReading reading)
    {
        var value = IsReadingCharging(reading)
            ? $"{reading.BatteryPercentage}% {_text["Charging"]}"
            : $"{reading.BatteryPercentage}%";
        return reading.Freshness == BatteryDataFreshness.Stale
            ? $"{value} ({LocalizeFailure("read_failed")})"
            : value;
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
        var fullyCharged = cableConnected
            && batteryBucket >= 100
            && _lastReadings.Count > 0
            && _lastReadings.All(IsReadingFullyCharged);
        var key = fullyCharged
            ? "full"
            : cableConnected
                ? $"charging:{batteryBucket}"
                : $"battery:{batteryBucket}";
        if (string.Equals(_lastIconKey, key, StringComparison.Ordinal))
            return;

        _lastIconKey = key;
        if (!_iconCache.TryGetValue(key, out var icon))
        {
            icon = CreateBatteryIcon(batteryBucket, cableConnected, fullyCharged);
            _iconCache[key] = icon;
        }
        _notifyIcon.Icon = icon;
    }

    private void SetDefaultIcon()
    {
        if (string.Equals(_lastIconKey, "default", StringComparison.Ordinal))
            return;

        _lastIconKey = "default";
        _notifyIcon.Icon = _appIcon;
    }

    private static Icon CreateBatteryIcon(int batteryBucket, bool cableConnected, bool fullyCharged)
    {
        var frames = new List<(int Size, byte[] Bytes)>();
        foreach (var size in new[] { 16, 20, 24, 32, 48, 64 })
        {
            using var bitmap = RenderBatteryBitmap(size, batteryBucket, cableConnected, fullyCharged);
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            frames.Add((size, png.ToArray()));
        }

        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)frames.Count);
            var offset = 6 + frames.Count * 16;
            foreach (var frame in frames)
            {
                writer.Write((byte)frame.Size);
                writer.Write((byte)frame.Size);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(frame.Bytes.Length);
                writer.Write(offset);
                offset += frame.Bytes.Length;
            }
            foreach (var frame in frames)
                writer.Write(frame.Bytes);
        }

        ico.Position = 0;
        using var icon = new Icon(ico);
        return (Icon)icon.Clone();
    }

    private static Bitmap RenderBatteryBitmap(int size, int batteryBucket, bool cableConnected, bool fullyCharged)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 64f, size / 64f);

        var body = new Rectangle(8, 19, 43, 27);
        var tip = new Rectangle(51, 27, 5, 11);
        var inner = new Rectangle(13, 24, 33, 17);
        using var outline = new Pen(Color.White, 4f);
        using var shadow = new Pen(Color.FromArgb(150, 0, 0, 0), 5f);
        g.DrawRoundedRectangle(shadow, body, 6);
        g.DrawRoundedRectangle(outline, body, 6);
        g.DrawRectangle(outline, tip);

        var fillColor = cableConnected || fullyCharged
            ? Color.FromArgb(34, 197, 94)
            : batteryBucket <= 10
                ? Color.FromArgb(239, 68, 68)
                : batteryBucket <= 20
                    ? Color.FromArgb(245, 158, 11)
                    : Color.FromArgb(235, 238, 242);
        var fillWidth = Math.Max(2, (int)Math.Round(inner.Width * Math.Clamp(batteryBucket / 100d, 0.03d, 1d)));
        using var fill = new SolidBrush(fillColor);
        g.FillRoundedRectangle(fill, new Rectangle(inner.X, inner.Y, fillWidth, inner.Height), 3);

        if (cableConnected || fullyCharged)
            DrawBolt(g, fillColor);
        return bitmap;
    }

    private static void DrawBolt(Graphics g, Color color)
    {
        var points = new[] { new Point(34, 13), new Point(24, 34), new Point(33, 34), new Point(28, 51), new Point(43, 28), new Point(34, 28) };
        using var white = new SolidBrush(Color.White);
        using var pen = new Pen(color, 1.5f);
        g.FillPolygon(white, points);
        g.DrawPolygon(pen, points);
    }

    private static Icon LoadAppIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "SoraV2BatteryTip.ico");
        return File.Exists(path) ? new Icon(path) : (Icon)SystemIcons.Application.Clone();
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
            foreach (var icon in _iconCache.Values)
                icon.Dispose();
            _iconCache.Clear();
            _appIcon.Dispose();
            lock (_deviceRefreshSync)
            {
                _deviceRefreshCts?.Cancel();
                _deviceRefreshCts?.Dispose();
                _deviceRefreshCts = null;
            }
            _checkGate.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal enum PowerBroadcastEvent
{
    Suspend,
    Resume,
    DisplayOn,
    DisplayOff
}

internal sealed class DeviceChangeWindow : NativeWindow, IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_DEVICECHANGE = 0x0219;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private static Guid SessionDisplayStatusGuid = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    private readonly Action<string, bool> _onChange;
    private readonly Action<PowerBroadcastEvent> _onPowerEvent;
    private IntPtr _notificationHandle;
    private IntPtr _powerNotificationHandle;

    public DeviceChangeWindow(Action<string, bool> onChange, Action<PowerBroadcastEvent> onPowerEvent)
    {
        _onChange = onChange;
        _onPowerEvent = onPowerEvent;
        CreateHandle(new CreateParams());
        RegisterHidNotifications();
        _powerNotificationHandle = RegisterPowerSettingNotification(Handle, ref SessionDisplayStatusGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
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
        else if (m.Msg == WM_POWERBROADCAST)
        {
            var eventType = m.WParam.ToInt32();
            if (eventType == PBT_APMSUSPEND)
                _onPowerEvent(PowerBroadcastEvent.Suspend);
            else if (eventType == PBT_APMRESUMEAUTOMATIC)
                _onPowerEvent(PowerBroadcastEvent.Resume);
            else if (eventType == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(m.LParam);
                if (setting.PowerSetting == SessionDisplayStatusGuid && setting.DataLength >= sizeof(int))
                {
                    var value = Marshal.ReadInt32(m.LParam, Marshal.SizeOf<PowerBroadcastSetting>());
                    _onPowerEvent(value == 0 ? PowerBroadcastEvent.DisplayOff : PowerBroadcastEvent.DisplayOn);
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
        if (_powerNotificationHandle != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_powerNotificationHandle);
            _powerNotificationHandle = IntPtr.Zero;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public int DataLength;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DevBroadcastDeviceInterface notificationFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
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
