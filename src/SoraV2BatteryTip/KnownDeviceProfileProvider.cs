using System.Text.Json;
using HidSharp;

namespace SoraV2BatteryTip;

internal sealed class KnownDeviceProfileProvider : IBatteryProvider, IMultipleBatteryProvider
{
    private readonly AppPaths _paths;

    public KnownDeviceProfileProvider(AppPaths paths) => _paths = paths;

    public string Name => "Known device profile";
    public int Priority => 100;

    public bool IsAvailable() => LoadProfiles().Any(profile => EnumerateMatchingDevices(profile).Any());

    public Task<BatteryReading?> ReadAsync(CancellationToken token) => Task.Run(() => ReadAllOnce(token).FirstOrDefault(), token);

    public Task<IReadOnlyList<BatteryReading>> ReadAllAsync(CancellationToken token)
    {
        return Task.Run<IReadOnlyList<BatteryReading>>(() => ReadAllOnce(token), token);
    }

    public void ReloadProfiles() => _paths.Ensure();

    public BatteryReading? TryReadProfileFile(string file)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(file), JsonOptions);
            if (profile == null || !profile.Enabled || !ValidateProfile(profile, out _) || IsHandledByBuiltInProvider(profile))
                return null;

            foreach (var device in EnumerateMatchingDevices(profile))
            {
                var reading = ReadProfileDevice(profile, device);
                if (reading != null)
                    return reading;
            }
        }
        catch { }

        return null;
    }

    public IReadOnlyList<ProfileValidationStatus> GetProfileStatus()
    {
        try
        {
            _paths.Ensure();
            if (!Directory.Exists(_paths.ProfilesDirectory))
                return Array.Empty<ProfileValidationStatus>();

            var statuses = new List<ProfileValidationStatus>();
            foreach (var file in Directory.GetFiles(_paths.ProfilesDirectory, "*.json", SearchOption.TopDirectoryOnly))
                statuses.Add(ReadProfileStatus(file));
            return statuses;
        }
        catch (Exception ex)
        {
            return new[]
            {
                new ProfileValidationStatus
                {
                    FileName = _paths.ProfilesDirectory,
                    IsValid = false,
                    Error = ex.GetType().Name
                }
            };
        }
    }

    private IReadOnlyList<BatteryReading> ReadAllOnce(CancellationToken token)
    {
        var readings = new List<BatteryReading>();
        foreach (var profile in LoadProfiles().OrderByDescending(profile => profile.Priority))
        {
            token.ThrowIfCancellationRequested();
            foreach (var device in EnumerateMatchingDevices(profile))
            {
                token.ThrowIfCancellationRequested();
                var reading = ReadProfileDevice(profile, device);
                if (reading != null)
                    if (!readings.Any(existing => string.Equals(existing.DeviceId, reading.DeviceId, StringComparison.OrdinalIgnoreCase)))
                        readings.Add(reading);
            }
        }

        return readings;
    }

    private BatteryReading? ReadProfileDevice(DeviceProfile profile, HidDevice device)
    {
        if (!profile.ReportType.Equals("Feature", StringComparison.OrdinalIgnoreCase))
            return null;

        if (profile.BatteryOffset == null)
            return null;

        if (!device.TryOpen(out var stream) || stream == null)
            return null;

        using (stream)
        {
            var reportId = ParseByte(profile.ReportId) ?? (byte)0;
            var requestBytes = ParseBytes(profile.RequestBytes);
            var length = Math.Max(Math.Max(2, profile.MinFeatureLength), device.GetMaxFeatureReportLength());
            var request = new byte[length];
            request[0] = reportId;
            for (var i = 0; i < requestBytes.Length && i < request.Length; i++)
                request[i] = requestBytes[i];

            if (profile.SendRequest)
            {
                stream.SetFeature(request, 0, request.Length);
                Thread.Sleep(Math.Clamp(profile.DelayMs, 0, 500));
            }

            var response = new byte[length];
            response[0] = ParseByte(profile.ResponseReportId) ?? request[0];
            stream.GetFeature(response, 0, response.Length);

            return ParseReading(profile, response, device);
        }
    }

    private BatteryReading? ParseReading(DeviceProfile profile, byte[] response, HidDevice device)
    {
        foreach (var start in profile.PayloadStarts.Length == 0 ? new[] { 0 } : profile.PayloadStarts)
        {
            var battery = ReadByte(response, start, profile.BatteryOffset);
            if (battery is null or < 1 or > 100)
                continue;

            var charging = ReadOptionalFlag(response, start, profile.ChargingOffset);
            var full = ReadOptionalFlag(response, start, profile.FullOffset);
            var online = ReadOptionalFlag(response, start, profile.OnlineOffset);
            if (charging == InvalidFlag || full == InvalidFlag || online == InvalidFlag)
                continue;

            return new BatteryReading
            {
                BatteryPercentage = battery.Value,
                IsCharging = charging == true || full == true,
                IsFullyCharged = full == true,
                IsOnline = online != false,
                IsCableConnected = IsCableConnected(profile, device) || charging == true || full == true,
                DeviceName = SafeProductName(device),
                DeviceId = SafeDeviceKey(device),
                VendorId = $"0x{device.VendorID:X4}",
                ProductId = $"0x{device.ProductID:X4}",
                Source = string.IsNullOrWhiteSpace(profile.Name) ? Name : profile.Name
            };
        }

        return null;
    }

    private bool IsCableConnected(DeviceProfile profile, HidDevice currentDevice)
    {
        var vendorId = ParseInt(profile.VendorId);
        if (vendorId == null)
            return false;

        if (ProductIdMatches(profile.CableProductIds, currentDevice.ProductID))
            return true;

        if (profile.AllowSameVendorFallback
            && ProductIdMatches(profile.ProductIds, currentDevice.ProductID)
            && HasMatchingDifferentProductDevice(profile, vendorId.Value, currentDevice.ProductID))
            return true;

        if (profile.AllowSameVendorFallback && !ProductIdMatches(profile.ProductIds, currentDevice.ProductID))
            return true;

        foreach (var productIdText in profile.CableProductIds)
        {
            var productId = ParseInt(productIdText);
            if (productId != null && DeviceList.Local.GetHidDevices(vendorId.Value, productId.Value).Any())
                return true;
        }

        return false;
    }

    private bool HasMatchingDifferentProductDevice(DeviceProfile profile, int vendorId, int currentProductId)
    {
        if (profile.ProductNameContains.Length == 0)
            return false;

        foreach (var device in DeviceList.Local.GetHidDevices().Where(device => device.VendorID == vendorId && device.ProductID != currentProductId))
        {
            if (device.GetMaxFeatureReportLength() <= 1)
                continue;
            if (ProductNameMatches(device, profile))
                return true;
        }

        return false;
    }

    private IEnumerable<HidDevice> EnumerateMatchingDevices(DeviceProfile profile)
    {
        var vendorId = ParseInt(profile.VendorId);
        if (vendorId == null)
            yield break;

        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (profile.AllowSameVendorFallback && profile.ProductNameContains.Length > 0)
        {
            foreach (var device in DeviceList.Local.GetHidDevices().Where(device => device.VendorID == vendorId.Value))
            {
                if (ProductIdMatches(profile.ProductIds, device.ProductID))
                    continue;
                if (!yielded.Add(SafeDeviceKey(device)))
                    continue;
                if (device.GetMaxFeatureReportLength() <= 1)
                    continue;
                if (!ProductNameMatches(device, profile))
                    continue;
                yield return device;
            }
        }

        foreach (var productIdText in profile.ProductIds)
        {
            var productId = ParseInt(productIdText);
            if (productId == null)
                continue;

            foreach (var device in DeviceList.Local.GetHidDevices(vendorId.Value, productId.Value))
            {
                if (!yielded.Add(SafeDeviceKey(device)))
                    continue;
                if (device.GetMaxFeatureReportLength() <= 1)
                    continue;
                if (!ProductNameMatches(device, profile))
                    continue;
                yield return device;
            }
        }
    }

    private bool ProductNameMatches(HidDevice device, DeviceProfile profile)
    {
        if (profile.ProductNameContains.Length == 0)
            return true;

        var name = SafeProductName(device);
        return profile.ProductNameContains.Any(part =>
            name.Contains(part, StringComparison.OrdinalIgnoreCase)
            || HasSharedSpecificNameToken(part, name));
    }

    private IReadOnlyList<DeviceProfile> LoadProfiles()
    {
        try
        {
            _paths.Ensure();
            if (!Directory.Exists(_paths.ProfilesDirectory))
                return Array.Empty<DeviceProfile>();

            var profiles = new List<DeviceProfile>();
            foreach (var file in Directory.GetFiles(_paths.ProfilesDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var profile = JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(file), JsonOptions);
                    if (profile != null && profile.Enabled && ValidateProfile(profile, out _) && !IsHandledByBuiltInProvider(profile))
                        profiles.Add(profile);
                }
                catch { }
            }
            return profiles;
        }
        catch
        {
            return Array.Empty<DeviceProfile>();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly bool? InvalidFlag = null;

    private static ProfileValidationStatus ReadProfileStatus(string file)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(file), JsonOptions);
            if (profile == null)
            {
                return new ProfileValidationStatus
                {
                    FileName = Path.GetFileName(file),
                    IsValid = false,
                    Error = "empty_or_invalid_json"
                };
            }

            var valid = ValidateProfile(profile, out var error);
            if (valid && IsHandledByBuiltInProvider(profile))
            {
                valid = false;
                error = "handled_by_builtin_provider";
            }

            return new ProfileValidationStatus
            {
                FileName = Path.GetFileName(file),
                Name = profile.Name,
                IsEnabled = profile.Enabled,
                IsValid = valid,
                Error = error
            };
        }
        catch (Exception ex)
        {
            return new ProfileValidationStatus
            {
                FileName = Path.GetFileName(file),
                IsValid = false,
                Error = ex.GetType().Name
            };
        }
    }

    private static bool ValidateProfile(DeviceProfile profile, out string error)
    {
        if (string.IsNullOrWhiteSpace(profile.VendorId) || ParseInt(profile.VendorId) == null)
        {
            error = "invalid_vendor_id";
            return false;
        }

        if (profile.ProductIds.Length == 0 || profile.ProductIds.Any(productId => ParseInt(productId) == null))
        {
            error = "invalid_product_ids";
            return false;
        }

        if (ParseByte(profile.ReportId) == null)
        {
            error = "invalid_report_id";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(profile.ResponseReportId) && ParseByte(profile.ResponseReportId) == null)
        {
            error = "invalid_response_report_id";
            return false;
        }

        if (profile.RequestBytes.Any(value => ParseByte(value) == null))
        {
            error = "invalid_request_bytes";
            return false;
        }

        if (!profile.SendRequest && profile.RequestBytes.Length > 0)
        {
            error = "request_bytes_not_allowed_when_send_request_false";
            return false;
        }

        if (!profile.ReportType.Equals("Feature", StringComparison.OrdinalIgnoreCase))
        {
            error = "unsupported_report_type";
            return false;
        }

        if (profile.BatteryOffset == null)
        {
            error = "missing_battery_offset";
            return false;
        }

        if (profile.BatteryOffset < 0)
        {
            error = "invalid_battery_offset";
            return false;
        }

        if (profile.MinFeatureLength > 0 && profile.BatteryOffset >= profile.MinFeatureLength)
        {
            error = "battery_offset_out_of_range";
            return false;
        }

        if (profile.PayloadStarts.Any(value => value < 0))
        {
            error = "invalid_payload_start";
            return false;
        }

        error = "";
        return true;
    }

    private static bool IsHandledByBuiltInProvider(DeviceProfile profile)
    {
        return ParseInt(profile.VendorId) == 0x373B
            || NinjutsoSoraOfficialProvider.HandlesProfile(profile.VendorId, profile.ProductIds);
    }

    private static int? ReadByte(byte[] bytes, int start, int? offset)
    {
        if (offset == null)
            return null;
        var index = start + offset.Value;
        return index >= 0 && index < bytes.Length ? bytes[index] : null;
    }

    private static bool? ReadOptionalFlag(byte[] bytes, int start, int? offset)
    {
        if (offset == null)
            return false;

        var value = ReadByte(bytes, start, offset);
        if (value is null or < 0 or > 1)
            return InvalidFlag;

        return value == 1;
    }

    private static byte[] ParseBytes(string[] values)
    {
        var bytes = new List<byte>();
        foreach (var value in values)
        {
            var parsed = ParseByte(value);
            if (parsed != null)
                bytes.Add(parsed.Value);
        }
        return bytes.ToArray();
    }

    private static byte? ParseByte(string? value)
    {
        var parsed = ParseInt(value);
        return parsed is >= 0 and <= 255 ? (byte)parsed.Value : null;
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

    private static string SafeProductName(HidDevice device)
    {
        try { return device.GetProductName() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeDeviceKey(HidDevice device)
    {
        try { return device.DevicePath; }
        catch { return $"{device.VendorID:X4}:{device.ProductID:X4}:{device.GetHashCode()}"; }
    }

    private static bool ProductIdMatches(string[] productIds, int currentProductId)
    {
        return productIds.Any(productId => ParseInt(productId) == currentProductId);
    }

    private static bool HasSharedSpecificNameToken(string learnedName, string currentName)
    {
        var learned = SpecificNameTokens(learnedName);
        if (learned.Count == 0)
            return false;

        var current = SpecificNameTokens(currentName);
        return current.Count > 0 && learned.Overlaps(current);
    }

    private static HashSet<string> SpecificNameTokens(string value)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wireless", "mouse", "gaming", "receiver", "dongle", "nano", "usb", "2.4g", "2g4", "hid", "device"
        };
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in value.Split(new[] { ' ', '-', '_', '.', '/', '\\', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length < 3 || ignored.Contains(token))
                continue;
            tokens.Add(token);
        }
        return tokens;
    }

    private sealed class DeviceProfile
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; } = 100;
        public string VendorId { get; set; } = "";
        public string[] ProductIds { get; set; } = Array.Empty<string>();
        public string[] CableProductIds { get; set; } = Array.Empty<string>();
        public string[] ProductNameContains { get; set; } = Array.Empty<string>();
        public bool AllowSameVendorFallback { get; set; }
        public string ReportType { get; set; } = "Feature";
        public string ReportId { get; set; } = "0x00";
        public string? ResponseReportId { get; set; }
        public string[] RequestBytes { get; set; } = Array.Empty<string>();
        public bool SendRequest { get; set; } = true;
        public int MinFeatureLength { get; set; }
        public int DelayMs { get; set; } = 80;
        public int[] PayloadStarts { get; set; } = Array.Empty<int>();
        public int? BatteryOffset { get; set; }
        public int? ChargingOffset { get; set; }
        public int? FullOffset { get; set; }
        public int? OnlineOffset { get; set; }
        public string Notes { get; set; } = "";
    }
}
