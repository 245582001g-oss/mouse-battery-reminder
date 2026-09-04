using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoraV2BatteryTip;

internal sealed class BatteryHistoryEntry
{
    public DateTime TimestampUtc { get; set; }
    public string DeviceKey { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DeviceSerial { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public int BatteryPercentage { get; set; }
    public bool IsCharging { get; set; }
    public bool IsCableConnected { get; set; }
    public string State { get; set; } = "";
    public string Source { get; set; } = "";
}

internal sealed class BatteryHistoryDevice
{
    public string DeviceKey { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string VendorId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public DateTime LastSeenUtc { get; init; }
    public int LastBatteryPercentage { get; init; }
    public bool IsCharging { get; init; }
}

internal sealed class BatteryHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly AppPaths _paths;
    private readonly object _sync = new();
    private readonly Dictionary<string, BatteryHistoryEntry> _lastByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _legacyMigrationChecked = new(StringComparer.OrdinalIgnoreCase);
    private bool _lastCacheLoaded;
    private DateTime _lastPruneDateUtc = DateTime.MinValue;

    public BatteryHistoryStore(AppPaths paths) => _paths = paths;

    public void Append(BatteryReading reading)
    {
        if (!reading.HasBatteryPercentage || reading.BatteryPercentage is < 1 or > 100)
            return;

        var deviceKey = CreateDeviceKey(reading);
        if (string.IsNullOrWhiteSpace(deviceKey))
            return;

        AppendRaw(new BatteryHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow,
            DeviceKey = deviceKey,
            DeviceName = DisplayDeviceName(reading),
            DeviceSerial = NormalizeSerial(reading.DeviceSerial),
            VendorId = NormalizeId(reading.VendorId),
            ProductId = NormalizeId(reading.ProductId),
            BatteryPercentage = reading.BatteryPercentage,
            IsCharging = reading.IsCharging || reading.IsFullyCharged || reading.IsCableConnected,
            IsCableConnected = reading.IsCableConnected,
            State = "sample",
            Source = reading.Source
        });
    }

    public void MarkOffline(BatteryReading reading)
    {
        var deviceKey = CreateDeviceKey(reading);
        if (string.IsNullOrWhiteSpace(deviceKey))
            return;

        AppendRaw(new BatteryHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow,
            DeviceKey = deviceKey,
            DeviceName = DisplayDeviceName(reading),
            DeviceSerial = NormalizeSerial(reading.DeviceSerial),
            VendorId = NormalizeId(reading.VendorId),
            ProductId = NormalizeId(reading.ProductId),
            BatteryPercentage = Math.Clamp(reading.BatteryPercentage, 1, 100),
            IsCharging = false,
            IsCableConnected = false,
            State = "device_offline",
            Source = reading.Source
        });
    }

    public IReadOnlyList<BatteryHistoryEntry> ReadLast(TimeSpan range, string? deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
            return Array.Empty<BatteryHistoryEntry>();

        lock (_sync)
        {
            PruneOldEntriesIfDue();
            var fromUtc = DateTime.UtcNow - range;
            return ReadEntries(fromUtc)
                .Where(entry => string.Equals(entry.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.TimestampUtc)
                .ToArray();
        }
    }

    public IReadOnlyList<BatteryHistoryDevice> ReadDevices(TimeSpan range)
    {
        lock (_sync)
        {
            PruneOldEntriesIfDue();
            var fromUtc = DateTime.UtcNow - range;
            var latestByDevice = new Dictionary<string, BatteryHistoryEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in ReadEntries(fromUtc))
            {
                if (!latestByDevice.TryGetValue(entry.DeviceKey, out var previous) || entry.TimestampUtc > previous.TimestampUtc)
                    latestByDevice[entry.DeviceKey] = entry;
            }

            return latestByDevice.Values
                .OrderByDescending(entry => entry.TimestampUtc)
                .Select(entry => new BatteryHistoryDevice
                {
                    DeviceKey = entry.DeviceKey,
                    DeviceName = entry.DeviceName,
                    VendorId = entry.VendorId,
                    ProductId = entry.ProductId,
                    LastSeenUtc = entry.TimestampUtc,
                    LastBatteryPercentage = entry.BatteryPercentage,
                    IsCharging = entry.IsCharging || entry.IsCableConnected
                })
                .ToArray();
        }
    }

