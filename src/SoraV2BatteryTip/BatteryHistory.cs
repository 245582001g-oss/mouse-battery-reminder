using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SoraV2BatteryTip;

internal sealed class BatteryHistoryEntry
{
    public DateTime TimestampUtc { get; set; }
    public string DeviceKey { get; set; } = "";
    public string DeviceName { get; set; } = "";
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

    public BatteryHistoryStore(AppPaths paths) => _paths = paths;

    public void Append(string state, BatteryReading reading)
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
            VendorId = NormalizeId(reading.VendorId),
            ProductId = NormalizeId(reading.ProductId),
            BatteryPercentage = reading.BatteryPercentage,
            IsCharging = reading.IsCharging || reading.IsFullyCharged || reading.IsCableConnected,
            IsCableConnected = reading.IsCableConnected,
            State = state,
            Source = reading.Source
        });
    }

    public IReadOnlyList<BatteryHistoryEntry> ReadLast(TimeSpan range, string? deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
            return Array.Empty<BatteryHistoryEntry>();

        PruneOldEntries();
        var fromUtc = DateTime.UtcNow - range;
        return ReadEntries(fromUtc)
            .Where(entry => string.Equals(entry.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.TimestampUtc)
            .ToArray();
    }

    public IReadOnlyList<BatteryHistoryDevice> ReadDevices(TimeSpan range)
    {
        PruneOldEntries();
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

    private void AppendRaw(BatteryHistoryEntry entry)
    {
        try
        {
            _paths.Ensure();
            PruneOldEntries();
            var last = ReadLastEntry(entry.DeviceKey);
            if (last != null && IsDuplicate(last, entry))
                return;
            File.AppendAllText(_paths.HistoryPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
        }
        catch { }
    }

    private BatteryHistoryEntry? ReadLastEntry(string deviceKey)
    {
        try
        {
            if (!File.Exists(_paths.HistoryPath))
                return null;

            BatteryHistoryEntry? last = null;
            foreach (var entry in ReadEntries(DateTime.MinValue))
            {
                if (string.Equals(entry.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase))
                    last = entry;
            }
            return last;
        }
        catch
        {
            return null;
        }
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

    private static bool IsDuplicate(BatteryHistoryEntry previous, BatteryHistoryEntry current)
    {
        var elapsed = current.TimestampUtc - previous.TimestampUtc;
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromMinutes(2))
            return false;

        var sameValue = previous.BatteryPercentage == current.BatteryPercentage
            && previous.IsCharging == current.IsCharging
            && previous.IsCableConnected == current.IsCableConnected
            && string.Equals(previous.DeviceKey, current.DeviceKey, StringComparison.OrdinalIgnoreCase);

        if (!sameValue)
            return false;

        return previous.State == current.State || elapsed <= TimeSpan.FromSeconds(20);
    }

    private void PruneOldEntries()
    {
        try
        {
            if (!File.Exists(_paths.HistoryPath))
                return;

            var cutoff = DateTime.UtcNow.AddDays(-30);
            var kept = ReadEntries(cutoff)
                .Select(entry => JsonSerializer.Serialize(entry, JsonOptions))
                .ToArray();
            File.WriteAllLines(_paths.HistoryPath, kept);
        }
        catch { }
    }

    private static string CreateDeviceKey(BatteryReading reading)
    {
        var vendorId = NormalizeId(reading.VendorId);
        var productId = NormalizeId(reading.ProductId);
        var name = DisplayDeviceName(reading);

        if (!string.IsNullOrWhiteSpace(vendorId) && !string.IsNullOrWhiteSpace(productId))
            return $"{vendorId}:{productId}:{NormalizeForKey(name)}";

        if (!string.IsNullOrWhiteSpace(reading.DeviceId))
            return NormalizeForKey(reading.DeviceId);

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
        var entries = _store.ReadLast(_range, _selectedDeviceKey);
        _chart.SetData(entries, _range);

        var latest = entries.LastOrDefault();
        var first = entries.FirstOrDefault();
        var consumed = first != null && latest != null ? Math.Max(0, first.BatteryPercentage - latest.BatteryPercentage) : 0;
        var hours = first != null && latest != null ? Math.Max(0.1, (latest.TimestampUtc - first.TimestampUtc).TotalHours) : 0;
        var average = hours > 0 ? consumed / hours : 0;
        var remainingHours = latest != null && average > 0.01 ? latest.BatteryPercentage / average : 0;
        var estimateText = remainingHours > 0 ? $"{remainingHours / 24:0.0} d" : _text["HistoryUnavailable"];
        var charging = latest?.IsCharging == true || latest?.IsCableConnected == true;
        var lastCharge = entries.LastOrDefault(e => e.IsCharging || e.IsCableConnected)?.TimestampUtc.ToLocalTime();
        var deviceName = CurrentDeviceName();

        _title.Text = latest == null ? deviceName : $"{deviceName} · {latest.BatteryPercentage}%";
        _summary.Text = $"{(entries.Count == 0 ? _text["HistoryDemo"] : _text["HistoryRealData"])}\r\n{(charging ? _text["Charging"] : "")}";
        _summary.ForeColor = charging ? Color.FromArgb(88, 224, 112) : Color.Gainsboro;
        _estimate.Text = $"{_text["HistoryEstimate"]}\r\n{estimateText}";
        _details.Text =
            $"{_text["HistoryConsumed"]}: {consumed}%    {_text["HistoryAverage"]}: {average:0.0}%/h\r\n" +
            $"{_text["HistoryLastFull"]}: {(lastCharge.HasValue ? lastCharge.Value.ToString("MM-dd HH:mm") : _text["HistoryUnavailable"])}";
    }

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

    public BatteryChartPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(38, 38, 38);
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 11F);
    }

    public void SetData(IReadOnlyList<BatteryHistoryEntry> entries, TimeSpan range)
    {
        _entries = entries;
        _range = range;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = new Rectangle(78, 38, Math.Max(10, Width - 120), Math.Max(10, Height - 96));
        using var grid = new Pen(Color.FromArgb(55, 255, 255, 255));
        using var label = new SolidBrush(Color.FromArgb(205, 220, 220, 220));
        using var line = new Pen(Color.FromArgb(45, 211, 111), 4F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var gap = new Pen(Color.FromArgb(120, 200, 200, 200), 3F) { DashStyle = DashStyle.Dash };

        foreach (var value in new[] { 100, 75, 50, 25, 0 })
        {
            var y = Y(value, plot);
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            g.DrawString($"{value}%", Font, label, 8, y - 11);
        }

        var from = DateTime.UtcNow - _range;
        var to = DateTime.UtcNow;
        for (var i = 1; i < _entries.Count; i++)
        {
            var a = _entries[i - 1];
            var b = _entries[i];
            var pen = b.TimestampUtc - a.TimestampUtc > TimeSpan.FromMinutes(35) ? gap : line;
            g.DrawLine(pen, X(a.TimestampUtc, plot, from, to), Y(a.BatteryPercentage, plot), X(b.TimestampUtc, plot, from, to), Y(b.BatteryPercentage, plot));
        }

        g.DrawString("Real records · gaps mean no record", Font, label, 8, Height - 34);
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
