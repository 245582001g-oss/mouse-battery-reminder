using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SoraV2BatteryTip;

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly SoraV2BatteryReader _reader;
    private readonly SettingsStore _store;
    private readonly AppPaths _paths;
    private readonly AlertSoundService _sound;
    private readonly Localizer _text;
    private readonly BatteryHistoryStore _history;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly DeviceChangeWindow _deviceChangeWindow;
    private readonly Control _uiInvoker;
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    private AppSettings _settings;
    private BatteryReading? _lastReading;
    private BatteryHistoryWindow? _historyWindow;
    private DateTime _lastAlertUtc = DateTime.MinValue;
    private int? _lastAlertedBatteryLevel;
    private Icon? _dynamicIcon;
    private ToolStripMenuItem? _statusItem;
    private string? _lastIconKey;
    private string? _lastTrayText;
    private string _statusText = "SORA V2: not detected";
    private bool _keepSoundMenuOpenAfterClick;
    private int? _lastBatteryPercentage;
    private int? _lastBatteryBucket;
    private bool _isCableConnected;
    private bool _isDetected;

    public TrayAppContext()
    {
        _paths = new AppPaths();
        _paths.Ensure();
        _store = new SettingsStore(_paths);
        _settings = _store.Load();
        _text = new Localizer(() => _settings);
        _reader = new SoraV2BatteryReader();
        _sound = new AlertSoundService(_paths, () => _settings);
        _history = new BatteryHistoryStore(_paths);

        _contextMenu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9F) };
        _contextMenu.Closing += KeepSoundMenuOpenWhenPreviewing;
        BuildContextMenu();

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Sora V2 Battery Tip",
            Visible = true,
            ContextMenuStrip = _contextMenu
        };
        _notifyIcon.MouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                await CheckNowAsync(clearOnMissing: true, updateUi: true);
            else if (e.Button == MouseButtons.Right)
                BuildContextMenu();
        };

        _pollTimer = new System.Windows.Forms.Timer();
        _pollTimer.Tick += async (_, _) => await CheckNowAsync(clearOnMissing: true, updateUi: true);

        _uiInvoker = new Control();
        _uiInvoker.CreateControl();
        _deviceChangeWindow = new DeviceChangeWindow(OnHidDeviceInterfaceChanged);

        ApplyTimerInterval();
        _ = CheckNowAsync(clearOnMissing: true, updateUi: true);
    }

    private void BuildContextMenu()
    {
        _contextMenu.Items.Clear();
        _statusItem = new ToolStripMenuItem(_statusText) { Enabled = false };
        _statusItem.ForeColor = _isCableConnected ? Color.ForestGreen : SystemColors.ControlText;

        _contextMenu.Items.Add(_statusItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem(_text["CheckNow"], null, async (_, _) => await CheckNowAsync(clearOnMissing: true, updateUi: true)));
        _contextMenu.Items.Add(new ToolStripMenuItem(_text["BatteryHistory"], null, (_, _) => ShowBatteryHistoryWindow()));
        _contextMenu.Items.Add(new ToolStripMenuItem(_text["TestSound"], null, (_, _) => _sound.PlayCurrent()));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(BuildNumberMenu(_text["Threshold"], _settings.AlertThreshold, new[] { 5, 10, 15, 20 }, "%", v => UpdateSettings(s => s.AlertThreshold = v)));
        _contextMenu.Items.Add(BuildNumberMenu(_text["Interval"], _settings.PollingIntervalMinutes, new[] { 5, 10, 15, 30 }, " min", v => UpdateSettings(s => s.PollingIntervalMinutes = v)));
        _contextMenu.Items.Add(BuildNumberMenu(_text["Cooldown"], _settings.AlertCooldownMinutes, new[] { 5, 10, 15, 30 }, " min", v => UpdateSettings(s => s.AlertCooldownMinutes = v)));
        _contextMenu.Items.Add(BuildSoundMenu());
        _contextMenu.Items.Add(BuildLanguageMenu());

        var startupItem = new ToolStripMenuItem(_text["Startup"]) { Checked = _settings.StartupWithWindows };
        startupItem.Click += (_, _) => UpdateSettings(s => s.StartupWithWindows = !s.StartupWithWindows);
        _contextMenu.Items.Add(startupItem);

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem(_text["Uninstall"], null, (_, _) => ConfirmUninstall()));
        _contextMenu.Items.Add(new ToolStripMenuItem(_text["Exit"], null, (_, _) => ExitThread()));
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
            item.Click += (_, _) => SelectSound(file);
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
            item.Click += (_, _) => SelectVolume(value);
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    private void SelectSound(string file)
    {
        KeepSoundMenuOpenBriefly();
        UpdateSettings(s => s.AlertSoundFile = file);
        RefreshOpenSoundMenuChecks();
        _sound.PlayCurrent();
    }

    private void SelectVolume(int volume)
    {
        KeepSoundMenuOpenBriefly();
        UpdateSettings(s => s.AlertVolume = volume);
        RefreshOpenSoundMenuChecks();
        _sound.PlayCurrent();
    }

    private void KeepSoundMenuOpenBriefly()
    {
        _keepSoundMenuOpenAfterClick = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            try
            {
                if (!_uiInvoker.IsDisposed && _uiInvoker.IsHandleCreated)
                    _uiInvoker.BeginInvoke((MethodInvoker)(() => _keepSoundMenuOpenAfterClick = false));
            }
            catch { }
        });
    }

    private void KeepSoundMenuOpenWhenPreviewing(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        if (_keepSoundMenuOpenAfterClick && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
            e.Cancel = true;
    }

    private void RefreshOpenSoundMenuChecks()
    {
        foreach (ToolStripItem item in _contextMenu.Items)
        {
            if (item is ToolStripMenuItem root && root.Text == _text["Sound"])
                RefreshSoundMenuChecks(root);
        }
    }

    private void RefreshSoundMenuChecks(ToolStripMenuItem soundRoot)
    {
        foreach (ToolStripItem item in soundRoot.DropDownItems)
        {
            if (item is ToolStripMenuItem child)
            {
                if (child.DropDownItems.Count > 0)
                {
                    foreach (ToolStripItem volumeItem in child.DropDownItems)
                    {
                        if (volumeItem is ToolStripMenuItem volumeMenuItem && int.TryParse((volumeMenuItem.Text ?? string.Empty).TrimEnd('%'), out var volume))
                            volumeMenuItem.Checked = volume == _settings.AlertVolume;
                    }
                    child.Text = $"{_text["Sound"]} {_settings.AlertVolume}%";
                }
                else if (child.Tag is string soundFile)
                {
                    child.Checked = soundFile.Equals(_settings.AlertSoundFile, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    private ToolStripMenuItem BuildLanguageMenu()
    {
        var menu = new ToolStripMenuItem(_text["Language"]);
        AddLanguageItem(menu, "Auto", "auto");
        AddLanguageItem(menu, "zh-CN", "zh-CN");
        AddLanguageItem(menu, "English", "en-US");
        return menu;
    }

    private void AddLanguageItem(ToolStripMenuItem menu, string label, string value)
    {
        var item = new ToolStripMenuItem(label) { Checked = _settings.Language.Equals(value, StringComparison.OrdinalIgnoreCase) };
        item.Click += (_, _) => UpdateSettings(s => s.Language = value);
        menu.DropDownItems.Add(item);
    }

    private void UpdateSettings(Action<AppSettings> mutate)
    {
        mutate(_settings);
        _store.Save(_settings);
        ApplyTimerInterval();
        RenderTrayState();
    }

    private void ConfirmUninstall()
    {
        var result = MessageBox.Show(
            _text["UninstallConfirm"],
            _text["UninstallTitle"],
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes)
            return;
        BeginUninstall();
    }

    private void BeginUninstall()
    {
        try
        {
            _settings.StartupWithWindows = false;
            StartupManager.SetEnabled(false);
            CleanRegistryRecords();
            if (Directory.Exists(_paths.DataDirectory))
                Directory.Delete(_paths.DataDirectory, recursive: true);
        }
        catch { }

        try
        {
            _notifyIcon.ShowBalloonTip(2500, _text["UninstallStartedTitle"], _text["UninstallStarted"], ToolTipIcon.Info);
        }
        catch { }

        LaunchSelfCleanupAfterExit();
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            try
            {
                if (!_uiInvoker.IsDisposed && _uiInvoker.IsHandleCreated)
                    _uiInvoker.BeginInvoke((MethodInvoker)(() => ExitThread()));
                else
                    ExitThread();
            }
            catch
            {
                ExitThread();
            }
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

    private static bool IsSafeAppDirectory(string appDir)
    {
        if (string.IsNullOrWhiteSpace(appDir))
            return false;
        var root = Path.GetPathRoot(appDir);
        if (string.Equals(root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), appDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return false;
        return File.Exists(Path.Combine(appDir, "SoraV2BatteryTip.exe")) || File.Exists(Path.Combine(appDir, "MouseBatteryGuard.exe"));
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

    private static void CleanRegistryRecords()
    {
        DeleteRegistryValue(@"Software\Microsoft\Windows\CurrentVersion\Run", "SoraV2BatteryTip");
        DeleteRegistryValue(@"Software\Microsoft\Windows\CurrentVersion\Run", "MouseBatteryGuard");
        DeleteRegistryValue(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "SoraV2BatteryTip");
        DeleteRegistryValue(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "MouseBatteryGuard");
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
            if (string.Equals(Path.GetFileName(executablePath), "SoraV2BatteryTip.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(executablePath), "MouseBatteryGuard.exe", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        return executablePath.Contains("SoraV2BatteryTip", StringComparison.OrdinalIgnoreCase) || executablePath.Contains("MouseBatteryGuard", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> CheckNowAsync(bool clearOnMissing, bool waitForCurrent = false, bool updateUi = true)
    {
        if (waitForCurrent)
            await _checkGate.WaitAsync();
        else if (!await _checkGate.WaitAsync(0))
            return false;

        try
        {
            if (updateUi)
                SetStatusText(_text["Checking"]);

            var reading = await _reader.ReadAsync(CancellationToken.None);
            if (reading != null)
            {
                ApplyReading(reading);
                _history.Append("detected", reading);
                if (updateUi)
                    RenderTrayState();
                MaybePlayAlert(reading);
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
        _lastReading = reading;
        _isDetected = true;
        _isCableConnected = reading.IsCableConnected;
        if (reading.HasBatteryPercentage && reading.BatteryPercentage is >= 1 and <= 100)
        {
            _lastBatteryPercentage = reading.BatteryPercentage;
            _lastBatteryBucket = ToBatteryBucket(reading.BatteryPercentage);
        }
    }

    private void ApplyMissingReading(bool clearOnMissing)
    {
        var connection = _reader.DetectConnection();
        if (connection.WiredPresent)
        {
            _isDetected = true;
            _isCableConnected = true;
            return;
        }

        if (connection.WirelessPresent || (!clearOnMissing && _lastBatteryBucket.HasValue))
        {
            _isDetected = true;
            _isCableConnected = false;
            if (_lastReading != null)
            {
                _lastReading = new BatteryReading
                {
                    BatteryPercentage = _lastBatteryPercentage ?? _lastReading.BatteryPercentage,
                    HasBatteryPercentage = _lastBatteryPercentage.HasValue || _lastReading.HasBatteryPercentage,
                    IsCharging = false,
                    IsFullyCharged = false,
                    IsOnline = connection.WirelessPresent,
                    IsCableConnected = false,
                    Source = _lastReading.Source
                };
            }
            return;
        }

        _isDetected = false;
        _isCableConnected = false;
        _lastReading = null;
    }

    private void RenderTrayState()
    {
        if (!_isDetected)
        {
            RenderNotDetected();
            return;
        }

        if (_isCableConnected)
        {
            var battery = _lastBatteryPercentage.HasValue ? $"{_lastBatteryPercentage.Value}% " : string.Empty;
            var text = $"SORA V2: {battery}{_text["Charging"]}";
            SetStatusText(text);
            SetTrayText(text.Length <= 63 ? text : text[..63]);
            UpdateIcon(cableConnected: true, batteryBucket: _lastBatteryBucket ?? 100);
            return;
        }

        if (_lastBatteryBucket.HasValue)
        {
            var text = _lastBatteryPercentage.HasValue ? $"SORA V2: {_lastBatteryPercentage.Value}%" : "SORA V2";
            SetStatusText(text);
            SetTrayText(text.Length <= 63 ? text : text[..63]);
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

    private void SetStatusText(string text)
    {
        _statusText = text;
        if (_statusItem != null && !_statusItem.IsDisposed)
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
            if (!_uiInvoker.IsDisposed && _uiInvoker.IsHandleCreated)
                _uiInvoker.BeginInvoke(RenderTrayState);
        }
        catch { }
    }

    private void OnHidDeviceInterfaceChanged(string devicePath, bool arrived)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return;

        if (devicePath.Contains("vid_1915&pid_ae12", StringComparison.OrdinalIgnoreCase))
        {
            _isDetected = arrived || _lastBatteryBucket.HasValue;
            _isCableConnected = arrived;
            if (!arrived && _lastReading != null)
            {
                _lastReading = new BatteryReading
                {
                    BatteryPercentage = _lastBatteryPercentage ?? _lastReading.BatteryPercentage,
                    HasBatteryPercentage = _lastBatteryPercentage.HasValue || _lastReading.HasBatteryPercentage,
                    IsCharging = false,
                    IsFullyCharged = false,
                    IsOnline = true,
                    IsCableConnected = false,
                    Source = _lastReading.Source
                };
            }
            PostRenderTrayState();
            return;
        }

        if (devicePath.Contains("vid_1915&pid_ae1c", StringComparison.OrdinalIgnoreCase))
        {
            if (arrived)
            {
                _isDetected = true;
                if (!_isCableConnected)
                    _isCableConnected = false;
            }
            else if (!_isCableConnected)
            {
                _isDetected = _lastBatteryBucket.HasValue;
            }
            PostRenderTrayState();
        }
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

    private int AlertLevelFor(int batteryPercentage)
    {
        if (batteryPercentage <= 3)
            return 3;
        if (batteryPercentage <= 5)
            return 5;
        if (batteryPercentage <= 10)
            return 10;
        return _settings.AlertThreshold;
    }

    private void ApplyTimerInterval()
    {
        _pollTimer.Stop();
        _pollTimer.Interval = Math.Max(1, _settings.PollingIntervalMinutes) * 60 * 1000;
        _pollTimer.Start();
    }

    private static int ToBatteryBucket(int percentage)
    {
        if (percentage >= 96)
            return 100;
        return Math.Clamp((int)Math.Round(percentage / 5d) * 5, 5, 100);
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
            _uiInvoker.Dispose();
            _pollTimer.Dispose();
            _contextMenu.Dispose();
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

