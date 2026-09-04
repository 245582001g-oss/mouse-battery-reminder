using HidSharp;

namespace SoraV2BatteryTip;

internal sealed class NinjutsoSoraOfficialProvider : IBatteryProvider
{
    private const int VendorId = 0x1915;
    private const byte FeatureReportId = 0x05;
    private const byte BatteryCommand = 0x15;
    private const int MinimumFeatureLength = 32;
    private const int IoTimeoutMs = 700;

    private static readonly HashSet<int> SupportedProductIds = new()
    {
        0xAE11, 0xAE12, 0xAE13, 0xAE14, 0xAE15, 0xAE16,
        0xAE1C, 0xAE8A, 0xAE8C
    };
    private readonly IAppEventLog? _log;

    public NinjutsoSoraOfficialProvider(IAppEventLog? log = null) => _log = log;

    public string Name => "SORA V2 Official HID";
    public int Priority => 300;

    public bool IsAvailable(HidInventorySnapshot inventory) => EnumerateCandidateDevices(inventory).Any();

    public Task<ProviderReadResult> ReadAsync(HidInventorySnapshot inventory, CancellationToken token)
    {
        return Task.Run(() => ReadAllOnce(inventory, token), token);
    }

    private ProviderReadResult ReadAllOnce(HidInventorySnapshot inventory, CancellationToken token)
    {
        var devices = EnumerateCandidateDevices(inventory).ToArray();
        var readings = new List<BatteryReading>();
        foreach (var device in devices)
        {
            token.ThrowIfCancellationRequested();

            var reading = TryReadDevice(device);
            if (reading != null && !readings.Any(existing => string.Equals(existing.DeviceId, reading.DeviceId, StringComparison.OrdinalIgnoreCase)))
                readings.Add(reading);
        }

        return new ProviderReadResult
        {
            Readings = readings,
            CandidateFound = devices.Length > 0,
            CandidateDeviceIds = devices.Select(device => Safe(() => device.DevicePath)).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray()
        };
    }

    private BatteryReading? TryReadDevice(HidDevice device)
    {
        if (!device.TryOpen(out var stream) || stream == null)
        {
            _log?.Write("warn", "provider.device_open_failed", Name, "failure", reading: DeviceForLog(device));
            return null;
        }

        using (stream)
        {
            try
            {
                stream.ReadTimeout = IoTimeoutMs;
                stream.WriteTimeout = IoTimeoutMs;

                var response = QueryBatteryReport(stream, device);
                var reading = ParseResponse(response);
                if (reading == null)
                {
                    _log?.Write("warn", "provider.parse_rejected", Name, "failure", new
                    {
                        reason = ParseFailureReason(response),
                        report_length = response.Length
                    }, reading: DeviceForLog(device));
                }
                return reading == null ? null : EnrichReading(reading, device);
            }
            catch (Exception ex)
            {
                _log?.Write("warn", "provider.io_failed", Name, "failure", new
                {
                    operation = "feature_query",
                    timeout_ms = IoTimeoutMs
                }, ex, DeviceForLog(device));
                return null;
            }
        }
    }

    private byte[] QueryBatteryReport(HidStream stream, HidDevice device)
    {
        var length = Math.Max(MinimumFeatureLength, SafeInt(device.GetMaxFeatureReportLength));
        var request = new byte[length];
        request[0] = FeatureReportId;
        request[1] = BatteryCommand;
        request[4] = 0x01;
        request[7] = 0x04;

        stream.SetFeature(request, 0, request.Length);
        Thread.Sleep(15);

        var response = new byte[length];
        response[0] = FeatureReportId;
        stream.GetFeature(response, 0, response.Length);

        if (response.Length > 9 && response[9] == 0)
        {
            _log?.Write("debug", "provider.zero_battery_retry", Name, "success", new
            {
                report_id = $"0x{FeatureReportId:X2}",
                command = $"0x{BatteryCommand:X2}"
            }, reading: DeviceForLog(device));
            Thread.Sleep(15);
            stream.SetFeature(request, 0, request.Length);
            Thread.Sleep(5);
            response[0] = FeatureReportId;
            stream.GetFeature(response, 0, response.Length);
        }

        return response;
    }