    private void AppendRaw(BatteryHistoryEntry entry)
    {
        lock (_sync)
        {
            try
            {
                _paths.Ensure();
                PruneOldEntriesIfDue();
                MigrateLegacyEntries(entry);
                EnsureLastCacheLoaded();
                _lastByDevice.TryGetValue(entry.DeviceKey, out var previous);
                entry.State = DetermineEvent(previous, entry);
                if (string.IsNullOrEmpty(entry.State))
                    return;

                File.AppendAllText(_paths.HistoryPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
                _lastByDevice[entry.DeviceKey] = entry;
            }
            catch { }
        }
    }

    private void EnsureLastCacheLoaded()
    {
        if (_lastCacheLoaded)
            return;

        _lastByDevice.Clear();
        foreach (var entry in ReadEntries(DateTime.MinValue))
        {
            if (!_lastByDevice.TryGetValue(entry.DeviceKey, out var previous) || entry.TimestampUtc > previous.TimestampUtc)
                _lastByDevice[entry.DeviceKey] = entry;
        }
        _lastCacheLoaded = true;
    }

    private void MigrateLegacyEntries(BatteryHistoryEntry current)
    {
        if (!_legacyMigrationChecked.Add(current.DeviceKey) || !File.Exists(_paths.HistoryPath))
            return;

        var legacyKey = $"{current.VendorId}:{current.ProductId}:{NormalizeForKey(current.DeviceName)}";
        if (string.Equals(legacyKey, current.DeviceKey, StringComparison.OrdinalIgnoreCase))
            return;

        var entries = ReadEntries(DateTime.MinValue).ToArray();
        var changed = false;
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.DeviceKey, legacyKey, StringComparison.OrdinalIgnoreCase))
                continue;
            entry.DeviceKey = current.DeviceKey;
            entry.DeviceSerial = current.DeviceSerial;
            changed = true;
        }

        if (!changed)
            return;

        var temporaryPath = _paths.HistoryPath + ".migrate.tmp";
        File.WriteAllLines(temporaryPath, entries.Select(entry => JsonSerializer.Serialize(entry, JsonOptions)));
        File.Move(temporaryPath, _paths.HistoryPath, overwrite: true);
        _lastCacheLoaded = false;
    }

    private static string DetermineEvent(BatteryHistoryEntry? previous, BatteryHistoryEntry current)
    {
        if (previous == null)
            return current.State == "device_offline" ? "device_offline" : "sample";
        if (current.State == "device_offline")
            return previous.State == "device_offline" ? "" : "device_offline";
        if (previous.State == "device_offline")
            return "device_online";
        if (!previous.IsCharging && current.IsCharging)
            return "charging_start";
        if (previous.IsCharging && !current.IsCharging)
            return "charging_end";
        if (previous.BatteryPercentage != current.BatteryPercentage)
            return "sample";
        if (current.TimestampUtc - previous.TimestampUtc >= TimeSpan.FromHours(1))
            return "heartbeat";
        return "";
    }

    private IReadOnlyList<BatteryHistoryEntry> ReadEntries(DateTime fromUtc)
    {
        var entries = new List<BatteryHistoryEntry>();
        try
        {
            if (!File.Exists(_paths.HistoryPath))
                return entries;

            foreach (var line in File.ReadLines(_paths.HistoryPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                BatteryHistoryEntry? entry;
                try { entry = JsonSerializer.Deserialize<BatteryHistoryEntry>(line); }
                catch { continue; }

                if (entry == null || entry.TimestampUtc < fromUtc || !IsValidEntry(entry))
                    continue;

                entries.Add(entry);
            }
        }
        catch { }

        return entries;
    }

    private static bool IsValidEntry(BatteryHistoryEntry entry)
    {
        return !string.IsNullOrWhiteSpace(entry.DeviceKey)
            && !string.IsNullOrWhiteSpace(entry.DeviceName)
            && entry.BatteryPercentage is >= 1 and <= 100;
    }

    private void PruneOldEntriesIfDue()
    {
        try
        {
            var todayUtc = DateTime.UtcNow.Date;
            if (_lastPruneDateUtc == todayUtc)
                return;
            _lastPruneDateUtc = todayUtc;

            if (!File.Exists(_paths.HistoryPath))
                return;

            var cutoff = DateTime.UtcNow.AddDays(-30);
            var kept = ReadEntries(cutoff)
                .Select(entry => JsonSerializer.Serialize(entry, JsonOptions))
                .ToArray();
            var temporaryPath = _paths.HistoryPath + ".tmp";
            File.WriteAllLines(temporaryPath, kept);
            File.Move(temporaryPath, _paths.HistoryPath, overwrite: true);
            _lastCacheLoaded = false;
        }
        catch { }
    }

    private static string CreateDeviceKey(BatteryReading reading)
    {
        var vendorId = NormalizeId(reading.VendorId);
        var productId = NormalizeId(reading.ProductId);
        var name = DisplayDeviceName(reading);
        var serial = NormalizeSerial(reading.DeviceSerial);

        if (!string.IsNullOrWhiteSpace(vendorId) && !string.IsNullOrWhiteSpace(productId) && !string.IsNullOrWhiteSpace(serial))
            return $"{vendorId}:{productId}:SERIAL:{serial}";

        if (!string.IsNullOrWhiteSpace(reading.DeviceId))
            return $"{vendorId}:{productId}:PATH:{ShortHash(reading.DeviceId)}";

        if (!string.IsNullOrWhiteSpace(vendorId) && !string.IsNullOrWhiteSpace(productId))
            return $"{vendorId}:{productId}:NAME:{NormalizeForKey(name)}";

        return NormalizeForKey($"{reading.Source}:{name}");
    }

    private static string DisplayDeviceName(BatteryReading reading)
    {
        var name = string.IsNullOrWhiteSpace(reading.DeviceName) ? reading.Source : reading.DeviceName;
        name = name
            .Replace("Wireless mouse", "Mouse", StringComparison.OrdinalIgnoreCase)
            .Replace("NANO dongle", "Dongle", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return string.IsNullOrWhiteSpace(name) ? "Mouse" : Shorten(name, 48);
    }

    private static string NormalizeId(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();
    }

    private static string NormalizeSerial(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var normalized = NormalizeForKey(value);
        return normalized.All(character => character == '0') ? "" : normalized;
    }

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static string NormalizeForKey(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();
    }

    private static string Shorten(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength].TrimEnd();
    }
}

