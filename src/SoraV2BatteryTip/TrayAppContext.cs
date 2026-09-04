using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SoraV2BatteryTip;

internal sealed class TrayAppContext : ApplicationContext
{
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
    private readonly IAppEventLog _log;
    private readonly SettingsStore _settingsStore;
    private readonly Localizer _text;
    private readonly AlertSoundService _sound;
    private readonly BatteryHistoryStore _history;
    private readonly DiagnosticsExporter _diagnostics;
    private readonly BatteryCandidateCollector _candidateCollector;
    private readonly ProfileDraftImporter _draftImporter;
    private readonly HidDeviceInventory _hidInventory;
    private readonly DeviceBatteryStateStore _deviceStates;
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
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly LowBatteryAlertTracker _alertTracker = new();
    private readonly ChargingPollingPolicy _chargingPollingPolicy = new();
    private readonly Dictionary<string, DeviceRefreshEvent> _deviceRefreshEvents = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _deviceRefreshBaselinePaths = new(StringComparer.OrdinalIgnoreCase);

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
    private ChargingPollingReason? _activePollingReason;
    private int _queuedCheck;
    private string _queuedCheckTrigger = "queued";
    private int _isDisposing;
    private long _pollSequence;
    private bool _isDetected;
    private bool _isCableConnected;
    private bool _keepSoundMenuOpen;
    private bool _displayActive = true;
    private DateTime? _lastCheckLocal;
    private DateTime _deviceRefreshBurstStartedUtc = DateTime.MinValue;
    private string _lastHidPresenceSignature = "";
    private string _lastSource = "none";
    private string _lastFailureReason = "not_detected";
    private bool _deviceRefreshForced;
    private bool _deviceRefreshTouchesTrackedDevice;
    private bool _deviceRefreshHasUntrackedDeviceEvent;

    public TrayAppContext(AppPaths paths, IAppEventLog log)
    {
        _paths = paths;
        _log = log;
        _log.Write("info", "tray.init_started", nameof(TrayAppContext), "success");
        _paths.Ensure();
        _settingsStore = new SettingsStore(_paths, _log);
        _settings = _settingsStore.Load();
        _text = new Localizer(() => _settings);
        _sound = new AlertSoundService(_paths, () => _settings, _log);
        _history = new BatteryHistoryStore(_paths, _log);
        _hidInventory = new HidDeviceInventory(log: _log);
        _deviceStates = new DeviceBatteryStateStore(_log);
        _profileProvider = new KnownDeviceProfileProvider(_paths, _hidInventory, _log);
        _candidateCollector = new BatteryCandidateCollector(_paths);
        _draftImporter = new ProfileDraftImporter(_paths, _profileProvider);
        _providerManager = new BatteryProviderManager(
            _hidInventory,
            new IBatteryProvider[]
            {
                new NinjutsoSoraOfficialProvider(_log, ApplyIdentityObservations),
                new CompxBatteryProvider(_log),
                _profileProvider
            },
            _log);
        _diagnostics = new DiagnosticsExporter(_paths, () => _settings, CreateDiagnosticsState, _hidInventory, _log);

        if (_settings.StartupWithWindows)
            StartupManager.SetEnabled(true, _log);

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
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ObserveUiTask(CheckNowAsync(clearOnMissing: true, updateUi: true, trigger: "manual_tray_click"), "manual_tray_check");
        };

        _pollTimer = new System.Windows.Forms.Timer();
        _pollTimer.Tick += (_, _) => ObserveUiTask(
            CheckNowAsync(clearOnMissing: true, updateUi: true, queueIfBusy: false, trigger: "timer"),
            "timer_check");

        _ui = new Control();
        _ui.CreateControl();
        _deviceChangeWindow = new DeviceChangeWindow(OnDeviceChanged, OnPowerEvent, _log);
        var initialInventory = _hidInventory.GetSnapshot();
        if (initialInventory.IsReliable)
            _lastHidPresenceSignature = initialInventory.CreatePresenceSignature();