    private static BatteryReading? ParseResponse(byte[] response)
    {
        if (response.Length <= 10)
            return null;

        if (response[0] != FeatureReportId || response[1] != BatteryCommand)
            return null;

        var battery = response[9];
        if (battery is < 1 or > 100)
            return null;

        var charging = response[10] == 1;
        return new BatteryReading
        {
            BatteryPercentage = battery,
            IsCharging = charging,
            IsFullyCharged = charging && battery >= 100,
            IsOnline = true,
            IsCableConnected = charging,
            PowerState = charging
                ? battery >= 100 ? DevicePowerState.FullyCharged : DevicePowerState.Charging
                : DevicePowerState.Discharging,
            ExternalPowerConnected = charging,
            Source = "SORA V2 Official HID"
        };
    }

    private static string ParseFailureReason(byte[] response)
    {
        if (response.Length <= 10)
            return "short_report";
        if (response[0] != FeatureReportId)
            return "wrong_report_id";
        if (response[1] != BatteryCommand)
            return "wrong_command";
        if (response[9] is < 1 or > 100)
            return "battery_out_of_range";
        return "unknown";
    }

    private static BatteryReading DeviceForLog(HidDevice device) => new()
    {
        HasBatteryPercentage = false,
        DeviceId = Safe(() => device.DevicePath),
        VendorId = $"0x{device.VendorID:X4}",
        ProductId = $"0x{device.ProductID:X4}",
        Source = "SORA V2 Official HID"
    };

    private static BatteryReading EnrichReading(BatteryReading reading, HidDevice device)
    {
        return new BatteryReading
        {
            BatteryPercentage = reading.BatteryPercentage,
            HasBatteryPercentage = reading.HasBatteryPercentage,
            IsCharging = reading.IsCharging,
            IsFullyCharged = reading.IsFullyCharged,
            IsOnline = reading.IsOnline,
            IsCableConnected = reading.IsCableConnected,
            PowerState = reading.PowerState,
            ExternalPowerConnected = reading.ExternalPowerConnected,
            DeviceName = Safe(() => device.GetProductName()),
            DeviceId = Safe(() => device.DevicePath),
            DeviceSerial = Safe(() => device.GetSerialNumber()),
            VendorId = $"0x{device.VendorID:X4}",
            ProductId = $"0x{device.ProductID:X4}",
            Source = reading.Source,
            TimestampUtc = reading.TimestampUtc
        };
    }

    private static IEnumerable<HidDevice> EnumerateCandidateDevices(HidInventorySnapshot inventory)
    {
        return inventory.Devices
            .Where(device => device.VendorID == VendorId)
            .Where(device => SupportedProductIds.Contains(device.ProductID))
            .Where(device => SafeInt(device.GetMaxFeatureReportLength) >= MinimumFeatureLength)
            .OrderByDescending(DeviceScore)
            .ToArray();
    }

    private static int DeviceScore(HidDevice device)
    {
        var score = 0;
        var path = Safe(() => device.DevicePath);
        var name = Safe(() => device.GetProductName());

        if (device.ProductID is 0xAE1C or 0xAE8A or 0xAE8C)
            score += 100;
        if (path.Contains("&mi_01", StringComparison.OrdinalIgnoreCase))
            score += 40;
        if (path.Contains("&col04", StringComparison.OrdinalIgnoreCase))
            score += 80;
        if (name.Contains("Sora", StringComparison.OrdinalIgnoreCase))
            score += 40;

        score += Math.Min(100, SafeInt(device.GetMaxFeatureReportLength) / 8);
        return score;
    }

    public static bool HandlesProfile(string? vendorId, IEnumerable<string> productIds)
    {
        if (ParseInt(vendorId) != VendorId)
            return false;

        return productIds.Any(productId => ParseInt(productId) is int parsed && SupportedProductIds.Contains(parsed));
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex) ? hex : null;

        return int.TryParse(value, out var dec) ? dec : null;
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
}
