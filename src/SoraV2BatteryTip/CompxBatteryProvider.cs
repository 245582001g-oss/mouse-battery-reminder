using HidSharp;
using System.Text;

namespace SoraV2BatteryTip;

internal sealed class CompxBatteryProvider : IBatteryProvider, IMultipleBatteryProvider
{
    private const int VendorId = 0x373B;
    private const byte ReportId = 0x08;
    private const byte CommandGetBatteryLevel = 0x04;
    private const int PayloadLength = 16;
    private const int ReportLength = PayloadLength + 1;
    private const int IoTimeoutMs = 700;
    private static readonly bool DiagnosticLoggingEnabled = false;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "SoraV2BatteryTip",
        "compx-hid-last.log");

    public string Name => "ATK/COMPX HID";
    public int Priority => 250;

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
            if (reading != null)
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

                var inputLength = Math.Max(ReportLength, SafeInt(device.GetMaxInputReportLength));
                foreach (var request in BuildRequests(device))
                {
                    try
                    {
                        WriteLog(device, $"write {request.Name} length={request.Bytes.Length} inputLength={inputLength} request={ToHex(request.Bytes, request.Bytes.Length)}");
                        stream.Write(request.Bytes, 0, request.Bytes.Length);

                        var deadline = DateTime.UtcNow.AddMilliseconds(IoTimeoutMs);
                        while (DateTime.UtcNow < deadline)
                        {
                            var response = new byte[inputLength];
                            var read = stream.Read(response, 0, response.Length);
                            if (read <= 0)
                                continue;

                            WriteLog(device, $"read bytes={read} response={ToHex(response, read)}");
                            var parsed = TryParseResponse(response, read);
                        if (parsed != null)
                            return EnrichReading(parsed, device);
                        }

                        WriteLog(device, $"read timeout without parsable response after {request.Name}");
                    }
                    catch (Exception ex)
                    {
                        WriteLog(device, $"{request.Name} {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog(device, $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        return null;
    }

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
            DeviceName = Safe(() => device.GetProductName()),
            DeviceId = Safe(() => device.DevicePath),
            VendorId = $"0x{device.VendorID:X4}",
            ProductId = $"0x{device.ProductID:X4}",
            Source = reading.Source
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

    private static IEnumerable<HidDevice> EnumerateCandidateDevices()
    {
        var devices = DeviceList.Local.GetHidDevices()
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

    private static void WriteLog(HidDevice device, string message)
    {
        if (!DiagnosticLoggingEnabled)
            return;

        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" | ")
                .Append($"vid=0x{device.VendorID:X4} pid=0x{device.ProductID:X4}")
                .Append(" | ")
                .Append($"usagePage={GetIntProperty(device, "UsagePage")?.ToString("X") ?? "-"} usage={GetIntProperty(device, "Usage")?.ToString("X") ?? "-"}")
                .Append(" | ")
                .Append(Safe(() => device.GetProductName()))
                .Append(" | ")
                .Append(message)
                .AppendLine()
                .ToString();

            File.AppendAllText(LogPath, line, Encoding.UTF8);
        }
        catch { }
    }

    private static string ToHex(byte[] bytes, int length)
    {
        return Convert.ToHexString(bytes.AsSpan(0, Math.Clamp(length, 0, bytes.Length)).ToArray());
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