        ApplyTimerInterval();
        _log.Write("info", "tray.init_completed", nameof(TrayAppContext), "success", new
        {
            initial_inventory_reliable = initialInventory.IsReliable,
            initial_hid_device_count = initialInventory.Devices.Count,
            initial_inventory_generation = initialInventory.Generation
        });
        ObserveUiTask(CheckNowAsync(clearOnMissing: true, updateUi: true, trigger: "startup"), "startup_check");
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
                _menu.Items.Add(new ToolStripMenuItem(FormatDeviceReading(reading)) { Enabled = false, ForeColor = DevicePowerSemantics.IsExternallyPowered(reading) ? Color.ForestGreen : SystemColors.ControlText });
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(_text["CheckNow"], null, (_, _) => ObserveUiTask(
            CheckNowAsync(clearOnMissing: true, updateUi: true, trigger: "manual_menu"),
            "manual_menu_check")));
        if (!_isDetected)
            _menu.Items.Add(new ToolStripMenuItem(_text["AutoSetupUnknownMouse"], null, async (_, _) => await AutoSetupUnknownMouse()));
        _menu.Items.Add(new ToolStripMenuItem(_text["BatteryHistory"], null, (_, _) => ShowBatteryHistoryWindow()));
        _menu.Items.Add(new ToolStripMenuItem(_text["TestSound"], null, (_, _) =>
        {
            _log.Write("info", "user.test_sound", nameof(TrayAppContext), "success");
            _sound.PlayCurrent();
        }));
        _menu.Items.Add(new ToolStripMenuItem(_text["OpenDebugLogs"], null, (_, _) => OpenDebugLogs()));
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
            StartupManager.SetEnabled(enabled, _log);
        };
        _menu.Items.Add(startup);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(_text["Uninstall"], null, (_, _) => ConfirmUninstall()));
        _menu.Items.Add(new ToolStripMenuItem(_text["Exit"], null, (_, _) =>
        {
            _log.Write("info", "user.exit_requested", nameof(TrayAppContext), "success");
            ExitThread();
        }));
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
        _log.Write("info", "user.profile_reload_requested", nameof(TrayAppContext), "success");
        _profileProvider.ReloadProfiles();
        try { _notifyIcon.ShowBalloonTip(1800, _text["DeviceProfiles"], _text["ProfilesReloaded"], ToolTipIcon.Info); }
        catch { }
        RequestMenuRebuild();
    }

    private void ImportLatestDrafts()
    {
        _log.Write("info", "profile.import_started", nameof(TrayAppContext), "success", new { mode = "latest_verified" });
        var result = _draftImporter.ImportLatestVerifiedDrafts();
        _profileProvider.ReloadProfiles();
        var message = result.Total == 0
            ? _text["NoDraftsFound"]
            : $"{_text[result.MessageKey]}: {result.Imported}/{result.Total}, rejected: {result.Rejected}";
        try { _notifyIcon.ShowBalloonTip(3000, _text["DeviceProfiles"], message, result.Imported > 0 ? ToolTipIcon.Info : ToolTipIcon.Warning); }
        catch { }
        _log.Write("info", "profile.import_completed", nameof(TrayAppContext), result.Imported > 0 ? "success" : "skipped", new
        {
            result.Imported,
            result.Rejected,
            result.Total,
            result.ToleranceUsed,
            result.StableReadsRequired
        });
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
            _log.Write("info", "candidate.collection_started", nameof(TrayAppContext), "success", new
            {
                official_battery_percentage = officialBattery.Value
            });
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
                var readOk = (await CheckNowAsync(clearOnMissing: true, updateUi: true, waitForGate: true, trigger: "profile_verification")).HasFreshSamples;
                var message = readOk
                    ? $"{_text["AutoSetupSuccess"]}: {result.Imported}/{result.Total}, ±{result.ToleranceUsed}%"
                    : $"{_text["AutoSetupImportedButReadFailed"]}: {result.Imported}/{result.Total}, ±{result.ToleranceUsed}%";
                try { _notifyIcon.ShowBalloonTip(3000, _text["DeviceProfiles"], message, readOk ? ToolTipIcon.Info : ToolTipIcon.Warning); }
                catch { }
                _log.Write("info", "candidate.collection_completed", nameof(TrayAppContext), readOk ? "success" : "degraded", new
                {
                    result.Imported,
                    result.Rejected,
                    result.Total,
                    result.ToleranceUsed,
                    verification_read_succeeded = readOk
                });
            }
            else
            {
                var message = $"{_text["AutoSetupFailed"]}: {result.Rejected}/{result.Total}";
                try { _notifyIcon.ShowBalloonTip(3500, _text["DeviceProfiles"], message, ToolTipIcon.Warning); }
                catch { }
                _log.Write("warn", "candidate.collection_completed", nameof(TrayAppContext), "failure", new
                {
                    result.Imported,
                    result.Rejected,
                    result.Total
                });
                TryOpenDirectory(dir);
            }
        }
        catch (Exception ex)
        {
            _log.Write("error", "candidate.collection_failed", nameof(TrayAppContext), "failure", exception: ex);
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

    private void OpenDebugLogs()
    {
        _log.Write("info", "user.open_debug_logs", nameof(TrayAppContext), "success");
        _log.Flush(TimeSpan.FromSeconds(1));
        _paths.OpenLogsDirectory();
    }

    private void ObserveUiTask(Task task, string operation)
    {
        _ = ObserveUiTaskAsync(task, operation);
    }

    private async Task ObserveUiTaskAsync(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            _log.Write("debug", "background_task.cancelled", nameof(TrayAppContext), "cancelled", new { operation });
        }
        catch (Exception ex)
        {
            _log.Write("error", "background_task.failed", nameof(TrayAppContext), "failure", new { operation }, ex);
        }
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
        _log.Write("info", "settings.changed", nameof(TrayAppContext), "success", new
        {
            alert_threshold = _settings.AlertThreshold,
            polling_interval_minutes = _settings.PollingIntervalMinutes,
            alert_cooldown_minutes = _settings.AlertCooldownMinutes,
            startup_with_windows = _settings.StartupWithWindows,
            language = _settings.Language,
            alert_sound_name = Path.GetFileName(_settings.AlertSoundFile),
            alert_volume = _settings.AlertVolume
        });
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
        try
        {
            var dir = _diagnostics.Export();
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); }
            catch (Exception ex) { _log.Write("warn", "diagnostics.open_folder_failed", nameof(TrayAppContext), "failure", exception: ex); }
            try { _notifyIcon.ShowBalloonTip(2500, _text["DiagnosticsDone"], dir, ToolTipIcon.Info); }
            catch (Exception ex) { _log.Write("warn", "diagnostics.balloon_failed", nameof(TrayAppContext), "failure", exception: ex); }
        }
        catch (Exception ex)
        {
            _log.Write("error", "diagnostics.user_export_failed", nameof(TrayAppContext), "failure", exception: ex);
        }
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
            reading.DeviceId,
            reading.LogicalDeviceId,
            reading.HistoryDeviceKey,
            reading.AssociationReceiverHistoryKey,
            reading.AssociationReceiverSerial,
            connectionTransport = reading.ConnectionTransport.ToString(),
            historyAnchorEvidence = reading.HistoryAnchorEvidence.ToString(),
            resolvedDeviceIds = DeviceIdentity.ResolvedDeviceIds(reading),
            reading.DeviceSerial,
            reading.VendorId,
            reading.ProductId,
            reading.BatteryPercentage,
            reading.HasBatteryPercentage,
            reading.IsCharging,
            reading.IsFullyCharged,
            reading.IsOnline,
            reading.IsCableConnected,
            powerState = reading.PowerState.ToString(),
            reading.ExternalPowerConnected,
            freshness = reading.Freshness.ToString(),
            reading.LastSuccessfulReadUtc,
            reading.ConsecutiveFailures,
            reading.ProviderName,
            reading.TimestampUtc,
            reading.Source
        }).ToArray(),
        processId = Environment.ProcessId
    };

    private async Task<PollAttemptOutcome> CheckNowAsync(
        bool clearOnMissing,
        bool updateUi,
        CancellationToken cancellationToken = default,
        bool preserveStateOnFailure = false,
        bool waitForGate = false,
        bool queueIfBusy = true,
        string trigger = "internal")
    {
        var pollSequence = Interlocked.Increment(ref _pollSequence);
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var operation = _log.BeginOperation($"poll-{trigger}");
        _log.Write("debug", "poll.requested", nameof(TrayAppContext), "success", new
        {
            poll_sequence = pollSequence,
            trigger,
            clear_on_missing = clearOnMissing,
            update_ui = updateUi,
            preserve_state_on_failure = preserveStateOnFailure,
            wait_for_gate = waitForGate,
            queue_if_busy = queueIfBusy
        });
        if (Volatile.Read(ref _isDisposing) != 0)
        {
            _log.Write("debug", "poll.skipped", nameof(TrayAppContext), "cancelled", new
            {
                poll_sequence = pollSequence,
                trigger,
                reason = "application_stopping",
                queued = false
            });
            return PollAttemptOutcome.NotStarted("application_stopping");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        cancellationToken = linkedCancellation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        if (waitForGate)
        {
            _log.Write("debug", "poll.gate_wait_started", nameof(TrayAppContext), "success", new { poll_sequence = pollSequence });
            await _checkGate.WaitAsync(cancellationToken);
        }
        else if (!await _checkGate.WaitAsync(0, cancellationToken))
        {
            if (queueIfBusy)
            {
                _queuedCheckTrigger = trigger;
                Interlocked.Exchange(ref _queuedCheck, 1);
            }
            _log.Write("debug", "poll.skipped", nameof(TrayAppContext), "skipped", new
            {
                poll_sequence = pollSequence,
                trigger,
                reason = "gate_busy",
                queued = queueIfBusy
            });
            return PollAttemptOutcome.NotStarted("gate_busy");
        }

        try
        {
            _log.Write("debug", "poll.started", nameof(TrayAppContext), "success", new { poll_sequence = pollSequence, trigger });
            cancellationToken.ThrowIfCancellationRequested();
            var previousStateKey = CreateReadingStateKey(_lastReadings, _lastSource, _lastFailureReason);
            if (updateUi && !preserveStateOnFailure)
                SetStatusText(_text["Checking"]);

            var result = await _providerManager.ReadAllAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var identityObservations = result.ProviderResults
                .SelectMany(batch => batch.IdentityObservations)
                .ToArray();
            ApplyIdentityObservations(identityObservations);
            var candidateCoverageComplete = DeviceIdentity.CandidatePathsCoveredBy(result.ProviderResults);
            if (preserveStateOnFailure
                && (result.Readings.Count == 0 || !DeviceIdentity.PreviousReadingsCoveredBy(_lastReadings, result.Readings)))
            {
                _log.Write("debug", "poll.state_preserved", nameof(TrayAppContext), "degraded", new
                {
                    poll_sequence = pollSequence,
                    reason = result.Readings.Count == 0 ? "no_readings" : "previous_devices_not_covered",
                    result.FailureReason,
                    result.InventoryReliable
                });
                return CreatePollAttemptOutcome(
                    result,
                    stateApplied: false,
                    Array.Empty<BatteryReading>(),
                    candidateCoverageComplete);
            }

            _lastCheckLocal = DateTime.Now;
            if (_lastCheckItem is { IsDisposed: false })
                _lastCheckItem.Text = $"{_text["LastCheck"]}: {_lastCheckLocal.Value:HH:mm:ss}";

            var stateUpdate = _deviceStates.Apply(result, DateTime.UtcNow, CurrentStaleThreshold());
            var usableFreshReadings = stateUpdate.FreshReadings
                .Where(IsUsableFreshBatterySample)
                .ToArray();
            foreach (var rekey in stateUpdate.Rekeys)
            {
                var previousRuntimeKey = DeviceIdentity.CreateRuntimeKey(rekey.PreviousReading);
                var currentRuntimeKey = DeviceIdentity.CreateRuntimeKey(rekey.CurrentReading);
                var alertStateMoved = false;
                var pollingStateMoved = false;
                var alertStateCleared = false;
                if (rekey.PreservePolicyState)
                {
                    alertStateMoved = _alertTracker.MoveState(previousRuntimeKey, currentRuntimeKey);
                    pollingStateMoved = _chargingPollingPolicy.MoveState(previousRuntimeKey, currentRuntimeKey);
                }
                else
                {
                    alertStateCleared = _alertTracker.Reset(previousRuntimeKey);
                }
                if (!string.Equals(previousRuntimeKey, currentRuntimeKey, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Write("info", rekey.PreservePolicyState
                        ? "device.policy_state_rekeyed"
                        : "device.policy_state_epoch_reset", nameof(TrayAppContext), "success", new
                    {
                        previous_device = _log.DeviceToken(rekey.PreviousReading),
                        current_device = _log.DeviceToken(rekey.CurrentReading),
                        preserve_policy_state = rekey.PreservePolicyState,
                        alert_state_moved = alertStateMoved,
                        polling_state_moved = pollingStateMoved,
                        alert_state_cleared = alertStateCleared,
                        reason = rekey.PreservePolicyState
                            ? "logical_identity_continuity"
                            : "receiver_serial_epoch_changed"
                    }, reading: rekey.CurrentReading);
                }
            }
            foreach (var reading in stateUpdate.OfflineReadings)
            {
                _history.MarkOffline(reading);
                var alertStateCleared = _alertTracker.Reset(DeviceIdentity.CreateRuntimeKey(reading));
                _log.Write("debug", "alert.state_cleared", nameof(TrayAppContext), "success", new
                {
                    reason = "device_offline",
                    previously_armed = alertStateCleared
                }, reading: reading);
            }

            if (stateUpdate.CurrentReadings.Count > 0)
            {
                _lastSource = string.Join(", ", stateUpdate.CurrentReadings.Select(reading => reading.Source).Distinct(StringComparer.OrdinalIgnoreCase));
                _lastFailureReason = usableFreshReadings.Length > 0 ? "" : "read_failed";
                ApplyReadings(stateUpdate.CurrentReadings);
                ApplyTimerInterval();
                foreach (var reading in stateUpdate.FreshReadings)
                    _history.Append(reading);
                if (updateUi)
                    RenderTrayState();
                ProcessLowBatteryAlerts(stateUpdate.FreshReadings);
                if (!string.Equals(previousStateKey, CreateReadingStateKey(_lastReadings, _lastSource, _lastFailureReason), StringComparison.Ordinal))
                    RequestMenuRebuild();
                _log.Write("debug", "poll.completed", nameof(TrayAppContext), usableFreshReadings.Length > 0 ? "success" : "degraded", new
                {
                    poll_sequence = pollSequence,
                    trigger,
                    duration_ms = started.ElapsedMilliseconds,
                    current_count = stateUpdate.CurrentReadings.Count,
                    fresh_count = stateUpdate.FreshReadings.Count,
                    offline_count = stateUpdate.OfflineReadings.Count,
                    rekey_count = stateUpdate.Rekeys.Count,
                    candidate_coverage_complete = candidateCoverageComplete,
                    stale = stateUpdate.HasStaleReadings,
                    source = _lastSource,
                    failure_reason = _lastFailureReason
                });
                return CreatePollAttemptOutcome(result, stateApplied: true, usableFreshReadings, candidateCoverageComplete);
            }

            _lastSource = result.Source;
            _lastFailureReason = result.FailureReason;
            ApplyMissingReading(clearOnMissing);
            ApplyTimerInterval();
            if (updateUi)
                RenderTrayState();
            if (!string.Equals(previousStateKey, CreateReadingStateKey(_lastReadings, _lastSource, _lastFailureReason), StringComparison.Ordinal))
                RequestMenuRebuild();
            _log.Write(result.FailureReason == "read_failed" ? "warn" : "debug", "poll.completed", nameof(TrayAppContext), "failure", new
            {
                poll_sequence = pollSequence,
                trigger,
                duration_ms = started.ElapsedMilliseconds,
                source = _lastSource,
                failure_reason = _lastFailureReason,
                result.HasCandidate,
                result.InventoryReliable
            });
            return CreatePollAttemptOutcome(result, stateApplied: true, usableFreshReadings, candidateCoverageComplete);
        }
        catch (OperationCanceledException)
        {
            _log.Write("debug", "poll.cancelled", nameof(TrayAppContext), "cancelled", new
            {
                poll_sequence = pollSequence,
                trigger,
                duration_ms = started.ElapsedMilliseconds
            });
            if (_shutdownCts.IsCancellationRequested)
                return PollAttemptOutcome.NotStarted("application_stopping");
            throw;
        }
        catch (Exception ex)
        {
            _log.Write("error", "poll.failed", nameof(TrayAppContext), "failure", new
            {
                poll_sequence = pollSequence,
                trigger,
                duration_ms = started.ElapsedMilliseconds
            }, ex);
            throw;
        }
        finally
        {
            _checkGate.Release();
            if (Volatile.Read(ref _isDisposing) == 0 && Interlocked.Exchange(ref _queuedCheck, 0) == 1)
            {
                var queuedTrigger = _queuedCheckTrigger;
                _log.Write("debug", "poll.queued_rerun", nameof(TrayAppContext), "success", new { poll_sequence = pollSequence, queued_trigger = queuedTrigger });
                ObserveUiTask(CheckNowAsync(clearOnMissing: true, updateUi: true, trigger: $"queued_after_{queuedTrigger}"), "queued_check");
            }
        }
    }

    private void ApplyIdentityObservations(IReadOnlyList<DeviceIdentityObservation> observations)
    {
        _deviceStates.ObserveIdentityEvidence(observations);
        foreach (var observation in observations)
            _history.RecordIdentityEvidence(observation);
    }

    private static PollAttemptOutcome CreatePollAttemptOutcome(
        BatteryReadAllResult result,
        bool stateApplied,
        IReadOnlyList<BatteryReading> usableFreshReadings,
        bool candidateCoverageComplete)
    {
        return new PollAttemptOutcome(
            true,
            result.InventoryReliable,
            stateApplied,
            usableFreshReadings.Count > 0,
            candidateCoverageComplete,
            result.ProviderResults
                .SelectMany(batch => batch.CandidateDeviceIds)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            usableFreshReadings
                .SelectMany(DeviceIdentity.ResolvedDeviceIds)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            result.FailureReason);
    }

    private static bool IsUsableFreshBatterySample(BatteryReading reading)
    {
        return reading.HasBatteryPercentage
            && reading.BatteryPercentage is >= 1 and <= 100
            && reading.IsOnline
            && reading.PowerState != DevicePowerState.Offline;
    }

    private void ApplyReadings(IReadOnlyList<BatteryReading> readings)
    {
        var previousDetected = _isDetected;
        var previousCableConnected = _isCableConnected;
        var previousBattery = _lastBatteryPercentage;
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
            _log.Write("warn", "tray.readings_rejected", nameof(TrayAppContext), "failure", new
            {
                input_count = readings.Count,
                reason = "no_valid_battery_reading"
            });
            ApplyMissingReading(clearOnMissing: true);
            return;
        }

        var iconReading = SelectIconReading(validReadings);
        _lastReadings = validReadings;
        _lastReading = iconReading;
        _isDetected = true;
        _isCableConnected = validReadings.All(DevicePowerSemantics.IsExternallyPowered);
        _lastBatteryPercentage = iconReading.BatteryPercentage;
        _lastBatteryBucket = ToBatteryBucket(iconReading.BatteryPercentage);
        _log.Write("debug", "tray.readings_applied", nameof(TrayAppContext), "success", new
        {
            input_count = readings.Count,
            valid_count = validReadings.Length,
            previous_detected = previousDetected,
            detected = _isDetected,
            previous_all_devices_charging = previousCableConnected,
            all_devices_charging = _isCableConnected,
            previous_icon_battery_percentage = previousBattery,
            icon_battery_percentage = _lastBatteryPercentage,
            icon_battery_bucket = _lastBatteryBucket,
            icon_device = _log.DeviceToken(iconReading)
        }, reading: iconReading);
    }

    private void ApplyMissingReading(bool clearOnMissing)
    {
        if (_lastFailureReason == "read_failed" && _lastReading != null)
        {
            _isDetected = true;
            _isCableConnected = DevicePowerSemantics.IsExternallyPowered(_lastReading);
            if (_lastReadings.Count == 0)
                _lastReadings = new[] { _lastReading };
            _log.Write("warn", "tray.state_preserved", nameof(TrayAppContext), "degraded", new
            {
                reason = "read_failed",
                clear_on_missing = clearOnMissing
            }, reading: _lastReading);
            return;
        }

        if (!clearOnMissing && _lastReading != null)
        {
            _isDetected = true;
            _log.Write("debug", "tray.state_preserved", nameof(TrayAppContext), "degraded", new
            {
                reason = "clear_disabled"
            }, reading: _lastReading);
            return;
        }

        _isDetected = false;
        _isCableConnected = false;
        _lastReadings = Array.Empty<BatteryReading>();
        _lastReading = null;
        _lastBatteryPercentage = null;
        _lastBatteryBucket = null;
        _log.Write("info", "tray.state_cleared", nameof(TrayAppContext), "success", new
        {
            reason = _lastFailureReason,
            clear_on_missing = clearOnMissing
        });
    }

    private void OnDeviceChanged(string devicePath, bool arrived)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return;

        if (devicePath.Contains("hid#", StringComparison.OrdinalIgnoreCase))
        {
            var tracked = IsExactTrackedDevicePath(devicePath);
            _log.Write("info", arrived ? "system.device_arrival" : "system.device_removal", nameof(TrayAppContext), "success", new
            {
                device_token = _log.TokenFor(devicePath, "dev"),
                device_path = devicePath,
                tracked_device = tracked,
                interface_type = "hid",
                fact_basis = "windows_wm_devicechange"
            });
            _hidInventory.Invalidate(arrived ? "device_arrival" : "device_removal");
            ScheduleRefreshAfterChange(devicePath, deviceArrived: arrived);
        }
    }

    private void ScheduleRefreshAfterChange(
        string? devicePath = null,
        bool forceRefresh = false,
        TimeSpan? quietPeriod = null,
        bool? deviceArrived = null)
    {
        CancellationTokenSource current;
        TimeSpan delay;
        bool touchesTrackedDevice;
        bool forced;
        bool requiresFullRetryWindow;
        IReadOnlyList<DeviceRefreshEvent> refreshEvents;
        IReadOnlySet<string> baselineDevicePaths;
        var hasDeviceEvent = !string.IsNullOrWhiteSpace(devicePath);
        var exactTrackedDevice = hasDeviceEvent && IsExactTrackedDevicePath(devicePath!);
        lock (_deviceRefreshSync)
        {
            var nowUtc = DateTime.UtcNow;
            if (_deviceRefreshCts == null || _deviceRefreshBurstStartedUtc == DateTime.MinValue)
            {
                _deviceRefreshBurstStartedUtc = nowUtc;
                _deviceRefreshEvents.Clear();
                _deviceRefreshBaselinePaths = _lastReadings
                    .SelectMany(DeviceIdentity.ResolvedDeviceIds)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            if (hasDeviceEvent)
            {
                _deviceRefreshEvents[devicePath!] = new DeviceRefreshEvent(
                    devicePath!,
                    deviceArrived ?? true,
                    exactTrackedDevice);
            }

            _deviceRefreshForced |= forceRefresh;
            _deviceRefreshTouchesTrackedDevice |= exactTrackedDevice;
            _deviceRefreshHasUntrackedDeviceEvent = _deviceRefreshEvents.Values.Any(item => item.Arrived && !item.WasTracked);

            var requestedDelay = quietPeriod ?? DeviceChangeQuietPeriod;
            var remaining = DeviceChangeMaximumCoalesce - (nowUtc - _deviceRefreshBurstStartedUtc);
            delay = remaining <= TimeSpan.Zero
                ? TimeSpan.Zero
                : requestedDelay <= remaining
                    ? requestedDelay
                    : remaining;

            var coalesced = _deviceRefreshCts != null;
            _deviceRefreshCts?.Cancel();
            _deviceRefreshCts = new CancellationTokenSource();
            current = _deviceRefreshCts;
            touchesTrackedDevice = _deviceRefreshTouchesTrackedDevice;
            forced = _deviceRefreshForced;
            requiresFullRetryWindow = _deviceRefreshHasUntrackedDeviceEvent;
            refreshEvents = _deviceRefreshEvents.Values.ToArray();
            baselineDevicePaths = _deviceRefreshBaselinePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _log.Write("debug", coalesced ? "device_refresh.coalesced" : "device_refresh.scheduled", nameof(TrayAppContext), "success", new
            {
                delay_ms = (long)delay.TotalMilliseconds,
                force_refresh = forced,
                touches_tracked_device = touchesTrackedDevice,
                has_device_event = hasDeviceEvent,
                device_arrived = deviceArrived,
                requires_full_retry_window = requiresFullRetryWindow,
                coalesced_event_count = refreshEvents.Count
            });
        }
        ObserveUiTask(
            RefreshAfterDeviceChangeAsync(
                delay,
                current,
                forced,
                touchesTrackedDevice,
                requiresFullRetryWindow,
                refreshEvents,
                baselineDevicePaths),
            "device_refresh");
    }

    private async Task RefreshAfterDeviceChangeAsync(
        TimeSpan delay,
        CancellationTokenSource owner,
        bool forceRefresh,
        bool touchesTrackedDevice,
        bool requiresFullRetryWindow,
        IReadOnlyList<DeviceRefreshEvent> refreshEvents,
        IReadOnlySet<string> baselineDevicePaths)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
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
            if (refreshEvents.Count == 0 && !forceRefresh && !touchesTrackedDevice && !requiresFullRetryWindow && !signatureChanged)
            {
                if (!TryCompleteDeviceRefresh(owner, snapshot, signature, () =>
                    _log.Write("debug", "device_refresh.skipped", nameof(TrayAppContext), "skipped", new
                    {
                        reason = "presence_unchanged",
                        duration_ms = started.ElapsedMilliseconds,
                        snapshot.Generation,
                        snapshot.IsReliable
                    })))
                {
                    throw new OperationCanceledException(owner.Token);
                }
                return;
            }

            for (var attempt = 0; attempt < DeviceReadRetryDelays.Length; attempt++)
            {
                var retryDelay = DeviceReadRetryDelays[attempt];
                if (retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(retryDelay, owner.Token).ConfigureAwait(false);
                    _hidInventory.Invalidate("device_refresh_retry");
                    snapshot = _hidInventory.GetSnapshot();
                    if (snapshot.IsReliable)
                        signature = snapshot.CreatePresenceSignature();
                }

                owner.Token.ThrowIfCancellationRequested();
                var finalAttempt = attempt == DeviceReadRetryDelays.Length - 1;
                _log.Write("debug", "device_refresh.retry", nameof(TrayAppContext), "success", new
                {
                    attempt = attempt + 1,
                    attempt_count = DeviceReadRetryDelays.Length,
                    delay_ms = (long)retryDelay.TotalMilliseconds,
                    final_attempt = finalAttempt,
                    snapshot_reliable = snapshot.IsReliable,
                    snapshot_generation = snapshot.Generation
                });
                var poll = await InvokeDeviceCheckAsync(finalAttempt, owner.Token).ConfigureAwait(false);
                owner.Token.ThrowIfCancellationRequested();
                var decision = DeviceRefreshPolicy.Evaluate(
                    refreshEvents,
                    baselineDevicePaths,
                    SnapshotDevicePaths(snapshot),
                    snapshot.IsReliable,
                    poll,
                    finalAttempt);
                if (decision.IsComplete)
                {
                    var level = decision.Outcome switch
                    {
                        "failure" => "warn",
                        "degraded" => "warn",
                        "skipped" => "debug",
                        _ => "info"
                    };
                    if (!TryCompleteDeviceRefresh(owner, snapshot, signature, () =>
                        _log.Write(level, "device_refresh.completed", nameof(TrayAppContext), decision.Outcome, new
                        {
                            attempt = attempt + 1,
                            duration_ms = started.ElapsedMilliseconds,
                            reason = decision.Reason,
                            event_count = refreshEvents.Count,
                            arrival_count = refreshEvents.Count(item => item.Arrived),
                            removal_count = refreshEvents.Count(item => !item.Arrived),
                            poll_completed = poll.PollCompleted,
                            poll_inventory_reliable = poll.InventoryReliable,
                            poll_state_applied = poll.StateApplied,
                            fresh_count = poll.FreshDevicePaths.Count,
                            candidate_count = poll.CandidateDevicePaths.Count,
                            candidate_coverage_complete = poll.CandidateCoverageComplete
                        })))
                    {
                        throw new OperationCanceledException(owner.Token);
                    }
                    return;
                }
                owner.Token.ThrowIfCancellationRequested();
                _log.Write("debug", "device_refresh.retry_continued", nameof(TrayAppContext), "success", new
                {
                    attempt = attempt + 1,
                    reason = decision.Reason,
                    poll_completed = poll.PollCompleted,
                    fresh_count = poll.FreshDevicePaths.Count,
                    candidate_count = poll.CandidateDevicePaths.Count
                });
            }

            if (!TryCompleteDeviceRefresh(owner, snapshot, signature, () =>
                _log.Write("warn", "device_refresh.completed", nameof(TrayAppContext), "failure", new
                {
                    attempt_count = DeviceReadRetryDelays.Length,
                    duration_ms = started.ElapsedMilliseconds
                })))
            {
                throw new OperationCanceledException(owner.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _log.Write("debug", "device_refresh.cancelled", nameof(TrayAppContext), "cancelled", new { duration_ms = started.ElapsedMilliseconds });
        }
        catch (Exception ex)
        {
            _log.Write("error", "device_refresh.failed", nameof(TrayAppContext), "failure", new { duration_ms = started.ElapsedMilliseconds }, ex);
        }
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
                    _deviceRefreshHasUntrackedDeviceEvent = false;
                    _deviceRefreshEvents.Clear();
                    _deviceRefreshBaselinePaths.Clear();
                }
            }
            owner.Dispose();
        }
    }

    private Task<PollAttemptOutcome> InvokeDeviceCheckAsync(bool finalAttempt, CancellationToken token)
    {
        var completion = new TaskCompletionSource<PollAttemptOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (token.IsCancellationRequested)
        {
            _log.Write("debug", "device_check.invoke_skipped", nameof(TrayAppContext), "cancelled", new { reason = "token_cancelled" });
            completion.TrySetCanceled(token);
            return completion.Task;
        }
        if (_ui.IsDisposed || !_ui.IsHandleCreated)
        {
            _log.Write("warn", "device_check.invoke_skipped", nameof(TrayAppContext), "failure", new { reason = "ui_handle_unavailable" });
            completion.TrySetResult(PollAttemptOutcome.NotStarted("ui_handle_unavailable"));
            return completion.Task;
        }

        token.Register(() => completion.TrySetCanceled(token));

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
                        waitForGate: true,
                        trigger: "device_change_retry");
                    completion.TrySetResult(result);
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled(token);
                }
                catch (Exception ex)
                {
                    _log.Write("error", "device_check.failed", nameof(TrayAppContext), "failure", exception: ex);
                    completion.TrySetResult(PollAttemptOutcome.NotStarted("device_check_failed"));
                }
            }));
        }
        catch (Exception ex)
        {
            _log.Write("error", "device_check.invoke_failed", nameof(TrayAppContext), "failure", exception: ex);
            completion.TrySetResult(PollAttemptOutcome.NotStarted("invoke_failed"));
        }

        return completion.Task;
    }

    private bool TryCompleteDeviceRefresh(
        CancellationTokenSource owner,
        HidInventorySnapshot snapshot,
        string signature,
        Action writeCompletion)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(writeCompletion);

        lock (_deviceRefreshSync)
        {
            if (owner.IsCancellationRequested || !ReferenceEquals(_deviceRefreshCts, owner))
                return false;

            if (snapshot.IsReliable)
                _lastHidPresenceSignature = signature;
            _deviceRefreshCts = null;
            _deviceRefreshBurstStartedUtc = DateTime.MinValue;
            _deviceRefreshForced = false;
            _deviceRefreshTouchesTrackedDevice = false;
            _deviceRefreshHasUntrackedDeviceEvent = false;
            _deviceRefreshEvents.Clear();
            _deviceRefreshBaselinePaths.Clear();
            writeCompletion();
            return true;
        }
    }

    private static IReadOnlySet<string> SnapshotDevicePaths(HidInventorySnapshot snapshot)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in snapshot.Devices)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(device.DevicePath))
                    paths.Add(device.DevicePath);
            }
            catch
            {
            }
        }
        return paths;
    }

    private bool IsExactTrackedDevicePath(string devicePath)
    {
        foreach (var reading in _lastReadings)
        {
            if (DeviceIdentity.ResolvedDeviceIds(reading)
                .Any(resolvedPath => string.Equals(resolvedPath, devicePath, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private void CancelPendingDeviceRefresh()
    {
        lock (_deviceRefreshSync)
        {
            var hadPendingRefresh = _deviceRefreshCts != null;
            _deviceRefreshCts?.Cancel();
            _deviceRefreshCts = null;
            _deviceRefreshBurstStartedUtc = DateTime.MinValue;
            _deviceRefreshForced = false;
            _deviceRefreshTouchesTrackedDevice = false;
            _deviceRefreshHasUntrackedDeviceEvent = false;
            _deviceRefreshEvents.Clear();
            _deviceRefreshBaselinePaths.Clear();
            if (hadPendingRefresh)
                _log.Write("debug", "device_refresh.cancelled", nameof(TrayAppContext), "cancelled", new { reason = "system_suspend_or_shutdown" });
        }
    }

    private void OnPowerEvent(PowerBroadcastEvent powerEvent)
    {
        _log.Write("info", "system.power_change", nameof(TrayAppContext), "success", new
        {
            power_event = powerEvent.ToString(),
            display_active_before = _displayActive,
            polling_interval_ms_before = _activePollingIntervalMilliseconds
        });
        switch (powerEvent)
        {
            case PowerBroadcastEvent.Suspend:
                _pollTimer.Stop();
                CancelPendingDeviceRefresh();
                break;
            case PowerBroadcastEvent.Resume:
                _displayActive = true;
                _hidInventory.Invalidate("system_resume");
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
        var changed = !string.Equals(_statusText, text, StringComparison.Ordinal);
        var previous = _statusText;
        _statusText = text;
        if (_statusItem is { IsDisposed: false })
        {
            _statusItem.Text = text;
            _statusItem.ForeColor = _isCableConnected ? Color.ForestGreen : SystemColors.ControlText;
        }
        if (changed)
        {
            _log.Write("debug", "tray.status_changed", nameof(TrayAppContext), "success", new
            {
                previous_status = previous,
                status = text,
                detected = _isDetected,
                all_devices_charging = _isCableConnected
            });
        }
    }

    private void SetTrayText(string text)
    {
        if (string.Equals(_lastTrayText, text, StringComparison.Ordinal))
            return;
        var previous = _lastTrayText;
        _lastTrayText = text;
        _notifyIcon.Text = text;
        _log.Write("debug", "tray.tooltip_changed", nameof(TrayAppContext), "success", new
        {
            previous_tooltip = previous,
            tooltip = text
        });
    }

    private void ProcessLowBatteryAlerts(IReadOnlyList<BatteryReading> readings)
    {
        var input = readings
            .Select(reading => new LowBatteryAlertInput(
                DeviceIdentity.CreateRuntimeKey(reading),
                reading.BatteryPercentage,
                reading.HasBatteryPercentage,
                DevicePowerSemantics.IsExternallyPowered(reading),
                DevicePowerSemantics.IsFullyCharged(reading),
                DevicePowerSemantics.IsExternallyPowered(reading)))
            .ToArray();
        var result = _alertTracker.Update(input, DateTime.UtcNow, _settings);
        foreach (var decision in result.Decisions)
        {
            var reading = readings.FirstOrDefault(item => string.Equals(DeviceIdentity.CreateRuntimeKey(item), decision.DeviceKey, StringComparison.OrdinalIgnoreCase));
            var eventName = decision.Disposition switch
            {
                LowBatteryAlertDisposition.Played => "alert.fired",
                LowBatteryAlertDisposition.Reset => "alert.reset",
                _ => "alert.suppressed"
            };
            var level = decision.Disposition == LowBatteryAlertDisposition.Played ? "info" : "debug";
            _log.Write(level, eventName, nameof(TrayAppContext), decision.Disposition == LowBatteryAlertDisposition.Played ? "success" : "skipped", new
            {
                decision.BatteryPercentage,
                reason = decision.Reason,
                threshold = _settings.AlertThreshold,
                cooldown_minutes = _settings.AlertCooldownMinutes,
                batch_sound_requested = result.ShouldPlaySound
            }, reading: reading);
        }
        if (result.ShouldPlaySound)
            _sound.PlayCurrent();
    }

    private void ApplyTimerInterval()
    {
        var devices = _lastReadings
            .Select(reading => new ChargingPollingDevice(
                DeviceIdentity.CreateRuntimeKey(reading),
                DevicePowerSemantics.IsExternallyPowered(reading),
                DevicePowerSemantics.IsFullyCharged(reading)))
            .ToArray();
        var decision = _chargingPollingPolicy.Evaluate(devices, _displayActive, DateTime.UtcNow, _settings);
        var intervalMilliseconds = (int)Math.Clamp(decision.Interval.TotalMilliseconds, 1, int.MaxValue);
        var intervalChanged = !_pollTimer.Enabled || _activePollingIntervalMilliseconds != intervalMilliseconds;
        var reasonChanged = _activePollingReason != decision.Reason;
        if (!intervalChanged && !reasonChanged)
            return;

        _log.Write("info", "poll.interval_changed", nameof(TrayAppContext), "success", new
        {
            previous_interval_ms = _activePollingIntervalMilliseconds,
            interval_ms = intervalMilliseconds,
            previous_reason = _activePollingReason?.ToString(),
            reason = decision.Reason.ToString(),
            charging_device_count = decision.ChargingDeviceKeys.Count,
            newly_charging_device_count = decision.NewlyChargingDeviceKeys.Count,
            display_active = _displayActive,
            fast_until_utc = decision.FastUntilUtc == DateTime.MinValue ? (DateTime?)null : decision.FastUntilUtc
        });
        _activePollingReason = decision.Reason;
        if (!intervalChanged)
            return;

        _pollTimer.Stop();
        _activePollingIntervalMilliseconds = intervalMilliseconds;
        _pollTimer.Interval = intervalMilliseconds;
        _pollTimer.Start();
    }

    private static string CreateReadingStateKey(IReadOnlyList<BatteryReading> readings, string source, string failureReason)
    {
        var devices = readings
            .OrderBy(DeviceIdentity.CreateRuntimeKey, StringComparer.OrdinalIgnoreCase)
            .Select(reading => $"{DeviceIdentity.CreateRuntimeKey(reading)}:{reading.DeviceId}:{reading.ConnectionTransport}:{reading.BatteryPercentage}:{reading.PowerState}:{reading.ExternalPowerConnected}:{reading.Freshness}:{reading.Source}");
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
        _log.Write("info", "uninstall.confirmation", nameof(TrayAppContext), result == DialogResult.Yes ? "success" : "cancelled", new
        {
            confirmed = result == DialogResult.Yes
        });
        if (result == DialogResult.Yes)
            BeginUninstall();
    }

    private void BeginUninstall()
    {
        try
        {
            _log.Write("warn", "uninstall.started", nameof(TrayAppContext), "success");
            _log.Flush(TimeSpan.FromSeconds(1));
            _settings.StartupWithWindows = false;
            _settingsStore.Save(_settings);
            StartupManager.SetEnabled(false, _log);
            CleanRegistryRecords();
            if (Directory.Exists(_paths.DataDirectory))
                Directory.Delete(_paths.DataDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _log.Write("error", "uninstall.cleanup_failed", nameof(TrayAppContext), "failure", exception: ex);
        }

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
            .Where(reading => !DevicePowerSemantics.IsExternallyPowered(reading))
            .OrderBy(reading => reading.BatteryPercentage)
            .FirstOrDefault()
            ?? readings.OrderBy(reading => reading.BatteryPercentage).First();
    }

    private string MouseCountLabel(int count)
    {
        return _settings.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? $"{count} mice"
            : $"{count} 个鼠标";
    }

    private string FormatCompactReading(BatteryReading reading)
    {
        var value = DevicePowerSemantics.IsExternallyPowered(reading)
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
            && _lastReadings.All(DevicePowerSemantics.IsFullyCharged);
        var key = fullyCharged
            ? "full"
            : cableConnected
                ? $"charging:{batteryBucket}"
                : $"battery:{batteryBucket}";
        if (string.Equals(_lastIconKey, key, StringComparison.Ordinal))
            return;

        var previousKey = _lastIconKey;
        _lastIconKey = key;
        if (!_iconCache.TryGetValue(key, out var icon))
        {
            icon = CreateBatteryIcon(batteryBucket, cableConnected, fullyCharged);
            _iconCache[key] = icon;
        }
        _notifyIcon.Icon = icon;
        _log.Write("info", "tray.icon_changed", nameof(TrayAppContext), "success", new
        {
            previous_icon = previousKey,
            icon = key,
            battery_bucket = batteryBucket,
            cable_connected = cableConnected,
            fully_charged = fullyCharged,
            displayed_battery_percentage = _lastBatteryPercentage,
            displayed_device = _lastReading == null ? null : _log.DeviceToken(_lastReading),
            fact_basis = "application_icon_assignment"
        }, reading: _lastReading);
    }

    private void SetDefaultIcon()
    {
        if (string.Equals(_lastIconKey, "default", StringComparison.Ordinal))
            return;

        var previousKey = _lastIconKey;
        _lastIconKey = "default";
        _notifyIcon.Icon = _appIcon;
        _log.Write("info", "tray.icon_changed", nameof(TrayAppContext), "success", new
        {
            previous_icon = previousKey,
            icon = "default",
            reason = _lastFailureReason
        });
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
        if (disposing && Interlocked.Exchange(ref _isDisposing, 1) == 0)
        {
            _log.Write("info", "tray.dispose_started", nameof(TrayAppContext), "success");
            _shutdownCts.Cancel();
            Interlocked.Exchange(ref _queuedCheck, 0);
            lock (_deviceRefreshSync)
            {
                _deviceRefreshCts?.Cancel();
                _deviceRefreshCts = null;
                _deviceRefreshBurstStartedUtc = DateTime.MinValue;
                _deviceRefreshForced = false;
                _deviceRefreshTouchesTrackedDevice = false;
                _deviceRefreshHasUntrackedDeviceEvent = false;
                _deviceRefreshEvents.Clear();
                _deviceRefreshBaselinePaths.Clear();
            }
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
            _log.Write("info", "tray.dispose_completed", nameof(TrayAppContext), "success");
            _log.Flush(TimeSpan.FromSeconds(1));
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
    private readonly IAppEventLog _log;
    private IntPtr _notificationHandle;
    private IntPtr _powerNotificationHandle;

    public DeviceChangeWindow(Action<string, bool> onChange, Action<PowerBroadcastEvent> onPowerEvent, IAppEventLog log)
    {
        _onChange = onChange;
        _onPowerEvent = onPowerEvent;
        _log = log;
        CreateHandle(new CreateParams());
        RegisterHidNotifications();
        _powerNotificationHandle = RegisterPowerSettingNotification(Handle, ref SessionDisplayStatusGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
        _log.Write(_powerNotificationHandle == IntPtr.Zero ? "warn" : "info", "system.power_notification_registered", nameof(DeviceChangeWindow), _powerNotificationHandle == IntPtr.Zero ? "failure" : "success", new
        {
            win32_error = _powerNotificationHandle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0
        });
    }

    protected override void WndProc(ref Message m)
    {
        try
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
        }
        catch (Exception ex)
        {
            _log.Write("warn", "system.window_message_decode_failed", nameof(DeviceChangeWindow), "failure", new { message_id = m.Msg }, ex);
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_notificationHandle != IntPtr.Zero)
        {
            var succeeded = UnregisterDeviceNotification(_notificationHandle);
            _log.Write(succeeded ? "debug" : "warn", "system.hid_notification_unregistered", nameof(DeviceChangeWindow), succeeded ? "success" : "failure", new
            {
                win32_error = succeeded ? 0 : Marshal.GetLastWin32Error()
            });
            _notificationHandle = IntPtr.Zero;
        }
        if (_powerNotificationHandle != IntPtr.Zero)
        {
            var succeeded = UnregisterPowerSettingNotification(_powerNotificationHandle);
            _log.Write(succeeded ? "debug" : "warn", "system.power_notification_unregistered", nameof(DeviceChangeWindow), succeeded ? "success" : "failure", new
            {
                win32_error = succeeded ? 0 : Marshal.GetLastWin32Error()
            });
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
        _log.Write(_notificationHandle == IntPtr.Zero ? "warn" : "info", "system.hid_notification_registered", nameof(DeviceChangeWindow), _notificationHandle == IntPtr.Zero ? "failure" : "success", new
        {
            win32_error = _notificationHandle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0
        });
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
