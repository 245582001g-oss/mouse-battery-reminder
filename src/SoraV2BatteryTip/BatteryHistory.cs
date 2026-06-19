using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SoraV2BatteryTip;

internal sealed class BatteryHistoryEntry
{
    public DateTime TimestampUtc { get; set; }
    public int BatteryPercentage { get; set; }
    public bool IsCharging { get; set; }
    public bool IsCableConnected { get; set; }
    public string State { get; set; } = "";
    public string Source { get; set; } = "";
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

        AppendRaw(new BatteryHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow,
            BatteryPercentage = reading.BatteryPercentage,
            IsCharging = reading.IsCharging || reading.IsFullyCharged || reading.IsCableConnected,
            IsCableConnected = reading.IsCableConnected,
            State = state,
            Source = reading.Source
        });
    }

    public IReadOnlyList<BatteryHistoryEntry> ReadLast(TimeSpan range)
    {
        PruneOldEntries();
        var fromUtc = DateTime.UtcNow - range;
        var entries = new List<BatteryHistoryEntry>();
        try
        {
            if (!File.Exists(_paths.HistoryPath))
                return entries;

            foreach (var line in File.ReadLines(_paths.HistoryPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var entry = JsonSerializer.Deserialize<BatteryHistoryEntry>(line);
                if (entry == null || entry.TimestampUtc < fromUtc || entry.BatteryPercentage is < 1 or > 100)
                    continue;
                entries.Add(entry);
            }
        }
        catch { }

        entries.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return entries;
    }

    private void AppendRaw(BatteryHistoryEntry entry)
    {
        try
        {
            _paths.Ensure();
            PruneOldEntries();
            var last = ReadLastEntry();
            if (last != null && IsDuplicate(last, entry))
                return;
            File.AppendAllText(_paths.HistoryPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
        }
        catch { }
    }

    private BatteryHistoryEntry? ReadLastEntry()
    {
        try
        {
            if (!File.Exists(_paths.HistoryPath))
                return null;
            var line = File.ReadLines(_paths.HistoryPath).LastOrDefault(item => !string.IsNullOrWhiteSpace(item));
            return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<BatteryHistoryEntry>(line);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDuplicate(BatteryHistoryEntry previous, BatteryHistoryEntry current)
    {
        var elapsed = current.TimestampUtc - previous.TimestampUtc;
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromMinutes(2))
            return false;

        var sameValue = previous.BatteryPercentage == current.BatteryPercentage
            && previous.IsCharging == current.IsCharging
            && previous.IsCableConnected == current.IsCableConnected;

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
            var kept = new List<string>();
            foreach (var line in File.ReadLines(_paths.HistoryPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var entry = JsonSerializer.Deserialize<BatteryHistoryEntry>(line);
                if (entry != null && entry.TimestampUtc >= cutoff)
                    kept.Add(line);
            }
            File.WriteAllLines(_paths.HistoryPath, kept);
        }
        catch { }
    }
}

internal sealed class BatteryHistoryWindow : Form
{
    private readonly BatteryHistoryStore _store;
    private readonly Localizer _text;
    private readonly BatteryChartPanel _chart = new();
    private readonly Label _title = new();
    private readonly Label _summary = new();
    private readonly Label _estimate = new();
    private readonly Label _details = new();
    private TimeSpan _range = TimeSpan.FromDays(1);

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

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(38), BackColor = BackColor, RowCount = 4, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
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

        var rangeBar = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = BackColor };
        AddRangeButton(rangeBar, _text["Last24Hours"], TimeSpan.FromDays(1), true);
        AddRangeButton(rangeBar, _text["Last7Days"], TimeSpan.FromDays(7), false);
        AddRangeButton(rangeBar, _text["Last30Days"], TimeSpan.FromDays(30), false);
        root.Controls.Add(rangeBar, 0, 1);

        var chartCard = Card();
        chartCard.Padding = new Padding(28);
        _chart.Dock = DockStyle.Fill;
        chartCard.Controls.Add(_chart);
        root.Controls.Add(chartCard, 0, 2);

        var detailCard = Card();
        detailCard.Padding = new Padding(28, 18, 28, 18);
        _details.Dock = DockStyle.Fill;
        _details.Font = new Font(Font.FontFamily, 14F);
        _details.ForeColor = Color.White;
        detailCard.Controls.Add(_details);
        root.Controls.Add(detailCard, 0, 3);

        Load += (_, _) => ApplyDarkTitleBar();
        Reload();
    }

    public void Reload()
    {
        var entries = _store.ReadLast(_range);
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

        _title.Text = latest == null ? _text["AppName"] : $"{_text["AppName"]} · {latest.BatteryPercentage}%";
        _summary.Text = $"{(entries.Count == 0 ? _text["HistoryDemo"] : _text["HistoryRealData"])}\r\n{(charging ? _text["Charging"] : "")}";
        _summary.ForeColor = charging ? Color.FromArgb(88, 224, 112) : Color.Gainsboro;
        _estimate.Text = $"{_text["HistoryEstimate"]}\r\n{estimateText}";
        _details.Text =
            $"{_text["HistoryConsumed"]}: {consumed}%    {_text["HistoryAverage"]}: {average:0.0}%/h\r\n" +
            $"{_text["HistoryLastFull"]}: {(lastCharge.HasValue ? lastCharge.Value.ToString("MM-dd HH:mm") : _text["HistoryUnavailable"])}";
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
