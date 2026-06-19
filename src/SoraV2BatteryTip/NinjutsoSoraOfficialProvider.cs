using HidSharp;

namespace SoraV2BatteryTip;

internal sealed class NinjutsoSoraOfficialProvider : IBatteryProvider, IMultipleBatteryProvider
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

    public string Name => "SORA V2 Official HID";
    public int Priority => 300;

    public bool IsAvailable() => EnumerateCandidateDevices().Any();

    public Task<BatteryReading?> ReadAsync(CancellationToken token)
    {
        return Task.Run(() => ReadAllOnce(token).FirstOrDefault(), token);
    }

    public Task<IReadOnlyList<BatteryReading>> ReadAllAsync(CancellationToken token)
    {
        return Task.Run<IReadOnlyList<BatteryReading>>(() => ReadAllOnce(token), token);
    }

    private static IReadOnlyList<BatteryReading> ReadAllOnce(CancellationToken token)
    {
        var readings = new List<BatteryReading>();
        foreach (var device in EnumerateCandidateDevices())
        {
            token.ThrowIfCancellationRequested();

            var reading = TryReadDevice(device);
            if (reading != null && !readings.Any(existing => string.Equals(existing.DeviceId, reading.DeviceId, StringComparison.OrdinalIgnoreCase)))
                readings.Add(reading);
        }

        return readings;
    }

    private static BatteryReading? TryReadDevice(HidDevice device)
    {
        if (!device.TryOpen(out var stream) || stream == null)
            return null;

        using (stream)
        {
            try
            {
                stream.ReadTimeout = IoTimeoutMs;
                stream.WriteTimeout = IoTimeoutMs;

                var response = QueryBatteryReport(stream, device);
                var reading = ParseResponse(response);
                return reading == null ? null : EnrichReading(reading, device);
            }
            catch
            {
                return null;
            }
        }
    }

    private static byte[] QueryBatteryReport(HidStream stream, HidDevice device)
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
            Source = "SORA V2 Official HID"
        };
    }

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
            DeviceName = Safe(() => device.GetProductName()),
            DeviceId = Safe(() => device.DevicePath),
            VendorId = $"0x{device.VendorID:X4}",
            ProductId = $"0x{device.ProductID:X4}",
            Source = reading.Source
        };
    }

    private static IEnumerable<HidDevice> EnumerateCandidateDevices()
    {
        return DeviceList.Local.GetHidDevices()
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
