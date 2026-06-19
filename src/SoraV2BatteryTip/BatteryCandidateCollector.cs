using System.Text.Json;
using HidSharp;

namespace SoraV2BatteryTip;

internal sealed class BatteryCandidateCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;

    public BatteryCandidateCollector(AppPaths paths) => _paths = paths;

    public string Collect(int officialBatteryPercentage)
    {
        _paths.Ensure();
        var dir = Path.Combine(_paths.DataDirectory, "candidates", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var draftsDir = Path.Combine(dir, "profile-drafts");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(draftsDir);

        var devices = CollectDevices(officialBatteryPercentage);
        var drafts = BuildProfileDrafts(devices, officialBatteryPercentage).ToArray();

        File.WriteAllText(Path.Combine(dir, "battery-candidates.json"), JsonSerializer.Serialize(new
        {
            timestampLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            officialBatteryPercentage,
            mode = "safe_feature_read_only",
            devices
        }, JsonOptions));

        File.WriteAllText(Path.Combine(dir, "profile-drafts.json"), JsonSerializer.Serialize(drafts, JsonOptions));

        foreach (var draft in drafts.Take(20))
        {
            var fileName = MakeSafeFileName($"{draft.Name}.json");
            File.WriteAllText(Path.Combine(draftsDir, fileName), JsonSerializer.Serialize(draft, JsonOptions));
        }

        return dir;
    }

    private static IReadOnlyList<DeviceCandidate> CollectDevices(int officialBatteryPercentage)
    {
        var devices = new List<DeviceCandidate>();
        foreach (var device in DeviceList.Local.GetHidDevices())
        {
            var featureLength = SafeInt(device.GetMaxFeatureReportLength);
            if (featureLength <= 1)
                continue;

            var reports = CollectFeatureReports(device, featureLength, officialBatteryPercentage);
            if (reports.Count == 0)
                continue;

            devices.Add(new DeviceCandidate
            {
                VendorId = $"0x{device.VendorID:X4}",
                ProductId = $"0x{device.ProductID:X4}",
                ProductName = Safe(() => device.GetProductName()),
                Manufacturer = Safe(() => device.GetManufacturer()),
                MaxFeatureReportLength = featureLength,
                DevicePathHash = HashPath(Safe(() => device.DevicePath)),
                Reports = reports
            });
        }

        return devices;
    }

    private static IReadOnlyList<FeatureReportCandidate> CollectFeatureReports(HidDevice device, int featureLength, int officialBatteryPercentage)
    {
        var reports = new List<FeatureReportCandidate>();
        if (!device.TryOpen(out var stream) || stream == null)
            return reports;

        using (stream)
        {
            foreach (var reportId in CandidateReportIds())
            {
                try
                {
                    var buffer = new byte[Math.Max(2, featureLength)];
                    buffer[0] = reportId;
                    stream.GetFeature(buffer, 0, buffer.Length);

                    var candidates = ExtractCandidates(buffer, officialBatteryPercentage);
                    if (candidates.Count == 0)
                        continue;

                    reports.Add(new FeatureReportCandidate
                    {
                        ReportId = $"0x{reportId:X2}",
                        Hex = Convert.ToHexString(buffer),
                        Candidates = candidates
                    });
                }
                catch { }
            }
        }

        return reports;
    }

    private static IReadOnlyList<CandidateByte> ExtractCandidates(byte[] bytes, int officialBatteryPercentage)
    {
        var candidates = new List<CandidateByte>();
        for (var offset = 0; offset < bytes.Length; offset++)
        {
            var value = bytes[offset];
            if (value is < 1 or > 100)
                continue;

            var delta = Math.Abs(value - officialBatteryPercentage);
            candidates.Add(new CandidateByte
            {
                Offset = offset,
                Value = value,
                Delta = delta,
                Exact = delta == 0,
                Near = delta <= 1
            });
        }

        return candidates
            .OrderBy(candidate => candidate.Delta)
            .ThenBy(candidate => candidate.Offset)
            .ToArray();
    }

    private static IEnumerable<ProfileDraft> BuildProfileDrafts(IReadOnlyList<DeviceCandidate> devices, int officialBatteryPercentage)
    {
        foreach (var device in devices)
        {
            foreach (var report in device.Reports)
            {
                var responseBytes = ParseHex(report.Hex);
                foreach (var candidate in report.Candidates.Where(candidate => candidate.Delta <= 5).OrderBy(candidate => candidate.Delta).ThenBy(candidate => candidate.Offset).Take(8))
                {
                    var chargingOffset = GuessFlagOffset(responseBytes, candidate.Offset + 1);
                    var fullOffset = GuessFlagOffset(responseBytes, candidate.Offset + 2);
                    var onlineOffset = GuessFlagOffset(responseBytes, candidate.Offset + 4) ?? GuessFlagOffset(responseBytes, candidate.Offset + 3);
                    yield return new ProfileDraft
                    {
                        Name = $"draft-{device.VendorId[2..]}-{device.ProductId[2..]}-{report.ReportId[2..]}-offset-{candidate.Offset}-value-{candidate.Value}",
                        Priority = 50,
                        VendorId = device.VendorId,
                        ProductIds = new[] { device.ProductId },
                        CableProductIds = Array.Empty<string>(),
                        ProductNameContains = string.IsNullOrWhiteSpace(device.ProductName) ? Array.Empty<string>() : new[] { device.ProductName },
                        AllowSameVendorFallback = true,
                        ReportType = "Feature",
                        ReportId = report.ReportId,
                        ResponseReportId = report.ReportId,
                        RequestBytes = Array.Empty<string>(),
                        SendRequest = false,
                        MinFeatureLength = device.MaxFeatureReportLength,
                        DelayMs = 0,
                        PayloadStarts = new[] { 0 },
                        BatteryOffset = candidate.Offset,
                        ChargingOffset = chargingOffset,
                        FullOffset = fullOffset,
                        OnlineOffset = onlineOffset,
                        Notes = $"Generated by safe read-only Learning Mode. Official battery hint: {officialBatteryPercentage}%. Candidate value: {candidate.Value}%, delta: {candidate.Delta}. Charging/full/online offsets are inferred from nearby 0/1 bytes when available."
                    };
                }
            }
        }
    }

    private static byte[] ParseHex(string hex)
    {
        try { return Convert.FromHexString(hex); }
        catch { return Array.Empty<byte>(); }
    }

    private static int? GuessFlagOffset(byte[] bytes, int offset)
    {
        if (offset < 0 || offset >= bytes.Length)
            return null;

        return bytes[offset] <= 1 ? offset : null;
    }

    private static IEnumerable<byte> CandidateReportIds()
    {
        yield return 0x00;
        for (byte id = 1; id <= 16; id++)
            yield return id;
        foreach (var id in new byte[] { 0x20, 0x30, 0x51, 0x80, 0x81, 0xF0, 0xF1, 0xFF })
            yield return id;
    }

    private static string Safe(Func<string?> getter)
    {
        try { return getter() ?? ""; }
        catch { return ""; }
    }

    private static int SafeInt(Func<int> getter)
    {
        try { return getter(); }
        catch { return 0; }
    }

    private static string HashPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(bytes)[..16];
    }

    private static string MakeSafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '-');
        return value;
    }

    private sealed class DeviceCandidate
    {
        public string VendorId { get; init; } = "";
        public string ProductId { get; init; } = "";
        public string ProductName { get; init; } = "";
        public string Manufacturer { get; init; } = "";
        public int MaxFeatureReportLength { get; init; }
        public string DevicePathHash { get; init; } = "";
        public IReadOnlyList<FeatureReportCandidate> Reports { get; init; } = Array.Empty<FeatureReportCandidate>();
    }

    private sealed class FeatureReportCandidate
    {
        public string ReportId { get; init; } = "";
        public string Hex { get; init; } = "";
        public IReadOnlyList<CandidateByte> Candidates { get; init; } = Array.Empty<CandidateByte>();
    }

    private sealed class CandidateByte
    {
        public int Offset { get; init; }
        public int Value { get; init; }
        public int Delta { get; init; }
        public bool Exact { get; init; }
        public bool Near { get; init; }
    }

    private sealed class ProfileDraft
    {
        public string Name { get; init; } = "";
        public int Priority { get; init; }
        public string VendorId { get; init; } = "";
        public string[] ProductIds { get; init; } = Array.Empty<string>();
        public string[] CableProductIds { get; init; } = Array.Empty<string>();
        public string[] ProductNameContains { get; init; } = Array.Empty<string>();
        public bool AllowSameVendorFallback { get; init; }
        public string ReportType { get; init; } = "Feature";
        public string ReportId { get; init; } = "";
        public string ResponseReportId { get; init; } = "";
        public string[] RequestBytes { get; init; } = Array.Empty<string>();
        public bool SendRequest { get; init; }
        public int MinFeatureLength { get; init; }
        public int DelayMs { get; init; }
        public int[] PayloadStarts { get; init; } = Array.Empty<int>();
        public int BatteryOffset { get; init; }
        public int? ChargingOffset { get; init; }
        public int? FullOffset { get; init; }
        public int? OnlineOffset { get; init; }
        public string Notes { get; init; } = "";
    }
}