internal sealed class BatteryHistoryWindow : Form
{
    private readonly BatteryHistoryStore _store;
    private readonly Localizer _text;
    private readonly BatteryChartPanel _chart = new();
    private readonly ComboBox _devicePicker = new();
    private readonly Label _title = new();
    private readonly Label _summary = new();
    private readonly Label _estimate = new();
    private readonly Label _details = new();
    private TimeSpan _range = TimeSpan.FromDays(1);
    private string? _selectedDeviceKey;
    private bool _refreshingDevices;

    public BatteryHistoryWindow(BatteryHistoryStore store, Localizer text)
    {
        _store = store;
        _text = text;
        Text = $"{_text["AppName"]} - {_text["BatteryHistory"]}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 760);
        Size = new Size(890, 1140);
        BackColor = Color.FromArgb(25, 25, 25);
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 11F);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(38), BackColor = BackColor, RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        Controls.Add(root);

        var header = Card();
        header.Padding = new Padding(30, 22, 30, 20);
        _title.SetBounds(30, 20, 500, 56);
        _title.Font = new Font(Font.FontFamily, 27F, FontStyle.Bold);
        _title.ForeColor = Color.White;
        _summary.SetBounds(31, 78, 420, 76);
        _summary.Font = new Font(Font.FontFamily, 15F);
        _estimate.SetBounds(560, 32, 210, 92);
        _estimate.TextAlign = ContentAlignment.MiddleRight;
        _estimate.Font = new Font(Font.FontFamily, 18F, FontStyle.Bold);
        _estimate.ForeColor = Color.White;
        header.Controls.Add(_title);
        header.Controls.Add(_summary);
        header.Controls.Add(_estimate);
        root.Controls.Add(header, 0, 0);

