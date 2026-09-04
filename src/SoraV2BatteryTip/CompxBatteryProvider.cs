using HidSharp;
namespace SoraV2BatteryTip;

internal sealed class CompxBatteryProvider : IBatteryProvider
{
    private const int VendorId = 0x373B;
    private const byte ReportId = 0x08;
    private const byte CommandGetBatteryLevel = 0x04;
    private const int PayloadLength = 16;
    private const int ReportLength = PayloadLength + 1;
    private const int IoTimeoutMs = 450;
    private readonly IAppEventLog? _log;

    public CompxBatteryProvider(IAppEventLog? log = null) => _log = log;

    public string Name => "ATK/COMPX HID";
    public int Priority => 250;

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
            if (reading != null)
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

                var inputLength = Math.Max(ReportLength, SafeInt(device.GetMaxInputReportLength));
                var rejectedFrames = 0;
                foreach (var request in BuildRequests(device))
                {
                    try
                    {
                        stream.Write(request.Bytes, 0, request.Bytes.Length);

                        var deadline = DateTime.UtcNow.AddMilliseconds(IoTimeoutMs);
                        while (DateTime.UtcNow < deadline)
                        {
                            var response = new byte[inputLength];
                            var read = stream.Read(response, 0, response.Length);
                            if (read <= 0)
                                continue;

                             var parsed = TryParseResponse(response, read);
                             if (parsed != null)
                                 return EnrichReading(parsed, device);
                            rejectedFrames++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _log?.Write("warn", "provider.io_failed", Name, "failure", new
                        {
                            operation = "write_read",
                            request_variant = request.Name,
                            request_length = request.Bytes.Length,
                            timeout_ms = IoTimeoutMs
                        }, ex, DeviceForLog(device));
                    }
                }
                _log?.Write("warn", "provider.read_timeout", Name, "failure", new
                {
                    timeout_ms = IoTimeoutMs,
                    rejected_frame_count = rejectedFrames,
                    input_report_length = inputLength
                }, reading: DeviceForLog(device));
            }
            catch (Exception ex)
            {
                _log?.Write("warn", "provider.io_failed", Name, "failure", new
                {
                    operation = "prepare_stream",
                    timeout_ms = IoTimeoutMs
                }, ex, DeviceForLog(device));
                return null;
            }
        }

        return null;
    }

    private static BatteryReading DeviceForLog(HidDevice device) => new()
    {
        HasBatteryPercentage = false,
        DeviceName = Safe(() => device.GetProductName()),
        DeviceId = Safe(() => device.DevicePath),
        DeviceSerial = Safe(() => device.GetSerialNumber()),
        VendorId = $"0x{device.VendorID:X4}",
        ProductId = $"0x{device.ProductID:X4}",
        Source = "ATK/COMPX HID"
    };

    private static BatteryReading? TryParseResponse(byte[] response, int length)
    {
        foreach (var payloadStart in PossiblePayloadStarts(response, length))
        {
            if (payloadStart + 6 >= length)
                continue;

            if (response[payloadStart] != CommandGetBatteryLevel)
                continue;

            var battery = response[payloadStart + 5];
            if (battery is < 1 or > 100)
                continue;

            var charge = response[payloadStart + 6];
            var charging = charge != 0;

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
                Source = "ATK/COMPX HID"
            };
        }

        return null;
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

    private static IEnumerable<int> PossiblePayloadStarts(byte[] response, int length)
    {
        if (length >= ReportLength && response[0] == ReportId && response[1] == CommandGetBatteryLevel)
            yield return 1;

        if (length >= PayloadLength && response[0] == CommandGetBatteryLevel)
            yield return 0;
    }

    private static IEnumerable<WriteRequest> BuildRequests(HidDevice device)
    {
        var payload = new byte[PayloadLength];
        payload[0] = CommandGetBatteryLevel;
        payload[^1] = CalculateChecksum(payload);

        var exact = new byte[ReportLength];
        exact[0] = ReportId;
        Buffer.BlockCopy(payload, 0, exact, 1, payload.Length);
        yield return new WriteRequest("report-08-payload-16", exact);

        var maxOutput = SafeInt(device.GetMaxOutputReportLength);
        if (maxOutput > ReportLength)
        {
            var padded = new byte[maxOutput];
            Buffer.BlockCopy(exact, 0, padded, 0, exact.Length);
            yield return new WriteRequest($"report-08-payload-16-padded-{maxOutput}", padded);
        }
    }

    private static byte CalculateChecksum(byte[] payload)
    {
        var sum = ReportId;
        for (var i = 0; i < payload.Length - 1; i++)
            sum += payload[i];
        return unchecked((byte)(85 - (sum & 0xFF)));
    }

    private static IEnumerable<HidDevice> EnumerateCandidateDevices(HidInventorySnapshot inventory)
    {
        var devices = inventory.Devices
            .Where(device => device.VendorID == VendorId)
            .Where(HasCompxReportShape)
            .OrderByDescending(DeviceScore)
            .ToArray();

        var officialInterfaces = devices.Where(IsOfficialCompxInterface).ToArray();
        return officialInterfaces.Length > 0 ? officialInterfaces : devices;
    }

    private static bool HasCompxReportShape(HidDevice device)
    {
        var inputLength = SafeInt(device.GetMaxInputReportLength);
        var outputLength = SafeInt(device.GetMaxOutputReportLength);
        if (inputLength < 8 || outputLength < 8)
            return false;

        var name = Safe(() => device.GetProductName());
        if (name.Contains("keyboard", StringComparison.OrdinalIgnoreCase))
            return false;

        var usagePage = GetIntProperty(device, "UsagePage");
        var usage = GetIntProperty(device, "Usage");
        if (usagePage.HasValue && usagePage.Value != 0xFF04)
            return false;
        if (usage.HasValue && usage.Value != 0x0002)
            return false;

        return true;
    }

    private static bool IsOfficialCompxInterface(HidDevice device)
    {
        return GetIntProperty(device, "UsagePage") == 0xFF04
            && GetIntProperty(device, "Usage") == 0x0002;
    }

    private static int DeviceScore(HidDevice device)
    {
        var score = 0;
        var name = Safe(() => device.GetProductName());
        var path = Safe(() => device.DevicePath);
        var inputLength = SafeInt(device.GetMaxInputReportLength);
        var outputLength = SafeInt(device.GetMaxOutputReportLength);

        if (name.Contains("mouse", StringComparison.OrdinalIgnoreCase))
            score += 80;
        if (name.Contains("dongle", StringComparison.OrdinalIgnoreCase) || name.Contains("receiver", StringComparison.OrdinalIgnoreCase))
            score += 60;
        if (name.Contains("ATK", StringComparison.OrdinalIgnoreCase) || name.Contains("VXE", StringComparison.OrdinalIgnoreCase) || name.Contains("DRAGONFLY", StringComparison.OrdinalIgnoreCase))
            score += 40;
        if (path.Contains("&mi_01", StringComparison.OrdinalIgnoreCase))
            score += 30;
        if (IsOfficialCompxInterface(device))
            score += 1000;
        if (inputLength >= 33)
            score += 20;
        if (outputLength >= 33)
            score += 20;

        return score;
    }

    private static int? GetIntProperty(HidDevice device, string propertyName)
    {
        try
        {
            var value = device.GetType().GetProperty(propertyName)?.GetValue(device);
            return value switch
            {
                int intValue => intValue,
                ushort ushortValue => ushortValue,
                short shortValue => shortValue,
                byte byteValue => byteValue,
                _ => null
            };
        }
        catch
        {
            return null;
        }
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

    private readonly record struct WriteRequest(string Name, byte[] Bytes);
}