        var deviceBar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = BackColor, ColumnCount = 2, Padding = new Padding(0, 0, 0, 14) };
        deviceBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        deviceBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var deviceLabel = new Label
        {
            Text = _text.IsZh ? "鼠标" : "Mouse",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.Gainsboro,
            Font = new Font(Font.FontFamily, 14F)
        };
        _devicePicker.Dock = DockStyle.Fill;
        _devicePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _devicePicker.Font = new Font(Font.FontFamily, 13F);
        _devicePicker.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshingDevices)
                return;
            if (_devicePicker.SelectedItem is DevicePickerItem item)
            {
                _selectedDeviceKey = item.DeviceKey;
                Reload();
            }
        };
        deviceBar.Controls.Add(deviceLabel, 0, 0);
        deviceBar.Controls.Add(_devicePicker, 1, 0);
        root.Controls.Add(deviceBar, 0, 1);

        var rangeBar = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = BackColor };
        AddRangeButton(rangeBar, _text["Last24Hours"], TimeSpan.FromDays(1), true);
        AddRangeButton(rangeBar, _text["Last7Days"], TimeSpan.FromDays(7), false);
        AddRangeButton(rangeBar, _text["Last30Days"], TimeSpan.FromDays(30), false);
        root.Controls.Add(rangeBar, 0, 2);

        var chartCard = Card();
        chartCard.Padding = new Padding(28);
        _chart.Dock = DockStyle.Fill;
        chartCard.Controls.Add(_chart);
        root.Controls.Add(chartCard, 0, 3);

        var detailCard = Card();
        detailCard.Padding = new Padding(28, 18, 28, 18);
        _details.Dock = DockStyle.Fill;
        _details.Font = new Font(Font.FontFamily, 14F);
        _details.ForeColor = Color.White;
        detailCard.Controls.Add(_details);
        root.Controls.Add(detailCard, 0, 4);

        Load += (_, _) => ApplyDarkTitleBar();
        Reload();
    }

    public void Reload()
    {
        RefreshDevicePicker();
        var viewEntries = _store.ReadLast(_range, _selectedDeviceKey);
        var modelEntries = _store.ReadLast(TimeSpan.FromDays(30), _selectedDeviceKey);
        _chart.SetData(viewEntries, _range, _text["HistoryChartLegend"], _text["HistoryDailyLegend"]);

        var latest = modelEntries.LastOrDefault(entry => entry.State != "device_offline");
        var forecast = BatteryForecastService.Analyze(modelEntries, latest?.BatteryPercentage ?? 0);
        var viewAnalysis = BatteryForecastService.Analyze(viewEntries, latest?.BatteryPercentage ?? 0);
        var estimateText = forecast.IsAvailable
            ? forecast.MaximumRemainingDays - forecast.MinimumRemainingDays < 0.2
                ? $"{forecast.MinimumRemainingDays:0.0} d"
                : $"{forecast.MinimumRemainingDays:0.0}–{forecast.MaximumRemainingDays:0.0} d"
            : _text["HistoryUnavailable"];
        var charging = latest?.IsCharging == true || latest?.IsCableConnected == true;
        var lastChargeEntry = modelEntries.LastOrDefault(entry => entry.State == "charging_end")
            ?? modelEntries.LastOrDefault(entry => (entry.IsCharging || entry.IsCableConnected) && entry.BatteryPercentage >= 99)
            ?? modelEntries.LastOrDefault(entry => entry.IsCharging || entry.IsCableConnected);
        var lastCharge = lastChargeEntry?.TimestampUtc.ToLocalTime();
        var deviceName = CurrentDeviceName();
        var averageText = forecast.IsAvailable
            ? $"{forecast.TypicalDrainPerHour:0.00}%/h"
            : _text["HistoryUnavailable"];
        var confidenceText = _text[$"Confidence{forecast.Confidence}"];

        _title.Text = latest == null ? deviceName : $"{deviceName} · {latest.BatteryPercentage}%";
        _summary.Text = $"{(modelEntries.Count == 0 ? _text["HistoryDemo"] : _text["HistoryRealData"])}\r\n{(charging ? _text["Charging"] : "")}";
        _summary.ForeColor = charging ? Color.FromArgb(88, 224, 112) : Color.Gainsboro;
        _estimate.Text = $"{_text["HistoryEstimate"]}\r\n{estimateText}";
        _details.Text =
            $"{_text["HistoryConsumed"]}: {viewAnalysis.ConsumedPercentage}%    {_text["HistoryAverage"]}: {averageText}\r\n" +
            $"{_text["HistoryLastFull"]}: {(lastCharge.HasValue ? lastCharge.Value.ToString("MM-dd HH:mm") : _text["HistoryUnavailable"])}\r\n" +
            $"{_text["HistoryConfidence"]}: {confidenceText}    {_text["HistoryBasis"]}: {forecast.SegmentCount} {_text["HistorySegments"]} / {forecast.ValidHours:0}h";
    }

    private static BatteryEstimate CalculateEstimate(IReadOnlyList<BatteryHistoryEntry> source)
    {
        if (source.Count < 2)
            return default;

        var entries = source.OrderBy(entry => entry.TimestampUtc).ToArray();
        var maximumGap = CalculateMaximumGap(entries);
        var segments = new List<DischargeSegment>();
        DateTime? segmentStartTime = null;
        DateTime? segmentEndTime = null;
        var segmentStartBattery = 0;
        var segmentEndBattery = 0;

        void CompleteSegment()
        {
            if (!segmentStartTime.HasValue || !segmentEndTime.HasValue)
                return;

            var hours = (segmentEndTime.Value - segmentStartTime.Value).TotalHours;
            var consumed = segmentStartBattery - segmentEndBattery;
            if (hours >= 0.5 && consumed >= 1)
            {
                var rate = consumed / hours;
                if (rate is >= 0.01 and <= 20)
                    segments.Add(new DischargeSegment(hours, consumed, rate));
            }

            segmentStartTime = null;
            segmentEndTime = null;
        }

        for (var i = 1; i < entries.Length; i++)
        {
            var previous = entries[i - 1];
            var current = entries[i];
            var elapsed = current.TimestampUtc - previous.TimestampUtc;
            var drop = previous.BatteryPercentage - current.BatteryPercentage;
            var maximumPlausibleDrop = Math.Max(5, (int)Math.Ceiling(elapsed.TotalHours * 10));
            var validPair = elapsed > TimeSpan.Zero
                && elapsed <= maximumGap
                && !IsCharging(previous)
                && !IsCharging(current)
                && drop >= 0
                && drop <= maximumPlausibleDrop;

            if (!validPair)
            {
                CompleteSegment();
                continue;
            }

            if (!segmentStartTime.HasValue)
            {
                segmentStartTime = previous.TimestampUtc;
                segmentStartBattery = previous.BatteryPercentage;
            }

            segmentEndTime = current.TimestampUtc;
            segmentEndBattery = current.BatteryPercentage;
        }

        CompleteSegment();

        var validHours = segments.Sum(segment => segment.Hours);
        var consumedPercentage = segments.Sum(segment => segment.ConsumedPercentage);
        if (segments.Count == 0 || validHours < 2 || consumedPercentage < 1)
            return new BatteryEstimate(false, 0, consumedPercentage, validHours, segments.Count);

        var average = WeightedMedianRate(segments);
        return new BatteryEstimate(average > 0.01, average, consumedPercentage, validHours, segments.Count);
    }

    private static TimeSpan CalculateMaximumGap(IReadOnlyList<BatteryHistoryEntry> entries)
    {
        var intervals = new List<double>();
        for (var i = 1; i < entries.Count; i++)
        {
            var minutes = (entries[i].TimestampUtc - entries[i - 1].TimestampUtc).TotalMinutes;
            if (minutes is > 0 and <= 180)
                intervals.Add(minutes);
        }

        if (intervals.Count == 0)
            return TimeSpan.FromMinutes(45);

        intervals.Sort();
        var middle = intervals.Count / 2;
        var median = intervals.Count % 2 == 0
            ? (intervals[middle - 1] + intervals[middle]) / 2
            : intervals[middle];
        return TimeSpan.FromMinutes(Math.Clamp(median * 3, 30, 180));
    }

    private static double WeightedMedianRate(IReadOnlyList<DischargeSegment> source)
    {
        var segments = source.OrderBy(segment => segment.RatePerHour).ToArray();
        var halfWeight = segments.Sum(segment => segment.Hours) / 2;
        var cumulative = 0d;
        foreach (var segment in segments)
        {
            cumulative += segment.Hours;
            if (cumulative >= halfWeight)
                return segment.RatePerHour;
        }

        return segments[^1].RatePerHour;
    }

    private static bool IsCharging(BatteryHistoryEntry entry)
    {
        return entry.IsCharging || entry.IsCableConnected;
    }

    private readonly record struct DischargeSegment(double Hours, int ConsumedPercentage, double RatePerHour);
    private readonly record struct BatteryEstimate(bool IsAvailable, double AverageDrainPerHour, int ConsumedPercentage, double ValidHours, int SegmentCount);

    private void RefreshDevicePicker()
    {
        var previous = _selectedDeviceKey;
        var devices = _store.ReadDevices(TimeSpan.FromDays(30));
        _refreshingDevices = true;
        try
        {
            _devicePicker.Items.Clear();
            foreach (var device in devices)
                _devicePicker.Items.Add(new DevicePickerItem(device, _text["Charging"]));

            if (_devicePicker.Items.Count == 0)
            {
                _selectedDeviceKey = null;
                _devicePicker.Enabled = false;
                return;
            }

            _devicePicker.Enabled = true;
            var selectedIndex = 0;
            for (var i = 0; i < _devicePicker.Items.Count; i++)
            {
                if (_devicePicker.Items[i] is DevicePickerItem item && string.Equals(item.DeviceKey, previous, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }

            _devicePicker.SelectedIndex = selectedIndex;
            if (_devicePicker.SelectedItem is DevicePickerItem selected)
                _selectedDeviceKey = selected.DeviceKey;
        }
        finally
        {
            _refreshingDevices = false;
        }
    }

    private string CurrentDeviceName()
    {
        return _devicePicker.SelectedItem is DevicePickerItem item ? item.DeviceName : _text["AppName"];
    }

    private void AddRangeButton(FlowLayoutPanel parent, string label, TimeSpan range, bool isDefault)
    {
        var button = new RadioButton
        {
            Text = label,
            Checked = isDefault,
            ForeColor = Color.White,
            BackColor = BackColor,
            AutoSize = false,
            Width = 230,
            Height = 60,
            Font = new Font(Font.FontFamily, 14F),
            Padding = new Padding(0, 8, 0, 0)
        };
        button.CheckedChanged += (_, _) =>
        {
            if (!button.Checked) return;
            _range = range;
            Reload();
        };
        parent.Controls.Add(button);
    }

    private static Panel Card() => new RoundedPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(38, 38, 38), Margin = new Padding(0, 0, 0, 24) };

    private void ApplyDarkTitleBar()
    {
        if (Environment.OSVersion.Version.Major < 10) return;
        var value = 1;
        DwmSetWindowAttribute(Handle, 20, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private sealed class DevicePickerItem
    {
        public DevicePickerItem(BatteryHistoryDevice device, string chargingText)
        {
            DeviceKey = device.DeviceKey;
            DeviceName = device.DeviceName;
            Text = device.IsCharging
                ? $"{device.DeviceName} · {device.LastBatteryPercentage}% {chargingText}"
                : $"{device.DeviceName} · {device.LastBatteryPercentage}%";
        }

        public string DeviceKey { get; }
        public string DeviceName { get; }
        private string Text { get; }
        public override string ToString() => Text;
    }
}

internal sealed class BatteryChartPanel : Control
{
    private IReadOnlyList<BatteryHistoryEntry> _entries = Array.Empty<BatteryHistoryEntry>();
    private TimeSpan _range = TimeSpan.FromDays(1);
    private string _levelLegend = "";
    private string _dailyLegend = "";

    public BatteryChartPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(38, 38, 38);
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 11F);
    }

    public void SetData(IReadOnlyList<BatteryHistoryEntry> entries, TimeSpan range, string levelLegend, string dailyLegend)
    {
        _entries = entries;
        _range = range;
        _levelLegend = levelLegend;
        _dailyLegend = dailyLegend;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_range > TimeSpan.FromDays(1.5))
        {
            DrawDailyUsage(g);
            return;
        }

        DrawBatteryLevel(g);
    }

    private void DrawBatteryLevel(Graphics g)
    {
        var plot = new Rectangle(78, 38, Math.Max(10, Width - 120), Math.Max(10, Height - 96));
        using var grid = new Pen(Color.FromArgb(55, 255, 255, 255));
        using var label = new SolidBrush(Color.FromArgb(205, 220, 220, 220));
        using var line = new Pen(Color.FromArgb(89, 169, 255), 4F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var chargingLine = new Pen(Color.FromArgb(70, 220, 105), 4F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var chargingBand = new SolidBrush(Color.FromArgb(32, 70, 220, 105));
        using var gap = new Pen(Color.FromArgb(120, 200, 200, 200), 3F) { DashStyle = DashStyle.Dash };

        foreach (var value in new[] { 100, 75, 50, 25, 0 })
        {
            var y = Y(value, plot);
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString($"{value}%", Font, label, 8, y - 11);
        }

        var from = DateTime.UtcNow - _range;
        var to = DateTime.UtcNow;
        var maximumGap = BatteryForecastService.GetMaximumGap(_entries);
        for (var i = 1; i < _entries.Count; i++)
        {
            var a = _entries[i - 1];
            var b = _entries[i];
            var x1 = X(a.TimestampUtc, plot, from, to);
            var x2 = X(b.TimestampUtc, plot, from, to);
            var isGap = b.TimestampUtc - a.TimestampUtc > maximumGap
                || a.State == "device_offline"
                || b.State is "device_offline" or "device_online";
            var charging = a.IsCharging || a.IsCableConnected || b.IsCharging || b.IsCableConnected;
            if (charging && !isGap)
                g.FillRectangle(chargingBand, Math.Min(x1, x2), plot.Top, Math.Max(2, Math.Abs(x2 - x1)), plot.Height);
            var pen = isGap ? gap : charging ? chargingLine : line;
            g.DrawLine(pen, x1, Y(a.BatteryPercentage, plot), x2, Y(b.BatteryPercentage, plot));
        }

        g.DrawString(_levelLegend, Font, label, 8, Height - 34);
    }

    private void DrawDailyUsage(Graphics g)
    {
        var days = Math.Clamp((int)Math.Round(_range.TotalDays), 2, 30);
        var today = DateTime.Today;
        var daily = new List<(DateTime Day, int Consumed)>();
        for (var offset = days - 1; offset >= 0; offset--)
        {
            var day = today.AddDays(-offset);
            var entries = _entries.Where(entry => entry.TimestampUtc.ToLocalTime().Date == day).ToArray();
            var analysis = BatteryForecastService.Analyze(entries, entries.LastOrDefault()?.BatteryPercentage ?? 0);
            daily.Add((day, analysis.ConsumedPercentage));
        }

        var maximumValue = Math.Max(5, (int)Math.Ceiling(daily.Max(item => item.Consumed) / 5d) * 5);
        var plot = new Rectangle(68, 38, Math.Max(10, Width - 100), Math.Max(10, Height - 104));
        using var grid = new Pen(Color.FromArgb(55, 255, 255, 255));
        using var label = new SolidBrush(Color.FromArgb(205, 220, 220, 220));
        using var bar = new SolidBrush(Color.FromArgb(89, 169, 255));

        foreach (var value in new[] { maximumValue, maximumValue / 2, 0 })
        {
            var y = plot.Bottom - value / (float)maximumValue * plot.Height;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString($"{value}%", Font, label, 8, y - 11);
        }

        var slotWidth = plot.Width / (float)days;
        var barWidth = Math.Max(3, slotWidth * 0.62f);
        for (var i = 0; i < daily.Count; i++)
        {
            var item = daily[i];
            var height = item.Consumed <= 0 ? 0 : Math.Max(2, item.Consumed / (float)maximumValue * plot.Height);
            var x = plot.Left + i * slotWidth + (slotWidth - barWidth) / 2;
            if (height > 0)
                g.FillRectangle(bar, x, plot.Bottom - height, barWidth, height);

            if (days <= 7 || i == daily.Count - 1 || i % 5 == 0)
                g.DrawString(item.Day.ToString("M/d"), Font, label, x - 4, plot.Bottom + 8);
        }

        g.DrawString(_dailyLegend, Font, label, 8, Height - 30);
    }

    private static float X(DateTime t, Rectangle plot, DateTime from, DateTime to)
    {
        var total = Math.Max(1, (to - from).TotalSeconds);
        return plot.Left + (float)Math.Clamp((t - from).TotalSeconds / total, 0, 1) * plot.Width;
    }

    private static float Y(int value, Rectangle plot) => plot.Bottom - Math.Clamp(value, 0, 100) / 100f * plot.Height;
}

internal sealed class RoundedPanel : Panel
{
    protected override void OnPaint(PaintEventArgs e)
    {
        using var path = new GraphicsPath();
        var r = ClientRectangle;
        r.Width -= 1;
        r.Height -= 1;
        const int d = 34;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }
}
